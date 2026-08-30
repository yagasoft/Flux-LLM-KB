using System.Runtime.CompilerServices;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Web;

namespace FluxKnowledge.Web.Tests;

internal static class IsolatedWebComposition
{
    [ModuleInitializer]
    internal static void Configure()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "FluxKnowledgeWebTests",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var layout = LiveRootLayout.CreateForIsolatedTests(root);
        Directory.CreateDirectory(layout.IndexRoot);
        Directory.CreateDirectory(layout.RetainedRoot);
        Directory.CreateDirectory(layout.ConfigRoot);
        Directory.CreateDirectory(layout.SpoolRoot);
        Directory.CreateDirectory(layout.TempRoot);
        Directory.CreateDirectory(layout.LogsRoot);
        WebHostComposition.ConfigureIsolatedTestLayout(layout);
    }
}
