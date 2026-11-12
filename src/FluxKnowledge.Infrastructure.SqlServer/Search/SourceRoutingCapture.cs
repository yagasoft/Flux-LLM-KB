using FluxKnowledge.Application.Ports;
using System.Globalization;
using System.Text;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

internal sealed record SourceRoutingSelection(IReadOnlySet<CanonicalBindingKey> AllKeys,
    IReadOnlySet<CanonicalBindingKey> DirectedKeys, SourceDirectedRetrievalStatus Status, long EstimatedBytes);

/// <summary>Request-local optional routing; complete canonical keys remain independent of metadata availability.</summary>
internal sealed class SourceRoutingCapture
{
    private readonly long _maximumBytes;
    private readonly IReadOnlySet<string> _query;
    private readonly HashSet<CanonicalBindingKey> _keys = [];
    private readonly HashSet<CanonicalBindingKey> _directed = [];
    private Dictionary<CanonicalBindingKey, RoutingPath>? _paths = [];
    private long _bytes, _peak;
    private bool _sealed;

    internal SourceRoutingCapture(string query, long maximumBytes = 64L * 1024 * 1024)
    {
        _maximumBytes = maximumBytes;
        _query = SourcePathTerms.Tokens(query).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _bytes = checked(1024L + _query.Sum(token => 128L + token.Length * 8L));
        if (_bytes > maximumBytes || maximumBytes <= 0) Disable();
        else _peak = _bytes;
    }

    internal void Add(CanonicalBindingKey key, string? source, string? root)
    {
        if (_sealed) throw new InvalidOperationException("Source capture already sealed.");
        _keys.Add(key);
        if (_paths is null) return;
        if (!TryRelative(source, root, out var relative)) { Disable(); return; }
        var path = new RoutingPath(source!, root!);
        if (_paths.TryGetValue(key, out var prior))
        {
            if (prior != path) Disable();
            return;
        }
        var matches = SourcePathTerms.Tokens(relative).Any(token => SourcePathTerms.IsTag(token) && _query.Contains(token));
        // Conservative retained estimate: canonical keys, aliases, strings and selected keys.
        // It is not an actual managed-heap or process-memory measurement.
        var cost = checked(1024L + 8L * (source!.Length + root!.Length) + (matches ? 256L : 0));
        if (cost > _maximumBytes || _bytes > _maximumBytes - cost) { Disable(); return; }
        _paths.Add(key, path); if (matches) _directed.Add(key);
        _bytes += cost; _peak = Math.Max(_peak, _bytes);
    }

    internal SourceRoutingSelection Complete(bool streamEnded, CancellationToken cancellationToken)
    {
        if (_sealed) throw new InvalidOperationException("Source capture already sealed.");
        _sealed = true;
        if (!streamEnded || cancellationToken.IsCancellationRequested)
        {
            Disable(); cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Incomplete source capture.");
        }
        var status = _paths is null ? SourceDirectedRetrievalStatus.Unavailable : SourceDirectedRetrievalStatus.Ready;
        _paths = null; // Alias evidence is no longer needed once the complete stream has been sealed.
        return new(_keys, _directed, status, _peak);
    }

    private void Disable() { _paths = null; _directed.Clear(); _bytes = 0; }

    private static bool TryRelative(string? source, string? root, out string relative)
    {
        relative = string.Empty;
        if (source is null || root is null || source.Length is < 1 or > 4000 || root.Length is < 1 or > 4000) return false;
        try
        {
            if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(root) ||
                !string.Equals(Path.GetFullPath(source), source, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(root), root, StringComparison.OrdinalIgnoreCase)) return false;
            var folder = Path.TrimEndingDirectorySeparator(root);
            var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + '\\';
            if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            relative = Path.GetRelativePath(folder, source);
            return relative.Length > 0 && !Path.IsPathRooted(relative) && !relative.Split('\\', '/').Any(part => part is "." or "..");
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private sealed record RoutingPath(string Source, string Root);
}

internal static class SourcePathTerms
{
    internal static bool IsTag(string token)
    {
        var count = 0; var letters = 0; var digit = false; var uppercase = true;
        foreach (var rune in token.EnumerateRunes())
        {
            if (++count > 32) return false;
            if (Rune.IsLetter(rune)) { letters++; uppercase &= Rune.IsUpper(rune); }
            digit |= Rune.IsDigit(rune);
        }
        return count >= 2 && letters > 0 && (digit || letters >= 2 && uppercase);
    }

    internal static IEnumerable<string> Tokens(string text)
    {
        var token = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsLetterOrDigit(rune) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                if (rune.IsBmp) token.Append((char)rune.Value);
                else token.Append(rune.ToString());
            }
            else if (token.Length != 0) { yield return token.ToString(); token.Clear(); }
        }
        if (token.Length != 0) yield return token.ToString();
    }
}
