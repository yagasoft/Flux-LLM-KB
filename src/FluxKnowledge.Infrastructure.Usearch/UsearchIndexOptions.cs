namespace FluxKnowledge.Infrastructure.Usearch;

public sealed record UsearchIndexOptions(string RootPath)
{
    public const string ConfigurationSectionName = "Usearch";

    public static UsearchIndexOptions FromConfiguredRoot(
        string? rootPath,
        string? repositoryRoot = null,
        string? deploymentRoot = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new InvalidOperationException("Usearch:RootPath must be configured to an app-owned data directory.");
        }

        var fullPath = Path.GetFullPath(rootPath);
        var resolvedRepositoryRoot = ResolvePhysicalPath(repositoryRoot ?? Directory.GetCurrentDirectory());
        var resolvedDeploymentRoot = ResolvePhysicalPath(deploymentRoot ?? AppContext.BaseDirectory);
        var resolvedRoot = ResolvePhysicalPath(fullPath);
        if (IsUnder(resolvedRoot, resolvedRepositoryRoot) || IsUnder(resolvedRoot, resolvedDeploymentRoot))
        {
            throw new InvalidOperationException("The USearch root must be outside the repository and deployment directories.");
        }

        return new UsearchIndexOptions(fullPath);
    }

    private static bool IsUnder(string candidate, string root) =>
        candidate.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase);

    private static string ResolvePhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = new DirectoryInfo(fullPath);
        var suffix = new Stack<string>();
        while (!directory.Exists && directory.Parent is not null)
        {
            suffix.Push(directory.Name);
            directory = directory.Parent;
        }
        var resolved = directory.ResolveLinkTarget(returnFinalTarget: true);
        var physical = (resolved ?? directory).FullName;
        while (suffix.Count > 0)
        {
            physical = Path.Combine(physical, suffix.Pop());
        }

        return Path.TrimEndingDirectorySeparator(physical);
    }
}
