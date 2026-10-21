using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class SegmentRetirementTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(false, "admitted")]
    [InlineData(true, "admitted")]
    [InlineData(false, "before-delete")]
    [InlineData(true, "before-delete")]
    public async Task Lost_original_transaction_cannot_reach_either_physical_delete(bool manifest, string window)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The first index must survive lost ownership during retirement.", segmented: true);
        var old = await environment.ActiveGenerationAsync();
        await environment.AddAndPumpAtPathAsync("The current revision remains authoritative.", "initial.txt");
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true, CancellationToken.None);
        var recovery = Path.Combine(environment.IndexRoot, "lost-retirement-session"); Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        var files = new ObservedProjectionFileStore(environment.IndexRoot);
        var operations = new SqlSegmentRetirementOperations(environment.Factory, files,
            environment.IndexRoot, recovery, new FutureClock(), new WindowsInteractiveGpuOwnerProbe());
        var marked = await operations.MarkManifestAsync(old.Id, Guid.NewGuid(), CancellationToken.None);
        Assert.NotEmpty(marked.Segments);
        if (manifest)
            foreach (var segment in marked.Segments)
                Assert.True(await operations.TryReclaimSegmentAsync(segment.Id, marked.OperationId, CancellationToken.None));
        files.Deletes = 0;
        var faulted = new SqlSegmentRetirementOperations(environment.Factory, files,
            environment.IndexRoot, recovery, new FutureClock(), new WindowsInteractiveGpuOwnerProbe())
        { FileOperationCheckpoint = async (phase, db, _) => { if (phase == window) await db.Database.GetDbConnection().CloseAsync(); } };
        await Assert.ThrowsAnyAsync<Exception>(async () => {
            if (manifest) await faulted.TryReclaimManifestAsync(old.Id, marked.OperationId, CancellationToken.None);
            else await faulted.TryReclaimSegmentAsync(marked.Segments[0].Id, marked.OperationId, CancellationToken.None);
        });
        Assert.Equal(0, files.Deletes);
        Assert.True(Directory.Exists(manifest ? old.IndexPath : marked.Segments[0].DirectoryPath));
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    private sealed class ObservedProjectionFileStore(string root) : IIndexProjectionFileStore
    {
        private readonly WindowsIndexProjectionFileStore _inner = new(root);
        internal int Deletes { get; set; }
        public long MaximumSegmentVerificationBytes(IndexSegmentDescriptor segment) => _inner.MaximumSegmentVerificationBytes(segment);
        public long MaximumManifestVerificationBytes(IndexGenerationDescriptor generation) => _inner.MaximumManifestVerificationBytes(generation);
        public ValueTask DeleteSegmentAsync(IndexSegmentDescriptor segment, CancellationToken token)
        { Deletes++; return _inner.DeleteSegmentAsync(segment, token); }
        public ValueTask DeleteManifestAsync(IndexGenerationDescriptor generation, CancellationToken token)
        { Deletes++; return _inner.DeleteManifestAsync(generation, token); }
    }

    [NativeSqlServerFact]
    public async Task Recognised_application_recovery_packets_allow_unreferenced_index_reclamation()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The initial revision can be retired after an independently backed-up application update.", segmented: true);
        var old = await environment.ActiveGenerationAsync();
        await environment.AddAndPumpAtPathAsync("The replacement remains searchable after old index reclamation.", "initial.txt");
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true, CancellationToken.None);
        var recovery = Path.Combine(environment.IndexRoot, "recognised-app-recovery");
        var packet = Path.Combine(recovery, "IncrementalUpdates", "20261020T111743Z-61b5e452b29f");
        Directory.CreateDirectory(packet);
        await File.WriteAllTextAsync(Path.Combine(packet, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(new {
            commit = new string('a', 40), staged_at_utc = DateTimeOffset.UtcNow, application_root = Path.Combine(recovery, "App"),
            migrations = false, clean_slate = false }));
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        var operations = new SqlSegmentRetirementOperations(environment.Factory, new WindowsIndexProjectionFileStore(environment.IndexRoot),
            environment.IndexRoot, recovery, new FutureClock(), new WindowsInteractiveGpuOwnerProbe());
        var result = await operations.RunOnceAsync(new IndexProjectionMaintenanceBudget(RetainedInactiveManifests: 0), CancellationToken.None);
        Assert.Null(result.ReasonCode); Assert.True(result.ReclaimedManifests > 0);
        Assert.False(Directory.Exists(old.IndexPath));
        Assert.True(File.Exists(Path.Combine(packet, "manifest.json")));
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerFact]
    public async Task Scheduled_orphan_retirement_requires_terminal_proof_and_the_thirty_minute_grace()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "A bounded candidate may finish native sealing but never publish its manifest.", publish: false, segmented: true);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var draft = await db.IndexGenerations.Where(value => value.IndexPath == string.Empty).Select(value => value.Id).SingleAsync();
        using var scope = environment.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        var candidate = await store.PrepareAsync(draft, CancellationToken.None);
        var built = await new UsearchSegmentBuilder(new(environment.IndexRoot), new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot), new(2))
            .BuildAsync(candidate.ModelFingerprint, candidate.Dimensions, store.ReadBuildVectorsAsync(candidate.Id, CancellationToken.None),
                CancellationToken.None, (segment, vectors, token) => store.SealSegmentAsync(candidate.Id, segment, vectors, token));
        var recovery = Path.Combine(environment.IndexRoot, "orphan-grace-recovery"); Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        var operations = new SqlSegmentRetirementOperations(environment.Factory, new WindowsIndexProjectionFileStore(environment.IndexRoot),
            environment.IndexRoot, recovery, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        Assert.Equal(0, (await operations.RunOnceAsync(CancellationToken.None)).ReclaimedSegments);
        await store.AbandonAsync(candidate.Id, CancellationToken.None);
        Assert.Equal(0, (await operations.RunOnceAsync(CancellationToken.None)).ReclaimedSegments);
        foreach (var segment in await db.IndexSegments.ToArrayAsync()) segment.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-31);
        await db.SaveChangesAsync();
        var reclaimed = await operations.RunOnceAsync(CancellationToken.None);
        Assert.Equal(built.Count, reclaimed.ReclaimedSegments);
        Assert.All(built, value => Assert.False(Directory.Exists(value.DirectoryPath)));
        Assert.True(await db.Vectors.AnyAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deferred_manifest_reclamation_remains_visible_until_recovery_ownership_is_available(bool sqlRetries)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The old generation has finished native segment reclamation.", segmented: true);
        var old = await environment.ActiveGenerationAsync();
        await environment.AddAndPumpAtPathAsync("This replacement remains current during manifest retirement.", "initial.txt");
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true, CancellationToken.None);
        var recovery = Path.Combine(environment.IndexRoot, "manifest-owner-recovery"); Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        IDbContextFactory<FluxKnowledgeDbContext> factory = environment.Factory;
        if (sqlRetries)
        {
            await using var binding = await environment.Factory.CreateDbContextAsync();
            factory = new Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory<FluxKnowledgeDbContext>(
                new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(binding.Database.GetConnectionString(),
                    options => options.EnableRetryOnFailure()).Options);
        }
        var operations = new SqlSegmentRetirementOperations(factory, new WindowsIndexProjectionFileStore(environment.IndexRoot),
            environment.IndexRoot, recovery, new FutureClock(), new WindowsInteractiveGpuOwnerProbe());
        var marked = await operations.MarkManifestAsync(old.Id, Guid.NewGuid(), CancellationToken.None);
        foreach (var segment in marked.Segments)
            Assert.True(await operations.TryReclaimSegmentAsync(segment.Id, marked.OperationId, CancellationToken.None));
        var recoveryStore = new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System);
        await using (var shared = await recoveryStore.TryAcquireSharedLeaseAsync(TimeSpan.Zero, CancellationToken.None))
        {
            Assert.NotNull(shared);
            var blocked = await operations.RunOnceAsync(CancellationToken.None);
            Assert.Equal("segment-retirement-manifest-pending", blocked.ReasonCode);
            Assert.True(blocked.WorkRemaining);
            Assert.True(Directory.Exists(old.IndexPath));
        }
        var completed = await operations.RunOnceAsync(CancellationToken.None);
        Assert.Null(completed.ReasonCode);
        Assert.False(completed.WorkRemaining);
        Assert.Equal(1, completed.ReclaimedManifests);
        Assert.False(Directory.Exists(old.IndexPath));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bounded_maintenance_rotates_past_pinned_manifests_and_native_owned_segments(bool nativeOwners)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The first source starts a series of historical projections.", segmented: true);
        await environment.AddAndPumpAtPathAsync("A second source remains independently replaceable.", "second.txt");
        await environment.AddAndPumpAtPathAsync("The first source is replaced by its current revision.", "initial.txt");
        await environment.AddAndPumpAtPathAsync("The second source also receives its current revision.", "second.txt");
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true, CancellationToken.None);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var state = await db.IndexState.AsNoTracking().SingleAsync();
        var inactive = await db.IndexGenerations.Where(value => value.FormatVersion == 2 && value.Id != state.ActiveIndexGenerationId)
            .OrderBy(value => value.Id).ToArrayAsync();
        var commonTime = DateTimeOffset.UtcNow.AddDays(-2);
        foreach (var row in inactive) row.CreatedAtUtc = commonTime;
        await db.SaveChangesAsync();
        var recovery = Path.Combine(environment.IndexRoot, "rotating-recovery"); Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        var probe = new WindowsInteractiveGpuOwnerProbe();
        var operations = new SqlSegmentRetirementOperations(environment.Factory, new WindowsIndexProjectionFileStore(environment.IndexRoot),
            environment.IndexRoot, recovery, new FutureClock(), probe);
        string? blockedSegment = null; string? eligibleSegment = null;
        if (nativeOwners)
        {
            foreach (var row in inactive) await operations.MarkManifestAsync(row.Id, Guid.NewGuid(), CancellationToken.None);
            var pending = await db.IndexSegments.Where(value => value.State == 2).OrderBy(value => value.Id).ToArrayAsync();
            Assert.True(pending.Length >= 2);
            foreach (var row in pending) row.CreatedAtUtc = commonTime;
            blockedSegment = pending[0].Id; eligibleSegment = pending[^1].Id;
            var owner = probe.Current;
            db.IndexSegmentOwners.Add(new() { Id = Guid.NewGuid(), SegmentId = blockedSegment, OwnerInstanceId = Guid.NewGuid(),
                OwnerProcessId = owner.ProcessId, OwnerStartedAtUtc = owner.StartedAtUtc, OwnerMachineFingerprint = owner.MachineFingerprint,
                CreatedAtUtc = commonTime });
        }
        else
        {
            foreach (var row in inactive.Take(2)) db.IndexPublicationCandidates.Add(new()
            {
                Id = Guid.NewGuid(), CorpusEpoch = state.CorpusEpoch, ExpectedCorpusVersion = state.CorpusVersion,
                PublicationCorpusVersion = state.CorpusVersion, BaseGenerationId = row.Id, State = 0,
                BeforeChecksum = new('0', 64), AfterChecksum = new('0', 64), CreatedAtUtc = commonTime,
                OwnerInstanceId = Guid.NewGuid(), OwnerProcessId = probe.Current.ProcessId, OwnerStartedAtUtc = probe.Current.StartedAtUtc,
                OwnerMachineFingerprint = probe.Current.MachineFingerprint
            });
        }
        await db.SaveChangesAsync();
        var budget = new IndexProjectionMaintenanceBudget(MaximumSegments: 1, RetainedInactiveManifests: 0);
        IndexProjectionMaintenanceResult? last = null;
        for (var attempt = 0; attempt < 8; attempt++) last = await operations.RunOnceAsync(budget, CancellationToken.None);
        Assert.True(last!.WorkRemaining); // A clean rotated page cannot claim the blocked prefix drained.
        if (nativeOwners)
        {
            Assert.Equal(2, (await db.IndexSegments.AsNoTracking().SingleAsync(value => value.Id == blockedSegment)).State);
            Assert.Equal(3, (await db.IndexSegments.AsNoTracking().SingleAsync(value => value.Id == eligibleSegment)).State);
            Assert.True(await db.IndexSegmentOwners.AnyAsync(value => value.SegmentId == blockedSegment));
            Assert.True(Directory.Exists(Path.Combine(environment.IndexRoot, "segments", blockedSegment!)));
        }
        else
        {
            Assert.All(inactive.Take(2), value => Assert.True(Directory.Exists(value.IndexPath)));
            var eligibleManifest = inactive[^1].Id;
            Assert.NotNull((await db.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == eligibleManifest)).ReclaimedAtUtc);
        }
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerFact]
    public async Task Failed_physical_attempts_consume_the_maintenance_byte_budget()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The old source has multiple passages spread across more than one bounded immutable native segment.", segmented: true);
        await environment.AddAndPumpAtPathAsync("The second source has independently replaceable passages.", "second.txt");
        await environment.AddAndPumpAtPathAsync("The replacement has its own different vector payload.", "initial.txt");
        await environment.AddAndPumpAtPathAsync("The second replacement also creates a separate native payload.", "second.txt");
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true, CancellationToken.None);
        var recovery = Path.Combine(environment.IndexRoot, "failed-retirement-recovery"); Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        var failing = new FailedProjectionFileStore();
        var operations = new SqlSegmentRetirementOperations(environment.Factory, failing,
            environment.IndexRoot, recovery, new FutureClock(), new WindowsInteractiveGpuOwnerProbe());
        await using var db = await environment.Factory.CreateDbContextAsync();
        var active = (await db.IndexState.SingleAsync()).ActiveIndexGenerationId;
        var obsolete = await db.IndexGenerations.Where(value => value.FormatVersion == 2 && value.Id != active)
            .OrderBy(value => value.CreatedAtUtc).Select(value => value.Id).ToArrayAsync();
        var marked = new List<IndexSegmentDescriptor>();
        foreach (var id in obsolete) marked.AddRange((await operations.MarkManifestAsync(id, Guid.NewGuid(), CancellationToken.None)).Segments);
        Assert.True(marked.Count >= 2);
        var budget = new IndexProjectionMaintenanceBudget(MaximumBytes: marked.Max(value =>
            2 * (value.FileLength + new FileInfo(Path.Combine(value.DirectoryPath, "metadata.json")).Length)), RetainedInactiveManifests: 0);
        var result = await operations.RunOnceAsync(budget, CancellationToken.None);
        Assert.Equal(0, result.ReclaimedSegments);
        Assert.True(failing.AttemptedBytes > 0);
        Assert.True(failing.AttemptedBytes <= budget.MaximumBytes);
        Assert.All(marked, value => Assert.True(Directory.Exists(value.DirectoryPath)));
    }

    private sealed class FailedProjectionFileStore : IIndexProjectionFileStore
    {
        public long AttemptedBytes { get; private set; }
        public long MaximumSegmentVerificationBytes(IndexSegmentDescriptor segment) =>
            2 * (segment.FileLength + new FileInfo(Path.Combine(segment.DirectoryPath, "metadata.json")).Length);
        public long MaximumManifestVerificationBytes(IndexGenerationDescriptor generation) => 2 * 16 * 1024 * 1024;
        public ValueTask DeleteSegmentAsync(IndexSegmentDescriptor segment, CancellationToken cancellationToken)
        {
            AttemptedBytes += 2 * (segment.FileLength + new FileInfo(Path.Combine(segment.DirectoryPath, "metadata.json")).Length);
            throw new InvalidOperationException("segment-retirement-synthetic-io-failure");
        }
        public ValueTask DeleteManifestAsync(IndexGenerationDescriptor generation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("segment-retirement-synthetic-io-failure");
    }

    [NativeSqlServerFact]
    public async Task Maintenance_observes_history_age_byte_limits_and_unresolved_recovery_references()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The obsolete source remains protected until the configured history age.", segmented: true);
        var old = await environment.ActiveGenerationAsync();
        await environment.AddAndPumpAtPathAsync("This is the source's current replacement.", "initial.txt");
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true, CancellationToken.None);
        var recovery = Path.Combine(environment.IndexRoot, "bounded-retirement-recovery"); Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        SqlSegmentRetirementOperations Operations(TimeProvider time) => new(environment.Factory, new WindowsIndexProjectionFileStore(environment.IndexRoot),
            environment.IndexRoot, recovery, time, new WindowsInteractiveGpuOwnerProbe());
        var budget = new IndexProjectionMaintenanceBudget();
        Assert.Equal(new IndexProjectionMaintenanceResult(0, 0, 0), await Operations(TimeProvider.System).RunOnceAsync(budget, CancellationToken.None));
        var operations = Operations(new FutureClock());
        var small = await operations.RunOnceAsync(budget with { MaximumBytes = 1 }, CancellationToken.None);
        Assert.Equal("segment-retirement-byte-budget", small.ReasonCode);
        Assert.Equal(0, small.ReclaimedBytes);
        Assert.True(Directory.Exists(old.IndexPath));
        var unknown = Path.Combine(recovery, "unknown-reference.json");
        await File.WriteAllTextAsync(unknown, "{}");
        await Assert.ThrowsAsync<RecoveryIndexAuditException>(async () => await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery));
        var blocked = await operations.RunOnceAsync(budget, CancellationToken.None);
        Assert.Equal("segment-retirement-recovery-reference-audit-required", blocked.ReasonCode);
        Assert.Equal(0, blocked.ReclaimedSegments);
        Assert.True(Directory.Exists(old.IndexPath));
        File.Delete(unknown);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        var recovered = await operations.RunOnceAsync(budget, CancellationToken.None);
        Assert.Null(recovered.ReasonCode);
        Assert.True(recovered.ReclaimedSegments > 0);
        Assert.False(Directory.Exists(old.IndexPath));
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerFact]
    public async Task Repeated_bounded_maintenance_removes_obsolete_payloads_and_preserves_current_and_recent_history()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The first revision is replaced during bounded retention.", segmented: true);
        await environment.AddAndPumpAtPathAsync("A separate source remains current across every replacement.", "control.txt");
        for (var revision = 1; revision <= 5; revision++)
            await environment.AddAndPumpAtPathAsync($"Revision {revision} changes the first source's eligible passages.", "initial.txt");
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true, CancellationToken.None);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var active = (await db.IndexState.SingleAsync()).ActiveIndexGenerationId;
        var keep = await db.IndexGenerations.Where(value => value.FormatVersion == 2 && value.Id != active)
            .OrderByDescending(value => value.CreatedAtUtc).ThenByDescending(value => value.Id).Take(2).Select(value => value.Id).ToArrayAsync();
        var currentSegments = await db.CurrentIndexMembers.Select(value => value.SegmentId).Distinct().ToArrayAsync();
        var memberCount = await db.IndexSegmentVectors.Where(value => currentSegments.Contains(value.SegmentId)).CountAsync();
        var recovery = Path.Combine(environment.IndexRoot, "maintenance-recovery"); Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        var operations = new SqlSegmentRetirementOperations(environment.Factory, new WindowsIndexProjectionFileStore(environment.IndexRoot),
            environment.IndexRoot, recovery, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        var budget = new IndexProjectionMaintenanceBudget(MaximumSegments: 1);
        var removedSegments = 0;
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var result = await operations.RunOnceAsync(budget, CancellationToken.None);
            Assert.InRange(result.ReclaimedSegments, 0, 1);
            Assert.InRange(result.ReclaimedBytes, 0, budget.MaximumBytes);
            Assert.InRange(result.ReclaimedManifests, 0, 2);
            removedSegments += result.ReclaimedSegments;
        }
        Assert.True(removedSegments > 0);
        var remaining = await db.IndexGenerations.AsNoTracking().Where(value => value.FormatVersion == 2 && value.ReclaimedAtUtc == null).ToArrayAsync();
        Assert.Equal(3, remaining.Length);
        Assert.All(remaining, value => Assert.True(value.Id == active || keep.Contains(value.Id)));
        Assert.All(remaining, value => Assert.True(File.Exists(Path.Combine(value.IndexPath, "manifest.json"))));
        Assert.All(await db.IndexSegments.AsNoTracking().Where(value => value.State == 3).ToArrayAsync(), value => Assert.False(Directory.Exists(value.DirectoryPath)));
        Assert.Equal(memberCount, await db.IndexSegmentVectors.Where(value => currentSegments.Contains(value.SegmentId)).CountAsync());
        Assert.Equal(active, (await db.IndexState.AsNoTracking().SingleAsync()).ActiveIndexGenerationId);
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
        var expired = new SqlSegmentRetirementOperations(environment.Factory, new WindowsIndexProjectionFileStore(environment.IndexRoot),
            environment.IndexRoot, recovery, new FutureClock(), new WindowsInteractiveGpuOwnerProbe());
        for (var iteration = 0; iteration < 20; iteration++) await expired.RunOnceAsync(budget, CancellationToken.None);
        Assert.Equal(1, await db.IndexGenerations.CountAsync(value => value.FormatVersion == 2 && value.ReclaimedAtUtc == null));
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    private sealed class FutureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(2);
    }

    [NativeSqlServerFact]
    public async Task A_sealed_segment_without_a_manifest_requires_terminal_producer_proof_before_reclamation()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "A publication can conflict after sealing native segments but before sealing its manifest.", publish: false, segmented: true);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var draft = await context.IndexGenerations.Where(value => value.IndexPath == string.Empty).Select(value => value.Id).SingleAsync();
        using var scope = environment.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        var candidate = await store.PrepareAsync(draft, CancellationToken.None);
        var segments = await new UsearchSegmentBuilder(new(environment.IndexRoot), new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot), new(2))
            .BuildAsync(candidate.ModelFingerprint, candidate.Dimensions, store.ReadBuildVectorsAsync(candidate.Id, CancellationToken.None),
                CancellationToken.None, (segment, vectors, token) => store.SealSegmentAsync(candidate.Id, segment, vectors, token));
        var recovery = Path.Combine(environment.IndexRoot, "test-recovery"); Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        var retirement = new SqlSegmentRetirementOperations(environment.Factory, new WindowsIndexProjectionFileStore(environment.IndexRoot),
            environment.IndexRoot, recovery, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        var operation = Guid.NewGuid();
        var target = segments[0];
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await retirement.MarkUnreferencedSegmentAsync(target.Id, operation, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(target.DirectoryPath, "index.usearch")));
        await store.AbandonAsync(candidate.Id, CancellationToken.None);
        foreach (var segment in segments)
        {
            await retirement.MarkUnreferencedSegmentAsync(segment.Id, operation, CancellationToken.None);
            await retirement.MarkUnreferencedSegmentAsync(segment.Id, operation, CancellationToken.None);
            Assert.True(await retirement.TryReclaimSegmentAsync(segment.Id, operation, CancellationToken.None));
            Assert.False(Directory.Exists(segment.DirectoryPath));
        }
        Assert.True(await context.Vectors.AnyAsync());
        Assert.False(await context.IndexSegmentVectors.AnyAsync());
    }

    [NativeSqlServerFact]
    public async Task Retired_payloads_wait_for_query_and_native_owners_then_reclaim_through_repeated_phases()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The old source will be replaced while a reader retains its native owner.", segmented: true);
        var previous = await environment.ActiveGenerationAsync();
        var probe = new WindowsInteractiveGpuOwnerProbe();
        var captured = await new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System).TryAcquireAsync(Guid.NewGuid(), probe.Current,
            previous.ModelFingerprint, previous.Dimensions, CancellationToken.None);
        Assert.NotNull(captured);
        using var scope = environment.Services.CreateScope();
        var native = await scope.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(captured, CancellationToken.None);
        var query = new float[previous.Dimensions]; query[0] = 1;
        Assert.NotEmpty(await native.SearchAsync(query, 100, CancellationToken.None));
        await environment.AddAndPumpAtPathAsync("The current replacement must survive retirement of the obsolete source projection.", "initial.txt");
        var current = await environment.ActiveGenerationAsync();
        var recoveryRoot = Path.Combine(environment.IndexRoot, "test-recovery");
        Directory.CreateDirectory(recoveryRoot);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recoveryRoot);
        var operations = new SqlSegmentRetirementOperations(environment.Factory, new WindowsIndexProjectionFileStore(environment.IndexRoot),
            environment.IndexRoot, recoveryRoot, TimeProvider.System, probe);
        var operationId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await operations.MarkManifestAsync(previous.Id, operationId, CancellationToken.None));
        await native.DisposeAsync();
        var plan = await operations.MarkManifestAsync(previous.Id, operationId, CancellationToken.None);
        Assert.NotEmpty(plan.Segments);
        var target = plan.Segments[0];
        Assert.False(await operations.TryReclaimSegmentAsync(target.Id, operationId, CancellationToken.None));
        // This needs SQL access to acknowledge native disposal, proving retirement released both fences while owners drain.
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(false, CancellationToken.None);
        foreach (var segment in plan.Segments)
        {
            Assert.True(await operations.TryReclaimSegmentAsync(segment.Id, operationId, CancellationToken.None));
            Assert.True(await operations.TryReclaimSegmentAsync(segment.Id, operationId, CancellationToken.None));
            Assert.False(Directory.Exists(segment.DirectoryPath));
        }
        Assert.True(await operations.TryReclaimManifestAsync(previous.Id, operationId, CancellationToken.None));
        Assert.False(Directory.Exists(previous.IndexPath));
        Assert.True(File.Exists(Path.Combine(current.IndexPath, "manifest.json")));
        Assert.Equal(current.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.All(await context.IndexSegments.Where(value => value.RetirementOperationId == operationId).ToArrayAsync(), value => Assert.Equal(3, value.State));
        Assert.True(await context.Vectors.AnyAsync());
    }
}
