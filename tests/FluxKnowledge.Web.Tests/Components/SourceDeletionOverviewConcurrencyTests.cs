using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class SourceDeletionOverviewConcurrencyTests(NativeSqlServerFixture fixture, ITestOutputHelper output)
    : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private readonly Guid _root = Guid.NewGuid();
    private readonly Guid _publicRoot = Guid.NewGuid();
    private readonly Guid _operation = Guid.NewGuid();
    private readonly Guid _lease = Guid.NewGuid();
    private readonly Guid _target = Guid.NewGuid();
    private string? _confirmation;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [NativeSqlServerTheory]
    [InlineData("claim")]
    [InlineData("receipt")]
    [InlineData("root_delete")]
    [InlineData("purge")]
    public async Task Reader_first_excludes_the_deletion_writer_through_the_closing_fence(string writerKind)
    {
        await SeedAsync(writerKind);
        var order = new EventOrder();
        var pause = new ReadPause(output, order);
        var commits = new CommitOrder(order, output);
        var writerTrace = new WriterTrace(output, writerKind is "purge" or "root_delete" ? "IndexState" : "SourceDeletionOperations");
        var read = Reader(new Factory(fixture.ConnectionString, pause)).ReadOverviewAsync(default).AsTask();
        Task? write = null;
        var writerWaited = false;
        try
        {
            await pause.Reached.Task.WaitAsync(Deadline);
            write = WriteAsync(writerKind, new Factory(fixture.ConnectionString, writerTrace, commits));
            await writerTrace.LockAttempted.Task.WaitAsync(Deadline);
            // The real writer cannot commit between the overview's two fences.
            await Task.Delay(150);
            writerWaited = !write.IsCompleted;
        }
        finally { pause.Release.TrySetResult(); }
        var overview = await read.WaitAsync(Deadline);
        if (write is not null) await write.WaitAsync(Deadline);
        Assert.True(writerWaited, "The deletion writer committed inside the overview read.");
        AssertSnapshotAndFenceTruth(overview, pause, "Enabled");
        Assert.NotEmpty(commits.Attempts);
        // A conservative refusal must never conceal a writer that escaped the
        // fence after the 150ms check but before the closing SQL result.
        Assert.All(commits.Attempts, sequence => Assert.True(sequence > pause.Fences[1].Sequence));
        await AssertWriterCommittedAsync(writerKind);
    }

    [NativeSqlServerTheory]
    [InlineData("claim")]
    [InlineData("receipt")]
    [InlineData("root_delete")]
    [InlineData("purge")]
    public async Task Writer_first_blocks_overview_until_commit_and_preserves_fence_truth(string writerKind)
    {
        await SeedAsync(writerKind);
        var pause = new CommitPause(output);
        var trace = new FenceTrace(output);
        var write = WriteAsync(writerKind, new Factory(fixture.ConnectionString, pause, new WriterTrace(output)));
        Task<OverviewProjection>? read = null;
        var readerWaited = false;
        try
        {
            await pause.Reached.Task.WaitAsync(Deadline);
            read = Reader(new Factory(fixture.ConnectionString, trace)).ReadOverviewAsync(default).AsTask();
            await Task.Delay(150);
            readerWaited = !read.IsCompleted;
        }
        finally { pause.Release.TrySetResult(); }
        await write.WaitAsync(Deadline);
        var overview = await read!.WaitAsync(Deadline);
        Assert.True(readerWaited, "The overview did not wait for the uncommitted deletion writer.");
        AssertSnapshotAndFenceTruth(overview, trace, writerKind == "root_delete" ? "Deleting" : "Enabled");
        await AssertWriterCommittedAsync(writerKind);
    }

    [NativeSqlServerFact]
    public async Task Cancelling_an_overview_releases_its_locks_and_the_real_claim_resumes()
    {
        await SeedAsync("claim");
        var pause = new ReadPause(output);
        using var stopping = new CancellationTokenSource();
        var read = Reader(new Factory(fixture.ConnectionString, pause)).ReadOverviewAsync(stopping.Token).AsTask();
        await pause.Reached.Task.WaitAsync(Deadline);
        var write = WriteAsync("claim", new Factory(fixture.ConnectionString));
        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await write.WaitAsync(Deadline);
        var trace = new FenceTrace(output);
        AssertSnapshotAndFenceTruth(await Reader(new Factory(fixture.ConnectionString, trace)).ReadOverviewAsync(default), trace, "Enabled");
        await AssertWriterCommittedAsync("claim");
    }

    [NativeSqlServerFact]
    public async Task A_stalled_overview_has_a_bounded_lock_lifetime_without_caller_cancellation()
    {
        await SeedAsync("claim");
        var pause = new ReadPause(output);
        var read = Reader(new Factory(fixture.ConnectionString, pause)).ReadOverviewAsync(default).AsTask();
        Task? write = null;
        try
        {
            await pause.Reached.Task.WaitAsync(Deadline);
            write = WriteAsync("claim", new Factory(fixture.ConnectionString));
            await AssertInternalTimeoutAsync(read);
        }
        finally
        {
            pause.Release.TrySetResult();
            try { await read.WaitAsync(Deadline); } catch (TimeoutException) { }
            if (write is not null) await write.WaitAsync(Deadline);
        }
        await AssertWriterCommittedAsync("claim");
    }

    [NativeSqlServerFact]
    public async Task Waiting_for_the_deletion_precheck_is_included_in_the_same_read_budget()
    {
        await SeedAsync("claim");
        var factory = new Factory(fixture.ConnectionString);
        await using var writer = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(fixture.ConnectionString).Options);
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.SourceDeletionOperations.Where(o => o.Id == _operation)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.UpdatedAtUtc, DateTimeOffset.UtcNow));
        var read = Reader(factory).ReadOverviewAsync(default).AsTask();
        try
        {
            await AssertInternalTimeoutAsync(read);
        }
        finally
        {
            await transaction.CommitAsync();
            try { await read.WaitAsync(Deadline); } catch (TimeoutException) { }
        }
        await WriteAsync("claim", factory).WaitAsync(Deadline);
        await AssertWriterCommittedAsync("claim");
    }

    [NativeSqlServerFact]
    public async Task Unrelated_concurrent_changes_are_still_rejected_during_deletion()
    {
        await SeedAsync("claim");
        using var stopping = new CancellationTokenSource();
        // Prepare only the independent writer connection/command. No query runs
        // before the cold overview; the actual mutation stays between its fences.
        await using var writer = new SqlConnection(fixture.ConnectionString);
        await writer.OpenAsync(stopping.Token);
        await using var change = writer.CreateCommand();
        change.CommandText = "UPDATE [SourceRootConfigurations] SET [DisplayName] = @name WHERE [Id] = @root";
        change.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 256) { Value = "Changed concurrently" });
        change.Parameters.Add(new SqlParameter("@root", SqlDbType.UniqueIdentifier) { Value = _publicRoot });
        var pause = new ReadPause(output);
        using var timing = new ReadTimingTrace(stopping.Token);
        output.WriteLine($"{DateTimeOffset.UtcNow:O} timestamp={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency} Overview starting.");
        var read = Reader(new Factory(fixture.ConnectionString, timing, pause)).ReadOverviewAsync(stopping.Token).AsTask();
        Exception? failure = null;
        try
        {
            await WaitForReadPauseAsync(pause, read);
            output.WriteLine($"{DateTimeOffset.UtcNow:O} timestamp={Stopwatch.GetTimestamp()} Unrelated write executing.");
            Assert.Equal(1, await change.ExecuteNonQueryAsync(stopping.Token));
            output.WriteLine($"{DateTimeOffset.UtcNow:O} timestamp={Stopwatch.GetTimestamp()} Unrelated write completed; rows=1.");
            output.WriteLine($"{DateTimeOffset.UtcNow:O} timestamp={Stopwatch.GetTimestamp()} Closing fence released.");
            pause.Release.TrySetResult();
            var overview = await read.WaitAsync(Deadline);
            output.WriteLine($"{DateTimeOffset.UtcNow:O} timestamp={Stopwatch.GetTimestamp()} Overview completed.");
            Assert.Equal(2, pause.Fences.Count);
            Assert.NotEqual(pause.Fences[0].Version, pause.Fences[1].Version);
            Assert.False(overview.Activity.IsConsistent);
        }
        catch (Exception exception)
        {
            timing.Record("overview-failed", exception: exception);
            failure = exception;
            throw;
        }
        finally
        {
            timing.Record("cleanup-start");
            pause.Release.TrySetResult();
            try
            {
                await stopping.CancelAsync();
                await read;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception exception) when (failure is not null)
            {
                output.WriteLine($"{DateTimeOffset.UtcNow:O} Read cleanup observed {exception.GetType().Name}; original test failure retained.");
            }
            finally { timing.WriteTo(output); }
        }
    }

    private static async Task WaitForReadPauseAsync(ReadPause pause, Task<OverviewProjection> read)
    {
        if (await Task.WhenAny(pause.Reached.Task, read).WaitAsync(Deadline) == read && !pause.Reached.Task.IsCompleted)
        {
            await read; // Preserve the actual read failure instead of a later barrier timeout.
            throw new InvalidOperationException("Overview completed before the closing-fence interleaving was established.");
        }
        await pause.Reached.Task.WaitAsync(Deadline);
    }

    [NativeSqlServerFact]
    public async Task Multiple_overview_readers_and_deletion_claim_receipts_all_make_progress()
    {
        await SeedAsync("claim");
        var factory = new Factory(fixture.ConnectionString);
        var store = new SqlSourceDeletionStore(factory, TimeProvider.System);
        var pause = new ReadPause(output);
        var firstRead = Reader(new Factory(fixture.ConnectionString, pause)).ReadOverviewAsync(default).AsTask();
        await pause.Reached.Task.WaitAsync(Deadline);
        var writes = 0;
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 12; i++)
            {
                var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
                await store.DeferAsync(work, "source-delete-search-queries-active", default);
                Interlocked.Increment(ref writes);
            }
        });
        var readers = Enumerable.Range(0, 4).Select(async readerIndex =>
        {
            var accepted = 0;
            var inconsistent = 0;
            var budgetExceeded = 0;
            for (var i = 0; i < 8; i++)
            {
                try
                {
                    if ((await Reader(factory).ReadOverviewAsync(default)).Activity.IsConsistent) accepted++;
                    else inconsistent++;
                }
                catch (OverviewReadBudgetExceededException)
                {
                    // A bounded unavailable sample is allowed; each reader must
                    // still make progress within its original eight attempts.
                    budgetExceeded++;
                }
            }
            output.WriteLine($"Reader {readerIndex}: accepted={accepted}, inconsistent={inconsistent}, budget-exceeded={budgetExceeded}.");
            return accepted;
        }).ToArray();
        pause.Release.TrySetResult();
        AssertSnapshotAndFenceTruth(await firstRead.WaitAsync(Deadline), pause, "Enabled");
        await Task.WhenAll(readers.Append(writer.ContinueWith(_ => 0, TaskScheduler.Default))).WaitAsync(Deadline);
        await writer;
        Assert.Equal(12, writes);
        Assert.All(readers, task => Assert.True(task.Result > 0));
    }

    private static async Task AssertInternalTimeoutAsync(Task<OverviewProjection> read)
    {
        // A test's own deadline is not evidence of the reader enforcing its budget.
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5))));
        await Assert.ThrowsAsync<OverviewReadBudgetExceededException>(() => read);
    }

    private void AssertSnapshotAndFenceTruth(OverviewProjection overview, FenceTrace trace, string lifecycle)
    {
        Assert.Equal(2, trace.Fences.Count);
        var before = trace.Fences[0];
        var after = trace.Fences[1];
        // Read-only cold queries can advance DBTS without changing stored rows.
        // Exclusion and data are mandatory; acceptance follows the actual fences.
        Assert.Equal(before.IsStable && after.IsStable && before.Version == after.Version, overview.Activity.IsConsistent);
        Assert.Equal(lifecycle, Assert.Single(overview.Activity.Sources, s => s.Id == _publicRoot).Lifecycle);
    }

    private async Task SeedAsync(string writerKind)
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = new Factory(fixture.ConnectionString).CreateDbContext();
        context.SourceRootConfigurations.AddRange(Root(_root, SourceRootState.Deleting), Root(_publicRoot, SourceRootState.Enabled));
        context.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
        {
            Id = _operation, SourceRootId = _root, State = writerKind is "receipt" or "purge" ? 1 : 0,
            Phase = writerKind == "purge" ? "purge-records" : "cleanup-files",
            LeaseId = writerKind is "receipt" or "purge" ? _lease : null,
            LeaseExpiresAtUtc = writerKind is "receipt" or "purge" ? now.AddMinutes(15) : null,
            CreatedAtUtc = now, UpdatedAtUtc = now
        });
        if (writerKind == "receipt") context.SourceDeletionCleanupItems.Add(new()
        {
            Id = _target, SourceDeletionOperationId = _operation, StorageKind = 2, RelativePath = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = now, UpdatedAtUtc = now
        });
        await context.SaveChangesAsync();
        if (writerKind == "root_delete")
        {
            // Preview writes an independent versioned intent. It is outside the
            // publication fence and must not be hidden by this commit-order test.
            var factory = new Factory(fixture.ConnectionString);
            var service = new NativeCorpusCommandService(new SqlNativeOperationStore(factory, TimeProvider.System),
                new SqlNativeCorpusActionStore(factory, new NoRootCreationPolicy(), new LocalPrivateContentDisclosure()));
            _confirmation = (await service.PreviewAsync(PublicMutation(), "test", default)).ConfirmationId;
        }
        SourceRootConfigurationEntity Root(Guid id, SourceRootState state) => new()
        {
            Id = id, DisplayName = "Overview deletion " + id.ToString("N"), CanonicalPath = "C:\\synthetic-overview\\" + id.ToString("N"),
            State = (int)state, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]",
            AllowedClassificationsJson = "[\"text/plain\"]", MaximumFileBytes = 10000, ConfigurationRevision = 1,
            ReconciliationCadenceSeconds = 900, CreatedAtUtc = now, UpdatedAtUtc = now
        };
    }

    private async Task WriteAsync(string kind, Factory factory)
    {
        var store = new SqlSourceDeletionStore(factory, TimeProvider.System);
        var work = new SourceDeletionWorkItem(_operation, _root, _lease, kind == "purge" ? "purge-records" : "cleanup-files");
        switch (kind)
        {
            case "claim": Assert.NotNull(await store.ClaimNextAsync(default)); break;
            case "receipt": await store.RecordCleanupResultAsync(work, _target, new(true), default); break;
            case "purge": Assert.Equal("cleanup-files", (await store.PurgeAsync(work, null, default)).Phase); break;
            case "root_delete":
                var service = new NativeCorpusCommandService(new SqlNativeOperationStore(factory, TimeProvider.System),
                    new SqlNativeCorpusActionStore(factory, new NoRootCreationPolicy(), new LocalPrivateContentDisclosure()));
                await service.CommitAsync(PublicMutation(), _confirmation ?? throw new InvalidOperationException("Preview not prepared."),
                    "overview:" + _publicRoot.ToString("N"), "test", default);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private NativeCorpusMutation PublicMutation() => new("root_delete", JsonSerializer.SerializeToElement(new { rootId = _publicRoot }));

    private async Task AssertWriterCommittedAsync(string kind)
    {
        await using var context = new Factory(fixture.ConnectionString).CreateDbContext();
        var operation = await context.SourceDeletionOperations.SingleAsync(o => o.Id == _operation);
        if (kind == "claim") Assert.Equal(1, operation.State);
        if (kind == "receipt")
        {
            Assert.Equal(0, operation.State);
            Assert.Equal(1, await context.SourceDeletionCleanupItems.Where(i => i.Id == _target).Select(i => i.State).SingleAsync());
        }
        if (kind == "purge") Assert.Equal("cleanup-files", operation.Phase);
        if (kind == "root_delete") Assert.Equal(2, await context.SourceRootConfigurations.Where(r => r.Id == _publicRoot).Select(r => r.State).SingleAsync());
    }

    private static SqlProjectionReader Reader(Factory factory) => new(factory, new RecoveryStatus(), new SqlGpuSchedulerStore(factory));
    private sealed class RecoveryStatus : IDerivedIndexRecoveryStatus
    {
        public DerivedIndexRecoverySnapshot Snapshot => new(DerivedIndexRecoveryState.Healthy, null, null, null, null, 0);
    }
    private sealed class NoRootCreationPolicy : ISourceRootPathPolicy
    {
        public SourceRootPathValidation ValidateAndCanonicalise(SourceRootCreateRequest request) => throw new NotSupportedException();
    }
    private sealed class Factory(string connection, params IInterceptor[] interceptors) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection, sql => sql.EnableRetryOnFailure()).AddInterceptors(interceptors).Options);
    }
    private sealed record CapturedFence(long Version, bool IsStable, long Sequence);
    private sealed class EventOrder
    {
        private long _sequence;
        public long Next() => Interlocked.Increment(ref _sequence);
    }
    private sealed class CommitOrder(EventOrder order, ITestOutputHelper output) : DbTransactionInterceptor
    {
        public List<long> Attempts { get; } = [];
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            var sequence = order.Next();
            Attempts.Add(sequence);
            output.WriteLine($"{DateTimeOffset.UtcNow:O} WRITE COMMIT attempt sequence={sequence}");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ReadTimingTrace(CancellationToken caller) : DbCommandInterceptor, IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly ConcurrentQueue<string> _observations = new();
        private CancellationToken _commandToken;
        private CancellationTokenRegistration? _cancellation;

        public void Record(string phase, Guid commandId = default, string query = "overview", TimeSpan? duration = null,
            Exception? exception = null)
        {
            _observations.Enqueue($"READ TIMING elapsed={Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F3}ms " +
                $"phase={phase} command={commandId} query={query} duration={duration?.TotalMilliseconds:F3}ms " +
                $"command-cancelled={_commandToken.IsCancellationRequested} caller-cancelled={caller.IsCancellationRequested} " +
                $"exception={exception?.GetType().Name} sql-number={(exception as SqlException)?.Number}");
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            _commandToken = cancellationToken;
            // Observe the read token without issuing SQL or moving the cold read.
            _cancellation ??= cancellationToken.Register(() => Record("read-token-cancelled"));
            Record("command-start", eventData.CommandId, Query(command));
            return ValueTask.FromResult(result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Record("reader-returned", eventData.CommandId, Query(command), eventData.Duration);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData,
            InterceptionResult result)
        {
            Record("reader-disposing", eventData.CommandId, Query(command), eventData.Duration);
            return result;
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Record("command-failed", eventData.CommandId, Query(command), eventData.Duration, eventData.Exception);
            return Task.CompletedTask;
        }

        public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Record("command-cancelled", eventData.CommandId, Query(command), eventData.Duration);
            return Task.CompletedTask;
        }

        private static string Query(DbCommand command)
        {
            if (command.CommandText.Contains("MIN_ACTIVE_ROWVERSION", StringComparison.Ordinal)) return "fence";
            var start = command.CommandText.IndexOf("FROM [", StringComparison.Ordinal);
            var end = start < 0 ? -1 : command.CommandText.IndexOf(']', start + 6);
            return end < 0 ? "other" : command.CommandText[(start + 6)..end];
        }

        public void WriteTo(ITestOutputHelper output)
        {
            foreach (var observation in _observations) output.WriteLine(observation);
        }

        public void Dispose() => _cancellation?.Dispose();
    }
    private class FenceTrace(ITestOutputHelper output, EventOrder? order = null) : DbCommandInterceptor
    {
        private int _ordinal;
        public List<CapturedFence> Fences { get; } = [];
        protected void TracePause() => output.WriteLine($"{DateTimeOffset.UtcNow:O} timestamp={Stopwatch.GetTimestamp()} Closing fence paused.");
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("MIN_ACTIVE_ROWVERSION", StringComparison.Ordinal)) return result;
            // Buffer the actual one-row result, not a second diagnostic SQL query.
            // EF receives those exact values through an equivalent typed reader.
            var table = new DataTable();
            table.Columns.Add("Version", typeof(long));
            table.Columns.Add("IsStable", typeof(bool));
            try
            {
                if (!await result.ReadAsync(cancellationToken)) throw new InvalidOperationException("Missing fence result.");
                var version = result.GetInt64(result.GetOrdinal("Version"));
                var stable = result.GetBoolean(result.GetOrdinal("IsStable"));
                table.Rows.Add(version, stable);
                var sequence = order?.Next() ?? 0;
                Fences.Add(new(version, stable, sequence));
                output.WriteLine($"{DateTimeOffset.UtcNow:O} FENCE context={eventData.Context?.ContextId} transaction={command.Transaction?.GetHashCode()} ordinal={++_ordinal} Version={version} IsStable={stable} sequence={sequence}");
                if (await result.ReadAsync(cancellationToken)) throw new InvalidOperationException("Unexpected second fence row.");
            }
            finally { await result.DisposeAsync(); }
            return table.CreateDataReader();
        }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            output.WriteLine($"{DateTimeOffset.UtcNow:O} READ FAILED context={eventData.Context?.ContextId} {eventData.Exception.GetType().Name}");
            return Task.CompletedTask;
        }
    }
    private sealed class WriterTrace(ITestOutputHelper output, string? lockTable = null) : DbCommandInterceptor
    {
        public TaskCompletionSource LockAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (lockTable is not null && command.CommandText.Contains($"[{lockTable}]", StringComparison.Ordinal) &&
                command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal)) LockAttempted.TrySetResult();
            Trace(command, eventData.Context, "executing");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Trace(command, eventData.Context, "reader opened");
            return ValueTask.FromResult(result);
        }
        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            Trace(command, eventData.Context, "reader disposed after enumeration");
            return result;
        }
        private void Trace(DbCommand command, DbContext? context, string phase)
        {
            if (!command.CommandText.Contains("[SourceDeletionOperations]", StringComparison.Ordinal)) return;
            var kind = command.CommandText.Contains("UPDATE [SourceDeletionOperations]", StringComparison.Ordinal) ? "UPDATE" : "operation SELECT";
            output.WriteLine($"{DateTimeOffset.UtcNow:O} WRITE {kind} {phase} context={context?.ContextId} transaction={command.Transaction?.GetHashCode()}");
        }
    }
    private sealed class ReadPause(ITestOutputHelper output, EventOrder? order = null) : FenceTrace(output, order)
    {
        private int _fences;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("MIN_ACTIVE_ROWVERSION", StringComparison.Ordinal) && Interlocked.Increment(ref _fences) == 2)
            {
                TracePause();
                Reached.TrySetResult();
                await Release.Task.WaitAsync(Deadline, cancellationToken);
            }
            return result;
        }
    }
    private sealed class CommitPause(ITestOutputHelper output) : DbTransactionInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            output.WriteLine($"{DateTimeOffset.UtcNow:O} WRITE COMMIT waiting context={eventData.Context?.ContextId} transaction={transaction.GetHashCode()}");
            Reached.TrySetResult();
            await Release.Task.WaitAsync(Deadline, cancellationToken);
            return result;
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            output.WriteLine($"{DateTimeOffset.UtcNow:O} WRITE COMMIT completed context={eventData.Context?.ContextId} transaction={transaction.GetHashCode()}");
            return Task.CompletedTask;
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        await using var context = new Factory(fixture.ConnectionString).CreateDbContext();
        var operations = context.SourceDeletionOperations.Where(o => o.SourceRootId == _root || o.SourceRootId == _publicRoot);
        await context.SourceDeletionCleanupItems.Where(i => operations.Any(o => o.Id == i.SourceDeletionOperationId)).ExecuteDeleteAsync();
        await operations.ExecuteDeleteAsync();
        await context.SourceRootConfigurations.Where(r => r.Id == _root || r.Id == _publicRoot).ExecuteDeleteAsync();
    }
}

public sealed class SourceDeletionReadFenceTests
{
    [Fact]
    public void Stable_fences_with_different_versions_are_rejected()
    {
        var before = new ActivityReadFence { Version = 2084, IsStable = true };
        var after = new ActivityReadFence { Version = 2085, IsStable = true };
        Assert.False(before.Accepts(after));
    }
}
