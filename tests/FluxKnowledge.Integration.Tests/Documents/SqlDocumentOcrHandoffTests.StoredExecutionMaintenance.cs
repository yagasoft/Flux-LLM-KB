using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [NativeSqlServerTheory]
    [InlineData("none")]
    [InlineData("source")]
    [InlineData("root-config")]
    [InlineData("owner")]
    [InlineData("execution")]
    [InlineData("native")]
    [InlineData("request-version")]
    [InlineData("task-version")]
    [InlineData("job-version")]
    [InlineData("digest")]
    [InlineData("changed-result")]
    [InlineData("missing-proof")]
    [InlineData("stale-proof")]
    [InlineData("foreign-proof")]
    public async Task Stored_execution_maintenance_requires_the_exact_original_binding_and_fresh_physical_proof(string change)
    {
        var (factory, store, original) = await SeedStoredExecutionMaintenanceAsync();
        var binding = change switch
        {
            "source" => original with { SourceRootId = Guid.NewGuid() },
            "root-config" => original with { RootConfigurationRevision = original.RootConfigurationRevision + 1 },
            "owner" => original with { CapacityOwnerKey = "foreign-owner" },
            "execution" => original with { Lease = original.Lease with { ExecutionInstanceId = Guid.NewGuid() } },
            "native" => original with { NativeProcess = original.NativeProcess with { ProcessId = original.NativeProcess.ProcessId + 1 } },
            "request-version" => original with { RequestRowVersion = new byte[8] },
            "task-version" => original with { TaskRowVersion = new byte[8] },
            "job-version" => original with { JobRowVersion = new byte[8] },
            "digest" => original with { ResultDigest = new byte[32] },
            _ => original
        };
        var proof = new MaintenanceVerifier
        {
            Observation = change == "missing-proof" ? null : new(SyntheticSupervisor(),
                DateTimeOffset.UtcNow.AddSeconds(change == "stale-proof" ? -60 : 0), new string('b', 64))
        };
        if (change == "foreign-proof") proof.Observation = proof.Observation! with
        {
            Verifier = proof.Observation.Verifier with
            { Process = proof.Observation.Verifier.Process with { MachineFingerprint = "foreign-machine" } }
        };
        if (change == "changed-result")
        {
            await using var changed = factory.CreateDbContext();
            var request = await changed.DocumentOcrRequests.SingleAsync();
            request.ResultJson += " ";
            await changed.SaveChangesAsync();
            binding = binding with { RequestRowVersion = request.RowVersion.ToArray() };
        }
        await using var before = factory.CreateDbContext();
        var requestBefore = await before.DocumentOcrRequests.AsNoTracking().SingleAsync();
        var jobBefore = await before.Jobs.AsNoTracking().SingleAsync(value => value.Id == binding.ParentJobId);
        var result = await store.AcceptStoredExecutionCleanupAsync(binding, proof, default);
        Assert.Equal(change == "none", result.Accepted);
        await using var check = factory.CreateDbContext();
        var requestAfter = await check.DocumentOcrRequests.SingleAsync();
        Assert.Equal(requestBefore.ResultJson, requestAfter.ResultJson);
        Assert.Equal(requestBefore.ResultDigest, requestAfter.ResultDigest);
        Assert.Equal(requestBefore.ExecutionInstanceId, requestAfter.ExecutionInstanceId);
        Assert.Equal(requestBefore.SupervisorInstanceId, requestAfter.SupervisorInstanceId);
        Assert.Equal(requestBefore.SupervisorProcessId, requestAfter.SupervisorProcessId);
        Assert.Equal(requestBefore.SupervisorProcessStartedAtUtc, requestAfter.SupervisorProcessStartedAtUtc);
        Assert.Equal(requestBefore.NativeProcessId, requestAfter.NativeProcessId);
        Assert.Equal(requestBefore.NativeProcessStartedAtUtc, requestAfter.NativeProcessStartedAtUtc);
        Assert.Equal(requestBefore.ExecutionClaimedAtUtc, requestAfter.ExecutionClaimedAtUtc);
        Assert.Equal(requestBefore.ExecutionDeadlineAtUtc, requestAfter.ExecutionDeadlineAtUtc);
        Assert.Equal(JsonSerializer.Serialize(jobBefore), JsonSerializer.Serialize(await check.Jobs.SingleAsync(value => value.Id == binding.ParentJobId)));
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, (await check.GpuMiniTasks.SingleAsync()).ExecutionState);
        Assert.Equal((int)GpuCapacitySlotState.Uncertain, (await check.GpuCapacitySlots.SingleAsync()).State);
        if (change != "none")
        {
            Assert.Equal(requestBefore.RowVersion, requestAfter.RowVersion);
            Assert.Empty(await check.GpuExecutorEvidence.ToArrayAsync());
            return;
        }
        Assert.NotNull(requestAfter.NativeCleanupConfirmedAtUtc);
        Assert.Equal((int)DocumentOcrCleanupKind.StoppedMaintenance, requestAfter.CleanupProofKind);
        var calls = proof.Calls;
        proof.Observation = null;
        Assert.Equal(DocumentOcrLifecycleDisposition.AlreadyApplied,
            (await store.AcceptStoredExecutionCleanupAsync(binding, proof, default)).Disposition);
        Assert.True((await store.SettleCompletionAsync(binding.Lease.Handle, binding.Lease.MiniTaskId, binding.ResultDigest, default)).Accepted);
        Assert.Equal(DocumentOcrLifecycleDisposition.AlreadyApplied,
            (await store.AcceptStoredExecutionCleanupAsync(binding, proof, default)).Disposition);
        Assert.True((await store.SettleCompletionAsync(binding.Lease.Handle, binding.Lease.MiniTaskId, binding.ResultDigest, default)).Accepted);
        Assert.Equal(calls, proof.Calls);
    }

    [NativeSqlServerTheory]
    [InlineData("proof")]
    [InlineData("settlement")]
    public async Task Stored_execution_maintenance_recovers_lost_acknowledgements_without_changing_result_or_execution(string fault)
    {
        var (factory, _, binding) = await SeedStoredExecutionMaintenanceAsync();
        var once = 0;
        var store = new SqlDocumentOcrStore(factory, CreateCoordinator(factory), afterLifecycleCommitted: (result, _) =>
        {
            var target = fault == "proof" ? "document-ocr-stored-execution-cleanup-confirmed" : "document-ocr-settled";
            if (result.Disposition == DocumentOcrLifecycleDisposition.Applied && result.ReasonCode == target &&
                Interlocked.Increment(ref once) == 1) throw new TimeoutException("Synthetic committed response loss.");
            return ValueTask.CompletedTask;
        }, sourceAdmission: new FixtureSourceAdmission(factory));
        var proof = new MaintenanceVerifier { Observation = new(SyntheticSupervisor(), DateTimeOffset.UtcNow, new string('b', 64)) };
        if (fault == "proof")
            await AssertStoredExecutionLostResponseAsync(() => store.AcceptStoredExecutionCleanupAsync(binding, proof, default).AsTask());
        else
            Assert.True((await store.AcceptStoredExecutionCleanupAsync(binding, proof, default)).Accepted);
        var calls = proof.Calls;
        proof.Observation = null;
        Assert.Equal(DocumentOcrLifecycleDisposition.AlreadyApplied,
            (await store.AcceptStoredExecutionCleanupAsync(binding, proof, default)).Disposition);
        if (fault == "settlement")
            await AssertStoredExecutionLostResponseAsync(() => store.SettleCompletionAsync(binding.Lease.Handle,
                binding.Lease.MiniTaskId, binding.ResultDigest, default).AsTask());
        Assert.True((await store.SettleCompletionAsync(binding.Lease.Handle, binding.Lease.MiniTaskId, binding.ResultDigest, default)).Accepted);
        Assert.Equal(DocumentOcrLifecycleDisposition.AlreadyApplied,
            (await store.AcceptStoredExecutionCleanupAsync(binding, proof, default)).Disposition);
        Assert.Equal(calls, proof.Calls);
        await using var check = factory.CreateDbContext();
        Assert.Equal(binding.Lease.ExecutionInstanceId, (await check.DocumentOcrRequests.SingleAsync()).ExecutionInstanceId);
        Assert.Equal(binding.ResultDigest, (await check.DocumentOcrRequests.SingleAsync()).ResultDigest);
        Assert.Equal((int)GpuMiniTaskExecutionState.Completed, (await check.GpuMiniTasks.SingleAsync()).ExecutionState);
        Assert.Equal((int)GpuCapacitySlotState.Available, (await check.GpuCapacitySlots.SingleAsync()).State);
        Assert.Single(await check.GpuExecutorEvidence.ToArrayAsync());
        Assert.Single(await check.GpuSchedulerOperationReceipts.Where(value => value.OperationKind == "ocr-settlement").ToArrayAsync());
    }

    private static async Task AssertStoredExecutionLostResponseAsync(Func<Task> action)
    {
        // This fixture disables retries: EF wraps the injected transient timeout,
        // preserving the committed-response failure as its exact inner cause.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(action);
        Assert.IsType<TimeoutException>(error.InnerException);
    }

    [NativeSqlServerFact]
    public async Task Stored_execution_maintenance_refuses_conflicting_cleanup_evidence_and_a_new_operation_after_settlement()
    {
        var (factory, store, binding) = await SeedStoredExecutionMaintenanceAsync();
        var proof = new MaintenanceVerifier { Observation = new(SyntheticSupervisor(), DateTimeOffset.UtcNow, new string('b', 64)) };
        Assert.True((await store.AcceptStoredExecutionCleanupAsync(binding, proof, default)).Accepted);
        Assert.True((await store.SettleCompletionAsync(binding.Lease.Handle, binding.Lease.MiniTaskId, binding.ResultDigest, default)).Accepted);
        Assert.False((await store.AcceptStoredExecutionCleanupAsync(binding with { OperationId = Guid.NewGuid() }, proof, default)).Accepted);
        await using var db = factory.CreateDbContext();
        var evidence = await db.GpuExecutorEvidence.SingleAsync();
        evidence.VerifierKey = "foreign-verifier";
        await db.SaveChangesAsync();
        var version = (await db.DocumentOcrRequests.SingleAsync()).RowVersion.ToArray();
        var calls = proof.Calls;
        Assert.False((await store.AcceptStoredExecutionCleanupAsync(binding, proof, default)).Accepted);
        Assert.Equal(calls, proof.Calls);
        await using var check = factory.CreateDbContext();
        Assert.Equal(version, (await check.DocumentOcrRequests.SingleAsync()).RowVersion);
        Assert.Equal("foreign-verifier", (await check.GpuExecutorEvidence.SingleAsync()).VerifierKey);
    }

    private async Task<(IDbContextFactory<FluxKnowledgeDbContext> Factory, SqlDocumentOcrStore Store,
        DocumentOcrStoredExecutionRecoveryBinding Binding)> SeedStoredExecutionMaintenanceAsync(bool actualMachine = false)
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var task = Assert.IsType<Guid>((await store.HandoffAsync(new(claim, revision, hash, [0]), default)).MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var lease = Assert.IsType<DocumentOcrExecutionLease>((await store.TryClaimExecutionAsync(handle, task,
            Guid.NewGuid(), actualMachine ? new(Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current) : SyntheticSupervisor(), default)).Lease);
        var native = new DocumentOcrProcessIdentity(1234, DateTimeOffset.UtcNow, lease.Supervisor.Process.MachineFingerprint);
        Assert.True((await store.PermitNativeStartAsync(lease, native, default)).Accepted);
        var saved = await store.StoreResultAsync(lease, new(true, "document-ocr-complete",
            [new(0, 0, [new("text", 1, 2, 3, 4, "Stored execution recovery.")])]), default);
        var scheduler = new SqlGpuSchedulerStore(factory, sourceAdmission: new FixtureSourceAdmission(factory));
        var reservation = Assert.Single(await scheduler.ReadStaleCapacityReservationsAsync(DateTimeOffset.UtcNow.AddDays(1), default));
        Assert.True((await scheduler.MarkCapacityUncertainAsync(Guid.NewGuid(), reservation, default)).Committed);
        await using var read = factory.CreateDbContext();
        var request = await read.DocumentOcrRequests.SingleAsync();
        var job = await read.Jobs.SingleAsync(value => value.Id == claim.JobId.Value);
        var source = await read.SourceRevisions.SingleAsync(value => value.Id == revision.Value);
        var root = await read.SourceRootConfigurations.SingleAsync(value => value.Id == source.SourceRootId);
        return (factory, store, new(lease, native, job.Id, request.PipelineRecordId, revision.Value, root.Id,
            root.ConfigurationRevision, (await read.GpuCapacitySlots.SingleAsync()).OwnerKey!, saved.ResultDigest,
            request.RowVersion.ToArray(), (await read.GpuMiniTasks.SingleAsync()).RowVersion.ToArray(), job.RowVersion.ToArray(), Guid.NewGuid()));
    }

    [NativeSqlServerFact]
    public async Task Stored_execution_CLI_settles_the_original_result_and_replays_without_requeue_or_model_loading()
    {
        var (factory, _, binding) = await SeedStoredExecutionMaintenanceAsync(actualMachine: true);
        var area = Path.Combine(Path.GetTempPath(), "FluxStoredOcrCli_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        try
        {
            await using var db = factory.CreateDbContext();
            var identity = await db.Database.SqlQueryRaw<string>("SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName'))+'|'+DB_NAME()+'|'+CONVERT(nvarchar(36),database_guid) AS [Value] FROM sys.database_recovery_status WHERE database_id=DB_ID()").SingleAsync();
            var originalJob = await db.Jobs.AsNoTracking().SingleAsync(value => value.Id == binding.ParentJobId);
            var config = Path.Combine(area, "config.json"); await File.WriteAllTextAsync(config, "{}");
            var hold = Path.Combine(area, "hold.json"); await File.WriteAllTextAsync(hold, JsonSerializer.Serialize("synthetic-stored-execution"));
            var verifier = Path.Combine(area, "synthetic-verifier.ps1");
            await File.WriteAllTextAsync(verifier, "[ordered]@{CleanupVerified=$true;ObservedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')}|ConvertTo-Json -Compress");
            static async Task<string> HashAsync(string path) => Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var plan = Path.Combine(area, "plan.json");
            await File.WriteAllTextAsync(plan, JsonSerializer.Serialize(new
            {
                Version = 3, Mode = "StoredExecution", Binding = (object?)null, TerminalBinding = (object?)null, StoredExecutionBinding = binding,
                ReleaseId = "synthetic-stored-execution", DatabaseIdentity = identity, OriginalHistory = (await db.Database.GetAppliedMigrationsAsync()).ToArray(),
                OperationRoot = area, HoldPath = hold, ConfigurationPath = config, ConfigurationSha256 = await HashAsync(config),
                VerifierScriptPath = verifier, VerifierScriptSha256 = await HashAsync(verifier)
            }));
            var assembly = typeof(SqlDocumentOcrHandoffTests).Assembly.Location;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(assembly, "runtimeconfig.json"), "--depsfile",
                    Path.ChangeExtension(assembly, "deps.json"), typeof(FluxKnowledge.Cli.Commands.OcrRecoveryCommand).Assembly.Location,
                    "ocr-recovery", "apply", "--plan", plan, "--sha256", await HashAsync(plan) }) start.ArgumentList.Add(argument);
                start.Environment["ConnectionStrings__FluxKnowledge"] = _fixture.ConnectionString;
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try { await process.WaitForExitAsync(budget.Token); } catch { if (!process.HasExited) process.Kill(true); throw; }
                var output = await stdout; Assert.True(process.ExitCode == 0, output + await stderr);
                using var response = JsonDocument.Parse(output);
                Assert.True(response.RootElement.GetProperty("CleanupConfirmed").GetBoolean());
                Assert.True(response.RootElement.GetProperty("StoredExecution").GetBoolean());
                Assert.True(response.RootElement.GetProperty("Settled").GetBoolean());
                Assert.False(response.RootElement.GetProperty("Requeued").GetBoolean());
                Assert.Equal(binding.OperationId, response.RootElement.GetProperty("OperationId").GetGuid());
            }
            await using var verify = factory.CreateDbContext();
            Assert.Equal((int)GpuCapacitySlotState.Available, (await verify.GpuCapacitySlots.SingleAsync()).State);
            Assert.Equal(binding.Lease.ExecutionInstanceId, (await verify.DocumentOcrRequests.SingleAsync()).ExecutionInstanceId);
            Assert.Equal(binding.ResultDigest, (await verify.DocumentOcrRequests.SingleAsync()).ResultDigest);
            Assert.Equal(JsonSerializer.Serialize(originalJob), JsonSerializer.Serialize(await verify.Jobs.SingleAsync(value => value.Id == binding.ParentJobId)));
            Assert.Single(await verify.GpuExecutorEvidence.ToArrayAsync());
        }
        finally { Directory.Delete(area, true); }
    }

    [NativeSqlServerFact]
    public async Task Stored_execution_preparation_reads_the_same_original_binding_consumed_by_SQL()
    {
        var (_, _, expected) = await SeedStoredExecutionMaintenanceAsync();
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", """
            $ErrorActionPreference='Stop'
            . (Join-Path $env:FLUX_OCR_BINDING_ROOT 'scripts/deploy/incremental-ocr-lifecycle-recovery.ps1')
            . (Join-Path $env:FLUX_OCR_BINDING_ROOT 'scripts/deploy/incremental-ocr-stored-execution.ps1')
            Get-OcrStoredExecutionBinding $env:FLUX_OCR_BINDING_SQL ([Guid]$env:FLUX_OCR_BINDING_BATCH) ([Guid]$env:FLUX_OCR_BINDING_OPERATION) | ConvertTo-Json -Depth 12 -Compress
            """ }) start.ArgumentList.Add(argument);
        start.Environment["FLUX_OCR_BINDING_ROOT"] = FluxKnowledge.Testing.RepositoryFiles.Root();
        start.Environment["FLUX_OCR_BINDING_SQL"] = _fixture.ConnectionString;
        start.Environment["FLUX_OCR_BINDING_BATCH"] = expected.Lease.Handle.BatchId.ToString();
        start.Environment["FLUX_OCR_BINDING_OPERATION"] = expected.OperationId.ToString();
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(budget.Token); } catch { if (!process.HasExited) process.Kill(true); throw; }
        var output = await stdout; Assert.True(process.ExitCode == 0, output + await stderr);
        var actual = JsonSerializer.Deserialize<DocumentOcrStoredExecutionRecoveryBinding>(output);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }
}
