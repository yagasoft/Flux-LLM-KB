using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Cli.Commands;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class VisioScopedPipelineIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Dashboard_prepares_visio_then_the_desktop_handoff_refuses_a_changed_root_configuration()
    {
        var path = Path.Combine(Path.GetTempPath(), "visio-guided-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var bytes = new byte[] { 1, 2, 3, 4 };
            await File.WriteAllBytesAsync(Path.Combine(path, "public.vsdx"), bytes);
            var observations = new WindowsSourceObservationReader();
            SourceFileObservation observed;
            var rootConfig = SourceRootConfiguration.Create(path, "Guided Visio", recursive: true, followLinks: false, maximumFileBytes: 1024);
            using (var file = observations.Open(rootConfig, "public.vsdx")) observed = file.Observation;
            var (request, _) = await SeedAsync(RetainedProcessorBranchState.Blocked, path, observed);
            var factory = SqlTestData.CreateFactory(fixture);
            await using (var metadata = await factory.CreateDbContextAsync())
            {
                using var authority = PhysicalFileIdentity.OpenDirectoryLease(path);
                var root = await metadata.SourceRootConfigurations.SingleAsync();
                root.HealthEvidenceJson = JsonSerializer.Serialize(new { physicalIdentity = new { authority.Identity.IdentityFingerprint } });
                await metadata.SaveChangesAsync();
            }
            var admission = new FixtureSourceAdmission(factory);
            var native = new SqlNativeOperationStore(factory, TimeProvider.System);
            var commands = new RetainedRecoveryNativeCommands(factory, native, new RetainedProcessorOptions(), admission, observations);
            var target = new RetainedRecoveryTarget("visio", request.SourceRevisionId.Value);
            var preview = await commands.PreviewAsync(target, default);
            var receipt = await commands.CommitAsync(target, preview.Confirmation.ConfirmationId, "visio-guided-test", default);
            var waiting = await commands.ReadProgressAsync(target, receipt.OperationId, default);
            var branchId = Assert.IsType<Guid>(waiting!.BranchId);
            var branches = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System, sourceAdmission: admission);
            var claim = Assert.Single(await branches.ClaimAsync("guided-visio-preparation", 1, request.ExpectedProcessorFingerprint, default));
            Assert.Equal(branchId, claim.BranchId);
            var child = DocumentProcessingInput.CreateVisioChild(claim, new(request.SourceRevisionId, bytes, request.ExpectedInputSha256, bytes.Length));
            Assert.True(await branches.CommitAsync(claim, new RetainedProcessorCompletion([child], new string('d', 64)), default));
            Assert.Equal("Waiting for the Visio desktop companion", (await commands.ReadProgressAsync(target, receipt.OperationId, default))?.State);

            var registration = new SqlRetainedTextRegistrationStore(factory, TimeProvider.System, sourceAdmission: admission);
            Assert.NotNull(await registration.RegisterVisioBranchAsync(request, branchId, default));
            await using (var change = await factory.CreateDbContextAsync())
            {
                var root = await change.SourceRootConfigurations.SingleAsync();
                root.ConfigurationRevision++;
                root.ExcludePatternsJson = "[\"*.vsdx\"]";
                await change.SaveChangesAsync();
            }
            Assert.Null(await new SqlOutboxStore(factory, sourceAdmission: admission).ClaimVisioDocumentAsync(request, branchId,
                "interactive-visio-guard", DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.FromMinutes(12), default));
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_command_executes_discovery_branch_without_legacy_relation_and_replay_does_not_extract_again(bool physicalHistory)
    {
        var path = Path.Combine(Path.GetTempPath(), "visio-prepared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var (request, branchId) = await SeedDiscoveredVisioAsync(path);
            var factory = SqlTestData.CreateFactory(fixture);
            if (physicalHistory) await AddPhysicalPredecessorAsync(request, factory);
            var extractor = new PreparedVisioExtractor();
            using var output = new StringWriter();
            Assert.Equal(0, await VisioDocumentCommand.ExecuteAsync(request, factory,
                new PreparedVisioReader(request.ExpectedInputSha256), extractor, output, default, path));
            Assert.Contains("visio-extracted", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, extractor.Calls);
            await using var check = await factory.CreateDbContextAsync();
            Assert.Empty(await check.SourceActivityRelations.ToListAsync());
            Assert.Equal(branchId, (await check.SourceProcessorBranches.SingleAsync()).Id);
            Assert.Single(await check.PipelineRecords.ToListAsync());
            Assert.Equal("Prepared Visio text", (await check.Artifacts.SingleAsync()).SearchText);
            Assert.Equal((int)PublicJobState.Completed,
                await check.Jobs.Where(x => x.Operation == PipelineOperations.ExtractVisio).Select(x => x.PublicState).SingleAsync());
            Assert.Single(await check.Jobs.Where(x => x.Operation == PipelineOperations.NormaliseText).ToListAsync());
            output.GetStringBuilder().Clear();
            Assert.Equal(0, await VisioDocumentCommand.ExecuteAsync(request, factory,
                new PreparedVisioReader(request.ExpectedInputSha256), extractor, output, default, path));
            Assert.Contains("visio-already-extracted", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, extractor.Calls);
            Assert.Single(await check.Artifacts.ToListAsync());
            Assert.Equal(2, await check.Jobs.CountAsync());
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [NativeSqlServerTheory]
    [InlineData("physical", true)]
    [InlineData("archive", false)]
    [InlineData("archive-no-parent", false)]
    [InlineData("derived", false)]
    [InlineData("derived-no-parent", false)]
    [InlineData("suppressed", false)]
    [InlineData("wrong-hash", false)]
    [InlineData("wrong-root", false)]
    [InlineData("wrong-branch", false)]
    public async Task Prepared_delivery_uses_physical_origin_and_preserves_exact_claim_guards(string scenario, bool eligible)
    {
        var path = Path.Combine(Path.GetTempPath(), "visio-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var (request, branchId) = await SeedDiscoveredVisioAsync(path);
            var factory = SqlTestData.CreateFactory(fixture);
            var admission = new FixtureSourceAdmission(factory);
            Assert.NotNull(await new SqlRetainedTextRegistrationStore(factory, TimeProvider.System, sourceAdmission: admission)
                .RegisterVisioBranchAsync(request, branchId, default));
            var previousId = await AddPhysicalPredecessorAsync(request, factory);
            await using (var change = await factory.CreateDbContextAsync())
            {
                // Keep the nested-owner control otherwise eligible under shared source ownership.
                await change.SourceRevisions.Where(x => x.Id == previousId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.SuppressedAtUtc, (DateTimeOffset?)null));
                var owner = change.SourceRevisions.Where(x => x.Id == request.SourceRevisionId.Value);
                if (scenario.StartsWith("archive", StringComparison.Ordinal))
                    await owner.ExecuteUpdateAsync(s => s.SetProperty(x => x.OriginKind, 1));
                if (scenario.StartsWith("derived", StringComparison.Ordinal))
                    await owner.ExecuteUpdateAsync(s => s.SetProperty(x => x.OriginKind, 2));
                if (scenario.EndsWith("no-parent", StringComparison.Ordinal))
                    await owner.ExecuteUpdateAsync(s => s.SetProperty(x => x.ParentSourceRevisionId, (Guid?)null));
                if (scenario == "suppressed")
                    await owner.ExecuteUpdateAsync(s => s.SetProperty(x => x.SuppressedAtUtc, DateTimeOffset.UtcNow));
                if (scenario == "wrong-root")
                {
                    var other = (await SeedAsync(RetainedProcessorBranchState.Blocked)).Request;
                    var otherRoot = await change.SourceRevisions.Where(x => x.Id == other.SourceRevisionId.Value)
                        .Select(x => x.SourceRootId).SingleAsync();
                    await change.SourceRevisions.Where(x => x.ParentSourceRevisionId == request.SourceRevisionId.Value)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceRootId, otherRoot));
                }
            }
            var selected = scenario == "wrong-hash" ? request with { ExpectedInputSha256 = new string('b', 64) } : request;
            var dispatch = await new SqlOutboxStore(factory, sourceAdmission: admission).ClaimVisioDocumentAsync(selected,
                scenario == "wrong-branch" ? Guid.NewGuid() : branchId, "origin-test", DateTimeOffset.UtcNow.AddSeconds(1),
                TimeSpan.FromMinutes(12), default);
            Assert.Equal(eligible, dispatch is not null);
            await using var check = await factory.CreateDbContextAsync();
            Assert.Equal(0, (await check.Jobs.SingleAsync()).AttemptCount);
            Assert.Empty(await check.Artifacts.ToListAsync());
            if (!eligible) Assert.Null((await check.OutboxMessages.SingleAsync()).LeaseOwner);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [NativeSqlServerTheory]
    [InlineData("input-hash")]
    [InlineData("processor-version")]
    [InlineData("foreign-owner")]
    [InlineData("not-completed")]
    public async Task Prepared_command_refuses_a_mismatched_or_unfinished_branch_without_extracting(string mismatch)
    {
        var path = Path.Combine(Path.GetTempPath(), "visio-prepared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var (request, branchId) = await SeedDiscoveredVisioAsync(path);
            var factory = SqlTestData.CreateFactory(fixture);
            await using (var change = await factory.CreateDbContextAsync())
            {
                var branch = change.SourceProcessorBranches.Where(x => x.Id == branchId);
                if (mismatch == "input-hash") await branch.ExecuteUpdateAsync(s => s.SetProperty(x => x.InputSha256, new string('b', 64)));
                if (mismatch == "processor-version") await branch.ExecuteUpdateAsync(s => s.SetProperty(x => x.ProcessorVersion, "phase-6-vsdx-visio-v1"));
                if (mismatch == "not-completed") await branch.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, (int)RetainedProcessorBranchState.Pending));
                if (mismatch == "foreign-owner")
                {
                    var other = (await SeedAsync(RetainedProcessorBranchState.Blocked)).Request;
                    await branch.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceRevisionId, other.SourceRevisionId.Value));
                }
            }
            var extractor = new PreparedVisioExtractor();
            await Assert.ThrowsAsync<RetainedProcessorException>(() => VisioDocumentCommand.ExecuteAsync(request, factory,
                new PreparedVisioReader(request.ExpectedInputSha256), extractor, TextWriter.Null, default, path));
            Assert.Equal(0, extractor.Calls);
            await using var check = await factory.CreateDbContextAsync();
            Assert.Empty(await check.Artifacts.ToListAsync());
            Assert.Empty(await check.PipelineRecords.ToListAsync());
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [NativeSqlServerFact]
    public async Task Prepared_command_checks_availability_before_recovering_an_expired_execution()
    {
        var request = (await SeedAsync(RetainedProcessorBranchState.Blocked)).Request;
        var factory = SqlTestData.CreateFactory(fixture);
        var branchId = await CompleteCurrentPreparationAsync(request, factory);
        var admission = new FixtureSourceAdmission(factory);
        await new SqlRetainedTextRegistrationStore(factory, TimeProvider.System, sourceAdmission: admission)
            .RegisterVisioBranchAsync(request, branchId, default);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var dispatch = (await new SqlOutboxStore(factory, sourceAdmission: admission)
            .ClaimVisioDocumentAsync(request, branchId, "prepared-test", now, TimeSpan.FromMinutes(12), default))!;
        Assert.NotNull(await new SqlJobClaimStore(factory, sourceAdmission: admission)
            .ClaimForDispatchAsync(dispatch, "prepared-test", now, TimeSpan.FromMinutes(12), default));
        await using (var change = await factory.CreateDbContextAsync())
        {
            await change.SourceRootConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, (int)SourceRootState.Deleting));
            await change.Jobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
            await change.OutboxMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => VisioDocumentCommand.ExecuteAsync(request,
            factory, new PreparedVisioReader(request.ExpectedInputSha256), new UnavailableVisioExtractor(), TextWriter.Null, default));
        Assert.Equal("visio-unavailable-for-test", error.OutcomeCode);
        await using var check = await factory.CreateDbContextAsync();
        Assert.Equal((int)PublicJobState.WorkerProcessing, (await check.Jobs.SingleAsync()).PublicState);
        Assert.Equal("prepared-test", (await check.Jobs.SingleAsync()).LeaseOwner);
        Assert.Null((await check.OutboxMessages.SingleAsync()).DispatchedAtUtc);
    }

    private async Task<(DocumentReprocessRequest Request, Guid BranchId)> SeedDiscoveredVisioAsync(string path)
    {
        var (request, predecessor) = await SeedAsync(RetainedProcessorBranchState.Blocked, path);
        var factory = SqlTestData.CreateFactory(fixture);
        var branchId = await CompleteCurrentPreparationAsync(request, factory);
        await File.WriteAllBytesAsync(Path.Combine(path, "public.vsdx"), [1, 2, 3, 4]);
        using var authority = PhysicalFileIdentity.OpenDirectoryLease(path);
        await using var db = await factory.CreateDbContextAsync();
        var owner = await db.SourceRevisions.SingleAsync(x => x.Id == request.SourceRevisionId.Value);
        var root = await db.SourceRootConfigurations.SingleAsync(x => x.Id == owner.SourceRootId);
        root.HealthEvidenceJson = JsonSerializer.Serialize(new { physicalIdentity = new { authority.Identity.IdentityFingerprint } });
        // Ordinary discovery has no terminal structural predecessor or legacy supersession relation.
        await db.SourceActivityRelations.ExecuteDeleteAsync();
        var previous = await db.SourceProcessorBranches.SingleAsync(x => x.Id == predecessor);
        var previousActivity = previous.SourceActivityId;
        db.SourceProcessorBranches.Remove(previous);
        db.SourceActivities.Remove(await db.SourceActivities.SingleAsync(x => x.Id == previousActivity));
        await db.SaveChangesAsync();
        return (request, branchId);
    }

    private sealed class PreparedVisioReader(string hash) : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RetainedSourceBytes(id, [1, 2, 3, 4], hash, 4));
        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId id, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Visio must use the retained byte input.");
    }

    private static async Task<Guid> AddPhysicalPredecessorAsync(DocumentReprocessRequest request,
        IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var owner = await db.SourceRevisions.AsNoTracking().SingleAsync(x => x.Id == request.SourceRevisionId.Value);
        await db.SourceRevisions.Where(x => x.Id == owner.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, 2L));
        var previous = new SourceRevisionEntity
        {
            Id = Guid.NewGuid(), SourceRootId = owner.SourceRootId, StableSourceIdentity = owner.StableSourceIdentity,
            Revision = 1, ContentSha256 = new string('b', 64), CanonicalPath = owner.CanonicalPath,
            Classification = owner.Classification, Extension = owner.Extension, ByteLength = 3,
            DiscoveredAtUtc = owner.DiscoveredAtUtc.AddMinutes(-1), SuppressedAtUtc = DateTimeOffset.UtcNow
        };
        db.SourceRevisions.Add(previous);
        await db.SaveChangesAsync();
        await db.SourceRevisions.Where(x => x.Id == owner.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ParentSourceRevisionId, previous.Id));
        return previous.Id;
    }

    private sealed class PreparedVisioExtractor : IVisioDocumentExtractor
    {
        public int Calls { get; private set; }
        public string? GetUnavailableReason() => null;
        public ValueTask<VisioDocumentResult> ExtractAsync(RetainedSourceBytes retained, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new VisioDocumentResult(
                new DocumentExtractionResult("Prepared Visio text", true, []), "{}", 1, 0, 1, 1));
        }
    }
}
