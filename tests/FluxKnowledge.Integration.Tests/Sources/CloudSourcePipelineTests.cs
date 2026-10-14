using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Search;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.TestSupport;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

[Collection("sql-full-text")]
public sealed class CloudSourcePipelineTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Cloud_sample_previews_registers_publishes_and_reads_back_without_ingesting_unavailable_sibling()
    {
        using var cloud = new CloudSourceFixture();
        var directory = cloud.Directory("documents");
        cloud.File("documents/available.txt", "Cloud telescope evidence remains locally available.");
        cloud.File("documents/offline.txt", "Unavailable private sibling.", dehydrate: true);
        cloud.MakeCloudDirectory(directory);
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrelated baseline");
        var policy = new SourceRootPathPolicy(new LocalIngressOptions([directory]));
        var enumerator = new LocalSourceEnumerator();
        var previewReader = new SourceRootProjectionReader(environment.Factory, policy,
            enumerator, new LocalSourceCapabilityHandlerRegistry([]));
        var preview = await previewReader.PreviewAsync(new(directory, "Cloud sample", false, [], [], 1024, "test"), default);
        Assert.Equal(1, preview.PlannedInProcessCount);
        var unavailable = await Assert.ThrowsAnyAsync<IOException>(() => new Utf8FileSourceReader(new LocalIngressOptions([directory]))
            .ReadAsync(Path.Combine(directory, "offline.txt"), default).AsTask());
        Assert.True(unavailable is SourceContentUnavailableException, unavailable.ToString());
        Assert.True(preview.UnavailableFileCount == 1, JsonSerializer.Serialize(enumerator.LastEvidence));
        Assert.True(preview.IsPartial);
        await using (var before = environment.Factory.CreateDbContext()) Assert.Empty(await before.SourceRootConfigurations.ToListAsync());

        var commands = new NativeCorpusCommandService(
            new NativeOperationService(new SqlNativeOperationStore(environment.Factory, TimeProvider.System), []),
            new SqlNativeCorpusActionStore(environment.Factory, policy, new LocalPrivateContentDisclosure()));
        var mutation = new NativeCorpusMutation("root_create", JsonSerializer.SerializeToElement(new
        {
            path = directory, displayName = "Cloud sample", recursive = false, maximumFileBytes = 1024,
            includePatterns = new[] { "available.txt", "offline.txt" }
        }));
        var admission = await commands.PreviewAsync(mutation, "test", default);
        await commands.CommitAsync(mutation, admission.ConfirmationId, $"cloud:{Guid.NewGuid():N}", "test", default);
        var scans = new SqlSourceScanStore(environment.Factory, TimeProvider.System);
        var claim = Assert.IsType<ClaimedSourceScan>(await scans.ClaimNextReleasedAsync(
            "cloud-test", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), default));
        using var artifacts = new ContentAddressedSourceArtifactStore(environment.ArtifactRoot);
        var worker = new SourceScanWorker(new LocalSourceEnumerator(), scans, artifacts,
            new SqlSourceActivityStore(environment.Factory, TimeProvider.System),
            new RetainedTextActivityPlanner(new SqlRetainedTextRegistrationStore(environment.Factory, TimeProvider.System)));
        var scanResult = await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default);
        await scans.CompleteAsync(claim, scanResult, null, default);
        await environment.PumpAsync();

        var retrieval = new CorpusRetrievalService(new SqlCorpusRetrievalReader(environment.Factory),
            new ScopedCorpusRetrievalTests.TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        var hit = Assert.Single((await retrieval.SearchAsync(new CorpusSearchRequest(
            "telescope", 5, "root", claim.SourceRoot.Id.Value, null), default)).Results);
        var read = await retrieval.ReadAsync(new CorpusReadRequest(hit.EvidenceRef, 1024), default);
        Assert.Contains("telescope evidence remains locally available", read.Text);
        await using var verification = environment.Factory.CreateDbContext();
        var revision = Assert.Single(await verification.SourceRevisions.Where(value => value.SourceRootId == claim.SourceRoot.Id.Value).ToListAsync());
        Assert.EndsWith("available.txt", revision.CanonicalPath, StringComparison.OrdinalIgnoreCase);
        Assert.Null(revision.SuppressedAtUtc);
        var scan = await verification.SourceScanRequests.SingleAsync(value => value.Id == claim.ScanRequest.Id.Value);
        Assert.True(scan.ErrorFileCount >= 1);
    }
}
