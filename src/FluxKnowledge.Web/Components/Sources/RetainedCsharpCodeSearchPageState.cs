using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;

namespace FluxKnowledge.Web.Components.Sources;

/// <summary>Trusted-local search state for persisted retained C# declaration facts.</summary>
public sealed class RetainedCsharpCodeSearchPageState(ILocalRetainedCsharpCodeReader reader)
{
    public IReadOnlyList<LocalRetainedCsharpCodeSearchProjection> Results { get; private set; } = [];
    public string? Error { get; private set; }
    public bool HasMore => _nextCursor is not null;

    private string? _query;
    private LocalRetainedCsharpCodeSearchCursor? _nextCursor;

    public async ValueTask SearchAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var page = await reader.SearchPageAsync(
                new LocalRetainedCsharpCodeSearchPageRequest(query, 10, null),
                cancellationToken).ConfigureAwait(false);
            Results = page.Results;
            _query = query;
            _nextCursor = page.NextCursor;
            Error = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Results = [];
            _query = null;
            _nextCursor = null;
            Error = "Retained C# fact search could not be completed.";
        }
    }

    public async ValueTask LoadMoreAsync(CancellationToken cancellationToken)
    {
        if (_query is null || _nextCursor is null)
        {
            return;
        }

        try
        {
            var page = await reader.SearchPageAsync(
                new LocalRetainedCsharpCodeSearchPageRequest(_query, 10, _nextCursor),
                cancellationToken).ConfigureAwait(false);
            Results = Merge(Results, page.Results);
            _nextCursor = page.NextCursor;
            Error = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Error = "Retained C# fact search could not be completed.";
        }
    }

    private static IReadOnlyList<LocalRetainedCsharpCodeSearchProjection> Merge(
        IReadOnlyList<LocalRetainedCsharpCodeSearchProjection> current,
        IReadOnlyList<LocalRetainedCsharpCodeSearchProjection> next)
    {
        var ordered = current.ToList();
        var indexes = current.Select((value, index) => (value.BranchId, index))
            .ToDictionary(value => value.BranchId, value => value.Item2);
        foreach (var value in next)
        {
            if (!indexes.TryGetValue(value.BranchId, out var index))
            {
                indexes.Add(value.BranchId, ordered.Count);
                ordered.Add(value);
                continue;
            }

            var existing = ordered[index];
            ordered[index] = value with
            {
                Symbols = [.. existing.Symbols, .. value.Symbols],
                References = [.. existing.References, .. value.References]
            };
        }

        return ordered;
    }
}
