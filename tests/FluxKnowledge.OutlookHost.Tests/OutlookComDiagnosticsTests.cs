using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace FluxKnowledge.OutlookHost.Tests;

public sealed class OutlookComDiagnosticsTests
{
    [Theory]
    [InlineData(-2147024891, true)]
    [InlineData(-2147467259, false)]
    public void COM_failure_classification_preserves_stage_and_requires_permission_evidence(
        int hresult,
        bool accessDenied)
    {
        var failure = OutlookComFailureClassifier.ClassifyCom(
            new COMException("COM diagnostic", hresult),
            OutlookComFailureStage.MessageBody);

        Assert.Equal(
            accessDenied ? OutlookComFailureReason.FolderAccessDenied : OutlookComFailureReason.OutlookUnavailable,
            failure.Reason);
        Assert.Equal(OutlookComFailureStage.MessageBody, failure.Stage);
    }

    [Fact]
    public void Generic_programmatic_access_prompt_is_not_permission_evidence()
    {
        var failure = OutlookComFailureClassifier.ClassifyCom(
            new COMException("A program is trying to access e-mail address information.", -2147467259),
            OutlookComFailureStage.AttachmentByteProperty);

        Assert.Equal(OutlookComFailureReason.OutlookUnavailable, failure.Reason);
        Assert.Equal(OutlookComFailureStage.AttachmentByteProperty, failure.Stage);
    }

    [Fact]
    public void Generic_programmatic_access_status_is_not_permission_evidence()
    {
        var failure = OutlookComFailureClassifier.ClassifyCom(
            new COMException("Programmatic access requires approval.", -2147467259),
            OutlookComFailureStage.MessageOpen);

        Assert.Equal(OutlookComFailureReason.OutlookUnavailable, failure.Reason);
    }

    [Fact]
    public void Explicit_programmatic_access_denial_is_permission_evidence()
    {
        var failure = OutlookComFailureClassifier.ClassifyCom(
            new COMException("Programmatic access was denied by Outlook.", -2147467259),
            OutlookComFailureStage.AttachmentByteProperty);

        Assert.Equal(OutlookComFailureReason.FolderAccessDenied, failure.Reason);
    }

    [Fact]
    public void Generic_COM_hresult_preserves_every_canonical_stage_without_access_denied()
    {
        foreach (var stage in Enum.GetValues<OutlookComFailureStage>())
        {
            var failure = OutlookComFailureClassifier.ClassifyCom(
                new COMException("generic COM failure", -2147467259),
                stage);

            Assert.Equal(OutlookComFailureReason.OutlookUnavailable, failure.Reason);
            Assert.Equal(stage, failure.Stage);
        }
    }

    [Fact]
    public async Task Verbose_writer_emits_raw_COM_details_only_when_enabled_to_the_explicit_private_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"flux-outlook-diagnostics-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(directory, "private-com-errors.log");
        try
        {
            Directory.CreateDirectory(directory);
            ConfigurePrivateRootAcl(directory);
            var failure = OutlookComFailureClassifier.ClassifyCom(
                new COMException("private COM diagnostic", -2147467259),
                OutlookComFailureStage.MessageOpen);

            await OutlookComDiagnosticWriter.Create(enabled: false, outputPath, directory).WriteAsync(failure, CancellationToken.None);
            Assert.False(File.Exists(outputPath));

            await OutlookComDiagnosticWriter.Create(enabled: true, outputPath, directory).WriteAsync(failure, CancellationToken.None);
            var content = await File.ReadAllTextAsync(outputPath);
            Assert.Contains("stage=message_open", content, StringComparison.Ordinal);
            Assert.Contains("hresult=0x80004005", content, StringComparison.Ordinal);
            Assert.Contains("exception_type=System.Runtime.InteropServices.COMException", content, StringComparison.Ordinal);
            Assert.Contains("message=private COM diagnostic", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Verbose_writer_suppresses_raw_details_when_the_console_error_stream_is_redirected()
    {
        var previous = Console.Error;
        var captured = new StringWriter();
        try
        {
            Console.SetError(captured);
            var failure = OutlookComFailureClassifier.ClassifyCom(
                new COMException("private redirected diagnostic", -2147467259),
                OutlookComFailureStage.ActivationSession);

            await OutlookComDiagnosticWriter.Create(enabled: true, outputPath: null)
                .WriteAsync(failure, CancellationToken.None);

            Assert.Equal(string.Empty, captured.ToString());
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [Fact]
    public void Explicit_output_is_confined_to_the_existing_application_private_root()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-private-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            ConfigurePrivateRootAcl(privateRoot);
            var controlledOutput = Path.Combine(privateRoot, "com-errors.log");
            var repositoryOutput = Path.Combine(Directory.GetCurrentDirectory(), "com-errors.log");
            var broadTemporaryOutput = Path.Combine(Path.GetTempPath(), "com-errors.log");

            Assert.True(OutlookComDiagnosticWriter.IsValidExplicitPrivateLocalPath(controlledOutput, privateRoot));
            Assert.False(OutlookComDiagnosticWriter.IsValidExplicitPrivateLocalPath(repositoryOutput, privateRoot));
            Assert.False(OutlookComDiagnosticWriter.IsValidExplicitPrivateLocalPath(broadTemporaryOutput, privateRoot));
        }
        finally
        {
            Directory.Delete(privateRoot, recursive: true);
        }
    }

    [Fact]
    public void Explicit_output_rejects_broad_acl_children_existing_destinations_and_file_reparse_escapes()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-private-root-{Guid.NewGuid():N}");
        var broadChild = Path.Combine(privateRoot, "broad-child");
        var broadDestination = Path.Combine(privateRoot, "broad-destination.log");
        var escapeTarget = Path.Combine(Path.GetTempPath(), $"flux-escape-{Guid.NewGuid():N}.log");
        var reparseDestination = Path.Combine(privateRoot, "reparse-destination.log");
        Directory.CreateDirectory(broadChild);
        File.WriteAllText(broadDestination, "existing");
        File.WriteAllText(escapeTarget, "escape");
        try
        {
            ConfigurePrivateRootAcl(privateRoot);
            ConfigureBroadAcl(broadChild);
            ConfigureBroadAcl(broadDestination);
            File.CreateSymbolicLink(reparseDestination, escapeTarget);

            Assert.False(OutlookComDiagnosticWriter.IsValidExplicitPrivateLocalPath(
                Path.Combine(broadChild, "com-errors.log"), privateRoot));
            Assert.False(OutlookComDiagnosticWriter.IsValidExplicitPrivateLocalPath(broadDestination, privateRoot));
            Assert.False(OutlookComDiagnosticWriter.IsValidExplicitPrivateLocalPath(reparseDestination, privateRoot));
        }
        finally
        {
            if (File.Exists(reparseDestination))
            {
                File.Delete(reparseDestination);
            }
            File.Delete(escapeTarget);
            Directory.Delete(privateRoot, recursive: true);
        }
    }

    [Fact]
    public void Explicit_output_rejects_a_broad_read_only_child_directory()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-private-root-{Guid.NewGuid():N}");
        var broadReadChild = Path.Combine(privateRoot, "broad-read-child");
        Directory.CreateDirectory(broadReadChild);
        try
        {
            ConfigurePrivateRootAcl(privateRoot);
            ConfigureBroadReadAcl(broadReadChild);

            Assert.False(OutlookComDiagnosticWriter.IsValidExplicitPrivateLocalPath(
                Path.Combine(broadReadChild, "com-errors.log"), privateRoot));
        }
        finally
        {
            Directory.Delete(privateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Subscription_teardown_COM_failure_is_staged_and_releases_both_resources()
    {
        var released = new List<object?>();
        var items = new object();
        var folder = new object();
        var itemChangeRemovalRan = false;

        var failure = await Assert.ThrowsAsync<OutlookComHostException>(() =>
            ClassicOutlookComAdapter.DisposeHintSubscriptionAsync(
                () => throw new COMException("private unsubscribe failure", -2147467259),
                () => itemChangeRemovalRan = true,
                released.Add,
                items,
                folder).AsTask());

        Assert.Equal(OutlookComFailureStage.FolderSubscription, failure.Stage);
        Assert.Equal(OutlookComFailureReason.OutlookUnavailable, failure.Reason);
        Assert.True(itemChangeRemovalRan);
        Assert.Equal([items, folder], released);
    }

    [Fact]
    public async Task Subscription_teardown_with_both_unhooks_failing_preserves_the_first_staged_COM_failure()
    {
        var released = new List<object?>();
        var items = new object();
        var folder = new object();
        var firstFailure = new COMException("first private unsubscribe failure", -2147467259);

        var failure = await Assert.ThrowsAsync<OutlookComHostException>(() =>
            ClassicOutlookComAdapter.DisposeHintSubscriptionAsync(
                () => throw firstFailure,
                () => throw new COMException("second private unsubscribe failure", -2147467259),
                released.Add,
                items,
                folder).AsTask());

        Assert.Equal(OutlookComFailureStage.FolderSubscription, failure.Stage);
        Assert.Same(firstFailure, failure.InnerException);
        Assert.Equal([items, folder], released);
    }

    private static void ConfigurePrivateRootAcl(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetOwner(identity.User!);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity.User!,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void ConfigureBroadAcl(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetOwner(identity.User!);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity.User!,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        if (Directory.Exists(path))
        {
            var directorySecurity = new DirectorySecurity();
            directorySecurity.SetOwner(identity.User!);
            directorySecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            directorySecurity.AddAccessRule(new FileSystemAccessRule(
                identity.User!,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            directorySecurity.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(directorySecurity);
        }
        else
        {
            new FileInfo(path).SetAccessControl(security);
        }
    }

    private static void ConfigureBroadReadAcl(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetOwner(identity.User!);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity.User!,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
