using System.Diagnostics;
using System.Globalization;
using System.Data.Common;
using System.Text.Json;
using System.Xml.Linq;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class SegmentedSelectionScaleTests(NativeSqlServerFixture fixture, ITestOutputHelper output) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Indexed_scope_seeding_preserves_parent_document_owner_root_and_suppression_rules()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Unrelated plain-source control.", segmented: true);
        var firstId = await environment.AddRetainedAndPumpAsync("The original physical owner.");
        var childId = await environment.AddRetainedAndPumpAsync("The retained child has a separate root in this scope fixture.");
        var ownerId = await environment.AddRetainedAndPumpAsync("The selected document publication owner.");
        await using var db = await environment.Factory.CreateDbContextAsync();
        var first = await db.PipelineRecords.AsNoTracking().SingleAsync(value => value.Id == firstId);
        var child = await db.PipelineRecords.AsNoTracking().SingleAsync(value => value.Id == childId);
        var owner = await db.PipelineRecords.AsNoTracking().SingleAsync(value => value.Id == ownerId);
        var firstRevision = await db.SourceRevisions.AsNoTracking().SingleAsync(value => value.Id == first.SourceRevisionId);
        var childRevision = await db.SourceRevisions.AsNoTracking().SingleAsync(value => value.Id == child.SourceRevisionId);
        var ownerRevision = await db.SourceRevisions.AsNoTracking().SingleAsync(value => value.Id == owner.SourceRevisionId);
        await db.SourceRevisions.Where(value => value.Id == childRevision.Id).ExecuteUpdateAsync(setters =>
            setters.SetProperty(value => value.OriginKind, 1).SetProperty(value => value.ParentSourceRevisionId, firstRevision.Id));
        await AssertScopeAsync(new(2, firstRevision.Id), first.SourceIdentityId, child.SourceIdentityId);
        await AssertScopeAsync(new(2, childRevision.Id)); // An inclusive seed cannot make a child its own publication owner.
        await AssertScopeAsync(new(3, firstRevision.SourceRootId), first.SourceIdentityId);
        await AssertScopeAsync(new(3, childRevision.SourceRootId), child.SourceIdentityId);
        await AssertScopeAsync(new(1, child.SourceIdentityId), child.SourceIdentityId);

        var branchId = Guid.NewGuid();
        var activity = await db.SourceActivities.AsNoTracking().SingleAsync(value => value.SourceRevisionId == ownerRevision.Id);
        db.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
        {
            Id = branchId, SourceActivityId = activity.Id, SourceRevisionId = ownerRevision.Id,
            InputSha256 = ownerRevision.ContentSha256, ProcessorVersion = "synthetic-scope-v1", ProcessorFingerprint = "synthetic-scope-v1",
            State = (int)RetainedProcessorBranchState.Completed, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        db.DocumentPublications.Add(new DocumentPublicationEntity
        {
            OwnerSourceRevisionId = ownerRevision.Id, DocumentInputSourceRevisionId = childRevision.Id, SourceProcessorBranchId = branchId,
            PipelineRecordId = child.Id, PipelineRecordRevision = child.Revision, ProcessorFingerprint = "synthetic-scope-v1", PublishedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        await db.SourceRevisions.Where(value => value.Id == childRevision.Id).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.OriginKind, 2));
        await AssertScopeAsync(new(2, firstRevision.Id), first.SourceIdentityId);
        await AssertScopeAsync(new(2, ownerRevision.Id), owner.SourceIdentityId, child.SourceIdentityId);
        await AssertScopeAsync(new(3, ownerRevision.SourceRootId), owner.SourceIdentityId, child.SourceIdentityId);
        await AssertScopeAsync(new(3, childRevision.SourceRootId)); // The coalesced owner root remains authoritative.
        await db.SourceRevisions.Where(value => value.Id == ownerRevision.Id).ExecuteUpdateAsync(setters =>
            setters.SetProperty(value => value.SuppressedAtUtc, DateTimeOffset.UtcNow));
        await AssertScopeAsync(new(2, ownerRevision.Id));
        await AssertScopeAsync(new(1, child.SourceIdentityId));
        await db.SourceRevisions.Where(value => value.Id == ownerRevision.Id).ExecuteUpdateAsync(setters =>
            setters.SetProperty(value => value.SuppressedAtUtc, (DateTimeOffset?)null));
        await db.SourceRootConfigurations.Where(value => value.Id == ownerRevision.SourceRootId).ExecuteUpdateAsync(setters =>
            setters.SetProperty(value => value.State, (int)SourceRootState.Deleting));
        await AssertScopeAsync(new(3, ownerRevision.SourceRootId));
        await AssertScopeAsync(new(1, child.SourceIdentityId));

        async Task AssertScopeAsync(IndexAffectedScope scope, params Guid[] expected)
        {
            var rows = await SqlSegmentedIndexStore.QueryEligible(db, JsonSerializer.Serialize(new[] { scope }), 0).ToArrayAsync();
            Assert.Equal(expected.Order(), rows.Select(value => value.SourceIdentityId).Order());
        }
    }

    [NativeSqlServerTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task A_one_source_delta_does_not_scan_ten_times_more_unrelated_rows(int selection)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            selection >= 3 ? string.Join(' ', Enumerable.Range(0, 750).Select(index => $"passage_{index}"))
                : "The selected source has one small unchanged passage.", publish: selection != 2, segmented: true);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var selectedSource = await db.PipelineRecords.Select(value => value.SourceIdentityId).SingleAsync();
        var selectedDraft = selection == 2 ? await db.IndexGenerations.Where(value => value.IndexPath == string.Empty).Select(value => value.Id).SingleAsync() : Guid.Empty;
        await environment.AddAndPumpAsync("The unrelated source supplies isolated SQL-only scale rows.");
        var templateVector = await db.Vectors.OrderByDescending(value => value.VectorId).Select(value => value.VectorId).FirstAsync();
        var scopes = JsonSerializer.Serialize(new[] { new IndexAffectedScope(1, selectedSource) });
        var candidate = new IndexPublicationCandidateEntity { AffectedScopesJson = scopes, CorpusEpoch = await db.IndexState.Select(value => value.CorpusEpoch).SingleAsync() };
        if (selection == 2)
        {
            using var scope = environment.Services.CreateScope();
            var prepared = await scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>().PrepareAsync(selectedDraft, CancellationToken.None);
            candidate = await db.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == prepared.Id);
        }
        if (selection >= 3)
        {
            // A removed member requires rebuilding its mixed segment's survivor.
            // Other segments, including the scale rows below, must not be read.
            var segmentCount = selection == 4 ? 2 : 1;
            var segments = await db.CurrentIndexMembers.Where(value => value.SourceIdentityId == selectedSource)
                .GroupBy(value => value.SegmentId).Where(group => group.Count() == 2).OrderBy(group => group.Key).Select(group => group.Key).Take(segmentCount).ToArrayAsync();
            Assert.Equal(segmentCount, segments.Length);
            var members = await db.CurrentIndexMembers.Where(value => value.SourceIdentityId == selectedSource).OrderBy(value => value.VectorId).ToArrayAsync();
            var removed = selection is 5 or 7 ? members : segments.Select(segment => members.First(value => value.SegmentId == segment)).ToArray();
            candidate.Id = Guid.NewGuid();
            candidate.BaseGenerationId = await db.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync();
            candidate.BeforeChecksum = candidate.AfterChecksum = candidate.OwnerMachineFingerprint = new string('0', 64);
            candidate.BeforeCount = removed.Length;
            candidate.WorklistComplete = true;
            candidate.CreatedAtUtc = candidate.OwnerStartedAtUtc = DateTimeOffset.UtcNow;
            var state = await db.IndexState.AsNoTracking().SingleAsync();
            candidate.ExpectedIndexFormat = state.RequiredIndexFormat;
            candidate.ExpectedCorpusVersion = candidate.PublicationCorpusVersion = state.CorpusVersion;
            db.IndexPublicationCandidates.Add(candidate);
            foreach (var member in removed) db.IndexCandidateMembers.Add(new()
            {
                CandidateId = candidate.Id, Phase = 0, VectorId = member.VectorId,
                PayloadChecksum = await db.Vectors.Where(value => value.VectorId == member.VectorId).Select(value => value.PayloadChecksum).SingleAsync(),
                SourceIdentityId = member.SourceIdentityId, OwnerSourceRevisionId = member.OwnerSourceRevisionId, SourceRootId = member.SourceRootId
            });
            await db.SaveChangesAsync();
        }
        var table = selection == 1 ? "[CurrentIndexMembers]" : "[Vectors]";
        await db.Database.OpenConnectionAsync();
        // Additional canonical and ledger rows belong to the unrelated source only.
        // SQL-only segments exercise selection plans; no ANN query consumes them.
        await SeedUnrelatedAsync(db, templateVector, 1000);
        var seals = new SealCommands();
        if (selection >= 6)
        {
            // Capture commands executed by the real fenced seal methods, then
            // measure those exact parameterised statements at both scales.
            var factory = new SealFactory(fixture.ConnectionString, seals);
            var store = new SqlSegmentedIndexStore(factory, TimeProvider.System,
                new FluxKnowledge.Integrations.Windows.WindowsInteractiveGpuOwnerProbe());
            var profile = await store.ReadManifestAsync(candidate.BaseGenerationId!.Value, CancellationToken.None);
            var touched = await SqlSegmentedIndexStore.TouchedSegments(db, candidate).ToArrayAsync();
            var builder = new UsearchSegmentBuilder(UsearchIndexOptions.FromConfiguredRoot(environment.IndexRoot),
                new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot));
            var built = await builder.BuildAsync(profile.Generation.ModelFingerprint, profile.Generation.Dimensions,
                store.ReadBuildVectorsAsync(candidate.Id, CancellationToken.None), CancellationToken.None,
                async (segment, vectors, token) =>
                {
                    seals.Enabled = true;
                    try { await store.SealSegmentAsync(candidate.Id, segment, vectors, token); }
                    finally { seals.Enabled = false; }
                }, candidate.Id);
            Assert.Equal(selection == 7 ? 0 : 1, built.Sum(value => value.VectorCount));
            var manifest = new UsearchManifestValidator(UsearchIndexOptions.FromConfiguredRoot(environment.IndexRoot),
                new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot)).Place(
                    new(candidate.CorpusEpoch, candidate.PublicationCorpusVersion), profile.Generation.ModelFingerprint,
                    profile.Generation.Dimensions, profile.Segments.Where(value => !touched.Contains(value.Id)).Concat(built).ToArray());
            seals.Enabled = true;
            try { await store.SealManifestAsync(candidate.Id, manifest, CancellationToken.None); }
            finally { seals.Enabled = false; }
            Assert.Equal(2, seals.Commands.Count(command => command.CommandText.Contains("EXCEPT", StringComparison.Ordinal)));
            if (selection == 6) Assert.Contains(seals.Commands, command => command.CommandText.Contains("[Vectors]", StringComparison.Ordinal));
        }
        _ = await MeasureAsync(); // Warm compilation; do not compare first-use setup.
        var small = await MeasureAsync();
        await SeedUnrelatedAsync(db, templateVector, 9000);
        _ = await MeasureAsync();
        var large = await MeasureAsync();
        output.WriteLine($"selection={selection} table={table} unrelated_vectors=1000/10000 selected_rows={small.Rows}/{large.Rows} rows_read={small.Reads}/{large.Reads} elapsed_ms={small.Milliseconds:F2}/{large.Milliseconds:F2} operators={large.Operators}");
        var expectedRows = selection is 5 or 7 ? 0 : selection == 4 ? 2 : 1;
        Assert.Equal(expectedRows, small.Rows);
        Assert.Equal(expectedRows, large.Rows);
        Assert.InRange(small.Reads, 1, 24 * Math.Max(1, candidate.BeforeCount));
        Assert.InRange(large.Reads, 1, small.Reads + 24);
        Assert.DoesNotContain("Scan", large.Operators, StringComparison.Ordinal);

        async Task<(int Rows, long Reads, double Milliseconds, string Operators)> MeasureAsync()
        {
            var timer = Stopwatch.StartNew();
            if (selection >= 6)
            {
                long reads = 0;
                var operators = new List<string>();
                foreach (var captured in seals.Commands)
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = captured.CommandText;
                    foreach (var parameter in captured.Parameters) command.Parameters.Add(((ICloneable)parameter).Clone());
                    var measured = await ExecutePageAsync(command, "[CurrentIndexMembers]", requireTable: false, countOnly: true);
                    reads += measured.Reads;
                    operators.Add(measured.Operators);
                }
                timer.Stop();
                return (selection == 7 ? 0 : 1, reads, timer.Elapsed.TotalMilliseconds, string.Join(',', operators));
            }
            var touched = new List<string>();
            (List<long> Ids, long Reads, string Operators) capture = selection >= 2 ? await ExecutePageAsync(SqlSegmentedIndexStore.TouchedSegments(db, candidate).CreateDbCommand(),
                "[CurrentIndexMembers]", requireTable: false, segments: touched) : (new List<long>(), 0L, "");
            var page = await ReadPageAsync(0, touched.ToArray());
            // Include the exhaustion query: a small first TOP page can conceal a full scan here.
            (List<long> Ids, long Reads, string Operators) exhausted = page.Ids.Count == 0 ? (new List<long>(), 0L, "") : await ReadPageAsync(page.Ids[^1], touched.ToArray());
            Assert.Empty(exhausted.Ids);
            timer.Stop();
            return (page.Ids.Count, capture.Reads + page.Reads + exhausted.Reads, timer.Elapsed.TotalMilliseconds, capture.Operators + "," + page.Operators + "," + exhausted.Operators);
        }

        async Task<(List<long> Ids, long Reads, string Operators)> ReadPageAsync(long last, string[] touched)
        {
            if (selection >= 2)
            {
                var selected = await ExecutePageAsync(SqlSegmentedIndexStore.QueryBuildVectorIds(db, candidate, last, touched).CreateDbCommand(),
                    "[CurrentIndexMembers]", requireTable: false);
                if (selected.Ids.Count == 0) return selected;
                var payloads = await ExecutePageAsync(SqlSegmentedIndexStore.QueryBuildVectors(db, selected.Ids.ToArray()).CreateDbCommand(), "[Vectors]");
                Assert.Equal(selected.Ids, payloads.Ids);
                return (payloads.Ids, selected.Reads + payloads.Reads, selected.Operators + "," + payloads.Operators);
            }
            return await ExecutePageAsync(selection == 1
                ? SqlSegmentedIndexStore.QueryCurrent(db, candidate, last).CreateDbCommand()
                : SqlSegmentedIndexStore.QueryEligible(db, scopes, last).CreateDbCommand(), table);
        }

        async Task<(List<long> Ids, long Reads, string Operators)> ExecutePageAsync(System.Data.Common.DbCommand command, string measuredTable, bool requireTable = true, List<string>? segments = null, bool countOnly = false)
        {
            // Execute EF's actual parameterised command. XML returns actual operator
            // row counters as a result set rather than relying on provider messages.
            await using var ownedCommand = command;
            command.CommandText = "SET STATISTICS XML ON;\n" + command.CommandText + ";\nSET STATISTICS XML OFF;";
            await using var reader = await command.ExecuteReaderAsync();
            var ids = new List<long>();
            while (await reader.ReadAsync())
                if (countOnly) ids.Add(1);
                else if (segments is not null) segments.Add(reader.GetString(0));
                else ids.Add(reader.GetInt64(reader.GetOrdinal("VectorId")));
            XDocument? plan = null;
            while (await reader.NextResultAsync())
                if (reader.FieldCount == 1 && await reader.ReadAsync()) plan = XDocument.Parse(reader.GetString(0));
            Assert.NotNull(plan);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            var operators = plan.Descendants(ns + "RelOp").Where(op => op.Elements().Any(access =>
                (string?)access.Element(ns + "Object")?.Attribute("Table") == measuredTable ||
                countOnly && (string?)access.Element(ns + "Object")?.Attribute("Table") == "[Vectors]")).ToArray();
            if (requireTable) Assert.NotEmpty(operators);
            var reads = operators.Sum(RowsRead);
            // Empty UNION branches can retain an executed scan operator while
            // reading zero rows. Count every read; report operators doing work.
            return (ids, reads, string.Join(',', operators.Where(op => RowsRead(op) > 0).Select(op => (string?)op.Attribute("PhysicalOp"))));

            long RowsRead(XElement op) => op.Descendants(ns + "RunTimeCountersPerThread")
                .Sum(counter => long.Parse((string?)counter.Attribute("ActualRowsRead") ?? (string?)counter.Attribute("ActualRows") ?? "0", CultureInfo.InvariantCulture));
        }

    }

    [NativeSqlServerTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Full_manifest_membership_proof_scales_across_many_segments_and_rejects_differences(int difference)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The full rebuild membership fixture.", segmented: true);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var template = await db.Vectors.Select(value => value.VectorId).FirstAsync();
        await SeedUnrelatedAsync(db, template, difference == 0 ? 100000 : 10000);
        if (difference == 0)
        {
            // Retained generations make each vector occur in many historical
            // segments, as in a long-lived catalogue. A clean tiny table hides
            // the row-goal anti-join regression seen during full rebuilds.
            await db.Database.ExecuteSqlRawAsync("""
                DECLARE @segments TABLE ([Id] char(64) NOT NULL);
                INSERT [IndexSegments] ([Id], [ModelFingerprint], [Dimensions], [NativeFormat], [DirectoryPath],
                    [MembershipChecksum], [FileChecksum], [FileLength], [VectorCount], [State], [CreatedAtUtc], [SealedAtUtc])
                OUTPUT INSERTED.[Id] INTO @segments
                SELECT LOWER(CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varbinary(16), NEWID())), 2)),
                    [template].[ModelFingerprint], [template].[Dimensions], N'synthetic-sql-only', N'sql-only-no-native-payload',
                    REPLICATE('0',64), REPLICATE('0',64), 1, (SELECT COUNT_BIG(*) FROM [CurrentIndexMembers]), 1,
                    SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
                FROM (SELECT TOP (20) [object_id] FROM sys.all_objects) AS [history]
                CROSS JOIN (SELECT TOP (1) [ModelFingerprint], [Dimensions] FROM [Vectors]) AS [template];
                INSERT [IndexSegmentVectors] ([SegmentId], [VectorId])
                SELECT [segment].[Id], [current].[VectorId] FROM @segments AS [segment] CROSS JOIN [CurrentIndexMembers] AS [current];
                """);
        }
        var state = await db.IndexState.AsNoTracking().SingleAsync();
        var profile = await db.Vectors.AsNoTracking().FirstAsync();
        var members = await db.CurrentIndexMembers.AsNoTracking().OrderBy(value => value.VectorId).ToArrayAsync();
        var candidate = new IndexPublicationCandidateEntity
        {
            Id = Guid.NewGuid(), BuildPurpose = 1, CorpusEpoch = state.CorpusEpoch,
            ExpectedIndexFormat = state.RequiredIndexFormat, ExpectedCorpusVersion = state.CorpusVersion,
            PublicationCorpusVersion = state.CorpusVersion, BaseGenerationId = state.ActiveIndexGenerationId,
            BeforeChecksum = new('0', 64), AfterChecksum = new('0', 64), OwnerMachineFingerprint = new('0', 64),
            WorklistComplete = true, AfterCount = members.Length, CreatedAtUtc = DateTimeOffset.UtcNow,
            OwnerStartedAtUtc = DateTimeOffset.UtcNow
        };
        db.IndexPublicationCandidates.Add(candidate);
        db.IndexCandidateMembers.AddRange(members.Select(value => new IndexCandidateMemberEntity
        {
            CandidateId = candidate.Id, Phase = 1, VectorId = value.VectorId, PayloadChecksum = profile.PayloadChecksum,
            SourceIdentityId = value.SourceIdentityId, OwnerSourceRevisionId = value.OwnerSourceRevisionId, SourceRootId = value.SourceRootId
        }));
        // SQL-only sealed metadata exercises the real membership proof. It is
        // never activated or supplied to an ANN reader and needs no model.
        var segments = new List<IndexSegmentDescriptor>();
        for (var ordinal = 0; ordinal < 70; ordinal++)
        {
            var ids = members.Where((_, index) => index % 70 == ordinal).Select(value => value.VectorId).ToArray();
            var checksum = ordinal.ToString("x64", CultureInfo.InvariantCulture);
            const string format = "synthetic-sql-only";
            var segment = new IndexSegmentDescriptor(IndexSegmentDescriptor.ComputeId(format, checksum, candidate.Id),
                profile.ModelFingerprint, profile.Dimensions, "sql-only-no-native-payload", checksum, new('0', 64),
                1, ids.Length, format, PhysicalIncarnation: candidate.Id);
            segments.Add(segment);
            db.IndexSegments.Add(new()
            {
                Id = segment.Id, ModelFingerprint = segment.ModelFingerprint, Dimensions = segment.Dimensions,
                NativeFormat = format, DirectoryPath = segment.DirectoryPath, MembershipChecksum = checksum,
                FileChecksum = segment.FileChecksum, FileLength = 1, VectorCount = ids.Length, State = 1,
                ProducingCandidateId = candidate.Id, CreatedAtUtc = DateTimeOffset.UtcNow, SealedAtUtc = DateTimeOffset.UtcNow
            });
            db.IndexSegmentVectors.AddRange(ids.Select(id => new IndexSegmentVectorEntity { SegmentId = segment.Id, VectorId = id }));
        }
        await db.SaveChangesAsync();
        var first = members[0].VectorId;
        if (difference == 1) await db.IndexSegmentVectors.Where(value => value.SegmentId == segments[0].Id && value.VectorId == first).ExecuteDeleteAsync();
        if (difference == 2) await db.IndexCandidateMembers.Where(value => value.CandidateId == candidate.Id && value.VectorId == first).ExecuteDeleteAsync();
        var generation = new IndexGenerationDescriptor(Guid.NewGuid(), profile.ModelFingerprint, profile.Dimensions,
            "sql-only-no-native-manifest", new('0', 64), members.Length, new(state.CorpusEpoch, state.CorpusVersion), 2, segments.Count);
        var capture = new SealCommands { Enabled = true };
        var store = new SqlSegmentedIndexStore(new SealFactory(fixture.ConnectionString, capture), TimeProvider.System,
            new FluxKnowledge.Integrations.Windows.WindowsInteractiveGpuOwnerProbe());
        if (difference != 0)
        {
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.SealManifestAsync(candidate.Id, new(generation, segments), CancellationToken.None));
            Assert.Equal("index-manifest-replacement-membership-invalid", refusal.Message);
            Assert.False(await db.IndexGenerations.AnyAsync(value => value.Id == generation.Id));
            Assert.Equal(0, await db.IndexPublicationCandidates.Where(value => value.Id == candidate.Id).Select(value => value.State).SingleAsync());
            return;
        }
        await store.SealManifestAsync(candidate.Id, new(generation, segments), CancellationToken.None);
        var checks = capture.Commands.Where(value => value.CommandText.Contains("EXCEPT", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, checks.Length);
        await db.Database.OpenConnectionAsync();
        foreach (var check in checks)
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SET STATISTICS XML ON;\n" + check.CommandText + ";\nSET STATISTICS XML OFF;";
            foreach (var parameter in check.Parameters) command.Parameters.Add(((ICloneable)parameter).Clone());
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0, Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
            XDocument? plan = null;
            while (await reader.NextResultAsync())
                if (reader.FieldCount == 1 && await reader.ReadAsync()) plan = XDocument.Parse(reader.GetString(0));
            Assert.NotNull(plan);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            var access = plan.Descendants(ns + "RelOp").Where(op => op.Elements().Any(element =>
                (string?)element.Element(ns + "Object")?.Attribute("Table") is "[IndexCandidateMembers]" or "[IndexSegmentVectors]"));
            var reads = access.Sum(op => op.Descendants(ns + "RunTimeCountersPerThread").Sum(counter =>
                long.Parse((string?)counter.Attribute("ActualRowsRead") ?? (string?)counter.Attribute("ActualRows") ?? "0", CultureInfo.InvariantCulture)));
            output.WriteLine($"full_manifest_members={members.Length} segments={segments.Count} membership_rows_read={reads}");
            Assert.InRange(reads, 1, members.Length * 8L);
        }
    }

    private static Task SeedUnrelatedAsync(FluxKnowledgeDbContext db, long templateVector, int count) => db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @chunks TABLE ([Id] bigint NOT NULL);
            DECLARE @ordinal int = (SELECT MAX([Ordinal]) FROM [TextChunks] WHERE [ArtifactId] =
                (SELECT [ArtifactId] FROM [TextChunks] WHERE [Id] = (SELECT [TextChunkId] FROM [Vectors] WHERE [VectorId] = {templateVector})));
            INSERT [TextChunks] ([ArtifactId], [SourceRevision], [Ordinal], [StartOffset], [Length], [Content], [ContentHash],
                [PassagePolicyFingerprint], [ContextHeader], [SearchInputHash])
            OUTPUT INSERTED.[Id] INTO @chunks
            SELECT TOP ({count}) [template].[ArtifactId], [template].[SourceRevision],
                @ordinal + CONVERT(int, ROW_NUMBER() OVER (ORDER BY (SELECT NULL))), [template].[StartOffset], [template].[Length],
                [template].[Content], [template].[ContentHash], [template].[PassagePolicyFingerprint], [template].[ContextHeader], [template].[SearchInputHash]
            FROM [TextChunks] AS [template] CROSS JOIN sys.all_objects AS [a] CROSS JOIN sys.all_objects AS [b]
            WHERE [template].[Id] = (SELECT [TextChunkId] FROM [Vectors] WHERE [VectorId] = {templateVector});
            INSERT [Vectors] ([TextChunkId], [ModelFingerprint], [Dimensions], [Values], [TextChunkContentHash], [PayloadChecksum],
                [SearchInputHash], [SourceRevision], [IsDeleted], [IndexGenerationId], [CreatedAtUtc])
            SELECT [chunk].[Id], [template].[ModelFingerprint], [template].[Dimensions], [template].[Values], [template].[TextChunkContentHash],
                [template].[PayloadChecksum], [template].[SearchInputHash], [template].[SourceRevision], 0, [template].[IndexGenerationId], SYSDATETIMEOFFSET()
            FROM @chunks AS [chunk] CROSS JOIN [Vectors] AS [template] WHERE [template].[VectorId] = {templateVector};
            DECLARE @segment char(64) = LOWER(CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varbinary(16), NEWID())), 2));
            INSERT [IndexSegments] ([Id], [ModelFingerprint], [Dimensions], [NativeFormat], [DirectoryPath], [MembershipChecksum],
                [FileChecksum], [FileLength], [VectorCount], [State], [CreatedAtUtc], [SealedAtUtc])
            SELECT @segment, [template].[ModelFingerprint], [template].[Dimensions], N'synthetic-sql-only', N'sql-only-no-native-payload',
                REPLICATE('0', 64), REPLICATE('0', 64), 1, {count}, 1, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
            FROM [Vectors] AS [template] WHERE [template].[VectorId] = {templateVector};
            INSERT [IndexSegmentVectors] ([SegmentId], [VectorId])
            SELECT @segment, [vector].[VectorId] FROM @chunks AS [chunk] INNER JOIN [Vectors] AS [vector] ON [vector].[TextChunkId] = [chunk].[Id];
            INSERT [CurrentIndexMembers] ([CorpusEpoch], [VectorId], [SegmentId], [SourceIdentityId], [OwnerSourceRevisionId], [SourceRootId])
            SELECT [template].[CorpusEpoch], [vector].[VectorId], @segment, [template].[SourceIdentityId],
                [template].[OwnerSourceRevisionId], [template].[SourceRootId]
            FROM @chunks AS [chunk] INNER JOIN [Vectors] AS [vector] ON [vector].[TextChunkId] = [chunk].[Id]
            CROSS JOIN [CurrentIndexMembers] AS [template] WHERE [template].[VectorId] = {templateVector};
            """);
    private sealed class SealFactory(string connection, SealCommands capture) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection).AddInterceptors(capture).Options);
    }

    private sealed class SealCommands : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public List<(string CommandText, DbParameter[] Parameters)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && (command.CommandText.Contains("[CurrentIndexMembers]", StringComparison.Ordinal) ||
                command.CommandText.Contains("[Vectors]", StringComparison.Ordinal) ||
                command.CommandText.Contains("EXCEPT", StringComparison.Ordinal)))
                Commands.Add((command.CommandText, command.Parameters.Cast<DbParameter>()
                    .Select(value => (DbParameter)((ICloneable)value).Clone()).ToArray()));
            return ValueTask.FromResult(result);
        }
    }

}
