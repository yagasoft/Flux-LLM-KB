using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Explicit identity for re-evaluation of immutable retained revisions under the revised defaults.</summary>
public static class RetainedDefaultPolicy
{
    public const string Version = "default-source-policy-20261016-v1";
    public const string RelationshipKind = "superseded-by-default-policy";
    public static readonly string TextFingerprint = Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(Version + ":strict-utf8:32MiB:inert-source-text")));
    public static readonly SourceCapabilityDescriptor UnsupportedBinary = new(new Guid("c093898c-ab53-4c28-b4ea-06c3a2b32dd4"),
        "retained-binary-format-parser-unavailable", Version + "-unsupported", ExecutionClass.DeferredCapability,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Version + ":unsupported-binary-v1"))),
        SourceActivityKind.DocumentParsing, "UnsupportedBinary");
    public static bool IsCorrectedTextReason(string? reason) => reason is
        "File extension is unknown to local source ingestion." or
        "Source code ingestion is not enabled for this root." or
        "File exceeds the effective UTF-8 text ingestion limit.";
}

public sealed record RetainedPolicyAdmission(
    Guid RootId, Guid SourceRevisionId, Guid PredecessorActivityId, long ConfigurationRevision,
    string InputSha256, long ByteLength, string ProcessorVersion, string DescriptorFingerprint,
    int ActivityKind, bool IsText);
