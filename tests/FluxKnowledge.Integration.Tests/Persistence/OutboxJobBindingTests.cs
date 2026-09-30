using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class OutboxJobBindingTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Completed_delivery_cannot_claim_a_replacement_job_at_the_same_record_revision_and_stage()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var now = DateTimeOffset.UtcNow;
        var seeded = await SqlTestData.SeedWorkItemAsync(fixture, now, PublicJobState.WorkerQueued, null);
        var factory = SqlTestData.CreateFactory(fixture);
        var dispatch = await new SqlOutboxStore(factory).ClaimNextDueAsync("old-delivery", now, TimeSpan.FromMinutes(1),
            [PipelineOperations.ExtractUtf8], CancellationToken.None);
        Assert.NotNull(dispatch);
        Assert.Equal(seeded.DispatchMessageId, dispatch.DispatchMessageId);
        await using (var context = await factory.CreateDbContextAsync())
        {
            var original = await context.Jobs.SingleAsync();
            original.PublicState = (int)PublicJobState.Completed;
            context.Jobs.Add(new JobEntity
            {
                Id = Guid.NewGuid(), PipelineRecordId = original.PipelineRecordId,
                SourceRevision = original.SourceRevision, Stage = original.Stage,
                Operation = original.Operation, PublicState = (int)PublicJobState.WorkerQueued,
                DueAtUtc = now
            });
            await context.SaveChangesAsync();
        }
        var claim = await new SqlJobClaimStore(factory).ClaimForDispatchAsync(dispatch, "worker", now, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Null(claim);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal((int)PublicJobState.WorkerQueued, await verification.Jobs.Where(job => job.Id != seeded.JobId.Value).Select(job => job.PublicState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Replacement_delivery_claims_its_own_job_and_preserves_completed_work()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var now = DateTimeOffset.UtcNow;
        var seeded = await SqlTestData.SeedWorkItemAsync(fixture, now, PublicJobState.Completed, null);
        var factory = SqlTestData.CreateFactory(fixture);
        var newJobId = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            var oldDispatch = await context.OutboxMessages.SingleAsync();
            oldDispatch.DispatchedAtUtc = now;
            context.Jobs.Add(new JobEntity
            {
                Id = newJobId, PipelineRecordId = seeded.PipelineRecordId.Value, SourceRevision = 1,
                Stage = (int)PipelineStage.Extract, Operation = PipelineOperations.ExtractUtf8,
                PublicState = (int)PublicJobState.WorkerQueued, DueAtUtc = now
            });
            await context.SaveChangesAsync();
        }
        await new SqlOutboxStore(factory).EnqueueAsync(DispatchMessage.Create(new(newJobId), seeded.PipelineRecordId,
            1, PipelineStage.Extract, PipelineOperations.ExtractUtf8, 1, "replacement", now), CancellationToken.None);
        var dispatch = await new SqlOutboxStore(factory).ClaimNextDueAsync("new-delivery", now, TimeSpan.FromMinutes(1),
            [PipelineOperations.ExtractUtf8], CancellationToken.None);
        Assert.NotNull(dispatch);
        var claim = await new SqlJobClaimStore(factory).ClaimForDispatchAsync(dispatch, "worker", now, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Equal(newJobId, claim.JobId.Value);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal((int)PublicJobState.Completed, (await verification.Jobs.SingleAsync(job => job.Id == seeded.JobId.Value)).PublicState);
    }

    [NativeSqlServerTheory]
    [InlineData("expired")]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("unbound")]
    public async Task Ordinary_worker_claim_requires_the_exact_live_bound_delivery(string mismatch)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var now = DateTimeOffset.UtcNow;
        var seeded = await SqlTestData.SeedWorkItemAsync(fixture, now, PublicJobState.WorkerQueued, null);
        var factory = SqlTestData.CreateFactory(fixture);
        var dispatch = await new SqlOutboxStore(factory).ClaimNextDueAsync("dispatcher", now, TimeSpan.FromMinutes(1),
            [PipelineOperations.ExtractUtf8], CancellationToken.None);
        Assert.NotNull(dispatch);
        if (mismatch == "owner") dispatch = dispatch with { LeaseOwner = "other" };
        if (mismatch == "generation") dispatch = dispatch with { LeaseGeneration = dispatch.LeaseGeneration + 1 };
        if (mismatch == "unbound")
        {
            await using var context = await factory.CreateDbContextAsync();
            await context.OutboxMessages.ExecuteUpdateAsync(setters => setters.SetProperty(message => message.JobId, (Guid?)null));
        }
        var claim = await new SqlJobClaimStore(factory).ClaimForDispatchAsync(dispatch, "worker",
            mismatch == "expired" ? now.AddMinutes(2) : now, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Null(claim);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal((int)PublicJobState.WorkerQueued, (await verification.Jobs.SingleAsync(job => job.Id == seeded.JobId.Value)).PublicState);
    }

    [NativeSqlServerFact]
    public async Task Enqueue_refuses_a_job_with_a_different_record_or_stage()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var now = DateTimeOffset.UtcNow;
        var seeded = await SqlTestData.SeedWorkItemAsync(fixture, now, PublicJobState.WorkerQueued, null);
        var factory = SqlTestData.CreateFactory(fixture);
        var outbox = new SqlOutboxStore(factory);
        foreach (var message in new[]
        {
            DispatchMessage.Create(seeded.JobId, PipelineRecordId.New(), 1, PipelineStage.Extract, PipelineOperations.ExtractUtf8, 1, "wrong-record", now),
            DispatchMessage.Create(seeded.JobId, seeded.PipelineRecordId, 1, PipelineStage.Embed, PipelineOperations.Embed, 1, "wrong-stage", now)
        })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await outbox.EnqueueAsync(message, CancellationToken.None));
            Assert.Equal("outbox-job-binding-invalid", error.Message);
        }
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Single(await verification.OutboxMessages.ToArrayAsync());
    }
}
