using FluxKnowledge.Application.Operations;

namespace FluxKnowledge.Integrations.Windows;

/// <summary>
/// Runs only against an explicitly disposable plan and injected ports. No live port implementation
/// is supplied by this milestone.
/// </summary>
public sealed class FreshStartExecutor
{
    private readonly IFreshStartFileSystem _fileSystem;
    private readonly IFreshStartSql _sql;
    private readonly IFreshStartCodex _codex;
    private readonly IFreshStartVss _vss;

    public FreshStartExecutor(
        IFreshStartFileSystem fileSystem,
        IFreshStartSql sql,
        IFreshStartCodex codex,
        IFreshStartVss vss,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(codex);
        ArgumentNullException.ThrowIfNull(vss);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _fileSystem = fileSystem;
        _sql = sql;
        _codex = codex;
        _vss = vss;
    }

    public async Task<FreshStartExecutionResult> ExecuteAsync(
        FreshStartPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.Mode, FreshStartPlan.RequiredMode, StringComparison.Ordinal))
        {
            return FreshStartExecutionResult.Refused("fresh-start-mode-required");
        }

        if (!plan.IsDisposableSimulation || plan.Layout.IsProduction)
        {
            return FreshStartExecutionResult.Refused("live-execution-unavailable");
        }

        var initial = await ValidateInitialStateAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!initial.IsValid)
        {
            return FreshStartExecutionResult.Refused(initial.Reason!);
        }

        try
        {
            var pluginValidation = await ValidatePluginOperationAsync(plan, cancellationToken).ConfigureAwait(false);
            if (!pluginValidation.IsValid) return FreshStartExecutionResult.Refused(pluginValidation.Reason!);
            await _codex.ClearKnownPluginAsync(pluginValidation.Plugin!, cancellationToken).ConfigureAwait(false);

            var databaseValidation = await ValidateDatabaseOperationAsync(plan, cancellationToken).ConfigureAwait(false);
            if (!databaseValidation.IsValid) return FreshStartExecutionResult.Refused(databaseValidation.Reason!);
            await _sql.ResetAsync(databaseValidation.Database!, cancellationToken).ConfigureAwait(false);

            var removed = 0;
            foreach (var expectedFile in initial.Files)
            {
                var operationGuard = await ValidateRootAndSnapshotAsync(plan, cancellationToken).ConfigureAwait(false);
                if (!operationGuard.IsValid) return FreshStartExecutionResult.Refused(operationGuard.Reason!);
                var currentFile = await _fileSystem.InspectFileAsync(expectedFile.Path, cancellationToken).ConfigureAwait(false);
                if (currentFile is null || currentFile != expectedFile || !ValidateFile(plan, currentFile).IsValid)
                {
                    return FreshStartExecutionResult.Refused("file-identity-changed");
                }

                await _fileSystem.DeleteFileAsync(currentFile, cancellationToken).ConfigureAwait(false);
                removed++;
            }

            return FreshStartExecutionResult.Completed(removed);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return FreshStartExecutionResult.Refused("operation-failed");
        }
    }

    private async Task<InitialValidation> ValidateInitialStateAsync(
        FreshStartPlan plan,
        CancellationToken cancellationToken)
    {
        var guard = await ValidateRootAndSnapshotAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!guard.IsValid) return InitialValidation.Invalid(guard.Reason!);

        var database = await _sql.InspectAsync(cancellationToken).ConfigureAwait(false);
        var databaseValidation = ValidateDatabase(plan, database);
        if (!databaseValidation.IsValid) return InitialValidation.Invalid(databaseValidation.Reason!);

        var plugin = await _codex.InspectAsync(cancellationToken).ConfigureAwait(false);
        var pluginValidation = ValidatePlugin(plan, plugin);
        if (!pluginValidation.IsValid) return InitialValidation.Invalid(pluginValidation.Reason!);

        var files = await _fileSystem.EnumerateFilesAsync(plan.ResetRoots, cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var validation = ValidateFile(plan, file);
            if (!validation.IsValid) return InitialValidation.Invalid(validation.Reason!);
            if (!seen.Add(validation.CanonicalPath!)) return InitialValidation.Invalid("duplicate-file-identity");
        }

        return new InitialValidation(true, null, files.ToArray());
    }

    private async Task<OperationValidation> ValidateRootAndSnapshotAsync(
        FreshStartPlan plan,
        CancellationToken cancellationToken)
    {
        var root = await _fileSystem.InspectRootAsync(plan.Layout.Root, cancellationToken).ConfigureAwait(false);
        if (!SamePath(root.RootPath, plan.Layout.Root) ||
            !string.Equals(root.Owner, FreshStartOwnership.Application, StringComparison.Ordinal) ||
            root.IsReparsePoint ||
            !SamePath(root.ResolvedPath, plan.Layout.Root))
        {
            return OperationValidation.Invalid("unexpected-or-unowned-root");
        }

        var snapshot = await _vss.InspectAsync(plan.Volume, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(snapshot.Volume, plan.Volume, StringComparison.OrdinalIgnoreCase))
        {
            return OperationValidation.Invalid("unexpected-volume");
        }

        return snapshot.HasInheritedSnapshot
            ? OperationValidation.Invalid("inherited-snapshot-present")
            : OperationValidation.Valid();
    }

    private async Task<OperationValidation> ValidateDatabaseOperationAsync(
        FreshStartPlan plan,
        CancellationToken cancellationToken)
    {
        var guard = await ValidateRootAndSnapshotAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!guard.IsValid) return guard;
        var database = await _sql.InspectAsync(cancellationToken).ConfigureAwait(false);
        var validation = ValidateDatabase(plan, database);
        return validation.IsValid
            ? OperationValidation.Valid(database: database)
            : validation;
    }

    private async Task<OperationValidation> ValidatePluginOperationAsync(
        FreshStartPlan plan,
        CancellationToken cancellationToken)
    {
        var guard = await ValidateRootAndSnapshotAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!guard.IsValid) return guard;
        var plugin = await _codex.InspectAsync(cancellationToken).ConfigureAwait(false);
        var validation = ValidatePlugin(plan, plugin);
        return validation.IsValid
            ? OperationValidation.Valid(plugin: plugin)
            : validation;
    }

    private static OperationValidation ValidateDatabase(FreshStartPlan plan, FreshStartDatabaseState database)
    {
        var expected = plan.DatabaseIdentity;
        return string.Equals(database.Owner, FreshStartOwnership.Application, StringComparison.Ordinal) &&
               database.IsDisposable &&
               string.Equals(database.CatalogName, expected.CatalogName, StringComparison.Ordinal) &&
               SamePath(database.DataFilePath, expected.DataFilePath) &&
               SamePath(database.LogFilePath, expected.LogFilePath)
            ? OperationValidation.Valid(database: database)
            : OperationValidation.Invalid("unexpected-database-or-attached-files");
    }

    private static OperationValidation ValidatePlugin(FreshStartPlan plan, FreshStartPluginState plugin)
    {
        var expected = plan.PluginIdentity;
        return string.Equals(plugin.Owner, FreshStartOwnership.Application, StringComparison.Ordinal) &&
               plugin.IsDisposable &&
               SamePath(plugin.Identity.MarketplaceRoot, expected.MarketplaceRoot) &&
               string.Equals(plugin.Identity.MarketplaceName, expected.MarketplaceName, StringComparison.Ordinal) &&
               string.Equals(plugin.Identity.PluginName, expected.PluginName, StringComparison.Ordinal)
            ? OperationValidation.Valid(plugin: plugin)
            : OperationValidation.Invalid("foreign-plugin-identity");
    }

    private static LiveRootPathValidation ValidateFile(FreshStartPlan plan, FreshStartFileState file)
    {
        var lexical = plan.Layout.ValidateOwnedPath(file.Path, MissingPathInspector.Instance);
        if (!lexical.IsValid) return lexical;
        if (!string.Equals(file.Owner, FreshStartOwnership.Application, StringComparison.Ordinal))
        {
            return new(false, null, "unowned-file");
        }

        if (file.IsReparsePoint || !SamePath(file.ResolvedPath, lexical.CanonicalPath))
        {
            return new(false, null, "reparse-point-escape");
        }

        var approved = plan.ResetRoots.Any(root =>
            SamePath(lexical.CanonicalPath, root) || IsUnder(lexical.CanonicalPath!, root));
        return approved
            ? lexical
            : new LiveRootPathValidation(false, null, "file-outside-reset-roots");
    }

    private static bool IsUnder(string candidate, string root) =>
        candidate.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private sealed class MissingPathInspector : ILiveRootPathInspector
    {
        public static MissingPathInspector Instance { get; } = new();
        public LiveRootPathInspection Inspect(string path) => new(false, false, null);
    }

    private sealed record InitialValidation(bool IsValid, string? Reason, IReadOnlyList<FreshStartFileState> Files)
    {
        public static InitialValidation Invalid(string reason) => new(false, reason, []);
    }

    private sealed record OperationValidation(
        bool IsValid,
        string? Reason,
        FreshStartDatabaseState? Database = null,
        FreshStartPluginState? Plugin = null)
    {
        public static OperationValidation Valid(
            FreshStartDatabaseState? database = null,
            FreshStartPluginState? plugin = null) => new(true, null, database, plugin);

        public static OperationValidation Invalid(string reason) => new(false, reason);
    }
}

public static class FreshStartOwnership
{
    public const string Application = "FluxKnowledge";
}

public interface IFreshStartFileSystem
{
    ValueTask<FreshStartRootState> InspectRootAsync(string expectedRoot, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<FreshStartFileState>> EnumerateFilesAsync(IReadOnlyList<string> approvedRoots, CancellationToken cancellationToken);
    ValueTask<FreshStartFileState?> InspectFileAsync(string path, CancellationToken cancellationToken);
    ValueTask DeleteFileAsync(FreshStartFileState expected, CancellationToken cancellationToken);
}

public interface IFreshStartSql
{
    ValueTask<FreshStartDatabaseState> InspectAsync(CancellationToken cancellationToken);
    ValueTask ResetAsync(FreshStartDatabaseState expected, CancellationToken cancellationToken);
}

public interface IFreshStartCodex
{
    ValueTask<FreshStartPluginState> InspectAsync(CancellationToken cancellationToken);
    ValueTask ClearKnownPluginAsync(FreshStartPluginState expected, CancellationToken cancellationToken);
}

public interface IFreshStartVss
{
    ValueTask<FreshStartVolumeSnapshotState> InspectAsync(string expectedVolume, CancellationToken cancellationToken);
}

public sealed record FreshStartRootState(
    string RootPath,
    string Owner,
    bool IsReparsePoint,
    string? ResolvedPath);

public sealed record FreshStartFileState(
    string Path,
    string Owner,
    bool IsReparsePoint,
    string? ResolvedPath);

public sealed record FreshStartDatabaseState(
    string CatalogName,
    string DataFilePath,
    string LogFilePath,
    string Owner,
    bool IsDisposable);

public sealed record FreshStartPluginState(
    FreshStartPluginIdentity Identity,
    string Owner,
    bool IsDisposable);

public sealed record FreshStartVolumeSnapshotState(string Volume, bool HasInheritedSnapshot);

public sealed record FreshStartExecutionResult(
    bool Succeeded,
    string? Reason,
    int RemovedFileCount)
{
    internal static FreshStartExecutionResult Refused(string reason) => new(false, reason, 0);
    internal static FreshStartExecutionResult Completed(int removedFileCount) => new(true, null, removedFileCount);
}
