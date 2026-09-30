using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Explicit identity for code text that intentionally coexists with structured parsing.</summary>
public static class RepositorySourceTextPolicy
{
    public const string ProcessorVersion = "repository-source-text-v1";
    public static readonly string DescriptorFingerprint = Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes("repository-source-text-v1|git-tracked|strict-utf8|text/plain|dual-csharp")));

    public static bool IsEnabled(SourceRootConfiguration root, string path) =>
        root.DiscoveryMode == SourceDiscoveryMode.GitTracked && root.IndexSourceText && SourceClassifier.IsSourceTextExtension(path);

    public static bool MatchesActivity(string version, string descriptor) =>
        string.Equals(version, ProcessorVersion, StringComparison.Ordinal) &&
        string.Equals(descriptor, DescriptorFingerprint, StringComparison.Ordinal);
}
