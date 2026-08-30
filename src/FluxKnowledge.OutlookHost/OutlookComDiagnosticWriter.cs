using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FluxKnowledge.Application.Operations;

namespace FluxKnowledge.OutlookHost;

/// <summary>Opt-in local-only writer for raw COM details; it has no public or durable application surface.</summary>
internal interface IOutlookComDiagnosticSink
{
    ValueTask WriteAsync(OutlookComHostException failure, CancellationToken cancellationToken);
}

internal sealed class OutlookComDiagnosticWriter(string? outputPath) : IOutlookComDiagnosticSink
{
    private readonly string? _outputPath = outputPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _created;
    internal static string ProductionRoot => LiveRootLayout.Production.LogsRoot;

    public static IOutlookComDiagnosticSink Create(bool enabled, string? outputPath) =>
        Create(enabled, outputPath, ProductionRoot);

    internal static IOutlookComDiagnosticSink Create(bool enabled, string? outputPath, string privateRoot) =>
        !enabled ? NoOpOutlookComDiagnosticSink.Instance :
        outputPath is null ? new OutlookComDiagnosticWriter(null) :
        IsValidExplicitPrivateLocalPath(outputPath, privateRoot) ? new OutlookComDiagnosticWriter(ResolveOutputPath(outputPath)) :
        NoOpOutlookComDiagnosticSink.Instance;

    public async ValueTask WriteAsync(OutlookComHostException failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Stage is null || failure.InnerException is null)
        {
            return;
        }

        try
        {
            var entry = string.Join(Environment.NewLine,
                $"stage={ToWireValue(failure.Stage.Value)}",
                $"hresult=0x{failure.InnerException.HResult:X8}",
                $"exception_type={failure.InnerException.GetType().FullName}",
                $"message={failure.InnerException.Message}",
                string.Empty);
            if (_outputPath is null)
            {
                if (Environment.UserInteractive && !Console.IsErrorRedirected)
                {
                    await Console.Error.WriteAsync(entry).ConfigureAwait(false);
                }
                return;
            }

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_created && !IsSafePrivateExistingFile(_outputPath))
                {
                    return;
                }

                await using var stream = new FileStream(
                    _outputPath,
                    _created ? FileMode.Append : FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(entry), cancellationToken).ConfigureAwait(false);
                _created = true;
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Diagnostics must not change the host's existing fail-safe outcome.
        }
    }

    internal static bool IsValidExplicitPrivateLocalPath(string outputPath) =>
        IsValidExplicitPrivateLocalPath(outputPath, ProductionRoot);

    internal static bool IsValidExplicitPrivateLocalPath(string outputPath, string privateRoot)
    {
        if (string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(privateRoot) ||
            !Path.IsPathFullyQualified(outputPath) || !Path.IsPathFullyQualified(privateRoot) ||
            outputPath.StartsWith("\\\\", StringComparison.Ordinal) ||
            privateRoot.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(privateRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root) || !IsSafePrivateExistingDirectory(root))
            {
                return false;
            }

            var fullOutputPath = Path.GetFullPath(outputPath);
            var parent = Directory.Exists(outputPath)
                ? fullOutputPath
                : Path.GetDirectoryName(fullOutputPath);
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent) ||
                !IsContainedBy(root, parent) || HasUnsafeDirectoryBetween(root, parent))
            {
                return false;
            }

            return !File.Exists(fullOutputPath) && !IsReparsePoint(fullOutputPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string ResolveOutputPath(string outputPath) => Directory.Exists(outputPath)
        ? Path.Combine(outputPath, $"outlook-com-errors-{Guid.NewGuid():N}.log")
        : outputPath;

    private static bool IsContainedBy(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool HasUnsafeDirectoryBetween(string root, string parent)
    {
        var relative = Path.GetRelativePath(root, parent);
        var current = root;
        if (relative == ".")
        {
            return false;
        }

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!IsSafePrivateExistingDirectory(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSafePrivateExistingDirectory(string path) =>
        !IsReparsePoint(path) && HasSafePrivateAcl(new DirectoryInfo(path));

    private static bool IsSafePrivateExistingFile(string path) =>
        File.Exists(path) && !IsReparsePoint(path) && HasSafePrivateAcl(new FileInfo(path));

    private static bool HasSafePrivateAcl(FileSystemInfo path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not SecurityIdentifier currentUser)
        {
            return false;
        }

        FileSystemSecurity security = path switch
        {
            DirectoryInfo directory => directory.GetAccessControl(),
            FileInfo file => file.GetAccessControl(),
            _ => throw new ArgumentOutOfRangeException(nameof(path), path, null)
        };
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != currentUser)
        {
            return false;
        }

        var broadlyWritable = new[]
        {
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)
        };
        var broadReadOrWriteRights =
            FileSystemRights.ReadData |
            FileSystemRights.ReadAttributes |
            FileSystemRights.ReadExtendedAttributes |
            FileSystemRights.ReadPermissions |
            FileSystemRights.ExecuteFile |
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.WriteAttributes |
            FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;
        return !security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference is SecurityIdentifier sid &&
                broadlyWritable.Contains(sid) &&
                (rule.FileSystemRights & broadReadOrWriteRights) != 0);
    }

    private static string ToWireValue(OutlookComFailureStage stage) => stage switch
    {
        OutlookComFailureStage.ActivationSession => "activation_session",
        OutlookComFailureStage.FolderSubscription => "folder_subscription",
        OutlookComFailureStage.Enumeration => "enumeration",
        OutlookComFailureStage.MessageOpen => "message_open",
        OutlookComFailureStage.MessageBody => "message_body",
        OutlookComFailureStage.AttachmentEnumeration => "attachment_enumeration",
        OutlookComFailureStage.AttachmentByteProperty => "attachment_byte_property",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };
}

internal sealed class NoOpOutlookComDiagnosticSink : IOutlookComDiagnosticSink
{
    public static NoOpOutlookComDiagnosticSink Instance { get; } = new();

    public ValueTask WriteAsync(OutlookComHostException failure, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
