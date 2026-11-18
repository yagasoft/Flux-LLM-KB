using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;

namespace FluxKnowledge.Web.Components.Sources;

internal sealed class SourceJournalOperator(SourceJournalOptions options, ISourceChangeJournalReader reader, ISourceJournalVolumeInventory inventory)
{
    private static readonly StringComparer IdentityComparer = StringComparer.OrdinalIgnoreCase;

    public bool Enabled => options.Enabled;
    public bool UsesProtectedHelper => !string.IsNullOrWhiteSpace(options.PipeName);
    public IReadOnlyList<string> ApprovedVolumes => options.ApprovedVolumes;

    internal IReadOnlyList<JournalVolume> ReadLocalVolumes() => inventory.ReadLocalVolumes();

    internal static string? FindLocalVolume(string path, IReadOnlyList<JournalVolume> volumes)
    {
        var drive = Path.GetPathRoot(path);
        return volumes.FirstOrDefault(x => IdentityComparer.Equals(x.Drive, drive))?.Identity;
    }

    internal async Task<JournalProbe> ProbeAsync(string volume, CancellationToken ct)
    {
        if (!Enabled) return new("Journal scanning is disabled in the running app.");
        if (!ApprovedVolumes.Contains(volume, IdentityComparer))
            return new("Journal access is unverified because this volume is outside the app allowlist.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var capability = await reader.QueryAsync(volume, deadline.Token);
            if (!IdentityComparer.Equals(volume, capability.VolumeIdentity))
                return new("The journal returned a different volume identity. Check the app and helper configuration.");
            var cursor = new JournalCursor(capability.VolumeIdentity, capability.JournalId, capability.NextUsn);
            var page = await reader.ReadAsync(cursor, 65536, deadline.Token);
            if (page.Start != cursor || page.Next.VolumeIdentity != cursor.VolumeIdentity ||
                page.Next.JournalId != cursor.JournalId || page.Next.NextUsn < cursor.NextUsn)
                return new("The journal read could not be tied to the expected volume and cursor. Retry the check.");
            return new("Journal query and read succeeded.", capability.JournalId.ToString("X16"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new("Journal access timed out. Check that the volume is online and the protected helper is running.");
        }
        catch (SourceJournalGapException)
        {
            return new("The journal cursor changed during the check. Retry the check and reconcile source metadata if coverage is incomplete.");
        }
        catch (IOException error)
        {
            return new((WindowsSourceChangeJournalReader.DiagnosticFailure(error) ?? error.Message) switch
            {
                "SourceJournalHelperRefused" => "The protected helper refused this query. Check its approved volumes and indexing identity with a Windows administrator.",
                "SourceJournalHelperUnauthenticated" => "The journal helper could not be authenticated. Check the protected helper installation with a Windows administrator.",
                "SourceJournalHelperUnavailable" => "The protected helper could not read the journal. Check the helper and Windows journal status.",
                "journal-inactive" => "Windows reports that the USN journal is inactive. Ask a Windows administrator to check journal provisioning for this volume.",
                "journal-deleting" => "Windows is deleting the USN journal. Wait for Windows to finish, then check the volume again.",
                "volume-access-denied" => "Windows denied access to the volume journal. Check the approved helper identity with a Windows administrator.",
                "volume-unavailable" => "Windows could not open the volume. Restore the expected local volume and check again.",
                _ => "The journal could not be read. Check the volume and the protected helper installation."
            });
        }
        catch (UnauthorizedAccessException)
        {
            return new("Windows denied journal transport access. Ask a Windows administrator to check the helper and indexing identity.");
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException)
        {
            return new("The journal transport response or configuration is invalid. Check that the app and helper releases match.");
        }
    }

    internal JournalAllowlistProposal Preview(IReadOnlyCollection<string> selection, IReadOnlyList<JournalVolume> volumes,
        IReadOnlyList<JournalSourceRoot> roots)
    {
        if (!Enabled || !UsesProtectedHelper)
            throw new InvalidOperationException("The protected journal helper must be configured before editing the allowlist.");
        if (selection.Count is < 1 or > 16)
            throw new InvalidOperationException("Select between 1 and 16 volumes. To stop journal scanning completely, use the reviewed feature-off deployment.");

        var current = ApprovedVolumes.ToHashSet(IdentityComparer);
        var added = selection.Except(current, IdentityComparer).ToHashSet(IdentityComparer);
        var removed = current.Except(selection, IdentityComparer).ToHashSet(IdentityComparer);
        if (selection.Distinct(IdentityComparer).Count() != selection.Count ||
            selection.Any(x => !IsCanonicalIdentity(x)) ||
            added.Any(id => !volumes.Any(volume => IdentityComparer.Equals(id, volume.Identity))))
            throw new InvalidOperationException("Select only the local fixed NTFS volumes shown. Refresh the volume list and review again.");
        if (added.Count == 0 && removed.Count == 0)
            throw new InvalidOperationException("Choose a volume to add or remove before reviewing the change.");

        var impacts = new List<JournalRootImpact>();
        var unmapped = new List<JournalSourceRoot>();
        foreach (var root in roots)
        {
            var identity = FindLocalVolume(root.CanonicalPath, volumes);
            if (identity is null) unmapped.Add(root);
            if (identity is not null && added.Contains(identity))
                impacts.Add(new(root, identity, true));
            if (identity is not null && removed.Contains(identity))
                impacts.Add(new(root, identity, false));
            if (root.StoredVolume is { } stored && !IdentityComparer.Equals(stored, identity) && removed.Contains(stored))
                impacts.Add(new(root, stored, false));
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ExpectedApprovedVolumes = current.Order(IdentityComparer),
            ApprovedVolumes = selection.Order(IdentityComparer)
        }, new JsonSerializerOptions { WriteIndented = true });
        return new(added.Order(IdentityComparer).ToArray(), removed.Order(IdentityComparer).ToArray(), impacts, unmapped,
            Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToBase64String(bytes));
    }

    private static bool IsCanonicalIdentity(string value) =>
        value.Length == 49 && value.StartsWith(@"\\?\VOLUME{", StringComparison.Ordinal) &&
        value.EndsWith(@"}\", StringComparison.Ordinal) && Guid.TryParseExact(value.AsSpan(11, 36), "D", out _) &&
        value == value.ToUpperInvariant();
}

internal sealed record JournalVolume(string Drive, string Identity);
internal interface ISourceJournalVolumeInventory
{
    IReadOnlyList<JournalVolume> ReadLocalVolumes();
}

internal sealed class WindowsSourceJournalVolumeInventory : ISourceJournalVolumeInventory
{
    public IReadOnlyList<JournalVolume> ReadLocalVolumes()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var volumes = new List<JournalVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady &&
                    string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    volumes.Add(new(drive.Name, WindowsSourceChangeJournalReader.CanonicalVolume(drive.Name)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return volumes;
    }
}

internal sealed record JournalProbe(string Message, string? JournalId = null);
internal sealed record JournalSourceRoot(Guid Id, string DisplayName, string CanonicalPath, string State, string? StoredVolume = null);
internal sealed record JournalRootImpact(JournalSourceRoot Root, string Volume, bool Added);
internal sealed record JournalAllowlistProposal(IReadOnlyList<string> Added, IReadOnlyList<string> Removed,
    IReadOnlyList<JournalRootImpact> Impacts, IReadOnlyList<JournalSourceRoot> Unmapped, string Sha256, string Base64);
