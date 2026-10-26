using System.ComponentModel;
using System.Runtime.InteropServices;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

public sealed class WindowsSourceObservationReader : ISourceObservationReader
{
    public ISourceAbsenceLease? OpenAbsence(SourceRootConfiguration root, string relativePath)
    {
        var full = Path.GetFullPath(relativePath, root.CanonicalPath);
        if (full == root.CanonicalPath || !WindowsSourcePathAccess.IsWithin(root.CanonicalPath, full)) return null;
        var parent = WindowsSourcePathAccess.OpenDirectoryPath(root.CanonicalPath);
        try
        {
            if (root.PhysicalIdentityFingerprint is { } id && parent.Identity.IdentityFingerprint != id)
                throw new SourceSnapshotChangedException("SourceRootIdentityMismatch");
            var parts = Path.GetRelativePath(root.CanonicalPath, full).Split(Path.DirectorySeparatorChar);
            var path = root.CanonicalPath;
            for (var i = 0; i < parts.Length; i++)
            {
                ValidateCompleteNamespace(parent, root.CanonicalPath);
                try
                {
                    using var found = WindowsSourcePathAccess.OpenRelative(parent.Handle, parts[i], 0x80, 7, 0);
                    WindowsSourcePathAccess.ValidateTag(WindowsSourcePathAccess.ReadMetadata(found));
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    ValidateCompleteNamespace(parent, root.CanonicalPath);
                    return new AbsenceLease(parent, parts[i], root.CanonicalPath);
                }
                if (i == parts.Length - 1) { parent.Dispose(); return null; }
                path = Path.Combine(path, parts[i]);
                var next = WindowsSourcePathAccess.OpenDirectoryPath(path);
                parent.Dispose(); parent = next;
            }
            parent.Dispose(); return null;
        }
        catch { parent.Dispose(); throw; }
    }
    public ISourceObservationLease Open(SourceRootConfiguration root, string relativePath)
    {
        var path = Path.GetFullPath(relativePath, root.CanonicalPath);
        if (path == root.CanonicalPath || !WindowsSourcePathAccess.IsWithin(root.CanonicalPath, path))
            throw new UnauthorizedAccessException("SourceObservationOutsideRoot");
        // Request read access to honour explicit denies, but never read content for this observation.
        var source = WindowsSourcePathAccess.OpenRead(root.CanonicalPath, path, root.PhysicalIdentityFingerprint);
        try { return new ObservationLease(source, root, relativePath, Describe(source)); }
        catch { source.Dispose(); throw; }
    }
    public IReadOnlyList<SourceNamespaceObservation> ReadRootAncestors(SourceRootConfiguration root)
    {
        using var lease = OpenRoot(root);
        return lease.Ancestors;
    }
    public ISourceRootObservationLease OpenRoot(SourceRootConfiguration root)
    {
        var lease = WindowsSourcePathAccess.OpenDirectoryPath(root.CanonicalPath);
        try
        {
        if (root.PhysicalIdentityFingerprint is { } fingerprint && lease.Identity.IdentityFingerprint != fingerprint)
            throw new SourceSnapshotChangedException("SourceRootIdentityMismatch");
        var result = new List<SourceNamespaceObservation>();
        for (var i = 0; i < lease.HeldComponents.Count; i++)
        {
            var current = lease.HeldComponents[i];
            result.Add(new(i == lease.HeldComponents.Count - 1 ? "." : "!ancestor-" + i,
                PhysicalFileIdentity.Get(current.Handle).Split(':')[^1], i == 0 ? "" : PhysicalFileIdentity.Get(lease.HeldComponents[i - 1].Handle).Split(':')[^1],
                i != lease.HeldComponents.Count - 1));
        }
        return new RootObservationLease(lease, result);
        }
        catch { lease.Dispose(); throw; }
    }
    internal static SourceFileObservation Describe(WindowsSourcePathAccess.SourceReadLease source)
    {
        if (!GetFileInformationByHandleEx(source.Handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInformation>()))
            throw new IOException("SourceObservationMetadataUnavailable", new Win32Exception(Marshal.GetLastPInvokeError()));
        var identity = PhysicalFileIdentity.Get(source.Handle);
        return new(identity, identity.Split(':')[^1], PhysicalFileIdentity.Get(source.Parent.Handle).Split(':')[^1],
            RandomAccess.GetLength(source.Handle), basic.LastWrite, basic.Change, basic.Attributes, PhysicalFileIdentity.GetLinkCount(source.Handle));
    }
    internal static RetainedFileObservation DescribeRetained(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInformation>()))
            throw new IOException("RetainedObservationMetadataUnavailable", new Win32Exception(Marshal.GetLastPInvokeError()));
        return new(PhysicalFileIdentity.Get(handle), RandomAccess.GetLength(handle), basic.LastWrite, basic.Change);
    }
    private sealed class ObservationLease(WindowsSourcePathAccess.SourceReadLease source, SourceRootConfiguration root,
        string relative, SourceFileObservation observation) : ISourceObservationLease
    {
        public SourceFileObservation Observation { get; } = observation;
        public void ValidateCurrent()
        {
            source.Parent.ValidateCurrent();
            WindowsSourcePathAccess.EnsureContentAvailable(WindowsSourcePathAccess.ReadMetadata(source.Handle));
            if (Describe(source) != Observation) throw new SourceSnapshotChangedException("SourceObservationChanged");
            using var current = WindowsSourcePathAccess.OpenRead(root.CanonicalPath, Path.GetFullPath(relative, root.CanonicalPath),
                root.PhysicalIdentityFingerprint, Observation.Identity);
            if (Describe(current) != Observation) throw new SourceSnapshotChangedException("SourceObservationPathChanged");
        }
        public void Dispose() => source.Dispose();
    }
    private sealed class RootObservationLease(WindowsSourcePathAccess.DirectoryPathLease lease,
        IReadOnlyList<SourceNamespaceObservation> ancestors) : ISourceRootObservationLease
    {
        public IReadOnlyList<SourceNamespaceObservation> Ancestors { get; } = ancestors;
        public void ValidateCurrent() => lease.ValidateCurrent();
        public void Dispose() => lease.Dispose();
    }
    private static void ValidateCompleteNamespace(WindowsSourcePathAccess.DirectoryPathLease parent, string root)
    {
        parent.ValidateCurrent();
        foreach (var component in parent.HeldComponents.Where(x => WindowsSourcePathAccess.IsWithin(root, x.Path)))
            if (WindowsSourcePathAccess.ReadMetadata(component.Handle).NamespaceIncomplete)
                throw new SourceContentUnavailableException("SourceNamespaceIncomplete");
    }

    private sealed class AbsenceLease(WindowsSourcePathAccess.DirectoryPathLease parent, string name, string root) : ISourceAbsenceLease
    {
        public void ValidateCurrent()
        {
            ValidateCompleteNamespace(parent, root);
            try { using var found = WindowsSourcePathAccess.OpenRelative(parent.Handle, name, 0x80, 7, 0); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            { ValidateCompleteNamespace(parent, root); return; }
            throw new SourceSnapshotChangedException("SourceAbsenceNoLongerCurrent");
        }
        public void Dispose() => parent.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation { public long Creation; public long Access; public long LastWrite; public long Change; public uint Attributes; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out BasicInformation information, uint size);
}
