namespace FluxKnowledge.Domain.Sources;

/// <summary>Containment for already validated source-relative paths, including the admitted root subtree.</summary>
public static class SourcePathScope
{
    public static bool Contains(string scope, bool isSubtree, string relativePath) =>
        relativePath.Equals(scope, StringComparison.OrdinalIgnoreCase) || isSubtree &&
        (scope == "." || relativePath.StartsWith(Path.TrimEndingDirectorySeparator(scope) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
}
