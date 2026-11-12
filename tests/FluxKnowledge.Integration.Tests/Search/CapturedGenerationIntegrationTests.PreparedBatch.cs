using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

public sealed partial class CapturedGenerationIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Prepared_batch_failures_preserve_recovery_signals_and_every_original_cause(bool searchFails, bool permissionFailure)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "A guarded prepared read.", segmented: true);
        using var services = environment.Services.CreateScope();
        var store = services.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        var generation = await environment.Store.GetActiveGenerationIdAsync(default);
        var manifest = await store.ReadManifestAsync(generation!.Value, default);
        var segment = Assert.Single(manifest.Segments);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var ids = await db.IndexSegmentVectors.Where(row => row.SegmentId == segment.Id).Select(row => row.VectorId).ToArrayAsync();
        var options = new UsearchIndexOptions(environment.IndexRoot);
        var paths = new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot);
        await using var cache = new UsearchSegmentCache(options, paths,
            new SqlIndexSegmentOwnerStore(environment.Factory, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe()), TimeProvider.System);
        var signal = new BatchRecoverySignal();
        var factory = new UsearchCorpusAnnLeaseFactory(new SqlPipelineStore(environment.Factory), new UsearchGenerationValidator(), store, options, paths, cache, signal);
        var sql = await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System));
        var guarded = new BatchFailureLease(sql);
        await using var ann = await factory.OpenAsync(guarded, default);
        await ann.PrepareAsync(new SqlCorpusRetrievalReader(environment.Factory), default);
        var original = new IndexGenerationValidationException("synthetic final readability proof failure");
        Exception cleanup = permissionFailure ? new UnauthorizedAccessException("synthetic native close permission failure") : new IOException("synthetic native close uncertainty");
        cache.BeforeNativeDisposal = _ => throw cleanup;
        guarded.AfterSearch = async ct => { await cache.DisposeAsync(); if (searchFails) throw original; return await sql.IsReadableAsync(ct); };
        var vector = await environment.Embeddings.CreateEmbeddingAsync("guarded evidence", default);
        try
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => ann.SearchAsync(vector.Values, 10,
                new("all", [], null), new PreparedKeySelection(new Dictionary<string, IReadOnlySet<long>> { [segment.Id] = ids.ToHashSet() }), default).AsTask());
            Assert.Contains(cleanup, error.Flatten().InnerExceptions);
            if (searchFails) Assert.Contains(original, error.Flatten().InnerExceptions);
            Assert.Contains(signal.Faults, fault => fault.Category == (permissionFailure ? DerivedIndexRecoveryFailureCategory.PermissionsDenied : DerivedIndexRecoveryFailureCategory.TransientIo));
            if (searchFails) Assert.Contains(signal.Faults, fault => fault.Category == DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex);
            if (permissionFailure) Assert.Equal(DerivedIndexRecoveryFailureCategory.PermissionsDenied, signal.Faults[0].Category);
            Assert.All(signal.Faults, fault => Assert.Equal(generation, fault.ActiveGenerationId));
            Assert.Single(await db.IndexSegmentOwners.ToArrayAsync());
        }
        finally { cache.BeforeNativeDisposal = null; await cache.SweepAsync(true, default); }
        Assert.Empty(await db.IndexSegmentOwners.ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scoped_native_call_preserves_matches_with_one_retirement_batch_for_only_nonempty_segments(bool expanded)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "First prepared segment.", segmented: true);
        await environment.AddAndPumpAsync("Second prepared segment.");
        await environment.AddAndPumpAsync("Third prepared segment.");
        using var services = environment.Services.CreateScope();
        var generation = await environment.Store.GetActiveGenerationIdAsync(default);
        var store = services.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        var manifest = await store.ReadManifestAsync(generation!.Value, default);
        Assert.Equal(3, manifest.Segments.Count);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var ids = manifest.Segments.Select(s => s.Id).ToArray();
        var rows = await db.IndexSegmentVectors.AsNoTracking().Where(row => ids.Contains(row.SegmentId))
            .Select(row => new { row.SegmentId, row.VectorId }).ToArrayAsync();
        var keys = manifest.Segments.ToDictionary(s => s.Id,
            s => (IReadOnlySet<long>)rows.Where(r => r.SegmentId == s.Id).Select(r => r.VectorId).ToHashSet());
        keys[manifest.Segments[1].Id] = new HashSet<long>();
        var owner = new WindowsInteractiveGpuOwnerProbe();
        var owners = new RetirementCounter(new SqlIndexSegmentOwnerStore(environment.Factory, TimeProvider.System, owner));
        var options = new UsearchIndexOptions(environment.IndexRoot);
        var paths = new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot);
        await using var cache = new UsearchSegmentCache(options, paths, owners, TimeProvider.System);
        var factory = new UsearchCorpusAnnLeaseFactory(new SqlPipelineStore(environment.Factory), new UsearchGenerationValidator(), store, options, paths, cache);
        await using var ann = await factory.OpenAsync(await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)), default);
        var reader = new SqlCorpusRetrievalReader(environment.Factory);
        await ann.PrepareAsync(reader, default);
        var vector = await environment.Embeddings.CreateEmbeddingAsync("prepared semantic evidence", default);
        var limit = expanded ? 400 : 100;
        var expected = new List<AnnMatch>();
        foreach (var segment in manifest.Segments.Where(s => keys[s.Id].Count > 0))
        {
            await using var member = await cache.BorrowPreparedAsync(segment, default);
            expected.AddRange(expanded ? await member.SearchCandidatePoolAsync(vector.Values, limit, keys[segment.Id], default)
                : await member.SearchAsync(vector.Values, limit, keys[segment.Id], default));
        }
        owners.Reads.Clear();
        var selection = new PreparedKeySelection(keys);
        var scope = new ResolvedCorpusScope("all", [], null);
        var actual = expanded ? await ((IAnnCandidatePool)ann).SearchCandidatePoolAsync(vector.Values, limit, scope, selection, default)
            : await ann.SearchAsync(vector.Values, limit, scope, selection, default);
        Assert.Equal(expected.OrderBy(p => p.Distance).ThenBy(p => p.VectorId).Take(limit), actual);
        Assert.Equal(manifest.Segments.Where(s => keys[s.Id].Count > 0).Select(s => s.Id), Assert.Single(owners.Reads));
        Assert.Equal(manifest.Segments.Select(s => s.Id), selection.Reads);
        Assert.DoesNotContain(actual, p => rows.Any(row => row.SegmentId == manifest.Segments[1].Id && row.VectorId == p.VectorId));
    }

    private sealed class PreparedKeySelection(IReadOnlyDictionary<string, IReadOnlySet<long>> keys) : ICorpusAnnScopeSelection
    {
        public List<string> Reads { get; } = [];
        public ValueTask<IReadOnlySet<long>> ReadSegmentKeysAsync(ICorpusGenerationLease lease, ResolvedCorpusScope scope, IndexSegmentDescriptor segment, CancellationToken ct)
        { Reads.Add(segment.Id); return ValueTask.FromResult(keys[segment.Id]); }
    }
    private sealed class RetirementCounter(IIndexSegmentOwnerStore inner) : IIndexSegmentOwnerStore
    {
        public List<string[]> Reads { get; } = [];
        public ValueTask<Guid> RegisterAsync(IndexSegmentDescriptor segment, Guid instance, CancellationToken ct) => inner.RegisterAsync(segment, instance, ct);
        public ValueTask OpenedAsync(Guid id, CancellationToken ct) => inner.OpenedAsync(id, ct);
        public ValueTask ReleasedAfterDisposalAsync(Guid id, CancellationToken ct) => inner.ReleasedAfterDisposalAsync(id, ct);
        public ValueTask<IReadOnlySet<string>> ReadRetiringAsync(IReadOnlyList<string> ids, CancellationToken ct)
        { Reads.Add(ids.ToArray()); return inner.ReadRetiringAsync(ids, ct); }
    }
    private sealed class BatchRecoverySignal : IDerivedIndexRecoverySignal
    {
        public List<DerivedIndexRecoveryFault> Faults { get; } = [];
        public void Notify(DerivedIndexRecoveryFault fault) => Faults.Add(fault);
        public ValueTask<DerivedIndexRecoveryFault> WaitAsync(CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class BatchFailureLease(ICorpusGenerationLease inner) : ICorpusGenerationLease
    {
        private int _reads;
        public Func<CancellationToken, ValueTask<bool>>? AfterSearch { get; set; }
        public Guid LeaseId => inner.LeaseId;
        public IndexGenerationDescriptor Generation => inner.Generation;
        public ValueTask<bool> IsCurrentAsync(CancellationToken ct) => inner.IsCurrentAsync(ct);
        public ValueTask<bool> IsReadableAsync(CancellationToken ct) => AfterSearch is { } work && Interlocked.Increment(ref _reads) == 2
            ? work(ct) : inner.IsReadableAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
