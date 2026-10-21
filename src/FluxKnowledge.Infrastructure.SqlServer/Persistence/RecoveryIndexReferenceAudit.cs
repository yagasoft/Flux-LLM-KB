using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Integrations.Files;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed record RecoveryIndexPin(string Packet, Guid? GenerationId = null, string? IndexPath = null);
public sealed record RecoveryIndexAudit(string RootPath, string IndexRoot, string RootFingerprint, string InventoryChecksum,
    IReadOnlyList<RecoveryIndexPin> Pins, int PacketCount);
public sealed class RecoveryIndexAuditException(string reason, string relativePath) : InvalidOperationException(reason)
{
    public string RelativePath { get; } = relativePath;
}

/// <summary>Privileged, bounded inspection of recovery reference metadata. Never opens source or index payloads.</summary>
public sealed class RecoveryIndexReferenceAudit(string indexRoot)
{
    public const int ClassifierVersion = 1;
    private static readonly JsonSerializerOptions StrictJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record RetentionJournal(Guid OperationId, string ManifestHash, Guid GenerationId, bool Restore,
        List<string> Started, List<string> Completed, bool DirectoryStarted = false, bool DirectoryCompleted = false);
    private static readonly HashSet<string> Payloads = new(StringComparer.Ordinal)
        { "candidate", "previous", "failed", "candidate-interactive-host", "previous-interactive-host", "operator" };
    private static readonly HashSet<string> NonPinningFiles = new(StringComparer.Ordinal)
    {
        "candidate-config.json", "original-config.json", "source-access-config-before.json", "source-access-config-after.json",
        "code-disclosure-up.sql", "corpus-fulltext-up.sql", "corpus-fulltext-down.sql", "hybrid-idempotent-up.sql",
        "repository-recovery-up.sql", "source-deletion-cleanup-up.sql", "source-deletion-up.sql", "source-deletion-down.sql",
        "iis-hosting-settings.json", "iis-preload-startup.json", "outlook-restoration-receipt.json", "patch-binding.json", "patch-progress.json",
        "source-deletion-hold-release-intent.json", "source-deletion-hold-released.json", "source-deletion-migration-attempt.json",
        "source-deletion-migration-prepared.json", "source-deletion-migration-verified.json", "stopped-pool-recovery-baseline.json"
    };

    public RecoveryIndexAudit Capture(string recoveryRoot, CancellationToken token = default)
    {
        var root = Path.GetFullPath(recoveryRoot);
        var index = Path.TrimEndingDirectorySeparator(Path.GetFullPath(indexRoot));
        var held = new List<IDisposable>();
        var inventory = new List<string>();
        var pins = new List<RecoveryIndexPin>();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var entries = 0; var packets = 0; long bytes = 0;
        string Relative(string path) => Path.GetRelativePath(root, path);
        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        void Refuse(string path, string reason = "index-recovery-packet-unsupported") => throw new RecoveryIndexAuditException(reason, Relative(path));
        void Budget(string path)
        {
            token.ThrowIfCancellationRequested();
            if (++entries > 8192 || packets > 512 || timer.Elapsed > TimeSpan.FromSeconds(30)) Refuse(path, "index-recovery-audit-budget");
        }
        void DirectoryProof(string path)
        {
            Budget(path);
            try
            {
                held.Add(PhysicalFileIdentity.OpenDirectoryLease(path));
                inventory.Add(Relative(path) + "|directory|" + PhysicalFileIdentity.GetDirectory(path).IdentityFingerprint);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Refuse(path, "index-recovery-path-unavailable"); }
        }
        string[] Children(string path) => Directory.EnumerateFileSystemEntries(path).Select(value => { Budget(value); return value; })
            .Order(StringComparer.Ordinal).ToArray();
        JsonDocument? Metadata(string path)
        {
            Budget(path);
            try
            {
                using var file = new FileStream(PhysicalFileIdentity.OpenReadNoFollow(path), FileAccess.Read);
                if (file.Length is < 1 or > 8 * 1024 * 1024 || bytes + file.Length > 64 * 1024 * 1024) Refuse(path, "index-recovery-audit-budget");
                bytes += file.Length;
                inventory.Add(Relative(path) + "|" + file.Length + "|" + Convert.ToHexStringLower(SHA256.HashData(file)));
                file.Position = 0;
                if (!path.EndsWith(".json", StringComparison.Ordinal)) return null;
                var document = JsonDocument.Parse(file, new() { MaxDepth = 48 });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    document.RootElement.EnumerateObject().Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() !=
                    document.RootElement.EnumerateObject().Count()) { document.Dispose(); Refuse(path, "index-recovery-metadata-invalid"); }
                return document;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            { Refuse(path, "index-recovery-metadata-invalid"); return null; }
        }
        string GenerationPath(string path, Guid id, string packet)
        {
            if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFullPath(path),
                Path.Combine(index, "generations", id.ToString("N")), StringComparison.OrdinalIgnoreCase))
                Refuse(packet, "index-recovery-reference-outside-layout");
            return Path.GetFullPath(path);
        }
        void Payload(string path)
        {
            DirectoryProof(path);
            // These directories are restored as application code, never as an ANN payload.
            // Only the packet's reference metadata can pin an external generation.
            var role = Path.GetFileName(path).Contains("interactive-host", StringComparison.Ordinal) ? "OutlookHost" :
                Path.GetFileName(path) == "operator" ? "Cli" : "Web";
            var entry = Path.Combine(path, "FluxKnowledge." + role + ".dll");
            using var file = new FileStream(PhysicalFileIdentity.OpenReadNoFollow(entry), FileAccess.Read);
            if (file.Length is < 1 or > 64 * 1024 * 1024) Refuse(entry, "index-recovery-payload-invalid");
            inventory.Add(Relative(entry) + "|application-entry|" + PhysicalFileIdentity.Get(file.SafeFileHandle) + "|" + file.Length);
        }
        string Rebuild(string path, JsonElement value)
        {
            CorpusRebuildPlan? plan;
            try { plan = value.Deserialize<CorpusRebuildPlan>(StrictJson); }
            catch (JsonException) { Refuse(path, "index-recovery-rebuild-invalid"); return ""; }
            if (plan is null || plan.OperationId == Guid.Empty || plan.TargetEpoch == Guid.Empty || plan.PreviousStamp is null ||
                plan.Profile is null || plan.Inputs is null || plan.ProjectionArtifacts is null || plan.Generations is null ||
                plan.SettledEmbeddingRequestIds is null || plan.Generations.Count > 4096 ||
                plan.ManifestHash != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan with { ManifestHash = "" })))))
                Refuse(path, "index-recovery-rebuild-invalid");
            foreach (var generation in plan!.Generations)
            {
                if (generation is null || generation.Id == Guid.Empty || generation.IndexPath is null) Refuse(path, "index-recovery-rebuild-invalid");
                if (pins.Count >= 8192) Refuse(path, "index-recovery-audit-budget");
                pins.Add(new(Relative(path), generation!.Id, string.IsNullOrEmpty(generation.IndexPath) ? null : GenerationPath(generation.IndexPath, generation.Id, path)));
            }
            return plan.ManifestHash;
        }
        bool VersionOne(JsonElement value) => value.TryGetProperty("Version", out var version) &&
            version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == 1;
        void KnownFields(string path, JsonElement value, string[] allowed)
        {
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)) ||
                value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
                Refuse(path, "index-recovery-reference-metadata-unsupported");
        }
        void Predecessor(string path, JsonElement value)
        {
            KnownFields(path, value, ["ReleaseId", "OperationId", "TargetEpoch", "ManifestHash", "DatabaseServer", "DatabaseName",
                "OriginalHistory", "CandidateHash", "OperatorHash", "ActivatedConfigHash", "PreservedInputFingerprint",
                "JournalHash", "ManifestFileHash", "InteractiveHostWasEnabled"]);
            value.TryGetProperty("ReleaseId", out var release); value.TryGetProperty("ManifestHash", out var expected);
            if (release.ValueKind != JsonValueKind.String ||
                !Regex.IsMatch(release.GetString()!, @"^\d{8}T\d{6}Z-[0-9a-f]{12}-hybrid$", RegexOptions.CultureInvariant) ||
                expected.ValueKind != JsonValueKind.String)
                Refuse(path, "index-recovery-predecessor-incomplete");
            var manifestPath = Path.Combine(root, "IncrementalUpdates", release.GetString()!, "corpus-rebuild-manifest.json");
            if (!File.Exists(manifestPath)) Refuse(path, "index-recovery-predecessor-incomplete");
            // Every sibling packet is also inspected by the complete inventory. This
            // check prevents a binding from silently referring to an absent/different manifest.
            using var manifest = Metadata(manifestPath);
            if (!manifest!.RootElement.TryGetProperty("ManifestHash", out var actual) || actual.ValueKind != JsonValueKind.String ||
                actual.GetString() != expected.GetString()) Refuse(path, "index-recovery-predecessor-incomplete");
        }
        void SnapshotFile(string path, long expectedBytes)
        {
            Budget(path);
            try
            {
                using var file = new FileStream(PhysicalFileIdentity.OpenReadNoFollow(path), FileAccess.Read);
                if (file.Length != expectedBytes) Refuse(path, "index-recovery-snapshot-incomplete");
                // Only establish self-containment here. Restore verifies the saved SHA-256 before using these bytes.
                inventory.Add(Relative(path) + "|snapshot|" + PhysicalFileIdentity.Get(file.SafeFileHandle) + "|" + file.Length);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Refuse(path, "index-recovery-snapshot-incomplete"); }
        }
        void RetentionPacket(string path)
        {
            packets++; DirectoryProof(path);
            var manifestPath = Path.Combine(path, "manifest.json");
            using var document = Metadata(manifestPath);
            IndexRetentionManifest? manifest;
            try
            {
                manifest = document!.RootElement.Deserialize<IndexRetentionManifest>(StrictJson);
                if (manifest is null) Refuse(manifestPath, "index-recovery-retention-invalid");
                SqlIndexRetentionOperations.ValidateSnapshotManifest(manifest, index);
            }
            catch (Exception exception) when (exception is JsonException or NativeOperationException)
            { Refuse(manifestPath, "index-recovery-retention-invalid"); return; }
            if (!Guid.TryParseExact(Path.GetFileName(path), "N", out var id) || id != manifest!.ManifestId)
                Refuse(manifestPath, "index-recovery-retention-invalid");
            var expected = manifest.Entries.ToDictionary(value => value.Generation.Id.ToString("N"), StringComparer.Ordinal);
            foreach (var child in Children(path))
                if (Path.GetFileName(child) != "manifest.json" && !expected.ContainsKey(Path.GetFileName(child))) Refuse(child);
            foreach (var entry in manifest.Entries)
            {
                var directory = Path.Combine(path, entry.Generation.Id.ToString("N")); DirectoryProof(directory);
                SnapshotFile(Path.Combine(directory, "members.bin"), checked(entry.MemberCount * sizeof(long)));
                foreach (var file in entry.Files) SnapshotFile(Path.Combine(directory, file.Name), file.Bytes);
                foreach (var child in Children(directory))
                {
                    var name = Path.GetFileName(child);
                    if (name == "members.bin" || entry.Files.Any(file => file.Name == name)) continue;
                    if (!name.EndsWith(".journal.json", StringComparison.Ordinal)) Refuse(child);
                    if (!Guid.TryParseExact(name[..^13], "N", out var operation)) Refuse(child);
                    using var journalDocument = Metadata(child);
                    RetentionJournal? journal;
                    try { journal = journalDocument!.RootElement.Deserialize<RetentionJournal>(StrictJson); }
                    catch (JsonException) { Refuse(child, "index-recovery-retention-journal-invalid"); return; }
                    var names = entry.Files.Select(file => file.Name).Order(StringComparer.Ordinal).ToArray();
                    if (journal is null || journal.OperationId != operation || journal.GenerationId != entry.Generation.Id ||
                        journal.ManifestHash != SqlIndexRetentionOperations.ManifestHash(manifest) || !journal.DirectoryCompleted ||
                        journal.Started is null || journal.Completed is null ||
                        !journal.Started.Order(StringComparer.Ordinal).SequenceEqual(names) ||
                        !journal.Completed.Order(StringComparer.Ordinal).SequenceEqual(names) || (!journal.Restore && !journal.DirectoryStarted))
                        Refuse(child, "index-recovery-operation-unfinished");
                }
            }
        }
        void ApplicationPacket(string path)
        {
            if (!Regex.IsMatch(Path.GetFileName(path), @"^\d{8}T\d{6}Z-[0-9a-f]{12}(?:-hybrid|-hybridpatch|-repositoryrecovery)?$", RegexOptions.CultureInvariant)) Refuse(path);
            packets++; DirectoryProof(path);
            string? rebuildHash = null;
            foreach (var child in Children(path))
            {
                var name = Path.GetFileName(child);
                if (Payloads.Contains(name)) { Payload(child); continue; }
                if (name is not ("manifest.json" or "corpus-rebuild-manifest.json" or "hybrid-state.json" or "repository-recovery-receipt.json") &&
                    !NonPinningFiles.Contains(name)) Refuse(child);
                using var document = Metadata(child);
                if (document is null) continue;
                var value = document.RootElement;
                if (name == "corpus-rebuild-manifest.json") rebuildHash = Rebuild(child, value);
                if (name == "manifest.json")
                {
                    var expected = new[] { "commit", "staged_at_utc", "application_root", "migrations", "clean_slate", "reviewed_legacy_index_payloads" };
                    if (value.EnumerateObject().Any(property => !expected.Contains(property.Name, StringComparer.Ordinal)) ||
                        !value.TryGetProperty("commit", out var commit) || commit.ValueKind != JsonValueKind.String ||
                        !Regex.IsMatch(commit.GetString()!, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant) ||
                        !value.TryGetProperty("application_root", out var app) || app.ValueKind != JsonValueKind.String || !Path.IsPathFullyQualified(app.GetString()!) ||
                        !value.TryGetProperty("staged_at_utc", out var time) || time.ValueKind != JsonValueKind.String || !time.TryGetDateTimeOffset(out _) ||
                        !value.TryGetProperty("migrations", out var migrations) || migrations.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                        !value.TryGetProperty("clean_slate", out var clean) || clean.ValueKind != JsonValueKind.False)
                        Refuse(child, "index-recovery-application-manifest-invalid");
                }
                if (name == "repository-recovery-receipt.json" &&
                    (!value.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
                     !value.TryGetProperty("Phase", out var phase) || phase.ValueKind != JsonValueKind.String || phase.GetString() != "Completed"))
                    Refuse(child, "index-recovery-operation-unfinished");
                if (name == "hybrid-state.json")
                {
                    KnownFields(child, value, ["Version", "ReleaseId", "Commit", "OperationId", "DatabaseServer", "DatabaseName",
                        "OriginalHistory", "InitialHistory", "SchemaAttempted", "ResetCommitted", "HoldReleased", "HoldReleaseAttempted",
                        "ResetManifestHash", "CandidateHash", "OperatorHash", "OriginalConfigHash", "ActivatedConfigHash",
                        "PreservedInputFingerprint", "InteractiveHostWasEnabled", "Replacement", "ReviewedLegacyIndexPayloads"]);
                    value.TryGetProperty("ReleaseId", out var release); value.TryGetProperty("ResetCommitted", out var committed);
                    if (!VersionOne(value) || release.ValueKind != JsonValueKind.String ||
                        release.GetString() != Path.GetFileName(path) ||
                        committed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Refuse(child, "index-recovery-operation-unfinished");
                    var hasHash = value.TryGetProperty("ResetManifestHash", out var expected);
                    if ((committed.ValueKind == JsonValueKind.True || hasHash) &&
                        (rebuildHash is null || !hasHash || expected.ValueKind != JsonValueKind.String || expected.GetString() != rebuildHash))
                        Refuse(child, "index-recovery-operation-unfinished");
                    if (value.TryGetProperty("Replacement", out var previous)) Predecessor(child, previous);
                }
                if (name == "patch-binding.json")
                {
                    KnownFields(child, value, ["Version", "ReleaseId", "Commit", "OperationId", "TargetEpoch", "ManifestHash", "Predecessor",
                        "CandidateHash", "OperatorHash", "ConfigurationHash", "PreservedInputFingerprint", "ReviewedLegacyIndexPayloads"]);
                    var hasPredecessor = value.TryGetProperty("Predecessor", out var previous);
                    if (!VersionOne(value) || !hasPredecessor) Refuse(child, "index-recovery-predecessor-incomplete");
                    Predecessor(child, previous);
                }
            }
        }
        try
        {
            var ancestors = new Stack<string>();
            for (var ancestor = root; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor)) ancestors.Push(ancestor);
            while (ancestors.TryPop(out var ancestor)) held.Add(PhysicalFileIdentity.OpenDirectoryLease(ancestor));
            DirectoryProof(root);
            foreach (var category in Children(root))
            {
                var name = Path.GetFileName(category); DirectoryProof(category);
                if (name == "IncrementalUpdates") { foreach (var packet in Children(category)) ApplicationPacket(packet); continue; }
                if (name == "IndexRetention") { foreach (var packet in Children(category)) RetentionPacket(packet); continue; }
                var allowed = Regex.IsMatch(name, @"^HookUtf8-\d{8}T\d{6}Z$", RegexOptions.CultureInvariant)
                    ? new[] { "adapter-0.ps1", "adapter-1.ps1" }
                    : Regex.IsMatch(name, @"^OcrEnablement-\d{8}$", RegexOptions.CultureInvariant)
                        ? ["acl-before.xml", "appsettings.Production.before.json", "pool-before.xml"] : [];
                if (allowed.Length == 0) Refuse(category);
                packets++;
                foreach (var file in Children(category))
                {
                    if (!allowed.Contains(Path.GetFileName(file), StringComparer.Ordinal)) Refuse(file);
                    using var ignored = Metadata(file);
                }
            }
            return new(root, index, PhysicalFileIdentity.GetDirectory(root).IdentityFingerprint,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', inventory.Order(StringComparer.Ordinal))))),
                pins.OrderBy(value => value.Packet, StringComparer.Ordinal).ThenBy(value => value.GenerationId).ToArray(), packets);
        }
        finally { for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose(); }
    }
}
