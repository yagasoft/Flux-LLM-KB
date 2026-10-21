using System.Data;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class SqlIndexRecoveryReferenceTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeSqlRecovery_" + Guid.NewGuid().ToString("N"));
    private string Recovery => Path.Combine(_root, "Recovery");
    private string Index => Path.Combine(_root, "Index");
    private readonly ControlledOwner _owner = new();
    private SqlIndexRecoveryReferences Store() => new(SqlTestData.CreateFactory(fixture), TimeProvider.System, _owner);

    private async Task InitialiseAsync()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        Directory.CreateDirectory(Recovery); Directory.CreateDirectory(Index);
    }

    [NativeSqlServerFact]
    public async Task A_successful_seal_hands_off_its_lock_before_the_original_connection_is_disposed()
    {
        await InitialiseAsync();
        var observed = false;
        var store = new SqlIndexRecoveryReferences(SqlTestData.CreateFactory(fixture), TimeProvider.System, _owner)
        {
            AfterAuditCommit = async (original, token) =>
            {
                Assert.Equal(ConnectionState.Open, original.State);
                await using var next = new SqlConnection(new SqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false }.ConnectionString);
                await next.OpenAsync(token);
                await using var command = next.CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'FluxKnowledge.RecoveryReferences',@LockMode=N'Shared',@LockOwner=N'Session',@LockTimeout=0; SELECT @r;";
                var result = (int)(await command.ExecuteScalarAsync(token))!;
                Assert.True(result >= 0, $"A sealed audit still holds its session-exclusive lock: {result}.");
                command.CommandText = "DECLARE @r int; EXEC @r=sys.sp_releaseapplock @Resource=N'FluxKnowledge.RecoveryReferences',@LockOwner=N'Session'; SELECT @r;";
                Assert.Equal(0, (int)(await command.ExecuteScalarAsync(token))!);
                Assert.Equal(ConnectionState.Open, original.State);
                await using var context = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync(token);
                Assert.True((await context.IndexRecoveryAudit.AsNoTracking().SingleAsync(value => value.Id == 1, token)).IsValid);
                observed = true;
            }
        };
        await store.AuditAsync(Recovery, Index, CancellationToken.None);
        Assert.True(observed);
    }

    [NativeSqlServerFact]
    public async Task A_sealed_audit_is_repeatable_and_bound_to_the_exact_configured_root()
    {
        await InitialiseAsync();
        var first = await Store().AuditAsync(Recovery, Index, CancellationToken.None);
        var second = await Store().AuditAsync(Recovery, Index, CancellationToken.None);
        Assert.Equal(first.InventoryChecksum, second.InventoryChecksum);
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await SqlIndexRecoveryReferences.RequireCurrentAsync(db, Recovery, Index, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqlIndexRecoveryReferences.RequireCurrentAsync(db, Recovery + "-foreign", Index, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqlIndexRecoveryReferences.RequireCurrentAsync(db, Recovery, Index + "-foreign", CancellationToken.None));
        Assert.Empty(await db.IndexRecoveryWriters.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task A_writer_invalidates_before_filesystem_work_and_excludes_sealing_until_it_finishes()
    {
        await InitialiseAsync();
        await Store().AuditAsync(Recovery, Index, CancellationToken.None);
        await using var writer = await Store().BeginWriteAsync(Recovery, CancellationToken.None);
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.False((await db.IndexRecoveryAudit.AsNoTracking().SingleAsync()).IsValid);
        Assert.Single(await db.IndexRecoveryWriters.ToListAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Store().AuditAsync(Recovery, Index, timeout.Token));
        await writer.CompleteAsync(CancellationToken.None);
        await writer.DisposeAsync();
        await Store().AuditAsync(Recovery, Index, CancellationToken.None);
        Assert.True((await db.IndexRecoveryAudit.AsNoTracking().SingleAsync()).IsValid);
        Assert.Empty(await db.IndexRecoveryWriters.ToListAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_session_before_a_protocol_transaction_cannot_reopen_and_mutate_without_its_lock(bool audit)
    {
        await InitialiseAsync();
        await Store().AuditAsync(Recovery, Index, CancellationToken.None);
        var faulted = new SqlIndexRecoveryReferences(SqlTestData.CreateFactory(fixture), TimeProvider.System, _owner)
        { BeforeProtocolTransaction = async (connection, _) => await connection.CloseAsync() };
        if (audit)
            await Assert.ThrowsAnyAsync<Exception>(async () => await faulted.AuditAsync(Recovery, Index, CancellationToken.None));
        else
            await Assert.ThrowsAnyAsync<Exception>(async () => { await using var writer = await faulted.BeginWriteAsync(Recovery, CancellationToken.None); });
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.Empty(await db.IndexRecoveryWriters.ToArrayAsync());
        Assert.Equal(!audit, (await db.IndexRecoveryAudit.AsNoTracking().SingleAsync()).IsValid);
    }

    [NativeSqlServerTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Lost_session_after_transaction_binding_cannot_commit_on_a_replacement_connection(bool audit, bool beforeFirstCommand)
    {
        await InitialiseAsync();
        await Store().AuditAsync(Recovery, Index, CancellationToken.None);
        var faulted = new SqlIndexRecoveryReferences(SqlTestData.CreateFactory(fixture), TimeProvider.System, _owner)
        {
            BeforeProtocolSave = beforeFirstCommand ? null : async (connection, _) => await connection.CloseAsync(),
            AfterProtocolTransaction = beforeFirstCommand ? async (connection, _) => await connection.CloseAsync() : null
        };
        if (audit)
            await Assert.ThrowsAnyAsync<Exception>(async () => await faulted.AuditAsync(Recovery, Index, CancellationToken.None));
        else
            await Assert.ThrowsAnyAsync<Exception>(async () => { await using var writer = await faulted.BeginWriteAsync(Recovery, CancellationToken.None); });
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.Empty(await db.IndexRecoveryWriters.ToArrayAsync());
        Assert.Equal(!audit, (await db.IndexRecoveryAudit.AsNoTracking().SingleAsync()).IsValid);
    }

    [NativeSqlServerFact]
    public async Task Session_end_without_completion_retains_writer_proof_until_physical_process_exit_is_known()
    {
        await InitialiseAsync();
        await Store().AuditAsync(Recovery, Index, CancellationToken.None);
        await using var writer = await Store().BeginWriteAsync(Recovery, CancellationToken.None, ownerExitSufficient: true);
        await writer.DisposeAsync();
        foreach (var observation in new[] { GpuInteractiveOwnerObservation.Alive, GpuInteractiveOwnerObservation.Unknown })
        {
            _owner.Observation = observation;
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Store().AuditAsync(Recovery, Index, CancellationToken.None));
            Assert.Equal("index-recovery-writer-exit-not-proven", refused.Message);
        }
        _owner.Observation = GpuInteractiveOwnerObservation.Exited;
        await Store().AuditAsync(Recovery, Index, CancellationToken.None);
        await using var check = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.Empty(await check.IndexRecoveryWriters.ToListAsync());
        Assert.True((await check.IndexRecoveryAudit.SingleAsync()).IsValid);
    }

    [NativeSqlServerFact]
    public async Task Retirement_read_ownership_blocks_writer_invalidation_until_its_transaction_ends()
    {
        await InitialiseAsync();
        await Store().AuditAsync(Recovery, Index, CancellationToken.None);
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await SqlIndexRecoveryReferences.RequireCurrentAsync(db, Recovery, Index, CancellationToken.None);
        var pending = Store().BeginWriteAsync(Recovery, CancellationToken.None).AsTask();
        await Task.Delay(200);
        Assert.False(pending.IsCompleted);
        await transaction.CommitAsync();
        await using var writer = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await db.IndexRecoveryAudit.AsNoTracking().SingleAsync()).IsValid);
        await writer.CompleteAsync(CancellationToken.None);
    }

    [NativeSqlServerFact]
    public async Task Unknown_metadata_leaves_a_current_path_reason_and_never_seals_a_partial_inventory()
    {
        await InitialiseAsync();
        await Store().AuditAsync(Recovery, Index, CancellationToken.None);
        await using (var writer = await Store().BeginWriteAsync(Recovery, CancellationToken.None))
        {
            await File.WriteAllTextAsync(Path.Combine(Recovery, "unknown-reference.json"), "{}");
            await writer.CompleteAsync(CancellationToken.None);
        }
        var refused = await Assert.ThrowsAsync<RecoveryIndexAuditException>(async () => await Store().AuditAsync(Recovery, Index, CancellationToken.None));
        Assert.Equal("unknown-reference.json", refused.RelativePath);
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        var audit = await db.IndexRecoveryAudit.SingleAsync();
        Assert.False(audit.IsValid);
        Assert.Equal("unknown-reference.json", audit.FailurePath);
    }

    [NativeSqlServerFact]
    public async Task A_writer_racing_a_completed_capture_cannot_leave_the_sealed_audit_current()
    {
        await InitialiseAsync();
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var auditor = new SqlIndexRecoveryReferences(SqlTestData.CreateFactory(fixture), TimeProvider.System, _owner)
        { AfterCapture = async token => { captured.SetResult(); await resume.Task.WaitAsync(token); } };
        var auditing = auditor.AuditAsync(Recovery, Index, CancellationToken.None).AsTask();
        await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var writing = Store().BeginWriteAsync(Recovery, CancellationToken.None).AsTask();
        try { await Task.Delay(150); Assert.False(writing.IsCompleted); }
        finally { resume.TrySetResult(); }
        await auditing.WaitAsync(TimeSpan.FromSeconds(5));
        await using var writer = await writing.WaitAsync(TimeSpan.FromSeconds(5));
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.False((await db.IndexRecoveryAudit.AsNoTracking().SingleAsync()).IsValid);
        await writer.CompleteAsync(CancellationToken.None);
    }

    [NativeSqlServerFact]
    public async Task Resealing_replaces_exact_external_pins_and_removes_superseded_failure_information()
    {
        await InitialiseAsync();
        var packet = Path.Combine(Recovery, "IncrementalUpdates", "20261020T111743Z-61b5e452b29f");
        Directory.CreateDirectory(packet);
        var generation = Guid.NewGuid(); var native = Path.Combine(Index, "generations", generation.ToString("N"));
        RecoveryIndexReferenceAuditTests.WriteRebuild(packet, generation, native);
        await Store().AuditAsync(Recovery, Index, CancellationToken.None);
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        var pin = Assert.Single(await db.IndexRecoveryPins.AsNoTracking().ToArrayAsync());
        Assert.Equal(generation, pin.GenerationId); Assert.Equal(native, pin.IndexPath);
        await using (var writer = await Store().BeginWriteAsync(Recovery, CancellationToken.None))
        {
            File.Delete(Path.Combine(packet, "corpus-rebuild-manifest.json"));
            await File.WriteAllTextAsync(Path.Combine(packet, "unknown.json"), "{}");
            await writer.CompleteAsync(CancellationToken.None);
        }
        await Assert.ThrowsAsync<RecoveryIndexAuditException>(async () => await Store().AuditAsync(Recovery, Index, CancellationToken.None));
        Assert.NotNull((await db.IndexRecoveryAudit.AsNoTracking().SingleAsync()).FailurePath);
        await using (var writer = await Store().BeginWriteAsync(Recovery, CancellationToken.None))
        { File.Delete(Path.Combine(packet, "unknown.json")); await writer.CompleteAsync(CancellationToken.None); }
        await Store().AuditAsync(Recovery, Index, CancellationToken.None);
        Assert.Empty(await db.IndexRecoveryPins.ToArrayAsync());
        var audit = await db.IndexRecoveryAudit.AsNoTracking().SingleAsync();
        Assert.True(audit.IsValid); Assert.Null(audit.FailurePath);
    }

    private sealed class ControlledOwner : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current { get; } = new WindowsInteractiveGpuOwnerProbe().Current;
        public GpuInteractiveOwnerObservation Observation { get; set; } = GpuInteractiveOwnerObservation.Alive;
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner) => Observation;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
