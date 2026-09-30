using System.Text;
using System.Text.Json;

namespace FluxKnowledge.Application.IntegrationV1;

/// <summary>Incremental ingress with live resource admission, rather than an aggregate product size ceiling.</summary>
public static class NativeRequestInput
{
    public static async Task<JsonDocument> ReadJsonAsync(Stream stream, CancellationToken cancellationToken, Func<long>? availableMemory = null)
    {
        using var reservation = new Reservation(availableMemory);
        await using var guarded = new ResourceReadStream(stream, reservation);
        var document = await JsonDocument.ParseAsync(guarded, cancellationToken: cancellationToken).ConfigureAwait(false);
        try { Validate(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    public static async Task<string> ReadTextAsync(TextReader reader, Reservation reservation, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            reservation.AddInputBytes(read * sizeof(char));
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }

    public static void Validate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new NativeOperationException("invalid-json");
                Validate(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var entry in element.EnumerateArray()) Validate(entry);
    }

    public sealed class Reservation(Func<long>? availableMemory = null) : IDisposable
    {
        private static readonly object Gate = new();
        private static long pendingBytes;
        private long reserved;
        // Accounts for parser rows, decoded strings, mapping/canonicalisation and mutation binding copies.
        // This is an allocation estimate; acceptance depends on current memory, not a fixed request size.
        private const int RepresentationBytesPerInputByte = 32;

        public void AddInputBytes(int count)
        {
            var estimate = checked((long)count * RepresentationBytesPerInputByte);
            lock (Gate)
            {
                var headroom = availableMemory?.Invoke() ?? Headroom();
                if (estimate > headroom - pendingBytes) throw new NativeOperationException("resource-pressure");
                pendingBytes = checked(pendingBytes + estimate);
                reserved = checked(reserved + estimate);
            }
        }

        public void Dispose()
        {
            lock (Gate) { pendingBytes -= reserved; reserved = 0; }
        }

        private static long Headroom()
        {
            var memory = GC.GetGCMemoryInfo();
            return Math.Max(0, Math.Min(memory.TotalAvailableMemoryBytes - GC.GetTotalMemory(false),
                memory.HighMemoryLoadThresholdBytes - memory.MemoryLoadBytes));
        }
    }

    public sealed class ResourceReadStream(Stream inner, Reservation reservation) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count); reservation.AddInputBytes(read); return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            reservation.AddInputBytes(read); return read;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
