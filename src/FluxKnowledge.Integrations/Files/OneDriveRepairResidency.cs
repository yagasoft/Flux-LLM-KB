using System.Runtime.InteropServices;
using FluxKnowledge.Application.Ports;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

internal static class OneDriveRepairResidency
{
    internal static bool IsPlaceholder(SafeFileHandle file, WindowsSourcePathAccess.Metadata metadata)
    {
        WindowsSourcePathAccess.EnsureContentAvailable(metadata);
        // Corroborate exposed attributes with the Cloud Files API on the same
        // object. ERROR_NOT_A_CLOUD_FILE is affirmative ordinary-file evidence.
        const int notACloudFile = unchecked((int)0x80070178);
        var info = new byte[8192]; // Fixed basic header plus up to 4 KiB provider identity.
        int status;
        try { status = CfGetPlaceholderInfo(file, 0, info, (uint)info.Length, out _); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        { throw Unavailable(); }
        var reparsePoint = (metadata.Attributes & 0x400) != 0;
        if (status == notACloudFile && !reparsePoint && metadata.PlaceholderState == 0) return false;
        if (status == 0 && reparsePoint && WindowsSourcePathAccess.IsCloudTag(metadata.ReparseTag) &&
            (metadata.PlaceholderState & 1) != 0) return true;
        throw Unavailable();
    }

    internal delegate int OnDiskRangeQuery(SafeFileHandle file, long[] ranges, out uint returned);

    internal static void RequireComplete(SafeFileHandle file, SafeFileHandle sourceRoot, WindowsSourcePathAccess.Metadata metadata,
        OnDiskRangeQuery? rangeQuery = null)
    {
        // Queries use guarded no-follow, no-recall handles and do not read the data stream.
        WindowsSourcePathAccess.EnsureContentAvailable(metadata);
        if (metadata.IsDirectory || (metadata.Attributes & 0x400) == 0 || !WindowsSourcePathAccess.IsCloudTag(metadata.ReparseTag) ||
            (metadata.PlaceholderState & 1) == 0 || (metadata.PlaceholderState & ~0x0du) != 0)
            throw Unavailable();

        try
        {
            var provider = new byte[1028];
            if (CfGetSyncRootProvider(file, 2, provider, (uint)provider.Length, out var providerBytes) != 0 ||
                WindowsSourceJournalSealQualification.ParseProviderIdentity(provider, providerBytes)?.Name is not ("OneDrive" or "OneDrive - Business") ||
                CfGetSyncRootId(file, 0, out var syncRoot, 8, out var fileBytes) != 0 || fileBytes != 8 || syncRoot == 0 ||
                CfGetSyncRootId(sourceRoot, 0, out var sourceSyncRoot, 8, out var rootBytes) != 0 || rootBytes != 8 || sourceSyncRoot != syncRoot)
                throw Unavailable();

            var eof = RandomAccess.GetLength(file);
            // A staging substitute can veto native evidence but never override it.
            if (eof <= 0 || !HasCompleteRanges(file, eof, QueryOnDiskRanges) ||
                (rangeQuery is not null && !HasCompleteRanges(file, eof, rangeQuery)) || RandomAccess.GetLength(file) != eof)
                throw Unavailable();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        { throw Unavailable(); }
    }

    private static bool HasCompleteRanges(SafeFileHandle file, long eof, OnDiskRangeQuery query)
    {
        var ranges = new long[128]; // Bounded; failed/truncated responses establish no coverage.
        return query(file, ranges, out var returned) == 0 &&
            returned <= ranges.Length * sizeof(long) && returned % (2 * sizeof(long)) == 0 &&
            CoversFile(eof, ranges.AsSpan(0, (int)returned / sizeof(long)));
    }

    private static int QueryOnDiskRanges(SafeFileHandle file, long[] ranges, out uint returned) =>
        CfGetPlaceholderRangeInfo(file, 1, 0, -1, ranges, (uint)(ranges.Length * sizeof(long)), out returned);

    internal static bool CoversFile(long eof, ReadOnlySpan<long> pairs)
    {
        if (eof <= 0 || pairs.Length == 0 || pairs.Length % 2 != 0) return false;
        long covered = 0;
        for (var i = 0; i < pairs.Length; i += 2)
        {
            var length = pairs[i + 1];
            if (pairs[i] != covered || length <= 0 || length > eof - covered) return false;
            covered += length;
        }
        return covered == eof;
    }

    private static SourceContentUnavailableException Unavailable() =>
        new("Complete local OneDrive residency could not be verified. No download was requested.");

    [DllImport("cldapi.dll", EntryPoint = "CfGetSyncRootInfoByHandle")]
    private static extern int CfGetSyncRootProvider(SafeFileHandle file, int infoClass, [Out] byte[] buffer, uint length, out uint returned);

    [DllImport("cldapi.dll", EntryPoint = "CfGetSyncRootInfoByHandle")]
    private static extern int CfGetSyncRootId(SafeFileHandle file, int infoClass, out long rootId, uint length, out uint returned);

    [DllImport("cldapi.dll")]
    private static extern int CfGetPlaceholderInfo(SafeFileHandle file, int infoClass,
        [Out] byte[] info, uint capacity, out uint written);

    [DllImport("cldapi.dll")]
    private static extern int CfGetPlaceholderRangeInfo(SafeFileHandle file, int infoClass, long start, long length,
        [Out] long[] ranges, uint capacity, out uint written);
}
