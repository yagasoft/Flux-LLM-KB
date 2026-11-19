using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
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

    [NativeSqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Older_native_or_legacy_staging_seals_require_a_fresh_native_audit(int previousVersion)
    {
        await InitialiseAsync();
        await Store().AuditAsync(Recovery, Index, default);
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        await db.IndexRecoveryAudit.ExecuteUpdateAsync(set => set.SetProperty(value => value.ClassifierVersion, previousVersion));
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => SqlIndexRecoveryReferences.RequireCurrentAsync(db, Recovery, Index, default));
            Assert.Equal("segment-retirement-legacy-live-proof-required", failure.Message);
            await transaction.RollbackAsync();
        }
        await Store().AuditAsync(Recovery, Index, default);
        await using var current = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await SqlIndexRecoveryReferences.RequireCurrentAsync(db, Recovery, Index, default);
        await current.CommitAsync();
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

    [NativeSqlServerFact]
    public async Task Superseded_archive_retirement_reconciles_only_the_bound_exited_writers_and_seals_the_retained_release()
    {
        await InitialiseAsync();
        var owner = new RetirementOwner(); var plan = await RetirementPlanAsync(owner);
        var store = new SqlIndexRecoveryReferences(SqlTestData.CreateFactory(fixture), TimeProvider.System, owner);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.AuditAsync(Recovery, Index, default));
        var audit = await store.RetireSupersededArchivesAsync(plan, default);
        Assert.Equal(1, audit.PacketCount); Assert.Empty(audit.Pins);
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.True((await db.IndexRecoveryAudit.SingleAsync()).IsValid);
        Assert.Empty(await db.IndexRecoveryWriters.ToArrayAsync());
        Assert.True(Directory.Exists(Path.Combine(plan.QuarantineRoot, plan.Moves[0].RelativePath)));
        Assert.True(File.Exists(plan.ApplicationEntry));
        var repeated = await store.RetireSupersededArchivesAsync(plan, default);
        Assert.Equal(audit.InventoryChecksum, repeated.InventoryChecksum);
    }

    [NativeSqlServerTheory]
    [InlineData("alive")]
    [InlineData("unknown")]
    [InlineData("writer-drift")]
    [InlineData("publication-drift")]
    [InlineData("database-drift")]
    [InlineData("unfinished-rebuild")]
    public async Task Retirement_requires_exact_exited_owners_and_a_current_database_publication(string fault)
    {
        await InitialiseAsync();
        var owner = new RetirementOwner(); var plan = await RetirementPlanAsync(owner);
        if (fault == "alive") owner.WriterObservation = GpuInteractiveOwnerObservation.Alive;
        if (fault == "unknown") owner.WriterObservation = GpuInteractiveOwnerObservation.Unknown;
        if (fault == "writer-drift") plan = plan with { Writers = [] };
        if (fault == "publication-drift") plan = plan with { CorpusVersion = plan.CorpusVersion + 1 };
        if (fault == "database-drift") plan = plan with { DatabaseIncarnation = Guid.NewGuid() };
        if (fault == "unfinished-rebuild")
        {
            await using var edit = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
            edit.CorpusRebuildOperations.Add(new() { Id = Guid.NewGuid(), TargetEpoch = Guid.NewGuid(),
                ManifestHash = new('a', 64), ManifestJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow });
            await edit.SaveChangesAsync();
        }
        var store = new SqlIndexRecoveryReferences(SqlTestData.CreateFactory(fixture), TimeProvider.System, owner);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => await store.RetireSupersededArchivesAsync(plan, default));
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.Single(await db.IndexRecoveryWriters.ToArrayAsync());
        Assert.False((await db.IndexRecoveryAudit.SingleAsync()).IsValid);
        Assert.True(Directory.Exists(Path.Combine(Recovery, plan.Moves[0].RelativePath)));
    }

    [NativeSqlServerFact]
    public async Task An_interrupted_retirement_keeps_the_audit_invalid_and_writers_until_a_resumed_native_seal()
    {
        await InitialiseAsync();
        var owner = new RetirementOwner(); var plan = await RetirementPlanAsync(owner);
        var failing = new SqlIndexRecoveryReferences(SqlTestData.CreateFactory(fixture), TimeProvider.System, owner)
        { AfterCapture = _ => throw new IOException("simulated loss after archive renames") };
        await Assert.ThrowsAsync<IOException>(async () => await failing.RetireSupersededArchivesAsync(plan, default));
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.Single(await db.IndexRecoveryWriters.ToArrayAsync());
        Assert.False((await db.IndexRecoveryAudit.AsNoTracking().SingleAsync()).IsValid);
        Assert.True(Directory.Exists(Path.Combine(plan.QuarantineRoot, plan.Moves[0].RelativePath)));
        var resumed = new SqlIndexRecoveryReferences(SqlTestData.CreateFactory(fixture), TimeProvider.System, owner);
        await resumed.RetireSupersededArchivesAsync(plan, default);
        Assert.True((await db.IndexRecoveryAudit.AsNoTracking().SingleAsync()).IsValid);
        Assert.Empty(await db.IndexRecoveryWriters.AsNoTracking().ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task The_real_privileged_cli_retires_a_disposable_archive_and_replays_its_confirmed_seal()
    {
        await InitialiseAsync();
        var probe = new WindowsInteractiveGpuOwnerProbe();
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add("Start-Sleep -Milliseconds 300");
        using var exited = Process.Start(start)!;
        var formerOwner = new GpuInteractiveOwnerIdentity(exited.Id, new DateTimeOffset(exited.StartTime.ToUniversalTime()), probe.Current.MachineFingerprint);
        await exited.WaitForExitAsync();
        var plan = await RetirementPlanAsync(new RetirementOwner(formerOwner, probe.Current));
        var current = plan.RetainedPackets.Single();
        plan = plan with { RetainedPackets = [], Moves = [.. plan.Moves,
            new(current, PhysicalFileIdentity.GetDirectory(Path.Combine(plan.RecoveryRoot, current)).IdentityFingerprint)],
            RuntimeSnapshots = [new(Path.GetDirectoryName(plan.ApplicationEntry)!,
                [new(Path.GetFileName(plan.ApplicationEntry), new FileInfo(plan.ApplicationEntry).Length, plan.ApplicationSha256)])] };
        var path = Path.Combine(_root, "retirement-plan.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan); await File.WriteAllBytesAsync(path, bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        for (var replay = 0; replay < 2; replay++)
        {
            var cli = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            cli.ArgumentList.Add(typeof(FluxKnowledge.Cli.Commands.IndexRecoveryCommand).Assembly.Location);
            foreach (var argument in new[] { "index-recovery", "retire-archives", "--plan", path, "--plan-sha256", hash }) cli.ArgumentList.Add(argument);
            cli.Environment["ConnectionStrings__FluxKnowledge"] = fixture.ConnectionString;
            using var child = Process.Start(cli)!;
            try
            {
                var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                var stdout = await output; var stderr = await error;
                Assert.True(child.ExitCode == 0, stderr); Assert.Equal("", stderr);
                using var result = JsonDocument.Parse(stdout);
                Assert.True(result.RootElement.GetProperty("SqlAuditSealed").GetBoolean());
            }
            finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
        }
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.True((await db.IndexRecoveryAudit.AsNoTracking().SingleAsync()).IsValid);
        Assert.Empty(await db.IndexRecoveryWriters.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task A_zero_writer_disposition_resumes_after_its_last_rename_and_refuses_a_different_valid_seal()
    {
        await InitialiseAsync(); var owner = new RetirementOwner(); var plan = await RetirementPlanAsync(owner, createWriter: false);
        var failing = new SqlIndexRecoveryReferences(SqlTestData.CreateFactory(fixture), TimeProvider.System, owner)
        { AfterCapture = _ => throw new IOException("pre-commit interruption") };
        await Assert.ThrowsAsync<IOException>(async () => await failing.RetireSupersededArchivesAsync(plan, default));
        var store = new SqlIndexRecoveryReferences(SqlTestData.CreateFactory(fixture), TimeProvider.System, owner);
        await store.RetireSupersededArchivesAsync(plan, default);
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        var seal = await db.IndexRecoveryAudit.SingleAsync(); Assert.True(seal.IsValid);
        seal.InventoryChecksum = new('f', 64); await db.SaveChangesAsync();
        Assert.Equal("index-recovery-retirement-completion-unconfirmed",
            (await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.RetireSupersededArchivesAsync(plan, default))).Message);
    }

    [NativeSqlServerFact]
    public async Task An_archive_consumer_started_after_preparation_prevents_the_first_rename()
    {
        await InitialiseAsync(); var owner = new RetirementOwner(); var plan = await RetirementPlanAsync(owner);
        var script = Path.Combine(_root, "consumer.ps1");
        await File.WriteAllTextAsync(script, "param([string]$Archive)\nStart-Sleep -Seconds 30\n");
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-File", script, "-Archive", Path.Combine(Recovery, plan.Moves[0].RelativePath) }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        try
        {
            var store = new SqlIndexRecoveryReferences(SqlTestData.CreateFactory(fixture), TimeProvider.System, owner);
            Assert.Equal("index-recovery-retirement-live-consumer-or-observation-failed",
                (await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.RetireSupersededArchivesAsync(plan, default))).Message);
            Assert.True(Directory.Exists(Path.Combine(Recovery, plan.Moves[0].RelativePath)));
        }
        finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); }
    }

    private async Task<RecoveryArchiveRetirementPlan> RetirementPlanAsync(RetirementOwner owner, bool createWriter = true)
    {
        var old = Path.Combine(Recovery, "IncrementalUpdates", "20261022T211724Z-cbb3ec8eacf3");
        var current = Path.Combine(Recovery, "IncrementalUpdates", "20261115T121345Z-64e7e17c1dc0");
        var app = Path.Combine(_root, "App"); var quarantine = Path.Combine(_root, "Quarantine");
        Directory.CreateDirectory(old); Directory.CreateDirectory(current); Directory.CreateDirectory(app); Directory.CreateDirectory(quarantine);
        File.WriteAllText(Path.Combine(old, "unrecognised-old-interrupted-packet.json"), "{}");
        File.WriteAllText(Path.Combine(current, "manifest.json"), JsonSerializer.Serialize(new {
            commit = new string('a', 40), staged_at_utc = DateTimeOffset.UtcNow, application_root = app,
            migrations = false, clean_slate = false, vector_publication_metadata = (object?)null }));
        var entry = Path.Combine(app, "FluxKnowledge.Web.dll"); File.WriteAllText(entry, "synthetic app, never executed");
        Directory.CreateDirectory(Path.Combine(current, "candidate"));
        File.Copy(entry, Path.Combine(current, "candidate", "FluxKnowledge.Web.dll"));
        var store = new SqlIndexRecoveryReferences(SqlTestData.CreateFactory(fixture), TimeProvider.System, owner);
        if (createWriter) { await using var writer = await store.BeginWriteAsync(Recovery, default); }
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        var state = await db.IndexState.SingleAsync(value => value.Id == 1);
        if (state.CorpusEpoch == Guid.Empty) { state.CorpusEpoch = Guid.NewGuid(); await db.SaveChangesAsync(); }
        var rows = await db.IndexRecoveryWriters.AsNoTracking().ToArrayAsync();
        await db.Database.OpenConnectionAsync();
        return new(1, Guid.NewGuid(), await SqlIndexRecoveryReferences.DatabaseIdentityAsync(db, default), Recovery, Index,
            entry, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(entry))),
            Path.GetRelativePath(Recovery, Path.Combine(current, "candidate", "FluxKnowledge.Web.dll")), quarantine,
            PhysicalFileIdentity.GetDirectory(Recovery).IdentityFingerprint, state.CorpusEpoch, state.CorpusVersion,
            state.ActiveIndexGenerationId, null, null, [owner.Service], rows.Select(row => new RecoveryWriterDisposition(row.Id,
                row.RecoveryRoot, row.OwnerProcessId, row.OwnerStartedAtUtc, row.OwnerMachineFingerprint,
                row.OwnerExitSufficient, row.CreatedAtUtc)).ToArray(),
            [new(Path.GetRelativePath(Recovery, old), PhysicalFileIdentity.GetDirectory(old).IdentityFingerprint)],
            [Path.GetRelativePath(Recovery, current)]);
    }

    private sealed class RetirementOwner(GpuInteractiveOwnerIdentity? current = null, GpuInteractiveOwnerIdentity? service = null) : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current { get; } = current ?? new WindowsInteractiveGpuOwnerProbe().Current;
        public GpuInteractiveOwnerIdentity Service => service ?? Current with { ProcessId = Current.ProcessId + 1 };
        public GpuInteractiveOwnerObservation WriterObservation { get; set; } = GpuInteractiveOwnerObservation.Exited;
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity value) =>
            value == Service ? GpuInteractiveOwnerObservation.Alive : WriterObservation;
    }

    private sealed class ControlledOwner : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current { get; } = new WindowsInteractiveGpuOwnerProbe().Current;
        public GpuInteractiveOwnerObservation Observation { get; set; } = GpuInteractiveOwnerObservation.Alive;
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner) => Observation;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
