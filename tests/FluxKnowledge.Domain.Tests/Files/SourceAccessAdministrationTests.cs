using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

[SupportedOSPlatform("windows")]
public sealed class SourceAccessAdministrationTests
{
    private static byte[] Request(string root, string[]? protectedRoots = null) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        RootPath = root, IndexerSid = "S-1-5-21-1-2-3-1001", ProtectedRoots = protectedRoots ?? [@"I:\private-fixture"]
    });

    [Fact]
    public void Preparing_a_root_grant_is_read_only_and_preserves_the_physical_identity()
    {
        var path = Directory.CreateTempSubdirectory("FluxKnowledgeRootReadPlan_").FullName;
        try
        {
            using var lease = WindowsSourcePathAccess.OpenDirectoryPath(path, readPermissions: true);
            var before = SourceReadGrant.Read(lease.Handle);
            using var plan = JsonDocument.Parse(WindowsSourceAccessAdministration.PlanRootReadGrant(Request(path)));
            Assert.Equal(lease.Identity.IdentityFingerprint, plan.RootElement.GetProperty("RootIdentityFingerprint").GetString());
            Assert.Equal(before, SourceReadGrant.Read(lease.Handle));
            var proposed = SourceReadGrant.Propose(before, new SecurityIdentifier("S-1-5-21-1-2-3-1001"), true);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(proposed)), plan.RootElement.GetProperty("ExpectedAfterSha256").GetString());
            Assert.False(plan.RootElement.GetProperty("ActualIndexerReadVerified").GetBoolean());
        }
        finally { Directory.Delete(path); }
    }

    [Fact]
    public void Preparing_a_protected_location_grant_is_refused()
    {
        var path = Directory.CreateTempSubdirectory("FluxKnowledgeRootReadRefused_").FullName;
        try
        {
            var failure = Record.Exception(() => WindowsSourceAccessAdministration.PlanRootReadGrant(Request(path, [path])));
            Assert.IsType<InvalidDataException>(failure);
        }
        finally { Directory.Delete(path); }
    }

    [Fact]
    public void Preparing_a_root_grant_preserves_all_effective_read_denies()
    {
        var path = Directory.CreateTempSubdirectory("FluxKnowledgeRootReadDeny_").FullName;
        try
        {
            var security = new DirectoryInfo(path).GetAccessControl();
            security.AddAccessRule(new(new SecurityIdentifier("S-1-5-21-1-2-3-1001"), FileSystemRights.ReadData, AccessControlType.Deny));
            new DirectoryInfo(path).SetAccessControl(security);
            var failure = Record.Exception(() => WindowsSourceAccessAdministration.PlanRootReadGrant(Request(path)));
            Assert.IsType<UnauthorizedAccessException>(failure);
        }
        finally { Directory.Delete(path); }
    }

    [Fact]
    public async Task Interactive_admin_cannot_accidentally_start_the_production_broker()
    {
        Assert.False(WindowsIdentity.GetCurrent().IsSystem);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            WindowsSourceAccessAdministration.ServeAsync(@"I:\nonexistent-helper", default));
    }

    [Fact]
    public void Approved_root_provisioning_adds_only_read_and_preserves_existing_children()
    {
        using var fixture = new SourceAccessBrokerStorageTests.Fixture();
        var child = Path.Combine(fixture.Source, "existing.txt"); File.WriteAllText(child, "Existing fixture");
        using var childHandle = PhysicalFileIdentity.OpenReadNoFollow(child);
        var childBefore = SourceReadGrant.Read(childHandle);
        var plan = WindowsSourceAccessAdministration.PlanRootReadGrant(Request(fixture.Source, [fixture.Helper]));
        File.WriteAllBytes(Path.Combine(fixture.Helper, "root-read-plan.json"), plan);
        using var receipt = JsonDocument.Parse(WindowsSourceAccessAdministration.ApplyRootReadGrant(fixture.Helper,
            Convert.ToHexStringLower(SHA256.HashData(plan))));
        Assert.True(receipt.RootElement.GetProperty("Succeeded").GetBoolean());
        Assert.True(receipt.RootElement.GetProperty("Changed").GetBoolean());
        Assert.Equal(childBefore, SourceReadGrant.Read(childHandle));
        using var root = WindowsSourcePathAccess.OpenDirectoryPath(fixture.Source, readPermissions: true);
        var added = Assert.Single(new RawSecurityDescriptor(SourceReadGrant.Read(root.Handle), 0).DiscretionaryAcl!.Cast<CommonAce>(),
            ace => ace.SecurityIdentifier.Value == fixture.Policy.IndexerSid);
        Assert.Equal(0x100081, added.AccessMask);
        Assert.Equal(AceFlags.ObjectInherit | AceFlags.ContainerInherit, added.AceFlags);
        Assert.False(File.Exists(Path.Combine(fixture.Helper, "root-read-grant.pending.json")));
    }

    [Fact]
    public void Approval_hash_and_permission_drift_prevent_any_unreviewed_root_write()
    {
        using var fixture = new SourceAccessBrokerStorageTests.Fixture();
        var plan = WindowsSourceAccessAdministration.PlanRootReadGrant(Request(fixture.Source, [fixture.Helper]));
        File.WriteAllBytes(Path.Combine(fixture.Helper, "root-read-plan.json"), plan);
        Assert.Throws<InvalidDataException>(() => WindowsSourceAccessAdministration.ApplyRootReadGrant(fixture.Helper, new string('0', 64)));
        var security = new DirectoryInfo(fixture.Source).GetAccessControl();
        security.AddAccessRule(new(new SecurityIdentifier("S-1-5-21-1-2-3-1002"), FileSystemRights.ReadData, AccessControlType.Allow));
        new DirectoryInfo(fixture.Source).SetAccessControl(security);
        using var root = WindowsSourcePathAccess.OpenDirectoryPath(fixture.Source, readPermissions: true);
        var drifted = SourceReadGrant.Read(root.Handle);
        Assert.Throws<InvalidDataException>(() => WindowsSourceAccessAdministration.ApplyRootReadGrant(fixture.Helper,
            Convert.ToHexStringLower(SHA256.HashData(plan))));
        Assert.Equal(drifted, SourceReadGrant.Read(root.Handle));
        Assert.False(File.Exists(Path.Combine(fixture.Helper, "root-read-grant.pending.json")));
    }
}
