using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

[Collection("sql-full-text")]
public sealed class WindowEligibilityWriterTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public async Task Missing_flags_request_durable_work_only_when_a_backfill_writer_is_registered(bool writerEnabled,bool direct)
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"A bounded publication awaiting a derived flag.",segmented:true);
        await using var db=environment.Factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
        await db.TextChunkWindowEligibilities.ExecuteDeleteAsync();
        var signal=new BackfillSignal();
        var guarded=new GuardedFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString)
            .AddInterceptors(new RejectReadOnlyMutations(writerEnabled)).Options);
        var reader=new SqlCorpusRetrievalReader(guarded,new CorpusAnnBindingCache(),windowBackfillSignal:writerEnabled?signal:null);
        var lease=await new SqlCorpusGenerationLeaseStore(environment.Factory,TimeProvider.System).TryAcquireReadableAsync(
            Guid.NewGuid(),new WindowsInteractiveGpuOwnerProbe().Current,DeterministicTokenHashEmbeddingProvider.Fingerprint,256,default);
        Assert.NotNull(lease);
        using var scope=environment.Services.CreateScope();
        var manifest=await scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>().ReadManifestAsync(lease.Generation.Id,default);
        var segment=Assert.Single(manifest.Segments);
        var error=await Assert.ThrowsAsync<PassageRetrievalRefusalException>(async () =>
        {
            if(direct) await reader.ReadSegmentKeysAsync(lease,new("all",[],null),segment,default);
            else await reader.PrepareAsync(lease,segment,default);
        });
        Assert.Equal("warming",error.Message);
        Assert.Equal(writerEnabled?1:0,signal.Requests);
        Assert.Equal(writerEnabled?1:0,await db.TextChunkWindowBackfillStates.CountAsync());
        await lease.DisposeAsync();
    }

    [NativeSqlServerFact]
    public async Task Preparation_validates_all_memberships_but_never_requires_flags_for_a_deleting_source()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"The keeper remains searchable.",segmented:true);
        var deleting = await environment.AddRetainedAndPumpAsync("The removed source must not be backfilled.");
        await using var db = environment.Factory.CreateDbContext();
        var root = await db.PipelineRecords.Where(value => value.Id==deleting).Select(value => value.SourceRevision!.SourceRootId).SingleAsync();
        await db.SourceRootConfigurations.Where(value => value.Id==root).ExecuteUpdateAsync(set => set.SetProperty(value => value.State,2));
        var artifacts = db.Artifacts.Where(value => value.PipelineRecordId==deleting).Select(value => value.Id);
        await db.TextChunkWindowEligibilities.Where(value => artifacts.Contains(value.ArtifactId)).ExecuteDeleteAsync();
        var reader = new SqlCorpusRetrievalReader(environment.Factory,new CorpusAnnBindingCache());
        var lease = await new SqlCorpusGenerationLeaseStore(environment.Factory,TimeProvider.System).TryAcquireReadableAsync(
            Guid.NewGuid(),new WindowsInteractiveGpuOwnerProbe().Current,DeterministicTokenHashEmbeddingProvider.Fingerprint,256,default);
        Assert.NotNull(lease);
        using var scope = environment.Services.CreateScope();
        await using var ann = await scope.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(lease,default);
        await ann.PrepareAsync(reader,default);
        Assert.True(ann.IsPrepared(reader));
        Assert.False(await db.TextChunkWindowEligibilities.AnyAsync(value => artifacts.Contains(value.ArtifactId)));
        Assert.Empty(await db.TextChunkWindowBackfillStates.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Restart_preparation_reuses_durable_flags_without_reading_canonical_windows()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "A durable flag survives process restart.\nPublication and disclosure remain authoritative.",segmented:true);
        await using var db = environment.Factory.CreateDbContext();
        var before = await db.TextChunkWindowEligibilities.OrderBy(value => value.TextChunkId).Select(value => value.CalculatedAtUtc).ToArrayAsync();
        Assert.NotEmpty(before);
        var guard = new NoCanonicalPreparationReads();
        var contexts = new GuardedFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).AddInterceptors(guard).Options);
        using var scope = environment.Services.CreateScope();
        for (var restart = 0; restart < 2; restart++)
        {
            var reader = new SqlCorpusRetrievalReader(contexts,new CorpusAnnBindingCache());
            var lease = await new SqlCorpusGenerationLeaseStore(environment.Factory,TimeProvider.System).TryAcquireReadableAsync(
                Guid.NewGuid(),new WindowsInteractiveGpuOwnerProbe().Current,DeterministicTokenHashEmbeddingProvider.Fingerprint,256,default);
            Assert.NotNull(lease);
            await using var ann = await scope.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(lease,default);
            await ann.PrepareAsync(reader,default);
            Assert.True(ann.IsPrepared(reader));
        }
        Assert.Equal(2,guard.Reads);
        Assert.Equal(before,await db.TextChunkWindowEligibilities.OrderBy(value => value.TextChunkId).Select(value => value.CalculatedAtUtc).ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_canonical_indexing_persists_true_and_false_window_decisions_with_exact_bindings(bool longLine)
    {
        var text = longLine ? new string('x',40_000) : "Ordinary retained passage.\nA second complete line.";
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,text,publish:false,embed:false);
        await using var context = environment.Factory.CreateDbContext();
        var chunks = await context.TextChunks.Include(value => value.Artifact).Include(value => value.WindowEligibility).ToArrayAsync();
        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            var proof = Assert.IsType<FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.TextChunkWindowEligibilityEntity>(chunk.WindowEligibility);
            Assert.Equal(chunk.Id,proof.TextChunkId);
            Assert.Equal(chunk.ArtifactId,proof.ArtifactId);
            Assert.Equal(chunk.Artifact.SourceRevision,proof.CanonicalSourceRevision);
            Assert.Equal(chunk.Artifact.ContentHash,proof.CanonicalContentHash);
            Assert.Equal(chunk.Artifact.SearchText.Length,proof.CanonicalLength);
            Assert.Equal(chunk.SourceRevision,proof.ChunkSourceRevision);
            Assert.Equal(chunk.ContentHash,proof.ChunkContentHash);
            Assert.Equal(chunk.StartOffset,proof.StartOffset);
            Assert.Equal(chunk.Length,proof.Length);
            Assert.Equal(CanonicalWindowEligibility.RuleVersion,proof.RuleVersion);
            Assert.Equal(CanonicalWindowEligibility.Evaluate(chunk.Artifact.SearchText,chunk.StartOffset,chunk.Length,chunk.Content),proof.Available);
        }
        Assert.Contains(chunks,value => value.WindowEligibility!.Available == !longLine);
        Assert.Equal(chunks.Length,await context.TextChunkWindowEligibilities.CountAsync());
    }

    private sealed class GuardedFactory(DbContextOptions<FluxKnowledgeDbContext> options) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(options);
    }

    private sealed class NoCanonicalPreparationReads : DbCommandInterceptor
    {
        public int Reads { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,
            InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            if (command.CommandText.Contains("corpus-ann-static-bindings",StringComparison.Ordinal))
            {
                Reads++;
                Assert.DoesNotContain("#CorpusAnnWindows",command.CommandText,StringComparison.Ordinal);
                Assert.DoesNotContain("[chunk].[Content]",command.CommandText,StringComparison.Ordinal);
                Assert.DoesNotContain("WindowText",command.CommandText,StringComparison.Ordinal);
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class BackfillSignal : ITextChunkWindowBackfillSignal
    {
        public int Requests { get; private set; }
        public void RequestBackfill()=>Requests++;
    }
    private sealed class RejectReadOnlyMutations(bool writerEnabled) : DbCommandInterceptor
    {
        private void Check(DbCommand command)
        {
            if(!writerEnabled && System.Text.RegularExpressions.Regex.IsMatch(command.CommandText,@"\b(INSERT|UPDATE|DELETE)\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidOperationException("A read-only retrieval attempted a derived-data mutation.");
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,
            InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default){Check(command);return ValueTask.FromResult(result);}
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,
            InterceptionResult<int> result,CancellationToken cancellationToken=default){Check(command);return ValueTask.FromResult(result);}
    }
}
