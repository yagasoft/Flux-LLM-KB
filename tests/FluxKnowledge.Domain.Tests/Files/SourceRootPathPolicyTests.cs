using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Integrations.Files;
using Xunit;
using Xunit.Sdk;

namespace FluxKnowledge.Domain.Tests.Files;

public sealed class SourceRootPathPolicyTests : IDisposable
{
    private readonly string _allowedRoot = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeSourceRoot_{Guid.NewGuid():N}");

    public SourceRootPathPolicyTests()
    {
        Directory.CreateDirectory(_allowedRoot);
    }

    [Fact]
    public void Validate_rejects_a_unc_path_before_it_can_be_resolved()
    {
        var policy = CreatePolicy();

        var exception = Assert.Throws<UnauthorizedAccessException>(() =>
            policy.ValidateAndCanonicalise(Request(@"\\server\share\corpus")));

        Assert.Contains("UNC", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_rejects_a_directory_outside_the_configured_fixed_drive_roots()
    {
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeOutside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        try
        {
            var exception = Assert.Throws<UnauthorizedAccessException>(() =>
                CreatePolicy().ValidateAndCanonicalise(Request(outsideRoot)));

            Assert.Contains("configured", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void Validate_rejects_a_reparse_alias_escape_outside_configured_physical_root()
    {
        var target = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeTarget_{Guid.NewGuid():N}");
        var link = Path.Combine(_allowedRoot, "linked");
        Directory.CreateDirectory(target);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception linkException) when (linkException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                throw SkipException.ForSkip($"Directory symbolic links are unavailable: {linkException.Message}");
            }

            var exception = Assert.Throws<UnauthorizedAccessException>(() =>
                CreatePolicy().ValidateAndCanonicalise(Request(link)));

            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void Validate_rejects_a_protected_deployment_sql_cache_or_secret_location()
    {
        var protectedRoot = Path.Combine(_allowedRoot, "protected");
        Directory.CreateDirectory(protectedRoot);
        var policy = new SourceRootPathPolicy(
            new LocalIngressOptions([_allowedRoot]),
            new SourceRootPathPolicyOptions([protectedRoot]));

        var exception = Assert.Throws<UnauthorizedAccessException>(() =>
            policy.ValidateAndCanonicalise(Request(protectedRoot)));

        Assert.Contains("protected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Policy_allows_an_absent_protected_sql_parent_to_be_registered_without_weakening_existing_root_validation()
    {
        var absentProtectedRoot = Path.Combine(_allowedRoot, "not-created", "sql-data");

        var policy = new SourceRootPathPolicy(
            new LocalIngressOptions([_allowedRoot]),
            new SourceRootPathPolicyOptions([absentProtectedRoot]));

        Assert.NotNull(policy);
    }

    [Fact]
    public void Validate_returns_canonical_physical_identity_and_sanitised_permission_evidence()
    {
        var nested = Path.Combine(_allowedRoot, "corpus");
        Directory.CreateDirectory(nested);

        var result = CreatePolicy().ValidateAndCanonicalise(Request(Path.Combine(nested, ".")));

        Assert.Equal(Path.GetFullPath(nested), result.CanonicalPath);
        Assert.Equal(Path.GetFullPath(nested), result.PhysicalIdentity.CanonicalPath);
        Assert.True(result.PhysicalIdentity.IsFixedNtfs);
        Assert.True(result.PermissionEvidence.CanEnumerate);
        Assert.DoesNotContain(nested, result.PermissionEvidenceJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pathFingerprint", result.PermissionEvidenceJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_resolves_case_aliases_to_one_final_path_and_stable_physical_identity()
    {
        var corpus = Path.Combine(_allowedRoot, "CaseSensitiveName");
        Directory.CreateDirectory(corpus);
        var alias = Path.Combine(_allowedRoot, "casesensitivename");
        if (!Directory.Exists(alias))
        {
            throw SkipException.ForSkip("The local filesystem does not expose a case-insensitive directory alias.");
        }

        var first = CreatePolicy().ValidateAndCanonicalise(Request(corpus));
        var second = CreatePolicy().ValidateAndCanonicalise(Request(alias));

        Assert.Equal(first.CanonicalPath, second.CanonicalPath);
        Assert.Equal(first.PhysicalIdentity, second.PhysicalIdentity);
        Assert.NotEqual(first.CanonicalPath, first.PermissionEvidence.PathFingerprint);
    }

    public void Dispose() => Directory.Delete(_allowedRoot, recursive: true);

    private SourceRootPathPolicy CreatePolicy() =>
        new(new LocalIngressOptions([_allowedRoot]));

    private static SourceRootCreateRequest Request(string path) => new(
        path,
        "Test corpus",
        Recursive: true,
        IncludePatterns: ["*.txt"],
        ExcludePatterns: ["bin/**"],
        FollowLinks: false,
        MaximumFileBytes: 1024,
        AllowedClassifications: ["text/plain"],
        ReconciliationCadence: TimeSpan.FromMinutes(15),
        RequestedBy: "test");
}
