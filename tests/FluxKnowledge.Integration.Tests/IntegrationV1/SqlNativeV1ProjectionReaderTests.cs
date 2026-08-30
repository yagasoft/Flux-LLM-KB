using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Code;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.IntegrationV1.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.IntegrationV1;

public sealed class SqlNativeV1ProjectionReaderTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Code_symbols_are_secret_filtered_and_return_an_opaque_keyset_continuation_only_for_another_row()
    {
        var branchId = await SeedCodeBranchAsync(
            ("Public.Type", "public void First()"),
            ("Secret.Type", "public string Password = secret-content-sentinel"),
            ("Public.Type.Last", "public void Last()"));
        var codec = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
        var reader = new SqlNativeV1ProjectionReader(
            SqlTestData.CreateFactory(_fixture),
            new LocalPrivateContentDisclosure(),
            new UnusedRetainedDetailReader(),
            codec);
        var service = new NativeCodeQueryService(reader, codec);

        var first = JsonSerializer.SerializeToElement(await service.ExecuteAsync(
            new NativeCodeQuery("symbols", null, branchId, 1, null),
            CancellationToken.None));

        var firstItem = Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.Equal("Public.Type", firstItem.GetProperty("qualifiedName").GetProperty("value").GetString());
        var firstCursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(firstCursor));
        Assert.DoesNotContain(branchId.ToString("D"), firstCursor!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Public.Type", firstCursor!, StringComparison.Ordinal);

        var second = JsonSerializer.SerializeToElement(await service.ExecuteAsync(
            new NativeCodeQuery("symbols", null, branchId, 1, firstCursor),
            CancellationToken.None));
        var withheld = Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.True(withheld.GetProperty("renderedSignature").GetProperty("withheld").GetBoolean());
        Assert.Equal("secret-content-withheld", withheld.GetProperty("renderedSignature").GetProperty("reasonCode").GetString());
        var secondCursor = second.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(secondCursor));

        var final = JsonSerializer.SerializeToElement(await service.ExecuteAsync(
            new NativeCodeQuery("symbols", null, branchId, 1, secondCursor),
            CancellationToken.None));
        Assert.Single(final.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, final.GetProperty("nextCursor").ValueKind);
    }

    [NativeSqlServerFact]
    public async Task Code_matches_apply_the_disclosure_policy_to_public_and_secret_derived_fields()
    {
        var branchId = await SeedCodeBranchAsync(
            ("Match.Public", "public void MatchPublic()"),
            ("Match.Secret", "public string Password = secret-content-sentinel"));
        var codec = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
        var result = JsonSerializer.SerializeToElement(await new NativeCodeQueryService(CreateReader(codec), codec).ExecuteAsync(
            new NativeCodeQuery("matches", "Match", branchId, 10, null),
            CancellationToken.None));

        var items = result.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("Match.Public", items[0].GetProperty("qualifiedName").GetProperty("value").GetString());
        Assert.True(items[1].GetProperty("renderedSignature").GetProperty("withheld").GetBoolean());
        Assert.Equal("secret-content-withheld", items[1].GetProperty("renderedSignature").GetProperty("reasonCode").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("nextCursor").ValueKind);
    }

    [NativeSqlServerFact]
    public async Task Code_matches_at_the_exact_query_limit_can_follow_a_bound_cursor()
    {
        var query = new string('q', NativeV1ContractLimits.MaximumCodeQueryCharacters);
        var branchId = await SeedCodeBranchAsync(
            ($"{query}.First", "public void First()"),
            ($"{query}.Second", "public void Second()"));
        var codec = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
        var service = new NativeCodeQueryService(CreateReader(codec), codec);

        var first = JsonSerializer.SerializeToElement(await service.ExecuteAsync(
            new NativeCodeQuery("matches", query, branchId, 1, null),
            CancellationToken.None));
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        var second = JsonSerializer.SerializeToElement(await service.ExecuteAsync(
            new NativeCodeQuery("matches", query, branchId, 1, cursor),
            CancellationToken.None));

        Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [NativeSqlServerTheory]
    [InlineData("overview")]
    [InlineData("sources")]
    [InlineData("jobs")]
    [InlineData("workers")]
    [InlineData("processors")]
    [InlineData("recovery")]
    public async Task Operations_status_views_are_bounded_and_do_not_advertise_excluded_capabilities(string view)
    {
        var codec = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
        var result = JsonSerializer.SerializeToElement(await new NativeOperationsStatusService(CreateReader(codec)).ExecuteAsync(
            new NativeOperationsStatus(view, null, null, 1),
            CancellationToken.None));

        if (result.TryGetProperty("items", out var items)) Assert.InRange(items.GetArrayLength(), 0, 1);
        var json = result.GetRawText();
        Assert.DoesNotContain("backfill", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shell", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plugin", json, StringComparison.OrdinalIgnoreCase);
    }

    [NativeSqlServerFact]
    public async Task Corpus_branches_use_query_bound_keyset_pages_and_stop_after_the_last_filtered_row()
    {
        var (rootId, orderedBranches) = await SeedCorpusBranchesAsync();
        var codec = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
        var service = new NativeCorpusQueryService(CreateReader(codec), codec);

        string? cursor = null;
        for (var index = 0; index < orderedBranches.Length; index++)
        {
            var response = JsonSerializer.SerializeToElement(await service.ExecuteAsync(
                new NativeCorpusQuery("branches", rootId, null, null, 1, cursor),
                CancellationToken.None));
            var item = Assert.Single(response.GetProperty("items").EnumerateArray());
            Assert.Equal(orderedBranches[index], item.GetProperty("Id").GetGuid());
            cursor = response.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null
                : response.GetProperty("nextCursor").GetString();
            Assert.Equal(index < orderedBranches.Length - 1, cursor is not null);
        }
    }

    [NativeSqlServerTheory]
    [InlineData("roots")]
    [InlineData("assets")]
    [InlineData("branches")]
    [InlineData("processors")]
    [InlineData("jobs")]
    public async Task Corpus_list_views_are_bounded_and_expose_only_opaque_continuation_state(string view)
    {
        var codec = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
        var result = JsonSerializer.SerializeToElement(await new NativeCorpusQueryService(CreateReader(codec), codec).ExecuteAsync(
            new NativeCorpusQuery(view, null, null, null, 1, null),
            CancellationToken.None));

        Assert.InRange(result.GetProperty("items").GetArrayLength(), 0, 1);
        Assert.True(result.TryGetProperty("nextCursor", out var cursor));
        if (cursor.ValueKind == JsonValueKind.String)
        {
            Assert.DoesNotContain("source-original-must-not-open", cursor.GetString()!, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("CanonicalPath", result.GetRawText(), StringComparison.Ordinal);
    }

    [NativeSqlServerFact]
    public async Task Pre_existing_unsafe_corpus_display_name_is_withheld_from_root_and_status_reads()
    {
        const string unsafeDisplayName = "eyJhY2Nlc3NUb2tlbiI6InZhbHVlIn0=";
        var rootId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = $"C:\\native-v1-unsafe-metadata\\{rootId:N}",
                DisplayName = unsafeDisplayName,
                State = (int)SourceRootState.Paused,
                Recursive = false,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                FollowLinks = false,
                MaximumFileBytes = 1024,
                AllowedClassificationsJson = "[]",
                CrawlMode = 0,
                ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await context.SaveChangesAsync();
        }

        var codec = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
        var reader = CreateReader(codec);
        var roots = JsonSerializer.SerializeToElement(await new NativeCorpusQueryService(reader, codec).ExecuteAsync(
            new NativeCorpusQuery("roots", rootId, null, null, 10, null),
            CancellationToken.None));
        var status = JsonSerializer.SerializeToElement(await new NativeOperationsStatusService(reader).ExecuteAsync(
            new NativeOperationsStatus("sources", rootId, null, 100),
            CancellationToken.None));

        var rootDisplayName = Assert.Single(roots.GetProperty("items").EnumerateArray())
            .GetProperty("displayName");
        Assert.True(rootDisplayName.GetProperty("withheld").GetBoolean());
        Assert.Equal("secret-content-withheld", rootDisplayName.GetProperty("reasonCode").GetString());
        var statusItem = status.GetProperty("items").EnumerateArray()
            .Single(value => value.GetProperty("Id").GetGuid() == rootId);
        Assert.True(statusItem.GetProperty("displayName").GetProperty("withheld").GetBoolean());
        Assert.DoesNotContain(unsafeDisplayName, roots.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(unsafeDisplayName, status.GetRawText(), StringComparison.Ordinal);
    }

    [NativeSqlServerFact]
    public async Task Audit_events_are_secret_filtered_and_keyset_paged_by_the_immutable_event_identity()
    {
        var rootId = Guid.NewGuid();
        var times = new[]
        {
            DateTimeOffset.Parse("2099-01-03T00:00:00+00:00"),
            DateTimeOffset.Parse("2099-01-02T00:00:00+00:00"),
            DateTimeOffset.Parse("2099-01-01T00:00:00+00:00")
        };
        await using (var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = $"C:\\native-v1-audit\\{rootId:N}",
                DisplayName = "Native v1 audit",
                State = 0,
                Recursive = true,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                FollowLinks = false,
                MaximumFileBytes = 1024,
                AllowedClassificationsJson = "[]",
                CrawlMode = 0,
                ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1,
                CreatedAtUtc = times[^1],
                UpdatedAtUtc = times[0]
            });
            foreach (var (time, index) in times.Select((value, index) => (value, index)))
            {
                context.AuditEvents.Add(new AuditEventEntity
                {
                    SourceRootId = rootId,
                    EventFamily = "code",
                    Severity = "information",
                    EventType = $"native-v1-test.{index}",
                    Actor = "test",
                    DetailsJson = index == 1 ? "secret-content-sentinel" : $"safe-{index}",
                    OccurredAtUtc = time
                });
            }
            await context.SaveChangesAsync();
        }

        var codec = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
        var service = new NativeAuditQueryService(CreateReader(codec), codec);
        var first = JsonSerializer.SerializeToElement(await service.ExecuteAsync(
            new NativeAuditQuery("events", rootId, null, 1, null),
            CancellationToken.None));
        Assert.Equal("safe-0", Assert.Single(first.GetProperty("items").EnumerateArray()).GetProperty("details").GetProperty("value").GetString());
        var firstCursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(firstCursor));

        var second = JsonSerializer.SerializeToElement(await service.ExecuteAsync(
            new NativeAuditQuery("events", rootId, null, 1, firstCursor),
            CancellationToken.None));
        Assert.True(Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("details").GetProperty("withheld").GetBoolean());
        var secondCursor = second.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(secondCursor));

        var final = JsonSerializer.SerializeToElement(await service.ExecuteAsync(
            new NativeAuditQuery("events", rootId, null, 1, secondCursor),
            CancellationToken.None));
        Assert.Equal("safe-2", Assert.Single(final.GetProperty("items").EnumerateArray()).GetProperty("details").GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, final.GetProperty("nextCursor").ValueKind);
        await using var verify = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        var persisted = await verify.AuditEvents.AsNoTracking()
            .Where(value => value.SourceRootId == rootId)
            .OrderByDescending(value => value.OccurredAtUtc)
            .Select(value => value.DetailsJson)
            .ToArrayAsync();
        Assert.Equal(new[] { "safe-0", "secret-content-sentinel", "safe-2" }, persisted);
    }

    [NativeSqlServerTheory]
    [InlineData(false, "retained-artifact-missing")]
    [InlineData(true, "retained-artifact-checksum-invalid")]
    public async Task Corpus_detail_reports_retained_artifact_integrity_reasons_without_reopening_a_source_original(
        bool writeCorruptArtifact,
        string expectedReason)
    {
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"flux-native-v1-retained-{Guid.NewGuid():N}");
        var originalRoot = Path.Combine(Path.GetTempPath(), $"flux-native-v1-original-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactRoot);
        Directory.CreateDirectory(originalRoot);
        try
        {
            var seeded = await SeedRetainedDetailBranchAsync(artifactRoot, originalRoot, writeCorruptArtifact);
            var codec = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
            using var retained = new SqlRetainedSourceReader(SqlTestData.CreateFactory(_fixture), artifactRoot);
            var disclosure = new LocalPrivateContentDisclosure();
            var reader = new SqlNativeV1ProjectionReader(
                SqlTestData.CreateFactory(_fixture),
                disclosure,
                new SqlLocalRetainedDetailReader(SqlTestData.CreateFactory(_fixture), retained, disclosure),
                codec);
            var result = JsonSerializer.SerializeToElement(await new NativeCorpusQueryService(reader, codec).ExecuteAsync(
                new NativeCorpusQuery("detail", null, seeded.BranchId, null, 1, null),
                CancellationToken.None));

            Assert.Equal(expectedReason, result.GetProperty("reasonCode").GetString());
            Assert.True(File.Exists(seeded.OriginalPath));
            Assert.DoesNotContain(seeded.OriginalPath, result.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
            if (Directory.Exists(originalRoot)) Directory.Delete(originalRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Corpus_detail_returns_bounded_checksum_provenance_without_a_source_original_path()
    {
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"flux-native-v1-retained-{Guid.NewGuid():N}");
        var originalRoot = Path.Combine(Path.GetTempPath(), $"flux-native-v1-original-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactRoot);
        Directory.CreateDirectory(originalRoot);
        try
        {
            var seeded = await SeedRetainedDetailBranchAsync(artifactRoot, originalRoot, writeCorruptArtifact: null);
            var codec = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
            using var retained = new SqlRetainedSourceReader(SqlTestData.CreateFactory(_fixture), artifactRoot);
            var disclosure = new LocalPrivateContentDisclosure();
            var reader = new SqlNativeV1ProjectionReader(
                SqlTestData.CreateFactory(_fixture),
                disclosure,
                new SqlLocalRetainedDetailReader(SqlTestData.CreateFactory(_fixture), retained, disclosure),
                codec);
            var result = JsonSerializer.SerializeToElement(await new NativeCorpusQueryService(reader, codec).ExecuteAsync(
                new NativeCorpusQuery("detail", null, seeded.BranchId, null, 1, null),
                CancellationToken.None));

            Assert.Equal(seeded.Hash, result.GetProperty("provenance").GetProperty("ArtifactHash").GetString());
            Assert.Equal(seeded.Bytes.Length, result.GetProperty("provenance").GetProperty("ArtifactByteLength").GetInt64());
            Assert.DoesNotContain(seeded.OriginalPath, result.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
            if (Directory.Exists(originalRoot)) Directory.Delete(originalRoot, recursive: true);
        }
    }

    private SqlNativeV1ProjectionReader CreateReader(INativeV1CursorCodec codec) => new(
        SqlTestData.CreateFactory(_fixture),
        new LocalPrivateContentDisclosure(),
        new UnusedRetainedDetailReader(),
        codec);

    private async Task<(Guid RootId, Guid[] OrderedBranches)> SeedCorpusBranchesAsync()
    {
        var rootId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2099-02-01T00:00:00+00:00");
        var branches = new List<Guid>();
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId,
            CanonicalPath = $"C:\\native-v1-corpus\\{rootId:N}",
            DisplayName = "Native v1 corpus",
            State = 0,
            Recursive = true,
            IncludePatternsJson = "[]",
            ExcludePatternsJson = "[]",
            FollowLinks = false,
            MaximumFileBytes = 1024,
            AllowedClassificationsJson = "[\"text/plain\"]",
            CrawlMode = 0,
            ReconciliationCadenceSeconds = 900,
            ConfigurationRevision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        for (var index = 0; index < 3; index++)
        {
            var revisionId = Guid.NewGuid();
            var activityId = Guid.NewGuid();
            var branchId = Guid.NewGuid();
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(revisionId.ToString("D"))));
            var updatedAt = now.AddMinutes(-index);
            context.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = revisionId,
                SourceRootId = rootId,
                StableSourceIdentity = $"native-v1-corpus:{revisionId:N}",
                Revision = 1,
                ContentSha256 = hash,
                CanonicalPath = $"C:\\source-original-must-not-open\\{revisionId:N}.txt",
                Classification = "AcceptedUtf8Text",
                Extension = ".txt",
                ByteLength = 1,
                DiscoveredAtUtc = updatedAt,
                DiscoveryEvidenceJson = "{}"
            });
            context.SourceActivities.Add(new SourceActivityEntity
            {
                Id = activityId,
                SourceRevisionId = revisionId,
                ActivityKind = (int)SourceActivityKind.DocumentParsing,
                ExecutionClass = (int)ExecutionClass.InProcess,
                ProcessorVersion = "native-v1-test",
                InputFingerprint = hash,
                State = (int)SourceActivityState.Pending,
                CreatedAtUtc = updatedAt,
                UpdatedAtUtc = updatedAt
            });
            context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
            {
                Id = branchId,
                SourceActivityId = activityId,
                SourceRevisionId = revisionId,
                InputSha256 = hash,
                ProcessorVersion = "native-v1-test",
                ProcessorFingerprint = new string('f', 64),
                State = (int)RetainedProcessorBranchState.Pending,
                CreatedAtUtc = updatedAt,
                UpdatedAtUtc = updatedAt
            });
            branches.Add(branchId);
        }
        await context.SaveChangesAsync();
        return (rootId, branches.ToArray());
    }

    private async Task<RetainedSeed> SeedRetainedDetailBranchAsync(
        string artifactRoot,
        string originalRoot,
        bool? writeCorruptArtifact)
    {
        var bytes = Encoding.UTF8.GetBytes("retained native v1 detail");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var originalPath = Path.Combine(originalRoot, $"{revisionId:N}.txt");
        await File.WriteAllBytesAsync(originalPath, bytes);
        var artifactDirectory = Path.Combine(artifactRoot, "sha256", hash[..2]);
        Directory.CreateDirectory(artifactDirectory);
        if (writeCorruptArtifact is not false)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(artifactDirectory, $"{hash}.bin"),
                writeCorruptArtifact is true ? bytes[..^1].Concat("X"u8.ToArray()).ToArray() : bytes);
        }
        var now = DateTimeOffset.UtcNow;
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId,
            CanonicalPath = originalRoot,
            DisplayName = "Native v1 retained detail",
            State = 0,
            Recursive = true,
            IncludePatternsJson = "[]",
            ExcludePatternsJson = "[]",
            FollowLinks = false,
            MaximumFileBytes = 1024,
            AllowedClassificationsJson = "[\"text/plain\"]",
            CrawlMode = 0,
            ReconciliationCadenceSeconds = 900,
            ConfigurationRevision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = revisionId,
            SourceRootId = rootId,
            StableSourceIdentity = $"native-v1-retained:{revisionId:N}",
            Revision = 1,
            ContentSha256 = hash,
            CanonicalPath = originalPath,
            Classification = "AcceptedUtf8Text",
            Extension = ".txt",
            ByteLength = bytes.Length,
            DiscoveredAtUtc = now,
            DiscoveryEvidenceJson = "{}"
        });
        context.SourceArtifacts.Add(new SourceArtifactEntity
        {
            Id = Guid.NewGuid(),
            SourceRevisionId = revisionId,
            ContentSha256 = hash,
            StoreRelativePath = $"sha256\\{hash[..2]}\\{hash}.bin",
            ByteLength = bytes.Length,
            ChecksumVerifiedAtUtc = now,
            ReferenceCount = 1
        });
        context.SourceActivities.Add(new SourceActivityEntity
        {
            Id = activityId,
            SourceRevisionId = revisionId,
            ActivityKind = (int)SourceActivityKind.DocumentParsing,
            ExecutionClass = (int)ExecutionClass.InProcess,
            ProcessorVersion = "native-v1-test",
            InputFingerprint = hash,
            State = (int)SourceActivityState.Completed,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
        {
            Id = branchId,
            SourceActivityId = activityId,
            SourceRevisionId = revisionId,
            InputSha256 = hash,
            ProcessorVersion = "native-v1-test",
            ProcessorFingerprint = new string('9', 64),
            State = (int)RetainedProcessorBranchState.Completed,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await context.SaveChangesAsync();
        return new RetainedSeed(branchId, hash, bytes, originalPath);
    }

    private sealed record RetainedSeed(Guid BranchId, string Hash, byte[] Bytes, string OriginalPath);

    private async Task<Guid> SeedCodeBranchAsync(params (string QualifiedName, string Signature)[] symbols)
    {
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(branchId.ToString("D"))));
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId,
            CanonicalPath = $"C:\\native-v1-projection\\{rootId:N}",
            DisplayName = "Native v1 projection",
            State = 0,
            Recursive = true,
            IncludePatternsJson = "[]",
            ExcludePatternsJson = "[]",
            FollowLinks = false,
            MaximumFileBytes = 1024,
            AllowedClassificationsJson = "[\"text/plain\"]",
            CrawlMode = 0,
            ReconciliationCadenceSeconds = 900,
            ConfigurationRevision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = revisionId,
            SourceRootId = rootId,
            StableSourceIdentity = $"native-v1-projection:{revisionId:N}",
            Revision = 1,
            ContentSha256 = hash,
            CanonicalPath = $"C:\\source-original-must-not-open\\{revisionId:N}.cs",
            Classification = "AcceptedUtf8Text",
            Extension = ".cs",
            ByteLength = 1,
            DiscoveredAtUtc = now,
            DiscoveryEvidenceJson = "{}"
        });
        context.SourceActivities.Add(new SourceActivityEntity
        {
            Id = activityId,
            SourceRevisionId = revisionId,
            ActivityKind = (int)SourceActivityKind.CodeParsing,
            ExecutionClass = (int)ExecutionClass.InProcess,
            ProcessorVersion = "native-v1-test",
            InputFingerprint = hash,
            State = (int)SourceActivityState.Completed,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
        {
            Id = branchId,
            SourceActivityId = activityId,
            SourceRevisionId = revisionId,
            InputSha256 = hash,
            ProcessorVersion = "native-v1-test",
            ProcessorFingerprint = new string('a', 64),
            State = (int)RetainedProcessorBranchState.Completed,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        context.SourceProcessorCodeDocuments.Add(new SourceProcessorCodeDocumentEntity
        {
            SourceProcessorBranchId = branchId,
            SourceRevisionId = revisionId,
            RetainedArtifactSha256 = hash,
            DescriptorFingerprint = new string('b', 64),
            ParserFingerprint = new string('c', 64),
            HandlerImplementationId = "native-v1-test",
            DecodedCharacterCount = 1,
            LineCount = 1,
            SymbolCount = symbols.Length,
            DocumentFingerprint = new string('d', 64),
            CompletionFingerprint = new string('e', 64)
        });
        for (var ordinal = 0; ordinal < symbols.Length; ordinal++)
        {
            context.SourceProcessorCodeSymbols.Add(new SourceProcessorCodeSymbolEntity
            {
                DocumentId = branchId,
                Ordinal = ordinal,
                DeclarationKindCode = 1,
                LocalName = $"Symbol{ordinal}",
                QualifiedName = symbols[ordinal].QualifiedName,
                RenderedSignature = symbols[ordinal].Signature,
                Modifiers = "public",
                LexicalParentOrdinal = -1,
                SpanStartUtf16 = ordinal,
                SpanLengthUtf16 = 1,
                SymbolFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{branchId:D}:{ordinal}")))
            });
        }
        await context.SaveChangesAsync();
        return branchId;
    }

    private sealed class UnusedRetainedDetailReader : ILocalRetainedDetailReader
    {
        public ValueTask<LocalRetainedDetailProjection?> ReadAsync(Guid branchId, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Code projections must not open retained detail.");

        public ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid branchId, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Code projections must not open retained detail.");
    }
}
