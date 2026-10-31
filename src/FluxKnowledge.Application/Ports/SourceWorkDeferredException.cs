namespace FluxKnowledge.Application.Ports;

public sealed class SourceWorkDeferredException() : InvalidOperationException("source-work-not-admitted");
