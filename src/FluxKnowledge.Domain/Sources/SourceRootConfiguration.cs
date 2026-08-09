using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Domain.Sources;

public sealed record SourceRootId(Guid Value)
{
    public static SourceRootId New() => new(Guid.NewGuid());
}

public sealed record SourceRootConfiguration
{
    public SourceRootId Id { get; private init; }

    public string CanonicalPath { get; private init; }

    public string DisplayName { get; private init; }

    public bool Recursive { get; private init; }

    public bool FollowLinks { get; private init; }

    public long MaximumFileBytes { get; private init; }

    public IReadOnlyList<string> IncludePatterns { get; private init; }

    public IReadOnlyList<string> ExcludePatterns { get; private init; }

    public IReadOnlyList<string> AllowedClassifications { get; private init; }

    public TimeSpan ReconciliationCadence { get; private init; }

    public SourceRootState State { get; private init; }

    public long ConfigurationRevision { get; private init; }

    public string? StateReason { get; private init; }

    /// <summary>Sanitised physical root identity captured when the root was admitted.</summary>
    public string? PhysicalIdentityFingerprint { get; private init; }

    /// <summary>Persisted roots are admitted only with a durable physical identity.</summary>
    public bool RequiresPhysicalIdentityValidation { get; private init; }

    public static SourceRootConfiguration Create(
        string canonicalPath,
        string displayName,
        bool recursive,
        bool followLinks,
        long maximumFileBytes,
        IReadOnlyList<string>? includePatterns = null,
        IReadOnlyList<string>? excludePatterns = null,
        IReadOnlyList<string>? allowedClassifications = null,
        TimeSpan? reconciliationCadence = null)
    {
        EnsureCanonicalPath(canonicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (maximumFileBytes <= 0)
        {
            throw new DomainInvariantException("Maximum file bytes must be positive.");
        }

        var effectiveIncludePatterns = CopyRules(includePatterns, nameof(includePatterns));
        var effectiveExcludePatterns = CopyRules(excludePatterns, nameof(excludePatterns));
        var effectiveAllowedClassifications = CopyRules(allowedClassifications, nameof(allowedClassifications));
        var effectiveReconciliationCadence = reconciliationCadence ?? TimeSpan.FromMinutes(15);
        if (effectiveReconciliationCadence <= TimeSpan.Zero)
        {
            throw new DomainInvariantException("Source root reconciliation cadence must be positive.");
        }

        return new SourceRootConfiguration(
            SourceRootId.New(),
            canonicalPath,
            displayName,
            recursive,
            followLinks,
            maximumFileBytes,
            effectiveIncludePatterns,
            effectiveExcludePatterns,
            effectiveAllowedClassifications,
            effectiveReconciliationCadence,
            SourceRootState.Enabled,
            1,
            null);
    }

    public SourceRootConfiguration Pause(string reason) => Transition(SourceRootState.Enabled, SourceRootState.Paused, reason);

    public SourceRootConfiguration Resume(string reason) => Transition(SourceRootState.Paused, SourceRootState.Enabled, reason);

    public static SourceRootConfiguration Restore(
        SourceRootId id,
        string canonicalPath,
        string displayName,
        bool recursive,
        bool followLinks,
        long maximumFileBytes,
        IReadOnlyList<string>? includePatterns,
        IReadOnlyList<string>? excludePatterns,
        IReadOnlyList<string>? allowedClassifications,
        TimeSpan reconciliationCadence,
        SourceRootState state,
        long configurationRevision,
        string? stateReason = null,
        string? physicalIdentityFingerprint = null,
        bool requiresPhysicalIdentityValidation = false)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (configurationRevision <= 0)
        {
            throw new DomainInvariantException("Source root configuration revision must be positive.");
        }

        var created = Create(canonicalPath, displayName, recursive, followLinks, maximumFileBytes,
            includePatterns, excludePatterns, allowedClassifications, reconciliationCadence);
        return created with
        {
            Id = id,
            State = state,
            ConfigurationRevision = configurationRevision,
            StateReason = stateReason,
            PhysicalIdentityFingerprint = physicalIdentityFingerprint,
            RequiresPhysicalIdentityValidation = requiresPhysicalIdentityValidation
        };
    }

    private SourceRootConfiguration Transition(SourceRootState requiredState, SourceRootState nextState, string reason)
    {
        EnsureBoundedReason(reason);
        if (State != requiredState)
        {
            throw new DomainInvariantException($"Source root must be {requiredState} before it can become {nextState}.");
        }

        return this with
        {
            State = nextState,
            StateReason = reason,
            ConfigurationRevision = ConfigurationRevision + 1
        };
    }

    private static void EnsureCanonicalPath(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        if (!Path.IsPathFullyQualified(canonicalPath) ||
            !string.Equals(Path.GetFullPath(canonicalPath), canonicalPath, StringComparison.Ordinal))
        {
            throw new DomainInvariantException("Source root path must be a canonical absolute path.");
        }
    }

    private static void EnsureBoundedReason(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > 1024)
        {
            throw new DomainInvariantException("Source root reason must be at most 1024 characters.");
        }
    }

    private static IReadOnlyList<string> CopyRules(IReadOnlyList<string>? values, string parameterName)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new DomainInvariantException($"{parameterName} cannot contain blank values.");
        }

        return Array.AsReadOnly(values.ToArray());
    }

    private SourceRootConfiguration(
        SourceRootId id,
        string canonicalPath,
        string displayName,
        bool recursive,
        bool followLinks,
        long maximumFileBytes,
        IReadOnlyList<string> includePatterns,
        IReadOnlyList<string> excludePatterns,
        IReadOnlyList<string> allowedClassifications,
        TimeSpan reconciliationCadence,
        SourceRootState state,
        long configurationRevision,
        string? stateReason)
    {
        Id = id;
        CanonicalPath = canonicalPath;
        DisplayName = displayName;
        Recursive = recursive;
        FollowLinks = followLinks;
        MaximumFileBytes = maximumFileBytes;
        IncludePatterns = includePatterns;
        ExcludePatterns = excludePatterns;
        AllowedClassifications = allowedClassifications;
        ReconciliationCadence = reconciliationCadence;
        State = state;
        ConfigurationRevision = configurationRevision;
        StateReason = stateReason;
    }
}
