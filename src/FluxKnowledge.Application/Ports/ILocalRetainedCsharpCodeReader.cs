using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;

namespace FluxKnowledge.Application.Ports;

/// <summary>Reads retained C# facts through named trusted-local contracts only.</summary>
public interface ILocalRetainedCsharpCodeReader
{
    ValueTask<LocalRetainedCsharpCodeDetailProjection?> ReadAsync(
        Guid branchId,
        CancellationToken cancellationToken);

    /// <summary>Reads one explicitly continued, bounded fact page from a verified retained C# branch.</summary>
    ValueTask<LocalRetainedCsharpCodeDetailProjection?> ReadPageAsync(
        Guid branchId,
        LocalRetainedCsharpCodePageRequest pageRequest,
        CancellationToken cancellationToken) => ReadAsync(branchId, cancellationToken);

    ValueTask<IReadOnlyList<LocalRetainedCsharpCodeSearchProjection>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Searches an explicitly continued, bounded page of actual matching durable fact rows.</summary>
    async ValueTask<LocalRetainedCsharpCodeSearchPage> SearchPageAsync(
        LocalRetainedCsharpCodeSearchPageRequest pageRequest,
        CancellationToken cancellationToken) =>
        new(await SearchAsync(pageRequest.Query, pageRequest.Limit, cancellationToken).ConfigureAwait(false), null);

    ValueTask<LocalDisclosureResult> ReadExcerptAsync(
        Guid branchId,
        CancellationToken cancellationToken);
}
