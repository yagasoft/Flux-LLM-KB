using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Cleanup_target_capture_is_stable_after_mixed_execution_and_audit_history()
    {
        await Execution_pages_delete_mixed_ocr_and_embedding_history_through_the_complete_graph();
        await InitializeAsync();
        await Cancellation_after_audit_delete_rolls_back_the_page_and_preserves_its_source_anchors();
        await InitializeAsync();
        await Graph_purge_removes_bounded_vector_then_chunk_pages_and_replays_the_same_receipt();
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_graph_keeps_ownership_on_server_with_unrelated_publications_in_both_cache_orders(bool ownedPageFirst)
    {
        var graph = await SeedArtifactPageGraphAsync([(48, 0), (48, 0), (60, 0)]);
        var publication = await SeedPublicationQueryGraphAsync(graph.Source, graph.Control, 35000);
        var identities = await ReadIdentityIdsAsync(graph.Source, graph.Control);
        var budget = new GraphOwnershipParameterBudget();
        var timing = new GraphCommandTimings();
        var factory = new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString).AddInterceptors(budget, timing).Options);
        var store = new SqlSourceDeletionStore(factory, TimeProvider.System);
        var maxUnit = 0d;
        if (ownedPageFirst) await RunPageAsync();
        await SeedUnrelatedPublicationsAsync(graph.Control, publication.ForeignBranch);
        if (!ownedPageFirst) await RunPageAsync();
        await AssertArtifactPageAnchorsAsync(graph.Source, graph.ArtifactIds[1..]);
        await using (var check = CreateContext())
        {
            Assert.Equal(600, await check.DocumentPublications.CountAsync());
            Assert.False(await check.DocumentPublications.AnyAsync(value => value.OwnerSourceRevisionId == graph.Source.RevisionId));
            Assert.False(await check.DocumentPublications.AnyAsync(value => value.DocumentInputSourceRevisionId == graph.Source.RevisionId));
        }
        // The next call repeats an empty owned-publication delete against the
        // foreign catalogue, which caused the production continuation timeout.
        await RunPageAsync();
        await AssertArtifactPageAnchorsAsync(graph.Source, graph.ArtifactIds[2..]);
        await CompleteArtifactGraphAsync(store, graph.Source, graph.Control, graph.Operation,
            elapsed => maxUnit = Math.Max(maxUnit, elapsed));
        await using (var check = CreateContext())
        {
            Assert.Equal(600, await check.DocumentPublications.CountAsync());
            Assert.True(await check.SourceProcessorBranches.AnyAsync(value => value.Id == publication.ForeignBranch));
            Assert.False(await check.SourceIdentities.AnyAsync(value => value.Id == identities.Owned));
            Assert.True(await check.SourceIdentities.AnyAsync(value => value.Id == identities.Foreign));
        }
        Assert.True(maxUnit < 30_000, $"Graph unit took {maxUnit:F1}ms.");
        Assert.True(timing.MaxMs < 15_000, $"Graph statement took {timing.MaxMs:F1}ms: {timing.Slowest}");
        output.WriteLine($"publication_graph owned_first={ownedPageFirst} max_unit_ms={maxUnit:F1} max_statement_ms={timing.MaxMs:F1} max_guid_values={budget.MaximumGuidValues}");
        return;

        async Task RunPageAsync()
        {
            var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            var watch = Stopwatch.StartNew();
            Assert.Equal("purge-records", (await store.PurgeAsync(work, null, default)).Phase);
            maxUnit = Math.Max(maxUnit, watch.Elapsed.TotalMilliseconds);
        }
    }

    [NativeSqlServerFact]
    public async Task Final_graph_preserves_a_source_identity_shared_by_another_root()
    {
        var graph = await SeedArtifactPageGraphAsync([(0, 0)]);
        var identities = await ReadIdentityIdsAsync(graph.Source, graph.Control);
        var foreignRecord = Guid.NewGuid();
        var foreignRevision = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceRevisions.Add(new() { Id = foreignRevision, SourceRootId = graph.Control.RootId,
                StableSourceIdentity = "shared-identity-control", Revision = 2, ContentSha256 = new string('a', 64),
                CanonicalPath = "C:\\synthetic\\shared-identity-control.txt", Classification = "AcceptedUtf8Text",
                Extension = ".txt", ByteLength = 4, DiscoveredAtUtc = DateTimeOffset.UtcNow });
            setup.PipelineRecords.Add(new() { Id = foreignRecord, SourceIdentityId = identities.Owned,
                SourceRevisionId = foreignRevision, Revision = 2, ContentHash = new string('a', 64),
                RootLineageRecordId = foreignRecord, RegisteredAtUtc = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
        }
        await CompleteArtifactGraphAsync(new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System),
            graph.Source, graph.Control, graph.Operation);
        await using var check = CreateContext();
        Assert.True(await check.SourceIdentities.AnyAsync(value => value.Id == identities.Owned));
        Assert.True(await check.PipelineRecords.AnyAsync(value => value.Id == foreignRecord && value.SourceIdentityId == identities.Owned));
    }

    private async Task<(Guid Owned, Guid Foreign)> ReadIdentityIdsAsync(SeededRoot source, SeededRoot control)
    {
        await using var context = CreateContext();
        return (await context.PipelineRecords.Where(value => value.Id == source.RecordId).Select(value => value.SourceIdentityId).SingleAsync(),
            await context.PipelineRecords.Where(value => value.Id == control.RecordId).Select(value => value.SourceIdentityId).SingleAsync());
    }

    private async Task<(Guid OwnedBranch, Guid ForeignBranch, Guid OwnedActivity, Guid ForeignActivity)> SeedPublicationQueryGraphAsync(
        SeededRoot source, SeededRoot control, int extraRevisions)
    {
        var now = DateTimeOffset.UtcNow;
        var hash = new string('a', 64);
        var overlapRevision = Guid.NewGuid();
        var ownedActivity = Guid.NewGuid();
        var foreignActivity = Guid.NewGuid();
        var ownedBranch = Guid.NewGuid();
        var foreignBranch = Guid.NewGuid();
        await using var setup = CreateContext();
        setup.SourceRevisions.Add(new() { Id = overlapRevision, SourceRootId = source.RootId,
            StableSourceIdentity = "publication-overlap", Revision = 1, ContentSha256 = hash,
            CanonicalPath = "C:\\synthetic\\publication-overlap.txt", Classification = "AcceptedUtf8Text",
            Extension = ".txt", ByteLength = 4, DiscoveredAtUtc = now });
        AddActivity(ownedActivity, source.RevisionId, "owned");
        AddActivity(foreignActivity, control.RevisionId, "foreign");
        AddBranch(ownedBranch, ownedActivity, source.RevisionId);
        AddBranch(foreignBranch, foreignActivity, control.RevisionId);
        setup.DocumentPublications.AddRange(
            Publication(source.RevisionId, control.RevisionId, ownedBranch, control.RecordId),
            Publication(control.RevisionId, source.RevisionId, foreignBranch, control.RecordId),
            Publication(overlapRevision, overlapRevision, ownedBranch, source.RecordId));
        await setup.SaveChangesAsync();
        if (extraRevisions > 0)
        {
            await setup.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT SourceRevisions(Id,SourceRootId,StableSourceIdentity,Revision,ContentSha256,CanonicalPath,Classification,Extension,ByteLength,DiscoveredAtUtc,OriginKind)
                SELECT id,{source.RootId},CONVERT(nvarchar(36),id),1,{hash},CONCAT('C:\publication-query\',id),'AcceptedUtf8Text','.txt',4,SYSUTCDATETIME(),0
                FROM(SELECT TOP({extraRevisions}) NEWID() AS id FROM sys.all_objects a CROSS JOIN sys.all_objects b) seed;
                """);
        }
        return (ownedBranch, foreignBranch, ownedActivity, foreignActivity);

        void AddActivity(Guid id, Guid revision, string version) => setup.SourceActivities.Add(new()
        {
            Id = id, SourceRevisionId = revision, ActivityKind = 0, ExecutionClass = 0, ProcessorVersion = version,
            InputFingerprint = hash, State = (int)SourceActivityState.Completed, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        void AddBranch(Guid id, Guid activity, Guid revision) => setup.SourceProcessorBranches.Add(new()
        {
            Id = id, SourceActivityId = activity, SourceRevisionId = revision, InputSha256 = hash,
            ProcessorVersion = "synthetic", ProcessorFingerprint = hash, State = (int)RetainedProcessorBranchState.Completed,
            CreatedAtUtc = now, UpdatedAtUtc = now
        });
        DocumentPublicationEntity Publication(Guid owner, Guid input, Guid branch, Guid record) => new()
        {
            OwnerSourceRevisionId = owner, DocumentInputSourceRevisionId = input, SourceProcessorBranchId = branch,
            PipelineRecordId = record, PipelineRecordRevision = 1, ProcessorFingerprint = hash, PublishedAtUtc = now
        };
    }

    private async Task SeedUnrelatedPublicationsAsync(SeededRoot control, Guid branch)
    {
        await using var setup = CreateContext();
        await setup.Database.OpenConnectionAsync();
        await setup.Database.ExecuteSqlRawAsync("CREATE TABLE #ForeignPublications(Id uniqueidentifier NOT NULL PRIMARY KEY);");
        var hash = new string('a', 64);
        await setup.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT #ForeignPublications SELECT TOP(600) NEWID() FROM sys.all_objects;
            INSERT SourceRevisions(Id,SourceRootId,StableSourceIdentity,Revision,ContentSha256,CanonicalPath,Classification,Extension,ByteLength,DiscoveredAtUtc,OriginKind)
            SELECT Id,{control.RootId},CONVERT(nvarchar(36),Id),1,{hash},CONCAT('C:\foreign-publication\',Id),'AcceptedUtf8Text','.txt',4,SYSUTCDATETIME(),0 FROM #ForeignPublications;
            INSERT DocumentPublications(OwnerSourceRevisionId,DocumentInputSourceRevisionId,SourceProcessorBranchId,PipelineRecordId,PipelineRecordRevision,ProcessorFingerprint,PublishedAtUtc)
            SELECT Id,Id,{branch},{control.RecordId},1,{hash},SYSUTCDATETIME() FROM #ForeignPublications;
            DROP TABLE #ForeignPublications;
            """);
    }

    private sealed class GraphOwnershipParameterBudget : DbCommandInterceptor
    {
        public int MaximumGuidValues { get; private set; }
        private void Check(DbCommand command)
        {
            var count = 0;
            foreach (DbParameter parameter in command.Parameters)
            {
                if (parameter.Value is Guid) count++;
                else if (parameter.Value is string { Length: > 0 } value && value[0] == '[')
                {
                    using var json = JsonDocument.Parse(value);
                    count += json.RootElement.EnumerateArray().Count(item => item.ValueKind == JsonValueKind.String && item.TryGetGuid(out _));
                }
            }
            MaximumGuidValues = Math.Max(MaximumGuidValues, count);
            Assert.True(count <= 4096, $"Final graph ownership must stay on the server; received {count} GUID values.");
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }
    }
}
