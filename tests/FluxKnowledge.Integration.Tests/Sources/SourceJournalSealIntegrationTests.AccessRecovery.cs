using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalSealIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [SupportedOSPlatform("windows")]
    public async Task Due_metadata_reconciliation_uses_guarded_repair_without_falling_through_on_uncertainty(bool cached, bool unconfirmed)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var path = scenario.Path;
        var source = Path.GetDirectoryName(path)!;
        using var restricted = RepairRestrictedToken();
        using var identity = WindowsIdentity.GetCurrent();
        var reader = identity.User!;
        var parent = new DirectoryInfo(source);
        var parentAcl = parent.GetAccessControl();
        parentAcl.AddAccessRule(new(reader, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        parent.SetAccessControl(parentAcl);
        if (!cached) ProtectFixtureFile(path);
        scenario.Options.Enabled = false;
        var root = await WindowsIdentity.RunImpersonatedAsync(restricted, () => scenario.ScanAsync("before-access-recovery"));
        await using (var db = scenario.Factory.CreateDbContext())
            Assert.Equal(cached ? 1 : 0, await db.SourceRevisions.CountAsync());
        if (cached) ProtectFixtureFile(path);
        else Assert.Contains(scenario.Enumeration.LastEvidence, issue => issue.RelativePath == "one.txt");
        WindowsIdentity.RunImpersonated(restricted, () => Assert.Throws<SourceAccessDeniedException>(() =>
            new WindowsSourceObservationReader().Open(root, "one.txt")));

        using var helper = new IsolatedRepairHelper();
        var policy = new SourceAccessPolicy(1, "FluxKnowledge.SourceAccess." + Guid.NewGuid().ToString("N"), reader.Value,
            [new(Path.Combine(Path.GetDirectoryName(source)!, "unrelated"), new string('a', 64))], [scenario.RetainedRoot]);
        File.WriteAllBytes(Path.Combine(helper.Root, "policy.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(policy));
        using var storage = new SourceAccessBrokerStorage(helper.Root);
        var journal = storage.JournalRoot;
        var broker = new WindowsSourceAccessBroker(storage.ReadPolicy(), journal, phase =>
        {
            if (unconfirmed && phase == SourceAccessRepairPhase.DescriptorWritten)
                throw new IOException("Fixture lost verification after the ACL write.");
        }, storage);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var server = SourceAccessPipeServer.ServeOneAsync(policy, broker, reader, deadline.Token);
        var grant = storage.ChangeFileInheritanceGrant(root.Id.Value, Guid.Empty, true, source, root.PhysicalIdentityFingerprint!, []);
        Assert.True(grant.Enabled);
        scenario.AccessRepairClient = new WindowsSourceAccessRepairClient(policy.PipeName, reader);
        scenario.Restart();
        scenario.SourceBytes = 0;
        scenario.Clock.Now += root.ReconciliationCadence + TimeSpan.FromSeconds(1);
        var claim = await scenario.ClaimAsync("automatic-weekly-reconciliation");
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var request = await db.SourceScanRequests.SingleAsync(row => row.Id == claim.ScanRequest.Id.Value);
            Assert.Equal(1, request.RequestKind);
            Assert.Equal("reconciliation", request.RequestedBy);
        }
        SourceIncrementalPlan? due = null;
        scenario.BeforeReuse = plan => { due = plan; return Task.CompletedTask; };
        await WindowsIdentity.RunImpersonatedAsync(restricted, () => scenario.RunAsync(claim));
        await server;
        Assert.NotNull(due);
        Assert.True(due.IsReconciliation);
        if (unconfirmed)
        {
            Assert.Contains(scenario.Enumeration.LastEvidence, issue => issue.RelativePath == "one.txt" && issue.Detail == "SourceAccessRepairUnconfirmed");
            Assert.Single(Directory.EnumerateFiles(journal));
        }
        else
        {
            Assert.Empty(scenario.Enumeration.LastEvidence);
            Assert.Empty(Directory.EnumerateFiles(journal));
        }
        Assert.Equal(cached ? 0 : 64, scenario.SourceBytes);
        Assert.False(new FileInfo(path).GetAccessControl().AreAccessRulesProtected);
        await using var verified = scenario.Factory.CreateDbContext();
        Assert.Single(await verified.SourceRevisions.ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("expired-lease")]
    [InlineData("configuration")]
    [InlineData("epoch")]
    [SupportedOSPlatform("windows")]
    public async Task Stale_repair_authority_does_not_dispatch(string failure)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var claim = await scenario.ClaimAsync("before-authority-change");
        var plan = Assert.IsType<SourceIncrementalPlan>(await scenario.PrepareAsync(claim));
        await using (var db = scenario.Factory.CreateDbContext())
        {
            switch (failure)
            {
                case "expired-lease": scenario.Clock.Now += TimeSpan.FromMinutes(11); break;
                case "configuration": (await db.SourceRootConfigurations.SingleAsync()).ConfigurationRevision++; break;
                case "epoch": (await db.SourceRootScanStates.SingleAsync()).RecoveryGeneration++; break;
            }
            await db.SaveChangesAsync();
        }
        var calls = 0;
        var coordinator = new SqlSourceIncrementalCoordinator(scenario.Factory, scenario.Clock, new WindowsSourceChangeJournalReader(),
            new WindowsSourceObservationReader(), scenario.Artifacts, scenario.Options);
        Assert.Equal(SourceAccessRepairOutcome.NotApproved, await coordinator.AdmitAccessRepairAsync(claim.SourceRoot, claim.ScanRequest,
            plan, () => { calls++; return SourceAccessRepairOutcome.Repaired; }, default));
        Assert.Equal(0, calls);
    }

    [SupportedOSPlatform("windows")]
    private sealed class IsolatedRepairHelper : IDisposable
    {
        public string Root { get; } = @"I:\FluxKnowledgeAccessSqlStage-" + Guid.NewGuid().ToString("N");

        public IsolatedRepairHelper()
        {
            var root = Directory.CreateDirectory(Root);
            var security = new DirectorySecurity();
            var administrators = new SecurityIdentifier("S-1-5-32-544");
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(administrators);
            foreach (var sid in new[] { administrators, new SecurityIdentifier("S-1-5-18") })
                security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            root.SetAccessControl(security);
            Directory.CreateDirectory(Path.Combine(Root, "journal"));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectFixtureFile(string path)
    {
        var acl = new FileSecurity();
        var administrators = new SecurityIdentifier("S-1-5-32-544");
        acl.SetOwner(administrators);
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { administrators, new SecurityIdentifier("S-1-5-18") })
            acl.AddAccessRule(new(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }

    [SupportedOSPlatform("windows")]
    private static SafeAccessTokenHandle RepairRestrictedToken()
    {
        using var current = WindowsIdentity.GetCurrent();
        var administrators = new SecurityIdentifier("S-1-5-32-544");
        var bytes = new byte[administrators.BinaryLength];
        administrators.GetBinaryForm(bytes, 0);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            if (!CreateRepairRestrictedToken(current.AccessToken, 1, 1, [new() { Sid = pin.AddrOfPinnedObject() }],
                    0, IntPtr.Zero, 0, IntPtr.Zero, out var restricted))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return restricted;
        }
        finally { pin.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RepairSidAndAttributes { internal IntPtr Sid; internal uint Attributes; }

    [DllImport("advapi32.dll", EntryPoint = "CreateRestrictedToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRepairRestrictedToken(SafeAccessTokenHandle token, uint flags, uint disableCount,
        RepairSidAndAttributes[] disable, uint deleteCount, IntPtr delete, uint restrictedCount, IntPtr restricted,
        out SafeAccessTokenHandle result);
}
