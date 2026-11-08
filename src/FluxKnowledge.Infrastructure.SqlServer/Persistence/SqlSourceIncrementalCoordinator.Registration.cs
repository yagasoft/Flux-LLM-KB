using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceIncrementalCoordinator
{
    private async ValueTask<JournalPollResult> RegisterMissingCheckpointAsync(string volume, CancellationToken ct)
    {
        Guid? rootId;
        await using (var lookup = await factory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            // A null claim usually means due/ownership, not missing history. Never query
            // a new head to replace an existing checkpoint, including a leased one.
            if (await lookup.SourceVolumeJournalCheckpoints.AsNoTracking().AnyAsync(x => x.VolumeIdentity == volume, ct).ConfigureAwait(false))
                return JournalPollResult.NoClaim;
            rootId = await (from state in lookup.SourceRootScanStates.AsNoTracking()
                join root in lookup.SourceRootConfigurations.AsNoTracking() on state.SourceRootId equals root.Id
                where state.VolumeIdentity == volume && root.State == (int)SourceRootState.Enabled
                orderby root.Id select (Guid?)root.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }
        if (rootId is null) return JournalPollResult.NoClaim;
        try
        {
            var capability = await reader.QueryAsync(volume, ct).ConfigureAwait(false);
            if (!capability.VolumeIdentity.Equals(volume, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SourceJournalRegistrationBindingInvalid");
            var result = JournalPollResult.NoClaim;
            await _journal.ExecuteAsync(async db =>
            {
                result = JournalPollResult.NoClaim;
                // Match Enrol/Accept ordering: root before checkpoint. Query may have
                // overlapped a pause or a successful first registration by another owner.
                var root = await db.SourceRootConfigurations.FromSqlInterpolated(
                    $"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={rootId.Value}").SingleOrDefaultAsync(ct).ConfigureAwait(false);
                if (root is null || root.State != (int)SourceRootState.Enabled) return;
                var checkpoint = await db.SourceVolumeJournalCheckpoints.FromSqlInterpolated(
                    $"SELECT * FROM [SourceVolumeJournalCheckpoints] WITH (UPDLOCK,HOLDLOCK) WHERE [VolumeIdentity]={volume}").SingleOrDefaultAsync(ct).ConfigureAwait(false);
                if (checkpoint is not null || !await db.SourceRootScanStates.AnyAsync(x =>
                    x.SourceRootId == rootId.Value && x.VolumeIdentity == volume, ct).ConfigureAwait(false)) return;
                db.SourceVolumeJournalCheckpoints.Add(new()
                {
                    VolumeIdentity = volume, JournalId = capability.JournalId.ToString("X16"), NextUsn = capability.NextUsn
                });
                db.AuditEvents.Add(new AuditEventEntity
                {
                    EventType = "source.journal.first_registration", EventFamily = "source-journal", Severity = "information",
                    Actor = "source-journal", OccurredAtUtc = time.GetUtcNow(), DetailsJson = JsonSerializer.Serialize(new
                    {
                        reason = "initial-query-recovered", coverage = "no-historical-coverage", eligibleRootId = rootId.Value,
                        cursor = new JournalCursor(volume, capability.JournalId, capability.NextUsn)
                    })
                });
                result = JournalPollResult.Idle;
            }, ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException ||
            error is OperationCanceledException && !ct.IsCancellationRequested)
        { return JournalPollResult.Unavailable; }
    }
}
