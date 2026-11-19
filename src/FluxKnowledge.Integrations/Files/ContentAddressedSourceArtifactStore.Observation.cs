using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

public sealed partial class ContentAddressedSourceArtifactStore
{
    public ValueTask<IRetainedFileObservationLease> OpenObservationAsync(string relativePath, string hash,
        long byteLength, CancellationToken ct) =>
        OpenObservationCoreAsync(relativePath, hash, byteLength, ct);

    private async ValueTask<IRetainedFileObservationLease> OpenObservationCoreAsync(string relativePath, string hash,
        long byteLength, CancellationToken ct)
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
            try { stream = new FileStream(handle, FileAccess.Read, 128 * 1024, isAsync: true); }
            catch { handle.Dispose(); throw; }
            var stamp = WindowsSourceObservationReader.DescribeRetained(handle);
            if (stamp.ByteLength != byteLength) throw new InvalidDataException("RetainedObservationLengthMismatch");
            var observation = new RetainedObservationLease(this, sha, shard, stream, stamp);
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
        PhysicalDirectoryLease shard, FileStream stream, RetainedFileObservation stamp) : IRetainedFileObservationLease
    {
        public RetainedFileObservation Observation { get; } = stamp;
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
