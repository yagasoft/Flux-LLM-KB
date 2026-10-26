using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalNativeTests
{
    [OneDriveQualificationFact]
    [SupportedOSPlatform("windows")]
    public async Task Actual_OneDrive_unchanged_matched_captures_isolate_the_added_close_from_content_edits()
    {
        var providerRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_ONEDRIVE_ROOT")!));
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetEnvironmentVariable("OneDriveCommercial")!)), providerRoot, ignoreCase: true);
        var id = Guid.NewGuid().ToString("N");
        var sources = Path.Combine(providerRoot, "Codex Source Journal Qualification " + id);
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "OneDriveQualification-" + id);
        var paths = new[] { Path.Combine(sources, "sealed.txt"), Path.Combine(sources, "unsealed.txt") };
        var readers = new List<FileStream>();
        var captures = new List<object>();
        var evidence = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_ONEDRIVE_EVIDENCE")!;
        Assert.StartsWith(@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\incremental-source-scanning-20261024\", Path.GetFullPath(evidence), StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(sources); Directory.CreateDirectory(area);
        try
        {
            foreach (var path in paths) { await File.WriteAllBytesAsync(path, new byte[64 * 1024]); Assert.Equal(1, (await WaitForOneDriveSyncAsync(path)).InSync); }
            var stamps = paths.Select(File.GetLastWriteTimeUtc).ToArray();
            foreach (var path in paths) readers.Add(new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "Retained"));
            for (var index = 0; index < paths.Length; index++)
            {
                var root = SourceRootConfiguration.Create(sources, "Unchanged matched OneDrive qualification", true, false, 1024 * 1024,
                    includePatterns: [Path.GetFileName(paths[index])]);
                var enumerator = new LocalSourceEnumerator();
                var ownership = new SourceScanOwnership(SourceScanRequestId.New(), new(Guid.NewGuid(), "unchanged-matched-onedrive", index + 1));
                await using var iterator = enumerator.EnumerateCapturedAsync(root, ownership, default).GetAsyncEnumerator();
                Assert.True(await iterator.MoveNextAsync());
                await using var capture = iterator.Current;
                Assert.Equal(Path.GetFileName(paths[index]), capture.Snapshot.RelativePath);
                var retained = await artifacts.PutCapturedFileAsync(capture,
                    new(capture.Snapshot.ContentSha256, "application/octet-stream", capture.Snapshot.ByteLength), default);
                try
                {
                    SourceCaptureJournalSeal? seal = index == 0 ? await Assert.IsAssignableFrom<IJournalSealableSourceCapture>(capture).SealAsync(default) : null;
                    captures.Add(new { File = Path.GetFileName(paths[index]), Sealed = seal is not null, SealUsn = seal?.Usn,
                        capture.Snapshot.ContentSha256, capture.Snapshot.StableSourceIdentity });
                }
                finally { if (retained.PublicationLease is { } lease) await lease.DisposeAsync(); }
            }
            foreach (var reader in readers) reader.Dispose(); readers.Clear();
            for (var index = 0; index < paths.Length; index++) Assert.Equal(stamps[index], File.GetLastWriteTimeUtc(paths[index]));
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            (int Pin, int InSync)[] states;
            do
            {
                states = paths.Select(ReadOneDriveSync).ToArray();
                if (states.All(x => x.InSync == 1)) break;
                await Task.Delay(250);
            } while (DateTimeOffset.UtcNow < deadline);
            await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(new
            {
                ObservedAtUtc = DateTimeOffset.UtcNow, Provider = ReadOneDriveProvider(providerRoot),
                EnvironmentFingerprint = WindowsSourceJournalSealQualification.GetEnvironmentFingerprint(), NoContentEditsAfterInitialSync = true,
                Captures = captures, FinalStates = states.Select((state, index) => new { Sealed = index == 0, state.Pin, state.InSync }),
                BothProviderConfirmedSynced = states.All(x => x.InSync == 1)
            }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.All(states, state => Assert.Equal(1, state.InSync));
        }
        finally
        {
            foreach (var reader in readers) reader.Dispose();
            Assert.StartsWith(providerRoot + Path.DirectorySeparatorChar, Path.GetFullPath(sources), StringComparison.OrdinalIgnoreCase);
            File.SetAttributes(sources, File.GetAttributes(sources) & ~FileAttributes.ReadOnly);
            Directory.Delete(sources, true); Directory.Delete(area, true);
        }
    }

    [OneDriveQualificationFact]
    [SupportedOSPlatform("windows")]
    public async Task Actual_OneDrive_synced_capture_seals_preserve_later_repeated_write_evidence_with_a_persistent_reader()
    {
        var providerRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_ONEDRIVE_ROOT")!));
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetEnvironmentVariable("OneDriveCommercial")!)), providerRoot, ignoreCase: true);
        var processes = Process.GetProcessesByName("OneDrive");
        Assert.NotEmpty(processes);
        var clientVersions = processes.Select(process => FileVersionInfo.GetVersionInfo(process.MainModule!.FileName).FileVersion).Distinct().ToArray();
        foreach (var process in processes) process.Dispose();
        var provider = ReadOneDriveProvider(providerRoot);
        Assert.Contains("OneDrive", provider.Name, StringComparison.OrdinalIgnoreCase);
        var qualification = new WindowsSourceJournalSealQualification(new()
        {
            SealQualifications = [new("synthetic-provider-binding", WindowsSourceJournalSealQualification.GetEnvironmentFingerprint()!,
                "actual-onedrive-hydrated", "eight-cycle-native-probe") { ProviderName = provider.Name, ProviderVersion = provider.Version }]
        });
        Assert.NotNull(qualification.GetProfile("synthetic-provider-binding", providerRoot));
        var id = Guid.NewGuid().ToString("N");
        var sources = Path.Combine(providerRoot, "Codex Source Journal Qualification " + id);
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "OneDriveQualification-" + id);
        Directory.CreateDirectory(sources); Directory.CreateDirectory(area);
        var receipts = new List<object>();
        Exception? failure = null;
        try
        {
            var path = Path.Combine(sources, "synthetic-repeated-write.txt");
            await File.WriteAllBytesAsync(path, new byte[64 * 1024]);
            var firstSync = await WaitForOneDriveSyncAsync(path);
            Assert.Equal(1, firstSync.InSync);
            var stamp = File.GetLastWriteTimeUtc(path);
            var root = SourceRootConfiguration.Create(sources, "Synthetic OneDrive journal qualification", true, false, 1024 * 1024);
            var native = new WindowsSourceChangeJournalReader();
            using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "Retained"));
            using var persistent = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var persistentId = PhysicalFileIdentity.Get(persistent.SafeFileHandle);
            for (var cycle = 0; cycle < 8; cycle++)
            {
                Overwrite(cycle, (byte)(65 + cycle));
                string digest; SourceCaptureJournalSeal seal;
                var bytesRead = 0;
                var enumeration = new LocalSourceEnumerator { SourceBytesRead = bytes => bytesRead += bytes };
                var ownership = new SourceScanOwnership(SourceScanRequestId.New(), new(Guid.NewGuid(), "actual-onedrive-qualification", cycle + 1));
                await using (var iterator = enumeration.EnumerateCapturedAsync(root, ownership, default).GetAsyncEnumerator())
                {
                    Assert.True(await iterator.MoveNextAsync());
                    await using var capture = iterator.Current;
                    Assert.Equal(persistentId, capture.Snapshot.StableSourceIdentity);
                    digest = capture.Snapshot.ContentSha256;
                    var retained = await artifacts.PutCapturedFileAsync(capture, new(digest, "application/octet-stream", capture.Snapshot.ByteLength), default);
                    try
                    {
                        seal = await Assert.IsAssignableFrom<IJournalSealableSourceCapture>(capture).SealAsync(default);
                        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
                        Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
                    }
                    finally { if (retained.PublicationLease is { } lease) await lease.DisposeAsync(); }
                }
                Assert.Equal(64 * 1024, bytesRead);
                var boundary = await native.QueryAsync(sources, default);
                var closePage = await native.ReadAsync(new(boundary.VolumeIdentity, boundary.JournalId, seal.Usn), 65536, default);
                Assert.Contains(closePage.Changes, change => change.Usn == seal.Usn && change.FileId == seal.Observation.FileId &&
                    (change.Reason & 0x80000000) != 0);
                Overwrite(32 * 1024 + cycle, (byte)(97 + cycle));
                var end = await native.QueryAsync(sources, default);
                var cursor = new JournalCursor(boundary.VolumeIdentity, boundary.JournalId, boundary.NextUsn);
                var observed = false;
                for (var pageIndex = 0; cursor.NextUsn < end.NextUsn && pageIndex < 128; pageIndex++)
                {
                    var page = await native.ReadAsync(cursor, 1024 * 1024, default);
                    Assert.True(page.Next.NextUsn > cursor.NextUsn);
                    observed |= page.Changes.Any(change => change.FileId == seal.Observation.FileId && change.Usn >= boundary.NextUsn &&
                        change.Usn < end.NextUsn && change.ContentChanged);
                    cursor = page.Next;
                }
                Assert.True(cursor.NextUsn >= end.NextUsn);
                Assert.True(observed, "The real provider failed the repeated-write boundary; do not qualify this environment.");
                Assert.Equal(persistentId, PhysicalFileIdentity.Get(path));
                receipts.Add(new { Cycle = cycle, seal.Usn, boundary.NextUsn, EndUsn = end.NextUsn, ContentChangeObserved = observed,
                    SourceBytesRead = bytesRead, SealCloseRecordMatched = true, Provider = ReadOneDriveProvider(path) });
            }
            // Provider synchronisation is a separate claim. Do not change timestamps/content to make this pass.
            persistent.Dispose();
            var finalSync = await WaitForOneDriveSyncAsync(path);
            Assert.Equal(1, finalSync.InSync);
            var evidence = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_ONEDRIVE_EVIDENCE");
            Assert.NotNull(evidence);
            Assert.StartsWith(@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\incremental-source-scanning-20261024\",
                Path.GetFullPath(evidence), StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(new
            {
                ObservedAtUtc = DateTimeOffset.UtcNow, Scope = "Dedicated synthetic folder under the actual running OneDrive business provider; no production root qualification inferred.",
                Provider = provider, ClientVersions = clientVersions, EnvironmentFingerprint = WindowsSourceJournalSealQualification.GetEnvironmentFingerprint(),
                PersistentReadHandle = true, TimestampRestored = true, FinalProviderConfirmedSync = finalSync.InSync,
                OrdinaryWriteSyncHealth = "Not asserted by this native-boundary test; see the separately retained matched control.", Cycles = receipts
            }, new JsonSerializerOptions { WriteIndented = true }));

            void Overwrite(int offset, byte value)
            {
                using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                { writer.Position = offset; writer.WriteByte(value); writer.Flush(true); }
                File.SetLastWriteTimeUtc(path, stamp);
            }
        }
        catch (Exception error)
        {
            failure = error;
            var evidence = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_ONEDRIVE_EVIDENCE");
            if (evidence is not null && Path.GetFullPath(evidence).StartsWith(@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\incremental-source-scanning-20261024\", StringComparison.OrdinalIgnoreCase))
                await File.WriteAllTextAsync(evidence + ".failure.json", JsonSerializer.Serialize(new
                { ObservedAtUtc = DateTimeOffset.UtcNow, Provider = provider, ClientVersions = clientVersions, Error = error.ToString(), CompletedCycles = receipts }));
            throw;
        }
        finally
        {
            Assert.StartsWith(providerRoot + Path.DirectorySeparatorChar, Path.GetFullPath(sources), StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(@"I:\Codex Workspaces\Staging\FluxKnowledge\", Path.GetFullPath(area), StringComparison.OrdinalIgnoreCase);
            try
            {
                // OneDrive marks its own synthetic directories read-only. Change only this fixture's attributes.
                File.SetAttributes(sources, File.GetAttributes(sources) & ~FileAttributes.ReadOnly);
                Directory.Delete(sources, true); Directory.Delete(area, true);
            }
            catch (IOException) when (failure is not null) { /* Preserve the primary qualification failure and its receipt. */ }
            catch (UnauthorizedAccessException) when (failure is not null) { /* Preserve the primary qualification failure and its receipt. */ }
        }
    }

    private sealed record OneDriveProvider(uint Status, string Name, string Version);
    private static OneDriveProvider ReadOneDriveProvider(string path)
    {
        var bytes = new byte[1028];
        Marshal.ThrowExceptionForHR(GetSyncRootInfo(path, 2, bytes, (uint)bytes.Length, out var returned));
        Assert.Equal((uint)bytes.Length, returned);
        return new(BitConverter.ToUInt32(bytes), Encoding.Unicode.GetString(bytes, 4, 512).TrimEnd('\0'),
            Encoding.Unicode.GetString(bytes, 516, 512).TrimEnd('\0'));
    }

    private static async Task<(int Pin, int InSync)> WaitForOneDriveSyncAsync(string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        var lastInSync = -1;
        do
        {
            var state = ReadOneDriveSync(path);
            lastInSync = state.InSync;
            if (state.InSync == 1)
            {
                return state;
            }
            await Task.Delay(250);
        } while (DateTimeOffset.UtcNow < deadline);
        throw new IOException($"The synthetic OneDrive file did not become a provider-confirmed synced placeholder; in-sync {lastInSync}, provider {JsonSerializer.Serialize(ReadOneDriveProvider(path))}.");
    }

    private static (int Pin, int InSync) ReadOneDriveSync(string path)
    {
        // READ_ATTRIBUTES, OPEN_REPARSE_POINT and OPEN_NO_RECALL: metadata only.
        using var handle = OpenDirectory(path, 0x80, 7, IntPtr.Zero, 3, 0x00300000, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        var bytes = new byte[8192];
        var result = GetPlaceholderInfo(handle, 0, bytes, (uint)bytes.Length, out var returned);
        // The provider has not converted a newly created local file yet. It cannot
        // count as synced; only an actual successful provider response can qualify.
        if (result == unchecked((int)0x80070178)) return (-1, -1);
        Marshal.ThrowExceptionForHR(result);
        Assert.InRange(returned, 28u, (uint)bytes.Length);
        return (BitConverter.ToInt32(bytes), BitConverter.ToInt32(bytes, 4));
    }

    [DllImport("CldApi.dll", EntryPoint = "CfGetSyncRootInfoByPath", CharSet = CharSet.Unicode)]
    private static extern int GetSyncRootInfo(string path, int informationClass, [Out] byte[] buffer, uint length, out uint returned);
    [DllImport("CldApi.dll", EntryPoint = "CfGetPlaceholderInfo")]
    private static extern int GetPlaceholderInfo(SafeFileHandle handle, int informationClass, [Out] byte[] buffer, uint length, out uint returned);
}
