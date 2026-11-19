using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

/// <summary>Owns a bounded read-only handle and its physical directory leases. Content identity comes from ingestion.</summary>
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

    /// <summary>Takes ownership even when metadata validation fails. Opening does not read or hash the payload.</summary>
    public static async ValueTask<RetainedSourceReadLease> OpenAsync(SourceRevisionId revisionId, Stream stream,
        string expectedSha256, long expectedLength, long maximumByteLength, CancellationToken cancellationToken,
        IDisposable? ownership = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!stream.CanRead || !stream.CanSeek || stream.CanWrite || maximumByteLength <= 0 ||
                expectedLength < 0 || expectedLength > maximumByteLength || stream.Length != expectedLength ||
                expectedSha256 is not { Length: 64 } || !expectedSha256.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("The retained artifact exceeds its bounded read contract.");
            stream.Position = 0;
            cancellationToken.ThrowIfCancellationRequested();
            return new RetainedSourceReadLease(revisionId, stream, expectedSha256, expectedLength, ownership);
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
