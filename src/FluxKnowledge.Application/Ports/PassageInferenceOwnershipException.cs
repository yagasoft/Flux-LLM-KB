namespace FluxKnowledge.Application.Ports;

/// <summary>Native ownership was not proved released; optional ranking cannot treat this as a usable result.</summary>
public sealed class PassageInferenceOwnershipException(Exception? innerException = null)
    : InvalidOperationException("passage-inference-ownership-unconfirmed", innerException);
