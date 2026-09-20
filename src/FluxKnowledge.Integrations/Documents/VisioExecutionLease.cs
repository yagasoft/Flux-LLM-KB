using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Documents;

/// <summary>One async-neutral machine execution gate, held from SQL ownership/recovery through proven COM cleanup.</summary>
public sealed class VisioExecutionLease : IVisioDocumentExtractor, IDisposable
{
    private readonly VisioDocumentExtractor _extractor;
    private readonly VerifiedNativeDirectory _parent;
    private readonly SafeFileHandle _file;
    private readonly object _sync = new();
    private bool _extracting;
    private bool _disposed;

    private VisioExecutionLease(VisioDocumentExtractor extractor, VerifiedNativeDirectory parent, SafeFileHandle file)
    {
        _extractor = extractor;
        _parent = parent;
        _file = file;
    }

    public static VisioExecutionLease Acquire(VisioDocumentExtractor extractor) =>
        AcquireForTest(extractor, Path.Combine(LiveRootLayout.Production.RuntimeRoot, "visio-execution.lock"));

    internal static VisioExecutionLease AcquireForTest(VisioDocumentExtractor extractor, string lockPath)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        if (extractor.GetUnavailableReason() is { } reason) throw new RetainedProcessorException(reason);
        try
        {
            var (parent, file) = new HandleRelativeNativeFileSystem().OpenOrCreateStableFile(lockPath);
            var lease = new VisioExecutionLease(extractor, parent, file);
            if (extractor.GetUnavailableReason() is { } changedReason)
            {
                lease.Dispose();
                throw new RetainedProcessorException(changedReason);
            }
            return lease;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RetainedProcessorException("visio-execution-gate-unavailable", innerException: exception);
        }
    }

    public string? GetUnavailableReason()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _extracting ? "visio-extractor-already-running" : _extractor.GetUnavailableReason();
        }
    }

    public async ValueTask<VisioDocumentResult> ExtractAsync(RetainedSourceBytes retained, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_extracting) throw new RetainedProcessorException("visio-extractor-already-running");
            _extracting = true;
        }
        try { return await _extractor.ExtractWithinLeaseAsync(retained, cancellationToken).ConfigureAwait(false); }
        finally { lock (_sync) { _extracting = false; } }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_extracting) throw new InvalidOperationException("visio-execution-still-running");
            _disposed = true;
            _file.Dispose();
            _parent.Dispose();
            // The lock file is deliberately never deleted/recreated: all callers bind to this same object.
        }
    }
}
