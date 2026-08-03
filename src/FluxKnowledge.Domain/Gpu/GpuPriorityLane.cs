namespace FluxKnowledge.Domain.Gpu;

public enum GpuPriorityLane
{
    InteractiveRetrieval = 0,
    DocumentIndexing = 1,
    ImageOcr = 2,
    ImageEnrichment = 3,
    VideoOrUnknown = 4
}
