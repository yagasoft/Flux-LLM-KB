using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Application.Ports;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

[SupportedOSPlatform("windows")]
public sealed class SourceAccessBrokerStorageTests
{
    [Theory]
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;BU)", false, false, true)]
    [InlineData("O:S-1-5-21-1-2-3-1001G:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)", false, false, false)]
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FW;;;BU)", false, false, false)]
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x2;;;BU)", true, false, true)]
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x40;;;BU)", true, false, false)]
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x10000;;;BU)", true, false, false)]
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x10102;;;BU)", true, true, true)]
    [InlineData("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x40000;;;BU)", true, true, false)]
    public void Trusted_namespace_requires_trusted_ownership_and_no_applicable_untrusted_mutation(
        string sddl, bool ancestor, bool volumeRoot, bool allowed)
    {
        var raw = new RawSecurityDescriptor(sddl); var bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
        var failure = Record.Exception(() => SourceAccessBrokerStorage.ValidateDescriptor(bytes, ancestor, volumeRoot));
        if (allowed) Assert.Null(failure);
        else Assert.IsType<UnauthorizedAccessException>(failure);
    }

    [Fact]
    public void Protected_policy_is_read_from_a_held_ordinary_file_and_cannot_be_replaced()
    {
        using var fixture = new Fixture();
        using var storage = new SourceAccessBrokerStorage(fixture.Helper);
        Assert.Equal(fixture.Policy.IndexerSid, storage.ReadPolicy().IndexerSid);
        storage.ValidateCurrent();
        Assert.Throws<IOException>(() => File.Move(fixture.PolicyPath, fixture.PolicyPath + ".replaced"));
    }

    [Fact]
    public void Policy_owned_by_an_untrusted_principal_is_refused_even_with_safe_aces()
    {
        using var fixture = new Fixture();
        var acl = new FileInfo(fixture.PolicyPath).GetAccessControl();
        acl.SetOwner(WindowsIdentity.GetCurrent().User!); new FileInfo(fixture.PolicyPath).SetAccessControl(acl);
        using var storage = new SourceAccessBrokerStorage(fixture.Helper);
        Assert.Throws<UnauthorizedAccessException>(() => storage.ReadPolicy());
    }

    [Fact]
    public void Policy_hardlink_and_redirected_journal_are_refused()
    {
        using var fixture = new Fixture();
        Assert.True(CreateHardLink(Path.Combine(fixture.Root, "policy-alias.json"), fixture.PolicyPath, IntPtr.Zero),
            new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        using (var storage = new SourceAccessBrokerStorage(fixture.Helper))
            Assert.Throws<UnauthorizedAccessException>(() => storage.ReadPolicy());
        Directory.Delete(fixture.Journal);
        Directory.CreateSymbolicLink(fixture.Journal, fixture.Root);
        Assert.ThrowsAny<UnauthorizedAccessException>(() => new SourceAccessBrokerStorage(fixture.Helper));
    }

    [Fact]
    public void Broker_with_protected_storage_repairs_source_read_without_granting_indexer_policy_or_journal_writes()
    {
        using var fixture = new Fixture();
        var indexerSid = WindowsIdentity.GetCurrent().User!;
        foreach (var directory in new[] { fixture.Root, fixture.Source })
        {
            var acl = new DirectoryInfo(directory).GetAccessControl();
            acl.AddAccessRule(new(indexerSid, (FileSystemRights)0x100081, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(acl);
        }
        var path = Path.Combine(fixture.Source, "protected-file.txt");
        File.WriteAllText(path, "Guarded protected fixture");
        var fileSecurity = new FileInfo(path).GetAccessControl();
        fileSecurity.SetAccessRuleProtection(true, false);
        fileSecurity.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            fileSecurity.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(fileSecurity);
        var policy = fixture.Policy with { IndexerSid = indexerSid.Value };
        File.WriteAllBytes(fixture.PolicyPath, JsonSerializer.SerializeToUtf8Bytes(policy));
        using var storage = new SourceAccessBrokerStorage(fixture.Helper);
        var broker = new WindowsSourceAccessBroker(storage.ReadPolicy(), fixture.Journal, storage: storage);
        using var token = SourceAccessBrokerTests.RestrictedToken();
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        Assert.Equal(SourceAccessRepairOutcome.Repaired, broker.Repair(new(fixture.Source, policy.Roots[0].IdentityFingerprint,
            "protected-file.txt", false), indexer, default));
        WindowsIdentity.RunImpersonated(token, () =>
        {
            using var source = WindowsSourcePathAccess.OpenRead(fixture.Source, path);
            using var reader = new StreamReader(new FileStream(source.Handle, FileAccess.Read));
            Assert.Equal("Guarded protected fixture", reader.ReadToEnd());
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(fixture.PolicyPath, "refused"));
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(fixture.Journal, "refused.json"), "refused"));
        });
        Assert.Empty(Directory.EnumerateFiles(fixture.Journal));
    }

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(FixtureVolume(), "FluxKnowledgeAccessFixture-" + Guid.NewGuid().ToString("N"));
        public string Helper => Path.Combine(Root, "helper");
        public string Journal => Path.Combine(Helper, "journal");
        public string Source => Path.Combine(Root, "source");
        public string PolicyPath => Path.Combine(Helper, "policy.json");
        public SourceAccessPolicy Policy => new(1, "FluxKnowledge.SourceAccess.fixture", "S-1-5-21-1-2-3-1001",
            [new(Source, PhysicalFileIdentity.GetDirectory(Source).IdentityFingerprint)], [Helper]);
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
            foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
                security.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Root).SetAccessControl(security);
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Journal);
            File.WriteAllBytes(PolicyPath, JsonSerializer.SerializeToUtf8Bytes(Policy));
        }
        public void Dispose()
        {
            var parent = Path.GetPathRoot(Root)!;
            if (Path.GetDirectoryName(Root) != parent || !Path.GetFileName(Root).StartsWith("FluxKnowledgeAccessFixture-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture escaped its dedicated volume-root directory.");
            Directory.Delete(Root, true);
        }

        private static string FixtureVolume()
        {
            var configured = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_SOURCE_ACCESS_VOLUME_ROOT");
            var root = configured ?? Path.GetPathRoot(Path.GetTempPath())!;
            if (!Path.IsPathFullyQualified(root) || !string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType != DriveType.Fixed)
                throw new InvalidOperationException("The source-access fixture requires an explicit local fixed volume root.");
            return root;
        }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string existing, IntPtr security);
}
