using System.Data.Common;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class PipelineActivitySqlTests
{
    [NativeSqlServerTheory]
    [InlineData("owner", true)]
    [InlineData("generation", true)]
    [InlineData("slot", true)]
    [InlineData("owner", false)]
    public async Task Snapshot_overview_keeps_one_committed_GPU_image_when_ownership_changes_during_the_read(string change, bool commit)
    {
        await EnableOwnedSnapshotAsync();
        var seed = await SeedGpuReadLockAsync(DateTimeOffset.UtcNow);
        var mutation = new SnapshotAfterCounts(async () =>
        {
            await using var writer = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
            await using var transaction = await writer.Database.BeginTransactionAsync();
            if (change == "owner")
                await writer.GpuExecutorDispatches.Where(d => d.BatchId == seed.Batch)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.OwnerKey, "ChangedOwner"));
            else if (change == "generation")
                await writer.GpuExecutorDispatches.Where(d => d.BatchId == seed.Batch)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.AdmissionGeneration, 8));
            else
                await writer.GpuCapacitySlots.Where(s => s.SlotKey == seed.Slot)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.ActiveBatchId, (Guid?)null));
            if (commit) await transaction.CommitAsync(); else await transaction.RollbackAsync();
        });
        var sample = await SnapshotReader(mutation).ReadOverviewAsync(default);
        Assert.True(mutation.Changed);
        Assert.True(sample.Activity.IsConsistent);
        Assert.Equal(1, PipelineActivityView.From(sample.Activity, seed.Root).ActiveGpuTasks);
        Assert.All(mutation.Connections, c => Assert.Equal(mutation.Connections[0], c));
        Assert.All(mutation.Isolations, isolation => Assert.Equal(System.Data.IsolationLevel.Snapshot, isolation));
        var next = await SnapshotReader().ReadOverviewAsync(default);
        Assert.Equal(commit ? 0 : 1, PipelineActivityView.From(next.Activity, seed.Root).ActiveGpuTasks);
        Assert.Equal(commit ? 1 : 0, PipelineActivityView.From(next.Activity, seed.Root).UnconfirmedGpuTasks);
    }

    [NativeSqlServerFact]
    public async Task Snapshot_overview_displays_the_committed_owner_while_an_uncommitted_owner_change_is_held()
    {
        await EnableOwnedSnapshotAsync();
        var seed = await SeedGpuReadLockAsync(DateTimeOffset.UtcNow);
        await using var writer = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.GpuExecutorDispatches.Where(d => d.BatchId == seed.Batch)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.OwnerKey, "UncommittedOwner"));
        try
        {
            var sample = await SnapshotReader().ReadOverviewAsync(default);
            Assert.True(sample.Activity.IsConsistent);
            Assert.Equal(1, PipelineActivityView.From(sample.Activity, seed.Root).ActiveGpuTasks);
        }
        finally { await transaction.RollbackAsync(); }
    }

    private SqlProjectionReader SnapshotReader(params IInterceptor[] interceptors)
    {
        var factory = new TestDbContextFactory(_fixture.ConnectionString, interceptors);
        return new(factory, new FixedRecoveryStatus(new DerivedIndexRecoverySnapshot(DerivedIndexRecoveryState.Healthy,
            null, null, null, null, 0)), new SqlGpuSchedulerStore(factory), readOptions: new(true));
    }

    private async Task EnableOwnedSnapshotAsync()
    {
        Assert.Matches("^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$", _fixture.DatabaseName);
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"ALTER DATABASE [{_fixture.DatabaseName}] SET ALLOW_SNAPSHOT_ISOLATION ON";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class SnapshotAfterCounts(Func<Task> change) : DbCommandInterceptor
    {
        private bool _countsConsumed;
        public bool Changed { get; private set; }
        public List<DbConnection> Connections { get; } = [];
        public List<System.Data.IsolationLevel?> Isolations { get; } = [];
        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            if (command.CommandText.Contains("GROUP BY [j].[PublicState]", StringComparison.Ordinal)) _countsConsumed = true;
            return result;
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Connections.Add(command.Connection!);
            Isolations.Add(command.Transaction?.IsolationLevel);
            if (_countsConsumed && !Changed) { Changed = true; await change(); }
            return result;
        }
    }
}
