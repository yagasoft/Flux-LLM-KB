using System.Collections;
using System.Data.Common;
using System.Security.Cryptography;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

[Collection("sql-full-text")]
public sealed class ScopedDensePagingIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData("checksum")]
    [InlineData("nonfinite")]
    [InlineData("norm")]
    [InlineData("length")]
    public async Task A_corrupt_low_ranked_vector_on_the_last_page_refuses_the_entire_selection(string corruption)
    {
        var (pipeline, root, record) = await SeedAsync();
        await using var environment = pipeline;
        await using var db = await pipeline.Factory.CreateDbContextAsync();
        var late = await (from vector in db.Vectors join chunk in db.TextChunks on vector.TextChunkId equals chunk.Id
            join artifact in db.Artifacts on chunk.ArtifactId equals artifact.Id
            where artifact.PipelineRecordId == record orderby vector.VectorId descending select vector).FirstAsync();
        var values = late.Values.ToArray();
        if (corruption == "length") values = values[..^4];
        else if (corruption == "norm")
            for (var index = 0; index < values.Length; index += sizeof(float))
                BitConverter.GetBytes(BitConverter.ToSingle(values, index) * 2).CopyTo(values, index);
        else BitConverter.GetBytes(corruption == "nonfinite" ? float.NaN : 3f).CopyTo(values, 0);
        var checksum = corruption == "checksum" ? late.PayloadChecksum : Convert.ToHexStringLower(SHA256.HashData(values));
        // Deliberate corruption is confined to this generated disposable SQL catalog.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [Vectors] SET [Values] = {values}, [PayloadChecksum] = {checksum} WHERE [VectorId] = {late.VectorId}");
        await using var lease = await AcquireAsync(pipeline.Factory);
        var query = await pipeline.Embeddings.CreateEmbeddingAsync("Background evidence.", CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqlCorpusRetrievalReader(pipeline.Factory).ReadDenseCandidatesAsync(lease,
                new("root", [root], null), query.Values, CancellationToken.None));
        Assert.Equal("corpus-dense-vector-invalid", error.Message);
    }

    [NativeSqlServerTheory]
    [InlineData("version", false)]
    [InlineData("retirement", false)]
    [InlineData("lease", false)]
    [InlineData("suppression", false)]
    [InlineData("deletion", false)]
    [InlineData("version", true)]
    [InlineData("retirement", true)]
    [InlineData("lease", true)]
    [InlineData("suppression", true)]
    [InlineData("deletion", true)]
    public async Task Publication_or_lease_changes_between_pages_or_before_hydration_discard_all_accumulated_candidates(
        string mutation, bool beforeHydration)
    {
        var (pipeline, root, record) = await SeedAsync();
        await using var environment = pipeline;
        await using var lease = await AcquireAsync(pipeline.Factory);
        var observer = new PageObserver(async token =>
        {
            await using var db = await pipeline.Factory.CreateDbContextAsync(token);
            if (mutation == "lease")
            {
                await db.CorpusQueryLeases.Where(value => value.Id == lease.LeaseId).ExecuteDeleteAsync(token);
                return;
            }
            if (mutation == "retirement")
            {
                await db.IndexGenerations.Where(value => value.Id == lease.Generation.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(value => value.RetiredAtUtc, DateTimeOffset.UtcNow), token);
                return;
            }
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
            // Follow the publication writer's fence/version ordering in the test mutation.
            await db.Database.ExecuteSqlRawAsync("SELECT [Id] FROM [IndexState] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = 1", token);
            var state = await db.IndexState.SingleAsync(token);
            state.CorpusVersion++;
            if (mutation == "deletion")
                await db.PipelineRecords.Where(value => value.Id == record)
                    .ExecuteUpdateAsync(set => set.SetProperty(value => value.IsDeleted, true), token);
            if (mutation == "suppression")
                await db.SourceRevisions.Where(value => value.SourceRootId == root)
                    .ExecuteUpdateAsync(set => set.SetProperty(value => value.SuppressedAtUtc, DateTimeOffset.UtcNow), token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }, beforeHydration);
        var query = await pipeline.Embeddings.CreateEmbeddingAsync("Background evidence.", CancellationToken.None);
        var result = await new SqlCorpusRetrievalReader(ObservedFactory(observer)).ReadDenseCandidatesAsync(lease,
            new("root", [root], null), query.Values, CancellationToken.None);
        Assert.True(observer.Fired);
        Assert.Equal(beforeHydration ? 3 : 2, observer.PageCount);
        Assert.Equal("index-updating", result.Status);
        Assert.Empty(result.Candidates);
        Assert.False(await lease.IsCurrentAsync(CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Cancellation_interrupts_scoring_and_a_blocked_later_page_without_partial_results()
    {
        var (pipeline, root, record) = await SeedAsync();
        await using var environment = pipeline;
        await using var lease = await AcquireAsync(pipeline.Factory);
        var embedding = await pipeline.Embeddings.CreateEmbeddingAsync("Background evidence.", CancellationToken.None);
        using (var cancellation = new CancellationTokenSource())
        {
            var query = new CancellingQuery(embedding.Values, cancellation);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await new SqlCorpusRetrievalReader(pipeline.Factory).ReadDenseCandidatesAsync(lease,
                    new("root", [root], null), query, cancellation.Token));
            Assert.True(query.IndexReads > embedding.Values.Count);
        }
        await using var blocker = new SqlConnection(fixture.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new PageObserver(async token =>
        {
            await using var db = await pipeline.Factory.CreateDbContextAsync(token);
            var next = await (from vector in db.Vectors join chunk in db.TextChunks on vector.TextChunkId equals chunk.Id
                join artifact in db.Artifacts on chunk.ArtifactId equals artifact.Id
                where artifact.PipelineRecordId == record orderby vector.VectorId select vector.VectorId).Skip(256).FirstAsync(token);
            await using var command = new SqlCommand("SELECT [VectorId] FROM [Vectors] WITH (XLOCK, HOLDLOCK) WHERE [VectorId] = @id", blocker, transaction);
            command.Parameters.AddWithValue("@id", next);
            await command.ExecuteNonQueryAsync(token);
            blocked.TrySetResult();
        });
        using var ioCancellation = new CancellationTokenSource();
        var pending = new SqlCorpusRetrievalReader(ObservedFactory(observer)).ReadDenseCandidatesAsync(lease,
            new("root", [root], null), embedding.Values, ioCancellation.Token).AsTask();
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);
        ioCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await transaction.RollbackAsync();
        Assert.Equal(2, observer.PageCount);
        Assert.True(await lease.IsCurrentAsync(CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Workspace_containment_multi_root_sparse_membership_ties_and_empty_scope_use_current_SQL_only()
    {
        var (pipeline, firstRoot, firstRecord) = await SeedAsync(count: 130);
        await using var environment = pipeline;
        const string strongerOutside = "OutsideWinner forbidden.";
        var outside = await pipeline.AddRetainedAndPumpAsync(strongerOutside);
        var nested = await pipeline.AddRetainedAndPumpAsync(string.Join('\n', Enumerable.Repeat("Background evidence.", 130)));
        await using var db = await pipeline.Factory.CreateDbContextAsync();
        await BindInputsAsync(db);
        var sources = await (from record in db.PipelineRecords join source in db.SourceRevisions on record.SourceRevisionId equals source.Id
            select new { Record = record.Id, Source = source }).ToArrayAsync();
        var nestedSource = sources.Single(value => value.Record == nested).Source;
        var outsideSource = sources.Single(value => value.Record == outside).Source;
        // Different roots and a sibling prefix exercise the same path predicates as a nested workspace.
        var cwd = @"C:\scoped\project";
        await db.SourceRevisions.Where(value => value.Id == nestedSource.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(value => value.CanonicalPath, cwd + @"\child\nested.txt"));
        await db.SourceRevisions.Where(value => value.Id == outsideSource.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(value => value.CanonicalPath, cwd + @"-sibling\outside.txt"));
        var firstSource = sources.Single(value => value.Record == firstRecord).Source;
        await db.SourceRevisions.Where(value => value.Id == firstSource.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(value => value.CanonicalPath, cwd + @"\first.txt"));
        await using var lease = await AcquireAsync(pipeline.Factory);
        var query = await pipeline.Embeddings.CreateEmbeddingAsync(strongerOutside, CancellationToken.None);
        var inside = await pipeline.Embeddings.CreateEmbeddingAsync("Background evidence.", CancellationToken.None);
        Assert.True(Enumerable.Range(0, query.Values.Count).Sum(index => (double)inside.Values[index] * query.Values[index]) < 0.99);
        var reader = new SqlCorpusRetrievalReader(pipeline.Factory);
        var allRoots = new[] { firstRoot, nestedSource.SourceRootId, outsideSource.SourceRootId };
        var workspace = await reader.ReadDenseCandidatesAsync(lease, new("workspace", allRoots, cwd), query.Values, CancellationToken.None);
        Assert.Equal("ready", workspace.Status);
        Assert.Equal(100, workspace.Candidates.Count);
        Assert.DoesNotContain(workspace.Candidates, value => value.PipelineRecordId == outside);
        var union = await reader.ReadDenseCandidatesAsync(lease, new("root", [firstRoot, nestedSource.SourceRootId], null), query.Values, CancellationToken.None);
        Assert.Equal(workspace.Candidates.Select(value => value.ChunkId), union.Candidates.Select(value => value.ChunkId));
        var expected = await (from vector in db.Vectors join chunk in db.TextChunks on vector.TextChunkId equals chunk.Id
            join artifact in db.Artifacts on chunk.ArtifactId equals artifact.Id
            where artifact.PipelineRecordId == firstRecord || artifact.PipelineRecordId == nested
            orderby vector.VectorId select vector.TextChunkId).Take(100).ToArrayAsync();
        Assert.Equal(expected, union.Candidates.Select(value => value.ChunkId));
        var empty = await reader.ReadDenseCandidatesAsync(lease, new("root", [Guid.NewGuid()], null), query.Values, CancellationToken.None);
        Assert.Equal("ready", empty.Status);
        Assert.Empty(empty.Candidates);
    }

    private async Task<(SqlToUsearchRebuildTests.PipelineEnvironment Pipeline, Guid Root, Guid Record)> SeedAsync(int count = 513)
    {
        var builder = new PassageBuilder(new Words(), new PassagePolicy(2, 2, 128, 0, 0));
        var pipeline = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Baseline.", builder);
        var record = await pipeline.AddRetainedAndPumpAsync(string.Join('\n', Enumerable.Repeat("Background evidence.", count)));
        await using var db = await pipeline.Factory.CreateDbContextAsync();
        await BindInputsAsync(db);
        var root = await (from value in db.PipelineRecords join source in db.SourceRevisions on value.SourceRevisionId equals source.Id
            where value.Id == record select source.SourceRootId).SingleAsync();
        return (pipeline, root, record);
    }

    private static Task<int> BindInputsAsync(FluxKnowledgeDbContext db) => db.Database.ExecuteSqlRawAsync(
        "UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");

    private static async Task<ICorpusGenerationLease> AcquireAsync(IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        var lease = await new SqlCorpusGenerationLeaseStore(factory, TimeProvider.System).TryAcquireAsync(Guid.NewGuid(),
            new WindowsInteractiveGpuOwnerProbe().Current, DeterministicTokenHashEmbeddingProvider.Fingerprint, 256, CancellationToken.None);
        Assert.NotNull(lease);
        Assert.IsNotAssignableFrom<ICorpusAnnLease>(lease);
        return lease;
    }

    private IDbContextFactory<FluxKnowledgeDbContext> ObservedFactory(PageObserver observer) => new Factory(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).AddInterceptors(observer).Options);

    private sealed class Factory(DbContextOptions<FluxKnowledgeDbContext> options) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(options);
    }

    private sealed class Words : IPassageTokenizer
    {
        public string Fingerprint => "synthetic-scoped-pages-v1";
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private sealed class PageObserver(Func<CancellationToken, Task> mutate, bool beforeHydration = false) : DbCommandInterceptor
    {
        public int PageCount { get; private set; }
        public bool Fired { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var page = command.CommandText.Contains("FROM [IndexGenerationVectors] AS [member]", StringComparison.Ordinal);
            if (page)
            {
                PageCount++;
                Assert.Contains("[vector].[VectorId] >", command.CommandText, StringComparison.Ordinal);
                var budget = command.Parameters.Cast<DbParameter>().Select(value => value.Value).OfType<int>();
                Assert.Contains(256, budget);
            }
            var hydrate = command.CommandText.Contains("[chunk].[Id] IN (SELECT [Value]", StringComparison.Ordinal);
            if (!Fired && (beforeHydration ? hydrate : page && PageCount == 2))
            {
                Fired = true;
                await mutate(cancellationToken);
            }
            return result;
        }
    }

    private sealed class CancellingQuery(IReadOnlyList<float> values, CancellationTokenSource cancellation) : IReadOnlyList<float>
    {
        public int IndexReads { get; private set; }
        public int Count => values.Count;
        public float this[int index]
        {
            get { if (++IndexReads == values.Count + 8) cancellation.Cancel(); return values[index]; }
        }
        public IEnumerator<float> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
