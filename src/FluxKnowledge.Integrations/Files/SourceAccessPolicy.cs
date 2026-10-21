using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

public sealed record ApprovedSourceAccessRoot(string Path, string IdentityFingerprint, bool InheritableRead = true);

public sealed record SourceAccessPolicy(int Version, string PipeName, string IndexerSid,
    IReadOnlyList<ApprovedSourceAccessRoot> Roots, IReadOnlyList<string> ProtectedRoots)
{
    [SupportedOSPlatform("windows")]
    internal static SourceAccessPolicy Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 or > 1024 * 1024) throw new InvalidDataException("SourceAccessPolicyInvalid");
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        SourceAccessPipeProtocol.RejectDuplicateProperties(document.RootElement);
        var policy = document.RootElement.Deserialize<SourceAccessPolicy>(new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8,
            RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
        }) ?? throw new InvalidDataException("SourceAccessPolicyInvalid");
        try
        {
            SourceAccessPipeProtocol.ValidatePipeName(policy.PipeName);
            if (policy.Version != 1 || new SecurityIdentifier(policy.IndexerSid).Value != policy.IndexerSid ||
                policy.IndexerSid is "S-1-5-18" or "S-1-5-32-544" || policy.Roots.Count == 0 ||
                policy.Roots.Any(root => root is null || !Canonical(root.Path) || root.IdentityFingerprint.Length != 64 ||
                    !root.IdentityFingerprint.All(char.IsAsciiHexDigit) || root.IdentityFingerprint != root.IdentityFingerprint.ToLowerInvariant()) ||
                policy.Roots.Select(root => root.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != policy.Roots.Count ||
                policy.Roots.Select(root => root.IdentityFingerprint).Distinct(StringComparer.Ordinal).Count() != policy.Roots.Count ||
                policy.ProtectedRoots.Any(path => !Canonical(path)) || policy.Roots.Any(root => policy.ProtectedRoots.Any(path =>
                    WindowsSourcePathAccess.IsWithin(path, root.Path) || WindowsSourcePathAccess.IsWithin(root.Path, path))))
                throw new InvalidDataException("SourceAccessPolicyInvalid");
            return policy;
        }
        catch (ArgumentException exception) { throw new InvalidDataException("SourceAccessPolicyInvalid", exception); }
    }

    private static bool Canonical(string path) => Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) &&
        path == Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    internal ApprovedSourceAccessRoot? Resolve(SourceAccessRepairRequest request)
    {
        var root = Roots.SingleOrDefault(item => string.Equals(item.Path, request.RootPath, StringComparison.OrdinalIgnoreCase));
        if (root is null) return null;
        if (request.RootIdentityFingerprint is not null && request.RootIdentityFingerprint != root.IdentityFingerprint)
            throw new SourceSnapshotChangedException("The requested source root differs from its access policy.");
        _ = ResolveTarget(root.Path, request.RelativePath, request.IsDirectory);
        return root;
    }

    internal static string ResolveTarget(string root, string relative, bool directory)
    {
        if (relative == "." && directory) return root;
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0') ||
            relative.Split(['\\', '/']).Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new SourcePathSecurityException("SourcePathComponentInvalid", "The source-access request is not a normal relative path.");
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (!WindowsSourcePathAccess.IsWithin(root, target) || string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
            throw new SourcePathSecurityException("SourcePathOutsideRoot", "The source-access request escaped its approved root.");
        return target;
    }
}
