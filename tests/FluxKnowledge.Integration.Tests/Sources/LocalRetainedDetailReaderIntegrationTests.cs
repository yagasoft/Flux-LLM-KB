using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class LocalRetainedDetailReaderIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Read_returns_only_retained_bound_branch_detail_and_withholds_secret_attempt_evidence()
    {
        var bytes = "retained local detail"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactRoot = CreateArtifactRoot(hash, bytes);
        try
        {
            var ids = await SeedAsync(hash, bytes.Length, "secret-content-sentinel", artifactRoot);
            var reader = CreateReader(artifactRoot);

            var detail = await reader.ReadAsync(ids.BranchId, CancellationToken.None);

            Assert.NotNull(detail);
            Assert.Equal(ids.BranchId, detail!.BranchId);
            Assert.Equal(ids.RevisionId, detail.SourceRevisionId.Value);
            Assert.Equal($"C:\\retained-detail\\{ids.RevisionId:N}.txt", detail.LocalPath);
            Assert.Equal(hash, detail.ArtifactHash);
            Assert.Equal(hash, detail.InputHash);
            Assert.Equal(bytes.Length, detail.ArtifactByteLength);
            var member = Assert.Single(detail.Members);
            Assert.Equal(ids.ChildRevisionId, member.ChildSourceRevisionId);
            Assert.Equal("completed", member.Disposition);
            var attempt = Assert.Single(detail.Attempts);
            Assert.Null(attempt.Diagnostic);
            Assert.True(attempt.DiagnosticWithheld);
            Assert.Equal("secret-content-withheld", attempt.DiagnosticReasonCode);

            var excerpt = await reader.ReadExcerptAsync(ids.BranchId, CancellationToken.None);
            Assert.Equal("retained local detail", excerpt.Value);
            Assert.False(excerpt.Withheld);
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Read_excerpt_never_uses_the_source_original_and_withholds_a_secret_retained_value()
    {
        var bytes = "secret-content-sentinel"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactRoot = CreateArtifactRoot(hash, bytes);
        try
        {
            var originalPath = Path.Combine(artifactRoot, "source-original.txt");
            await File.WriteAllTextAsync(originalPath, "different original");
            var ids = await SeedAsync(hash, bytes.Length, null, artifactRoot, canonicalPath: originalPath);
            File.Delete(originalPath);

            var excerpt = await CreateReader(artifactRoot).ReadExcerptAsync(ids.BranchId, CancellationToken.None);

            Assert.Null(excerpt.Value);
            Assert.True(excerpt.Withheld);
            Assert.Equal("secret-content-withheld", excerpt.ReasonCode);
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Read_rejects_a_branch_whose_artifact_binding_is_malformed()
    {
        var bytes = "retained local detail"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactRoot = CreateArtifactRoot(hash, bytes);
        try
        {
            var ids = await SeedAsync(hash, bytes.Length, null, artifactRoot, artifactHash: new string('b', 64));

            await Assert.ThrowsAsync<InvalidDataException>(() => CreateReader(artifactRoot)
                .ReadAsync(ids.BranchId, CancellationToken.None).AsTask());
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Read_rejects_same_length_retained_file_corruption_before_presenting_local_detail()
    {
        var bytes = "retained local detail"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactRoot = CreateArtifactRoot(hash, bytes);
        try
        {
            var ids = await SeedAsync(hash, bytes.Length, null, artifactRoot);
            var path = Path.Combine(artifactRoot, "sha256", hash[..2], $"{hash}.bin");
            await File.WriteAllBytesAsync(path, "retained local detaiX"u8.ToArray());

            await Assert.ThrowsAsync<InvalidDataException>(() => CreateReader(artifactRoot)
                .ReadAsync(ids.BranchId, CancellationToken.None).AsTask());
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
        }
    }

    [NativeSqlServerTheory]
    [InlineData("{\"password\":\"synthetic-password\"}")]
    [InlineData("{\"diagnostic\":{\"access_token\":\"synthetic-token\"}}")]
    [InlineData("parser failed; payload={\"oauth_client_secret\":\"synthetic-secret\"}")]
    public async Task Read_withholds_JSON_shaped_credential_attempt_evidence(string evidence)
    {
        var bytes = "retained local detail"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactRoot = CreateArtifactRoot(hash, bytes);
        try
        {
            var ids = await SeedAsync(hash, bytes.Length, evidence, artifactRoot);

            var detail = await CreateReader(artifactRoot).ReadAsync(ids.BranchId, CancellationToken.None);

            var attempt = Assert.Single(detail!.Attempts);
            Assert.Null(attempt.Diagnostic);
            Assert.True(attempt.DiagnosticWithheld);
            Assert.Equal("secret-content-withheld", attempt.DiagnosticReasonCode);
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
        }
    }

    private ILocalRetainedDetailReader CreateReader(string artifactRoot) => new SqlLocalRetainedDetailReader(
        new TestDbContextFactory(_fixture.ConnectionString),
        new SqlRetainedSourceReader(new TestDbContextFactory(_fixture.ConnectionString), artifactRoot),
        new LocalPrivateContentDisclosure());

    private async Task<(Guid BranchId, Guid RevisionId, Guid ChildRevisionId)> SeedAsync(
        string hash,
        int byteLength,
        string? evidence,
        string artifactRoot,
        string? artifactHash = null,
        string? canonicalPath = null)
    {
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var childRevisionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var relative = Path.Combine("sha256", hash[..2], $"{hash}.bin");
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId, CanonicalPath = $"C:\\retained-detail\\{rootId:N}", DisplayName = "Detail", State = 0,
            Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false,
            MaximumFileBytes = 16 * 1024 * 1024, AllowedClassificationsJson = "[]", CrawlMode = 0,
            ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.SourceRevisions.AddRange(
            Revision(revisionId, rootId, hash, byteLength, canonicalPath ?? $"C:\\retained-detail\\{revisionId:N}.txt", now),
            Revision(childRevisionId, rootId, hash, 1, $"C:\\retained-detail\\child-{childRevisionId:N}.txt", now));
        context.SourceArtifacts.Add(new SourceArtifactEntity
        {
            Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = artifactHash ?? hash,
            StoreRelativePath = relative, ByteLength = byteLength, ChecksumVerifiedAtUtc = now, ReferenceCount = 1
        });
        context.SourceActivities.Add(new SourceActivityEntity
        {
            Id = activityId, SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.MetadataExtraction,
            ExecutionClass = (int)ExecutionClass.InProcess, ProcessorVersion = "detail-v1", InputFingerprint = hash,
            State = (int)SourceActivityState.Completed, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
        {
            Id = branchId, SourceActivityId = activityId, SourceRevisionId = revisionId, InputSha256 = hash,
            ProcessorVersion = "detail-v1", ProcessorFingerprint = "detail-fingerprint", State = 2,
            LeaseGeneration = 1, AttemptCount = 1, CompletedMemberCount = 1,
            CompletionReceiptFingerprint = new string('a', 64), CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.SourceProcessorBranchMembers.Add(new SourceProcessorBranchMemberEntity
        {
            Id = Guid.NewGuid(), BranchId = branchId, MemberFingerprint = new string('c', 64),
            ChildSourceRevisionId = childRevisionId, Disposition = "completed", ByteLength = 1, CreatedAtUtc = now
        });
        context.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity
        {
            Id = Guid.NewGuid(), BranchId = branchId, LeaseGeneration = 1, StartedAtUtc = now,
            FinishedAtUtc = now, OutcomeCode = "completed", EvidenceJson = evidence
        });
        await context.SaveChangesAsync();
        return (branchId, revisionId, childRevisionId);
    }

    private static SourceRevisionEntity Revision(Guid id, Guid rootId, string hash, int byteLength, string path, DateTimeOffset now) => new()
    {
        Id = id, SourceRootId = rootId, StableSourceIdentity = $"detail:{id:N}", Revision = 1,
        ContentSha256 = hash, CanonicalPath = path, Classification = "AcceptedUtf8Text", Extension = ".txt",
        ByteLength = byteLength, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}"
    };

    private static string CreateArtifactRoot(string hash, byte[] bytes)
    {
        var root = Path.Combine(Path.GetTempPath(), $"flux-local-retained-detail-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "sha256", hash[..2], $"{hash}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return root;
    }

    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString).Options;
        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
