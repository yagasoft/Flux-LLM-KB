using System.Security.Principal;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;

namespace FluxKnowledge.Web.Endpoints;

/// <summary>Read-only transport qualification while deployment admissions remain held.</summary>
public static class SourceJournalHelperHealthEndpoints
{
    public static IEndpointRouteBuilder MapFluxKnowledgeSourceJournalHelperHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/source-journal-helper", ProbeAsync);
        return endpoints;
    }

    private static async Task<IResult> ProbeAsync(HttpContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!LocalOperatorLoopbackGate.IsDirectLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var hold = services.GetService<IDeploymentValidationHold>();
        if (hold?.IsHeld != true) return Results.StatusCode(StatusCodes.Status409Conflict);
        var options = services.GetService<SourceJournalOptions>();
        var reader = services.GetService<ISourceChangeJournalReader>();
        var repair = services.GetService<ISourceAccessRepairClient>();
        var repairEnabled = services.GetRequiredService<IConfiguration>().GetValue<bool>("SourceAccess:Enabled");
        if (options is null || string.IsNullOrWhiteSpace(options.PipeName) || options.ApprovedVolumes.Length is < 1 or > 16 ||
            reader is null || repairEnabled && repair is null)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            foreach (var volume in options.ApprovedVolumes)
            {
                if (!hold.IsHeld) return Results.StatusCode(StatusCodes.Status409Conflict);
                var capability = await reader.QueryAsync(volume, deadline.Token).ConfigureAwait(false);
                if (!string.Equals(capability.VolumeIdentity, volume, StringComparison.OrdinalIgnoreCase))
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                var cursor = new JournalCursor(volume, capability.JournalId, capability.NextUsn);
                var page = await reader.ReadAsync(cursor, 65536, deadline.Token).ConfigureAwait(false);
                if (page.Start != cursor || page.Next.VolumeIdentity != cursor.VolumeIdentity || page.Next.JournalId != cursor.JournalId ||
                    page.Next.NextUsn < cursor.NextUsn)
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            if (!hold.IsHeld) return Results.StatusCode(StatusCodes.Status409Conflict);
            // No valid policy can approve an empty root. Resolve refuses this
            // before opening a source or creating/retiring an ACL-repair intent.
            var repairTransport = "Disabled";
            if (repairEnabled)
            {
                var outcome = repair!.TryRepair(new("", null, "probe", false), deadline.Token);
                if (outcome != SourceAccessRepairOutcome.NotApproved)
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                repairTransport = outcome.ToString();
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (!hold.IsHeld) return Results.StatusCode(StatusCodes.Status409Conflict);
            string? callerSid = null;
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                callerSid = identity.User?.Value;
            }
            return Results.Ok(new { Version = 1, VolumesChecked = options.ApprovedVolumes.Length,
                RepairTransport = repairTransport, CallerSid = callerSid });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
