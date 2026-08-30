using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Files;
using Microsoft.Extensions.Configuration;

namespace FluxKnowledge.Web.Configuration;

public sealed record NativeGoLiveRuntimeConfiguration(
    IReadOnlyList<string> SourceRoots,
    LocalIngressOptions LocalIngress,
    bool OutlookEnabled,
    bool WorkerEnabled,
    bool ModelRuntimeEnabled,
    bool GpuEnabled,
    bool OcrEnabled,
    bool VisionEnabled,
    bool AsrEnabled,
    bool FfmpegEnabled,
    bool NetworkParsingEnabled);

/// <summary>Defines the intentionally inert capabilities of the native go-live runtime.</summary>
public static class NativeGoLiveRuntimeOptions
{
    public static NativeGoLiveRuntimeConfiguration Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configuredIngress = configuration.GetSection("LocalIngress:AllowedRoots")
            .GetChildren()
            .Select(static child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();
        var requiredIngress = LiveRootLayout.Production.RetainedRoot;
        if (configuredIngress.Any(root => !string.Equals(root, requiredIngress, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("retained-ingress-required");
        }

        var result = new NativeGoLiveRuntimeConfiguration(
            ReadRoots(configuration, "SourceRoots"),
            new LocalIngressOptions([requiredIngress]),
            ReadBoolean(configuration, "Outlook:Enabled") || ReadBoolean(configuration, "OutlookCapture:Enabled"),
            ReadBoolean(configuration, "Worker:Enabled") || ReadBoolean(configuration, "NativeWorker:Enabled"),
            ReadBoolean(configuration, "Runtime:ModelRuntimeEnabled"),
            ReadBoolean(configuration, "Runtime:GpuEnabled"),
            ReadBoolean(configuration, "Runtime:OcrEnabled"),
            ReadBoolean(configuration, "Runtime:VisionEnabled"),
            ReadBoolean(configuration, "Runtime:AsrEnabled"),
            ReadBoolean(configuration, "Runtime:FfmpegEnabled"),
            ReadBoolean(configuration, "Runtime:NetworkParsingEnabled"));
        ValidateEffective(result);
        return result;
    }

    public static void ValidateEffective(NativeGoLiveRuntimeConfiguration options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SourceRoots is null || options.SourceRoots.Count != 0)
        {
            throw new InvalidOperationException("source-roots-active");
        }

        var roots = LocalIngressOptionsValidator.ValidateAndCanonicalise(options.LocalIngress);
        if (roots.Count != 1 || !string.Equals(roots[0], LiveRootLayout.Production.RetainedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("retained-ingress-required");
        }

        if (options.OutlookEnabled) throw new InvalidOperationException("outlook-active");
        if (options.WorkerEnabled || options.ModelRuntimeEnabled || options.GpuEnabled)
            throw new InvalidOperationException("phase-6-runtime-active");
        if (options.OcrEnabled || options.VisionEnabled || options.AsrEnabled || options.FfmpegEnabled)
            throw new InvalidOperationException("media-runtime-active");
        if (options.NetworkParsingEnabled) throw new InvalidOperationException("network-parsing-active");
    }

    private static IReadOnlyList<string> ReadRoots(IConfiguration configuration, string section) =>
        configuration.GetSection(section).GetChildren()
            .Select(static child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();

    private static bool ReadBoolean(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (value is null) return false;
        if (!bool.TryParse(value, out var result))
        {
            throw new InvalidOperationException($"invalid-disabled-capability:{key}");
        }

        return result;
    }
}
