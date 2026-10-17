using System.Security.AccessControl;
using System.Security.Principal;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.TestSupport;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

public sealed class SourcePathFailureEvidenceTests
{
    [Fact]
    public async Task File_data_access_denial_is_distinct_from_unsafe_redirection()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var fixture = new CloudSourceFixture();
        var path = Path.Combine(fixture.Root, "denied.txt");
        File.WriteAllText(path, "Private ACL fixture");
        var file = new FileInfo(path);
        var original = file.GetAccessControl();
        var denied = file.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny));
        var enumerator = new LocalSourceEnumerator();
        try
        {
            file.SetAccessControl(denied);
            await foreach (var _ in enumerator.EnumerateAsync(SourceRootConfiguration.Create(fixture.Root, "Private ACL fixture", false, false, 1024), default))
                Assert.Fail("The denied source must not be read.");
            var issue = Assert.Single(enumerator.LastEvidence);
            Assert.Equal("denied.txt", issue.RelativePath);
            Assert.Equal("permission", issue.Kind);
            Assert.Equal("SourceAccessDenied", issue.Detail);
            Assert.Equal(5, issue.NativeErrorCode);
        }
        finally { file.SetAccessControl(original); }
    }

    [Fact]
    public async Task Escaping_link_is_reported_as_a_security_refusal_without_reading_the_target()
    {
        using var fixture = new CloudSourceFixture();
        var root = fixture.Directory("source");
        var target = Path.Combine(fixture.Root, "outside.txt");
        File.WriteAllText(target, "Outside the authorised source");
        var link = Path.Combine(root, "escape.txt");
        File.CreateSymbolicLink(link, target);
        var enumerator = new LocalSourceEnumerator();
        await foreach (var _ in enumerator.EnumerateAsync(SourceRootConfiguration.Create(root, "Private source", true, false, 1024), default))
            Assert.Fail("A link must not authorise its target.");
        var issue = Assert.Single(enumerator.LastEvidence);
        Assert.Equal("escape.txt", issue.RelativePath);
        Assert.Equal("security", issue.Kind);
        Assert.Equal("SourceReparseTypeNotSupported", issue.Detail);
    }
}
