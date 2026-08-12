using Cloud.Unum.USearch;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Usearch;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Collections.Immutable;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class UsearchGenerationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIndexes_{Guid.NewGuid():N}");

    [Fact]
    public async Task Candidate_is_saved_reopened_validated_and_placed_as_an_immutable_generation()
    {
        var id = Guid.NewGuid();
        var store = new MemoryStore(id);
        var builder = new UsearchGenerationBuilder(store, UsearchIndexOptions.FromConfiguredRoot(_root), new UsearchGenerationValidator());

        var snapshot = await builder.BuildAndPlaceAsync(id, CancellationToken.None);
        var generation = snapshot.Generation;

        Assert.True(File.Exists(Path.Combine(generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
        Assert.Equal(2, generation.VectorCount);
        Assert.Equal(2, snapshot.Vectors.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_identical_candidates_reuse_the_immutable_placement_winner()
    {
        var store = new MemoryStore(Guid.NewGuid());
        var builder = new UsearchGenerationBuilder(store, UsearchIndexOptions.FromConfiguredRoot(_root), new UsearchGenerationValidator());

        var candidates = await Task.WhenAll(
            builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None).AsTask(),
            builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None).AsTask());

        Assert.Equal(candidates[0].Generation.Id, candidates[1].Generation.Id);
        Assert.True(File.Exists(Path.Combine(candidates[0].Generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
    }

    [Fact]
    public async Task Validator_rejects_a_reopened_index_with_correct_keys_but_wrong_vector_payloads()
    {
        var store = new CachingStore();
        var builder = new UsearchGenerationBuilder(store, UsearchIndexOptions.FromConfiguredRoot(_root), new UsearchGenerationValidator());
        var snapshot = await builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);

        ReplaceIndex(snapshot, MetricKind.Cos, _ => Vector(1).ToArray());

        Assert.Throws<IndexGenerationValidationException>(
            () => new UsearchGenerationValidator().Validate(snapshot.Generation.IndexPath, snapshot.Generation, snapshot.Vectors));
    }

    [Fact]
    public async Task Validator_rejects_a_single_vector_index_with_a_non_cosine_metric()
    {
        var store = new CachingStore();
        var builder = new UsearchGenerationBuilder(store, UsearchIndexOptions.FromConfiguredRoot(_root), new UsearchGenerationValidator());
        var snapshot = await builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);

        ReplaceIndex(snapshot, MetricKind.L2sq, vector => ToFloatValues(vector.Values));

        Assert.Throws<IndexGenerationValidationException>(
            () => new UsearchGenerationValidator().Validate(snapshot.Generation.IndexPath, snapshot.Generation, snapshot.Vectors));
    }

    [Fact]
    public async Task Validator_rejects_a_reopened_single_vector_Pearson_index()
    {
        var store = new CachingStore();
        var builder = new UsearchGenerationBuilder(store, UsearchIndexOptions.FromConfiguredRoot(_root), new UsearchGenerationValidator());
        var snapshot = await builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);

        ReplaceIndex(snapshot, MetricKind.Pearson, vector => ToFloatValues(vector.Values));

        Assert.Throws<IndexGenerationValidationException>(
            () => new UsearchGenerationValidator().Validate(snapshot.Generation.IndexPath, snapshot.Generation, snapshot.Vectors));
    }

    [Fact]
    public async Task Validator_accepts_a_reopened_single_vector_cosine_index()
    {
        var store = new CachingStore();
        var builder = new UsearchGenerationBuilder(store, UsearchIndexOptions.FromConfiguredRoot(_root), new UsearchGenerationValidator());
        var snapshot = await builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);

        new UsearchGenerationValidator().Validate(
            snapshot.Generation.IndexPath,
            snapshot.Generation,
            snapshot.Vectors);
    }

    [Fact]
    public void Configured_root_below_an_existing_link_to_the_repository_is_rejected()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRootValidation_{Guid.NewGuid():N}");
        var repository = Path.Combine(sandbox, "repository");
        var deployment = Path.Combine(sandbox, "deployment");
        var link = Path.Combine(sandbox, "linked-repository");
        try
        {
            Directory.CreateDirectory(repository);
            Directory.CreateDirectory(deployment);
            Directory.CreateSymbolicLink(link, repository);
            Directory.CreateDirectory(Path.Combine(link, "existing-child"));

            Assert.Throws<InvalidOperationException>(() => UsearchIndexOptions.FromConfiguredRoot(
                Path.Combine(link, "existing-child", "future-root"), repository, deployment));
        }
        finally
        {
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Pointer_replacement_dispose_race_stops_the_new_reader_before_a_wrong_result()
    {
        var store = new CachingStore();
        var builder = new UsearchGenerationBuilder(store, UsearchIndexOptions.FromConfiguredRoot(_root), new UsearchGenerationValidator());
        var first = await builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        store.Record(first);
        store.UseSecondVector = true;
        var second = await builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        store.Record(second);
        store.ActiveGeneration = first.Generation.Id;
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddScoped<IIndexGenerationStore>(provider => provider.GetRequiredService<CachingStore>());
        services.AddSingleton<UsearchAnnIndex>();
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<UsearchAnnIndex>();

        var initial = await reader.SearchAsync(Vector(0), 1, CancellationToken.None);
        store.ActiveGeneration = second.Generation.Id;
        store.BlockNextGenerationRead();
        var replacement = reader.SearchAsync(Vector(1), 1, CancellationToken.None).AsTask();
        await store.GenerationReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reader.Dispose();
        store.ReleaseGenerationRead();

        Assert.Equal(11, Assert.Single(initial).VectorId);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await replacement);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await reader.SearchAsync(Vector(1), 1, CancellationToken.None));
    }

    [Fact]
    public async Task Active_reader_reuses_a_matching_generation_and_replaces_it_after_the_sql_pointer_changes()
    {
        var store = new CachingStore();
        var options = UsearchIndexOptions.FromConfiguredRoot(_root);
        var builder = new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator());
        var firstSnapshot = await builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        var first = firstSnapshot.Generation;
        store.Record(firstSnapshot);
        store.UseSecondVector = true;
        var secondSnapshot = await builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        var second = secondSnapshot.Generation;
        store.Record(secondSnapshot);
        store.ActiveGeneration = first.Id;
        store.ResetGenerationReads();
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddScoped<IIndexGenerationStore>(provider => provider.GetRequiredService<CachingStore>());
        services.AddSingleton<UsearchAnnIndex>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var reader = provider.GetRequiredService<UsearchAnnIndex>();

        var initial = await reader.SearchAsync(Vector(0), 1, CancellationToken.None);
        var repeated = await reader.SearchAsync(Vector(0), 1, CancellationToken.None);
        store.ActiveGeneration = second.Id;
        var replaced = await reader.SearchAsync(Vector(1), 1, CancellationToken.None);

        Assert.Equal(11, Assert.Single(initial).VectorId);
        Assert.Equal(11, Assert.Single(repeated).VectorId);
        Assert.Equal(22, Assert.Single(replaced).VectorId);
        Assert.Equal(3, store.GenerationReads);
    }

    [Fact]
    public async Task Active_reader_reopens_when_recovery_replaces_the_path_under_the_same_generation_id()
    {
        var store = new CachingStore();
        var options = UsearchIndexOptions.FromConfiguredRoot(_root);
        var builder = new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator());
        var snapshot = await builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        store.Record(snapshot);
        store.ActiveGeneration = snapshot.Generation.Id;
        var replacementPath = Path.Combine(_root, "generations", "same-id-recovery");
        Directory.CreateDirectory(replacementPath);
        File.Copy(Path.Combine(snapshot.Generation.IndexPath, UsearchGenerationValidator.IndexFileName),
            Path.Combine(replacementPath, UsearchGenerationValidator.IndexFileName));
        File.Copy(Path.Combine(snapshot.Generation.IndexPath, UsearchGenerationValidator.MetadataFileName),
            Path.Combine(replacementPath, UsearchGenerationValidator.MetadataFileName));
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddScoped<IIndexGenerationStore>(provider => provider.GetRequiredService<CachingStore>());
        services.AddSingleton<UsearchAnnIndex>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var reader = provider.GetRequiredService<UsearchAnnIndex>();

        Assert.Equal(11, Assert.Single(await reader.SearchAsync(Vector(0), 1, CancellationToken.None)).VectorId);
        store.ReplacePath(snapshot.Generation.Id, replacementPath);

        Assert.Equal(11, Assert.Single(await reader.SearchAsync(Vector(0), 1, CancellationToken.None)).VectorId);
        Assert.Equal(2, store.GenerationReads);
    }

    [Fact]
    public async Task Healthy_probe_that_cannot_acquire_the_recovery_lock_does_not_create_recovery_evidence()
    {
        var snapshot = await CreateRecoverySnapshotAsync();
        var store = new ProbeRecoveryStore(snapshot) { LeaseAvailable = true };
        using var provider = CreateRecoveryProvider(store);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

        await coordinator.RunOnceAsync(CancellationToken.None);
        store.LeaseAvailable = false;

        await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        Assert.Empty(store.AuditEvents);
    }

    [Fact]
    public async Task Probe_does_not_replace_a_concurrent_reader_recovery_transition_with_healthy()
    {
        var snapshot = await CreateRecoverySnapshotAsync();
        var store = new ProbeRecoveryStore(snapshot) { LeaseAvailable = true };
        using var provider = CreateRecoveryProvider(store);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        await coordinator.RunOnceAsync(CancellationToken.None);

        store.BlockNextRead();
        var probe = coordinator.RunOnceAsync(CancellationToken.None).AsTask();
        await store.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Notify(new DerivedIndexRecoveryFault(DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex,
            snapshot.Generation.Id));
        store.ReleaseRead();
        await probe;

        Assert.Equal(DerivedIndexRecoveryState.Recovering, coordinator.Snapshot.State);
        Assert.Equal(DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex, coordinator.Snapshot.FailureCategory);
    }

    [Fact]
    public async Task Reader_validation_io_failure_notifies_transient_recovery_before_rethrowing()
    {
        var store = new CachingStore();
        var builder = new UsearchGenerationBuilder(store, UsearchIndexOptions.FromConfiguredRoot(_root), new UsearchGenerationValidator());
        var snapshot = await builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        store.Record(snapshot);
        store.ActiveGeneration = snapshot.Generation.Id;
        var signal = new CapturingRecoverySignal();
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddScoped<IIndexGenerationStore>(provider => provider.GetRequiredService<CachingStore>());
        services.AddSingleton<IDerivedIndexRecoverySignal>(signal);
        services.AddSingleton<UsearchAnnIndex>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var reader = provider.GetRequiredService<UsearchAnnIndex>();
        await using var lockHandle = new FileStream(Path.Combine(snapshot.Generation.IndexPath, UsearchGenerationValidator.MetadataFileName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<IOException>(async () => await reader.SearchAsync(Vector(0), 1, CancellationToken.None));

        var fault = Assert.Single(signal.Faults);
        Assert.Equal(DerivedIndexRecoveryFailureCategory.TransientIo, fault.Category);
        Assert.Equal(snapshot.Generation.Id, fault.ActiveGenerationId);
    }

    [Fact]
    public async Task Reader_final_index_open_permission_failure_notifies_non_retryable_recovery()
    {
        var store = new CachingStore();
        var builder = new UsearchGenerationBuilder(store, UsearchIndexOptions.FromConfiguredRoot(_root), new UsearchGenerationValidator());
        var snapshot = await builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        store.Record(snapshot);
        store.ActiveGeneration = snapshot.Generation.Id;
        var signal = new CapturingRecoverySignal();
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddScoped<IIndexGenerationStore>(provider => provider.GetRequiredService<CachingStore>());
        services.AddSingleton<IDerivedIndexRecoverySignal>(signal);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var reader = new UsearchAnnIndex(
            provider.GetRequiredService<IServiceScopeFactory>(),
            _ => throw new UnauthorizedAccessException("injected native index access denial"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await reader.SearchAsync(Vector(0), 1, CancellationToken.None));

        var fault = Assert.Single(signal.Faults);
        Assert.Equal(DerivedIndexRecoveryFailureCategory.PermissionsDenied, fault.Category);
        Assert.False(DerivedIndexRecoveryPolicy.Decide(fault.Category, 1).ShouldRetry);
    }

    private async Task<IndexGenerationCandidateSnapshot> CreateRecoverySnapshotAsync()
    {
        var store = new CachingStore();
        var builder = new UsearchGenerationBuilder(store, UsearchIndexOptions.FromConfiguredRoot(_root), new UsearchGenerationValidator());
        return await builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
    }

    private ServiceProvider CreateRecoveryProvider(ProbeRecoveryStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton<IDerivedIndexRecoveryStore>(store);
        services.AddScoped<IIndexGenerationStore>(_ => new CachingStore());
        services.AddSingleton(UsearchIndexOptions.FromConfiguredRoot(_root));
        services.AddSingleton<UsearchGenerationValidator>();
        services.AddScoped<UsearchGenerationBuilder>();
        services.AddSingleton<DerivedIndexFileSystem>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DerivedIndexRecoveryCoordinator>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class MemoryStore(Guid id) : IIndexGenerationStore
    {
        private IndexGenerationDescriptor _generation = new(id, "deterministic-tokenhash-v1:256", 256, string.Empty, new string('0', 64), 2);
        private readonly IReadOnlyList<CanonicalVector> _vectors =
        [
            Vector(1, 1, 0),
            Vector(2, 2, 1)
        ];

        public ValueTask<IReadOnlyList<CanonicalTextChunk>> ReadChunksAsync(FluxKnowledge.Domain.Common.PipelineRecordId pipelineRecordId, long sourceRevision, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CanonicalTextChunk>>([]);
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadVectorsAsync(Guid indexGenerationId, CancellationToken cancellationToken) => ValueTask.FromResult(_vectors);
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadEligibleVectorsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_vectors);
        public ValueTask<IndexGenerationDescriptor?> GetGenerationAsync(Guid indexGenerationId, CancellationToken cancellationToken) => ValueTask.FromResult<IndexGenerationDescriptor?>(_generation);
        public ValueTask<Guid?> GetActiveGenerationIdAsync(CancellationToken cancellationToken) => ValueTask.FromResult<Guid?>(null);
        public ValueTask UpdateGenerationMetadataAsync(IndexGenerationDescriptor generation, CancellationToken cancellationToken) { _generation = generation; return ValueTask.CompletedTask; }

        private static CanonicalVector Vector(long id, long chunkId, int dimension)
        {
            var values = new float[256];
            values[dimension] = 1F;
            var bytes = new byte[values.Length * sizeof(float)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return new CanonicalVector(
                id,
                chunkId,
                "deterministic-tokenhash-v1:256",
                256,
                bytes,
                new string('a', 64),
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                1);
        }
    }

    private sealed class CachingStore : IIndexGenerationStore
    {
        private readonly Dictionary<Guid, IndexGenerationDescriptor> _generations = [];
        private readonly Dictionary<Guid, IReadOnlyList<CanonicalVector>> _memberships = [];

        public Guid ActiveGeneration { get; set; }
        public bool UseSecondVector { get; set; }
        public int GenerationReads { get; private set; }
        public TaskCompletionSource<bool> GenerationReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool>? _generationReadRelease;
        public void ResetGenerationReads() => GenerationReads = 0;
        public void BlockNextGenerationRead() => _generationReadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseGenerationRead() => _generationReadRelease?.TrySetResult(true);
        public void Record(IndexGenerationCandidateSnapshot snapshot)
        {
            _generations[snapshot.Generation.Id] = snapshot.Generation;
            _memberships[snapshot.Generation.Id] = snapshot.Vectors;
        }
        public void ReplacePath(Guid generationId, string indexPath) =>
            _generations[generationId] = _generations[generationId] with { IndexPath = indexPath };
        public ValueTask<IReadOnlyList<CanonicalTextChunk>> ReadChunksAsync(FluxKnowledge.Domain.Common.PipelineRecordId pipelineRecordId, long sourceRevision, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CanonicalTextChunk>>([]);
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadVectorsAsync(Guid indexGenerationId, CancellationToken cancellationToken) => ValueTask.FromResult(_memberships[indexGenerationId]);
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadEligibleVectorsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CanonicalVector>>([UseSecondVector ? Vector(22, 1) : Vector(11, 0)]);
        public async ValueTask<IndexGenerationDescriptor?> GetGenerationAsync(Guid indexGenerationId, CancellationToken cancellationToken)
        {
            GenerationReads++;
            var release = _generationReadRelease;
            if (release is not null)
            {
                GenerationReadStarted.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                _generationReadRelease = null;
            }
            return _generations[indexGenerationId];
        }
        public ValueTask<Guid?> GetActiveGenerationIdAsync(CancellationToken cancellationToken) => ValueTask.FromResult<Guid?>(ActiveGeneration);
        public ValueTask UpdateGenerationMetadataAsync(IndexGenerationDescriptor generation, CancellationToken cancellationToken) { _generations[generation.Id] = generation; _memberships[generation.Id] = UseSecondVector ? [Vector(22, 1)] : [Vector(11, 0)]; return ValueTask.CompletedTask; }
        private static CanonicalVector Vector(long id, int dimension) { var values = UsearchGenerationTests.Vector(dimension).ToArray(); var bytes = new byte[values.Length * sizeof(float)]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return new CanonicalVector(id, id, "deterministic-tokenhash-v1:256", 256, bytes, new string('a', 64), Convert.ToHexStringLower(SHA256.HashData(bytes)), 1); }
    }

    private sealed class ProbeRecoveryStore(IndexGenerationCandidateSnapshot snapshot) : IDerivedIndexRecoveryStore
    {
        private readonly DerivedIndexRecoverySqlSnapshot _snapshot = new(
            snapshot.Generation.Id,
            snapshot.Generation,
            snapshot.Vectors.ToImmutableArray(),
            ImmutableHashSet.Create(snapshot.Generation.Id),
            ImmutableHashSet.Create(snapshot.Generation.IndexPath));
        private TaskCompletionSource<bool>? _readRelease;

        public bool LeaseAvailable { get; set; }
        public List<DerivedIndexRecoveryAuditEvent> AuditEvents { get; } = [];
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BlockNextRead() => _readRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseRead() => _readRelease?.TrySetResult(true);
        public async ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(CancellationToken cancellationToken)
        {
            var release = _readRelease;
            if (release is not null)
            {
                ReadStarted.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                _readRelease = null;
            }
            return _snapshot;
        }
        public ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(TimeSpan lockTimeout, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IDerivedIndexRecoveryLease?>(LeaseAvailable ? new Lease() : null);
        public ValueTask<bool> TryUpdateRecoveryPathAsync(Guid expectedActiveGenerationId, string expectedIndexPath, string replacementIndexPath, DateTimeOffset validatedAtUtc, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
        public ValueTask AppendAuditAsync(DerivedIndexRecoveryAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            AuditEvents.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Lease : IDerivedIndexRecoveryLease
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingRecoverySignal : IDerivedIndexRecoverySignal
    {
        public List<DerivedIndexRecoveryFault> Faults { get; } = [];
        public void Notify(DerivedIndexRecoveryFault fault) => Faults.Add(fault);
        public ValueTask<DerivedIndexRecoveryFault> WaitAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static IReadOnlyList<float> Vector(int dimension)
    {
        var values = new float[256];
        values[dimension] = 1F;
        return values;
    }

    private static void ReplaceIndex(
        IndexGenerationCandidateSnapshot snapshot,
        MetricKind metric,
        Func<CanonicalVector, float[]> values)
    {
        using var index = new USearchIndex(
            metric,
            ScalarKind.Float32,
            (ulong)snapshot.Generation.Dimensions,
            0,
            0,
            0,
            false);
        foreach (var vector in snapshot.Vectors)
        {
            index.Add((ulong)vector.VectorId, values(vector));
        }
        index.Save(Path.Combine(snapshot.Generation.IndexPath, UsearchGenerationValidator.IndexFileName));
    }

    private static float[] ToFloatValues(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }
}
