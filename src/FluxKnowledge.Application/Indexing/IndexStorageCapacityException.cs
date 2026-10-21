namespace FluxKnowledge.Application.Indexing;

/// <summary>Publication can resume after app-owned index storage has sufficient headroom.</summary>
public sealed class IndexStorageCapacityException : IOException
{
    public IndexStorageCapacityException() : base("index-storage-capacity-deferred") { }
}
