using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

internal static class SourceAccessRecovery
{
    internal static T Run<T>(string rootPath, string? rootIdentity, ISourceAccessRepairClient? client, Func<T> open,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (client is null) return open();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return open(); }
            catch (SourceAccessDeniedException denied)
            {
                var target = Path.GetFullPath(denied.DeniedPath);
                if (!WindowsSourcePathAccess.IsWithin(root, target) ||
                    string.Equals(root, target, StringComparison.OrdinalIgnoreCase) && !denied.IsDirectory)
                    throw WithReason(denied, "SourceAccessOutsideApprovedRoot");
                if (attempted.Count >= 16) throw WithReason(denied, "SourceAccessRepairLimit");
                if (!attempted.Add(target)) throw WithReason(denied, "SourceAccessRepairUnconfirmed");

                SourceAccessRepairOutcome outcome;
                cancellationToken.ThrowIfCancellationRequested();
                try { outcome = client.TryRepair(new(root, rootIdentity, Path.GetRelativePath(root, target), denied.IsDirectory), cancellationToken); }
                catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
                { outcome = SourceAccessRepairOutcome.Unavailable; }
                if (outcome is SourceAccessRepairOutcome.Repaired or SourceAccessRepairOutcome.AlreadyReadable) continue;
                if (outcome == SourceAccessRepairOutcome.ContentUnavailable)
                    throw new SourceContentUnavailableException("Source content is not available for a guarded local read. No download was requested.", denied);
                throw WithReason(denied, outcome switch
                {
                    SourceAccessRepairOutcome.NotApproved => "SourceAccessRepairNotApproved",
                    SourceAccessRepairOutcome.ExplicitDeny => "SourceAccessExplicitDeny",
                    SourceAccessRepairOutcome.UnsupportedPermissions => "SourceAccessPermissionsUnsupported",
                    SourceAccessRepairOutcome.OutsideScope => "SourceAccessOutsideApprovedRoot",
                    SourceAccessRepairOutcome.IdentityChanged => "SourceAccessIdentityChanged",
                    SourceAccessRepairOutcome.Unconfirmed => "SourceAccessRepairUnconfirmed",
                    _ => "SourceAccessRepairUnavailable"
                });
            }
        }
    }

    private static SourceAccessDeniedException WithReason(SourceAccessDeniedException denied, string reason) =>
        new(denied.DeniedPath, denied.IsDirectory, denied.InnerException ?? denied, reason);
}
