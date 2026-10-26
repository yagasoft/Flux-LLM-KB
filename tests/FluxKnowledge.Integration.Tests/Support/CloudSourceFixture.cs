using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.TestSupport;

/// <summary>A private, disconnected Cloud Files provider; never touches OneDrive or a network.</summary>
internal sealed class CloudSourceFixture : IDisposable
{
    public string Root { get; }

    public CloudSourceFixture(string? parentDirectory = null)
    {
        Root = Path.Combine(parentDirectory ?? Path.GetTempPath(), $"FluxKnowledgeCloudTest_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Root);
        var registration = new Registration
        {
            Size = (uint)Marshal.SizeOf<Registration>(), ProviderName = "FluxKnowledge isolated test",
            ProviderVersion = "1", ProviderId = new Guid("e0e6ca50-0d99-4b18-8590-10dabd9aac89")
        };
        var policies = new Policies { Size = (uint)Marshal.SizeOf<Policies>(), Hydration = 2, Population = 2 };
        var result = CfRegisterSyncRoot(Root, ref registration, ref policies, 2);
        if (result < 0)
        {
            System.IO.Directory.Delete(Root);
            Marshal.ThrowExceptionForHR(result);
        }
    }

    public string Directory(string name)
    {
        var path = Path.Combine(Root, name);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    public void MakeCloudDirectory(string path) => Convert(path, directory: true, dehydrate: false);
    public void MakeCloudOnlyFile(string path) => Convert(path, directory: false, dehydrate: true);

    public static (uint Attributes, uint Tag) Metadata(string path)
    {
        using var handle = OpenMetadata(path);
        if (!GetFileInformationByHandleEx(handle, 9, out var result, 8)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (result.Attributes, result.Tag);
    }

    public static SafeFileHandle OpenMetadata(string path)
    {
        var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02300000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    public string File(string relativePath, string content, bool dehydrate = false)
    {
        var path = Path.Combine(Root, relativePath);
        System.IO.File.WriteAllText(path, content);
        Convert(path, directory: false, dehydrate);
        return path;
    }

    public static int TryDehydrate(string path)
    {
        using var handle = CreateFile(path, 0xc0000000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) return Marshal.GetLastPInvokeError();
        var result = CfDehydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero);
        return result < 0 ? result : 0;
    }

    private static void Convert(string path, bool directory, bool dehydrate)
    {
        using var handle = CreateFile(path, 0xc0000000, directory ? 3u : 0, IntPtr.Zero, 3,
            directory ? 0x02000000u : 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Cannot open isolated placeholder fixture.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var result = CfConvertToPlaceholder(handle, new byte[] { 1 }, 1, directory ? 5u : dehydrate ? 3u : 1u, IntPtr.Zero, IntPtr.Zero);
        Marshal.ThrowExceptionForHR(result);
    }

    public void Dispose()
    {
        Marshal.ThrowExceptionForHR(CfUnregisterSyncRoot(Root));
        System.IO.Directory.Delete(Root, recursive: true);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Registration
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string ProviderName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ProviderVersion;
        public IntPtr SyncIdentity;
        public uint SyncIdentityLength;
        public IntPtr FileIdentity;
        public uint FileIdentityLength;
        public Guid ProviderId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Policies
    {
        public uint Size;
        public ushort Hydration;
        public ushort HydrationModifier;
        public ushort Population;
        public ushort PopulationModifier;
        public uint InSync;
        public uint HardLink;
        public uint Management;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint Tag; }

#pragma warning disable SYSLIB1054
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    private static extern int CfRegisterSyncRoot(string root, ref Registration registration, ref Policies policies, uint flags);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    private static extern int CfUnregisterSyncRoot(string root);
    [DllImport("cldapi.dll")]
    private static extern int CfConvertToPlaceholder(SafeFileHandle handle, byte[] identity, uint identityLength, uint flags, IntPtr usn, IntPtr overlapped);
    [DllImport("cldapi.dll")]
    private static extern int CfDehydratePlaceholder(SafeFileHandle handle, long offset, long length, uint flags, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTag info, uint size);
#pragma warning restore SYSLIB1054
}
