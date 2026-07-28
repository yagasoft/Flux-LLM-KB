using System.Security.Cryptography;

namespace FluxKnowledge.Application.Indexing;

public static class EmbedDraftDefaults
{
    public const string ModelFingerprint = "deterministic-tokenhash-v1:256";
    public const int Dimensions = 256;
    public const string ArtifactContentType = "application/vnd.fluxknowledge.embedding-set+binary";
    public static readonly string MetadataChecksum = new('0', 64);
    public static readonly string EmptyArtifactContentHash =
        Convert.ToHexStringLower(SHA256.HashData([]));
}
