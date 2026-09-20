using System.Text.Json;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Cli.Commands;

/// <summary>Trusted-local, exact-binding document reprocessing. It never enumerates a source.</summary>
public static class DocumentReprocessCommand
{
    public static async Task<int> ExecuteFromEnvironmentAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var lease = ExecutorLease.CreateFromEnvironment();
            return await ExecuteAsync(args, lease.Executor, output, error, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or SqlException or InvalidDataException)
        {
            await error.WriteLineAsync("Trusted-local document reprocessing is unavailable.").ConfigureAwait(false);
            return 1;
        }
    }

    internal static async Task<int> ExecuteAsync(
        string[] args,
        IDocumentReprocessExecutor executor,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (!TryReadRequest(args, out var request))
        {
            await WriteUsageAsync(error).ConfigureAwait(false);
            return 2;
        }

        var result = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.BranchId is null)
        {
            await error.WriteLineAsync("The exact document binding is not eligible for reprocessing.").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            result.BranchId,
            result.Created,
            result.WasReplay,
            result.Claimed,
            result.Completed,
            result.OutcomeCode
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web))).ConfigureAwait(false);
        return result.Completed || !result.Claimed ? 0 : 1;
    }

    private static bool TryReadRequest(string[] args, out DocumentReprocessRequest request)
    {
        request = default!;
        if (args.Length != 7 || args[0] != "reprocess" || args[1] != "--source-revision" ||
            args[3] != "--expected-input-sha256" || args[5] != "--expected-processor-fingerprint" ||
            !Guid.TryParse(args[2], out var revisionId) || !IsCanonicalSha256(args[4]) ||
            !IsDocumentProcessorFingerprint(args[6]))
        {
            return false;
        }

        request = new DocumentReprocessRequest(new SourceRevisionId(revisionId), args[4], args[6]);
        return true;
    }

    private static bool IsDocumentProcessorFingerprint(string fingerprint) =>
        string.Equals(fingerprint, VsdxStructuralTextProcessor.Capability.ProcessorFingerprint, StringComparison.Ordinal) ||
        string.Equals(fingerprint, PdfDocumentProcessor.Capability.ProcessorFingerprint, StringComparison.Ordinal);

    private static bool IsCanonicalSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task WriteUsageAsync(TextWriter error) =>
        await error.WriteLineAsync(
            "Usage: FluxKnowledge.Cli documents reprocess --source-revision <guid> --expected-input-sha256 <lowercase-sha256> --expected-processor-fingerprint <vsdx-or-pdf-fingerprint>").ConfigureAwait(false);

    private sealed class ExecutorLease(IDocumentReprocessExecutor executor, SqlRetainedSourceReader sourceReader) : IDisposable
    {
        public IDocumentReprocessExecutor Executor { get; } = executor;

        public static ExecutorLease CreateFromEnvironment()
        {
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__FluxKnowledge");
            var configuredArtifactRoot = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_SOURCE_ARTIFACT_ROOT");
            _ = LiveRootLayout.RequireExactProductionPathOverride(
                configuredArtifactRoot,
                LiveRootLayout.Production.RetainedRoot,
                "FLUXKNOWLEDGE_SOURCE_ARTIFACT_ROOT");
            if (string.IsNullOrWhiteSpace(connectionString) || !Directory.Exists(LiveRootLayout.Production.RetainedRoot))
            {
                throw new InvalidOperationException("Trusted-local document reprocess configuration is unavailable.");
            }

            var factory = new CliDbContextFactory(connectionString);
            var sourceReader = new SqlRetainedSourceReader(factory, LiveRootLayout.Production.RetainedRoot);
            var artifactWriter = new SqlRetainedArtifactWriter(factory, LiveRootLayout.Production.RetainedRoot);
            var branchStore = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);
            var capabilityService = new SourceCapabilityService(
                new SqlSourceActivityStore(factory, TimeProvider.System),
                new LocalSourceCapabilityHandlerRegistry(
                [
                    new VsdxStructuralTextCapabilityHandler(),
                    new PdfDocumentCapabilityHandler()
                ]));
            return new ExecutorLease(
                new LocalDocumentReprocessExecutor(
                    branchStore,
                    sourceReader,
                    capabilityService,
                    new VsdxStructuralTextProcessor(artifactWriter),
                    new PdfDocumentProcessor(artifactWriter)),
                sourceReader);
        }

        public void Dispose() => sourceReader.Dispose();
    }

    private sealed class CliDbContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);

        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

internal interface IDocumentReprocessExecutor
{
    ValueTask<DocumentReprocessCommandResult> ExecuteAsync(
        DocumentReprocessRequest request,
        CancellationToken cancellationToken);
}

internal sealed record DocumentReprocessCommandResult(
    Guid? BranchId,
    bool Created,
    bool WasReplay,
    bool Claimed,
    bool Completed,
    string OutcomeCode);

internal sealed class LocalDocumentReprocessExecutor(
    IRetainedProcessorBranchStore branchStore,
    IRetainedSourceReader sourceReader,
    SourceCapabilityService capabilityService,
    VsdxStructuralTextProcessor vsdxProcessor,
    PdfDocumentProcessor pdfProcessor,
    VisioDocumentInputProcessor? visioProcessor = null) : IDocumentReprocessExecutor
{
    public async ValueTask<DocumentReprocessCommandResult> ExecuteAsync(
        DocumentReprocessRequest request,
        CancellationToken cancellationToken)
    {
        var descriptor = visioProcessor is not null && request.ExpectedProcessorFingerprint == VisioDocumentInputProcessor.Capability.ProcessorFingerprint
            ? VisioDocumentInputProcessor.Capability
            : string.Equals(request.ExpectedProcessorFingerprint, VsdxStructuralTextProcessor.Capability.ProcessorFingerprint, StringComparison.Ordinal)
            ? VsdxStructuralTextProcessor.Capability
            : PdfDocumentProcessor.Capability;
        var registration = await capabilityService.RegisterAsync(descriptor, cancellationToken).ConfigureAwait(false);
        if (!registration.IsRunnable)
        {
            return new DocumentReprocessCommandResult(null, false, false, false, false, "document-processor-unavailable");
        }

        var requestResult = await branchStore.RequestDocumentReprocessAsync(request, cancellationToken).ConfigureAwait(false);
        if (!requestResult.Accepted || requestResult.SuccessorBranchId is null)
        {
            return new DocumentReprocessCommandResult(null, false, false, false, false, "document-reprocess-not-eligible");
        }

        var owner = $"document-reprocess:{Environment.ProcessId}:{Guid.NewGuid():N}";
        var claim = await branchStore.ClaimDocumentBranchAsync(
            requestResult.SuccessorBranchId.Value,
            request.SourceRevisionId,
            request.ExpectedInputSha256,
            request.ExpectedProcessorFingerprint,
            owner,
            cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return new DocumentReprocessCommandResult(
                requestResult.SuccessorBranchId,
                requestResult.Created,
                requestResult.WasReplay,
                Claimed: false,
                Completed: false,
                OutcomeCode: "document-reprocess-not-claimed");
        }

        try
        {
            var inspection = await sourceReader.InspectAsync(claim.SourceRevisionId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(inspection.ContentSha256, claim.InputSha256, StringComparison.Ordinal))
            {
                throw new RetainedProcessorException("retained-artifact-checksum-invalid");
            }

            var retained = await sourceReader.ReadBytesAsync(claim.SourceRevisionId, cancellationToken).ConfigureAwait(false);
            var completion = descriptor.Id == VisioDocumentInputProcessor.Capability.Id
                ? await visioProcessor!.ProcessAsync(claim, retained, cancellationToken).ConfigureAwait(false)
                : descriptor.Id == VsdxStructuralTextProcessor.Capability.Id
                ? await vsdxProcessor.ProcessAsync(claim, retained, new RetainedProcessorOptions(), cancellationToken).ConfigureAwait(false)
                : await pdfProcessor.ProcessAsync(claim, retained, new RetainedProcessorOptions(), cancellationToken).ConfigureAwait(false);
            try
            {
                var completed = await branchStore.CommitAsync(claim, completion, cancellationToken).ConfigureAwait(false);
                return new DocumentReprocessCommandResult(
                    claim.BranchId,
                    requestResult.Created,
                    requestResult.WasReplay,
                    Claimed: true,
                    Completed: completed,
                    OutcomeCode: completed ? "completed" : "document-reprocess-completion-fence-lost");
            }
            finally
            {
                foreach (var publicationLease in completion.Members
                             .Select(member => member.PublicationLease)
                             .OfType<ISourceArtifactPublicationLease>()
                             .Distinct())
                {
                    await publicationLease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await branchStore.RetryAsync(claim, "processor-cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (RetainedProcessorException exception)
        {
            var failed = await branchStore.FailAsync(claim,
                new RetainedProcessorFailure(exception.OutcomeCode, exception.MemberOutcomes), cancellationToken).ConfigureAwait(false);
            return new DocumentReprocessCommandResult(claim.BranchId, requestResult.Created, requestResult.WasReplay, true, false,
                failed ? exception.OutcomeCode : "document-reprocess-completion-fence-lost");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            var failed = await branchStore.FailAsync(claim, new RetainedProcessorFailure("retained-artifact-missing", []), cancellationToken).ConfigureAwait(false);
            return new DocumentReprocessCommandResult(claim.BranchId, requestResult.Created, requestResult.WasReplay, true, false,
                failed ? "retained-artifact-missing" : "document-reprocess-completion-fence-lost");
        }
        catch (Exception exception) when (exception is InvalidDataException or UnauthorizedAccessException)
        {
            var code = exception is InvalidDataException ? "retained-artifact-checksum-invalid" : "retained-artifact-path-invalid";
            var failed = await branchStore.FailAsync(claim, new RetainedProcessorFailure(code, []), cancellationToken).ConfigureAwait(false);
            return new DocumentReprocessCommandResult(claim.BranchId, requestResult.Created, requestResult.WasReplay, true, false,
                failed ? code : "document-reprocess-completion-fence-lost");
        }
        catch (IOException)
        {
            var retried = await branchStore.RetryAsync(claim, "retained-artifact-transient", cancellationToken).ConfigureAwait(false);
            return new DocumentReprocessCommandResult(claim.BranchId, requestResult.Created, requestResult.WasReplay, true, false,
                retried ? "retained-artifact-transient" : "document-reprocess-completion-fence-lost");
        }
    }
}
