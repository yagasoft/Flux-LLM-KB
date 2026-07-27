using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Web.Components.Status;

namespace FluxKnowledge.Web.Endpoints;

public static class PipelineEndpoints
{
    public static IEndpointRouteBuilder MapFluxKnowledgePipelineRecords(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/pipeline-records/utf8-file", RegisterUtf8FileAsync);
        endpoints.MapGet("/api/pipeline-records", GetPipelineRecordsAsync);
        endpoints.MapGet("/api/pipeline-records/{id:guid}", GetPipelineRecordAsync);
        return endpoints;
    }

    private static async Task<IResult> RegisterUtf8FileAsync(
        RegisterUtf8FileCommand command,
        RegisterUtf8FileHandler handler,
        IStatusEventPublisher statusEvents,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        await statusEvents.PublishAsync(
                new StatusChanged(result.PipelineRecordId, "pipeline", DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
        return Results.Accepted($"/api/pipeline-records/{result.PipelineRecordId.Value}", result);
    }

    private static async Task<IResult> GetPipelineRecordsAsync(IProjectionReader reader, CancellationToken cancellationToken) =>
        Results.Ok(await reader.ReadPipelineRecordsAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> GetPipelineRecordAsync(
        Guid id,
        IProjectionReader reader,
        CancellationToken cancellationToken)
    {
        var record = await reader.ReadPipelineRecordAsync(id, cancellationToken).ConfigureAwait(false);
        return record is null ? Results.NotFound() : Results.Ok(record);
    }
}
