using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Application.Contracts;

public sealed record RegisterUtf8FileCommand(
    string FullPath,
    string RequestedBy,
    string? SourceLabel);

public sealed record RegisterUtf8FileResult(
    PipelineRecordId PipelineRecordId,
    JobId InitialJobId,
    DispatchMessageId InitialDispatchMessageId,
    bool ExistingReceipt);
