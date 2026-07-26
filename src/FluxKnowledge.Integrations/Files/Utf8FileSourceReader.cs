using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Pipeline;

namespace FluxKnowledge.Integrations.Files;

public sealed class Utf8FileSourceReader : IUtf8FileSourceReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IReadOnlyList<string> _allowedRoots;

    public Utf8FileSourceReader(LocalIngressOptions options)
    {
        _allowedRoots = LocalIngressOptionsValidator.ValidateAndCanonicalise(options);
    }

    public async ValueTask<Utf8FileSource> ReadAsync(
        string suppliedPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suppliedPath);
        var canonicalPath = Path.GetFullPath(suppliedPath);
        if (!_allowedRoots.Any(root => IsWithinRoot(root, canonicalPath)))
        {
            throw new UnauthorizedAccessException(
                $"The source path is outside the configured local ingress roots: {canonicalPath}");
        }

        var bytes = await File.ReadAllBytesAsync(canonicalPath, cancellationToken)
            .ConfigureAwait(false);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"The source file is not valid UTF-8: {canonicalPath}",
                exception);
        }

        return new Utf8FileSource(
            canonicalPath,
            bytes,
            text,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static bool IsWithinRoot(string root, string path)
    {
        if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
