using System.Text.Json;
using FluxKnowledge.Application.Models;

namespace PpStructureOnnxBenchmark;

internal sealed record ProbeBundle(ModelBundleSpecification Specification, IReadOnlyDictionary<string, string> Paths, IReadOnlyDictionary<string, string> RoleDirectories);

internal static class BundleManifest
{
    private const string Root = @"J:\Models\runtimes\ppstructurev3-3.7.0-ort-1.30.0-cp312\bundles";
    private const string Availability = @"J:\Models\manifests\english-ocr-paddle-onnx-cpu-20260919\bundle-availability.json";
    private static readonly IReadOnlyDictionary<string, string> Native = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["text_detection"] = @"J:\Models\manifests\document-ocr-20260919\text-detection.json",
        ["english_recognition"] = @"J:\Models\manifests\document-ocr-20260919\english-recognition.json",
        ["page_orientation"] = @"J:\Models\manifests\document-ocr-20260919\page-orientation.json",
        ["line_orientation"] = @"J:\Models\manifests\document-ocr-20260919\line-orientation.json"
    };

    internal static ProbeBundle Load()
    {
        var files = new List<ModelArtifactSpecification>();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var document = JsonDocument.Parse(File.ReadAllBytes(Availability)))
        {
            foreach (var entry in document.RootElement.GetProperty("models").EnumerateArray())
            {
                Add(entry.GetProperty("role").GetString()!, entry.GetProperty("revision").GetString()!, entry.GetProperty("filename").GetString()!, entry.GetProperty("sha256").GetString()!, entry.GetProperty("bytes").GetInt64(), files, paths);
            }
        }
        foreach (var native in Native)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(native.Value));
            foreach (var entry in document.RootElement.GetProperty("files").EnumerateArray())
            {
                Add(native.Key, entry.GetProperty("revision").GetString()!, entry.GetProperty("filename").GetString()!, entry.GetProperty("sha256").GetString()!, entry.GetProperty("byteLength").GetInt64(), files, paths);
            }
        }
        if (files.Count != 22 || paths.Count != 22) throw new InvalidDataException("fixed-bundle-manifest-count-invalid");
        var roles = paths.Keys.Select(static value => value.Split('.', 2)[0]).Distinct(StringComparer.Ordinal).ToArray();
        if (roles.Length != 11) throw new InvalidDataException("fixed-bundle-role-count-invalid");
        return new ProbeBundle(new ModelBundleSpecification(1, files), paths, roles.ToDictionary(static role => role, static role => Path.Combine(Root, role), StringComparer.Ordinal));
    }

    private static void Add(string role, string revision, string actualName, string sha256, long bytes, ICollection<ModelArtifactSpecification> files, IDictionary<string, string> paths)
    {
        if (role.IndexOfAny(['\\', '/', ':']) >= 0 || actualName is not ("inference.onnx" or "inference.yml")) throw new InvalidDataException("fixed-bundle-entry-invalid");
        var logicalName = role + "." + actualName;
        if (!paths.TryAdd(logicalName, Path.Combine(Root, role, actualName))) throw new InvalidDataException("fixed-bundle-entry-duplicate");
        files.Add(new ModelArtifactSpecification(revision, logicalName, sha256, bytes));
    }
}
