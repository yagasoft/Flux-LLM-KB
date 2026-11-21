using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

[SupportedOSPlatform("windows")]
public sealed class OneDriveRepairNativeTests
{
    [OneDriveStagingFact("FLUXKNOWLEDGE_TEST_ONEDRIVE_RESIDENT")]
    public Task Real_resident_placeholder_repairs_the_denied_file_and_retries_on_the_same_identity() =>
        RunResidentScenario(missingRange: false);

    [OneDriveStagingFact("FLUXKNOWLEDGE_TEST_ONEDRIVE_RESIDENT")]
    public Task Fully_local_flags_with_a_reported_on_disk_gap_defer_the_actual_denied_read() =>
        RunResidentScenario(missingRange: true);

    private static async Task RunResidentScenario(bool missingRange)
    {
        var path = FixturePath("FLUXKNOWLEDGE_TEST_ONEDRIVE_RESIDENT");
        var source = Path.GetDirectoryName(path)!;
        var relative = Path.GetFileName(path);
        using var fixture = new SourceAccessBrokerStorageTests.Fixture();
        using var current = WindowsIdentity.GetCurrent();
        var sid = current.User!;
        var policy = fixture.Policy with { IndexerSid = sid.Value, PipeName = "FluxKnowledge.SourceAccess." + Guid.NewGuid().ToString("N") };
        File.WriteAllBytes(fixture.PolicyPath, JsonSerializer.SerializeToUtf8Bytes(policy));
        using var storage = new SourceAccessBrokerStorage(fixture.Helper);
        var fingerprint = PhysicalFileIdentity.GetDirectory(source).IdentityFingerprint;
        var registration = Guid.NewGuid();
        storage.ChangeFileInheritanceGrant(registration, Guid.Empty, true, source, fingerprint, []);
        using var parent = WindowsSourcePathAccess.OpenDirectoryPath(source);
        using var exactFile = WindowsSourcePathAccess.OpenRelative(parent.Handle, relative, 0x60080, 7, 0x40);
        Assert.Equal(path, PhysicalFileIdentity.GetFinalPath(exactFile), ignoreCase: true);
        Assert.Equal(1u, PhysicalFileIdentity.GetLinkCount(exactFile));
        var original = SourceReadGrant.Read(exactFile);
        var originalDescriptor = new RawSecurityDescriptor(original, 0);
        var protectedAcl = new FileSecurity();
        protectedAcl.SetAccessRuleProtection(true, false);
        foreach (var admin in new[] { "S-1-5-32-544", "S-1-5-18" })
            protectedAcl.AddAccessRule(new(new SecurityIdentifier(admin), FileSystemRights.FullControl, AccessControlType.Allow));
        try
        {
            SetDacl(exactFile, new RawSecurityDescriptor(protectedAcl.GetSecurityDescriptorBinaryForm(), 0));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var token = SourceAccessBrokerTests.RestrictedToken();
            string expected;
            using (WindowsSourcePathAccess.ExposePlaceholders())
            using (var attrs = WindowsSourcePathAccess.OpenRelative(parent.Handle, relative, 0x100080, 7, 0x60))
            {
                expected = PhysicalFileIdentity.Get(attrs);
                var info = WindowsSourcePathAccess.ReadMetadata(attrs);
                Assert.True(WindowsSourcePathAccess.IsCloudTag(info.ReparseTag) && (info.Attributes & 0x400) != 0, info.ToString());
                Assert.True(OneDriveRepairResidency.IsPlaceholder(attrs, info));
                OneDriveRepairResidency.RequireComplete(attrs, parent.Handle, info);
            }
            var denied = WindowsIdentity.RunImpersonated(token, () => Assert.Throws<SourceAccessDeniedException>(() =>
                WindowsSourcePathAccess.OpenRead(source, path, fingerprint, bindCloudDenial: true).Dispose()));
            Assert.Equal(5, Assert.IsType<System.ComponentModel.Win32Exception>(denied.InnerException).NativeErrorCode);
            Assert.Equal(expected, denied.DeniedFileIdentity);

            var (beforeEof, beforeRanges) = CaptureOnDiskRanges(exactFile);
            if (missingRange)
            {
                await AssertMissingRangeDefersAsync();
                return;
            }

            var writerFenced = false;
            var broker = new WindowsSourceAccessBroker(storage.ReadPolicy(), fixture.Journal, phase =>
            {
                if (phase != SourceAccessRepairPhase.IntentPersisted) return;
                var conflict = Assert.Throws<IOException>(() =>
                    WindowsSourcePathAccess.OpenRelative(parent.Handle, relative, 0x100082, 0, 0x60).Dispose());
                Assert.Equal(32, Assert.IsType<System.ComponentModel.Win32Exception>(conflict.InnerException).NativeErrorCode);
                writerFenced = true;
            }, storage: storage);
            var server = SourceAccessPipeServer.ServeOneAsync(policy, broker, sid, deadline.Token);
            var client = new WindowsSourceAccessRepairClient(policy.PipeName, sid);
            await WindowsIdentity.RunImpersonatedAsync(token, async () =>
            {
                using var retry = await SourceAccessRecovery.RunAsync(source, fingerprint, client,
                    () => WindowsSourcePathAccess.OpenRead(source, path, fingerprint, synchronous: true, bindCloudDenial: true),
                    (dispatch, _) => ValueTask.FromResult(dispatch()), deadline.Token, registration,
                    id => WindowsSourcePathAccess.OpenRead(source, path, fingerprint, id, synchronous: true, verifyRepairedCloud: true));
                Assert.Equal(expected, retry.Identity);
                using (var byId = WindowsSourcePathAccess.OpenFileReadByIdentity(parent.Handle, expected))
                    Assert.False(byId.IsInvalid, $"A readable file must open by ID under the same token (error {Marshal.GetLastPInvokeError()}).");
                using var reader = new StreamReader(new FileStream(retry.Handle, FileAccess.Read));
                Assert.Contains("Synthetic FluxKnowledge staging data only.", await reader.ReadToEndAsync(deadline.Token));
            });
            await server;
            var after = new RawSecurityDescriptor(SourceReadGrant.Read(exactFile), 0);
            Assert.True(writerFenced);
            Assert.False(after.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
            Assert.Equal(originalDescriptor.Owner, after.Owner);
            Assert.Equal(originalDescriptor.Group, after.Group);
            static string[] ExplicitAces(RawSecurityDescriptor descriptor) =>
                descriptor.DiscretionaryAcl!.Cast<GenericAce>().Where(ace => !ace.IsInherited).Select(ace =>
                {
                    var data = new byte[ace.BinaryLength];
                    ace.GetBinaryForm(data, 0);
                    return Convert.ToHexString(data);
                }).ToArray();
            var stagedDescriptor = new RawSecurityDescriptor(protectedAcl.GetSecurityDescriptorBinaryForm(), 0);
            Assert.Equal(ExplicitAces(stagedDescriptor), ExplicitAces(after));
            using var indexer = new WindowsIdentity(token.DangerousGetHandle());
            Assert.Equal(SourceAccessRepairOutcome.AlreadyReadable, broker.Repair(new(source, fingerprint, relative, false)
            {
                SourceRegistrationId = registration, DeniedFileIdentity = expected
            }, indexer, deadline.Token));
            Assert.Equal(SourceReadGrant.Bytes(after), SourceReadGrant.Read(exactFile));
            var (afterEof, afterRanges) = CaptureOnDiskRanges(exactFile);
            Assert.Equal(beforeEof, afterEof);
            Assert.Equal(beforeRanges, afterRanges);
            Assert.Empty(Directory.EnumerateFiles(fixture.Journal));

            async Task AssertMissingRangeDefersAsync()
            {
                var protectedDescriptor = SourceReadGrant.Read(exactFile);
                Assert.True(new RawSecurityDescriptor(protectedDescriptor, 0)
                    .ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
                Assert.True(OneDriveRepairResidency.CoversFile(beforeEof, beforeRanges));
                Assert.True(beforeEof > 3);
                var rangeQueries = 0;
                int ReportGap(SafeFileHandle file, long[] ranges, out uint returned)
                {
                    Assert.Equal(expected, PhysicalFileIdentity.Get(file));
                    var info = WindowsSourcePathAccess.ReadMetadata(file);
                    Assert.Equal(0u, info.PlaceholderState & 0x30u);
                    WindowsSourcePathAccess.EnsureContentAvailable(info);
                    rangeQueries++;
                    ranges[0] = 0;
                    ranges[1] = 1;
                    ranges[2] = 2;
                    ranges[3] = beforeEof - 2;
                    returned = 32;
                    return 0;
                }

                var staged = new WindowsSourceAccessBroker(storage.ReadPolicy(), fixture.Journal,
                    phase => Assert.Fail($"A missing range must never reach {phase}."), storage, ReportGap);
                var service = SourceAccessPipeServer.ServeOneAsync(policy, staged, sid, deadline.Token);
                var stagedClient = new WindowsSourceAccessRepairClient(policy.PipeName, sid);
                var openCount = 0;
                await WindowsIdentity.RunImpersonatedAsync(token, async () =>
                {
                    var unavailable = await Assert.ThrowsAsync<SourceContentUnavailableException>(async () =>
                    {
                        using var unexpected = await SourceAccessRecovery.RunAsync(source, fingerprint, stagedClient,
                            () =>
                            {
                                openCount++;
                                return WindowsSourcePathAccess.OpenRead(source, path, fingerprint, bindCloudDenial: true);
                            },
                            (dispatch, _) => ValueTask.FromResult(dispatch()), deadline.Token, registration,
                            _ => throw new InvalidOperationException("A deferred file must never be reopened for a read."));
                    });
                    Assert.Contains("No download was requested", unavailable.Message);
                });
                await service;
                Assert.Equal(1, openCount);
                Assert.Equal(1, rangeQueries);
                Assert.Equal(protectedDescriptor, SourceReadGrant.Read(exactFile));
                var (eof, actualRanges) = CaptureOnDiskRanges(exactFile);
                Assert.Equal(beforeEof, eof);
                Assert.Equal(beforeRanges, actualRanges);
                Assert.Empty(Directory.EnumerateFiles(fixture.Journal));
            }
        }
        finally
        {
            SetDacl(exactFile, originalDescriptor);
            Assert.Equal(original, SourceReadGrant.Read(exactFile));
        }
    }

    [OneDriveStagingFact("FLUXKNOWLEDGE_TEST_ONEDRIVE_CLOUD_ONLY")]
    public void Real_cloud_only_placeholder_leaves_permissions_untouched() =>
        RefusesNonresidentFile("FLUXKNOWLEDGE_TEST_ONEDRIVE_CLOUD_ONLY");

    [OneDriveStagingFact("FLUXKNOWLEDGE_TEST_ONEDRIVE_PARTIAL")]
    public void Real_partial_placeholder_leaves_permissions_untouched() =>
        RefusesNonresidentFile("FLUXKNOWLEDGE_TEST_ONEDRIVE_PARTIAL");

    private static void RefusesNonresidentFile(string variable)
    {
        var path = FixturePath(variable);
        var source = Path.GetDirectoryName(path)!;
        var relative = Path.GetFileName(path);
        using var fixture = new SourceAccessBrokerStorageTests.Fixture();
        using var current = WindowsIdentity.GetCurrent();
        var policy = fixture.Policy with { IndexerSid = current.User!.Value };
        File.WriteAllBytes(fixture.PolicyPath, JsonSerializer.SerializeToUtf8Bytes(policy));
        using var storage = new SourceAccessBrokerStorage(fixture.Helper);
        var fingerprint = PhysicalFileIdentity.GetDirectory(source).IdentityFingerprint;
        var registration = Guid.NewGuid();
        storage.ChangeFileInheritanceGrant(registration, Guid.Empty, true, source, fingerprint, []);
        using var parent = WindowsSourcePathAccess.OpenDirectoryPath(source);
        using var metadata = WindowsSourcePathAccess.OpenRelative(parent.Handle, relative, 0x20080, 7, 0x40);
        var before = SourceReadGrant.Read(metadata);
        var (eof, ranges) = CaptureOnDiskRanges(metadata);
        Assert.True(eof > 0);
        if (variable.EndsWith("_PARTIAL", StringComparison.Ordinal))
            Assert.InRange(ranges.Where((_, i) => (i & 1) != 0).Sum(), 1, eof - 1);
        else
            Assert.Empty(ranges);
        using var visibility = WindowsSourcePathAccess.ExposePlaceholders();
        var state = WindowsSourcePathAccess.ReadMetadata(metadata);
        using var token = SourceAccessBrokerTests.RestrictedToken();
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        var broker = new WindowsSourceAccessBroker(storage.ReadPolicy(), fixture.Journal, storage: storage);
        Assert.Equal(SourceAccessRepairOutcome.ContentUnavailable, broker.Repair(new(source, fingerprint, relative, false)
        {
            SourceRegistrationId = registration, DeniedFileIdentity = PhysicalFileIdentity.Get(metadata)
        }, indexer, default));
        Assert.Equal(before, SourceReadGrant.Read(metadata));
        Assert.Equal(state, WindowsSourcePathAccess.ReadMetadata(metadata));
        var (afterEof, afterRanges) = CaptureOnDiskRanges(metadata);
        Assert.Equal(eof, afterEof);
        Assert.Equal(ranges, afterRanges);
        Assert.Empty(Directory.EnumerateFiles(fixture.Journal));
    }

    private static string FixturePath(string variable)
    {
        var path = Path.GetFullPath(Environment.GetEnvironmentVariable(variable)!);
        var folderPath = Path.GetDirectoryName(path)!;
        Assert.Equal(@"E:\OneDrive - Link Development\Temp", Path.GetDirectoryName(folderPath), ignoreCase: true);
        Assert.StartsWith("FluxKnowledge-Acl-Staging-", Path.GetFileName(folderPath));
        if (variable.EndsWith("_RESIDENT", StringComparison.Ordinal))
            Assert.Equal("resident.txt", Path.GetFileName(path));
        return path;
    }

    private static void SetDacl(SafeFileHandle file, RawSecurityDescriptor descriptor)
    {
        const uint dacl = 4;
        const uint protectedDacl = 0x80000000;
        const uint unprotectedDacl = 0x20000000;
        var flags = descriptor.ControlFlags;
        if (flags.HasFlag(ControlFlags.DiscretionaryAclAutoInherited))
            descriptor = new RawSecurityDescriptor(SourceReadGrant.Bytes(descriptor), 0);
        descriptor.SetFlags(flags | (flags.HasFlag(ControlFlags.DiscretionaryAclAutoInherited)
            ? ControlFlags.DiscretionaryAclAutoInheritRequired : 0));
        var status = NtSetSecurityObject(file, dacl | (flags.HasFlag(ControlFlags.DiscretionaryAclProtected)
            ? protectedDacl : unprotectedDacl), SourceReadGrant.Bytes(descriptor));
        Assert.Equal(0, status);
    }

    private static (long Eof, long[] Ranges) CaptureOnDiskRanges(SafeFileHandle file)
    {
        var data = new long[512];
        Assert.Equal(0, CfGetPlaceholderRangeInfo(file, 1, 0, -1, data, 4096, out var count));
        Assert.InRange(count, 0u, 4096u);
        Assert.Equal(0u, count % 16);
        return (RandomAccess.GetLength(file), data[..(int)(count / sizeof(long))]);
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetSecurityObject(SafeFileHandle file, uint information, byte[] descriptor);

    [DllImport("cldapi.dll")]
    private static extern int CfGetPlaceholderRangeInfo(SafeFileHandle file, int infoClass, long start, long length,
        [Out] long[] ranges, uint capacity, out uint written);

    private sealed class OneDriveStagingFactAttribute : FactAttribute
    {
        public OneDriveStagingFactAttribute(string variable)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                Skip = "Requires the explicit isolated real OneDrive staging fixture.";
        }
    }
}
