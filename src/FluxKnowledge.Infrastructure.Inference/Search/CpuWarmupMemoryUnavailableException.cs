namespace FluxKnowledge.Infrastructure.Inference.Search;

/// <summary>Loading was refused before model allocation and the machine owner was released.</summary>
public sealed class CpuWarmupMemoryUnavailableException() : InvalidOperationException("cpu-search-memory-unavailable");
