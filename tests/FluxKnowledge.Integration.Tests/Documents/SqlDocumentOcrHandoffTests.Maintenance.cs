using FluxKnowledge.Application.Documents;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [NativeSqlServerFact]
    public async Task Legacy_recovery_refuses_missing_stale_or_changed_proof_and_preserves_saved_success_until_exact_verified_settlement()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var task = Assert.IsType<Guid>((await store.HandoffAsync(new(claim, revision, hash, [0]), CancellationToken.None)).MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var scheduler = new SqlGpuSchedulerStore(factory);
        await scheduler.AcknowledgeAsync(new(Guid.NewGuid(), handle), CancellationToken.None);
        var result = await store.StoreResultAsync(handle, task, new(true, "document-ocr-complete",
            [new(0, 0, [new("text", 1, 2, 3, 4, "saved legacy success")])]), CancellationToken.None);
        var reservation = Assert.Single(await scheduler.ReadStaleCapacityReservationsAsync(DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None));
        await scheduler.MarkCapacityUncertainAsync(Guid.NewGuid(), reservation, CancellationToken.None);
        await using (var migration = await factory.CreateDbContextAsync())
        {
            await migration.GetService<IMigrator>().MigrateAsync("20261021010911_AddIndexRecoveryReferenceAudit");
            try { await OcrLifecycleDeploymentTests.RunScriptAsync("ocr-lifecycle-migration-rehearsal.ps1", migration.Database.GetConnectionString()); }
            finally { await migration.Database.MigrateAsync(); }
        }
        await using var read = await factory.CreateDbContextAsync();
        var binding = new DocumentOcrLegacyRecoveryBinding(handle, task, claim.JobId.Value,
            (await read.GpuCapacitySlots.SingleAsync()).OwnerKey!, result.ResultDigest, Guid.NewGuid());
        var json = await read.DocumentOcrRequests.Select(r => r.ResultJson).SingleAsync();
        var proof = new MaintenanceVerifier();
        Assert.False((await store.AcceptLegacyCleanupAsync(binding, proof, CancellationToken.None)).Accepted);
        proof.Observation = new(SyntheticSupervisor(), DateTimeOffset.UtcNow.AddMinutes(-1), new string('b', 64));
        Assert.False((await store.AcceptLegacyCleanupAsync(binding, proof, CancellationToken.None)).Accepted);
        proof.Observation = proof.Observation with { ObservedAtUtc = DateTimeOffset.UtcNow };
        Assert.False((await store.AcceptLegacyCleanupAsync(binding with { ResultDigest = new byte[32] }, proof, CancellationToken.None)).Accepted);
        Assert.False((await store.AcceptLegacyCleanupAsync(binding with { CapacityOwnerKey = "another-owner" }, proof, CancellationToken.None)).Accepted);
        Assert.True((await store.AcceptLegacyCleanupAsync(binding, proof, CancellationToken.None)).Accepted);
        var calls = proof.Calls;
        proof.Observation = null;
        Assert.Equal(DocumentOcrLifecycleDisposition.AlreadyApplied,
            (await store.AcceptLegacyCleanupAsync(binding, proof, CancellationToken.None)).Disposition);
        Assert.Equal(calls, proof.Calls);
        Assert.True((await store.SettleCompletionAsync(handle, task, result.ResultDigest, CancellationToken.None)).Accepted);
        Assert.True(await store.RequeueCompletedAsync(handle, task, CancellationToken.None));
        Assert.False(await store.RequeueCompletedAsync(handle, task, CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(json, await verify.DocumentOcrRequests.Select(r => r.ResultJson).SingleAsync());
        Assert.Equal(result.ResultDigest, await verify.DocumentOcrRequests.Select(r => r.ResultDigest).SingleAsync());
        Assert.Equal((int)GpuCapacitySlotState.Available, await verify.GpuCapacitySlots.Select(s => s.State).SingleAsync());
        Assert.Equal("saved legacy success", Assert.Single(Assert.Single(Assert.IsType<DocumentOcrExecutionResult>(
            await store.ReadCompletedAsync(claim, hash, CancellationToken.None)).Pages).Blocks).Text);
        Assert.NotNull(await new SqlJobClaimStore(factory).ClaimNextDueAsync("legacy-resumed-consumer",
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), CancellationToken.None));
        var (_, nextClaim, nextRevision, nextHash) = await CreateClaimedPdfAsync(clearData: false);
        var nextTask = Assert.IsType<Guid>((await store.HandoffAsync(new(nextClaim, nextRevision, nextHash, [0]), CancellationToken.None)).MiniTaskId);
        var nextHandle = await AdmitDocumentOcrTaskAsync(factory);
        Assert.Equal(DocumentOcrLifecycleDisposition.Applied, (await store.TryClaimExecutionAsync(nextHandle, nextTask,
            Guid.NewGuid(), SyntheticSupervisor(), CancellationToken.None)).Disposition);
    }

    private sealed class MaintenanceVerifier : IDocumentOcrMaintenanceVerifier
    {
        internal DocumentOcrMaintenanceVerification? Observation { get; set; }
        internal int Calls { get; private set; }
        public ValueTask<DocumentOcrMaintenanceVerification?> VerifyStoppedAndCleanAsync(CancellationToken cancellationToken)
        { Calls++; return ValueTask.FromResult(Observation); }
    }
}
