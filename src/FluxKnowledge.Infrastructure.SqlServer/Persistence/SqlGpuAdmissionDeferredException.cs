namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Admission contention leaves the pending operation and durable wake untouched.</summary>
internal sealed class SqlGpuAdmissionDeferredException()
    : InvalidOperationException("GPU scheduler admission is temporarily fenced.");
