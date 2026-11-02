using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class PipelineActivitySqlTests
{
    [NativeSqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Journal_warning_uses_current_checkpoint_health_and_preserves_the_paused_scan_schedule(bool unavailable)
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var volume = $"synthetic-{root:N}";
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        var configuration = await context.SourceRootConfigurations.SingleAsync(x => x.Id == root);
        configuration.State = (int)SourceRootState.Paused;
        configuration.LastScanCompletedAtUtc = now;
        context.SourceRootScanStates.Add(new() { SourceRootId = root, VolumeIdentity = volume, Mode = "Fallback",
            Reason = "journal-unavailable", RecoveryRequired = true, LastReconciliationAtUtc = now });
        context.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100,
            UnavailableSinceUtc = unavailable ? now.AddDays(-2) : null,
            LastFailureCode = unavailable ? "journal-recovery-paused" : null, DueAtUtc = now.AddSeconds(10) });
        await context.SaveChangesAsync();

        var snapshot = await new SqlPipelineActivityReader(null, new FixedTimeProvider(now)).ReadAsync(context, now, default);
        var source = Assert.Single(snapshot.Sources, x => x.Id == root);
        var health = Assert.IsType<SourceJournalHealth>(source.JournalHealth);
        Assert.Equal("Paused", source.Lifecycle);
        Assert.Equal(unavailable, health.IsUnavailable);
        Assert.True(health.RecoveryRequired);
        Assert.Equal(unavailable ? "journal-recovery-paused" : null, health.Reason);
        Assert.Equal(unavailable ? now.AddDays(-2) : null, health.UnavailableSinceUtc);
        Assert.Equal(now.AddSeconds(10), health.NextRetryAtUtc);
        Assert.Equal(now.AddMinutes(15), health.NextReconciliationAtUtc);
        Assert.Equal(900, health.ReconciliationCadenceSeconds);
        Assert.False(await context.SourceScanRequests.AnyAsync(x => x.SourceRootId == root));
    }
}
