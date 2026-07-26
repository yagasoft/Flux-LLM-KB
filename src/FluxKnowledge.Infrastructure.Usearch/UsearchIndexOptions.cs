namespace FluxKnowledge.Infrastructure.Usearch;

public sealed record UsearchIndexOptions(string RootPath)
{
    public const string ConfigurationSectionName = "Usearch";

    public static UsearchIndexOptions FromConfiguredRoot(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new InvalidOperationException("Usearch:RootPath must be configured to an app-owned data directory.");
        }

        var fullPath = Path.GetFullPath(rootPath);
        var repositoryRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
        var deploymentRoot = Path.GetFullPath(AppContext.BaseDirectory);
        if (IsUnder(fullPath, repositoryRoot) || IsUnder(fullPath, deploymentRoot))
        {
            throw new InvalidOperationException("The USearch root must be outside the repository and deployment directories.");
        }

        return new UsearchIndexOptions(fullPath);
    }

    private static bool IsUnder(string candidate, string root) =>
        candidate.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase);
}
