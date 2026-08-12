using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;

namespace FluxKnowledge.Web.Components.Sources;

/// <summary>Trusted-local page state for verified retained C# facts.</summary>
public sealed class RetainedCsharpCodeDetailPageState(ILocalRetainedCsharpCodeReader reader)
{
    public LocalRetainedCsharpCodeDetailProjection? Detail { get; private set; }
    public LocalDisclosureResult? Excerpt { get; private set; }
    public string? Error { get; private set; }
    public bool HasMoreSymbols => _nextSymbolOrdinal is not null;
    public bool HasMoreReferences => _nextReferenceOrdinal is not null;
    public bool HasMoreDiagnostics => _nextDiagnosticOrdinal is not null;

    private Guid _branchId;
    private int? _nextSymbolOrdinal;
    private int? _nextReferenceOrdinal;
    private int? _nextDiagnosticOrdinal;

    public async ValueTask LoadAsync(Guid branchId, CancellationToken cancellationToken)
    {
        try
        {
            Detail = await reader.ReadPageAsync(
                branchId,
                LocalRetainedCsharpCodePageRequest.First,
                cancellationToken).ConfigureAwait(false);
            Excerpt = Detail is null
                ? null
                : await reader.ReadExcerptAsync(branchId, cancellationToken).ConfigureAwait(false);
            Error = Detail is null ? "The retained C# code detail was not found." : null;
            _branchId = branchId;
            _nextSymbolOrdinal = Detail?.NextSymbolOrdinal;
            _nextReferenceOrdinal = Detail?.NextReferenceOrdinal;
            _nextDiagnosticOrdinal = Detail?.NextDiagnosticOrdinal;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Detail = null;
            Excerpt = null;
            Error = "The retained C# code detail could not be loaded.";
            _branchId = Guid.Empty;
            _nextSymbolOrdinal = null;
            _nextReferenceOrdinal = null;
            _nextDiagnosticOrdinal = null;
        }
    }

    public ValueTask LoadMoreSymbolsAsync(CancellationToken cancellationToken) =>
        LoadMoreAsync(RetainedCsharpFactKind.Symbol, cancellationToken);

    public ValueTask LoadMoreReferencesAsync(CancellationToken cancellationToken) =>
        LoadMoreAsync(RetainedCsharpFactKind.Reference, cancellationToken);

    public ValueTask LoadMoreDiagnosticsAsync(CancellationToken cancellationToken) =>
        LoadMoreAsync(RetainedCsharpFactKind.Diagnostic, cancellationToken);

    private async ValueTask LoadMoreAsync(RetainedCsharpFactKind kind, CancellationToken cancellationToken)
    {
        if (Detail is null || _branchId == Guid.Empty || !HasMore(kind))
        {
            return;
        }

        try
        {
            var page = await reader.ReadPageAsync(
                _branchId,
                new LocalRetainedCsharpCodePageRequest(
                    kind == RetainedCsharpFactKind.Symbol ? _nextSymbolOrdinal : int.MaxValue,
                    kind == RetainedCsharpFactKind.Reference ? _nextReferenceOrdinal : int.MaxValue,
                    kind == RetainedCsharpFactKind.Diagnostic ? _nextDiagnosticOrdinal : int.MaxValue),
                cancellationToken).ConfigureAwait(false);
            if (page is null)
            {
                Detail = null;
                Excerpt = null;
                Error = "The retained C# code detail was not found.";
                return;
            }

            switch (kind)
            {
                case RetainedCsharpFactKind.Symbol:
                    _nextSymbolOrdinal = page.NextSymbolOrdinal;
                    break;
                case RetainedCsharpFactKind.Reference:
                    _nextReferenceOrdinal = page.NextReferenceOrdinal;
                    break;
                case RetainedCsharpFactKind.Diagnostic:
                    _nextDiagnosticOrdinal = page.NextDiagnosticOrdinal;
                    break;
            }

            Detail = page with
            {
                Symbols = kind == RetainedCsharpFactKind.Symbol
                    ? [.. Detail.Symbols, .. page.Symbols]
                    : Detail.Symbols,
                References = kind == RetainedCsharpFactKind.Reference
                    ? [.. Detail.References, .. page.References]
                    : Detail.References,
                Diagnostics = kind == RetainedCsharpFactKind.Diagnostic
                    ? [.. Detail.Diagnostics, .. page.Diagnostics]
                    : Detail.Diagnostics,
                NextSymbolOrdinal = _nextSymbolOrdinal,
                NextReferenceOrdinal = _nextReferenceOrdinal,
                NextDiagnosticOrdinal = _nextDiagnosticOrdinal
            };
            Error = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Error = "The retained C# code detail could not be loaded.";
        }
    }

    private bool HasMore(RetainedCsharpFactKind kind) => kind switch
    {
        RetainedCsharpFactKind.Symbol => HasMoreSymbols,
        RetainedCsharpFactKind.Reference => HasMoreReferences,
        RetainedCsharpFactKind.Diagnostic => HasMoreDiagnostics,
        _ => false
    };

    private enum RetainedCsharpFactKind
    {
        Symbol,
        Reference,
        Diagnostic
    }
}
