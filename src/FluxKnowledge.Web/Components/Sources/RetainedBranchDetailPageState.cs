using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;

namespace FluxKnowledge.Web.Components.Sources;

/// <summary>Trusted-local retained branch page state; it does not participate in corpus projections.</summary>
public sealed class RetainedBranchDetailPageState(ILocalRetainedDetailReader reader)
{
    public LocalRetainedDetailProjection? Detail { get; private set; }
    public LocalDisclosureResult? Excerpt { get; private set; }
    public string? Error { get; private set; }

    public async ValueTask LoadAsync(Guid branchId, CancellationToken cancellationToken)
    {
        try
        {
            Detail = await reader.ReadAsync(branchId, cancellationToken).ConfigureAwait(false);
            Excerpt = Detail is null ? null : await reader.ReadExcerptAsync(branchId, cancellationToken).ConfigureAwait(false);
            Error = Detail is null ? "The retained branch was not found." : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Detail = null;
            Excerpt = null;
            Error = "The retained branch detail could not be loaded.";
        }
    }
}
