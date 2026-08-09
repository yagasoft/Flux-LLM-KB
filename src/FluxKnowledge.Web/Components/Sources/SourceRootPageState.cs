using FluxKnowledge.Application.Contracts;

namespace FluxKnowledge.Web.Components.Sources;

public sealed class SourceRootPageState(ISourceRootProjectionReader reader)
{
    public IReadOnlyList<SourceRootListProjection> Roots { get; private set; } = [];

    public SourceRootPreview? Preview { get; private set; }

    private string? PreviewFingerprint { get; set; }

    public async ValueTask ReloadAsync(CancellationToken cancellationToken) =>
        Roots = await reader.ReadRootsAsync(cancellationToken).ConfigureAwait(false);

    public ValueTask LoadPreviewAsync(CancellationToken cancellationToken) =>
        LoadPreviewAsync(SourceRootDraft.Empty, cancellationToken);

    public async ValueTask LoadPreviewAsync(SourceRootDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        InvalidatePreview();
        Preview = await reader.PreviewAsync(draft, cancellationToken).ConfigureAwait(false);
        PreviewFingerprint = Fingerprint(draft);
    }

    public bool IsPreviewCurrent(SourceRootDraft draft) =>
        Preview is not null && string.Equals(PreviewFingerprint, Fingerprint(draft), StringComparison.Ordinal);

    public void InvalidatePreview()
    {
        Preview = null;
        PreviewFingerprint = null;
    }

    public ValueTask HandleStatusChangedAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statusChanged);
        return string.Equals(statusChanged.Projection, "sources", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(statusChanged.Projection, "reconnect", StringComparison.OrdinalIgnoreCase)
            ? ReloadAsync(cancellationToken)
            : ValueTask.CompletedTask;
    }

    private static string Fingerprint(SourceRootDraft draft) => string.Join(
        "\u001f",
        draft.FullPath,
        draft.DisplayName,
        draft.Recursive ? "1" : "0",
        draft.MaximumFileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        draft.RequestedBy,
        string.Join("\u001e", draft.IncludePatterns),
        string.Join("\u001e", draft.ExcludePatterns));
}
