using System.Runtime.InteropServices;

namespace FluxKnowledge.Web;

internal sealed record StagingGpuAdapterIdentity(uint Index,uint Vendor,uint Device,string Luid)
{
    // Metadata only: no D3D/DirectML device, execution provider or model is created.
    internal static StagingGpuAdapterIdentity ReadZero()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var iid=new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid,out var factory));
        try
        {
            var enumerate=Marshal.GetDelegateForFunctionPointer<Enumerate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory),7*IntPtr.Size));
            Marshal.ThrowExceptionForHR(enumerate(factory,0,out var adapter));
            try
            {
                var describe=Marshal.GetDelegateForFunctionPointer<Describe>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter),8*IntPtr.Size));
                Marshal.ThrowExceptionForHR(describe(adapter,out var metadata));
                return new(0,metadata.Vendor,metadata.Device,$"{unchecked((uint)metadata.AdapterLuid.High):X8}:{metadata.AdapterLuid.Low:X8}");
            }
            finally { Marshal.Release(adapter); }
        }
        finally { Marshal.Release(factory); }
    }
    [DllImport("dxgi.dll",ExactSpelling=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateDXGIFactory1(ref Guid iid,out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Enumerate(IntPtr factory,uint index,out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Describe(IntPtr adapter,out Description metadata);
    [StructLayout(LayoutKind.Sequential)] private struct LuidValue { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct Description
    {
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Name;
        public uint Vendor,Device,Subsystem,Revision;
        public UIntPtr DedicatedVideoBytes,DedicatedSystemBytes,SharedSystemBytes;
        public LuidValue AdapterLuid;
    }
}
