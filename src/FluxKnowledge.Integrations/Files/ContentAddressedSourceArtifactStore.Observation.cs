using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

public sealed partial class ContentAddressedSourceArtifactStore
{
    public ValueTask<IRetainedFileObservationLease> OpenVerificationObservationAsync(string relativePath, string sha256,
        long byteLength, ISourceVerificationReadBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return OpenObservationCoreAsync(relativePath, sha256, byteLength, true, budget, cancellationToken);
    }

    public ValueTask<IRetainedFileObservationLease> OpenObservationAsync(string relativePath, string hash,
        long byteLength, bool verifyChecksum, CancellationToken ct) =>
        OpenObservationCoreAsync(relativePath, hash, byteLength, verifyChecksum, null, ct);

    private async ValueTask<IRetainedFileObservationLease> OpenObservationCoreAsync(string relativePath, string hash,
        long byteLength, bool verifyChecksum, ISourceVerificationReadBudget? budget, CancellationToken ct)
    {
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit) || hash != hash.ToLowerInvariant() ||
            !relativePath.Equals(Path.Combine("sha256", hash[..2], hash + ".bin"), StringComparison.Ordinal))
            throw new InvalidDataException("RetainedObservationBindingInvalid");
        ct.ThrowIfCancellationRequested();
        EnsureRootCurrent();
        var shaPath = Path.Combine(_root, "sha256");
        var shardPath = Path.Combine(shaPath, hash[..2]);
        ValidateProjectedContained(shaPath); ValidateProjectedContained(shardPath);
        var sha = PhysicalFileIdentity.OpenDirectoryLease(shaPath);
        PhysicalDirectoryLease? shard = null; FileStream? stream = null;
        try
        {
            ValidateLeaseContained(sha);
            shard = PhysicalFileIdentity.OpenDirectoryLease(shardPath); ValidateLeaseContained(shard);
            var handle = PhysicalFileIdentity.OpenReadNoFollow(Path.Combine(shardPath, hash + ".bin"));
            try { stream = new FileStream(handle, FileAccess.Read, budget is null ? 128 * 1024 : 1, isAsync: true); }
            catch { handle.Dispose(); throw; }
            var stamp = WindowsSourceObservationReader.DescribeRetained(handle);
            DateTimeOffset? checksumVerifiedAt = null;
            if (stamp.ByteLength != byteLength) throw new InvalidDataException("RetainedObservationLengthMismatch");
            if (verifyChecksum)
            {
                var buffer = new byte[128 * 1024];
                using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long consumed = 0;
                while (consumed < stamp.ByteLength)
                {
                    var count = (int)Math.Min(buffer.Length, stamp.ByteLength - consumed);
                    if (budget is not null) await budget.AcquireAsync(count, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    var filled = 0;
                    while (filled < count)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(filled, count - filled), ct).ConfigureAwait(false);
                        if (read == 0) throw new InvalidDataException("RetainedObservationLengthMismatch");
                        budget?.RecordRead(SourceVerificationReadKind.Retained, read);
                        RetainedBytesRead?.Invoke(read);
                        filled += read;
                    }
                    checksum.AppendData(buffer, 0, count);
                    consumed += count;
                }
                if (Convert.ToHexStringLower(checksum.GetHashAndReset()) != hash) throw new InvalidDataException("RetainedObservationChecksumMismatch");
                checksumVerifiedAt = _time.GetUtcNow();
                stream.Position = 0;
            }
            var observation = new RetainedObservationLease(this, sha, shard, stream, stamp, checksumVerifiedAt);
            observation.ValidateCurrent();
            ct.ThrowIfCancellationRequested();
            return observation;
        }
        catch
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            shard?.Dispose(); sha.Dispose(); throw;
        }
    }

    private sealed class RetainedObservationLease(ContentAddressedSourceArtifactStore owner, PhysicalDirectoryLease sha,
        PhysicalDirectoryLease shard, FileStream stream, RetainedFileObservation stamp, DateTimeOffset? checksumVerifiedAt) : IRetainedFileObservationLease
    {
        public RetainedFileObservation Observation { get; } = stamp;
        public DateTimeOffset? ChecksumVerifiedAtUtc { get; } = checksumVerifiedAt;
        public void ValidateCurrent()
        {
            owner.EnsureRootCurrent(); owner.ValidateLeaseContained(sha); owner.ValidateLeaseContained(shard);
            if (WindowsSourceObservationReader.DescribeRetained(stream.SafeFileHandle) != Observation)
                throw new InvalidDataException("RetainedObservationChanged");
        }
        public async ValueTask DisposeAsync()
        { try { await stream.DisposeAsync().ConfigureAwait(false); } finally { shard.Dispose(); sha.Dispose(); } }
    }
}
