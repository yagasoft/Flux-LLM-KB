using System.Runtime.CompilerServices;

namespace FluxKnowledge.Testing;

/// <summary>Source checks follow their compiled caller, independently of redirected build output.</summary>
internal static class RepositoryFiles
{
    internal static string Root([CallerFilePath] string callerFile = "")
    {
        for (var directory = Directory.GetParent(callerFile); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FluxKnowledge.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate the checked-out repository containing the test source.");
    }
}
