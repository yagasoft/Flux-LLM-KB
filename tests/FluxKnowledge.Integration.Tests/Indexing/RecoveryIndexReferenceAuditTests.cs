using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class RecoveryIndexReferenceAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeRecoveryAudit_" + Guid.NewGuid().ToString("N"));
    private string Index => Path.Combine(_root, "Index");
    private string Recovery => Path.Combine(_root, "Recovery");
    private const string Release = "20261020T111743Z-61b5e452b29f";

    public RecoveryIndexReferenceAuditTests() { Directory.CreateDirectory(Index); Directory.CreateDirectory(Recovery); }

    [Fact]
    public void Recognised_app_and_configuration_backups_do_not_pin_native_payloads_even_when_they_contain_identifiers()
    {
        var packet = Packet();
        File.WriteAllText(Path.Combine(packet, "manifest.json"), JsonSerializer.Serialize(new
        {
            commit = new string('a', 40), staged_at_utc = DateTimeOffset.UtcNow, application_root = Path.Combine(_root, "App"),
            migrations = false, clean_slate = false
        }));
        File.WriteAllText(Path.Combine(packet, "original-config.json"), JsonSerializer.Serialize(new { Trace = Guid.NewGuid(), Root = Index }));
        var payload = Path.Combine(packet, "previous"); Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "FluxKnowledge.Web.dll"), "synthetic app payload, never executed");
        var audit = new RecoveryIndexReferenceAudit(Index).Capture(Recovery);
        Assert.Empty(audit.Pins);
        Assert.Equal(1, audit.PacketCount);
        var repeated = new RecoveryIndexReferenceAudit(Index).Capture(Recovery);
        Assert.Equal(audit.RootFingerprint, repeated.RootFingerprint);
        Assert.Equal(audit.InventoryChecksum, repeated.InventoryChecksum);
        Assert.Equal(audit.Pins, repeated.Pins);
        File.AppendAllText(Path.Combine(packet, "original-config.json"), " ");
        Assert.NotEqual(audit.InventoryChecksum, new RecoveryIndexReferenceAudit(Index).Capture(Recovery).InventoryChecksum);
    }

    [Fact]
    public void A_rebuild_manifest_pins_only_its_explicit_generation_and_physical_path()
    {
        var generation = Guid.NewGuid();
        var path = Path.Combine(Index, "generations", generation.ToString("N"));
        var packet = Packet();
        WriteRebuild(packet, generation, path);
        var audit = new RecoveryIndexReferenceAudit(Index).Capture(Recovery);
        var pin = Assert.Single(audit.Pins);
        Assert.Equal(generation, pin.GenerationId);
        Assert.Equal(path, pin.IndexPath);
        Assert.Equal(Path.Combine("IncrementalUpdates", Release, "corpus-rebuild-manifest.json"), pin.Packet);
    }

    [Theory]
    [InlineData("hybrid")]
    [InlineData("hybridpatch")]
    [InlineData("repositoryrecovery")]
    public void Existing_incremental_packet_variants_keep_their_exact_generation_references(string suffix)
    {
        var packet = Packet(Release + "-" + suffix); var generation = Guid.NewGuid();
        WriteRebuild(packet, generation, Path.Combine(Index, "generations", generation.ToString("N")));
        Assert.Equal(generation, Assert.Single(new RecoveryIndexReferenceAudit(Index).Capture(Recovery).Pins).GenerationId);
    }

    [Fact]
    public void An_early_hybrid_preplan_contains_no_external_generation_reference()
    {
        var release = Release + "-hybrid";
        File.WriteAllText(Path.Combine(Packet(release), "hybrid-state.json"), JsonSerializer.Serialize(new {
            Version = 1, ReleaseId = release, OperationId = Guid.NewGuid(), ResetCommitted = false,
            SchemaAttempted = true, HoldReleased = false, HoldReleaseAttempted = false }));
        Assert.Empty(new RecoveryIndexReferenceAudit(Index).Capture(Recovery).Pins);
    }

    [Theory]
    [InlineData("committed")]
    [InlineData("unknown-field")]
    [InlineData("missing-predecessor")]
    public void Legacy_hybrid_packets_still_require_all_declared_reference_metadata(string fault)
    {
        var packet = Packet(Release + "-hybrid");
        var state = new Dictionary<string, object?> { ["Version"] = 1, ["ReleaseId"] = Path.GetFileName(packet),
            ["OperationId"] = Guid.NewGuid(), ["ResetCommitted"] = fault == "committed" };
        if (fault == "committed") state["ResetManifestHash"] = new string('a', 64);
        if (fault == "unknown-field") state["IndexPath"] = Index;
        if (fault == "missing-predecessor") state["Replacement"] = new { ReleaseId = "20261019T111743Z-61b5e452b29f-hybrid" };
        var file = Path.Combine(packet, "hybrid-state.json"); File.WriteAllText(file, JsonSerializer.Serialize(state));
        Assert.Equal(Path.GetRelativePath(Recovery, file), Assert.Throws<RecoveryIndexAuditException>(
            () => new RecoveryIndexReferenceAudit(Index).Capture(Recovery)).RelativePath);
    }

    [Fact]
    public void A_complete_retention_snapshot_is_self_contained_without_the_original_live_generation()
    {
        var (manifest, _) = RetentionSnapshot();
        Assert.False(Directory.Exists(manifest.Entries[0].Generation.IndexPath));
        var audit = new RecoveryIndexReferenceAudit(Index).Capture(Recovery);
        Assert.Empty(audit.Pins);
        Assert.Equal(1, audit.PacketCount);
        Assert.Equal(audit.InventoryChecksum, new RecoveryIndexReferenceAudit(Index).Capture(Recovery).InventoryChecksum);
    }

    [Theory]
    [InlineData("missing-member")]
    [InlineData("missing-native")]
    [InlineData("escaped-name")]
    [InlineData("unfinished-journal")]
    [InlineData("wrong-format")]
    public void A_retention_snapshot_requires_its_own_complete_supported_payload_and_settled_journal(string fault)
    {
        var (manifest, directory) = RetentionSnapshot();
        var root = Path.GetDirectoryName(directory)!;
        var affected = Path.Combine(root, "manifest.json");
        if (fault == "missing-member") { affected = Path.Combine(directory, "members.bin"); File.Delete(affected); }
        if (fault == "missing-native") { affected = Path.Combine(directory, "index.usearch"); File.Delete(affected); }
        if (fault == "escaped-name")
        {
            manifest = manifest with { Entries = [manifest.Entries[0] with { Files = [new("../outside", 1, new('a', 64))] }] };
            File.WriteAllText(affected, JsonSerializer.Serialize(manifest));
        }
        if (fault == "wrong-format")
        {
            manifest.Entries[0].Generation.FormatVersion = 2;
            File.WriteAllText(affected, JsonSerializer.Serialize(manifest));
        }
        if (fault == "unfinished-journal")
        {
            var operation = Guid.NewGuid(); affected = Path.Combine(directory, operation.ToString("N") + ".journal.json");
            File.WriteAllText(affected, JsonSerializer.Serialize(new { OperationId = operation,
                ManifestHash = SqlIndexRetentionOperations.ManifestHash(manifest), GenerationId = manifest.Entries[0].Generation.Id,
                Restore = false, Started = new[] { "index.usearch" }, Completed = Array.Empty<string>(),
                DirectoryStarted = false, DirectoryCompleted = false }));
        }
        var refusal = Assert.Throws<RecoveryIndexAuditException>(() => new RecoveryIndexReferenceAudit(Index).Capture(Recovery));
        Assert.Equal(Path.GetRelativePath(Recovery, affected), refusal.RelativePath);
    }

    [Theory]
    [InlineData("{\"Version\":\"1\",\"Phase\":\"Completed\"}")]
    [InlineData("{\"Version\":1,\"Phase\":2}")]
    public void Malformed_packet_fields_keep_an_actionable_path_instead_of_throwing_an_unclassified_error(string json)
    {
        var path = Path.Combine(Packet(), "repository-recovery-receipt.json");
        File.WriteAllText(path, json);
        Assert.Equal(Path.GetRelativePath(Recovery, path), Assert.Throws<RecoveryIndexAuditException>(
            () => new RecoveryIndexReferenceAudit(Index).Capture(Recovery)).RelativePath);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("malformed")]
    [InlineData("escape")]
    [InlineData("unfinished")]
    [InlineData("oversized")]
    public void Unsupported_or_incomplete_reference_evidence_refuses_with_the_affected_packet(string fault)
    {
        var packet = Packet();
        var generation = Guid.NewGuid();
        var relative = Path.Combine("IncrementalUpdates", Release, "corpus-rebuild-manifest.json");
        var file = Path.Combine(packet, "corpus-rebuild-manifest.json");
        if (fault == "escape") WriteRebuild(packet, generation, Path.Combine(_root, "outside", generation.ToString("N")));
        if (fault == "malformed") File.WriteAllText(file, "{");
        if (fault == "unknown") { file = Path.Combine(packet, "unrecognised-reference.json"); File.WriteAllText(file, "{}"); relative = Path.GetRelativePath(Recovery, file); }
        if (fault == "unfinished") { file = Path.Combine(packet, "repository-recovery-receipt.json"); File.WriteAllText(file, "{\"Version\":1,\"Phase\":\"Activating\"}"); relative = Path.GetRelativePath(Recovery, file); }
        if (fault == "oversized") { using var output = File.Create(file); output.SetLength(8 * 1024 * 1024 + 1); }
        var refusal = Assert.Throws<RecoveryIndexAuditException>(() => new RecoveryIndexReferenceAudit(Index).Capture(Recovery));
        Assert.Equal(relative, refusal.RelativePath);
    }

    [Fact]
    public void A_directory_link_cannot_hide_an_external_reference()
    {
        var outside = Path.Combine(_root, "outside"); Directory.CreateDirectory(outside);
        var packet = Path.Combine(Recovery, "IncrementalUpdates"); Directory.CreateDirectory(packet);
        var link = Path.Combine(packet, Release);
        Directory.CreateSymbolicLink(link, outside);
        try { Assert.ThrowsAny<Exception>(() => new RecoveryIndexReferenceAudit(Index).Capture(Recovery)); }
        finally { Directory.Delete(link); }
        Assert.True(Directory.Exists(outside));
    }

    private string Packet(string release = Release)
    {
        var packet = Path.Combine(Recovery, "IncrementalUpdates", release);
        Directory.CreateDirectory(packet); return packet;
    }

    private (IndexRetentionManifest Manifest, string Directory) RetentionSnapshot()
    {
        var id = Guid.NewGuid(); var generation = Guid.NewGuid();
        var directory = Path.Combine(Recovery, "IndexRetention", id.ToString("N"), generation.ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "members.bin"), new byte[8]);
        File.WriteAllBytes(Path.Combine(directory, "index.usearch"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(directory, "metadata.json"), "{}");
        var files = new[] { "index.usearch", "metadata.json" }.Select(name => new IndexRetentionFile(name,
            new FileInfo(Path.Combine(directory, name)).Length, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))))).ToArray();
        var manifest = new IndexRetentionManifest(1, id, Guid.NewGuid(), [], [new(new IndexGenerationEntity {
            Id = generation, IndexPath = Path.Combine(Index, "generations", generation.ToString("N")), FormatVersion = 1,
            VectorCount = 1, ModelFingerprint = "synthetic", Dimensions = 4, MetadataChecksum = new('c', 64) },
            1, new('a', 64), new('b', 64), files)], 1);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(directory)!, "manifest.json"), JsonSerializer.Serialize(manifest));
        return (manifest, directory);
    }

    internal static void WriteRebuild(string packet, Guid generation, string path)
    {
        var plan = new CorpusRebuildPlan(Guid.NewGuid(), "test-server", "test-database", new(Guid.NewGuid(), 1),
            Guid.NewGuid(), new("synthetic", 4), new('a', 64), [], [],
            [new(generation, path, new('b', 64), "synthetic", 4, 1)], [], 1, 1, 1, "");
        plan = plan with { ManifestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan)))) };
        File.WriteAllText(Path.Combine(packet, "corpus-rebuild-manifest.json"), JsonSerializer.Serialize(plan));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
