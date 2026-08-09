using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class SourceRootProjectionTests
{
    [Fact]
    public void Source_root_list_projection_preserves_durable_scan_counts_and_state()
    {
        var lastScan = DateTimeOffset.Parse("2026-08-08T10:15:00Z");
        var projection = new SourceRootListProjection(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Research notes",
            "E:\\Corpus\\Research",
            "Enabled",
            lastScan,
            12,
            3,
            2,
            1);

        Assert.Equal("Research notes", projection.DisplayName);
        Assert.Equal("E:\\Corpus\\Research", projection.CanonicalPath);
        Assert.Equal("Enabled", projection.State);
        Assert.Equal(lastScan, projection.LastScanCompletedAtUtc);
        Assert.Equal(12, projection.IndexedCount);
        Assert.Equal(3, projection.DeferredCount);
        Assert.Equal(2, projection.BlockedCount);
        Assert.Equal(1, projection.ErrorCount);
    }

    [Fact]
    public async Task Source_root_state_preview_reports_accepted_deferred_and_blocked_files_before_save()
    {
        var preview = new SourceRootPreview(
            "E:\\Corpus\\Research",
            4,
            2,
            1,
            1,
            0,
            ["*.md"],
            ["private/**"],
            []);

        var state = new SourceRootPageState(new FixedPreviewReader(preview));

        await state.LoadPreviewAsync(CancellationToken.None);

        Assert.Equal(4, state.Preview!.MatchedFileCount);
        Assert.Equal(2, state.Preview.PlannedInProcessCount);
        Assert.Equal(1, state.Preview.DeferredCount);
        Assert.Equal(1, state.Preview.BlockedCount);
        Assert.Equal(["*.md"], state.Preview.EffectiveIncludePatterns);
        Assert.Equal(["private/**"], state.Preview.EffectiveExcludePatterns);
    }

    [Fact]
    public async Task Source_root_preview_counts_classified_files_through_the_admitted_policy_before_save()
    {
        var reader = new SourceRootProjectionReader(
            new ThrowingContextFactory(),
            new FixedPathPolicy(),
            new FixedEnumerator(
            [
                File("accepted.md", new SourceClassificationResult(SourceClassification.AcceptedUtf8Text, "text", null)),
                File("document.pdf", new SourceClassificationResult(SourceClassification.DeferredCapability, null, "PDF capability is unavailable.")),
                File("script.cs", new SourceClassificationResult(SourceClassification.DeferredPolicy, null, "Code ingestion is not enabled."))
            ]),
            new LocalSourceCapabilityHandlerRegistry([]));

        var preview = await reader.PreviewAsync(
            new SourceRootDraft("E:\\Corpus", "Corpus", true, ["*.md"], ["private/**"], 16L * 1024 * 1024, "test"),
            CancellationToken.None);

        Assert.Equal(3, preview.MatchedFileCount);
        Assert.Equal(1, preview.PlannedInProcessCount);
        Assert.Equal(1, preview.DeferredCount);
        Assert.Equal(1, preview.BlockedCount);
        Assert.Equal(["*.md"], preview.EffectiveIncludePatterns);
        Assert.Equal(["private/**"], preview.EffectiveExcludePatterns);
        Assert.Contains("PDF capability is unavailable.", preview.Reasons);
    }

    [Fact]
    public async Task Source_root_state_reloads_SQL_backed_root_list_when_reconnected()
    {
        var reader = new SequencedReader();
        var state = new SourceRootPageState(reader);

        await state.ReloadAsync(CancellationToken.None);
        await state.HandleStatusChangedAsync(
            new StatusChanged(null, "reconnect", DateTimeOffset.Parse("2026-08-08T10:16:00Z")),
            CancellationToken.None);

        Assert.Equal(2, reader.ReadRootListCount);
        Assert.Single(state.Roots);
        Assert.Equal("reloaded", state.Roots[0].DisplayName);
    }

    [Fact]
    public async Task Source_root_state_rejects_saving_a_draft_that_differs_from_its_preview()
    {
        var preview = new SourceRootPreview("E:\\Corpus", 0, 0, 0, 0, 0, ["*.md"], [], []);
        var state = new SourceRootPageState(new FixedPreviewReader(preview));
        var previewedDraft = new SourceRootDraft("E:\\Corpus", "Corpus", true, ["*.md"], [], 16L * 1024 * 1024, "test");
        var changedDraft = previewedDraft with { ExcludePatterns = ["private/**"] };

        await state.LoadPreviewAsync(previewedDraft, CancellationToken.None);

        Assert.True(state.IsPreviewCurrent(previewedDraft));
        Assert.False(state.IsPreviewCurrent(changedDraft));
    }

    [Fact]
    public async Task Source_root_detail_reloads_its_SQL_projection_when_the_circuit_reconnects()
    {
        var reader = new DetailReader(Detail("initial"), Detail("reloaded"));
        var state = new SourceRootDetailPageState(reader, reprocessor: null);

        await state.LoadAsync(Guid.Parse("33333333-3333-3333-3333-333333333333"), CancellationToken.None);
        await state.HandleStatusChangedAsync(
            new StatusChanged(null, "reconnect", DateTimeOffset.Parse("2026-08-08T10:17:00Z")),
            CancellationToken.None);

        Assert.Equal(2, reader.ReadRootCount);
        Assert.Equal("reloaded", state.Detail!.DisplayName);
    }

    [Fact]
    public async Task Source_root_detail_reprocess_forwards_the_durable_activity_idempotency_key_to_the_local_seam()
    {
        var activity = new DeferredContentReplayRequest(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            "44444444444444444444444444444444|2|11:phase-3a-v1|64:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "local-source-capability",
            Guid.Parse("9c56d5b2-c931-4c8b-ab66-fd0601e9c1df"),
            "phase-3a-v1",
            "phase-3a-inprocess-text-metadata-v1");
        var reprocessor = new RecordingReprocessor();
        var state = new SourceRootDetailPageState(new DetailReader(Detail("root", [activity])), reprocessor);

        await state.LoadAsync(Guid.Parse("33333333-3333-3333-3333-333333333333"), CancellationToken.None);
        var result = await state.ReprocessDeferredContentAsync(CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal([activity], reprocessor.RequestedActivities);
    }

    private sealed class FixedPreviewReader(SourceRootPreview preview) : ISourceRootProjectionReader
    {
        public ValueTask<IReadOnlyList<SourceRootListProjection>> ReadRootsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceRootListProjection>>([]);

        public ValueTask<SourceRootDetailProjection?> ReadRootAsync(Guid rootId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<SourceRootDetailProjection?>(null);

        public ValueTask<SourceRootPreview> PreviewAsync(SourceRootDraft draft, CancellationToken cancellationToken) =>
            ValueTask.FromResult(preview);
    }

    private sealed class SequencedReader : ISourceRootProjectionReader
    {
        public int ReadRootListCount { get; private set; }

        public ValueTask<IReadOnlyList<SourceRootListProjection>> ReadRootsAsync(CancellationToken cancellationToken)
        {
            ReadRootListCount++;
            return ValueTask.FromResult<IReadOnlyList<SourceRootListProjection>>(
            [
                new SourceRootListProjection(Guid.NewGuid(), ReadRootListCount == 1 ? "initial" : "reloaded", "E:\\Corpus", "Enabled", null, 0, 0, 0, 0)
            ]);
        }

        public ValueTask<SourceRootDetailProjection?> ReadRootAsync(Guid rootId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<SourceRootDetailProjection?>(null);

        public ValueTask<SourceRootPreview> PreviewAsync(SourceRootDraft draft, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SourceRootPreview("E:\\Corpus", 0, 0, 0, 0, 0, [], [], []));
    }

    private sealed class DetailReader(params SourceRootDetailProjection[] details) : ISourceRootProjectionReader
    {
        private int _index;

        public int ReadRootCount { get; private set; }

        public ValueTask<IReadOnlyList<SourceRootListProjection>> ReadRootsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceRootListProjection>>([]);

        public ValueTask<SourceRootDetailProjection?> ReadRootAsync(Guid rootId, CancellationToken cancellationToken)
        {
            ReadRootCount++;
            return ValueTask.FromResult<SourceRootDetailProjection?>(details[Math.Min(_index++, details.Length - 1)]);
        }

        public ValueTask<SourceRootPreview> PreviewAsync(SourceRootDraft draft, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SourceRootPreview("E:\\Corpus", 0, 0, 0, 0, 0, [], [], []));
    }

    private sealed class RecordingReprocessor : IDeferredContentReprocessor
    {
        public IReadOnlyList<DeferredContentReplayRequest> RequestedActivities { get; private set; } = [];

        public ValueTask<DeferredContentReplayResult> ReprocessAsync(
            IReadOnlyList<DeferredContentReplayRequest> activities,
            CancellationToken cancellationToken)
        {
            RequestedActivities = activities;
            return ValueTask.FromResult(new DeferredContentReplayResult(true, activities.Count, "Replay request accepted."));
        }
    }

    private sealed class FixedPathPolicy : ISourceRootPathPolicy
    {
        public SourceRootPathValidation ValidateAndCanonicalise(SourceRootCreateRequest request) =>
            new(
                request.FullPath,
                new SourceRootPhysicalIdentity(request.FullPath, "E:\\", true, new string('a', 64)),
                new SourceRootPermissionEvidence(true, new string('b', 64), "{}"));
    }

    private sealed class FixedEnumerator(IReadOnlyList<SourceDiscoveredFile> files) : ISourceFileEnumerator
    {
        public IReadOnlyList<SourceEnumerationEvidence> LastEvidence { get; } = [];

        public async IAsyncEnumerable<SourceDiscoveredFile> EnumerateAsync(
            SourceRootConfiguration sourceRoot,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class ThrowingContextFactory : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => throw new NotSupportedException();
    }

    private static SourceDiscoveredFile File(string path, SourceClassificationResult classification) =>
        new(
            $"E:\\Corpus\\{path}",
            path,
            path,
            [],
            true,
            new string('c', 64),
            1,
            DateTimeOffset.UnixEpoch,
            classification);

    private static SourceRootDetailProjection Detail(
        string displayName,
        IReadOnlyList<DeferredContentReplayRequest>? replayActivities = null) =>
        new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            displayName,
            "E:\\Corpus",
            "Enabled",
            "Completed",
            DateTimeOffset.UnixEpoch,
            1,
            1,
            0,
            0,
            0,
            [],
            replayActivities ?? []);
}
