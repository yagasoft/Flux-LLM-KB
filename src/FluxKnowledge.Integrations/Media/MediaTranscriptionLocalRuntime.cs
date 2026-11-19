using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Integrations.Files;

namespace FluxKnowledge.Integrations.Media;

/// <summary>Pins the supervised local runtime to two previously acquired models and a single decoding policy.</summary>
public static class MediaTranscriptionLocalRuntime
{
    private const string Cache = @"J:\Models";
    private const string GeneralRevision = "edaa852ec7e145841d8ffdb056a99866b5f0a478";
    private const string EgyptianRevision = "7676461a26fc6d62d1d852f54749c24d9cc383f7";
    internal const string Python = Cache + @"\runtimes\cpython-3.12.14-iis-84d3e0db03b2\python.exe";
    private const string Packages = Cache + @"\runtimes\local-transcription-fw1.2.1-ct2-4.8.1-py312\packages";
    private const string NativeLibraries = Cache + @"\runtimes\ppstructurev3-3.7.0-ort-1.30.0-cp312\Lib\site-packages\nvidia";
    private const string GeneralModel = Cache + @"\bundles\faster-whisper-large-v3\" + GeneralRevision;
    private const string EgyptianModel = Cache + @"\bundles\whisper-medium-arabic-codeswitched-ct2\" + EgyptianRevision;
    internal static string Script => Path.Combine(AppContext.BaseDirectory, "Media", "media_transcription_executor.py");
    private static readonly string[] DllDirectories =
        ["cublas", "cuda_runtime", "cudnn", "cufft", "curand", "cusolver", "cusparse", "nvjitlink"];
    internal static object Manifest => new { packages = Packages, generalModel = GeneralModel, egyptianModel = EgyptianModel,
        dllDirectories = DllDirectories.Select(component => Path.Combine(NativeLibraries, component, "bin")).ToArray() };

    // Exclusive admission uses the higher estimated peak of the two serially loaded models.
    // This is a staging policy value until real peak memory is measured.
    public static MediaTranscriptionGpuRuntime Profile { get; } = new(
        $"fw1.2.1-ct2.4.8.1:{GeneralRevision}:{EgyptianRevision}",
        "media-transcription-v1:detect-five-30s-0.80:ar-en-beam5-vad-int8-f16",
        "media-transcription:faster-whisper-v1", 8L * 1024 * 1024 * 1024);

    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            PhysicalFileIdentity.EnsureNoReparsePointTraversal(Cache);
            return HasPinnedFile(Python) && File.Exists(Script) &&
                HasPinnedFile(Path.Combine(Packages, "faster_whisper", "transcribe.py")) &&
                HasPinnedFile(Path.Combine(Packages, "ctranslate2", "__init__.py")) &&
                HasPinnedFile(Path.Combine(Packages, "av", "__init__.py")) &&
                HasPinnedFile(Path.Combine(Packages, "onnxruntime", "__init__.py")) &&
                HasPinnedFile(Path.Combine(Packages, "faster_whisper", "assets", "silero_vad_v6.onnx"), 1_245_151) &&
                DllDirectories.All(name => HasPinnedDirectory(Path.Combine(NativeLibraries, name, "bin"))) &&
                HasModel(GeneralModel, 3_087_284_237, 2394, 340, 2_480_617, 1_068_114) &&
                HasModel(EgyptianModel, 774_731_149, 2257, 184_990, 2_480_466, 1_068_103);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool HasModel(string directory, long weights, long config, long preprocessing, long tokenizer, long vocabulary) =>
        HasPinnedFile(Path.Combine(directory, "model.bin"), weights) &&
        HasPinnedFile(Path.Combine(directory, "config.json"), config) &&
        HasPinnedFile(Path.Combine(directory, "preprocessor_config.json"), preprocessing) &&
        HasPinnedFile(Path.Combine(directory, "tokenizer.json"), tokenizer) &&
        HasPinnedFile(Path.Combine(directory, "vocabulary.json"), vocabulary);

    private static bool HasPinnedFile(string path, long? expectedBytes = null)
    {
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return false;
        var file = new FileInfo(path);
        return file.Exists && (expectedBytes is null || file.Length == expectedBytes);
    }

    private static bool HasPinnedDirectory(string path)
    {
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(path);
        return Directory.Exists(path);
    }
}
