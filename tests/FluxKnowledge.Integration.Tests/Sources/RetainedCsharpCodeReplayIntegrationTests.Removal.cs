using System.Text;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class RetainedCsharpCodeReplayIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Removed_retained_input_rejects_late_completion_and_retires_only_after_its_lease_expires()
    {
        var bytes = Encoding.UTF8.GetBytes("namespace Removal; public sealed class C { public int Value => 1; }");
        var seeded = await SeedCsharpBranchAsync(bytes);
        var factory = SqlTestData.CreateFactory(fixture);
        var store = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System, sourceAdmission: new FixtureSourceAdmission(factory));
        var claim = Assert.Single(await store.ClaimCsharpCodeAsync("removed-csharp", 1,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, default));
        var completion = await ProcessAsync(claim, bytes);
        await using var db = CreateContext();
        (await db.SourceRevisions.SingleAsync(x => x.Id == claim.SourceRevisionId.Value)).SuppressedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var retirement = new SqlSourceWorkRetirement(factory, TimeProvider.System);
        Assert.Equal(0, await retirement.RetireAsync(default));
        Assert.False((await store.CompleteRetainedCsharpCodeAsync(claim, completion, default)).IsCommitted);
        Assert.Equal((int)RetainedProcessorBranchState.Running,
            await db.SourceProcessorBranches.AsNoTracking().Where(x => x.Id == seeded.BranchId).Select(x => x.State).SingleAsync());
        await db.SourceProcessorBranches.Where(x => x.Id == seeded.BranchId)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert.Equal(1, await retirement.RetireAsync(default));
        var branch = await db.SourceProcessorBranches.AsNoTracking().SingleAsync(x => x.Id == seeded.BranchId);
        Assert.Equal((int)RetainedProcessorBranchState.Blocked, branch.State);
        Assert.Null(branch.LeaseOwner);
        var activity = await db.SourceActivities.AsNoTracking().SingleAsync(x => x.Id == branch.SourceActivityId);
        Assert.Equal((int)SourceActivityState.CancelledSuperseded, activity.State);
        Assert.Equal("source-removed", activity.Reason);
        var attempt = await db.SourceProcessorAttempts.AsNoTracking().SingleAsync(x => x.BranchId == branch.Id);
        Assert.NotNull(attempt.FinishedAtUtc);
        Assert.Equal("source-removed", attempt.OutcomeCode);
        Assert.Empty(await db.SourceProcessorCodeDocuments.Where(x => x.SourceProcessorBranchId == seeded.BranchId).ToArrayAsync());
        Assert.False((await store.CompleteRetainedCsharpCodeAsync(claim, completion, default)).IsCommitted);
    }
}
