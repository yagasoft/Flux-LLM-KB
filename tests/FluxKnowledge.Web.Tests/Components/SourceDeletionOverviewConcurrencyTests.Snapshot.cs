using System.Data.Common;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class SourceDeletionOverviewConcurrencyTests
{
    [NativeSqlServerTheory]
    [InlineData("claim")]
    [InlineData("receipt")]
    [InlineData("root_delete")]
    [InlineData("purge")]
    public async Task Snapshot_reader_allows_real_deletion_writer_to_commit_and_retains_the_earlier_image(string kind)
    {
        await SeedAsync(kind);
        await EnableDeletionSnapshotAsync();
        var pause = new SnapshotReadPause();
        var factory = new Factory(fixture.ConnectionString, pause);
        var reader = new SqlProjectionReader(factory, new RecoveryStatus(), new SqlGpuSchedulerStore(factory), readOptions: new(true));
        var read = reader.ReadOverviewAsync(default).AsTask();
        try
        {
            await pause.Reached.Task.WaitAsync(Deadline);
            // Completion proves that the overview does not retain publication/operation writer locks.
            await WriteAsync(kind, new Factory(fixture.ConnectionString)).WaitAsync(TimeSpan.FromSeconds(1));
            await AssertWriterCommittedAsync(kind);
        }
        finally { pause.Release.TrySetResult(); }
        var earlier = await read.WaitAsync(Deadline);
        Assert.True(earlier.Activity.IsConsistent);
        Assert.Equal("Enabled", Assert.Single(earlier.Activity.Sources, s => s.Id == _publicRoot).Lifecycle);
        AssertEarlierDeletionImage(earlier, kind);
        var currentFactory = new Factory(fixture.ConnectionString);
        var current = await new SqlProjectionReader(currentFactory, new RecoveryStatus(), new SqlGpuSchedulerStore(currentFactory),
            readOptions: new(true)).ReadOverviewAsync(default);
        Assert.True(current.Activity.IsConsistent);
        if (kind == "root_delete") Assert.Equal("Deleting", Assert.Single(current.Activity.Sources, s => s.Id == _publicRoot).Lifecycle);
        else AssertCommittedDeletionImage(current, kind);
    }

    [NativeSqlServerTheory]
    [InlineData("claim")]
    [InlineData("receipt")]
    [InlineData("root_delete")]
    [InlineData("purge")]
    public async Task Snapshot_reader_never_observes_the_uncommitted_deletion_writer(string kind)
    {
        await SeedAsync(kind);
        await EnableDeletionSnapshotAsync();
        var writerPause = new CommitPause(output);
        var write = WriteAsync(kind, new Factory(fixture.ConnectionString, writerPause));
        try
        {
            await writerPause.Reached.Task.WaitAsync(Deadline);
            var factory = new Factory(fixture.ConnectionString);
            var earlier = await new SqlProjectionReader(factory, new RecoveryStatus(), new SqlGpuSchedulerStore(factory),
                readOptions: new(true)).ReadOverviewAsync(default);
            Assert.True(earlier.Activity.IsConsistent);
            Assert.Equal("Enabled", Assert.Single(earlier.Activity.Sources, s => s.Id == _publicRoot).Lifecycle);
            AssertEarlierDeletionImage(earlier, kind);
        }
        finally { writerPause.Release.TrySetResult(); }
        await write.WaitAsync(Deadline);
        await AssertWriterCommittedAsync(kind);
        var currentFactory = new Factory(fixture.ConnectionString);
        var current = await new SqlProjectionReader(currentFactory, new RecoveryStatus(), new SqlGpuSchedulerStore(currentFactory),
            readOptions: new(true)).ReadOverviewAsync(default);
        if (kind == "root_delete") Assert.Equal("Deleting", Assert.Single(current.Activity.Sources, s => s.Id == _publicRoot).Lifecycle);
        else AssertCommittedDeletionImage(current, kind);
    }

    private void AssertEarlierDeletionImage(FluxKnowledge.Application.Contracts.OverviewProjection sample, string kind)
    {
        var source = Assert.Single(sample.Activity.Sources, s => s.Id == _root);
        Assert.Equal("Deleting", source.Lifecycle);
        Assert.Equal(kind == "purge" ? "purge-records" : "cleanup-files", source.LifecyclePhase);
        Assert.Equal(kind is "receipt" or "purge" ? 1 : 0, source.ActiveSourceOperations);
    }

    private void AssertCommittedDeletionImage(FluxKnowledge.Application.Contracts.OverviewProjection sample, string kind)
    {
        var source = Assert.Single(sample.Activity.Sources, s => s.Id == _root);
        Assert.Equal("cleanup-files", source.LifecyclePhase);
        Assert.Equal(kind == "claim" ? 1 : 0, source.ActiveSourceOperations);
    }

    private async Task EnableDeletionSnapshotAsync()
    {
        Assert.Matches("^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$", fixture.DatabaseName);
        await using var context = new Factory(fixture.ConnectionString).CreateDbContext();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"ALTER DATABASE [{fixture.DatabaseName}] SET ALLOW_SNAPSHOT_ISOLATION ON";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class SnapshotReadPause : DbCommandInterceptor
    {
        private bool _countsConsumed;
        private bool _paused;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            if (command.CommandText.Contains("GROUP BY [j].[PublicState]", StringComparison.Ordinal)) _countsConsumed = true;
            return result;
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (_countsConsumed && !_paused)
            {
                _paused = true;
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
