using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Domain.Pipeline;

public sealed record SourceIdentity
{
    public SourceIdentityId Id { get; private init; }

    public string SourceKind { get; private init; }

    public string StableKey { get; private init; }

    public static SourceIdentity ForLocalFile(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        return new SourceIdentity(SourceIdentityId.New(), "local file", fullPath);
    }

    private SourceIdentity(SourceIdentityId id, string sourceKind, string stableKey)
    {
        Id = id;
        SourceKind = sourceKind;
        StableKey = stableKey;
    }
}
