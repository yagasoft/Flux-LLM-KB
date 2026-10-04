namespace FluxKnowledge.Application.Pipeline;

public sealed record RepositorySourceDeferral(string Reason, bool Blocked = false);

/// <summary>A precise repository eligibility refusal; ownership failures are never this outcome.</summary>
public sealed class RepositorySourceDeferredException(RepositorySourceDeferral deferral)
    : InvalidOperationException(deferral.Reason)
{
    public RepositorySourceDeferral Deferral { get; } = deferral;
}
