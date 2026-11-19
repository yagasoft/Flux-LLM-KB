using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Windows.NativeGoLive;

namespace FluxKnowledge.Integrations.Files;

public sealed class WindowsTranscriptSidecarWriter
{
    private const string StagingPrefix = ".fluxknowledge-transcript-";
    private const string StagingSuffix = ".tmp";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async ValueTask<TranscriptSidecarReceipt> WriteAsync(SourceRootConfiguration root, string recordingRelativePath,
        SourceFileObservation expected, string transcript, CancellationToken cancellationToken, Action? validateOwner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        cancellationToken.ThrowIfCancellationRequested();
        var length = StrictUtf8.GetByteCount(transcript);
        if (length > MediaTranscriptionGpuContracts.MaximumResultBytes)
            throw new IOException("transcription-sidecar-too-large");

        var content = StrictUtf8.GetBytes(transcript);
        var relativePath = TargetRelativePath(root, recordingRelativePath);
        using var source = OpenRecording(root, relativePath, expected);
        ValidateRecording();

        if (!await ExistingMatchesAsync().ConfigureAwait(false))
        {
            var staging = StagingPrefix + Guid.NewGuid().ToString("N") + StagingSuffix;
            try
            {
                await HandleRelativeNativeFileSystem.PublishTranscriptAsync(source.Parent.Handle, staging,
                    Path.GetFileName(relativePath), content, ValidateRecording, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                if (!await ExistingMatchesAsync().ConfigureAwait(false)) throw;
            }
        }
        ValidateRecording();
        return new(relativePath, Convert.ToHexStringLower(SHA256.HashData(content)), length);

        void ValidateRecording() => ValidateSource(source, expected, validateOwner);

        async ValueTask<bool> ExistingMatchesAsync()
        {
            var bytes = await ReadExistingAsync(root, relativePath, length, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return false;
            if (!bytes.AsSpan().SequenceEqual(content)) throw new IOException("transcription-sidecar-existing-file-conflicts");
            return true;
        }
    }

    public async ValueTask<TranscriptSidecarReceipt?> VerifyPlacedAsync(SourceRootConfiguration root, string recordingRelativePath,
        SourceFileObservation expected, string sha256, int length, CancellationToken cancellationToken, Action? validateOwner = null)
    {
        if (length is <= 0 or > MediaTranscriptionGpuContracts.MaximumResultBytes)
            throw new IOException("transcription-sidecar-too-large");
        var relativePath = TargetRelativePath(root, recordingRelativePath);
        using var source = OpenRecording(root, relativePath, expected);
        ValidateSource(source, expected, validateOwner);
        var bytes = await ReadExistingAsync(root, relativePath, length, cancellationToken).ConfigureAwait(false);
        if (bytes is null) return null;
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256)
            throw new IOException("transcription-sidecar-existing-file-conflicts");
        ValidateSource(source, expected, validateOwner);
        return new(relativePath, sha256, length);
    }

    private static string TargetRelativePath(SourceRootConfiguration root, string recordingRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingRelativePath);
        var components = recordingRelativePath.Split(['/', '\\']);
        if (root.State != SourceRootState.Enabled || root.DiscoveryMode != SourceDiscoveryMode.Filesystem ||
            root.RequiresPhysicalIdentityValidation && root.PhysicalIdentityFingerprint is null ||
            !root.Recursive && components.Length > 1 ||
            components.Any(component => component.Length == 0 || component is "." or ".." ||
                component.EndsWith('.') || component.EndsWith(' ') || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new IOException("transcription-sidecar-source-policy");

        var recording = Path.Combine(components);
        var relativePath = recording + MediaTranscriptionGpuContracts.SidecarSuffix;
        if ((root.AllowedClassifications.Count != 0 &&
            !root.AllowedClassifications.Contains("text/plain", StringComparer.OrdinalIgnoreCase)) ||
            !LocalSourceEnumerator.MatchesPolicy(recording, root.IncludePatterns, root.ExcludePatterns) ||
            !LocalSourceEnumerator.MatchesPolicy(relativePath, root.IncludePatterns, root.ExcludePatterns) ||
            Path.GetFileName(relativePath).Length > 255)
            throw new IOException("transcription-sidecar-source-policy");
        return relativePath;
    }

    private static WindowsSourcePathAccess.SourceReadLease OpenRecording(SourceRootConfiguration root, string relativePath,
        SourceFileObservation expected) => WindowsSourcePathAccess.OpenRead(root.CanonicalPath,
            Path.Combine(root.CanonicalPath, relativePath[..^MediaTranscriptionGpuContracts.SidecarSuffix.Length]),
            root.PhysicalIdentityFingerprint, expected.Identity);

    private static void ValidateSource(WindowsSourcePathAccess.SourceReadLease source, SourceFileObservation expected, Action? validateOwner)
    {
        validateOwner?.Invoke();
        source.Parent.ValidateCurrent();
        WindowsSourcePathAccess.EnsureContentAvailable(WindowsSourcePathAccess.ReadMetadata(source.Handle));
        if (expected.LinkCount != 1 || WindowsSourceObservationReader.Describe(source) != expected)
            throw new SourceSnapshotChangedException("transcription-sidecar-recording-changed");
    }

    private static async ValueTask<byte[]?> ReadExistingAsync(SourceRootConfiguration root, string relativePath, int expectedLength, CancellationToken token)
    {
        WindowsSourcePathAccess.SourceReadLease existing;
        try
        {
            existing = WindowsSourcePathAccess.OpenRead(root.CanonicalPath, Path.Combine(root.CanonicalPath, relativePath),
                root.PhysicalIdentityFingerprint);
        }
        catch (FileNotFoundException) { return null; }

        using (existing)
        {
            if (PhysicalFileIdentity.GetLinkCount(existing.Handle) != 1 ||
                RandomAccess.GetLength(existing.Handle) != expectedLength)
                throw new IOException("transcription-sidecar-existing-file-conflicts");
            var bytes = new byte[expectedLength];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await RandomAccess.ReadAsync(existing.Handle, bytes.AsMemory(offset), offset, token)
                    .ConfigureAwait(false);
                if (read == 0) throw new IOException("transcription-sidecar-existing-file-conflicts");
                offset += read;
            }
            return bytes;
        }
    }

    internal static bool IsStagingFile(string name) =>
        name.Length == StagingPrefix.Length + 32 + StagingSuffix.Length &&
        name.StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(StagingSuffix, StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(name.AsSpan(StagingPrefix.Length, name.Length - StagingPrefix.Length - StagingSuffix.Length), "N", out _);
}

public sealed record TranscriptSidecarReceipt(string RelativePath, string ContentSha256, int ByteLength);
