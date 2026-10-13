namespace FluxKnowledge.Infrastructure.Inference.Search;

/// <summary>A failed native release cannot permit the machine owner to be released.</summary>
internal sealed class CpuNativeReleaseUnconfirmedException(params Exception[] failures)
    : AggregateException("cpu-search-native-release-unconfirmed", failures);
