using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceScanWorkerTests
{
    [Fact]
    public async Task Scan_creates_one_deferred_activity_for_a_binary_file_without_retaining_an_artifact()
    {
        var root = SourceRootConfiguration.Create(
            Path.GetFullPath(Path.GetTempPath()), "test", true, false, 16 * 1024 * 1024);
        var request = SourceScanRequest.CreateHeld(root.Id, "test").Release(DateTimeOffset.UtcNow);
        var enumerator = new ReturningEnumerator(
            new SourceDiscoveredFile(
                Path.Combine(root.CanonicalPath, "report.txt"), "report.txt", "test:report", "%PDF-1.7"u8.ToArray(), true,
                new string('a', 64), 8, DateTimeOffset.UtcNow,
                new SourceClassificationResult(SourceClassification.DeferredCapability, null, "PDF")));
        var revisionStore = new RecordingScanStore();
        var artifactStore = new RecordingArtifactStore();
        var activityStore = new RecordingActivityStore();
        var worker = new SourceScanWorker(enumerator, revisionStore, artifactStore, activityStore);

        var result = await worker.ScanAsync(root, request, CancellationToken.None);

        Assert.Equal(1, result.DiscoveredCount);
        Assert.Equal(1, result.DeferredCount);
        Assert.Equal(1, artifactStore.PutCount);
        Assert.Equal(SourceActivityState.DeferredUnsupported, activityStore.Drafts.Single().InitialState);
        Assert.Equal(1, revisionStore.SuppressionCalls);
    }

    [Fact]
    public async Task Scan_does_not_suppress_unseen_paths_after_an_incomplete_enumeration()
    {
        var root = SourceRootConfiguration.Create(Path.GetFullPath(Path.GetTempPath()), "test", true, false, 16 * 1024 * 1024);
        var request = SourceScanRequest.CreateHeld(root.Id, "test").Release(DateTimeOffset.UtcNow);
        var enumerator = new ReturningEnumerator() { Evidence = [new SourceEnumerationEvidence("permission", "locked.txt", "UnauthorizedAccessException")] };
        var store = new RecordingScanStore();
        var worker = new SourceScanWorker(enumerator, store, new RecordingArtifactStore(), new RecordingActivityStore());

        await worker.ScanAsync(root, request, CancellationToken.None);

        Assert.Equal(0, store.SuppressionCalls);
        Assert.Equal(1, store.EvidenceCalls);
    }

    [Fact]
    public async Task Scan_records_a_bounded_snapshot_mutation_code_and_does_not_plan_text_after_retention_fails()
    {
        var root = SourceRootConfiguration.Create(Path.GetFullPath(Path.GetTempPath()), "test", true, false, 16 * 1024 * 1024);
        var request = SourceScanRequest.CreateHeld(root.Id, "test").Release(DateTimeOffset.UtcNow);
        var file = new SourceDiscoveredFile(
            Path.Combine(root.CanonicalPath, "report.txt"), "report.txt", "test:report", "text"u8.ToArray(), true,
            new string('a', 64), 4, DateTimeOffset.UtcNow,
            new SourceClassificationResult(SourceClassification.AcceptedUtf8Text, "text", null));
        var revisions = new RecordingScanStore();
        var activities = new RecordingActivityStore();
        var worker = new SourceScanWorker(
            new ReturningEnumerator(file),
            revisions,
            new ThrowingArtifactStore(new SourceSnapshotChangedException("the raw path must not persist")),
            activities);

        var result = await worker.ScanAsync(root, request, CancellationToken.None);

        Assert.Equal(1, result.BlockedCount);
        Assert.Equal("source-snapshot-changed", revisions.ArtifactFailureReason);
        var activity = Assert.Single(activities.Drafts);
        Assert.Equal(SourceActivityKind.DocumentParsing, activity.ActivityKind);
        Assert.Equal(ExecutionClass.DeferredCapability, activity.ExecutionClass);
        Assert.Equal(SourceActivityState.DeferredPolicy, activity.InitialState);
        Assert.Equal("source-snapshot-changed", activity.Reason);
        Assert.DoesNotContain(activities.Drafts, draft => draft.ActivityKind == SourceActivityKind.TextExtraction);
    }

    [Fact]
    public async Task Scan_places_bytes_then_converges_revision_and_artifact_before_planning_text()
    {
        var root = SourceRootConfiguration.Create(Path.GetFullPath(Path.GetTempPath()), "test", true, false, 16 * 1024 * 1024);
        var request = SourceScanRequest.CreateHeld(root.Id, "test").Release(DateTimeOffset.UtcNow);
        var file = new SourceDiscoveredFile(
            Path.Combine(root.CanonicalPath, "report.txt"), "report.txt", "test:report", "text"u8.ToArray(), true,
            new string('a', 64), 4, DateTimeOffset.UtcNow,
            new SourceClassificationResult(SourceClassification.AcceptedUtf8Text, "text", null));
        var events = new List<string>();
        var worker = new SourceScanWorker(
            new ReturningEnumerator(file),
            new RecordingScanStore(events),
            new RecordingArtifactStore(events),
            new RecordingActivityStore(events));

        var result = await worker.ScanAsync(root, request, CancellationToken.None);

        Assert.Equal(["artifact", "converge", "activity"], events.Take(3));
        Assert.Equal(0, result.IndexedCount);
    }

    [Fact]
    public async Task Scan_routes_accepted_csharp_to_the_inert_writer_not_ready_holding_activity()
    {
        var root = SourceRootConfiguration.Create(Path.GetFullPath(Path.GetTempPath()), "test", true, false, 16 * 1024 * 1024);
        var request = SourceScanRequest.CreateHeld(root.Id, "test").Release(DateTimeOffset.UtcNow);
        var file = new SourceDiscoveredFile(
            Path.Combine(root.CanonicalPath, "Example.cs"), "Example.cs", "test:example", "public class Example { }"u8.ToArray(), true,
            new string('a', 64), 24, DateTimeOffset.UtcNow,
            new SourceClassificationResult(SourceClassification.AcceptedUtf8Text, "public class Example { }", null));
        var activities = new RecordingActivityStore();

        await new SourceScanWorker(new ReturningEnumerator(file), new RecordingScanStore(), new RecordingArtifactStore(), activities)
            .ScanAsync(root, request, CancellationToken.None);

        var activity = Assert.Single(activities.Drafts);
        Assert.Equal(SourceActivityKind.DocumentParsing, activity.ActivityKind);
        Assert.Equal(ExecutionClass.DeferredCapability, activity.ExecutionClass);
        Assert.Equal(SourceActivityState.DeferredUnsupported, activity.InitialState);
        Assert.Equal("retained-csharp-code", activity.RequiredCapability);
        Assert.Equal("csharp-code-writer-not-ready", activity.Reason);
        Assert.DoesNotContain(activities.Drafts, draft => draft.ActivityKind == SourceActivityKind.TextExtraction);
    }

    [Theory]
    [InlineData(SourceClassification.DeferredCapability, "binary-csharp", "local-source-capability", SourceActivityState.DeferredUnsupported)]
    [InlineData(SourceClassification.DeferredPolicy, "invalid-csharp", null, SourceActivityState.DeferredPolicy)]
    public async Task Scan_does_not_route_ineligible_csharp_to_the_writer_holding_activity(
        SourceClassification classification,
        string reason,
        string? requiredCapability,
        SourceActivityState expectedState)
    {
        var root = SourceRootConfiguration.Create(Path.GetFullPath(Path.GetTempPath()), "test", true, false, 16 * 1024 * 1024);
        var request = SourceScanRequest.CreateHeld(root.Id, "test").Release(DateTimeOffset.UtcNow);
        var bytes = classification == SourceClassification.DeferredCapability ? "%PDF-1.7"u8.ToArray() : new byte[] { 0xc3, 0x28 };
        var file = new SourceDiscoveredFile(
            Path.Combine(root.CanonicalPath, "Example.cs"), "Example.cs", "test:example", bytes, true,
            new string('a', 64), bytes.Length, DateTimeOffset.UtcNow,
            new SourceClassificationResult(classification, null, reason));
        var activities = new RecordingActivityStore();

        await new SourceScanWorker(new ReturningEnumerator(file), new RecordingScanStore(), new RecordingArtifactStore(), activities)
            .ScanAsync(root, request, CancellationToken.None);

        var activity = Assert.Single(activities.Drafts);
        Assert.Equal(expectedState, activity.InitialState);
        Assert.Equal(requiredCapability, activity.RequiredCapability);
        Assert.Equal(reason, activity.Reason);
        Assert.NotEqual("csharp-code-writer-not-ready", activity.Reason);
    }

    [Fact]
    public async Task Scan_does_not_route_root_denied_csharp_to_the_writer_holding_activity()
    {
        var root = SourceRootConfiguration.Create(
            Path.GetFullPath(Path.GetTempPath()), "test", true, false, 16 * 1024 * 1024,
            allowedClassifications: ["application/pdf"]);
        var request = SourceScanRequest.CreateHeld(root.Id, "test").Release(DateTimeOffset.UtcNow);
        var bytes = "public class Example { }"u8.ToArray();
        var file = new SourceDiscoveredFile(
            Path.Combine(root.CanonicalPath, "Example.cs"), "Example.cs", "test:example", bytes, true,
            new string('a', 64), bytes.Length, DateTimeOffset.UtcNow,
            new SourceClassificationResult(SourceClassification.AcceptedUtf8Text, "public class Example { }", null));
        var activities = new RecordingActivityStore();

        await new SourceScanWorker(new ReturningEnumerator(file), new RecordingScanStore(), new RecordingArtifactStore(), activities)
            .ScanAsync(root, request, CancellationToken.None);

        var activity = Assert.Single(activities.Drafts);
        Assert.Equal(SourceActivityState.DeferredPolicy, activity.InitialState);
        Assert.Null(activity.RequiredCapability);
        Assert.Equal("The source root policy does not allow text/plain classification.", activity.Reason);
        Assert.NotEqual("csharp-code-writer-not-ready", activity.Reason);
    }

    [Fact]
    public async Task Scan_keeps_text_planning_available_when_a_failed_retention_attempt_converges_to_an_existing_artifact()
    {
        var root = SourceRootConfiguration.Create(Path.GetFullPath(Path.GetTempPath()), "test", true, false, 16 * 1024 * 1024);
        var request = SourceScanRequest.CreateHeld(root.Id, "test").Release(DateTimeOffset.UtcNow);
        var file = new SourceDiscoveredFile(
            Path.Combine(root.CanonicalPath, "report.txt"), "report.txt", "test:report", "text"u8.ToArray(), true,
            new string('a', 64), 4, DateTimeOffset.UtcNow,
            new SourceClassificationResult(SourceClassification.AcceptedUtf8Text, "text", null));
        var revisions = new RecordingScanStore { IsRetentionBlocked = false };
        var activities = new RecordingActivityStore();
        var worker = new SourceScanWorker(
            new ReturningEnumerator(file), revisions,
            new ThrowingArtifactStore(new SourceSnapshotChangedException("transient read failure")), activities);

        var result = await worker.ScanAsync(root, request, CancellationToken.None);

        Assert.Equal(0, result.IndexedCount);
        Assert.Equal(0, result.BlockedCount);
        Assert.Equal(SourceActivityKind.TextExtraction, Assert.Single(activities.Drafts).ActivityKind);
    }

    [Fact]
    public async Task Scan_does_not_suppress_unseen_paths_when_a_claimed_root_has_no_durable_identity()
    {
        var root = SourceRootConfiguration.Restore(
            SourceRootId.New(), Path.GetFullPath(Path.GetTempPath()), "test", false, false, 1024,
            [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1,
            physicalIdentityFingerprint: null,
            requiresPhysicalIdentityValidation: true);
        var request = SourceScanRequest.CreateHeld(root.Id, "test").Release(DateTimeOffset.UtcNow);
        var store = new RecordingScanStore();
        var worker = new SourceScanWorker(new LocalSourceEnumerator(), store, new RecordingArtifactStore(), new RecordingActivityStore());

        await worker.ScanAsync(root, request, CancellationToken.None);

        Assert.Equal(0, store.SuppressionCalls);
        Assert.Equal(1, store.EvidenceCalls);
    }

    [Fact]
    public async Task Scan_leaves_legacy_office_designation_to_the_source_neutral_retained_selector()
    {
        var root = SourceRootConfiguration.Create(Path.GetFullPath(Path.GetTempPath()), "test", true, false, 16 * 1024 * 1024);
        var request = SourceScanRequest.CreateHeld(root.Id, "test").Release(DateTimeOffset.UtcNow);
        var cfb = new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1, 0x00 };
        var file = new SourceDiscoveredFile(
            Path.Combine(root.CanonicalPath, "report.doc"), "report.doc", "test:report", cfb, true,
            new string('a', 64), cfb.Length, DateTimeOffset.UtcNow,
            new SourceClassificationResult(SourceClassification.DeferredCapability, null, "UnknownBinary"));
        var activities = new RecordingActivityStore();
        var worker = new SourceScanWorker(new ReturningEnumerator(file), new RecordingScanStore(), new RecordingArtifactStore(), activities);

        await worker.ScanAsync(root, request, CancellationToken.None);

        var activity = Assert.Single(activities.Drafts);
        Assert.Equal(SourceActivityState.DeferredUnsupported, activity.InitialState);
        Assert.Equal("local-source-capability", activity.RequiredCapability);
        Assert.Equal("UnknownBinary", activity.Reason);
    }

    [Fact]
    public async Task Watched_cfb_doc_ingress_is_retained_and_deferred_for_source_neutral_designation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"flux-cfb-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "legacy.doc"), [0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1]);
            var root = SourceRootConfiguration.Create(directory, "test", false, false, 16 * 1024 * 1024);
            var request = SourceScanRequest.CreateHeld(root.Id, "test").Release(DateTimeOffset.UtcNow);
            var artifacts = new RecordingArtifactStore();
            var activities = new RecordingActivityStore();

            await new SourceScanWorker(new LocalSourceEnumerator(), new RecordingScanStore(), artifacts, activities)
                .ScanAsync(root, request, CancellationToken.None);

            Assert.Equal(1, artifacts.PutCount);
            var activity = Assert.Single(activities.Drafts);
            Assert.Equal(SourceActivityState.DeferredUnsupported, activity.InitialState);
            Assert.Equal("local-source-capability", activity.RequiredCapability);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"physicalIdentity\":{\"identityFingerprint\":42}}")]
    [InlineData("{\"physicalIdentity\":{\"identityFingerprint\":\"\"}}")]
    [InlineData("{\"physicalIdentity\":{\"identityFingerprint\":\"not-hex\"}}")]
    public void Admission_identity_parser_fails_closed_for_missing_malformed_or_non_string_evidence(string? evidence) =>
        Assert.Null(SqlSourceScanStore.ParseAdmissionIdentityFingerprint(evidence));

    private sealed class ReturningEnumerator(params SourceDiscoveredFile[] files) : ISourceFileEnumerator
    {
        public IReadOnlyList<SourceEnumerationEvidence> Evidence { get; init; } = [];

        public IReadOnlyList<SourceEnumerationEvidence> LastEvidence => Evidence;

        public async IAsyncEnumerable<SourceDiscoveredFile> EnumerateAsync(
            SourceRootConfiguration sourceRoot,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
                await Task.Yield();
            }
        }
    }

    private sealed class RecordingScanStore(List<string>? events = null) : ISourceScanStore
    {
        public int SuppressionCalls { get; private set; }
        public int EvidenceCalls { get; private set; }
        public string? ArtifactFailureReason { get; private set; }
        public bool IsRetentionBlocked { get; init; } = true;

        public ValueTask<SourceRevisionId> ConvergeRevisionAndArtifactAsync(SourceRootConfiguration sourceRoot, SourceDiscoveredFile file, SourceArtifactReceipt receipt, CancellationToken cancellationToken)
        {
            events?.Add("converge");
            return ValueTask.FromResult(SourceRevisionId.New());
        }

        public ValueTask<SourceRetentionConvergence> ConvergeBlockedRevisionAsync(SourceRootConfiguration sourceRoot, SourceDiscoveredFile file, string reason, CancellationToken cancellationToken)
        {
            ArtifactFailureReason = reason;
            return ValueTask.FromResult(new SourceRetentionConvergence(SourceRevisionId.New(), IsRetentionBlocked));
        }

        public ValueTask SuppressUnseenAsync(SourceRootId sourceRootId, IReadOnlySet<SourceRevisionId> convergedRevisionIds, CancellationToken cancellationToken)
        {
            SuppressionCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask RecordEnumerationEvidenceAsync(SourceScanRequestId sourceScanRequestId, IReadOnlyList<SourceEnumerationEvidence> evidence, CancellationToken cancellationToken)
        {
            EvidenceCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingArtifactStore(List<string>? events = null) : ISourceArtifactStore
    {
        public int PutCount { get; private set; }

        public ValueTask<SourceArtifactReceipt> PutAsync(ReadOnlyMemory<byte> content, SourceArtifactMetadata metadata, CancellationToken cancellationToken)
        {
            PutCount++;
            events?.Add("artifact");
            return ValueTask.FromResult(new SourceArtifactReceipt(SourceArtifactId.New(), metadata.ContentSha256, "sha256\\aa\\x.bin", metadata.ByteLength, false));
        }

        public ValueTask<SourceArtifactReceipt> PutFileAsync(SourceDiscoveredFile snapshot, SourceArtifactMetadata metadata, CancellationToken cancellationToken) =>
            PutAsync(snapshot.ClassificationBuffer, metadata, cancellationToken);
    }

    private sealed class ThrowingArtifactStore(Exception exception) : ISourceArtifactStore
    {
        public ValueTask<SourceArtifactReceipt> PutAsync(ReadOnlyMemory<byte> content, SourceArtifactMetadata metadata, CancellationToken cancellationToken) =>
            ValueTask.FromException<SourceArtifactReceipt>(exception);

        public ValueTask<SourceArtifactReceipt> PutFileAsync(SourceDiscoveredFile snapshot, SourceArtifactMetadata metadata, CancellationToken cancellationToken) =>
            ValueTask.FromException<SourceArtifactReceipt>(exception);
    }

    private sealed class RecordingActivityStore(List<string>? events = null) : ISourceActivityStore
    {
        public List<SourceActivityDraft> Drafts { get; } = [];

        public ValueTask<SourceActivity> FindOrCreateAsync(SourceActivityDraft draft, CancellationToken cancellationToken)
        {
            events?.Add("activity");
            Drafts.Add(draft);
            return ValueTask.FromResult(SourceActivity.Create(
                draft.SourceRevisionId, draft.ActivityKind, draft.ExecutionClass, draft.ProcessorVersion,
                draft.InputFingerprint, draft.RequiredCapability, draft.Reason, draft.InitialState));
        }
    }
}
