using FluxKnowledge.Integrations.Files;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FluxKnowledge.Integrations.Models;

/// <summary>OS-released, machine-wide ownership of the resident CPU model pool.</summary>
public sealed class CpuSearchOwnerLease : IDisposable
{
    public const string FileName = "bge-cpu-search-owner.lock";
    public const string ProductionDirectory = @"I:\FluxKnowledge\Runtime";
    private FileStream? _stream;
    private readonly IReadOnlyList<SafeFileHandle> _directories;

    private CpuSearchOwnerLease(FileStream stream, IReadOnlyList<SafeFileHandle> directories)
    {
        _stream = stream;
        _directories = directories;
    }

    public static async Task<CpuSearchOwnerLease> AcquireProductionAsync(CancellationToken cancellationToken)
        => await AcquireAsync(ProductionDirectory, cancellationToken).ConfigureAwait(false);

    public static async Task<CpuSearchOwnerLease> AcquireAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var parent = Path.GetFullPath(directory);
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(parent);
        var path = Path.Combine(parent, FileName);
        var heldDirectories = HoldDirectoryChain(parent);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("cpu-search-owner-path-unsafe");
                FileStream? stream = null;
                try
                {
                    stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new UnauthorizedAccessException("cpu-search-owner-path-unsafe");
                    var owner = new CpuSearchOwnerLease(stream, heldDirectories);
                    heldDirectories = [];
                    return owner;
                }
                catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
                {
                    stream?.Dispose();
                    // Another worker still owns every native session. Do not load this process's pool yet.
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    stream?.Dispose();
                    throw;
                }
            }
        }
        finally { DisposeDirectories(heldDirectories); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stream, null) is not { } stream) return;
        try { stream.Dispose(); }
        finally { DisposeDirectories(_directories); }
    }

    private static IReadOnlyList<SafeFileHandle> HoldDirectoryChain(string directory)
    {
        var root = Path.GetPathRoot(directory) ?? throw new IOException("cpu-search-owner-root-invalid");
        var held = new List<SafeFileHandle>();
        try
        {
            var current = root;
            held.Add(OpenDirectory(current));
            foreach (var component in Path.GetRelativePath(root, directory).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                held.Add(OpenDirectory(current));
            }
            return held;
        }
        catch { DisposeDirectories(held); throw; }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        const uint readAttributes = 0x80, shareReadWrite = 0x3, openExisting = 3;
        const uint backupSemantics = 0x02000000, openReparsePoint = 0x00200000;
        var handle = CreateFile(path, readAttributes, shareReadWrite, IntPtr.Zero, openExisting,
            backupSemantics | openReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException("cpu-search-owner-directory-unavailable", new Win32Exception(error));
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            handle.Dispose();
            throw new UnauthorizedAccessException("cpu-search-owner-path-unsafe");
        }
        return handle;
    }

    private static void DisposeDirectories(IReadOnlyList<SafeFileHandle> directories)
    {
        for (var index = directories.Count - 1; index >= 0; index--) directories[index].Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
