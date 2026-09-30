using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class OutboxWorkerEligibilityTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(PublicJobState.GpuQueued)]
    [InlineData(PublicJobState.GpuProcessing)]
    public async Task Earlier_gpu_owned_delivery_does_not_delay_ready_publication(PublicJobState gpuState)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var now = DateTimeOffset.UtcNow;
        var gpu = await SqlTestData.SeedWorkItemAsync(fixture, now.AddMinutes(-1), gpuState,
            null, stage: PipelineStage.Embed, operation: PipelineOperations.Embed);
        var ready = await SqlTestData.SeedWorkItemAsync(fixture, now, PublicJobState.WorkerQueued,
            null, stage: PipelineStage.Publish, operation: PipelineOperations.Publish);
        var factory = SqlTestData.CreateFactory(fixture);
        var outbox = new SqlOutboxStore(factory);
        var dispatch = await outbox.ClaimNextDueAsync("dispatcher", now, TimeSpan.FromMinutes(1),
            [PipelineOperations.Embed, PipelineOperations.Publish], CancellationToken.None);

        Assert.NotNull(dispatch);
        Assert.Equal(ready.DispatchMessageId, dispatch.DispatchMessageId);
        var job = await new SqlJobClaimStore(factory).ClaimForDispatchAsync(dispatch, "worker", now,
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(ready.JobId, job.JobId);
        await using (var context = await factory.CreateDbContextAsync())
        {
            var untouched = await context.OutboxMessages.SingleAsync(value => value.Id == gpu.DispatchMessageId.Value);
            Assert.Equal(now.AddMinutes(-1), untouched.DueAtUtc);
            Assert.Equal(0, untouched.LeaseGeneration);
            Assert.Null(untouched.LeaseOwner);
            Assert.Null(untouched.LeaseExpiresAtUtc);
            var parent = await context.Jobs.SingleAsync(value => value.Id == gpu.JobId.Value);
            Assert.Equal((int)gpuState, parent.PublicState);
            // Simulate the existing settled GPU continuation returning its parent to the worker queue.
            parent.PublicState = (int)PublicJobState.WorkerQueued;
            await context.SaveChangesAsync();
        }

        var continuation = await outbox.ClaimNextDueAsync("dispatcher", now, TimeSpan.FromMinutes(1),
            [PipelineOperations.Embed, PipelineOperations.Publish], CancellationToken.None);
        Assert.NotNull(continuation);
        Assert.Equal(gpu.DispatchMessageId, continuation.DispatchMessageId);
        Assert.NotNull(await new SqlJobClaimStore(factory).ClaimForDispatchAsync(continuation, "worker", now,
            TimeSpan.FromMinutes(1), CancellationToken.None));
    }

    [NativeSqlServerTheory]
    [InlineData("future-due")]
    [InlineData("queued-live-lease")]
    [InlineData("processing-live-lease")]
    [InlineData("processing-without-expiry")]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("unbound")]
    [InlineData("wrong-record")]
    [InlineData("wrong-stage")]
    [InlineData("wrong-operation")]
    public async Task Unclaimable_bound_job_is_skipped_without_mutating_its_delivery(string reason)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var now = DateTimeOffset.UtcNow;
        var first = await SqlTestData.SeedWorkItemAsync(fixture, now.AddMinutes(-1),
            PublicJobState.WorkerQueued, null);
        var ready = await SqlTestData.SeedWorkItemAsync(fixture, now, PublicJobState.WorkerQueued, null);
        var factory = SqlTestData.CreateFactory(fixture);
        await using (var context = await factory.CreateDbContextAsync())
        {
            var job = await context.Jobs.SingleAsync(value => value.Id == first.JobId.Value);
            if (reason == "future-due") job.DueAtUtc = now.AddMinutes(1);
            if (reason == "queued-live-lease") job.LeaseExpiresAtUtc = now.AddMinutes(1);
            if (reason.StartsWith("processing", StringComparison.Ordinal)) job.PublicState = (int)PublicJobState.WorkerProcessing;
            if (reason == "processing-live-lease") job.LeaseExpiresAtUtc = now.AddMinutes(1);
            if (reason == "completed") job.PublicState = (int)PublicJobState.Completed;
            if (reason == "failed") job.PublicState = (int)PublicJobState.Failed;
            if (reason == "wrong-record") job.PipelineRecordId = ready.PipelineRecordId.Value;
            if (reason == "wrong-stage") job.Stage = (int)PipelineStage.Normalise;
            if (reason == "wrong-operation") job.Operation = PipelineOperations.NormaliseText;
            if (reason == "unbound")
                (await context.OutboxMessages.SingleAsync(value => value.Id == first.DispatchMessageId.Value)).JobId = null;
            await context.SaveChangesAsync();
        }

        var dispatch = await new SqlOutboxStore(factory).ClaimNextDueAsync("dispatcher", now,
            TimeSpan.FromMinutes(1), [PipelineOperations.ExtractUtf8], CancellationToken.None);
        Assert.NotNull(dispatch);
        Assert.Equal(ready.DispatchMessageId, dispatch.DispatchMessageId);
        await using var verification = await factory.CreateDbContextAsync();
        var untouched = await verification.OutboxMessages.SingleAsync(value => value.Id == first.DispatchMessageId.Value);
        Assert.Equal(now.AddMinutes(-1), untouched.DueAtUtc);
        Assert.Equal(0, untouched.LeaseGeneration);
        Assert.Null(untouched.LeaseOwner);
        Assert.Null(untouched.LeaseExpiresAtUtc);
    }

    [NativeSqlServerTheory]
    [InlineData(PublicJobState.WorkerQueued)]
    [InlineData(PublicJobState.WorkerProcessing)]
    public async Task Expired_worker_lease_remains_recoverable_through_bound_delivery(PublicJobState state)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var now = DateTimeOffset.UtcNow;
        var seeded = await SqlTestData.SeedWorkItemAsync(fixture, now, state, now.AddMinutes(-1),
            leaseGeneration: 3, attemptCount: 3);
        var factory = SqlTestData.CreateFactory(fixture);
        var dispatch = await new SqlOutboxStore(factory).ClaimNextDueAsync("dispatcher", now,
            TimeSpan.FromMinutes(1), [PipelineOperations.ExtractUtf8], CancellationToken.None);
        Assert.NotNull(dispatch);
        var job = await new SqlJobClaimStore(factory).ClaimForDispatchAsync(dispatch, "worker", now,
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(seeded.JobId, job.JobId);
        Assert.Equal(4, job.LeaseGeneration);
    }
}
