using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

[SupportedOSPlatform("windows")]
public sealed class SourceAccessBrokerTests
{
    [Fact]
    public void Actual_restricted_indexer_is_denied_before_then_reads_without_write_or_acl_rights()
    {
        using var fixture = new Fixture();
        var path = fixture.OldFile("old.txt");
        using var token = RestrictedToken();
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        WindowsIdentity.RunImpersonated(token, () => Assert.ThrowsAny<UnauthorizedAccessException>(() =>
            WindowsSourcePathAccess.OpenRead(fixture.Source, path).Dispose()));
        var broker = fixture.Broker();
        Assert.Equal(SourceAccessRepairOutcome.Repaired, broker.Repair(fixture.Request("old.txt"), indexer, default));
        WindowsIdentity.RunImpersonated(token, () =>
        {
            Assert.Equal("Private source fixture", ReadGuarded(fixture.Source, path));
            using var write = Open(path, 0x2, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            Assert.True(write.IsInvalid);
            Assert.Equal(5, Marshal.GetLastPInvokeError());
            using var dac = Open(path, 0x40000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            Assert.True(dac.IsInvalid);
            Assert.Equal(5, Marshal.GetLastPInvokeError());
        });
        var beforeRepeat = fixture.Acl(path);
        Assert.Equal(SourceAccessRepairOutcome.AlreadyReadable, broker.Repair(fixture.Request("old.txt"), indexer, default));
        Assert.Equal(beforeRepeat, fixture.Acl(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Journal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_and_inherited_denies_for_deny_only_group_are_not_overridden(bool inherited)
    {
        using var fixture = new Fixture();
        var path = fixture.OldFile("denied.txt");
        var acl = new FileInfo(path).GetAccessControl();
        acl.AddAccessRule(new(new SecurityIdentifier("S-1-5-32-544"), FileSystemRights.ReadData, AccessControlType.Deny));
        new FileInfo(path).SetAccessControl(acl);
        if (inherited)
        {
            // Native fixture marks this deny inherited, where an added explicit allow
            // would otherwise override it. The broker must inspect the actual token.
            var raw = new RawSecurityDescriptor(fixture.Acl(path), 0);
            var dacl = raw.DiscretionaryAcl!;
            var denied = dacl.Cast<CommonAce>().First(ace => ace.AceQualifier == AceQualifier.AccessDenied);
            denied.AceFlags |= AceFlags.Inherited;
            dacl.RemoveAce(0);
            dacl.InsertAce(dacl.Count, denied);
            Fixture.SetNative(path, raw);
        }
        using var token = RestrictedToken();
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        var before = fixture.Acl(path);
        Assert.Equal(SourceAccessRepairOutcome.ExplicitDeny, fixture.Broker().Repair(fixture.Request("denied.txt"), indexer, default));
        Assert.Equal(before, fixture.Acl(path));
    }

    [Fact]
    public void Restricted_sid_check_is_required_in_addition_to_user_and_enabled_groups()
    {
        using var fixture = new Fixture();
        var path = fixture.OldFile("restricted.txt");
        using var token = RestrictedToken(new SecurityIdentifier("S-1-5-21-1-2-3-99999"));
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        var before = fixture.Acl(path);
        Assert.Equal(SourceAccessRepairOutcome.UnsupportedPermissions,
            fixture.Broker().Repair(fixture.Request("restricted.txt"), indexer, default));
        Assert.Equal(before, fixture.Acl(path));
    }

    [Fact]
    public void Inherited_owner_rights_read_deny_is_not_overridden_by_an_explicit_user_allow()
    {
        using var fixture = new Fixture();
        var path = fixture.OldFile("owner-denied.txt");
        var security = new FileInfo(path).GetAccessControl();
        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        new FileInfo(path).SetAccessControl(security);
        var raw = new RawSecurityDescriptor(fixture.Acl(path), 0);
        raw.DiscretionaryAcl!.InsertAce(raw.DiscretionaryAcl.Count,
            new CommonAce(AceFlags.Inherited, AceQualifier.AccessDenied, 1,
                new SecurityIdentifier("S-1-3-4"), false, null));
        Fixture.SetNative(path, raw);
        using var token = RestrictedToken();
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        var before = fixture.Acl(path);
        Assert.False(SourceAccessTokenPermissions.CanRead(before, indexer));
        Assert.True(SourceAccessTokenPermissions.HasApplicableDeny(before, indexer));
        Assert.Equal(SourceAccessRepairOutcome.ExplicitDeny,
            fixture.Broker().Repair(fixture.Request("owner-denied.txt"), indexer, default));
        Assert.Equal(before, fixture.Acl(path));
        Assert.False(Directory.Exists(fixture.Journal));
    }

    [Fact]
    public void Held_directory_chain_detects_an_ancestor_reparse_change()
    {
        using var fixture = new Fixture();
        using var chain = WindowsSourcePathAccess.OpenDirectoryPath(fixture.Source);
        using var ancestor = Open(fixture.Source, 0x40000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(ancestor.IsInvalid);
        var substitute = System.Text.Encoding.Unicode.GetBytes(@"\??\" + fixture.Outside + '\0');
        var display = System.Text.Encoding.Unicode.GetBytes(fixture.Outside + '\0');
        var reparse = new byte[16 + substitute.Length + display.Length];
        BitConverter.GetBytes(0xa0000003u).CopyTo(reparse, 0);
        BitConverter.GetBytes(checked((ushort)(reparse.Length - 8))).CopyTo(reparse, 4);
        BitConverter.GetBytes(checked((ushort)(substitute.Length - 2))).CopyTo(reparse, 10);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(reparse, 12);
        BitConverter.GetBytes(checked((ushort)(display.Length - 2))).CopyTo(reparse, 14);
        substitute.CopyTo(reparse, 16); display.CopyTo(reparse, 16 + substitute.Length);
        Assert.True(DeviceIoControl(ancestor, 0x900a4, reparse, reparse.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
            new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        try { Assert.Throws<SourcePathSecurityException>(chain.ValidateCurrent); }
        finally
        {
            var delete = new byte[8]; BitConverter.GetBytes(0xa0000003u).CopyTo(delete, 0);
            Assert.True(DeviceIoControl(ancestor, 0x900ac, delete, delete.Length, IntPtr.Zero, 0, out _, IntPtr.Zero));
        }
    }

    [Fact]
    public void Replacement_and_same_volume_move_receive_their_own_idempotent_read_grants()
    {
        using var fixture = new Fixture();
        var first = fixture.OldFile("current.txt");
        using var token = RestrictedToken();
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        var broker = fixture.Broker();
        Assert.Equal(SourceAccessRepairOutcome.Repaired, broker.Repair(fixture.Request("current.txt"), indexer, default));
        File.Move(first, Path.Combine(fixture.Outside, "previous.txt"));
        var replacement = fixture.OldFile("replacement.txt");
        File.Move(replacement, first);
        WindowsIdentity.RunImpersonated(token, () => Assert.ThrowsAny<UnauthorizedAccessException>(() => ReadGuarded(fixture.Source, first)));
        Assert.Equal(SourceAccessRepairOutcome.Repaired, broker.Repair(fixture.Request("current.txt"), indexer, default));
        WindowsIdentity.RunImpersonated(token, () => Assert.Equal("Private source fixture", ReadGuarded(fixture.Source, first)));
    }

    private static string ReadGuarded(string root, string path)
    {
        using var source = WindowsSourcePathAccess.OpenRead(root, path);
        using var reader = new StreamReader(new FileStream(source.Handle, FileAccess.Read));
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("../outside/escape.txt")]
    [InlineData("..\\outside\\escape.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("folder/./file.txt")]
    [InlineData("folder /file.txt")]
    [InlineData("/absolute.txt")]
    public void Escaping_or_ambiguous_paths_do_not_change_permissions(string relative)
    {
        using var fixture = new Fixture();
        using var token = RestrictedToken();
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        Assert.Equal(SourceAccessRepairOutcome.OutsideScope, fixture.Broker().Repair(fixture.Request(relative), indexer, default));
        Assert.False(Directory.Exists(fixture.Journal));
    }

    [Fact]
    public void Symbolic_link_and_hardlink_cannot_delegate_a_grant_outside_the_source()
    {
        using var fixture = new Fixture();
        var original = fixture.OldFile("original.txt");
        var outside = Path.Combine(fixture.Outside, "other-link.txt");
        Assert.True(CreateHardLink(outside, original, IntPtr.Zero));
        using var token = RestrictedToken();
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        var before = fixture.Acl(original);
        var broker = fixture.Broker();
        Assert.Equal(SourceAccessRepairOutcome.OutsideScope, broker.Repair(fixture.Request("original.txt"), indexer, default));
        Assert.Equal(before, fixture.Acl(original));
        File.CreateSymbolicLink(Path.Combine(fixture.Source, "escape.txt"), outside);
        Assert.Equal(SourceAccessRepairOutcome.OutsideScope, broker.Repair(fixture.Request("escape.txt"), indexer, default));
        Assert.Equal(before, fixture.Acl(original));
    }

    [Fact]
    public void Replaced_root_identity_and_unapproved_root_are_refused()
    {
        using var fixture = new Fixture();
        var request = fixture.Request("file.txt");
        var broker = fixture.Broker();
        using var token = RestrictedToken();
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        Assert.Equal(SourceAccessRepairOutcome.NotApproved,
            broker.Repair(request with { RootPath = fixture.Outside }, indexer, default));
        Directory.Move(fixture.Source, fixture.Source + "-old");
        Directory.CreateDirectory(fixture.Source);
        fixture.OldFile("file.txt");
        Assert.Equal(SourceAccessRepairOutcome.IdentityChanged, broker.Repair(request, indexer, default));
    }

    [Fact]
    public async Task Pipe_authenticates_actual_indexer_token_and_guarded_retry_reads_content()
    {
        using var fixture = new Fixture();
        var path = fixture.OldFile("pipe.txt");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var policy = fixture.Policy() with { PipeName = "FluxKnowledge.SourceAccess." + Guid.NewGuid().ToString("N") };
        var owner = WindowsIdentity.GetCurrent().User!;
        var server = SourceAccessPipeServer.ServeOneAsync(policy, fixture.Broker(), owner, cancellation.Token);
        var client = new WindowsSourceAccessRepairClient(policy.PipeName, owner);
        using var token = RestrictedToken();
        WindowsIdentity.RunImpersonated(token, () =>
        {
            using var source = SourceAccessRecovery.Run(fixture.Source, fixture.Request("pipe.txt").RootIdentityFingerprint, client,
                () => WindowsSourcePathAccess.OpenRead(fixture.Source, path), cancellation.Token);
            using var reader = new StreamReader(new FileStream(source.Handle, FileAccess.Read));
            Assert.Equal("Private source fixture", reader.ReadToEnd());
        });
        await server;
        Assert.Empty(Directory.EnumerateFiles(fixture.Journal));
    }

    [Fact]
    public async Task Actual_pipe_recovery_reaches_discovery_retention_and_supported_readback()
    {
        using var fixture = new Fixture();
        fixture.OldFile("recovered.txt");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var policy = fixture.Policy() with { PipeName = "FluxKnowledge.SourceAccess." + Guid.NewGuid().ToString("N") };
        var owner = WindowsIdentity.GetCurrent().User!;
        var server = SourceAccessPipeServer.ServeOneAsync(policy, fixture.Broker(), owner, cancellation.Token);
        var client = new WindowsSourceAccessRepairClient(policy.PipeName, owner);
        using var token = RestrictedToken();
        await WindowsIdentity.RunImpersonatedAsync(token, async () =>
        {
            var enumerator = new LocalSourceEnumerator(sourceAccessRepairClient: client);
            var files = new List<FluxKnowledge.Application.Ports.SourceDiscoveredFile>();
            await foreach (var file in enumerator.EnumerateAsync(SourceRootConfiguration.Create(
                fixture.Source, "Access recovery fixture", false, false, 1024), cancellation.Token)) files.Add(file);
            var snapshot = Assert.Single(files);
            Assert.Equal("Private source fixture", snapshot.Classification.Text);
            Assert.Empty(enumerator.LastEvidence);
            using var store = new ContentAddressedSourceArtifactStore(fixture.Outside, sourceAccessRepairClient: client);
            var receipt = await store.PutFileAsync(snapshot, new(snapshot.ContentSha256, "text/plain", snapshot.ByteLength), cancellation.Token);
            if (receipt.PublicationLease is not null) await receipt.PublicationLease.DisposeAsync();
            var readback = await ContainedFileReader.ReadAsync(fixture.Outside, receipt.StoreRelativePath, 1024,
                cancellation.Token, snapshot.ContentSha256, snapshot.ByteLength);
            Assert.Equal("Private source fixture", System.Text.Encoding.UTF8.GetString(readback.Bytes));
        });
        await server;
        Assert.Empty(Directory.EnumerateFiles(fixture.Journal));
    }

    [Fact]
    public void Repaired_protected_directory_gives_new_children_read_access_without_rewriting_existing_children()
    {
        using var fixture = new Fixture();
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Source, "old-folder")).FullName;
        var security = new DirectoryInfo(directory).GetAccessControl();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var sid in new[] { "S-1-5-32-544", "S-1-5-18" })
            security.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
        var old = Path.Combine(directory, "existing.txt");
        File.WriteAllText(old, "Existing child");
        var beforeOld = fixture.Acl(old);
        using var token = RestrictedToken();
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        WindowsIdentity.RunImpersonated(token, () => Assert.Throws<SourceAccessDeniedException>(() =>
            WindowsSourcePathAccess.OpenDirectoryPath(directory, enumerate: true)));
        Assert.Equal(SourceAccessRepairOutcome.Repaired, fixture.Broker().Repair(
            fixture.Request("old-folder") with { IsDirectory = true }, indexer, default));
        Assert.Equal(beforeOld, fixture.Acl(old));
        var newDirectory = Directory.CreateDirectory(Path.Combine(directory, "new-folder")).FullName;
        var added = Path.Combine(newDirectory, "new.txt");
        File.WriteAllText(added, "Future file");
        WindowsIdentity.RunImpersonated(token, () => Assert.Equal("Future file", ReadGuarded(fixture.Source, added)));
    }

    [Fact]
    public void Unavailable_content_is_not_hydrated_or_given_a_permission_grant()
    {
        using var fixture = new Fixture();
        var path = fixture.OldFile("offline.txt");
        var before = fixture.Acl(path);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        try
        {
            using var token = RestrictedToken();
            using var indexer = new WindowsIdentity(token.DangerousGetHandle());
            Assert.Equal(SourceAccessRepairOutcome.ContentUnavailable,
                fixture.Broker().Repair(fixture.Request("offline.txt"), indexer, default));
            Assert.Equal(before, fixture.Acl(path));
            Assert.False(Directory.Exists(fixture.Journal));
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Offline));
        }
        finally { File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.Offline); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failure_after_native_write_retains_an_unconfirmed_intent_until_exact_success_is_verified(bool pathDrift)
    {
        using var fixture = new Fixture();
        var path = fixture.OldFile("uncertain.txt");
        using var token = RestrictedToken();
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        var broker = new WindowsSourceAccessBroker(fixture.Policy(), fixture.Journal, phase =>
        {
            if (phase == SourceAccessRepairPhase.DescriptorWritten)
                throw pathDrift ? new SourceSnapshotChangedException("Injected post-write drift") : new Win32Exception(5);
        });
        Assert.Equal(SourceAccessRepairOutcome.Unconfirmed, broker.Repair(fixture.Request("uncertain.txt"), indexer, default));
        Assert.Single(Directory.EnumerateFiles(fixture.Journal));
        WindowsIdentity.RunImpersonated(token, () => Assert.Equal("Private source fixture", ReadGuarded(fixture.Source, path)));
        Assert.Equal(SourceAccessRepairOutcome.AlreadyReadable, fixture.Broker().Repair(fixture.Request("uncertain.txt"), indexer, default));
        Assert.Empty(Directory.EnumerateFiles(fixture.Journal));
    }

    [Fact]
    public void Cancellation_before_native_write_removes_its_unwritten_intent_and_does_not_block_retry()
    {
        using var fixture = new Fixture();
        var path = fixture.OldFile("cancelled.txt");
        var before = fixture.Acl(path);
        using var token = RestrictedToken();
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        using var cancellation = new CancellationTokenSource();
        var broker = new WindowsSourceAccessBroker(fixture.Policy(), fixture.Journal, phase =>
        {
            if (phase == SourceAccessRepairPhase.IntentPersisted) cancellation.Cancel();
        });
        Assert.Throws<OperationCanceledException>(() => broker.Repair(fixture.Request("cancelled.txt"), indexer, cancellation.Token));
        Assert.Equal(before, fixture.Acl(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Journal));
        Assert.Equal(SourceAccessRepairOutcome.Repaired, fixture.Broker().Repair(fixture.Request("cancelled.txt"), indexer, default));
    }

    [Fact]
    public async Task Concurrent_repairs_add_one_read_grant_and_retire_their_successful_intent()
    {
        using var fixture = new Fixture();
        var path = fixture.OldFile("concurrent.txt");
        using var token = RestrictedToken();
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        var broker = fixture.Broker();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            broker.Repair(fixture.Request("concurrent.txt"), indexer, default))));
        Assert.Single(outcomes, outcome => outcome == SourceAccessRepairOutcome.Repaired);
        Assert.Equal(7, outcomes.Count(outcome => outcome == SourceAccessRepairOutcome.AlreadyReadable));
        Assert.Single(new RawSecurityDescriptor(fixture.Acl(path), 0).DiscretionaryAcl!.Cast<CommonAce>(),
            ace => ace.SecurityIdentifier == indexer.User);
        Assert.Empty(Directory.EnumerateFiles(fixture.Journal));
    }

    [Fact]
    public void Expired_absolute_budget_stops_before_write_even_if_the_cancellation_timer_has_not_fired()
    {
        using var fixture = new Fixture();
        var path = fixture.OldFile("deadline.txt");
        var before = fixture.Acl(path);
        using var token = RestrictedToken();
        using var indexer = new WindowsIdentity(token.DangerousGetHandle());
        var broker = new WindowsSourceAccessBroker(fixture.Policy(), fixture.Journal, phase =>
        {
            if (phase == SourceAccessRepairPhase.IntentPersisted) Thread.Sleep(150);
        });
        Assert.Throws<OperationCanceledException>(() => broker.Repair(fixture.Request("deadline.txt"), indexer,
            CancellationToken.None, DateTime.UtcNow.AddMilliseconds(100).Ticks));
        Assert.Equal(before, fixture.Acl(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Journal));
    }

    [Fact]
    public void Registration_checks_the_actual_token_instead_of_the_interactive_administrators_access()
    {
        using var fixture = new Fixture();
        var acl = new DirectoryInfo(fixture.Source).GetAccessControl();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            acl.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadAttributes | FileSystemRights.Synchronize, AccessControlType.Allow));
        new DirectoryInfo(fixture.Source).SetAccessControl(acl);
        var policy = new SourceRootPathPolicy(new LocalIngressOptions([fixture.Source]));
        var draft = new SourceRootCreateRequest(fixture.Source, "Token fixture", false, [], [], false, 1024, [], TimeSpan.FromMinutes(15), "test");
        Assert.True(policy.ValidateAndCanonicalise(draft).PermissionEvidence.CanEnumerate);
        using var token = RestrictedToken();
        WindowsIdentity.RunImpersonated(token, () => Assert.Throws<SourceAccessDeniedException>(() => policy.ValidateAndCanonicalise(draft)));
    }

    [Fact]
    public async Task Untrusted_pipe_owner_is_refused_before_sending_source_metadata()
    {
        using var fixture = new Fixture();
        var path = fixture.OldFile("private.txt");
        var before = fixture.Acl(path);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var policy = fixture.Policy() with { PipeName = "FluxKnowledge.SourceAccess." + Guid.NewGuid().ToString("N") };
        var server = SourceAccessPipeServer.ServeOneAsync(policy, fixture.Broker(), WindowsIdentity.GetCurrent().User!, cancellation.Token);
        // Public production client accepts only SYSTEM-owned endpoints.
        var client = new WindowsSourceAccessRepairClient(policy.PipeName);
        Assert.Equal(SourceAccessRepairOutcome.Unavailable, client.TryRepair(fixture.Request("private.txt"), cancellation.Token));
        await server;
        Assert.Equal(before, fixture.Acl(path));
        Assert.False(Directory.Exists(fixture.Journal));
    }

    [Fact]
    public async Task Pipe_caller_sid_must_match_the_approved_indexing_identity()
    {
        using var fixture = new Fixture();
        var path = fixture.OldFile("private.txt");
        var before = fixture.Acl(path);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var policy = fixture.Policy() with
        {
            PipeName = "FluxKnowledge.SourceAccess." + Guid.NewGuid().ToString("N"),
            IndexerSid = "S-1-5-21-1-2-3-99999"
        };
        var owner = WindowsIdentity.GetCurrent().User!;
        var broker = new WindowsSourceAccessBroker(policy, fixture.Journal);
        var server = SourceAccessPipeServer.ServeOneAsync(policy, broker, owner, cancellation.Token);
        var client = new WindowsSourceAccessRepairClient(policy.PipeName, owner);
        Assert.Equal(SourceAccessRepairOutcome.NotApproved, client.TryRepair(fixture.Request("private.txt"), cancellation.Token));
        await server;
        Assert.Equal(before, fixture.Acl(path));
        Assert.False(Directory.Exists(fixture.Journal));
    }

    [Fact]
    public async Task Malformed_connection_is_refused_and_the_next_valid_connection_still_succeeds()
    {
        using var fixture = new Fixture();
        var policy = fixture.Policy() with { PipeName = "FluxKnowledge.SourceAccess." + Guid.NewGuid().ToString("N") };
        var owner = WindowsIdentity.GetCurrent().User!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = SourceAccessPipeServer.RunAsync(policy, fixture.Broker(), cancellation.Token, owner);
        try
        {
            using var token = RestrictedToken();
            for (var index = 0; index < 10; index++)
            {
                var name = $"after-malformed-{index}.txt";
                fixture.OldFile(name);
                using (var malformed = new NamedPipeClientStream(".", policy.PipeName, SourceAccessPipeProtocol.ClientRights,
                           PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None))
                {
                    await malformed.ConnectAsync(cancellation.Token);
                    await malformed.WriteAsync(new byte[4], cancellation.Token); // Invalid zero-length frame.
                }
                WindowsIdentity.RunImpersonated(token, () => Assert.Equal(SourceAccessRepairOutcome.Repaired,
                    new WindowsSourceAccessRepairClient(policy.PipeName, owner).TryRepair(fixture.Request(name), cancellation.Token)));
            }
        }
        finally
        {
            cancellation.Cancel();
            try { await server; } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task Malformed_response_is_unconfirmed_without_replaying_the_request()
    {
        using var fixture = new Fixture();
        var policy = fixture.Policy() with { PipeName = "FluxKnowledge.SourceAccess." + Guid.NewGuid().ToString("N") };
        var owner = WindowsIdentity.GetCurrent().User!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var pipe = SourceAccessPipeServer.Create(policy, owner);
            await pipe.WaitForConnectionAsync(cancellation.Token);
            _ = await SourceAccessPipeProtocol.ReadAsync<SourceAccessPipeProtocol.Request>(pipe, cancellation.Token);
            await pipe.WriteAsync(new byte[4], cancellation.Token);
        });
        Assert.Equal(SourceAccessRepairOutcome.Unconfirmed, new WindowsSourceAccessRepairClient(policy.PipeName, owner)
            .TryRepair(fixture.Request("request.txt"), cancellation.Token));
        await server;
        Assert.False(Directory.Exists(fixture.Journal));
    }

    [Theory]
    [InlineData(0)] // Disconnect after receiving the result.
    [InlineData(1)] // Invalid acknowledgement.
    [InlineData(2)] // No acknowledgement: the absolute deadline releases the instance.
    public async Task Lost_acknowledgement_does_not_replay_repair_or_block_the_next_connection(int mode)
    {
        using var fixture = new Fixture();
        var path = fixture.OldFile("lost-ack.txt");
        var policy = fixture.Policy() with { PipeName = "FluxKnowledge.SourceAccess." + Guid.NewGuid().ToString("N") };
        var owner = WindowsIdentity.GetCurrent().User!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var server = SourceAccessPipeServer.RunAsync(policy, fixture.Broker(), cancellation.Token, owner);
        try
        {
            using var token = RestrictedToken();
            await WindowsIdentity.RunImpersonatedAsync(token, async () =>
            {
                using var pipe = new NamedPipeClientStream(".", policy.PipeName, SourceAccessPipeProtocol.ClientRights,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
                await pipe.ConnectAsync(cancellation.Token);
                await SourceAccessPipeProtocol.WriteAsync(pipe, new SourceAccessPipeProtocol.Request(1,
                    DateTime.UtcNow.AddSeconds(1).Ticks, fixture.Request("lost-ack.txt")), cancellation.Token);
                var response = await SourceAccessPipeProtocol.ReadAsync<SourceAccessPipeProtocol.Response>(pipe, cancellation.Token);
                Assert.Equal(SourceAccessRepairOutcome.Repaired, response.Outcome);
                if (mode == 0) pipe.Dispose();
                if (mode == 1) await pipe.WriteAsync(new byte[] { 0 }, cancellation.Token);
                Assert.Equal(SourceAccessRepairOutcome.AlreadyReadable, new WindowsSourceAccessRepairClient(policy.PipeName, owner)
                    .TryRepair(fixture.Request("lost-ack.txt"), cancellation.Token));
            });
            Assert.Empty(Directory.GetFiles(fixture.Journal));
            var verified = fixture.Acl(path);
            using var identity = WindowsIdentity.GetCurrent();
            Assert.Equal(SourceAccessRepairOutcome.AlreadyReadable, fixture.Broker().Repair(fixture.Request("lost-ack.txt"), identity, CancellationToken.None));
            Assert.Equal(verified, fixture.Acl(path));
        }
        finally
        {
            cancellation.Cancel();
            try { await server; } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task Transmitted_deadline_includes_time_spent_waiting_for_the_connection()
    {
        using var fixture = new Fixture();
        var policy = fixture.Policy() with { PipeName = "FluxKnowledge.SourceAccess." + Guid.NewGuid().ToString("N") };
        var owner = WindowsIdentity.GetCurrent().User!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Task.Run(() =>
        {
            started.SetResult(DateTime.UtcNow.Ticks);
            return new WindowsSourceAccessRepairClient(policy.PipeName, owner).TryRepair(fixture.Request("request.txt"), cancellation.Token);
        });
        var began = await started.Task;
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellation.Token);
        using var pipe = SourceAccessPipeServer.Create(policy, owner);
        await pipe.WaitForConnectionAsync(cancellation.Token);
        var request = await SourceAccessPipeProtocol.ReadAsync<SourceAccessPipeProtocol.Request>(pipe, cancellation.Token);
        await SourceAccessPipeProtocol.WriteAsync(pipe,
            new SourceAccessPipeProtocol.Response(1, SourceAccessRepairOutcome.NotApproved), cancellation.Token);
        Assert.Equal(SourceAccessRepairOutcome.NotApproved, await client);
        Assert.InRange(request.DeadlineUtcTicks, began, began + SourceAccessPipeProtocol.RequestTimeout.Ticks + TimeSpan.FromMilliseconds(100).Ticks);
    }

    internal static SafeAccessTokenHandle RestrictedToken(SecurityIdentifier? restrictTo = null)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        var admin = new SecurityIdentifier("S-1-5-32-544");
        var disabledBytes = new byte[admin.BinaryLength]; admin.GetBinaryForm(disabledBytes, 0);
        var disabledPin = GCHandle.Alloc(disabledBytes, GCHandleType.Pinned);
        var restrictedBytes = restrictTo is null ? [] : new byte[restrictTo.BinaryLength];
        restrictTo?.GetBinaryForm(restrictedBytes, 0);
        var restrictedPin = GCHandle.Alloc(restrictedBytes, GCHandleType.Pinned);
        try
        {
            if (!CreateRestrictedToken(identity.AccessToken, 1, 1,
                    [new() { Sid = disabledPin.AddrOfPinnedObject() }], 0, IntPtr.Zero, restrictTo is null ? 0u : 1u,
                    restrictTo is null ? [] : [new() { Sid = restrictedPin.AddrOfPinnedObject() }], out var token))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return token;
        }
        finally { restrictedPin.Free(); disabledPin.Free(); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("FluxKnowledgeAccessBroker_").FullName;
        public string Source { get; }
        public string Outside { get; }
        public string Journal => Path.Combine(_root, "journal");
        private readonly ApprovedSourceAccessRoot _approved;
        public Fixture()
        {
            Source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
            Outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
            _approved = new(Source, PhysicalFileIdentity.GetDirectory(Source).IdentityFingerprint);
        }
        public SourceAccessPolicy Policy() => new(1, "FluxKnowledge.SourceAccess.test", WindowsIdentity.GetCurrent().User!.Value,
            [_approved], [Outside]);
        public WindowsSourceAccessBroker Broker() => new(Policy(), Journal);
        public SourceAccessRepairRequest Request(string relative) => new(Source, _approved.IdentityFingerprint, relative, false);
        public string OldFile(string name)
        {
            var path = Path.Combine(Source, name);
            File.WriteAllText(path, "Private source fixture");
            var acl = new FileInfo(path).GetAccessControl();
            acl.SetAccessRuleProtection(true, false);
            acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
            acl.AddAccessRule(new(new SecurityIdentifier("S-1-5-32-544"), FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new(new SecurityIdentifier("S-1-5-18"), FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
            return path;
        }
        public byte[] Acl(string path) { using var h = Open(path, 0x20080, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero); return SourceReadGrant.Read(h); }
        public static void SetNative(string path, RawSecurityDescriptor descriptor)
        {
            if (descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclAutoInherited))
                descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInheritRequired);
            var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
            using var h = Open(path, 0x60080, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            Assert.Equal(0, NtSetSecurityObject(h, 4, bytes));
        }
        public void Dispose()
        {
            var path = Path.GetFullPath(_root);
            if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(path, true);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle token, uint flags, uint disableCount, SidAndAttributes[] disable,
        uint deleteCount, IntPtr delete, uint restrictedCount, SidAndAttributes[] restricted, out SafeAccessTokenHandle result);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle Open(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string existing, IntPtr security);
    [DllImport("ntdll.dll")]
    private static extern int NtSetSecurityObject(SafeFileHandle handle, uint info, byte[] descriptor);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
