using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Documents;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>Executes one exactly identified VSDX through the interactive Visio pipeline.</summary>
public static class VisioDocumentPipelineRunner
{
    internal static async ValueTask<DocumentReprocessRequest?> ReadNextRequestAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var baseCandidates =
            from owner in context.SourceRevisions.AsNoTracking()
            join root in context.SourceRootConfigurations.AsNoTracking() on owner.SourceRootId equals root.Id
            join branch in context.SourceProcessorBranches.AsNoTracking() on owner.Id equals branch.SourceRevisionId
            where owner.OriginKind == 0 && owner.Extension == ".vsdx" &&
                  branch.InputSha256 == owner.ContentSha256 &&
                  branch.ProcessorFingerprint == VisioDocumentInputProcessor.Capability.ProcessorFingerprint &&
                  branch.ProcessorVersion == DocumentProcessingInput.Visio.ParentProcessorVersion &&
                  branch.State == (int)RetainedProcessorBranchState.Completed &&
                  !context.DocumentPublications.Any(publication => publication.OwnerSourceRevisionId == owner.Id)
            select new { Owner = owner, Root = root, Branch = branch };

        var recovery = await baseCandidates
            .Where(value => (value.Root.State != (int)SourceRootState.Enabled || value.Owner.SuppressedAtUtc != null) &&
                (from member in context.SourceProcessorBranchMembers
                 join input in context.SourceRevisions on member.ChildSourceRevisionId equals input.Id
                 join record in context.PipelineRecords on input.Id equals record.SourceRevisionId
                 join job in context.Jobs on record.Id equals job.PipelineRecordId
                 join dispatch in context.OutboxMessages on record.Id equals dispatch.PipelineRecordId
                 where member.BranchId == value.Branch.Id && job.Operation == PipelineOperations.ExtractVisio &&
                       job.PublicState == (int)PublicJobState.WorkerProcessing && job.LeaseOwner != null &&
                       job.LeaseExpiresAtUtc < now && dispatch.Operation == PipelineOperations.ExtractVisio &&
                       dispatch.DispatchedAtUtc == null && dispatch.LeaseOwner != null && dispatch.LeaseExpiresAtUtc < now
                 select job.Id).Any())
            .OrderBy(value => value.Owner.DiscoveredAtUtc).ThenBy(value => value.Owner.Id)
            .Select(value => new { value.Owner.Id, value.Owner.ContentSha256 })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (recovery is not null)
        {
            return new DocumentReprocessRequest(new SourceRevisionId(recovery.Id), recovery.ContentSha256,
                VisioDocumentInputProcessor.Capability.ProcessorFingerprint);
        }

        var candidate = await baseCandidates
            .Where(value => value.Root.State == (int)SourceRootState.Enabled && value.Owner.SuppressedAtUtc == null &&
                (!(from member in context.SourceProcessorBranchMembers
                   join record in context.PipelineRecords on member.ChildSourceRevisionId equals record.SourceRevisionId
                   where member.BranchId == value.Branch.Id
                   select record.Id).Any() ||
                 (from member in context.SourceProcessorBranchMembers
                  join record in context.PipelineRecords on member.ChildSourceRevisionId equals record.SourceRevisionId
                  join job in context.Jobs on record.Id equals job.PipelineRecordId
                  join dispatch in context.OutboxMessages on record.Id equals dispatch.PipelineRecordId
                  where member.BranchId == value.Branch.Id && job.Operation == PipelineOperations.ExtractVisio &&
                        job.PublicState != (int)PublicJobState.Failed && job.PublicState != (int)PublicJobState.Completed &&
                        job.DueAtUtc <= now && (job.LeaseExpiresAtUtc == null || job.LeaseExpiresAtUtc <= now) &&
                        dispatch.Operation == PipelineOperations.ExtractVisio && dispatch.DispatchedAtUtc == null &&
                        dispatch.DueAtUtc <= now && (dispatch.LeaseExpiresAtUtc == null || dispatch.LeaseExpiresAtUtc <= now)
                  select job.Id).Any()))
            .OrderBy(value => value.Owner.DiscoveredAtUtc).ThenBy(value => value.Owner.Id)
            .Select(value => new { value.Owner.Id, value.Owner.ContentSha256 })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return candidate is null ? null : new DocumentReprocessRequest(
            new SourceRevisionId(candidate.Id), candidate.ContentSha256,
            VisioDocumentInputProcessor.Capability.ProcessorFingerprint);
    }

    public static async Task<int> ExecuteAsync(
        DocumentReprocessRequest request,
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        IRetainedSourceReader sourceReader,
        IVisioDocumentExtractor extractor,
        TextWriter output,
        CancellationToken cancellationToken,
        string retainedRoot)
    {
        var transitions = new SqlStageTransitionStore(factory);
        if (await RecoverDrainingExecutionAsync(request, factory, transitions, cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync("{\"outcomeCode\":\"visio-expired-execution-cleaned\"}").ConfigureAwait(false);
            return 0;
        }
        if (extractor.GetUnavailableReason() is { } reason) throw new RetainedProcessorException(reason);
        await using var branchContext = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var branchId = await branchContext.SourceProcessorBranches.AsNoTracking()
            .Where(branch => branch.SourceRevisionId == request.SourceRevisionId.Value &&
                branch.InputSha256 == request.ExpectedInputSha256 &&
                branch.ProcessorFingerprint == request.ExpectedProcessorFingerprint &&
                branch.ProcessorVersion == DocumentProcessingInput.Visio.ParentProcessorVersion &&
                branch.State == (int)RetainedProcessorBranchState.Completed)
            .Select(branch => (Guid?)branch.Id)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (branchId is null) throw new RetainedProcessorException("visio-preparation-pending");
        var recordId = await new SqlRetainedTextRegistrationStore(factory, TimeProvider.System, retainedRoot)
            .RegisterVisioBranchAsync(request, branchId.Value, cancellationToken).ConfigureAwait(false);
        if (recordId is null) throw new RetainedProcessorException("visio-registration-unavailable");
        var owner = $"visio-desktop:{Environment.ProcessId}:{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var outbox = new SqlOutboxStore(factory);
        var dispatch = await outbox.ClaimVisioDocumentAsync(request, branchId.Value, owner, now, TimeSpan.FromMinutes(12), cancellationToken).ConfigureAwait(false);
        if (dispatch is null)
        {
            await using var state = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var jobState = await state.Jobs.AsNoTracking().Where(j => j.PipelineRecordId == recordId && j.Operation == PipelineOperations.ExtractVisio)
                .Select(j => new { j.PublicState, j.Reason }).SingleAsync(cancellationToken).ConfigureAwait(false);
            var complete = jobState.PublicState == (int)PublicJobState.Completed;
            await output.WriteLineAsync(JsonSerializer.Serialize(new { branchId, pipelineRecordId = recordId,
                outcomeCode = complete ? "visio-already-extracted" : jobState.Reason ?? "visio-not-claimed" })).ConfigureAwait(false);
            return complete ? 0 : 1;
        }
        var job = await new SqlJobClaimStore(factory).ClaimForDispatchAsync(dispatch, owner, now, TimeSpan.FromMinutes(12), cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            await outbox.ReleaseAsync(dispatch, now, CancellationToken.None).ConfigureAwait(false);
            throw new RetainedProcessorException("visio-job-not-claimed");
        }
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var monitorStop = new CancellationTokenSource();
        var monitor = MonitorSourceAsync(factory, request, job, run, monitorStop.Token);
        var worker = new ExtractVisioStageWorker(sourceReader, new SqlPipelineStore(factory), extractor, transitions, TimeProvider.System);
        try { await worker.ExecuteAsync(new StageWorkItem(dispatch, job), run.Token).ConfigureAwait(false); }
        finally
        {
            await monitorStop.CancelAsync().ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
        }
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            branchId, pipelineRecordId = recordId, worker.OutcomeCode,
            pageCount = worker.Result?.Extraction.Pages.Count, worker.Result?.ShapeCount,
            worker.Result?.ConnectionCount, worker.Result?.ElapsedMilliseconds, worker.Result?.PrivateBytes
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web))).ConfigureAwait(false);
        return worker.Result is not null ? 0 : 1;
    }

    private static async Task MonitorSourceAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        DocumentReprocessRequest request,
        ClaimedJob job,
        CancellationTokenSource run,
        CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await using var context = await factory.CreateDbContextAsync(stop).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                var eligible = await (from owner in context.SourceRevisions
                                      join root in context.SourceRootConfigurations on owner.SourceRootId equals root.Id
                                      where owner.Id == request.SourceRevisionId.Value && owner.ContentSha256 == request.ExpectedInputSha256 &&
                                            owner.SuppressedAtUtc == null && root.State == (int)SourceRootState.Enabled
                                      select owner.Id).AnyAsync(stop).ConfigureAwait(false);
                eligible &= await context.Jobs.AnyAsync(j => j.Id == job.JobId.Value && j.LeaseOwner == job.LeaseOwner &&
                    j.LeaseGeneration == job.LeaseGeneration && j.LeaseExpiresAtUtc > now && j.PublicState == (int)PublicJobState.WorkerProcessing, stop).ConfigureAwait(false);
                if (!eligible) { await run.CancelAsync().ConfigureAwait(false); return; }
                await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            await run.CancelAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> RecoverDrainingExecutionAsync(
        DocumentReprocessRequest request,
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        SqlStageTransitionStore transitions,
        CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var candidate = await (from owner in context.SourceRevisions
                               join root in context.SourceRootConfigurations on owner.SourceRootId equals root.Id
                               join branch in context.SourceProcessorBranches on owner.Id equals branch.SourceRevisionId
                               join member in context.SourceProcessorBranchMembers on branch.Id equals member.BranchId
                               join input in context.SourceRevisions on member.ChildSourceRevisionId equals input.Id
                               join record in context.PipelineRecords on input.Id equals record.SourceRevisionId
                               join job in context.Jobs on record.Id equals job.PipelineRecordId
                               join dispatch in context.OutboxMessages on record.Id equals dispatch.PipelineRecordId
                               where owner.Id == request.SourceRevisionId.Value && owner.ContentSha256 == request.ExpectedInputSha256 &&
                                     (root.State != (int)SourceRootState.Enabled || owner.SuppressedAtUtc != null) &&
                                     branch.InputSha256 == request.ExpectedInputSha256 &&
                                     branch.ProcessorFingerprint == request.ExpectedProcessorFingerprint &&
                                     branch.ProcessorVersion == DocumentProcessingInput.Visio.ParentProcessorVersion &&
                                     input.Classification == DocumentProcessingInput.VisioClassification && input.ParentSourceRevisionId == owner.Id &&
                                     input.SourceRootId == owner.SourceRootId && input.ContentSha256 == request.ExpectedInputSha256 &&
                                     job.Operation == PipelineOperations.ExtractVisio && job.PublicState == (int)PublicJobState.WorkerProcessing &&
                                     job.LeaseOwner != null && job.LeaseExpiresAtUtc < now &&
                                     dispatch.Operation == PipelineOperations.ExtractVisio && dispatch.DispatchedAtUtc == null &&
                                     dispatch.LeaseOwner != null && dispatch.LeaseExpiresAtUtc < now
                               select new { Job = job, Dispatch = dispatch }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (candidate is null) return false;
        var j = candidate.Job;
        var d = candidate.Dispatch;
        await transitions.FailAsync(new StageFailureRequest(
            new ClaimedDispatchMessage(new DispatchMessageId(d.Id), new PipelineRecordId(d.PipelineRecordId), d.SourceRevision,
                (PipelineStage)d.Stage, d.Operation, d.DispatchGeneration, d.IdempotencyKey, d.DueAtUtc, d.LeaseOwner!, d.LeaseExpiresAtUtc!.Value, d.LeaseGeneration),
            new ClaimedJob(new JobId(j.Id), new PipelineRecordId(j.PipelineRecordId), j.SourceRevision, (PipelineStage)j.Stage,
                j.Operation, (PublicJobState)j.PublicState, j.DueAtUtc, j.AttemptCount, j.LeaseOwner!, j.LeaseExpiresAtUtc!.Value, j.LeaseGeneration),
            "visio-expired-execution-cleaned", null, nameof(VisioDocumentPipelineRunner)), cancellationToken).ConfigureAwait(false);
        return true;
    }
}
