using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

public enum VssAssociationState
{
    ExactExisting,
    SupportedAbsent,
    ForeignAssociation,
    Unsupported,
    Failed,
    Interrupted
}

public sealed record VssDiffAreaState(
    VssAssociationState State,
    string SourceVolumeId,
    string StorageVolumeId,
    ulong? MaximumBytes);

internal sealed record VssVolumeDiffAreaState(VssDiffAreaState Association, ulong TotalBytes);

internal sealed record NativeGoLiveVssPreflightObservation(
    VssDiffAreaState Association,
    ulong VolumeCapacityBytes,
    ulong RequiredMaximumBytes);

internal enum NativeGoLiveVssAction
{
    None,
    AddDiffArea,
    ChangeDiffAreaMaximumSize
}

internal sealed record NativeGoLiveVssMutationObservation(
    VssDiffAreaState Observed,
    VssDiffAreaState Verified,
    NativeGoLiveVssAction Action);

internal interface IVssDiffAreaComApi
{
    VssVolumeDiffAreaState Query(string volume);
    void AddDiffArea(string sourceVolumeId, string storageVolumeId, ulong maximumBytes);
    void ChangeDiffAreaMaximumSize(string sourceVolumeId, string storageVolumeId, ulong maximumBytes);
}

internal interface IVssOperationPrivilegeScope
{
    IDisposable EnableBackupPrivilege();
}

/// <summary>
/// Applies the one allowed VSS diff-area policy through the typed VSS COM API. It never creates a
/// snapshot and exposes no command, output-parsing, encryption or restore surface.
/// </summary>
internal sealed class VssDiffAreaAdministration
{
    internal const ulong MinimumDiffAreaBytes = 320UL * 1024 * 1024;
    private const decimal CanonicalMaximumStorageFraction = 0.10m;
    private readonly IVssDiffAreaComApi _api;
    private readonly IVssOperationPrivilegeScope _operationPrivilegeScope;

    public VssDiffAreaAdministration() : this(
        new WindowsVssDiffAreaComApi(),
        new WindowsVssOperationPrivilegeScope())
    {
    }

    internal VssDiffAreaAdministration(IVssDiffAreaComApi api) : this(api, new WindowsVssOperationPrivilegeScope())
    {
    }

    internal VssDiffAreaAdministration(
        IVssDiffAreaComApi api,
        IVssOperationPrivilegeScope operationPrivilegeScope)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _operationPrivilegeScope = operationPrivilegeScope ??
            throw new ArgumentNullException(nameof(operationPrivilegeScope));
    }

    public VssDiffAreaState QueryCanonicalState(CancellationToken cancellationToken = default)
        => QueryCanonicalObservation(cancellationToken).Association;

    internal NativeGoLiveVssPreflightObservation QueryCanonicalObservation(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return EmptyObservation(VssAssociationState.Interrupted);
        }

        try
        {
            var observed = _api.Query("I:");
            var required = observed.TotalBytes == 0
                ? 0
                : checked((ulong)decimal.Floor(observed.TotalBytes * CanonicalMaximumStorageFraction));
            return new NativeGoLiveVssPreflightObservation(
                observed.Association,
                observed.TotalBytes,
                required);
        }
        catch (OperationCanceledException)
        {
            return EmptyObservation(VssAssociationState.Interrupted);
        }
        catch (PlatformNotSupportedException)
        {
            return EmptyObservation(VssAssociationState.Unsupported);
        }
        catch (Exception exception) when (IsVssBoundaryFailure(exception))
        {
            return EmptyObservation(VssAssociationState.Failed);
        }
    }

    public VssDiffAreaState EnsureMaximumStorage(
        string volume,
        decimal maximumStorageFraction,
        CancellationToken cancellationToken = default) =>
        EnsureMaximumStorageObserved(volume, maximumStorageFraction, cancellationToken).Verified;

    internal NativeGoLiveVssMutationObservation EnsureMaximumStorageObserved(
        string volume,
        decimal maximumStorageFraction,
        CancellationToken cancellationToken = default)
    {
        ValidatePolicy(volume, maximumStorageFraction);
        var expected = QueryCanonicalObservation(cancellationToken);
        return EnsureMaximumStorageObserved(volume, maximumStorageFraction, expected, cancellationToken);
    }

    internal NativeGoLiveVssMutationObservation EnsureMaximumStorageObserved(
        string volume,
        decimal maximumStorageFraction,
        NativeGoLiveVssPreflightObservation expected,
        CancellationToken cancellationToken = default)
    {
        ValidatePolicy(volume, maximumStorageFraction);
        if (cancellationToken.IsCancellationRequested)
        {
            var interrupted = Empty(VssAssociationState.Interrupted);
            return new NativeGoLiveVssMutationObservation(interrupted, interrupted, NativeGoLiveVssAction.None);
        }

        try
        {
            var observed = _api.Query(volume);
            var state = observed.Association;
            var observedRequired = observed.TotalBytes == 0
                ? 0
                : checked((ulong)decimal.Floor(observed.TotalBytes * maximumStorageFraction));
            var exactObservation = new NativeGoLiveVssPreflightObservation(
                state,
                observed.TotalBytes,
                observedRequired);
            if (exactObservation != expected)
                return new NativeGoLiveVssMutationObservation(state, state, NativeGoLiveVssAction.None);
            if (state.State is not (VssAssociationState.ExactExisting or VssAssociationState.SupportedAbsent))
            {
                return new NativeGoLiveVssMutationObservation(state, state, NativeGoLiveVssAction.None);
            }
            if (!SameVolume(state.SourceVolumeId, state.StorageVolumeId) || observed.TotalBytes == 0)
            {
                var foreign = state with { State = VssAssociationState.ForeignAssociation };
                return new NativeGoLiveVssMutationObservation(state, foreign, NativeGoLiveVssAction.None);
            }

            var maximumBytes = expected.RequiredMaximumBytes;
            if (maximumBytes < MinimumDiffAreaBytes || maximumBytes > long.MaxValue)
            {
                var unsupported = state with { State = VssAssociationState.Unsupported, MaximumBytes = null };
                return new NativeGoLiveVssMutationObservation(state, unsupported, NativeGoLiveVssAction.None);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var action = state.State == VssAssociationState.ExactExisting
                ? NativeGoLiveVssAction.ChangeDiffAreaMaximumSize
                : NativeGoLiveVssAction.AddDiffArea;
            try
            {
                using var backupPrivilege = _operationPrivilegeScope.EnableBackupPrivilege();
                if (state.State == VssAssociationState.ExactExisting)
                {
                    _api.ChangeDiffAreaMaximumSize(state.SourceVolumeId, state.StorageVolumeId, maximumBytes);
                }
                else
                {
                    _api.AddDiffArea(state.SourceVolumeId, state.StorageVolumeId, maximumBytes);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new NativeGoLiveContractException(action == NativeGoLiveVssAction.AddDiffArea
                    ? "vss-add-diff-area-failed"
                    : "vss-change-diff-area-failed",
                    $"hresult-0x{unchecked((uint)exception.HResult):X8}",
                    exception);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var verified = _api.Query(volume).Association;
            if (verified.State != VssAssociationState.ExactExisting ||
                !SameVolume(verified.SourceVolumeId, state.SourceVolumeId) ||
                !SameVolume(verified.StorageVolumeId, state.StorageVolumeId) ||
                verified.MaximumBytes != maximumBytes)
            {
                var failed = state with { State = VssAssociationState.Failed, MaximumBytes = verified.MaximumBytes };
                return new NativeGoLiveVssMutationObservation(state, failed, action);
            }

            return new NativeGoLiveVssMutationObservation(state, verified, action);
        }
        catch (OperationCanceledException)
        {
            var interrupted = Empty(VssAssociationState.Interrupted);
            return new NativeGoLiveVssMutationObservation(interrupted, interrupted, NativeGoLiveVssAction.None);
        }
        catch (PlatformNotSupportedException)
        {
            var unsupported = Empty(VssAssociationState.Unsupported);
            return new NativeGoLiveVssMutationObservation(unsupported, unsupported, NativeGoLiveVssAction.None);
        }
        catch (NativeGoLiveContractException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var failed = Empty(VssAssociationState.Failed);
            return new NativeGoLiveVssMutationObservation(failed, failed, NativeGoLiveVssAction.None);
        }
    }

    private static void ValidatePolicy(string volume, decimal maximumStorageFraction)
    {
        if (!string.Equals(volume, "I:", StringComparison.Ordinal))
        {
            throw new ArgumentException("VSS administration is restricted to the canonical I: volume.", nameof(volume));
        }
        if (maximumStorageFraction != CanonicalMaximumStorageFraction)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumStorageFraction),
                "VSS administration requires the exact ten-percent policy.");
        }
    }

    private static bool SameVolume(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsVssBoundaryFailure(Exception exception) =>
        exception is COMException or Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException;

    private static VssDiffAreaState Empty(VssAssociationState state) => new(state, string.Empty, string.Empty, null);
    private static NativeGoLiveVssPreflightObservation EmptyObservation(VssAssociationState state) =>
        new(Empty(state), 0, 0);
}

internal sealed class WindowsVssOperationPrivilegeScope : IVssOperationPrivilegeScope
{
    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x08;
    private const uint SePrivilegeEnabled = 0x02;
    private const int ErrorNotAllAssigned = 1300;

    public IDisposable EnableBackupPrivilege()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("VSS diff-area administration is Windows-only.");
        }

        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var tokenHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeBackupPrivilege", out var luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var requested = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled }
            };
            if (!AdjustTokenPrivileges(
                    tokenHandle,
                    false,
                    ref requested,
                    (uint)Marshal.SizeOf<TokenPrivileges>(),
                    out var previous,
                    out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotAllAssigned)
            {
                throw new Win32Exception(error);
            }

            return new TokenPrivilegeReverter(tokenHandle, previous);
        }
        catch
        {
            CloseHandle(tokenHandle);
            throw;
        }
    }

    private sealed class TokenPrivilegeReverter(nint tokenHandle, TokenPrivileges previous) : IDisposable
    {
        private nint _tokenHandle = tokenHandle;
        private TokenPrivileges _previous = previous;

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _tokenHandle, 0);
            if (handle == 0) return;
            try
            {
                if (!AdjustTokenPrivilegesRestore(handle, false, ref _previous, 0, 0, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(
        string? systemName,
        string name,
        out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "AdjustTokenPrivileges")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        nint tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        out TokenPrivileges previousState,
        out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "AdjustTokenPrivileges")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivilegesRestore(
        nint tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

/// <summary>Windows-only VSS COM adapter. Volume display names are never parsed.</summary>
internal sealed class WindowsVssDiffAreaComApi : IVssDiffAreaComApi
{
    private static readonly Guid SoftwareProviderId = new("b5946137-7b9f-4925-af80-51abd60b20d5");
    private static readonly Guid DifferentialManagementInterfaceId = typeof(IVssDifferentialSoftwareSnapshotMgmt).GUID;

    public VssVolumeDiffAreaState Query(string volume)
    {
        EnsureWindows();
        var volumeId = GetVolumeId(volume);
        var totalBytes = GetTotalBytes(volume);

        using var api = OpenDifferentialManagement();
        var forSource = Enumerate(api.Value.QueryDiffAreasForVolume, volumeId);
        var onStorage = Enumerate(api.Value.QueryDiffAreasOnVolume, volumeId);

        if (forSource.Count > 1 ||
            forSource.Any(area => !SameVolume(area.SourceVolumeId, volumeId) || !SameVolume(area.StorageVolumeId, volumeId)) ||
            onStorage.Any(area => !SameVolume(area.SourceVolumeId, volumeId) || !SameVolume(area.StorageVolumeId, volumeId)))
        {
            var foreign = forSource.Concat(onStorage).FirstOrDefault() ??
                new VssDiffAreaState(VssAssociationState.ForeignAssociation, volumeId, volumeId, null);
            return new VssVolumeDiffAreaState(foreign with { State = VssAssociationState.ForeignAssociation }, totalBytes);
        }

        if (forSource.Count == 1)
        {
            return new VssVolumeDiffAreaState(forSource[0] with { State = VssAssociationState.ExactExisting }, totalBytes);
        }

        var supportedStorage = EnumerateVolumeIds(api.Value.QueryVolumesSupportedForDiffAreas, volumeId);
        var state = supportedStorage.Any(candidate => SameVolume(candidate, volumeId))
            ? VssAssociationState.SupportedAbsent
            : VssAssociationState.Unsupported;
        return new VssVolumeDiffAreaState(new VssDiffAreaState(state, volumeId, volumeId, null), totalBytes);
    }

    public void AddDiffArea(string sourceVolumeId, string storageVolumeId, ulong maximumBytes)
    {
        EnsureWindows();
        using var api = OpenDifferentialManagement();
        ThrowIfFailed(api.Value.AddDiffArea(sourceVolumeId, storageVolumeId, checked((long)maximumBytes)));
    }

    public void ChangeDiffAreaMaximumSize(string sourceVolumeId, string storageVolumeId, ulong maximumBytes)
    {
        EnsureWindows();
        using var api = OpenDifferentialManagement();
        ThrowIfFailed(api.Value.ChangeDiffAreaMaximumSize(sourceVolumeId, storageVolumeId, checked((long)maximumBytes)));
    }

    private static ComReference<IVssDifferentialSoftwareSnapshotMgmt> OpenDifferentialManagement()
    {
        var snapshotManagement = (IVssSnapshotMgmt)(object)new VssSnapshotMgmtClass();
        try
        {
            var interfaceId = DifferentialManagementInterfaceId;
            ThrowIfFailed(snapshotManagement.GetProviderMgmtInterface(
                SoftwareProviderId,
                ref interfaceId,
                out var differentialManagement));
            return new ComReference<IVssDifferentialSoftwareSnapshotMgmt>(
                (IVssDifferentialSoftwareSnapshotMgmt)differentialManagement);
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                Marshal.FinalReleaseComObject(snapshotManagement);
            }
        }
    }

    private static List<VssDiffAreaState> Enumerate(
        QueryDiffAreas query,
        string volumeId)
    {
        ThrowIfFailed(query(volumeId, out var enumerator));
        using var reference = new ComReference<IVssEnumMgmtObject>(enumerator);
        var result = new List<VssDiffAreaState>();
        foreach (var item in Enumerate(reference.Value))
        {
            if (item.Type != VssMgmtObjectType.DiffArea)
            {
                Free(item);
                continue;
            }

            try
            {
                var maximum = item.Value.DiffArea.MaximumDiffSpace < 0
                    ? (ulong?)null
                    : checked((ulong)item.Value.DiffArea.MaximumDiffSpace);
                result.Add(new VssDiffAreaState(
                    VssAssociationState.ExactExisting,
                    Marshal.PtrToStringUni(item.Value.DiffArea.VolumeName) ?? string.Empty,
                    Marshal.PtrToStringUni(item.Value.DiffArea.DiffAreaVolumeName) ?? string.Empty,
                    maximum));
            }
            finally
            {
                Free(item);
            }
        }
        return result;
    }

    private static List<string> EnumerateVolumeIds(
        QueryVolumesSupported query,
        string volumeId)
    {
        ThrowIfFailed(query(volumeId, out var enumerator));
        using var reference = new ComReference<IVssEnumMgmtObject>(enumerator);
        var result = new List<string>();
        foreach (var item in Enumerate(reference.Value))
        {
            try
            {
                if (item.Type == VssMgmtObjectType.DiffVolume)
                {
                    result.Add(Marshal.PtrToStringUni(item.Value.DiffVolume.VolumeName) ?? string.Empty);
                }
            }
            finally
            {
                Free(item);
            }
        }
        return result;
    }

    private static IEnumerable<VssMgmtObjectProperty> Enumerate(IVssEnumMgmtObject enumerator)
    {
        while (true)
        {
            var values = new VssMgmtObjectProperty[1];
            var result = enumerator.Next(1, values, out var fetched);
            if (result == 1 || fetched == 0) yield break;
            ThrowIfFailed(result);
            yield return values[0];
        }
    }

    private static void Free(VssMgmtObjectProperty property)
    {
        static void FreePointer(nint pointer)
        {
            if (pointer != 0) Marshal.FreeCoTaskMem(pointer);
        }

        switch (property.Type)
        {
            case VssMgmtObjectType.Volume:
                FreePointer(property.Value.Volume.VolumeName);
                FreePointer(property.Value.Volume.VolumeDisplayName);
                break;
            case VssMgmtObjectType.DiffVolume:
                FreePointer(property.Value.DiffVolume.VolumeName);
                FreePointer(property.Value.DiffVolume.VolumeDisplayName);
                break;
            case VssMgmtObjectType.DiffArea:
                FreePointer(property.Value.DiffArea.VolumeName);
                FreePointer(property.Value.DiffArea.DiffAreaVolumeName);
                break;
        }
    }

    private static string GetVolumeId(string volume)
    {
        var mountPoint = volume.EndsWith(Path.DirectorySeparatorChar) ? volume : volume + Path.DirectorySeparatorChar;
        var buffer = new char[50];
        if (!GetVolumeNameForVolumeMountPoint(mountPoint, buffer, buffer.Length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return new string(buffer, 0, Array.IndexOf(buffer, '\0'));
    }

    private static ulong GetTotalBytes(string volume)
    {
        var mountPoint = volume.EndsWith(Path.DirectorySeparatorChar) ? volume : volume + Path.DirectorySeparatorChar;
        if (!GetDiskFreeSpaceEx(mountPoint, out _, out var totalBytes, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return totalBytes;
    }

    private static bool SameVolume(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("VSS diff-area administration is Windows-only.");
        }
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0) Marshal.ThrowExceptionForHR(hresult);
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        [Out] char[] volumeName,
        int bufferLength);

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    private delegate int QueryDiffAreas(
        [MarshalAs(UnmanagedType.LPWStr)] string volumeName,
        out IVssEnumMgmtObject enumerator);

    private delegate int QueryVolumesSupported(
        [MarshalAs(UnmanagedType.LPWStr)] string originalVolumeName,
        out IVssEnumMgmtObject enumerator);

    private sealed class ComReference<T>(T value) : IDisposable where T : class
    {
        public T Value { get; } = value;
        public void Dispose()
        {
            if (OperatingSystem.IsWindows())
            {
                Marshal.FinalReleaseComObject(Value);
            }
        }
    }

    private enum VssMgmtObjectType
    {
        Unknown = 0,
        Volume = 1,
        DiffVolume = 2,
        DiffArea = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VssVolumeProperty
    {
        public nint VolumeName;
        public nint VolumeDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VssDiffVolumeProperty
    {
        public nint VolumeName;
        public nint VolumeDisplayName;
        public long VolumeFreeSpace;
        public long VolumeTotalSpace;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VssDiffAreaProperty
    {
        public nint VolumeName;
        public nint DiffAreaVolumeName;
        public long MaximumDiffSpace;
        public long AllocatedDiffSpace;
        public long UsedDiffSpace;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct VssMgmtObjectUnion
    {
        [FieldOffset(0)] public VssVolumeProperty Volume;
        [FieldOffset(0)] public VssDiffVolumeProperty DiffVolume;
        [FieldOffset(0)] public VssDiffAreaProperty DiffArea;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VssMgmtObjectProperty
    {
        public VssMgmtObjectType Type;
        public VssMgmtObjectUnion Value;
    }

    [ComImport]
    [Guid("0B5A2C52-3EB9-470A-96E2-6C6D4570E40F")]
    private sealed class VssSnapshotMgmtClass;

    [ComImport]
    [Guid("FA7DF749-66E7-4986-A27F-E2F04AE53772")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVssSnapshotMgmt
    {
        [PreserveSig]
        int GetProviderMgmtInterface(
            Guid providerId,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.IUnknown)] out object managementInterface);

        [PreserveSig]
        int QueryVolumesSupportedForSnapshots(Guid providerId, int context, out IVssEnumMgmtObject enumerator);

        [PreserveSig]
        int QuerySnapshotsByVolume(
            [MarshalAs(UnmanagedType.LPWStr)] string volumeName,
            Guid providerId,
            [MarshalAs(UnmanagedType.IUnknown)] out object enumerator);
    }

    [ComImport]
    [Guid("214A0F28-B737-4026-B847-4F9E37D79529")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVssDifferentialSoftwareSnapshotMgmt
    {
        [PreserveSig]
        int AddDiffArea(
            [MarshalAs(UnmanagedType.LPWStr)] string volumeName,
            [MarshalAs(UnmanagedType.LPWStr)] string diffAreaVolumeName,
            long maximumDiffSpace);

        [PreserveSig]
        int ChangeDiffAreaMaximumSize(
            [MarshalAs(UnmanagedType.LPWStr)] string volumeName,
            [MarshalAs(UnmanagedType.LPWStr)] string diffAreaVolumeName,
            long maximumDiffSpace);

        [PreserveSig]
        int QueryVolumesSupportedForDiffAreas(
            [MarshalAs(UnmanagedType.LPWStr)] string originalVolumeName,
            out IVssEnumMgmtObject enumerator);

        [PreserveSig]
        int QueryDiffAreasForVolume(
            [MarshalAs(UnmanagedType.LPWStr)] string volumeName,
            out IVssEnumMgmtObject enumerator);

        [PreserveSig]
        int QueryDiffAreasOnVolume(
            [MarshalAs(UnmanagedType.LPWStr)] string volumeName,
            out IVssEnumMgmtObject enumerator);

        [PreserveSig]
        int QueryDiffAreasForSnapshot(Guid snapshotId, out IVssEnumMgmtObject enumerator);

        [PreserveSig]
        int DeleteUnusedDiffAreas([MarshalAs(UnmanagedType.LPWStr)] string diffAreaVolumeName);
    }

    [ComImport]
    [Guid("01954E6B-9254-4E6E-808C-C9E05D007696")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVssEnumMgmtObject
    {
        [PreserveSig]
        int Next(
            uint elementCount,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] VssMgmtObjectProperty[] properties,
            out uint fetched);

        [PreserveSig]
        int Skip(uint elementCount);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(out IVssEnumMgmtObject enumerator);
    }
}
