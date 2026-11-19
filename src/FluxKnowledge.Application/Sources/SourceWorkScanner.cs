using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>All current scans use size/date reconciliation. Historical checksum work cannot read source bytes.</summary>
public sealed class SourceWorkScanner(SourceScanWorker ordinary) : ISourceScanner
{
    public ValueTask<SourceScanResult> ScanAsync(SourceRootConfiguration root, SourceScanRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.IsVerification) throw new InvalidOperationException("SourceVerificationPolicyRetired");
        return ordinary.ScanAsync(root, request, ct);
    }
}
