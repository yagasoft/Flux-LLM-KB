using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Pipeline;

public static class PipelineCompletionCriteria
{
    public static bool IsMet(PipelineStage completedStage, PipelineStage? nextStage) =>
        completedStage == PipelineStage.Publish && nextStage is null;
}
