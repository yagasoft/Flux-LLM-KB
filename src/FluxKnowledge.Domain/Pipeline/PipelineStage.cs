namespace FluxKnowledge.Domain.Pipeline;

public enum PipelineStage
{
    Identify,
    Extract,
    Normalise,
    CanonicalIndex,
    Embed,
    Publish
}
