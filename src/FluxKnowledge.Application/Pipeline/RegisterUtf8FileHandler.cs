using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Application.Pipeline;

public sealed record Utf8FileSource(
    string CanonicalPath,
    byte[] ExactBytes,
    string Text,
    string ContentHash);

public sealed record Utf8FileRegistration(
    string CanonicalPath,
    string ContentHash,
    string RequestedBy,
    string? SourceLabel);

public sealed record RegistrationReceipt(
    PipelineRecordId PipelineRecordId,
    JobId InitialJobId,
    DispatchMessageId InitialDispatchMessageId,
    long Revision,
    string ContentHash,
    PipelineRecordId RootLineageRecordId,
    PipelineRecordId? ParentRevisionRecordId,
    bool ExistingReceipt);

public interface IUtf8FileSourceReader
{
    ValueTask<Utf8FileSource> ReadAsync(
        string suppliedPath,
        CancellationToken cancellationToken);
}

public interface IRegistrationStore
{
    ValueTask<RegistrationReceipt> RegisterAsync(
        Utf8FileRegistration registration,
        CancellationToken cancellationToken);
}

public sealed class RegisterUtf8FileHandler(
    IUtf8FileSourceReader sourceReader,
    IRegistrationStore registrationStore,
    IOutboxWakeSignal? wakeSignal = null)
{
    public async ValueTask<RegisterUtf8FileResult> HandleAsync(
        RegisterUtf8FileCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.FullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RequestedBy);

        var source = await sourceReader.ReadAsync(command.FullPath, cancellationToken)
            .ConfigureAwait(false);
        var receipt = await registrationStore.RegisterAsync(
                new Utf8FileRegistration(
                    source.CanonicalPath,
                    source.ContentHash,
                    command.RequestedBy,
                    command.SourceLabel),
                cancellationToken)
            .ConfigureAwait(false);
        if (!receipt.ExistingReceipt)
        {
            wakeSignal?.Notify();
        }

        return new RegisterUtf8FileResult(
            receipt.PipelineRecordId,
            receipt.InitialJobId,
            receipt.InitialDispatchMessageId,
            receipt.ExistingReceipt);
    }
}
