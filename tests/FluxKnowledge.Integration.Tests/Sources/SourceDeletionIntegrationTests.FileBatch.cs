using System.Data.Common;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Physical_cleanup_commits_the_successful_prefix_and_failure_in_one_receipt_unit()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("file-batch-prefix", SourceRootState.Deleting, now);
        var operationId = await AddDeletionAsync(source, now);
        var ids = Enumerable.Range(1, 4).Select(value => new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)value)).ToArray();
        var names = ids.Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        await using (var seed = CreateContext())
        {
            await seed.SourceDeletionOperations.Where(row => row.Id == operationId)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Phase, "cleanup-files"));
            seed.SourceDeletionCleanupItems.AddRange(ids.Select((id, index) => new SourceDeletionCleanupItemEntity
            {
                Id = id, SourceDeletionOperationId = operationId, StorageKind = 2, RelativePath = names[index],
                CreatedAtUtc = now, UpdatedAtUtc = now
            }));
            await seed.SaveChangesAsync();
        }

        using var files = new CleanupBatchFiles();
        var paths = names.Select(name => Path.Combine(files.Index, "generations", name)).ToArray();
        Directory.CreateDirectory(paths[0]);
        Directory.CreateDirectory(paths[1]);
        File.WriteAllText(paths[2], "unexpected file in generation slot");
        Directory.CreateDirectory(paths[3]);

        var statements = new PhysicalReceiptCommands();
        var factory = new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString).AddInterceptors(statements).Options);
        var coordinator = new SourceDeletionCoordinator(new SqlSourceDeletionStore(factory, TimeProvider.System),
            fileStore: new WindowsSourceDeletionFileStore(files.Retained, files.Index),
            queryCleanupGate: new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System),
            timeProvider: new FrozenBatchTimer());

        await coordinator.RunOneAsync(default);

        await using var check = CreateContext();
        var receipts = await check.SourceDeletionCleanupItems.OrderBy(row => row.Id).ToArrayAsync();
        Assert.Equal(new[] { 1, 1, 2, 0 }, receipts.Select(row => row.State));
        Assert.Equal("source-delete-index-path-invalid", receipts[2].ReasonCode);
        var operation = await check.SourceDeletionOperations.SingleAsync(row => row.Id == operationId);
        Assert.Equal("attention", operation.Phase);
        Assert.Equal("cleanup-files", operation.ResumePhase);
        Assert.False(Directory.Exists(paths[0]));
        Assert.False(Directory.Exists(paths[1]));
        Assert.Equal("unexpected file in generation slot", File.ReadAllText(paths[2]));
        Assert.True(Directory.Exists(paths[3]));
        Assert.Equal(1, statements.ReceiptWrites);
    }


    [NativeSqlServerFact]
    public async Task Physical_cleanup_drains_a_mixed_queue_with_real_windows_io()
    {
        var now = DateTimeOffset.UtcNow;
        var deleting = await SeedRootAsync("file-batch-drain", SourceRootState.Deleting, now);

        var operationId = await AddDeletionAsync(deleting, now);
        await AdvanceSqlToFilesAsync(new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System), operationId);
        using var files = new CleanupBatchFiles();
        var originals = Path.Combine(files.Index, "original.txt");
        File.WriteAllText(originals, "source original");
        var random = new Random(73);
        var removedFiles = new List<string>();
        var preservedFiles = new List<string>();
        var generations = new List<string>();

        await using (var seed = CreateContext())
        {
            for (var i = 0; i < 128; i++)
            {
                var bytes = new byte[4096 << (2 * (i % 3))];
                random.NextBytes(bytes);
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var relative = Path.Combine("sha256", hash[..2], hash + ".bin");
                var path = Path.Combine(files.Retained, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                seed.SourceDeletionCleanupItems.Add(new()
                {
                    Id = Guid.NewGuid(), SourceDeletionOperationId = operationId, StorageKind = 1,
                    RelativePath = relative, ContentSha256 = hash, ByteLength = bytes.Length, CreatedAtUtc = now, UpdatedAtUtc = now
                });
                if (i % 16 == 0)
                {
                    var keeper = await SeedRootAsync($"file-batch-keeper-{i}", SourceRootState.Enabled, now);
                    seed.SourceArtifacts.Add(new()
                    {
                        Id = Guid.NewGuid(), SourceRevisionId = keeper.RevisionId, ContentSha256 = hash,
                        StoreRelativePath = relative, ByteLength = bytes.Length, ChecksumVerifiedAtUtc = now, ReferenceCount = 1
                    });
                    preservedFiles.Add(path);
                }
                else
                {
                    removedFiles.Add(path);
                }
            }

            for (var i = 0; i < 24; i++)
            {
                var name = Guid.NewGuid().ToString("N");
                var path = Path.Combine(files.Index, "generations", name);
                Directory.CreateDirectory(Path.Combine(path, "part"));
                File.WriteAllBytes(Path.Combine(path, "part", "vectors.bin"), new byte[65536]);
                seed.SourceDeletionCleanupItems.Add(new()
                {
                    Id = Guid.NewGuid(), SourceDeletionOperationId = operationId, StorageKind = 2,
                    RelativePath = name, CreatedAtUtc = now, UpdatedAtUtc = now
                });
                generations.Add(path);
            }
            await seed.SaveChangesAsync();
        }

        var workloadConnection = new SqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            ApplicationName = $"FluxKnowledge.FileCleanupBench.{Guid.NewGuid():N}",
            MaxPoolSize = 4,
            ConnectTimeout = 5
        };
        using var probe = new PhysicalRoundTripProbe(workloadConnection.ApplicationName);
        var factory = new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(workloadConnection.ConnectionString).Options);
        var coordinator = new SourceDeletionCoordinator(new SqlSourceDeletionStore(factory, TimeProvider.System),
            fileStore: new WindowsSourceDeletionFileStore(files.Retained, files.Index),
            artifactPublicationGate: new SqlSourceArtifactPublicationGate(factory),
            queryCleanupGate: new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System));
        var timer = Stopwatch.StartNew();
        Assert.True(await RunDeletionUntilStoppedAsync(coordinator));
        timer.Stop();

        await using var check = CreateContext();
        var operation = await check.SourceDeletionOperations.SingleAsync(row => row.Id == operationId);
        Assert.True(operation.Phase == "completed", $"Cleanup stopped in {operation.Phase}: {operation.ReasonCode}");
        Assert.Equal(8, operation.SharedArtifactCount);
        Assert.Empty(await check.SourceDeletionCleanupItems.Where(row => row.SourceDeletionOperationId == operationId).ToArrayAsync());
        Assert.All(removedFiles, path => Assert.False(File.Exists(path)));
        Assert.All(preservedFiles, path => Assert.True(File.Exists(path)));
        Assert.All(generations, path => Assert.False(Directory.Exists(path)));
        Assert.Equal("source original", File.ReadAllText(originals));
        var roundTrips = probe.RoundTrips;
        Assert.True(roundTrips > 0);
        Assert.True(probe.LegacyGateConnections > 0);
        output.WriteLine($"physical_cleanup blobs=128 shared=8 legacy_generations=24 sql_round_trips={roundTrips} legacy_gate_connections={probe.LegacyGateConnections} elapsed_ms={timer.Elapsed.TotalMilliseconds:F1}");
    }


    [NativeSqlServerFact]
    public async Task Physical_cleanup_replays_all_receipts_once_when_the_commit_acknowledgement_is_lost()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("file-batch-lost-ack", SourceRootState.Deleting, now);
        var operationId = await AddDeletionAsync(source, now);
        var ordinary = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        await AdvanceSqlToFilesAsync(ordinary, operationId);
        await SeedLegacyCleanupBatchAsync(operationId, [Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")], now);
        var work = Assert.IsType<SourceDeletionWorkItem>(await ordinary.ClaimNextAsync(default));
        var targets = await ordinary.ReadPendingCleanupAsync(work, default, 2);
        Assert.Equal(2, targets.Count);

        var changed = targets[1] with { RelativePath = Guid.NewGuid().ToString("N") };
        SourceDeletionFileOutcome[] invalid = [new(targets[0], new(true)), new(changed, new(true))];
        Assert.False(await ordinary.RecordCleanupResultsAsync(work, invalid, default));
        Assert.False(await ordinary.RecordCleanupResultsAsync(work with { SourceRootId = Guid.NewGuid() },
            [new(targets[0], new(true)), new(targets[1], new(true))], default));
        var wrongPhase = work with { Phase = "cleanup-memberships" };
        Assert.Empty(await ordinary.ReadPendingCleanupAsync(wrongPhase, default, 2));
        Assert.False(await ordinary.RecordCleanupResultsAsync(wrongPhase, [new(targets[0], new(true))], default));
        Assert.Equal(2, (await ordinary.ReadPendingCleanupAsync(work, default, 2)).Count);

        var ambiguous = new SqlSourceDeletionStore(new CommitLossContextFactory(_fixture.ConnectionString), TimeProvider.System);
        SourceDeletionFileOutcome[] outcomes = [new(targets[0], new(true)), new(targets[1], new(false, "source-delete-index-io-failed"))];
        Assert.True(await ambiguous.RecordCleanupResultsAsync(work, outcomes, default));
        Assert.False(await ordinary.RecordCleanupResultsAsync(work, outcomes, default));
        Assert.False(await ordinary.RecordCleanupResultsAsync(work,
            [new(targets[0], new(true)), new(targets[1], new(true))], default));

        await using var check = CreateContext();
        var states = await check.SourceDeletionCleanupItems.OrderBy(row => row.Id).Select(row => row.State).ToArrayAsync();
        Assert.Equal(new[] { 1, 2 }, states);
        var operation = await check.SourceDeletionOperations.SingleAsync(row => row.Id == operationId);
        Assert.Equal(work.LeaseId, operation.LastCommittedLeaseId);
        Assert.Equal("attention", operation.Phase);
        Assert.Equal("source-delete-index-io-failed", operation.ReasonCode);
    }

    [NativeSqlServerFact]
    public async Task Physical_cleanup_resumes_after_native_io_and_a_rolled_back_receipt_batch()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("file-batch-restart", SourceRootState.Deleting, now);
        var operationId = await AddDeletionAsync(source, now);
        var ordinary = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        await AdvanceSqlToFilesAsync(ordinary, operationId);

        using var files = new CleanupBatchFiles();
        var names = new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") };
        await SeedLegacyCleanupBatchAsync(operationId, names, now);
        var paths = names.Select(name => Directory.CreateDirectory(Path.Combine(files.Index, "generations", name)).FullName).ToArray();
        var receiptCommands = new PhysicalReceiptCommands();
        var factory = new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString).AddInterceptors(receiptCommands, new CancelPhysicalReceiptCommit(receiptCommands)).Options);
        var physicalStore = new WindowsSourceDeletionFileStore(files.Retained, files.Index);
        var interrupted = new SourceDeletionCoordinator(new SqlSourceDeletionStore(factory, TimeProvider.System),
            fileStore: physicalStore, queryCleanupGate: new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System));

        await Assert.ThrowsAsync<OperationCanceledException>(() => interrupted.RunOneAsync(default).AsTask());
        Assert.All(paths, path => Assert.False(Directory.Exists(path)));
        await using (var rolledBack = CreateContext())
        {
            Assert.Equal(2, await rolledBack.SourceDeletionCleanupItems.CountAsync(row => row.State == 0));
            var claimed = await rolledBack.SourceDeletionOperations.SingleAsync(row => row.Id == operationId);
            Assert.Equal("cleanup-files", claimed.Phase);
            claimed.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            await rolledBack.SaveChangesAsync();
        }

        var restarted = new SourceDeletionCoordinator(ordinary, fileStore: physicalStore,
            queryCleanupGate: new SqlDerivedIndexRecoveryStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System));
        Assert.True(await RunDeletionUntilStoppedAsync(restarted));
        await using var check = CreateContext();
        Assert.Equal("completed", await check.SourceDeletionOperations.Where(row => row.Id == operationId).Select(row => row.Phase).SingleAsync());
        Assert.Empty(await check.SourceDeletionCleanupItems.ToArrayAsync());
    }


    [NativeSqlServerFact]
    public async Task Physical_cleanup_leaves_the_suffix_pending_when_the_native_gate_session_is_lost()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("file-batch-gate-loss", SourceRootState.Deleting, now);
        var operationId = await AddDeletionAsync(source, now);
        using var files = new CleanupBatchFiles();
        var names = new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") };
        var paths = names.Select(name => Directory.CreateDirectory(Path.Combine(files.Index, "generations", name)).FullName).ToArray();
        var connection = new SqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            ApplicationName = $"FluxKnowledge.FileCleanupGateLoss.{Guid.NewGuid():N}"
        };
        var factory = new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection.ConnectionString).Options);
        var store = new SqlSourceDeletionStore(factory, TimeProvider.System);
        var gate = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);
        await AdvanceSqlToFilesAsync(store, operationId);
        await SeedLegacyCleanupBatchAsync(operationId, names, now);
        using var probe = new PhysicalRoundTripProbe(connection.ApplicationName);
        IDerivedIndexRecoveryLease? reader = null;
        var native = new WindowsSourceDeletionFileStore(files.Retained, files.Index);
        var interrupt = new AfterFirstFileDeletion(native, async ct =>
        {
            await probe.ActiveLegacyGate.CloseAsync();
            reader = await gate.TryAcquireSharedLeaseAsync(TimeSpan.Zero, ct);
            Assert.NotNull(reader);
        });
        try
        {
            await new SourceDeletionCoordinator(store, fileStore: interrupt, queryCleanupGate: gate,
                timeProvider: new FrozenBatchTimer()).RunOneAsync(default);
            await using var check = CreateContext();
            Assert.Single(paths, Directory.Exists);
            Assert.Equal(new[] { 0, 1 }, await check.SourceDeletionCleanupItems.OrderBy(row => row.State).Select(row => row.State).ToArrayAsync());
            Assert.Equal("source-delete-search-queries-active",
                await check.SourceDeletionOperations.Where(row => row.Id == operationId).Select(row => row.ReasonCode).SingleAsync());
        }
        finally
        {
            if (reader is not null) await reader.DisposeAsync();
        }

        Assert.True(await RunDeletionUntilStoppedAsync(new SourceDeletionCoordinator(store, fileStore: native, queryCleanupGate: gate)));
        Assert.All(paths, path => Assert.False(Directory.Exists(path)));
    }

    private sealed class FrozenBatchTimer : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }

    private sealed class AfterFirstFileDeletion(ISourceDeletionFileStore inner, Func<CancellationToken, Task> after)
        : ISourceDeletionFileStore
    {
        private bool _first = true;

        public async ValueTask<SourceDeletionFileResult> DeleteAsync(SourceDeletionFileTarget target, CancellationToken cancellationToken)
        {
            var result = await inner.DeleteAsync(target, cancellationToken);
            if (_first)
            {
                _first = false;
                await after(cancellationToken);
            }
            return result;
        }
    }

    private async Task SeedLegacyCleanupBatchAsync(Guid operationId, string[] names, DateTimeOffset now)
    {
        await using var seed = CreateContext();
        seed.SourceDeletionCleanupItems.AddRange(names.Select(name => new SourceDeletionCleanupItemEntity
        {
            Id = Guid.NewGuid(), SourceDeletionOperationId = operationId, StorageKind = 2, RelativePath = name,
            CreatedAtUtc = now, UpdatedAtUtc = now
        }));
        await seed.SaveChangesAsync();
    }

    private sealed class CancelPhysicalReceiptCommit(PhysicalReceiptCommands commands) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            commands.ReceiptWrites > 0
                ? throw new OperationCanceledException("Synthetic interruption before the physical receipt batch commits.")
                : ValueTask.FromResult(result);
    }

    private sealed class PhysicalRoundTripProbe : IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _applicationName;
        private readonly IDisposable _allListeners;
        private readonly ConcurrentBag<IDisposable> _subscriptions = [];
        private readonly ConcurrentDictionary<SqlConnection, bool> _connections = new();

        public PhysicalRoundTripProbe(string applicationName)
        {
            _applicationName = applicationName;
            _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public long RoundTrips => _connections.Keys.Sum(connection => (long)(connection.RetrieveStatistics()["ServerRoundtrips"] ?? 0L));
        public int LegacyGateConnections => _connections.Count(pair => pair.Value);
        public SqlConnection ActiveLegacyGate => _connections.Single(pair => pair.Value && pair.Key.State == System.Data.ConnectionState.Open).Key;

        public void OnNext(DiagnosticListener listener) =>
            _subscriptions.Add(listener.Subscribe(this, name => name == SqlClientConnectionOpenBefore.Name));

        public void OnNext(KeyValuePair<string, object?> entry)
        {
            if (entry.Value is not SqlClientConnectionOpenBefore opening || opening.Connection is not { } connection) return;
            var settings = new SqlConnectionStringBuilder(connection.ConnectionString);
            if (settings.ApplicationName != _applicationName) return;
            connection.StatisticsEnabled = true;
            _connections.TryAdd(connection, !settings.Pooling);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }

        public void Dispose()
        {
            _allListeners.Dispose();
            foreach (var subscription in _subscriptions) subscription.Dispose();
        }
    }

    private sealed class PhysicalReceiptCommands : DbCommandInterceptor
    {
        public int ReceiptWrites { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE [SourceDeletionCleanupItems]", StringComparison.Ordinal))
                ReceiptWrites++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CleanupBatchFiles : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("fk-source-cleanup-");

        public string Retained => Directory.CreateDirectory(Path.Combine(_root.FullName, "retained")).FullName;
        public string Index => Directory.CreateDirectory(Path.Combine(_root.FullName, "index")).FullName;

        public void Dispose() => _root.Delete(recursive: true);
    }
}
