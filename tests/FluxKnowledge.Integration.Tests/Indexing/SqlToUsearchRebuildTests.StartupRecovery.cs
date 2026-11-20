using System.Data;
using System.Data.Common;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerFact]
    public async Task Segmented_projection_maintenance_timeout_remains_nonterminal_beyond_five_probes_then_recovers()
    {
        var ownership = new ShortenedProjectionOwnershipWait();
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture,
            "Maintenance does not invalidate the serving index.", segmented: true,
            configureServices: services => services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(
                new IndexingSqlFaultFactory(_fixture.ConnectionString, ownership)));
        var recovery = new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System);
        var held = await recovery.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, default);
        Assert.NotNull(held);
        var coordinator = environment.Services.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                await coordinator.RunOnceAsync(default, retryDueWaited: true);
                Assert.Equal(DerivedIndexRecoveryState.IndexUpdating, coordinator.Snapshot.State);
                Assert.Null(coordinator.Snapshot.FailureCategory);
                ownership.Enabled = true;
            }
        }
        finally { await held!.DisposeAsync(); }
        Assert.Equal(5, ownership.ShortenedWaits);
        ownership.Enabled = false;
        await coordinator.RunOnceAsync(default);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
    }

    [NativeSqlServerFact]
    public async Task Segmented_projection_ownership_wait_honours_cancellation_and_releases_its_transaction()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Cancellation must not retain a projection read fence.", segmented: true);
        var recovery = new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System);
        var held = await recovery.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, default);
        Assert.NotNull(held);
        var store = new SqlSegmentedIndexStore(environment.Factory, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        try
        {
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadProjectionAsync(cancelled.Token).AsTask());
        }
        finally { await held!.DisposeAsync(); }
        await using var next = await recovery.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, default);
        Assert.NotNull(next);
    }

    [NativeSqlServerTheory]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-999)]
    public async Task Segmented_projection_non_timeout_lock_refusals_keep_the_failure_policy(int code)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "A non-timeout lock refusal remains a diagnosed failure.", segmented: true);
        var factory = new IndexingSqlFaultFactory(_fixture.ConnectionString, new ProjectionOwnershipResult(code));
        var store = new SqlSegmentedIndexStore(factory, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        var failure = await Record.ExceptionAsync(() => store.ReadProjectionAsync(default).AsTask());
        Assert.NotNull(failure);
        Assert.IsNotType<FluxKnowledge.Application.Pipeline.PublicationSnapshotConflictException>(failure);
        Assert.Contains(code.ToString(), failure.Message);
    }

    [NativeSqlServerFact]
    public async Task Segmented_publish_waits_for_shared_maintenance_then_reuses_completed_embedding()
    {
        var clock = new CapacityClock();
        var control = new SharedRecoveryRefusal();
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The original publication remains current.",
            segmented: true, clock: clock, configureServices: services =>
                services.AddScoped<IDerivedIndexRecoveryStore>(provider => new RefuseSharedRecoveryStore(
                    new SqlDerivedIndexRecoveryStore(provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(), clock), control)));
        var original = await environment.ActiveGenerationAsync();
        control.RefusalsRemaining = 3;
        await environment.AddAndPumpAsync("The next publication waits for maintenance without repeating embedding.");
        var recordId = environment.LastReceipt!.PipelineRecordId!.Value;
        await using var context = await environment.Factory.CreateDbContextAsync();
        var publish = await context.Jobs.AsNoTracking().SingleAsync(value => value.PipelineRecordId == recordId && value.Stage == (int)PipelineStage.Publish);
        Assert.Equal((int)PublicJobState.WorkerQueued, publish.PublicState);
        Assert.Equal(3, control.Refusals);
        Assert.Equal(original.Id, (await environment.ActiveGenerationAsync()).Id);
        Assert.False(await context.Jobs.AnyAsync(value => value.PipelineRecordId == recordId && value.PublicState == (int)PublicJobState.Failed));
        var embed = await context.Jobs.AsNoTracking().SingleAsync(value => value.PipelineRecordId == recordId && value.Stage == (int)PipelineStage.Embed);
        var artifact = await context.Artifacts.AsNoTracking().SingleAsync(value => value.PipelineRecordId == recordId && value.Stage == (int)PipelineStage.Embed);
        Assert.Equal((int)PublicJobState.Completed, embed.PublicState);
        clock.Advance();
        await environment.PumpAsync();
        Assert.True((await context.PipelineRecords.AsNoTracking().SingleAsync(value => value.Id == recordId)).CompletionCriteriaMet);
        Assert.Equal(embed.AttemptCount, (await context.Jobs.AsNoTracking().SingleAsync(value => value.Id == embed.Id)).AttemptCount);
        Assert.Equal(artifact.Id, (await context.Artifacts.AsNoTracking().SingleAsync(value => value.PipelineRecordId == recordId && value.Stage == (int)PipelineStage.Embed)).Id);
        Assert.False(await context.IndexPublicationCandidates.AnyAsync(value => value.State == 0 || value.State == 1));
        await AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerFact]
    public async Task Segmented_startup_reprobes_after_shared_maintenance_contention_without_building_a_candidate()
    {
        var control = new SharedRecoveryRefusal();
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The surviving passage remains searchable.",
            segmented: true, configureServices: services =>
            {
                services.AddScoped<IDerivedIndexRecoveryStore>(provider => new RefuseSharedRecoveryStore(
                    new SqlDerivedIndexRecoveryStore(provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(), TimeProvider.System), control));
            });
        var suppressed = await environment.AddRetainedAndPumpAsync("Suppress this source to require a no-draft refresh.");
        await using var context = await environment.Factory.CreateDbContextAsync();
        var revision = (await context.PipelineRecords.SingleAsync(value => value.Id == suppressed)).SourceRevisionId;
        var root = (await context.SourceRevisions.SingleAsync(value => value.Id == revision)).SourceRootId;
        await new SqlSourceScanStore(environment.Factory, TimeProvider.System).SuppressUnseenAsync(new SourceRootId(root),
            new HashSet<SourceRevisionId>(), CancellationToken.None);
        var before = await context.IndexPublicationCandidates.CountAsync();
        control.RefusalsRemaining = 1;
        var coordinator = environment.Services.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.IndexUpdating, coordinator.Snapshot.State);
        Assert.Null(coordinator.Snapshot.FailureCategory);
        Assert.Equal(1, control.Refusals);
        Assert.Equal(before, await context.IndexPublicationCandidates.CountAsync());
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        Assert.Null(coordinator.Snapshot.FailureCategory);
        Assert.False(await context.IndexPublicationCandidates.AnyAsync(value => value.State == 0 || value.State == 1));
    }

    [NativeSqlServerFact]
    public async Task Segmented_projection_read_fences_concurrent_retirement_but_rejects_a_genuinely_retired_active_generation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "A consistent projection snapshot.", segmented: true);
        var active = await environment.ActiveGenerationAsync();
        var pause = new ProjectionGenerationReadPause();
        var factory = new IndexingSqlFaultFactory(_fixture.ConnectionString, pause);
        var store = new SqlSegmentedIndexStore(factory, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        var reading = store.ReadProjectionAsync(CancellationToken.None).AsTask();
        Task? retirement = null;
        var wasBlocked = false;
        var writerReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            retirement = Task.Run(async () =>
            {
                await using var writer = await environment.Factory.CreateDbContextAsync();
                await using var transaction = await writer.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                writerReached.TrySetResult();
                await SqlPublishedPassageSelection.AcquireFenceAsync(writer, CancellationToken.None);
                await writer.IndexGenerations.Where(value => value.Id == active.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(value => value.RetiredAtUtc, DateTimeOffset.UtcNow));
                await transaction.CommitAsync();
            });
            await writerReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAny(retirement, Task.Delay(250));
            wasBlocked = !retirement.IsCompleted;
        }
        finally { pause.Resume.TrySetResult(); }
        SegmentedProjectionState? projection = null;
        var readFailure = await Record.ExceptionAsync(async () => projection = await reading.WaitAsync(TimeSpan.FromSeconds(10)));
        await retirement!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(wasBlocked, "Publication must wait until both projection metadata reads finish.");
        Assert.Null(readFailure);
        Assert.Equal(active.Id, projection!.Generation!.Id);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ReadProjectionAsync(CancellationToken.None));
        Assert.Equal("active-segmented-manifest-invalid", failure.Message);
    }

    private sealed class ProjectionGenerationReadPause : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [IndexGenerations]", StringComparison.Ordinal))
            {
                Reached.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class ShortenedProjectionOwnershipWait : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int ShortenedWaits { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("segmented-projection-read-ownership", StringComparison.Ordinal))
            {
                const string normalWait = "@LockTimeout = 5000;";
                Assert.Contains(normalWait, command.CommandText, StringComparison.Ordinal);
                // One probe uses the real deadline. The remaining probes ask SQL
                // for the same contested lock and get its real timeout immediately.
                command.CommandText = command.CommandText.Replace(normalWait, "@LockTimeout = 0;", StringComparison.Ordinal);
                ShortenedWaits++;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ProjectionOwnershipResult(int code) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("segmented-projection-read-ownership", StringComparison.Ordinal)) return ValueTask.FromResult(result);
            var rows = new DataTable();
            rows.Columns.Add("Value", typeof(int));
            rows.Rows.Add(code);
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(rows.CreateDataReader()));
        }
    }

    private sealed class SharedRecoveryRefusal
    {
        public int RefusalsRemaining { get; set; }
        public int Refusals { get; set; }
    }

    private sealed class RefuseSharedRecoveryStore(IDerivedIndexRecoveryStore inner, SharedRecoveryRefusal control) : IDerivedIndexRecoveryStore
    {
        public ValueTask<IDerivedIndexRecoveryLease?> TryAcquireSharedLeaseAsync(TimeSpan timeout, CancellationToken token)
        {
            if (control.RefusalsRemaining > 0) { control.RefusalsRemaining--; control.Refusals++; return ValueTask.FromResult<IDerivedIndexRecoveryLease?>(null); }
            return inner.TryAcquireSharedLeaseAsync(timeout, token);
        }
        public ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(TimeSpan timeout, CancellationToken token) => inner.TryAcquireExclusiveLeaseAsync(timeout, token);
        public ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(CancellationToken token) => inner.ReadActiveAsync(token);
        public ValueTask<bool> TryUpdateRecoveryPathAsync(Guid id, string expected, string replacement, DateTimeOffset at, CancellationToken token) => inner.TryUpdateRecoveryPathAsync(id, expected, replacement, at, token);
        public ValueTask AppendAuditAsync(DerivedIndexRecoveryAuditEvent value, CancellationToken token) => inner.AppendAuditAsync(value, token);
    }
}
