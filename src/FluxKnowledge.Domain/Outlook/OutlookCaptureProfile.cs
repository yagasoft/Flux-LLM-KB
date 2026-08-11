using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Domain.Outlook;

public sealed record OutlookCaptureProfile
{
    public OutlookCaptureProfileId Id { get; private init; }
    public string DisplayName { get; private init; }
    public OutlookIncrementalBasis IncrementalBasis { get; private init; }
    public OutlookCaptureState State { get; private init; }
    public long ConfigurationRevision { get; private init; }

    /// <summary>Received-time ordering can miss late-arriving historical items and requires an explicit manual reconciliation route.</summary>
    public bool RequiresManualReconciliation => IncrementalBasis == OutlookIncrementalBasis.ReceivedTime;

    public static OutlookCaptureProfile Create(
        string displayName,
        OutlookIncrementalBasis incrementalBasis = OutlookIncrementalBasis.LastModificationTime)
    {
        OutlookCaptureValidation.RequireDisplayName(displayName, nameof(displayName));
        if (!Enum.IsDefined(incrementalBasis))
        {
            throw new DomainInvariantException("Outlook incremental basis is invalid.");
        }

        return new OutlookCaptureProfile(
            OutlookCaptureProfileId.New(),
            displayName,
            incrementalBasis,
            OutlookCaptureState.Disabled,
            1);
    }

    public void EnsureCatchUpCanBeRequested()
    {
        if (State == OutlookCaptureState.Disabled)
        {
            throw new DomainInvariantException("A disabled Outlook profile cannot request catch-up work.");
        }
    }

    public static OutlookCaptureProfile Restore(
        OutlookCaptureProfileId id,
        string displayName,
        OutlookIncrementalBasis incrementalBasis,
        OutlookCaptureState state,
        long configurationRevision)
    {
        ArgumentNullException.ThrowIfNull(id);
        OutlookCaptureValidation.RequireNonEmpty(id.Value, nameof(id));
        var profile = Create(displayName, incrementalBasis);
        if (!Enum.IsDefined(state) || configurationRevision <= 0)
        {
            throw new DomainInvariantException("Outlook profile state or configuration revision is invalid.");
        }

        return profile with { Id = id, State = state, ConfigurationRevision = configurationRevision };
    }

    private OutlookCaptureProfile(
        OutlookCaptureProfileId id,
        string displayName,
        OutlookIncrementalBasis incrementalBasis,
        OutlookCaptureState state,
        long configurationRevision)
    {
        Id = id;
        DisplayName = displayName;
        IncrementalBasis = incrementalBasis;
        State = state;
        ConfigurationRevision = configurationRevision;
    }
}
