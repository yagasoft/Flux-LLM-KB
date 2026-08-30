namespace FluxKnowledge.Integrations.Files;

using FluxKnowledge.Application.Operations;

public static class LocalIngressOptionsValidator
{
    public static IReadOnlyList<string> ValidateAndCanonicalise(LocalIngressOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AllowedRoots is null || options.AllowedRoots.Count == 0)
        {
            throw new ArgumentException(
                "At least one canonical local ingress root is required.",
                nameof(options));
        }

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configuredRoot in options.AllowedRoots)
        {
            if (string.IsNullOrWhiteSpace(configuredRoot) ||
                !Path.IsPathFullyQualified(configuredRoot))
            {
                throw new ArgumentException(
                    "Every local ingress root must be an absolute path.",
                    nameof(options));
            }

            var canonicalRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(configuredRoot));
            if (!Directory.Exists(canonicalRoot) &&
                !string.Equals(canonicalRoot, LiveRootLayout.Production.RetainedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException(
                    $"The local ingress root does not exist: {canonicalRoot}");
            }

            roots.Add(canonicalRoot);
        }

        return roots.ToArray();
    }
}
