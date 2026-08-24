using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Publishes the retained-only media metadata descriptor without resolving the scoped parser.</summary>
public sealed class MediaMetadataCapabilityHandler : ILocalSourceCapabilityHandler
{
    public SourceCapabilityDescriptor Descriptor => MediaMetadataRetainedProcessor.Capability;
}
