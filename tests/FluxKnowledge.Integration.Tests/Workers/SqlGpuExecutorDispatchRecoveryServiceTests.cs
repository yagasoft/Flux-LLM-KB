using System.Data.Common;
using System.Threading.Channels;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Gpu;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Workers;

public sealed class SqlGpuExecutorDispatchRecoveryServiceTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private const string PendingExecutorKey = "test-executor";

    [NativeSqlServerFact]
    public async Task Recreated_hosted_service_redelivers_only_the_persisted_pending_handle_without_durable_mutation()
    {
        var scenario = await CreatePendingDispatchAsync(fixture);
        var before = await ReadRestartSnapshotAsync(scenario.Factory);
        var unmatchedRead = new DispatchReadObserver();
        var firstClock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-05T12:00:00+00:00"));
        var firstAdapter = new RecordingAdapter(scenario.Handle.ExecutorKey);

        await using (var firstProvider = CreateSqlProvider(scenario.ConnectionString, firstClock, firstAdapter, unmatchedRead))
        {
            AssertSqlDispatchStore(firstProvider);
            var firstService = ResolveRecoveryService(firstProvider);
            await firstService.StartAsync(CancellationToken.None);
            Assert.Equal(scenario.Handle, await firstAdapter.ReadDeliveryAsync());
            await AwaitCompletedRecoveryPassAsync(unmatchedRead, firstClock, 1);
            Assert.False(firstAdapter.Deliveries.Reader.TryRead(out _));
            await firstService.StopAsync(CancellationToken.None);
        }

        AssertRestartSnapshotEqual(before, await ReadRestartSnapshotAsync(scenario.Factory));

        var secondClock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-05T12:00:00+00:00"));
        var matchingRead = new DispatchReadObserver();
        var matchingAdapter = new RecordingAdapter(scenario.Handle.ExecutorKey);
        await using var secondProvider = CreateSqlProvider(scenario.ConnectionString, secondClock, matchingAdapter, matchingRead);
        AssertSqlDispatchStore(secondProvider);
        var secondService = ResolveRecoveryService(secondProvider);
        await secondService.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(scenario.Handle, await matchingAdapter.ReadDeliveryAsync());
            await AwaitCompletedRecoveryPassAsync(matchingRead, secondClock, 1);
            Assert.False(matchingAdapter.Deliveries.Reader.TryRead(out _));
        }
        finally
        {
            await secondService.StopAsync(CancellationToken.None);
        }

        AssertRestartSnapshotEqual(before, await ReadRestartSnapshotAsync(scenario.Factory));
    }

    [NativeSqlServerFact]
    public async Task Recreated_hosted_service_preserves_receipt_recorded_and_delivery_uncertain_dispatches_without_redelivery()
    {
        var scenario = await CreateNonPendingDispatchesAsync(fixture);
        await AssertNonPendingScenarioAsync(scenario);
        var before = await ReadRestartSnapshotAsync(scenario.Factory);
        var unmatchedRead = new DispatchReadObserver();
        var firstClock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-05T12:00:00+00:00"));
        var firstAdapter = new RecordingAdapter(PendingExecutorKey);

        await using (var firstProvider = CreateSqlProvider(
                         scenario.ConnectionString,
                         firstClock,
                         firstAdapter,
                         unmatchedRead))
        {
            AssertSqlDispatchStore(firstProvider);
            var firstService = ResolveRecoveryService(firstProvider);
            await firstService.StartAsync(CancellationToken.None);
            await AwaitCompletedRecoveryPassAsync(unmatchedRead, firstClock, 1);
            Assert.False(firstAdapter.Deliveries.Reader.TryRead(out _));
            await firstService.StopAsync(CancellationToken.None);
        }

        AssertRestartSnapshotEqual(before, await ReadRestartSnapshotAsync(scenario.Factory));

        var matchingRead = new DispatchReadObserver();
        var matchingAdapter = new RecordingAdapter(PendingExecutorKey);
        var secondClock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-05T12:00:00+00:00"));
        await using var secondProvider = CreateSqlProvider(
            scenario.ConnectionString,
            secondClock,
            matchingAdapter,
            matchingRead);
        AssertSqlDispatchStore(secondProvider);
        var secondService = ResolveRecoveryService(secondProvider);
        await secondService.StartAsync(CancellationToken.None);
        try
        {
            await AwaitCompletedRecoveryPassAsync(matchingRead, secondClock, 1);
            Assert.False(matchingAdapter.Deliveries.Reader.TryRead(out _));
        }
        finally
        {
            await secondService.StopAsync(CancellationToken.None);
        }

        AssertRestartSnapshotEqual(before, await ReadRestartSnapshotAsync(scenario.Factory));
    }

    private static async Task AwaitCompletedRecoveryPassAsync(
        DispatchReadObserver readObserver,
        ManualTimeProvider clock,
        int readCount)
    {
        await readObserver.ReadsReached(readCount).WaitAsync(TimeSpan.FromSeconds(2));
        await clock.ScheduledTimers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task AssertNonPendingScenarioAsync(NonPendingDispatchScenario scenario)
    {
        await using var context = await scenario.Factory.CreateDbContextAsync();
        var dispatches = await context.GpuExecutorDispatches.AsNoTracking().ToListAsync();
        Assert.Equal(2, dispatches.Count);
        Assert.Equal(
            (int)GpuExecutorDispatchState.ReceiptRecorded,
            Assert.Single(dispatches, dispatch => dispatch.DispatchId == scenario.ReceiptHandle.DispatchId).State);
        Assert.Equal(
            (int)GpuExecutorDispatchState.DeliveryUncertain,
            Assert.Single(dispatches, dispatch => dispatch.DispatchId == scenario.UncertainHandle.DispatchId).State);

        var receipt = Assert.Single(await context.GpuExecutorResultReceipts.AsNoTracking().ToListAsync());
        Assert.Equal(Guid.Parse("70000000-0000-0000-0000-000000000001"), receipt.OperationId);
        Assert.Equal(scenario.ReceiptHandle.DispatchId, receipt.DispatchId);
        Assert.Equal(Convert.ToHexString(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()), Hex(receipt.OpaqueResultDigest));

        var evidence = await context.GpuExecutorEvidence.AsNoTracking().OrderBy(value => value.OperationId).ToListAsync();
        Assert.Equal(2, evidence.Count);
        Assert.Contains(evidence, value =>
            value.OperationId == Guid.Parse("70000000-0000-0000-0000-000000000002") &&
            value.DispatchId == scenario.ReceiptHandle.DispatchId);
        Assert.Contains(evidence, value =>
            value.OperationId == Guid.Parse("70000000-0000-0000-0000-000000000004") &&
            value.DispatchId == scenario.UncertainHandle.DispatchId);
        Assert.Equal(7, await context.GpuSchedulerOperationReceipts.CountAsync());
    }

    private static ServiceProvider CreateSqlProvider(
        string connectionString,
        TimeProvider timeProvider,
        IGpuExecutorAdapter adapter,
        DispatchReadObserver readObserver)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<FluxKnowledgeDbContext>(options => options
            .UseSqlServer(connectionString)
            .AddInterceptors(readObserver));
        services.AddSingleton(timeProvider);
        services.AddSingleton(new GpuSchedulerOptions(
            1,
            1,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(1)));
        services.AddSingleton<IGpuExecutorAdapter>(adapter);
        services.AddFluxKnowledgeGpuScheduler();
        return services.BuildServiceProvider();
    }

    private static void AssertSqlDispatchStore(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        Assert.IsType<SqlGpuSchedulerStore>(scope.ServiceProvider.GetRequiredService<IGpuExecutorDispatchStore>());
    }

    private static GpuExecutorDispatchRecoveryService ResolveRecoveryService(ServiceProvider provider) =>
        Assert.IsType<GpuExecutorDispatchRecoveryService>(Assert.Single(
            provider.GetServices<IHostedService>(),
            service => service is GpuExecutorDispatchRecoveryService));

    private static async Task<PendingDispatchScenario> CreatePendingDispatchAsync(NativeSqlServerFixture fixture)
    {
        var admission = new SqlGpuAdmissionTests(fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"), SingleTaskOptions);

        return new PendingDispatchScenario(fixture.ConnectionString, factory, taskId, await ReadHandleAsync(factory, "slot-a"));
    }

    private static async Task<NonPendingDispatchScenario> CreateNonPendingDispatchesAsync(NativeSqlServerFixture fixture)
    {
        var admission = new SqlGpuAdmissionTests(fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await AddSlotAsync(factory, "slot-b");

        var receiptTaskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime-a", "settings-a", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"), SingleTaskOptions);
        var receiptHandle = await ReadHandleAsync(factory, "slot-a");

        var uncertainTaskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime-b", "settings-b", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-b"), SingleTaskOptions);
        var uncertainHandle = await ReadHandleAsync(factory, "slot-b");

        var store = new SqlGpuSchedulerStore(factory);
        Assert.True((await store.AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), receiptHandle), CancellationToken.None)).Accepted);
        Assert.True((await store.RecordReceiptAsync(
            new GpuExecutorResultReceipt(
                Guid.Parse("70000000-0000-0000-0000-000000000001"),
                receiptHandle,
                receiptTaskId,
                GpuMiniTaskBoundaryDisposition.Completed,
                Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
                GpuExecutorEvidenceClass.TaskOutcomeConfirmed),
            CancellationToken.None)).Accepted);
        Assert.True((await store.RecordTrustedEvidenceAsync(
            new GpuExecutorTrustedEvidence(
                Guid.Parse("70000000-0000-0000-0000-000000000002"),
                receiptHandle,
                "receipt-verifier",
                DateTimeOffset.Parse("2026-08-05T12:01:00+00:00"),
                GpuExecutorEvidenceClass.TaskOutcomeConfirmed),
            CancellationToken.None)).Accepted);
        Assert.True((await store.MarkDeliveryUncertainAsync(
            new GpuExecutorDeliveryUncertainty(Guid.Parse("70000000-0000-0000-0000-000000000003"), uncertainHandle),
            CancellationToken.None)).Accepted);
        Assert.True((await store.RecordTrustedEvidenceAsync(
            new GpuExecutorTrustedEvidence(
                Guid.Parse("70000000-0000-0000-0000-000000000004"),
                uncertainHandle,
                "uncertain-verifier",
                DateTimeOffset.Parse("2026-08-05T12:02:00+00:00"),
                GpuExecutorEvidenceClass.CapacityReleaseConfirmed),
            CancellationToken.None)).Accepted);

        return new NonPendingDispatchScenario(fixture.ConnectionString, factory, receiptTaskId, uncertainTaskId, receiptHandle, uncertainHandle);
    }

    private static async Task AddSlotAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, string slotKey)
    {
        await using var context = await factory.CreateDbContextAsync();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
        {
            SlotKey = slotKey,
            State = (int)GpuCapacitySlotState.Available,
            UpdatedAtUtc = DateTimeOffset.Parse("2026-08-05T12:00:00+00:00")
        });
        await context.SaveChangesAsync();
    }

    private static async Task<GpuExecutorBatchHandle> ReadHandleAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        string slotKey)
    {
        await using var context = await factory.CreateDbContextAsync();
        var dispatch = await context.GpuExecutorDispatches.SingleAsync(candidate => candidate.CapacitySlotKey == slotKey);
        return new GpuExecutorBatchHandle(
            dispatch.BatchId,
            dispatch.CapacitySlotKey,
            dispatch.ExecutorKey,
            dispatch.AdmissionGeneration,
            dispatch.DispatchId);
    }

    private static async Task<RestartSnapshot> ReadRestartSnapshotAsync(IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        await using var context = await factory.CreateDbContextAsync();
        var batches = await context.GpuBatches.AsNoTracking().OrderBy(value => value.Id).ToListAsync();
        var slots = await context.GpuCapacitySlots.AsNoTracking().OrderBy(value => value.SlotKey).ToListAsync();
        var tasks = await context.GpuMiniTasks.AsNoTracking().OrderBy(value => value.Id).ToListAsync();
        var dispatches = await context.GpuExecutorDispatches.AsNoTracking().OrderBy(value => value.DispatchId).ToListAsync();
        var operations = await context.GpuSchedulerOperationReceipts.AsNoTracking().OrderBy(value => value.OperationId).ToListAsync();
        var resultReceipts = await context.GpuExecutorResultReceipts.AsNoTracking().OrderBy(value => value.OperationId).ToListAsync();
        var evidence = await context.GpuExecutorEvidence.AsNoTracking().OrderBy(value => value.OperationId).ToListAsync();

        return new RestartSnapshot(
            batches.Select(value => new BatchSnapshot(value.Id, value.CapacitySlotKey, value.PriorityLane, value.ModelRuntimeKey,
                value.SettingsFingerprint, value.ItemCount, value.EstimatedBytes, value.AdmissionGeneration, value.OwnerKey,
                value.State, value.LastHeartbeatAtUtc, value.CreatedAtUtc, value.UpdatedAtUtc, Hex(value.RowVersion))).ToArray(),
            slots.Select(value => new SlotSnapshot(value.SlotKey, value.State, value.ActiveBatchId, value.OwnerKey,
                value.LastHeartbeatAtUtc, value.UpdatedAtUtc, Hex(value.RowVersion))).ToArray(),
            tasks.Select(value => new TaskSnapshot(value.Id, value.ParentJobId, value.SourceRevision, value.PriorityLane,
                value.ModelRuntimeKey, value.SettingsFingerprint, value.EstimatedBytes, value.AdmissionGeneration,
                value.IdempotencyKey, value.HandoffLeaseOwner, value.ExecutionState, value.CreatedSequence, value.DeferredUntilUtc,
                value.BatchId, value.ReservationAttemptCount, value.CreatedAtUtc, Hex(value.RowVersion))).ToArray(),
            dispatches.Select(value => new DispatchSnapshot(value.DispatchId, value.BatchId, value.CapacitySlotKey, value.OwnerKey,
                value.ExecutorKey, value.AdmissionGeneration, value.State, value.AcknowledgedAtUtc, value.UpdatedAtUtc,
                Hex(value.RowVersion))).ToArray(),
            operations.Select(value => new OperationReceiptSnapshot(value.OperationId, value.OperationKind, value.RequestFingerprint,
                value.BatchId, value.CapacitySlotKey, value.OwnerKey, value.AdmissionGeneration, value.Accepted, value.Committed,
                value.WakeReasons, value.AdmissionDisposition, value.DeferredUntilUtc, value.WakeGeneration, value.NextDeferredAtUtc,
                value.WakeConsumptionOperationId, value.EffectiveAdmissionReasons, value.CreatedAtUtc)).ToArray(),
            resultReceipts.Select(value => new ResultReceiptSnapshot(value.OperationId, value.DispatchId, value.BatchId,
                value.MiniTaskId, value.ExecutorKey, value.AdmissionGeneration, value.Disposition, value.EvidenceClass,
                Hex(value.OpaqueResultDigest), value.RequestFingerprint, value.CreatedAtUtc)).ToArray(),
            evidence.Select(value => new EvidenceSnapshot(value.OperationId, value.DispatchId, value.BatchId, value.CapacitySlotKey,
                value.ExecutorKey, value.AdmissionGeneration, value.EvidenceClass, value.VerifierKey, value.ObservedAtUtc,
                value.RequestFingerprint, value.CreatedAtUtc)).ToArray());
    }

    private static void AssertRestartSnapshotEqual(RestartSnapshot expected, RestartSnapshot actual)
    {
        Assert.Equal(expected.Batches, actual.Batches);
        Assert.Equal(expected.Slots, actual.Slots);
        Assert.Equal(expected.Tasks, actual.Tasks);
        Assert.Equal(expected.Dispatches, actual.Dispatches);
        Assert.Equal(expected.OperationReceipts, actual.OperationReceipts);
        Assert.Equal(expected.ResultReceipts, actual.ResultReceipts);
        Assert.Equal(expected.Evidence, actual.Evidence);
    }

    private static string? Hex(byte[]? value) => value is null ? null : Convert.ToHexString(value);

    private static readonly GpuSchedulerOptions SingleTaskOptions = new(
        1,
        100,
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromHours(1));

    private sealed record PendingDispatchScenario(
        string ConnectionString,
        IDbContextFactory<FluxKnowledgeDbContext> Factory,
        Guid TaskId,
        GpuExecutorBatchHandle Handle);

    private sealed record NonPendingDispatchScenario(
        string ConnectionString,
        IDbContextFactory<FluxKnowledgeDbContext> Factory,
        Guid ReceiptTaskId,
        Guid UncertainTaskId,
        GpuExecutorBatchHandle ReceiptHandle,
        GpuExecutorBatchHandle UncertainHandle);

    private sealed record RestartSnapshot(
        IReadOnlyList<BatchSnapshot> Batches,
        IReadOnlyList<SlotSnapshot> Slots,
        IReadOnlyList<TaskSnapshot> Tasks,
        IReadOnlyList<DispatchSnapshot> Dispatches,
        IReadOnlyList<OperationReceiptSnapshot> OperationReceipts,
        IReadOnlyList<ResultReceiptSnapshot> ResultReceipts,
        IReadOnlyList<EvidenceSnapshot> Evidence);

    private sealed record BatchSnapshot(Guid Id, string SlotKey, int PriorityLane, string RuntimeKey, string SettingsFingerprint,
        int ItemCount, long EstimatedBytes, long AdmissionGeneration, string OwnerKey, int State, DateTimeOffset? LastHeartbeatAtUtc,
        DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string? RowVersion);

    private sealed record SlotSnapshot(string SlotKey, int State, Guid? ActiveBatchId, string? OwnerKey,
        DateTimeOffset? LastHeartbeatAtUtc, DateTimeOffset UpdatedAtUtc, string? RowVersion);

    private sealed record TaskSnapshot(Guid Id, Guid ParentJobId, long SourceRevision, int PriorityLane, string RuntimeKey,
        string SettingsFingerprint, long EstimatedBytes, long AdmissionGeneration, string IdempotencyKey, string? HandoffLeaseOwner,
        int State, long CreatedSequence, DateTimeOffset? DeferredUntilUtc, Guid? BatchId, int ReservationAttemptCount,
        DateTimeOffset CreatedAtUtc, string? RowVersion);

    private sealed record DispatchSnapshot(Guid DispatchId, Guid BatchId, string SlotKey, string OwnerKey, string ExecutorKey,
        long AdmissionGeneration, int State, DateTimeOffset? AcknowledgedAtUtc, DateTimeOffset UpdatedAtUtc, string? RowVersion);

    private sealed record OperationReceiptSnapshot(Guid OperationId, string OperationKind, string? RequestFingerprint, Guid? BatchId,
        string? SlotKey, string? OwnerKey, long? AdmissionGeneration, bool Accepted, bool Committed, int WakeReasons,
        int? AdmissionDisposition, DateTimeOffset? DeferredUntilUtc, long? WakeGeneration, DateTimeOffset? NextDeferredAtUtc,
        Guid? WakeConsumptionOperationId, int? EffectiveAdmissionReasons, DateTimeOffset CreatedAtUtc);

    private sealed record ResultReceiptSnapshot(Guid OperationId, Guid DispatchId, Guid BatchId, Guid TaskId, string ExecutorKey,
        long AdmissionGeneration, int Disposition, int EvidenceClass, string? OpaqueResultDigest, string RequestFingerprint,
        DateTimeOffset CreatedAtUtc);

    private sealed record EvidenceSnapshot(Guid OperationId, Guid DispatchId, Guid BatchId, string SlotKey, string ExecutorKey,
        long AdmissionGeneration, int EvidenceClass, string VerifierKey, DateTimeOffset ObservedAtUtc, string RequestFingerprint,
        DateTimeOffset CreatedAtUtc);

    private sealed class RecordingAdapter(string executorKey) : IGpuExecutorAdapter
    {
        public string ExecutorKey { get; } = executorKey;
        public Channel<GpuExecutorBatchHandle> Deliveries { get; } = Channel.CreateUnbounded<GpuExecutorBatchHandle>();

        public ValueTask DeliverAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Deliveries.Writer.TryWrite(handle);
            return ValueTask.CompletedTask;
        }

        public Task<GpuExecutorBatchHandle> ReadDeliveryAsync() =>
            Deliveries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class DispatchReadObserver : DbCommandInterceptor
    {
        private readonly object _sync = new();
        private readonly List<TaskCompletionSource> _targets = [];
        private int _reads;

        public Task ReadsReached(int target)
        {
            lock (_sync)
            {
                if (_reads >= target)
                {
                    return Task.CompletedTask;
                }

                while (_targets.Count < target)
                {
                    _targets.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return _targets[target - 1].Task;
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("GpuExecutorDispatches", StringComparison.Ordinal))
            {
                RegisterRead();
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void RegisterRead()
        {
            lock (_sync)
            {
                var reads = ++_reads;
                foreach (var target in _targets.Take(reads))
                {
                    target.TrySetResult();
                }
            }
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = now;

        public Channel<DateTimeOffset> ScheduledTimers { get; } = Channel.CreateUnbounded<DateTimeOffset>();

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
            {
                throw new NotSupportedException("Only one-shot timers are supported.");
            }

            lock (_sync)
            {
                var dueAtUtc = _now.Add(dueTime);
                _timers.Add(new ManualTimer(callback, state, dueAtUtc));
                ScheduledTimers.Writer.TryWrite(dueAtUtc);
                return _timers[^1];
            }
        }

        public void AdvanceBy(TimeSpan duration)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_sync)
            {
                _now = _now.Add(duration);
                foreach (var timer in _timers)
                {
                    if (timer.TryTake(_now, out var callback, out var state))
                    {
                        callbacks.Add((callback, state));
                    }
                }
            }

            foreach (var (callback, state) in callbacks)
            {
                callback(state);
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset dueAtUtc) : ITimer
        {
            private int _completed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref _completed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool TryTake(DateTimeOffset now, out TimerCallback takenCallback, out object? takenState)
            {
                if (dueAtUtc > now || Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
                {
                    takenCallback = null!;
                    takenState = null;
                    return false;
                }

                takenCallback = callback;
                takenState = state;
                return true;
            }
        }
    }
}
