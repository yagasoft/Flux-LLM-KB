namespace FluxKnowledge.Infrastructure.Inference.Search;

/// <summary>Fixed offline model identities. FP16 is explicit probe selection; FP32 remains the default.</summary>
public enum BgeRerankerPrecision { Fp32, Fp16Internal }
