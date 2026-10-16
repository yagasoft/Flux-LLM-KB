using System.Security.Cryptography;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

/// <summary>Owns the same read-only handle used for verification and parsing, plus its physical directory leases.</summary>
public sealed class RetainedSourceReadLease : IAsyncDisposable
{
    private readonly IDisposable? _ownership;
    private RetainedSourceReadLease(SourceRevisionId revisionId, Stream stream, string sha256, long length, IDisposable? ownership)
    {
        SourceRevisionId = revisionId;
        Stream = stream;
        ContentSha256 = sha256;
        ByteLength = length;
        _ownership = ownership;
    }

    public SourceRevisionId SourceRevisionId { get; }
    public Stream Stream { get; }
    public string ContentSha256 { get; }
    public long ByteLength { get; }

    /// <summary>Takes ownership even when verification fails. No allocation is proportional to the input length.</summary>
    public static async ValueTask<RetainedSourceReadLease> VerifyAsync(SourceRevisionId revisionId, Stream stream,
        string expectedSha256, long expectedLength, long maximumByteLength, CancellationToken cancellationToken,
        IDisposable? ownership = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!stream.CanRead || !stream.CanSeek || stream.CanWrite || maximumByteLength <= 0 ||
                expectedLength < 0 || expectedLength > maximumByteLength || stream.Length != expectedLength)
                throw new InvalidDataException("The retained artifact exceeds its bounded read contract.");
            stream.Position = 0;
            var buffer = new byte[128 * 1024];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                length = checked(length + read);
                if (length > expectedLength) throw new InvalidDataException("The retained artifact length changed.");
                hash.AppendData(buffer, 0, read);
            }
            if (length != expectedLength || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expectedSha256, StringComparison.Ordinal))
                throw new InvalidDataException("The retained artifact checksum is invalid.");
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            return new RetainedSourceReadLease(revisionId, stream, expectedSha256, length, ownership);
        }
        catch
        {
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            finally { ownership?.Dispose(); }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Stream.DisposeAsync().ConfigureAwait(false); }
        finally { _ownership?.Dispose(); }
    }
}
