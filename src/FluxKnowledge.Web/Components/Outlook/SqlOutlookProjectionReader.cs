using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web.Components.Outlook;

public sealed record OutlookSpoolStatus(
    string Status,
    string Health,
    string Capacity,
    string? ConfigurationKey = null);

public sealed record OutlookFolderStatusProjection(
    Guid FolderId,
    string DisplayName,
    string State,
    DateTimeOffset? CursorUtc,
    int IngestedCount,
    int DeferredCount,
    int BlockedCount);

public sealed record OutlookProfileStatusProjection(
    Guid ProfileId,
    string DisplayName,
    string State,
    OutlookIncrementalBasis IncrementalBasis,
    long ConfigurationRevision,
    TimeSpan Cadence,
    TimeSpan MaximumOverlap,
    OutlookSpoolStatus Spool,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSuccessfulCatchUpAtUtc,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<OutlookFolderStatusProjection> Folders);

public sealed record OutlookPageProjection(IReadOnlyList<OutlookProfileStatusProjection> Profiles)
{
    public static OutlookPageProjection Empty { get; } = new([]);
}

public interface IOutlookProjectionReader
{
    ValueTask<OutlookPageProjection> ReadAsync(CancellationToken cancellationToken);
}

public interface IOutlookSpoolHealthReader
{
    ValueTask<OutlookSpoolStatus> ReadAsync(string privateSpoolRoot, CancellationToken cancellationToken);
}

public sealed record OutlookSpoolPolicyOptions(IReadOnlyList<string> AllowedRoots, long MinimumAvailableBytes)
{
    public const long DefaultMinimumAvailableBytes = 256L * 1024 * 1024;
}

/// <summary>Validates a private spool without returning its path through a projection.</summary>
public sealed class LocalOutlookSpoolValidator(OutlookSpoolPolicyOptions options)
    : IOutlookSpoolValidator, IOutlookSpoolHealthReader
{
    private readonly KeyValuePair<string, string>[] _configuredRoots = options.AllowedRoots
        .Where(static path => !string.IsNullOrWhiteSpace(path))
        .Select(Canonicalise)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select((path, index) => KeyValuePair.Create($"spool-{index + 1}", path))
        .ToArray();

    public IReadOnlyList<OutlookSpoolChoice> Choices => _configuredRoots
        .Select(static pair => new OutlookSpoolChoice(pair.Key, $"Configured spool {pair.Key["spool-".Length..]}"))
        .ToArray();

    public async ValueTask<OutlookSpoolValidation> ValidateAsync(
        string spoolConfigurationKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = _configuredRoots.SingleOrDefault(pair =>
            string.Equals(pair.Key, spoolConfigurationKey, StringComparison.Ordinal));
        if (configured.Key is null)
        {
            throw new ArgumentException("Select a configured Outlook spool.", nameof(spoolConfigurationKey));
        }
        return await ValidatePrivateRootAsync(configured.Value, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<OutlookSpoolValidation> ValidatePrivateRootAsync(
        string canonical,
        CancellationToken cancellationToken)
    {
        if (!IsLocalFixedPath(canonical))
        {
            throw new ArgumentException("The Outlook spool must be an allowed local fixed-drive directory.", nameof(canonical));
        }
        if (!Directory.Exists(canonical))
        {
            throw new DirectoryNotFoundException("The Outlook spool directory does not exist.");
        }

        EnsureNoReparseTraversal(canonical);
        var hasRequiredAccess = CanEnumerate(canonical);
        var isWritable = await CanWriteAsync(canonical, cancellationToken).ConfigureAwait(false);
        var hasCapacity = HasCapacity(canonical, options.MinimumAvailableBytes);
        var validation = new OutlookSpoolValidation(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToUpperInvariant()))),
            true,
            hasRequiredAccess,
            hasCapacity,
            isWritable,
            canonical);
        validation.Validate();
        return validation;
    }

    public async ValueTask<OutlookSpoolStatus> ReadAsync(
        string privateSpoolRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            var canonical = Canonicalise(privateSpoolRoot);
            var configured = _configuredRoots.SingleOrDefault(pair =>
                string.Equals(pair.Value, canonical, StringComparison.OrdinalIgnoreCase));
            if (configured.Key is null)
            {
                return new OutlookSpoolStatus("Configured", "Blocked", "Unknown");
            }
            var validation = await ValidatePrivateRootAsync(canonical, cancellationToken).ConfigureAwait(false);
            return new OutlookSpoolStatus(
                "Configured",
                validation.HasRequiredAccess && validation.IsWritable ? "Healthy" : "Blocked",
                validation.HasSufficientCapacity ? "Sufficient" : "Low",
                configured.Key);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return new OutlookSpoolStatus("Configured", "Blocked", "Unknown");
        }
    }

    private static string Canonicalise(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("The Outlook spool must be a fully qualified local path.", nameof(path));
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsLocalFixedPath(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root) && new DriveInfo(root).DriveType == DriveType.Fixed;
    }

    private static bool CanEnumerate(string path)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToArray();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> CanWriteAsync(string path, CancellationToken cancellationToken)
    {
        var probe = Path.Combine(path, $".flux-outlook-spool-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.DeleteOnClose))
            {
                await stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool HasCapacity(string path, long minimumAvailableBytes)
    {
        var root = Path.GetPathRoot(path);
        return root is not null && new DriveInfo(root).AvailableFreeSpace >= minimumAvailableBytes;
    }

    private static void EnsureNoReparseTraversal(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The Outlook spool cannot traverse a reparse point.");
            }
            current = current.Parent;
        }
    }
}

public sealed class SqlOutlookProjectionReader(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    IOutlookSpoolHealthReader spoolHealthReader,
    PersistedOutlookSpoolRootPolicy? spoolRootPolicy = null) : IOutlookProjectionReader
{
    public async ValueTask<OutlookPageProjection> ReadAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var profiles = await context.OutlookCaptureProfiles.AsNoTracking()
            .Where(profile => profile.State != (int)OutlookCaptureState.Stale)
            .OrderBy(profile => profile.DisplayName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var profileIds = profiles.Select(profile => profile.Id).ToArray();
        var folders = await context.OutlookCaptureFolders.AsNoTracking()
            .Where(folder => profileIds.Contains(folder.ProfileId) && folder.State != (int)OutlookCaptureState.Disabled)
            .OrderBy(folder => folder.DisplayName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var folderIds = folders.Select(folder => folder.Id).ToArray();
        var exportCounts = await context.OutlookCaptureExports.AsNoTracking()
            .Where(export => export.FolderId != null && folderIds.Contains(export.FolderId.Value))
            .GroupBy(export => new { FolderId = export.FolderId!.Value, export.State })
            .Select(group => new { group.Key.FolderId, group.Key.State, Count = group.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var catchUpTimes = await context.OutlookCaptureOperations.AsNoTracking()
            .Where(operation => operation.ProfileId != null && profileIds.Contains(operation.ProfileId.Value) &&
                operation.Kind == "complete-catchup" && operation.Accepted)
            .GroupBy(operation => operation.ProfileId!.Value)
            .Select(group => new { ProfileId = group.Key, CompletedAtUtc = group.Max(operation => operation.CompletedAtUtc) })
            .ToDictionaryAsync(row => row.ProfileId, row => row.CompletedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        var results = new List<OutlookProfileStatusProjection>(profiles.Count);
        foreach (var profile in profiles)
        {
            OutlookSpoolStatus spool;
            try
            {
                var canonicalSpoolRoot = spoolRootPolicy?.RequireCanonicalBeforeIo(profile.SpoolRoot)
                    ?? throw new InvalidDataException("The persisted Outlook spool root is unavailable.");
                spool = await spoolHealthReader.ReadAsync(canonicalSpoolRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                spool = new OutlookSpoolStatus("Configured", "Blocked", "Unknown");
            }
            var folderResults = folders.Where(folder => folder.ProfileId == profile.Id)
                .Select(folder => new OutlookFolderStatusProjection(
                    folder.Id,
                    folder.DisplayName,
                    ((OutlookCaptureState)folder.State).ToString(),
                    folder.CursorUtc,
                    Count(folder.Id, OutlookExportState.Ingested),
                    Count(folder.Id, OutlookExportState.Deferred),
                    Count(folder.Id, OutlookExportState.Blocked)))
                .ToArray();
            results.Add(new OutlookProfileStatusProjection(
                profile.Id,
                profile.DisplayName,
                ((OutlookCaptureState)profile.State).ToString(),
                (OutlookIncrementalBasis)profile.IncrementalBasis,
                profile.ConfigurationRevision,
                TimeSpan.FromTicks(profile.CadenceTicks),
                TimeSpan.FromTicks(profile.MaximumOverlapTicks),
                spool,
                profile.CreatedAtUtc,
                profile.UpdatedAtUtc,
                catchUpTimes.GetValueOrDefault(profile.Id),
                profile.IncrementalBasis == (int)OutlookIncrementalBasis.ReceivedTime
                    ? ["Received-time capture may miss older moved messages; run manual reconciliation when needed."]
                    : [],
                folderResults));
        }
        return new OutlookPageProjection(results);

        int Count(Guid folderId, OutlookExportState state) => exportCounts
            .Where(row => row.FolderId == folderId && row.State == (int)state)
            .Sum(row => row.Count);
    }
}
