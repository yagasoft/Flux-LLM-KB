using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Domain.Tests.Sources;

internal static class RetainedCsharpCodeTestData
{
    internal static readonly SourceRevisionId FixedSourceRevisionId =
        new(new Guid("11111111-2222-3333-4444-555555555555"));

    internal static RetainedCsharpCodeClaim Claim(
        byte[] bytes,
        string? inputSha256 = null,
        SourceRevisionId? sourceRevisionId = null,
        Guid? attemptId = null) => new(
            new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            sourceRevisionId ?? FixedSourceRevisionId,
            "retained-parent",
            inputSha256 ?? Sha256(bytes),
            "worker-csharp-test",
            7,
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
            attemptId ?? new Guid("99999999-8888-7777-6666-555555555555"));

    internal static byte[] Utf8(string source, bool bom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        return bom ? Encoding.UTF8.GetPreamble().Concat(bytes).ToArray() : bytes;
    }

    internal static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal sealed class Reader(RetainedSourceBytes retained) : IRetainedSourceReader
    {
        public int ReadCount { get; private set; }

        public ValueTask<RetainedSourceBytes> ReadBytesAsync(
            SourceRevisionId sourceRevisionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(retained);
        }

        public ValueTask<Utf8FileSource> ReadUtf8Async(
            SourceRevisionId sourceRevisionId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The C# processor must use the verified retained-byte reader.");
    }

    internal sealed class ThrowingReader(Exception exception) : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(
            SourceRevisionId sourceRevisionId,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<RetainedSourceBytes>(exception);

        public ValueTask<Utf8FileSource> ReadUtf8Async(
            SourceRevisionId sourceRevisionId,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<Utf8FileSource>(exception);
    }

    internal sealed class Disclosure(
        Func<string, LocalDisclosureKind, LocalDisclosureResult>? evaluate = null,
        Action<string, LocalDisclosureKind>? afterEvaluate = null) : ILocalPrivateContentDisclosure
    {
        public List<(string Value, LocalDisclosureKind Kind)> Calls { get; } = [];

        public LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind)
        {
            Calls.Add((value, kind));
            var result = evaluate?.Invoke(value, kind) ?? new LocalDisclosureResult(value, false, null);
            afterEvaluate?.Invoke(value, kind);
            return result;
        }
    }

    internal sealed class FailingDisclosure : ILocalPrivateContentDisclosure
    {
        public LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind) =>
            throw new InvalidOperationException("synthetic bounded scan failure");
    }

    internal static RetainedSourceBytes Retained(
        SourceRevisionId sourceRevisionId,
        byte[] bytes,
        string? contentSha256 = null,
        long? byteLength = null) =>
        new(sourceRevisionId, bytes, contentSha256 ?? Sha256(bytes), byteLength ?? bytes.LongLength);
}
