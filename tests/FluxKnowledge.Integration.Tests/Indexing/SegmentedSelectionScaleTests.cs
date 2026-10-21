using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
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
    public async Task A_one_source_delta_does_not_scan_ten_times_more_unrelated_rows(int selection)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The selected source has one small unchanged passage.", publish: selection != 2, segmented: true);
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
        var table = selection == 1 ? "[CurrentIndexMembers]" : "[Vectors]";
        await db.Database.OpenConnectionAsync();
        // Additional canonical and ledger rows belong to the unrelated source only.
        // SQL-only segments exercise selection plans; no ANN query consumes them.
        await SeedUnrelatedAsync(1000);
        _ = await MeasureAsync(); // Warm compilation; do not compare first-use setup.
        var small = await MeasureAsync();
        await SeedUnrelatedAsync(9000);
        _ = await MeasureAsync();
        var large = await MeasureAsync();
        output.WriteLine($"selection={selection} table={table} unrelated_vectors=1000/10000 selected_rows={small.Rows}/{large.Rows} rows_read={small.Reads}/{large.Reads} elapsed_ms={small.Milliseconds:F2}/{large.Milliseconds:F2} operators={large.Operators}");
        Assert.Equal(1, small.Rows);
        Assert.Equal(1, large.Rows);
        Assert.InRange(small.Reads, 1, 24);
        Assert.InRange(large.Reads, 1, small.Reads + 24);
        Assert.DoesNotContain("Scan", large.Operators, StringComparison.Ordinal);

        async Task<(int Rows, long Reads, double Milliseconds, string Operators)> MeasureAsync()
        {
            var timer = Stopwatch.StartNew();
            var page = await ReadPageAsync(0);
            Assert.NotEmpty(page.Ids);
            // Include the exhaustion query: a small first TOP page can conceal a full scan here.
            var exhausted = await ReadPageAsync(page.Ids[^1]);
            Assert.Empty(exhausted.Ids);
            timer.Stop();
            return (page.Ids.Count, page.Reads + exhausted.Reads, timer.Elapsed.TotalMilliseconds, page.Operators + "," + exhausted.Operators);
        }

        async Task<(List<long> Ids, long Reads, string Operators)> ReadPageAsync(long last)
        {
            // Execute EF's actual parameterised command. XML returns actual operator
            // row counters as a result set rather than relying on provider messages.
            await using var command = selection switch
            {
                1 => SqlSegmentedIndexStore.QueryCurrent(db, candidate, last).CreateDbCommand(),
                2 => SqlSegmentedIndexStore.QueryBuildVectors(db, candidate, last).CreateDbCommand(),
                _ => SqlSegmentedIndexStore.QueryEligible(db, scopes, last).CreateDbCommand()
            };
            command.CommandText = "SET STATISTICS XML ON;\n" + command.CommandText + ";\nSET STATISTICS XML OFF;";
            await using var reader = await command.ExecuteReaderAsync();
            var ids = new List<long>();
            while (await reader.ReadAsync()) ids.Add(reader.GetInt64(reader.GetOrdinal("VectorId")));
            XDocument? plan = null;
            while (await reader.NextResultAsync())
                if (reader.FieldCount == 1 && await reader.ReadAsync()) plan = XDocument.Parse(reader.GetString(0));
            Assert.NotNull(plan);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            var operators = plan.Descendants(ns + "RelOp").Where(op => op.Elements().Any(access =>
                (string?)access.Element(ns + "Object")?.Attribute("Table") == table)).ToArray();
            Assert.NotEmpty(operators);
            var reads = operators.Sum(op => op.Descendants(ns + "RunTimeCountersPerThread")
                .Sum(counter => long.Parse((string?)counter.Attribute("ActualRowsRead") ?? (string?)counter.Attribute("ActualRows") ?? "0", CultureInfo.InvariantCulture)));
            return (ids, reads, string.Join(',', operators.Select(op => (string?)op.Attribute("PhysicalOp"))));
        }

        Task SeedUnrelatedAsync(int count) => db.Database.ExecuteSqlInterpolatedAsync($"""
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
    }

}
