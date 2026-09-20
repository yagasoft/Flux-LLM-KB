using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Models;

namespace FluxKnowledge.Infrastructure.Inference.Documents;

internal static class DocumentOcrModelConfiguration
{
    private const string ConfigurationFilename = "inference.yml";

    public static async ValueTask<IReadOnlyList<string>> ReadCtcCharactersAsync(
        VerifiedLocalModelLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var configuration = lease.Files.SingleOrDefault(static file =>
            string.Equals(file.Filename, ConfigurationFilename, StringComparison.Ordinal));
        if (configuration is null || configuration.ByteLength is < 1 or > ModelManifestCodec.MaximumBytes)
        {
            throw new InvalidOperationException("document-ocr-model-configuration-invalid");
        }

        var bytes = new byte[checked((int)configuration.ByteLength)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await lease.ReadAsync(ConfigurationFilename, offset, bytes.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                throw new InvalidOperationException("document-ocr-model-configuration-read-failed");
            }

            offset = checked(offset + read);
        }

        string yaml;
        try
        {
            yaml = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException("document-ocr-model-configuration-invalid", exception);
        }

        return ParseCtcCharacters(yaml);
    }

    private static IReadOnlyList<string> ParseCtcCharacters(string yaml)
    {
        var characters = new List<string>();
        var inDictionary = false;
        var dictionaryIndent = -1;
        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var indent = line.Length - line.TrimStart(' ').Length;
            var content = line[indent..];
            if (!inDictionary)
            {
                if (string.Equals(content, "character_dict:", StringComparison.Ordinal))
                {
                    inDictionary = true;
                    dictionaryIndent = indent;
                }

                continue;
            }

            if (content.Length == 0) continue;
            if (indent <= dictionaryIndent && !content.StartsWith("- ", StringComparison.Ordinal)) break;
            if (indent != dictionaryIndent || !content.StartsWith("- ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("document-ocr-model-configuration-invalid");
            }

            characters.Add(ParseYamlScalar(content[2..]));
        }

        return characters.Count == 0
            ? throw new InvalidOperationException("document-ocr-model-configuration-invalid")
            : characters.AsReadOnly();
    }

    private static string ParseYamlScalar(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value)
                    ?? throw new InvalidOperationException("document-ocr-model-configuration-invalid");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("document-ocr-model-configuration-invalid", exception);
            }
        }

        return string.IsNullOrEmpty(value)
            ? throw new InvalidOperationException("document-ocr-model-configuration-invalid")
            : value;
    }
}
