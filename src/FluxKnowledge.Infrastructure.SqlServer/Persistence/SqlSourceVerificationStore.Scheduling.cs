using System.Text.Json;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceVerificationStore
{
    public async ValueTask<int> PumpAsync(CancellationToken ct)
    {
        if (!options.Enabled) return 0;
        await using var lookup = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var roots = await (from root in lookup.SourceRootConfigurations.AsNoTracking()
            join state in lookup.SourceRootScanStates.AsNoTracking() on root.Id equals state.SourceRootId
            where root.State == (int)SourceRootState.Enabled && state.BaselineEpoch > 0 && !state.RecoveryRequired && state.Mode != "Legacy"
            select root.Id).ToArrayAsync(ct).ConfigureAwait(false);
        var count = 0;
        foreach (var id in roots)
        {
            var queued = false;
            await _transactions.ExecuteAsync(async db =>
            {
                queued = false;
                var root = await LockRootAsync(db, id, ct).ConfigureAwait(false);
                var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == id, ct).ConfigureAwait(false);
                if (root.State != (int)SourceRootState.Enabled || SqlSourceIncrementalState.RequiresBaseline(state) || state.Mode == "Legacy") return;
                var policy = ReadPolicy(state);
                state.VerificationPolicyJson = JsonSerializer.Serialize(policy);
                state.VerificationPolicyRevision = Math.Max(1, state.VerificationPolicyRevision);
                var now = time.GetUtcNow();
                var active = await db.SourceVerificationRuns.SingleOrDefaultAsync(x => x.SourceRootId == id && x.Active, ct).ConfigureAwait(false);
                if (active is not null)
                {
                    var authority = JsonSerializer.Deserialize<FluxKnowledge.Application.Ports.SourceVerificationAuthority>(active.AuthorityJson)
                        ?? throw new InvalidDataException("SourceVerificationAuthorityMissing");
                    if (!Matches(root, state, authority))
                    { await InvalidateRunAsync(db, active, now, ct).ConfigureAwait(false); return; }
                    if (now >= active.CreatedAtUtc.AddDays(7) && active.State != "Running") active.State = "Overdue";
                    queued = await EnqueueBatchAsync(db, root, state, active, now, ct).ConfigureAwait(false);
                    return;
                }
                if (state.VerificationPaused) return;
                var last = await db.SourceVerificationRuns.Where(x => x.SourceRootId == id).MaxAsync(x => (DateTimeOffset?)x.CreatedAtUtc, ct).ConfigureAwait(false);
                if (SourceVerificationSchedule.DueCycle(policy, now, last, false, TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId)) is null) return;
                await StartInTransactionAsync(db, root, false, now, ct).ConfigureAwait(false);
                queued = db.ChangeTracker.Entries<Entities.SourceScanRequestEntity>().Any(x => x.State == EntityState.Added && x.Entity.RequestKind == RequestKind);
            }, ct).ConfigureAwait(false);
            if (queued) count++;
        }
        return count;
    }
}
