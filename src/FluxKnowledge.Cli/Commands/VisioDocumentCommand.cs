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

namespace FluxKnowledge.Cli.Commands;

/// <summary>One exact document, one interactive Visio invocation. No hosted workers, source scans or model providers.</summary>
public static class VisioDocumentCommand
{
    public static async Task<int> ExecuteFromEnvironmentAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (!TryReadRequest(args, out var request))
        {
            await error.WriteLineAsync("Usage: FluxKnowledge.Cli documents run-visio --source-revision <guid> --expected-input-sha256 <lowercase-sha256> --expected-processor-fingerprint phase-6-vsdx-retained-visio-v2").ConfigureAwait(false);
            return 2;
        }
        try
        {
            var extractor = new VisioDocumentExtractor();
            if (extractor.GetUnavailableReason() is { } reason)
            {
                await error.WriteLineAsync(reason).ConfigureAwait(false);
                return 1;
            }
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__FluxKnowledge");
            _ = LiveRootLayout.RequireExactProductionPathOverride(Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_SOURCE_ARTIFACT_ROOT"),
                LiveRootLayout.Production.RetainedRoot, "FLUXKNOWLEDGE_SOURCE_ARTIFACT_ROOT");
            if (string.IsNullOrWhiteSpace(connectionString) || !Directory.Exists(LiveRootLayout.Production.RetainedRoot))
                throw new InvalidOperationException("visio-local-configuration-unavailable");
            using var executionLease = VisioExecutionLease.Acquire(extractor);
            var factory = new ContextFactory(connectionString);
            using var sourceReader = new SqlRetainedSourceReader(factory, LiveRootLayout.Production.RetainedRoot);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                return await ExecuteAsync(request, factory, sourceReader, executionLease, output, stop.Token).ConfigureAwait(false);
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (RetainedProcessorException exception)
        {
            await error.WriteLineAsync(exception.OutcomeCode).ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("visio-cancelled").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or SqlException)
        {
            // COM/SQL exception details and paths can contain private document content or credentials.
            await error.WriteLineAsync("visio-local-execution-unavailable").ConfigureAwait(false);
            return 1;
        }
    }

    internal static bool TryReadRequest(string[] args, out DocumentReprocessRequest request)
    {
        request = default!;
        if (args.Length != 7 || args[0] != "run-visio" || args[1] != "--source-revision" ||
            args[3] != "--expected-input-sha256" || args[5] != "--expected-processor-fingerprint" ||
            !Guid.TryParse(args[2], out var revision) || revision == Guid.Empty || args[4].Length != 64 ||
            args[4].Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')) ||
            args[6] != VisioDocumentInputProcessor.Capability.ProcessorFingerprint) return false;
        request = new DocumentReprocessRequest(new SourceRevisionId(revision), args[4], args[6]);
        return true;
    }

    internal static async Task<int> ExecuteAsync(DocumentReprocessRequest request,
        IDbContextFactory<FluxKnowledgeDbContext> factory, IRetainedSourceReader sourceReader,
        IVisioDocumentExtractor extractor, TextWriter output, CancellationToken cancellationToken,
        string? testRetainedRoot = null)
    {
        // Recheck before any claim or crash recovery. An existing user instance must remain untouched.
        if (extractor.GetUnavailableReason() is { } reason) throw new RetainedProcessorException(reason);
        var transitions = new SqlStageTransitionStore(factory);
        if (await RecoverDrainingExecutionAsync(request, factory, transitions, cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync("{\"outcomeCode\":\"visio-expired-execution-cleaned\"}").ConfigureAwait(false);
            return 0;
        }
        var processor = new VisioDocumentInputProcessor();
        var capabilityService = new SourceCapabilityService(new SqlSourceActivityStore(factory, TimeProvider.System),
            new LocalSourceCapabilityHandlerRegistry([processor]));
        var retainedRoot = testRetainedRoot ?? LiveRootLayout.Production.RetainedRoot;
        var artifactWriter = new SqlRetainedArtifactWriter(factory, retainedRoot);
        var preparation = await new LocalDocumentReprocessExecutor(new SqlRetainedProcessorBranchStore(factory, TimeProvider.System),
            sourceReader, capabilityService, new VsdxStructuralTextProcessor(artifactWriter), new PdfDocumentProcessor(artifactWriter), processor)
            .ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (preparation.BranchId is null) throw new RetainedProcessorException(preparation.OutcomeCode);
        var branchId = preparation.BranchId.Value;
        var recordId = await new SqlRetainedTextRegistrationStore(factory, TimeProvider.System, retainedRoot)
            .RegisterVisioBranchAsync(request, branchId, cancellationToken).ConfigureAwait(false);
        if (recordId is null) throw new RetainedProcessorException(preparation.Completed ? "visio-registration-unavailable" : preparation.OutcomeCode);
        var owner = $"visio-desktop:{Environment.ProcessId}:{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var outbox = new SqlOutboxStore(factory);
        var dispatch = await outbox.ClaimVisioDocumentAsync(request, branchId, owner, now, TimeSpan.FromMinutes(12), cancellationToken).ConfigureAwait(false);
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

    private static async Task MonitorSourceAsync(IDbContextFactory<FluxKnowledgeDbContext> factory,
        DocumentReprocessRequest request, ClaimedJob job, CancellationTokenSource run, CancellationToken stop)
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

    private static async Task<bool> RecoverDrainingExecutionAsync(DocumentReprocessRequest request,
        IDbContextFactory<FluxKnowledgeDbContext> factory, SqlStageTransitionStore transitions, CancellationToken cancellationToken)
    {
        // This is reached only after the no-existing-Visio gate. An expired claim alone is never cleanup evidence.
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
                                     root.State != (int)SourceRootState.Enabled && branch.InputSha256 == request.ExpectedInputSha256 &&
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
            "visio-expired-execution-cleaned", null, nameof(VisioDocumentCommand)), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private sealed class ContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()).Options;
        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
