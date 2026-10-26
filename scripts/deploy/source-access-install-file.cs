using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class FluxSourceAccessInstallFile {
  [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes {
    public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
  }
  [StructLayout(LayoutKind.Sequential)] struct Tag { public uint Attributes, ReparseTag; }
  [StructLayout(LayoutKind.Sequential)] struct Info {
    public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
    public uint Serial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
  }
  [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern SafeFileHandle Open(string p,uint a,uint s,IntPtr x,uint d,uint f,IntPtr t);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandleEx(SafeFileHandle h,int c,out Tag t,uint n);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle h,out Info i);
  [DllImport("kernel32.dll", EntryPoint="GetFinalPathNameByHandleW", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern uint FinalPath(SafeFileHandle h,StringBuilder p,uint n,uint f);
  [DllImport("kernel32.dll", EntryPoint="CreateDirectoryW", CharSet=CharSet.Unicode, SetLastError=true)]
  [return: MarshalAs(UnmanagedType.Bool)] static extern bool CreateDirectory(string p,ref SecurityAttributes a);
  [DllImport("advapi32.dll")] static extern uint GetSecurityInfo(SafeFileHandle h,int type,uint information,
    out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
  [DllImport("advapi32.dll")] static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
  [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
  static bool Trusted(string sid,bool volume) => sid=="S-1-5-18" || sid=="S-1-5-32-544" ||
    volume && sid=="S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
  static int MapRights(int mask) {
    int mapped=mask&0x0fffffff;
    if((mask&unchecked((int)0x80000000))!=0) mapped|=0x120089;
    if((mask&0x40000000)!=0) mapped|=0x120116;
    if((mask&0x20000000)!=0) mapped|=0x1200a0;
    if((mask&0x10000000)!=0) mapped|=0x1f01ff;
    return mapped;
  }
  public static void ValidateDescriptor(byte[] bytes,bool volume) {
    var raw=new RawSecurityDescriptor(bytes,0);
    if(raw.Owner==null || !Trusted(raw.Owner.Value,volume) || raw.DiscretionaryAcl==null)
      throw new UnauthorizedAccessException("The installation namespace has unsafe ownership or permissions.");
    foreach(GenericAce generic in raw.DiscretionaryAcl) {
      var ace=generic as CommonAce;
      if(ace==null || ace.IsCallback || (ace.AceQualifier!=AceQualifier.AccessAllowed && ace.AceQualifier!=AceQualifier.AccessDenied))
        throw new UnauthorizedAccessException("Unsupported installation permission ACE.");
      if(ace.AceQualifier==AceQualifier.AccessAllowed && (ace.AceFlags&AceFlags.InheritOnly)==0 &&
         !Trusted(ace.SecurityIdentifier.Value,volume) && (MapRights(ace.AccessMask)&(volume?0xc0040:0xd0156))!=0)
        throw new UnauthorizedAccessException("The installation namespace permits untrusted mutation.");
    }
  }
  public static void ValidatePrivateDirectory(SafeFileHandle handle,string path) {
    if(!GetFileInformationByHandleEx(handle,9,out var tag,8) || !GetFileInformationByHandle(handle,out var info))
      throw new Win32Exception(Marshal.GetLastWin32Error());
    var final=new StringBuilder(32768); uint length=FinalPath(handle,final,32768,0);
    string physical=final.ToString(); if(physical.StartsWith(@"\\?\")) physical=physical.Substring(4);
    if(length==0 || length>=32768 || (tag.Attributes&0x00441400)!=0 || (tag.Attributes&0x10)==0 ||
       !string.Equals(physical,Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe installation directory.");
    uint error=GetSecurityInfo(handle,1,7,out _,out _,out _,out _,out var descriptor);
    if(error!=0) throw new Win32Exception((int)error);
    try {
      uint size=GetSecurityDescriptorLength(descriptor);
      if(size==0 || size>1048576) throw new IOException("Unsafe installation descriptor.");
      var bytes=new byte[size]; Marshal.Copy(descriptor,bytes,0,(int)size); ValidateDescriptor(bytes,false);
    } finally { LocalFree(descriptor); }
  }
  public static SafeFileHandle CreatePrivateDirectory(string path,byte[] descriptor) {
    string canonical=Path.GetFullPath(path); ValidateDescriptor(descriptor,false);
    var pin=GCHandle.Alloc(descriptor,GCHandleType.Pinned);
    try {
      var attributes=new SecurityAttributes { Length=Marshal.SizeOf<SecurityAttributes>(),Descriptor=pin.AddrOfPinnedObject(),Inherit=false };
      // CreateDirectoryW fails if any object already owns this name. Never adopt it.
      if(!CreateDirectory(canonical,ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error());
    } finally { pin.Free(); }
    var handle=Open(canonical,0x120080,3,IntPtr.Zero,3,0x02600000,IntPtr.Zero);
    try {
      if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
      ValidatePrivateDirectory(handle,canonical); return handle;
    } catch { handle.Dispose(); throw; }
  }
  public static SafeFileHandle OpenPrivateDirectory(string path) {
    string canonical=Path.GetFullPath(path);
    var handle=Open(canonical,0x120080,3,IntPtr.Zero,3,0x02600000,IntPtr.Zero);
    try {
      if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
      ValidatePrivateDirectory(handle,canonical); return handle;
    } catch { handle.Dispose(); throw; }
  }
  public static FileStream Read(string path,long maximum) {
    string canonical=Path.GetFullPath(path);
    var handle=Open(canonical,0x80000000,1,IntPtr.Zero,3,0x00600000,IntPtr.Zero);
    try {
      if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
      if(!GetFileInformationByHandleEx(handle,9,out var tag,8) || !GetFileInformationByHandle(handle,out var info))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      var final=new StringBuilder(32768); uint length=FinalPath(handle,final,32768,0);
      string physical=final.ToString(); if(physical.StartsWith(@"\\?\")) physical=physical.Substring(4);
      long bytes=((long)info.SizeHigh<<32)|info.SizeLow;
      if(length==0 || length>=32768 || (tag.Attributes&0x00441410)!=0 || info.Links!=1 || bytes<0 || bytes>maximum ||
         !string.Equals(physical,canonical,StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe candidate file.");
      return new FileStream(handle,FileAccess.Read);
    } catch { handle.Dispose(); throw; }
  }
}
