using System.Text.Json;
using System.Text.Json.Serialization;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceVerificationStore
{
    internal static (SourceVerificationPolicy? Policy, bool? Paused) ParseControl(JsonElement payload)
    {
        try
        {
            SourceVerificationPolicy? policy = null;
            bool? paused = null;
            if (payload.TryGetProperty("policy", out var value))
            {
                if (value.ValueKind != JsonValueKind.Object) throw new NativeOperationException("invalid-payload");
                policy = value.Deserialize<SourceVerificationPolicy>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new NativeOperationException("invalid-payload");
                policy.Validate();
                _ = TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId);
            }
            if (payload.TryGetProperty("paused", out var pause))
                paused = pause.ValueKind is JsonValueKind.True or JsonValueKind.False ? pause.GetBoolean() : throw new NativeOperationException("invalid-payload");
            if (policy is null && paused is null) throw new NativeOperationException("invalid-payload");
            return (policy, paused);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new NativeOperationException("invalid-payload"); }
    }

    internal static async Task ApplyControlInTransactionAsync(FluxKnowledgeDbContext db, SourceRootConfigurationEntity root,
        SourceRootScanStateEntity? state, string action, JsonElement payload, bool enabled, DateTimeOffset now, CancellationToken ct)
    {
        if (action == "source_verify")
        {
            if (!enabled) throw new NativeOperationException("source-verification-disabled");
            try { await StartInTransactionAsync(db, root, true, now, ct).ConfigureAwait(false); }
            catch (InvalidOperationException error) when (error.Message == "SourceVerificationBaselineRequired")
            { throw new NativeOperationException("source-verification-baseline-required"); }
            return;
        }
        var control = ParseControl(payload);
        if (state is null)
        {
            state = new() { SourceRootId = root.Id };
            db.SourceRootScanStates.Add(state);
        }
        state.VerificationPolicyJson = JsonSerializer.Serialize(control.Policy ?? ReadPolicy(state));
        state.VerificationPaused = control.Paused ?? state.VerificationPaused;
        state.VerificationPolicyRevision = checked(state.VerificationPolicyRevision + 1);
        if (control.Policy is { } policy)
        {
            var active = await db.SourceVerificationRuns.SingleOrDefaultAsync(x => x.SourceRootId == root.Id && x.Active, ct).ConfigureAwait(false);
            if (active is not null)
            {
                var next = active.NextBatchAtUtc is { } prior && prior < now.AddSeconds(5) ? prior : now.AddSeconds(5);
                if (!active.Manual)
                {
                    var window = SourceVerificationSchedule.CurrentOrNextWindow(policy, next, TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId));
                    if (!window.Contains(next)) next = window.Start;
                }
                active.NextBatchAtUtc = next;
            }
        }
    }
}
