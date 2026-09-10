using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluxKnowledge.Application.Models;

public sealed record ModelArtifactSpecification(
    string Revision,
    string Filename,
    string Sha256,
    long ByteLength);

public sealed class ModelBundleSpecification
{
    public ModelBundleSpecification(int schemaVersion, IEnumerable<ModelArtifactSpecification> files)
    {
        SchemaVersion = schemaVersion;
        Files = Array.AsReadOnly(files?.ToArray() ?? throw new ArgumentNullException(nameof(files)));
    }

    public int SchemaVersion { get; }

    public IReadOnlyList<ModelArtifactSpecification> Files { get; }
}

public static class ModelManifestCodec
{
    public const int MaximumBytes = 1024 * 1024;
    private const int MaximumFiles = 256;

    public static ModelBundleSpecification Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0 || utf8Json.Length > MaximumBytes)
        {
            throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
        }

        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(utf8Json);
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
            }

            var rootProperties = new HashSet<string>(StringComparer.Ordinal);
            int? schemaVersion = null;
            JsonElement? filesElement = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!rootProperties.Add(property.Name) || property.Name is not ("schemaVersion" or "files"))
                {
                    throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
                }

                if (property.Name == "schemaVersion")
                {
                    schemaVersion = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value)
                        ? value
                        : throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
                }
                else
                {
                    filesElement = property.Value;
                }
            }

            if (schemaVersion is not 1 || filesElement is not { ValueKind: JsonValueKind.Array })
            {
                throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
            }

            var files = new List<ModelArtifactSpecification>();
            foreach (var fileElement in filesElement.Value.EnumerateArray())
            {
                if (files.Count == MaximumFiles || fileElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
                }

                var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in fileElement.EnumerateObject())
                {
                    if (!values.TryAdd(property.Name, property.Value) ||
                        property.Name is not ("revision" or "filename" or "sha256" or "byteLength"))
                    {
                        throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
                    }
                }

                files.Add(new ModelArtifactSpecification(
                    ReadRequiredString(values, "revision"),
                    ReadRequiredString(values, "filename"),
                    ReadRequiredString(values, "sha256"),
                    ReadRequiredLength(values, "byteLength")));
            }

            return Validate(new ModelBundleSpecification(schemaVersion.Value, files));
        }
        catch (JsonException exception)
        {
            throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid, exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid, exception);
        }
    }

    public static ModelBundleSpecification Validate(ModelBundleSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (specification.SchemaVersion != 1 || specification.Files.Count is 0 or > MaximumFiles)
        {
            throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
        }

        long total = 0;
        var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<ModelArtifactSpecification>(specification.Files.Count);
        foreach (var file in specification.Files)
        {
            if (file is null)
            {
                throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
            }

            var revision = RequireHex(file.Revision, validLengths: [40, 64], ModelStoreReasons.SpecificationInvalid);
            var sha256 = RequireHex(file.Sha256, validLengths: [64], ModelStoreReasons.SpecificationInvalid);
            var filename = ValidateFilename(file.Filename);
            if (file.ByteLength < 0 || !filenames.Add(filename))
            {
                throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
            }

            try
            {
                total = checked(total + file.ByteLength);
            }
            catch (OverflowException exception)
            {
                throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid, exception);
            }

            normalized.Add(new ModelArtifactSpecification(revision, filename, sha256, file.ByteLength));
        }

        return new ModelBundleSpecification(specification.SchemaVersion, normalized);
    }

    public static string Fingerprint(ModelBundleSpecification specification)
    {
        var normalized = Validate(specification);
        var builder = new StringBuilder();
        builder.Append(normalized.SchemaVersion).Append('\n');
        foreach (var file in normalized.Files.OrderBy(static item => item.Filename, StringComparer.Ordinal))
        {
            builder.Append(file.Revision).Append('\t')
                .Append(file.Filename).Append('\t')
                .Append(file.Sha256).Append('\t')
                .Append(file.ByteLength).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string ReadRequiredString(IReadOnlyDictionary<string, JsonElement> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
        }

        var result = value.GetString();
        if (string.IsNullOrWhiteSpace(result) || result.Length > 1024)
        {
            throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
        }

        return result;
    }

    private static long ReadRequiredLength(IReadOnlyDictionary<string, JsonElement> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
        }

        return result;
    }

    private static string RequireHex(string? value, IReadOnlyList<int> validLengths, string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(value) || !validLengths.Contains(value.Length) || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ModelManifestException(reasonCode);
        }

        return value.ToLowerInvariant();
    }

    private static string ValidateFilename(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.Length > 128 ||
            filename is "." or ".." ||
            filename.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 ||
            filename.EndsWith(' ') || filename.EndsWith('.') ||
            filename.Any(static character => char.IsControl(character)))
        {
            throw new ModelManifestException(ModelStoreReasons.PathUnsafe);
        }

        var stem = Path.GetFileNameWithoutExtension(filename);
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
        {
            throw new ModelManifestException(ModelStoreReasons.PathUnsafe);
        }

        return filename;
    }
}
