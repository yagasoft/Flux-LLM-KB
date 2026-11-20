using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Sources;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Documents;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Media;
using FluxKnowledge.Integrations.Windows;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [TeamsSpeakerTrialFact]
    public async Task TeamsSpeakerPipelineTrial_publishes_a_labelled_sidecar_through_the_native_worker()
    {
        const string stageBase = @"I:\Codex Workspaces\Staging\FluxKnowledge";
        var fixturePath = Path.GetFullPath(Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEAMS_TRIAL_MP4")!);
        var fixtureFolder = Path.GetDirectoryName(fixturePath)!;
        Assert.Equal(stageBase, Path.GetDirectoryName(fixtureFolder), ignoreCase: true);
        var layoutForFixture = LiveRootLayout.CreateForIsolatedTests(fixtureFolder);
        Assert.True(layoutForFixture.ValidateOwnedPath(fixturePath, FileSystemLiveRootPathInspector.Instance).IsValid);
        Assert.Equal(".mp4", Path.GetExtension(fixturePath), ignoreCase: true);
        Assert.InRange(new FileInfo(fixturePath).Length, 1024, 16 * 1024 * 1024);
        var expectedName = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEAMS_TRIAL_SPEAKER");
        Assert.False(string.IsNullOrWhiteSpace(expectedName));
        Assert.True(MediaTranscriptionLocalRuntime.IsAvailable());

        await using var scenario = await DeletedSourceRecoveryIntegrationTests.Scenario.CreateAsync(
            _fixture.ConnectionString, maximumFileBytes: 16 * 1024 * 1024, seed: false);
        var layout = LiveRootLayout.CreateForIsolatedTests(scenario.Area);
        var recording = Path.Combine(scenario.Files, "meeting.mp4");
        File.Copy(fixturePath, recording);
        await scenario.ScanFilesystemAsync();
        var factory = scenario.Factory;
        var admission = scenario.Admission;
        await using var db = await factory.CreateDbContextAsync();
        var source = await db.SourceRevisions.AsNoTracking().SingleAsync();
        var discoveryOptions = new MediaTranscriptionDiscoveryOptions(
            [source.SourceRootId], source.DiscoveredAtUtc.AddMinutes(-1), []);
        using var retained = new SqlRetainedSourceReader(factory, scenario.Retained);
        var registration = new SqlMediaTranscriptionRegistrationStore(factory, admission,
            new WindowsSourceObservationReader(), retained, TimeProvider.System);
        Assert.Equal(1, (await registration.RunOnceAsync(discoveryOptions, runtimeReady: true, default)).Queued);

        var runtime = MediaTranscriptionLocalRuntime.Profile;
        db.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
        {
            SlotKey = PaddleOcrVlmRuntimeContract.CapacitySlotKey,
            State = (int)GpuCapacitySlotState.Available,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var scheduler = new SqlGpuSchedulerStore(factory, sourceAdmission: admission);
        var policy = new GpuWorkloadPolicy([
            new(PaddleOcrVlmRuntimeContract.ModelRuntimeKey, PaddleOcrVlmRuntimeContract.SettingsFingerprint, GpuWorkloadKind.Ocr),
            runtime.WorkloadProfile]);
        var gate = new SharedGpuAdmissionGate(policy, "unused-retrieval", runtime.EstimatedBytes, runtime);
        var options = new GpuSchedulerOptions(1, runtime.EstimatedBytes, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(1), policy);
        var gpuWake = new ChannelGpuSchedulerWakeSignal();
        var outboxWake = new ChannelOutboxWakeSignal();
        var events = new NullStatusPublisher();
        var coordinator = new GpuSchedulerCoordinator(scheduler, gate, events, gpuWake, TimeProvider.System, options);
        var native = new MediaTranscriptionLocalExecutor(layout, retained);
        var store = new SqlMediaTranscriptionGpuStore(factory, coordinator, runtime, TimeProvider.System, admission,
            discoveryOptions, native);
        var transitions = new StageTransitionService(new SqlStageTransitionStore(factory, sourceAdmission: admission),
            events, outboxWake, TimeProvider.System);
        var worker = new TranscribeMediaStageWorker(new SqlPipelineStore(factory), store, transitions, TimeProvider.System,
            MediaTranscriptionLocalRuntime.IsAvailable, store);

        await RunStageAsync("speaker-queue");
        var round = await scheduler.RunAdmissionRoundAsync(GpuSchedulerWakeReason.WorkReady, options, gate.DecideAsync, default);
        Assert.Equal(GpuAdmissionDisposition.Admit, round.Disposition);
        var handle = Assert.Single(await scheduler.ReadPendingDispatchesAsync(default));
        var owner = new WindowsInteractiveGpuOwnerProbe();
        using var lifetime = new TestHostApplicationLifetime();
        await using var services = new ServiceCollection().AddSingleton(store).AddSingleton(native).BuildServiceProvider();
        var adapter = new MediaTranscriptionGpuExecutor(services.GetRequiredService<IServiceScopeFactory>(), runtime,
            owner, new DocumentOcrProcessRecovery(owner), outboxWake, gpuWake, TimeProvider.System, lifetime);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await adapter.DeliverAsync(handle, timeout.Token);
        Assert.Empty(await scheduler.ReadPendingDispatchesAsync(default));
        Assert.Empty(await store.ReadRecoveryAsync(default));
        Assert.Equal((int)GpuCapacitySlotState.Available, await db.GpuCapacitySlots.AsNoTracking().Select(s => s.State).SingleAsync());

        await RunStageAsync("speaker-publish");
        var sidecar = recording + MediaTranscriptionGpuContracts.SidecarSuffix;
        Assert.True(File.Exists(sidecar), "The normal Flux stage did not publish the transcript sidecar.");
        var transcript = await File.ReadAllTextAsync(sidecar);
        Assert.True(transcript.Split('\n').Any(line => line.Contains($"] {expectedName}:", StringComparison.Ordinal) ||
            line.Contains($"] {expectedName} or ", StringComparison.Ordinal)), "The reviewed speaker label was missing from the sidecar.");
        var receipt = await db.Artifacts.AsNoTracking().SingleAsync(a =>
            a.ContentType == MediaTranscriptionGpuContracts.SidecarReceiptContentType);
        Assert.Equal("", receipt.SearchText);

        var sidecars = new List<string> { sidecar };
        foreach (var sample in Directory.EnumerateFiles(fixtureFolder, "*.transcript.txt"))
        {
            Assert.True(layoutForFixture.ValidateOwnedPath(sample, FileSystemLiveRootPathInspector.Instance).IsValid);
            Assert.InRange(new FileInfo(sample).Length, 1, 16 * 1024 * 1024);
            var destination = Path.Combine(scenario.Files, Path.GetFileName(sample));
            File.Copy(sample, destination);
            sidecars.Add(destination);
        }

        await scenario.ScanFilesystemAsync();
        var sidecarSources = new List<(string Path, Guid Revision)>();
        foreach (var path in sidecars)
        {
            var sidecarRevision = await db.SourceRevisions.AsNoTracking().SingleAsync(revision => revision.CanonicalPath == path);
            Assert.NotEqual(source.Id, sidecarRevision.Id);
            sidecarSources.Add((path, sidecarRevision.Id));
            var activity = await new SqlSourceActivityStore(factory, TimeProvider.System).FindOrCreateAsync(
                new(new SourceRevisionId(sidecarRevision.Id), SourceActivityKind.TextExtraction, ExecutionClass.InProcess,
                    SourceScanWorker.TextProcessorVersion, sidecarRevision.ContentSha256, null, null), default);
            Assert.True(await new SqlRetainedTextRegistrationStore(factory, TimeProvider.System, scenario.Retained,
                sourceAdmission: admission).RegisterAsync(activity, default));
        }

        var indexing = new ServiceCollection();
        indexing.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(factory);
        indexing.AddSingleton<ISourceWorkAdmission>(admission);
        indexing.AddSingleton<IRetainedSourceReader>(retained);
        indexing.AddSingleton<IUtf8FileSourceReader>(new Utf8FileSourceReader(new LocalIngressOptions([scenario.Files])));
        indexing.AddFluxKnowledgeOutboxWorkers();
        indexing.AddSingleton<IEmbeddingProvider, DeterministicTokenHashEmbeddingProvider>();
        indexing.AddSingleton(new PassageBuilder(new TeamsTrialWordTokenizer()));
        indexing.AddSingleton<FluxKnowledge.Application.Visibility.ILocalPrivateContentDisclosure, LocalPrivateContentDisclosure>();
        indexing.AddSingleton<FluxKnowledge.Application.Visibility.CsharpDisclosureProofBuilder>();
        indexing.AddFluxKnowledgeUsearch(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Usearch:RootPath"] = layout.IndexRoot,
            ["IndexPublication:Mode"] = "Legacy"
        }).Build());
        indexing.AddScoped<IStageWorker, CanonicalIndexStageWorker>();
        indexing.AddScoped<IStageWorker, EmbedStageWorker>();
        indexing.AddScoped<IStageWorker, PublishStageWorker>();
        await using var indexServices = indexing.BuildServiceProvider();
        await indexServices.GetRequiredService<OutboxPumpService>().PumpOnceAsync(timeout.Token);

        await SqlTestData.WaitForFullTextAsync(db);
        var reader = new SqlCorpusRetrievalReader(factory);
        var codec = new CorpusEvidenceCodec(new EphemeralDataProtectionProvider());
        var disclosure = new LocalPrivateContentDisclosure();
        // The native literal path uses no semantic inference or ANN lease.
        var engine = new HybridPassageRetrievalEngine(reader, reader, null!, null!, null!, owner, codec, disclosure);
        var retrieval = new CorpusRetrievalService(reader, codec, disclosure, new(true), engine);
        var searchScope = await reader.ResolveScopeAsync("workspace", null, scenario.Files, timeout.Token);
        Assert.NotNull(searchScope);
        var candidates = await reader.ReadLiteralCandidatesAsync(expectedName!, searchScope, timeout.Token);
        foreach (var (path, sidecarRevision) in sidecarSources)
        {
            var document = candidates.Where(candidate => candidate.OwnerSourceRevisionId == sidecarRevision)
                .Take(12).ToArray();
            Assert.NotEmpty(document);
            CorpusSearchHit? passage = null;
            var diagnostics = new List<string>();
            foreach (var candidate in document)
            {
                var query = candidate.Content;
                var matches = await retrieval.SearchAsync(new CorpusSearchRequest(query, 10, "workspace",
                    null, scenario.Files, "literal"), timeout.Token);
                passage = matches.Results.FirstOrDefault(match => match.OwnerSourceRevisionId == sidecarRevision &&
                    match.Passage == query);
                if (passage is not null) break;
                var sourceHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(candidate.SourceIdentity)));
                var context = await reader.ReadAsync(new(2, candidate.RootId, candidate.OwnerSourceRevisionId, sourceHash,
                    candidate.PipelineRecordId, candidate.PipelineRecordRevision, candidate.ArtifactId, candidate.ArtifactHash,
                    candidate.ChunkId, candidate.ChunkHash, candidate.StartOffset, candidate.Length, candidate.CorpusEpoch),
                    0, timeout.Token);
                diagnostics.Add($"eligible={matches.CandidateCount},warnings={string.Join(',', matches.Warnings)}," +
                    $"context={context is not null},bodyWithheld={disclosure.Evaluate(query, FluxKnowledge.Application.Visibility.LocalDisclosureKind.RetainedDetail).Withheld}," +
                    $"guardWithheld={context?.DisclosureText is null || disclosure.EvaluateCodeGuard(context.DisclosureText, FluxKnowledge.Application.Visibility.LocalDisclosureKind.RetainedDetail, context.GuardProof, context.StartOffset, context.Text.Length).Withheld}");
            }
            Assert.True(passage is not null, $"No guarded search hit for {Path.GetFileName(path)}. {string.Join(';', diagnostics)}");
            var readback = await retrieval.ReadAsync(new(passage.EvidenceRef, 0), timeout.Token);
            Assert.Equal(passage.Passage, readback.Text);
        }
        Assert.Equal(0, (await registration.RunOnceAsync(discoveryOptions, runtimeReady: true, default)).Queued);

        async Task RunStageAsync(string workerName)
        {
            var now = DateTimeOffset.UtcNow;
            var delivery = Assert.IsType<ClaimedDispatchMessage>(await new SqlOutboxStore(factory, sourceAdmission: admission)
                .ClaimNextDueAsync(workerName, now, TimeSpan.FromMinutes(5), [worker.Operation], default));
            var claim = Assert.IsType<ClaimedJob>(await new SqlJobClaimStore(factory, sourceAdmission: admission)
                .ClaimForDispatchAsync(delivery, workerName, now, TimeSpan.FromMinutes(5), default));
            await worker.ExecuteAsync(new(delivery, claim), default);
        }
    }

    private sealed class TeamsTrialWordTokenizer : IPassageTokenizer
    {
        public string Fingerprint => "synthetic-teams-trial-word-v1";
        public int CountTokens(string text) =>
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private sealed class TeamsSpeakerTrialFactAttribute : FactAttribute
    {
        public TeamsSpeakerTrialFactAttribute()
        {
            if (!OperatingSystem.IsWindows() ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEAMS_TRIAL_MP4")))
                Skip = "Requires an explicitly provisioned staging Teams clip and a dedicated GPU trial.";
        }
    }
}
