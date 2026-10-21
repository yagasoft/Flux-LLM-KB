namespace FluxKnowledge.Application.Ports;

/// <summary>Pins strict app-owned segment paths; never enables source cloud/reparse traversal.</summary>
public interface IIndexSegmentReadLeaseFactory
{
    IDisposable Acquire(string segmentDirectory);
    IDisposable AcquireManifest(string manifestDirectory);
    IDisposable AcquireDirectory(string directory, bool create = false);
}
