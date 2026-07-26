using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Domain.Pipeline;

public sealed record SourceIdentity(SourceIdentityId Id, string SourceKind, string StableKey)
{
    public static SourceIdentity ForLocalFile(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        return new SourceIdentity(SourceIdentityId.New(), "local file", fullPath);
    }
}
