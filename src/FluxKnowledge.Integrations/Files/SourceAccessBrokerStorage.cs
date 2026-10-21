using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

[SupportedOSPlatform("windows")]
internal sealed class SourceAccessBrokerStorage : IDisposable
{
    private readonly WindowsSourcePathAccess.DirectoryPathLease _root;
    private readonly WindowsSourcePathAccess.DirectoryPathLease _journal;
    private SafeFileHandle? _policyHandle;
    private SourceAccessPolicy? _policy;
    private bool _disposed;
    internal string RootPath { get; }
    internal string JournalRoot { get; }

    internal SourceAccessBrokerStorage(string root)
    {
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        JournalRoot = Path.Combine(RootPath, "journal");
        var drive = new DriveInfo(Path.GetPathRoot(RootPath)!);
        if (drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("SourceAccessStorageRequiresFixedNtfs");
        _root = WindowsSourcePathAccess.OpenDirectoryPath(RootPath, readPermissions: true);
        try
        {
            _journal = WindowsSourcePathAccess.OpenDirectoryPath(JournalRoot, readPermissions: true);
            ValidateCurrent();
        }
        catch { _journal?.Dispose(); _root.Dispose(); throw; }
    }

    internal SourceAccessPolicy ReadPolicy()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_policy is not null) return _policy;
        ValidateCurrent();
        var handle = OpenTrustedFile(_root, "policy.json", readData: true);
        try
        {
            var length = RandomAccess.GetLength(handle);
            if (length is <= 0 or > 1024 * 1024) throw new InvalidDataException("SourceAccessPolicyInvalid");
            var bytes = new byte[checked((int)length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
            var policy = SourceAccessPolicy.Parse(bytes);
            ValidateCurrent();
            // Keep policy immutable until the task is stopped for a reviewed policy update.
            _policyHandle = handle;
            return _policy = policy;
        }
        catch { handle.Dispose(); throw; }
    }

    internal byte[] ReadRootReadPlan()
    {
        ValidateCurrent();
        using var handle = OpenTrustedFile(_root, "root-read-plan.json", readData: true);
        var length = RandomAccess.GetLength(handle);
        if (length is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException("SourceAccessRootPlanInvalid");
        var bytes = new byte[checked((int)length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        ValidateCurrent();
        return bytes;
    }

    internal void ValidateCurrent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateChain(_root);
        ValidateChain(_journal);
        using var current = WindowsSourcePathAccess.OpenDirectoryPath(JournalRoot, readPermissions: true);
        if (current.Identity != _journal.Identity || current.IdentityAt(RootPath) != _root.Identity)
            throw new UnauthorizedAccessException("SourceAccessStorageIdentityChanged");
        if (_policyHandle is not null) ValidateFile(_policyHandle, Path.Combine(RootPath, "policy.json"));
    }

    internal void ValidateJournalFile(string path)
    {
        ValidateCurrent();
        var name = Path.GetFileName(path);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), JournalRoot, StringComparison.OrdinalIgnoreCase) ||
            name.Length != 69 || !name.EndsWith(".json", StringComparison.Ordinal) ||
            !name.Take(64).All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new UnauthorizedAccessException("SourceAccessJournalPathInvalid");
        using var handle = OpenTrustedFile(_journal, name, readData: false);
    }

    private static SafeFileHandle OpenTrustedFile(WindowsSourcePathAccess.DirectoryPathLease parent, string name, bool readData)
    {
        var path = Path.Combine(parent.Path, name);
        var handle = WindowsSourcePathAccess.OpenRelative(parent.Handle, name, 0x120080u | (readData ? 1u : 0), 1, 0x60, path);
        try { ValidateFile(handle, path); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private static void ValidateFile(SafeFileHandle handle, string path)
    {
        var metadata = WindowsSourcePathAccess.ReadMetadata(handle);
        if (metadata.IsDirectory || (metadata.Attributes & 0x00441400) != 0 ||
            PhysicalFileIdentity.GetLinkCount(handle) != 1 ||
            !string.Equals(PhysicalFileIdentity.GetFinalPath(handle), path, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("SourceAccessStorageFileUnsafe");
        ValidateDescriptor(SourceReadGrant.Read(handle));
    }

    private static void ValidateChain(WindowsSourcePathAccess.DirectoryPathLease chain)
    {
        chain.ValidateCurrent();
        foreach (var item in chain.HeldComponents)
        {
            var metadata = WindowsSourcePathAccess.ReadMetadata(item.Handle);
            if ((metadata.Attributes & 0x00441400) != 0) throw new UnauthorizedAccessException("SourceAccessStorageReparseRefused");
            var volume = string.Equals(item.Path, Path.GetPathRoot(item.Path), StringComparison.OrdinalIgnoreCase);
            ValidateDescriptor(SourceReadGrant.Read(item.Handle),
                ancestor: !string.Equals(item.Path, chain.Path, StringComparison.OrdinalIgnoreCase), volumeRoot: volume);
        }
    }

    internal static void ValidateDescriptor(byte[] descriptor, bool ancestor = false, bool volumeRoot = false)
    {
        var raw = new RawSecurityDescriptor(descriptor, 0);
        // A volume root cannot itself be renamed, deleted or made a reparse point.
        // Creating unrelated siblings there is safe when DELETE_CHILD and ACL control are denied.
        var unsafeMask = volumeRoot ? 0xc0040 : ancestor ? 0xd0140 : 0xd0156;
        if (!Trusted(raw.Owner?.Value, ancestor) || raw.DiscretionaryAcl is null ||
            raw.DiscretionaryAcl.Cast<GenericAce>().Any(ace => ace is not CommonAce || ace is CommonAce { IsCallback: true }) ||
            raw.DiscretionaryAcl.Cast<CommonAce>().Any(ace => ace.AceQualifier == AceQualifier.AccessAllowed &&
                (ace.AceFlags & AceFlags.InheritOnly) == 0 && !Trusted(ace.SecurityIdentifier.Value, ancestor) &&
                (SourceReadGrant.MapGenericFileRights(ace.AccessMask) & unsafeMask) != 0))
            throw new UnauthorizedAccessException("SourceAccessStoragePermissionsUnsafe");
    }

    private static bool Trusted(string? sid, bool ancestor) => sid is "S-1-5-18" or "S-1-5-32-544" || ancestor &&
        sid == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"; // Windows TrustedInstaller, ancestors only.

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _policyHandle?.Dispose(); _journal.Dispose(); _root.Dispose();
    }
}
