using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed record IndexRetentionStorage(string IndexRoot, string RecoveryRoot,
    Func<string, string?> CanonicalGenerationPath, Func<string, bool> IsSafeIndexDirectory,
    Action<string>? ValidateOwnedPath = null);
public sealed record IndexRetentionFile(string Name, long Bytes, string Sha256);
public sealed record IndexRetentionEntry(IndexGenerationEntity Generation, long MemberCount,
    string MemberSha256, string VectorBindingSha256, IReadOnlyList<IndexRetentionFile> Files);
public sealed record IndexRetentionManifest(int SchemaVersion, Guid ManifestId, Guid DatabaseIncarnation,
    IReadOnlyList<Guid> ProtectedGenerationIds, IReadOnlyList<IndexRetentionEntry> Entries,
    long MaximumMembersPerUnit, int UnitDeadlineSeconds = 20);

/// <summary>One exact historical generation per existing native receipt. No automatic pruning policy.</summary>
public sealed class SqlIndexRetentionOperations(IDbContextFactory<FluxKnowledgeDbContext> factory,
    IndexRetentionStorage storage, IGpuInteractiveOwnerProbe? queryOwnerProbe = null)
{
    internal Action<string>? FailureInjector { get; set; }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record Payload(Guid ManifestId, string ManifestHash, Guid GenerationId, Guid? DeletionReceiptId);
    private sealed record Members(long Count, string Sha256, string VectorBinding);
    private sealed record FileJournal(Guid OperationId, string ManifestHash, Guid GenerationId, bool Restore,
        List<string> Started, List<string> Completed, bool DirectoryStarted = false, bool DirectoryCompleted = false);

    public static string ManifestHash(IndexRetentionManifest manifest) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest)));

    internal static void ParsePayload(string json, bool restore) => _ = PayloadFrom(json, restore);
    private static Payload PayloadFrom(string json, bool restore)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(value => value.Name is not
                    ("manifestId" or "manifestHash" or "generationId" or "deletionReceiptId"))) Refuse("invalid-payload");
            var id = root.GetProperty("manifestId").GetGuid();
            var generation = root.GetProperty("generationId").GetGuid();
            var hash = root.GetProperty("manifestHash").GetString()!;
            Guid? deletion = root.TryGetProperty("deletionReceiptId", out var value) && value.ValueKind != JsonValueKind.Null ? value.GetGuid() : null;
            if (id == Guid.Empty || generation == Guid.Empty || hash is not { Length: 64 } || hash.Any(value => !Uri.IsHexDigit(value)) ||
                restore != deletion.HasValue || deletion == Guid.Empty) Refuse("invalid-payload");
            return new(id, hash, generation, deletion);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or InvalidOperationException && error is not NativeOperationException)
        { throw new NativeOperationException("invalid-payload"); }
    }

    // Read-only plan output stays outside live storage. Member IDs are streamed to
    // ordered binary child artifacts, never flattened into the parent JSON or RAM.
    public async Task<IndexRetentionManifest> PrepareAsync(Guid manifestId, IReadOnlyList<Guid> ids,
        IReadOnlyList<Guid> protectedIds, string outputDirectory, CancellationToken ct)
    {
        if (manifestId == Guid.Empty || ids.Count == 0 || ids.Distinct().Count() != ids.Count || ids.Intersect(protectedIds).Any()) Refuse("index-retention-plan-invalid");
        SafeComponents(outputDirectory);
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any()) Refuse("index-retention-plan-exists");
        Directory.CreateDirectory(outputDirectory);
        var entries = new List<IndexRetentionEntry>();
        Guid database;
        await using (var db = await NewOperatorContextAsync(ct).ConfigureAwait(false))
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            database = await DatabaseIdentityAsync(db, ct).ConfigureAwait(false);
            foreach (var id in ids)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
                var generation = await db.IndexGenerations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, ct).ConfigureAwait(false)
                    ?? throw new NativeOperationException("target-not-found");
                await CheckClosureAsync(db, generation, manifestId, ct).ConfigureAwait(false);
                var members = await ReadMembersAsync(db, id, Path.Combine(outputDirectory, id.ToString("N") + ".members.bin"), ct).ConfigureAwait(false);
                if (members.Count != generation.VectorCount || members.Count == 0) Refuse("index-retention-membership-invalid");
                var files = await DescribeFilesAsync(generation.IndexPath, ct).ConfigureAwait(false);
                entries.Add(new(generation, members.Count, members.Sha256, members.VectorBinding, files));
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        var manifest = new IndexRetentionManifest(1, manifestId, database, protectedIds, entries, entries.Max(value => value.MemberCount));
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions), ct).ConfigureAwait(false);
        return manifest;
    }

    // Explicit local operator staging; execution approval is separate from a plan.
    // Existing verified snapshots are reused. Original generation files are never moved.
    public async Task StageAsync(IndexRetentionManifest manifest, string planDirectory, CancellationToken ct)
    {
        ValidateManifest(manifest);
        var root = SnapshotRoot(manifest.ManifestId);
        Directory.CreateDirectory(root);
        foreach (var entry in manifest.Entries)
        {
            await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            if (await DatabaseIdentityAsync(db, ct).ConfigureAwait(false) != manifest.DatabaseIncarnation) Refuse("index-retention-database-changed");
            await CheckEntryAsync(db, manifest, entry, ct).ConfigureAwait(false);
            var directory = SnapshotEntry(manifest.ManifestId, entry.Generation.Id);
            Directory.CreateDirectory(directory);
            await CopyVerifiedAsync(Path.Combine(planDirectory, entry.Generation.Id.ToString("N") + ".members.bin"),
                Path.Combine(directory, "members.bin"), entry.MemberCount * sizeof(long), entry.MemberSha256, ct).ConfigureAwait(false);
            foreach (var file in entry.Files)
                await CopyVerifiedAsync(Path.Combine(entry.Generation.IndexPath, file.Name), Path.Combine(directory, file.Name), file.Bytes, file.Sha256, ct).ConfigureAwait(false);
            FailureInjector?.Invoke("snapshot-copied");
        }
        var path = Path.Combine(root, "manifest.json");
        if (File.Exists(path))
        {
            var existing = await ReadManifestFileAsync(path, ct).ConfigureAwait(false);
            if (ManifestHash(existing) != ManifestHash(manifest)) Refuse("index-retention-snapshot-conflict");
        }
        else await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, JsonOptions), ct).ConfigureAwait(false);
    }

    internal async Task AcquireOwnershipAsync(FluxKnowledgeDbContext db, CancellationToken ct)
    {
        // Match existing cleanup order: Exclusive recovery BEFORE publication fence.
        if (!await SqlCorpusGenerationLeaseStore.TryAcquireCleanupTransactionAsync(db, queryOwnerProbe, ct).ConfigureAwait(false))
            Refuse("index-retention-owner-not-drained");
    }

    public async ValueTask<IReadOnlyList<NativeTargetVersion>> ResolveTargetsAsync(string action, string json, CancellationToken ct)
    {
        var payload = PayloadFrom(json, action == "index_retention_restore");
        var (manifest, entry) = await ReadBoundAsync(payload, ct).ConfigureAwait(false);
        await using var db = await NewOperatorContextAsync(ct).ConfigureAwait(false);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await TargetsAsync(db, action, payload, manifest, entry, ct).ConfigureAwait(false);
    }

    internal async Task ApplySqlAsync(FluxKnowledgeDbContext db, string action, string json,
        IReadOnlyList<NativeTargetVersion> expectedTargets, CancellationToken ct)
    {
        var payload = PayloadFrom(json, action == "index_retention_restore");
        var (manifest, entry) = await ReadBoundAsync(payload, ct).ConfigureAwait(false);
        var targets = await TargetsAsync(db, action, payload, manifest, entry, ct).ConfigureAwait(false);
        if (NativeOperationCanonicalization.SerializeTargets(NativeOperationCanonicalization.CanonicalizeTargets(targets)) !=
            NativeOperationCanonicalization.SerializeTargets(expectedTargets)) Refuse("operation-fenced");
        FailureInjector?.Invoke("before-sql");
        if (action == "index_retention")
        {
            var removed = await db.IndexGenerationVectors.Where(value => value.GenerationId == entry.Generation.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            if (removed != entry.MemberCount || await db.IndexGenerations.Where(value => value.Id == entry.Generation.Id &&
                value.RowVersion == entry.Generation.RowVersion).ExecuteDeleteAsync(ct).ConfigureAwait(false) != 1) Refuse("operation-fenced");
        }
        else
        {
            var row = JsonSerializer.Deserialize<IndexGenerationEntity>(JsonSerializer.Serialize(entry.Generation))!;
            row.RowVersion = [];
            db.IndexGenerations.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await RestoreMembersAsync(db, manifest, entry, ct).ConfigureAwait(false);
            db.AuditEvents.Add(new() { EventType = "index_retention.sql_restored", EventFamily = "index", Severity = "information",
                Actor = "native-retention", CorrelationId = "index-retention:" + payload.DeletionReceiptId!.Value.ToString("D"),
                DetailsJson = JsonSerializer.Serialize(new { payload.ManifestId, payload.GenerationId, payload.ManifestHash }), OccurredAtUtc = DateTimeOffset.UtcNow });
        }
        FailureInjector?.Invoke("after-sql");
    }

    private async Task<IReadOnlyList<NativeTargetVersion>> TargetsAsync(FluxKnowledgeDbContext db, string action, Payload payload,
        IndexRetentionManifest manifest, IndexRetentionEntry entry, CancellationToken ct)
    {
        if (await DatabaseIdentityAsync(db, ct).ConfigureAwait(false) != manifest.DatabaseIncarnation) Refuse("index-retention-database-changed");
        await VerifySnapshotAsync(manifest, entry, ct).ConfigureAwait(false);
        var targets = new List<NativeTargetVersion> { new($"index-retention-manifest:{manifest.ManifestId:D}", payload.ManifestHash) };
        if (action == "index_retention")
        {
            await CheckEntryAsync(db, manifest, entry, ct).ConfigureAwait(false);
            targets.Add(new($"index-retention-generation:{entry.Generation.Id:D}", Convert.ToBase64String(entry.Generation.RowVersion)));
        }
        else
        {
            var deletion = await db.NativeOperationReceipts.AsNoTracking().SingleOrDefaultAsync(value => value.OperationId == payload.DeletionReceiptId && value.Action == "index_retention", ct).ConfigureAwait(false)
                ?? throw new NativeOperationException("index-retention-deletion-receipt-invalid");
            await AuthenticateReceiptAsync(db, "index_retention", payload with { DeletionReceiptId = null }, deletion.OperationId, ct).ConfigureAwait(false);
            if (await db.IndexGenerations.AnyAsync(value => value.Id == entry.Generation.Id, ct).ConfigureAwait(false)) Refuse("index-retention-restore-conflict");
            await CheckClosureAsync(db, entry.Generation, manifest.ManifestId, ct).ConfigureAwait(false);
            await VerifySavedVectorsAsync(db, manifest, entry, ct).ConfigureAwait(false);
            await VerifyRestorePathsAsync(entry, ct).ConfigureAwait(false);
            targets.Add(new($"index-retention-generation:{entry.Generation.Id:D}", "absent"));
            targets.Add(new($"index-retention-deletion:{deletion.OperationId:D}", deletion.RequestFingerprint));
        }
        return targets;
    }

    // Runs AFTER the retryable SQL transaction, including authenticated durable replay.
    // Its own transaction is deliberately not wrapped in an execution strategy.
    public async ValueTask<NativeActionReceipt> FinalizeAsync(string action, string json, NativeActionReceipt receipt, CancellationToken ct)
    {
        var restore = action == "index_retention_restore";
        var payload = PayloadFrom(json, restore);
        var (manifest, entry) = await ReadBoundAsync(payload, ct).ConfigureAwait(false);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(manifest.UnitDeadlineSeconds));
        ct = budget.Token;
        await using var db = await NewOperatorContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        await AcquireOwnershipAsync(db, ct).ConfigureAwait(false);
        await SqlPublishedPassageSelection.AcquireFenceAsync(db, ct).ConfigureAwait(false);
        await AuthenticateReceiptAsync(db, action, payload, receipt.OperationId, ct).ConfigureAwait(false);
        if (await DatabaseIdentityAsync(db, ct).ConfigureAwait(false) != manifest.DatabaseIncarnation) Refuse("index-retention-database-changed");
        await VerifySnapshotAsync(manifest, entry, ct).ConfigureAwait(false);
        var originalCorrelation = "index-retention:" + receipt.OperationId.ToString("D");
        if (!restore && await db.AuditEvents.AnyAsync(value => value.EventType == "index_retention.sql_restored" && value.CorrelationId == originalCorrelation, ct).ConfigureAwait(false))
        {
            // The SQL restoration marker revokes the old deletion. It does not
            // prove that the separately journalled file restoration completed.
            var restored = await db.NativeOperationReceipts.Where(value => value.Action == "index_retention_restore")
                .Join(db.NativeOperationIntents, value => value.IntentId, value => value.Id, (value, intent) => new { Receipt = value, intent.TargetMetadataJson })
                .Where(value => value.TargetMetadataJson.Contains($"index-retention-deletion:{receipt.OperationId:D}"))
                .Select(value => value.Receipt.OperationId).ToArrayAsync(ct).ConfigureAwait(false);
            var correlations = restored.Select(value => "index-retention:" + value.ToString("D")).ToArray();
            var filesRestored = await db.AuditEvents.AnyAsync(value => value.EventType == "index_retention.files_completed" && correlations.Contains(value.CorrelationId!), ct).ConfigureAwait(false);
            return receipt with { Outcome = filesRestored ? "restored" : "restoration-files-pending", ReasonCode = "index-retention-restoration-started" };
        }
        await CheckClosureAsync(db, entry.Generation, manifest.ManifestId, ct).ConfigureAwait(false);
        if (restore)
        {
            var restored = await db.IndexGenerations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == entry.Generation.Id, ct).ConfigureAwait(false);
            if (restored is null || !SameRow(restored, entry.Generation, includeVersion: false)) Refuse("index-retention-restore-conflict");
            await CheckMembersAsync(db, entry, ct).ConfigureAwait(false);
        }
        else if (await db.IndexGenerations.AnyAsync(value => value.Id == entry.Generation.Id, ct).ConfigureAwait(false) ||
                 await db.IndexGenerationVectors.AnyAsync(value => value.GenerationId == entry.Generation.Id, ct).ConfigureAwait(false)) Refuse("index-retention-sql-not-completed");
        var completed = await db.AuditEvents.AnyAsync(value => value.EventType == "index_retention.files_completed" && value.CorrelationId == originalCorrelation, ct).ConfigureAwait(false);
        if (completed)
        {
            if (restore) await VerifyRestorePathsAsync(entry, ct, requireAll: true).ConfigureAwait(false);
            else if (Directory.Exists(entry.Generation.IndexPath) || File.Exists(entry.Generation.IndexPath)) Refuse("index-retention-finalized-state-changed");
            return receipt with { Outcome = "completed" };
        }
        FailureInjector?.Invoke("before-files");
        await FinalizeFilesAsync(manifest, entry, receipt.OperationId, payload.ManifestHash, restore, db, ct).ConfigureAwait(false);
        db.AuditEvents.Add(new() { EventType = "index_retention.files_completed", EventFamily = "index", Severity = "information", Actor = "native-retention",
            CorrelationId = originalCorrelation, DetailsJson = JsonSerializer.Serialize(new { payload.ManifestId, payload.GenerationId, restore }), OccurredAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        FailureInjector?.Invoke("after-file-receipt");
        return receipt with { Outcome = "completed" };
    }

    private async Task AuthenticateReceiptAsync(FluxKnowledgeDbContext db, string action, Payload payload, Guid operationId, CancellationToken ct)
    {
        var receipt = await db.NativeOperationReceipts.AsNoTracking().SingleOrDefaultAsync(value => value.OperationId == operationId && value.Action == action && value.Outcome == "sql-completed-files-pending", ct).ConfigureAwait(false)
            ?? throw new NativeOperationException("index-retention-receipt-invalid");
        var intent = await db.NativeOperationIntents.AsNoTracking().SingleAsync(value => value.Id == receipt.IntentId, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { manifestId = payload.ManifestId, manifestHash = payload.ManifestHash,
            generationId = payload.GenerationId, deletionReceiptId = payload.DeletionReceiptId }));
        var canonical = NativeOperationCanonicalization.CanonicalizeJson(document.RootElement.GetRawText());
        var targets = JsonSerializer.Deserialize<NativeTargetVersion[]>(intent.TargetMetadataJson)!;
        var matches = receipt.RequestFingerprint == NativeOperationCanonicalization.CreateRequestFingerprint(action, canonical, targets);
        if (!matches && payload.DeletionReceiptId is null)
        {
            var withoutNull = NativeOperationCanonicalization.CanonicalizeJson(JsonSerializer.Serialize(new { manifestId = payload.ManifestId,
                manifestHash = payload.ManifestHash, generationId = payload.GenerationId }));
            matches = receipt.RequestFingerprint == NativeOperationCanonicalization.CreateRequestFingerprint(action, withoutNull, targets);
        }
        if (intent.Action != action || !matches) Refuse("index-retention-receipt-invalid");
    }

    private async Task CheckEntryAsync(FluxKnowledgeDbContext db, IndexRetentionManifest manifest, IndexRetentionEntry entry, CancellationToken ct)
    {
        var current = await db.IndexGenerations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == entry.Generation.Id, ct).ConfigureAwait(false);
        if (current is null || !SameRow(current, entry.Generation, includeVersion: true)) Refuse("operation-fenced");
        await CheckClosureAsync(db, current, manifest.ManifestId, ct).ConfigureAwait(false);
        await CheckMembersAsync(db, entry, ct).ConfigureAwait(false);
        var actual = await DescribeFilesAsync(current.IndexPath, ct).ConfigureAwait(false);
        if (JsonSerializer.Serialize(actual) != JsonSerializer.Serialize(entry.Files)) Refuse("index-retention-files-changed");
    }
    private async Task CheckMembersAsync(FluxKnowledgeDbContext db, IndexRetentionEntry entry, CancellationToken ct)
    {
        var members = await ReadMembersAsync(db, entry.Generation.Id, null, ct).ConfigureAwait(false);
        if (members.Count != entry.MemberCount || members.Sha256 != entry.MemberSha256 || members.VectorBinding != entry.VectorBindingSha256) Refuse("index-retention-membership-changed");
    }

    private async Task CheckClosureAsync(FluxKnowledgeDbContext db, IndexGenerationEntity generation, Guid manifestId, CancellationToken ct)
    {
        var id = generation.Id;
        var path = Canonical(generation.IndexPath);
        if (generation.EmbeddingJobId is not null || generation.ValidatedAtUtc is null || generation.RowVersion.Length != 8 ||
            await db.IndexState.AnyAsync(value => value.ActiveIndexGenerationId == id || value.CorpusRebuildOperationId != null, ct).ConfigureAwait(false) ||
            await db.Vectors.AnyAsync(value => value.IndexGenerationId == id, ct).ConfigureAwait(false) ||
            await db.CorpusQueryLeases.AnyAsync(value => value.GenerationId == id, ct).ConfigureAwait(false) ||
            await db.EmbeddingGpuRequests.AnyAsync(value => value.GenerationId == id, ct).ConfigureAwait(false) ||
            await db.Artifacts.AnyAsync(value => value.SearchText == id.ToString("D") || value.SearchText == id.ToString("N") || value.SearchText == path, ct).ConfigureAwait(false) ||
            await db.SourceDeletionCleanupItems.AnyAsync(value => value.StorageKind == 2 && value.RelativePath == id.ToString("N"), ct).ConfigureAwait(false)) Refuse("index-retention-referenced");
        await using (var command = Command(db, """
            SELECT COUNT_BIG(*) FROM CorpusRebuildOperations o
            CROSS APPLY OPENJSON(o.ManifestJson,'$.Generations') WITH(Id uniqueidentifier '$.Id') r WHERE r.Id=@id;
            """))
        {
            command.Parameters.Add(new SqlParameter("@id", id));
            if (Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0) Refuse("index-retention-referenced");
        }
        var self = $"index-retention-manifest:{manifestId:D}";
        if (await db.NativeOperationIntents.AnyAsync(value => (value.TargetMetadataJson.Contains(id.ToString("D")) || value.TargetMetadataJson.Contains(id.ToString("N"))) &&
            !((value.Action == "index_retention" || value.Action == "index_retention_restore") && value.TargetMetadataJson.Contains(self)), ct).ConfigureAwait(false)) Refuse("index-retention-recovery-reference");
        await using (var command = Command(db, "SELECT Id,IndexPath FROM IndexGenerations WHERE IndexPath<>N'' ORDER BY Id"))
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var otherId = reader.GetGuid(0); var other = reader.GetString(1);
                if (otherId == id) continue;
                var canonical = Canonical(other);
                if (Overlaps(path, canonical)) Refuse("index-retention-path-shared");
            }
        }
        await CheckRecoveryReferencesAsync(manifestId, id, ct).ConfigureAwait(false);
    }

    private async Task CheckRecoveryReferencesAsync(Guid manifestId, Guid generationId, CancellationToken ct)
    {
        var own = SnapshotRoot(manifestId);
        Owned(storage.RecoveryRoot);
        foreach (var file in RecoveryJsonFiles(storage.RecoveryRoot))
        {
            if (IsUnder(file, own)) continue;
            Owned(file);
            await using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (Path.GetFileName(file) == "repository-recovery-receipt.json" &&
                (!doc.RootElement.TryGetProperty("Phase", out var phase) || phase.GetString() != "Completed")) Refuse("index-retention-release-not-completed");
            if (References(doc.RootElement, generationId)) Refuse("index-retention-recovery-reference");
        }
    }
    private IEnumerable<string> RecoveryJsonFiles(string root)
    {
        var directories = new Stack<string>(); directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            Owned(directory);
            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)) { Owned(file); yield return file; }
            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)) { Owned(child); directories.Push(child); }
        }
    }
    private static bool References(JsonElement value, Guid id) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(property => References(property.Value, id)),
        JsonValueKind.Array => value.EnumerateArray().Any(item => References(item, id)),
        JsonValueKind.String => value.GetString() is { } text && (text.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase) || text.Contains(id.ToString("N"), StringComparison.OrdinalIgnoreCase)),
        _ => false
    };

    private async Task<Members> ReadMembersAsync(FluxKnowledgeDbContext db, Guid id, string? outputFile, CancellationToken ct)
    {
        await using var command = Command(db, """
            SELECT m.VectorId,v.TextChunkId,v.ModelFingerprint,v.Dimensions,v.PayloadChecksum,v.SourceRevision,v.IsDeleted,v.SearchInputHash,v.RowVersion
            FROM IndexGenerationVectors m JOIN Vectors v ON v.VectorId=m.VectorId WHERE m.GenerationId=@id ORDER BY m.VectorId;
            """);
        command.Parameters.Add(new SqlParameter("@id", id));
        await using var output = outputFile is null ? null : new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var members = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var vectors = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        var bytes = new byte[sizeof(long)]; long count = 0, previous = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var vectorId = reader.GetInt64(0);
            if (vectorId <= previous) Refuse("index-retention-membership-invalid");
            previous = vectorId; count++;
            BinaryPrimitives.WriteInt64LittleEndian(bytes, vectorId); members.AppendData(bytes);
            if (output is not null) await output.WriteAsync(bytes, ct).ConfigureAwait(false);
            var text = FormattableString.Invariant($"{vectorId}:{reader.GetInt64(1)}:{reader.GetString(2)}:{reader.GetInt32(3)}:{reader.GetString(4)}:{reader.GetInt64(5)}:{reader.GetBoolean(6)}:{(reader.IsDBNull(7) ? "" : reader.GetString(7))}:{Convert.ToBase64String((byte[])reader.GetValue(8))}\n");
            vectors.AppendData(Encoding.UTF8.GetBytes(text));
        }
        return new(count, Convert.ToHexStringLower(members.GetHashAndReset()), Convert.ToHexStringLower(vectors.GetHashAndReset()));
    }

    private async Task RestoreMembersAsync(FluxKnowledgeDbContext db, IndexRetentionManifest manifest, IndexRetentionEntry entry, CancellationToken ct)
    {
        var path = Path.Combine(SnapshotEntry(manifest.ManifestId, entry.Generation.Id), "members.bin");
        await using var input = File.OpenRead(path);
        var bytes = new byte[sizeof(long)]; var batch = new List<long>(1000);
        while (input.Position < input.Length)
        {
            await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false); batch.Add(BinaryPrimitives.ReadInt64LittleEndian(bytes));
            if (batch.Count < 1000 && input.Position < input.Length) continue;
            await using var command = Command(db, "INSERT IndexGenerationVectors(GenerationId,VectorId) SELECT @id,CONVERT(bigint,[value]) FROM OPENJSON(@members)");
            command.Parameters.Add(new SqlParameter("@id", entry.Generation.Id));
            command.Parameters.Add(new SqlParameter("@members", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(batch) });
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != batch.Count) Refuse("index-retention-restore-conflict");
            batch.Clear();
        }
    }
    private async Task VerifySavedVectorsAsync(FluxKnowledgeDbContext db, IndexRetentionManifest manifest, IndexRetentionEntry entry, CancellationToken ct)
    {
        // A temporary SQL table joins streamed saved IDs to current canonical rows;
        // it is connection-local and is never a persistence or schema migration.
        await using (var create = Command(db, "CREATE TABLE #RetentionMembers(VectorId bigint NOT NULL PRIMARY KEY)"))
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        try
        {
            await using var input = File.OpenRead(Path.Combine(SnapshotEntry(manifest.ManifestId, entry.Generation.Id), "members.bin"));
            var bytes = new byte[sizeof(long)]; var batch = new List<long>(1000);
            while (input.Position < input.Length)
            {
                await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false); batch.Add(BinaryPrimitives.ReadInt64LittleEndian(bytes));
                if (batch.Count < 1000 && input.Position < input.Length) continue;
                await using var insert = Command(db, "INSERT #RetentionMembers SELECT CONVERT(bigint,[value]) FROM OPENJSON(@members)");
                insert.Parameters.Add(new SqlParameter("@members", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(batch) });
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false); batch.Clear();
            }
            // Reuse the same reader/checksum shape, changing only its member source.
            var current = await ReadSavedMemberBindingAsync(db, ct).ConfigureAwait(false);
            if (current.Count != entry.MemberCount || current.Sha256 != entry.MemberSha256 || current.VectorBinding != entry.VectorBindingSha256) Refuse("index-retention-canonical-vectors-changed");
        }
        finally
        {
            await using var drop = Command(db, "DROP TABLE #RetentionMembers");
            await drop.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<Members> ReadSavedMemberBindingAsync(FluxKnowledgeDbContext db, CancellationToken ct)
    {
        await using var command = Command(db, """
            SELECT m.VectorId,v.TextChunkId,v.ModelFingerprint,v.Dimensions,v.PayloadChecksum,v.SourceRevision,v.IsDeleted,v.SearchInputHash,v.RowVersion
            FROM #RetentionMembers m JOIN Vectors v ON v.VectorId=m.VectorId ORDER BY m.VectorId;
            """);
        using var members = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var vectors = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        var bytes = new byte[sizeof(long)]; long count = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0); count++; BinaryPrimitives.WriteInt64LittleEndian(bytes, id); members.AppendData(bytes);
            vectors.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{id}:{reader.GetInt64(1)}:{reader.GetString(2)}:{reader.GetInt32(3)}:{reader.GetString(4)}:{reader.GetInt64(5)}:{reader.GetBoolean(6)}:{(reader.IsDBNull(7) ? "" : reader.GetString(7))}:{Convert.ToBase64String((byte[])reader.GetValue(8))}\n")));
        }
        return new(count, Convert.ToHexStringLower(members.GetHashAndReset()), Convert.ToHexStringLower(vectors.GetHashAndReset()));
    }

    private async Task FinalizeFilesAsync(IndexRetentionManifest manifest, IndexRetentionEntry entry, Guid operationId, string manifestHash, bool restore,
        FluxKnowledgeDbContext db, CancellationToken ct)
    {
        var journalPath = Path.Combine(SnapshotEntry(manifest.ManifestId, entry.Generation.Id), operationId.ToString("N") + ".journal.json");
        Owned(journalPath);
        var journal = File.Exists(journalPath) ? JsonSerializer.Deserialize<FileJournal>(await File.ReadAllTextAsync(journalPath, ct).ConfigureAwait(false))! : new(operationId, manifestHash, entry.Generation.Id, restore, [], []);
        if (journal.OperationId != operationId || journal.ManifestHash != manifestHash || journal.GenerationId != entry.Generation.Id || journal.Restore != restore ||
            journal.Started.Any(name => !entry.Files.Any(file => file.Name == name)) || journal.Completed.Any(name => !journal.Started.Contains(name))) Refuse("index-retention-file-receipt-invalid");
        var directory = Canonical(entry.Generation.IndexPath);
        // After intent to remove the directory is durable, an existing directory
        // has an unknown identity. Never delete a possible replacement, even empty.
        if (!restore && journal.DirectoryStarted && Directory.Exists(directory)) Refuse("index-retention-file-outcome-unknown");
        if (!restore && !Directory.Exists(directory) && !journal.DirectoryStarted) Refuse("index-retention-file-outcome-unknown");
        if (Directory.Exists(directory))
        {
            if (!storage.IsSafeIndexDirectory(directory) || Directory.EnumerateDirectories(directory).Any() ||
                Directory.EnumerateFiles(directory).Any(path => !entry.Files.Any(file => file.Name == Path.GetFileName(path)))) Refuse("index-retention-files-changed");
        }
        else if (restore) { Directory.CreateDirectory(directory); Canonical(directory); }
        foreach (var file in entry.Files)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(directory, file.Name); Owned(target);
            if (!restore && journal.Completed.Contains(file.Name) && File.Exists(target)) Refuse("index-retention-finalized-state-changed");
            if (File.Exists(target)) await MatchFileAsync(target, file.Bytes, file.Sha256, ct).ConfigureAwait(false);
            else if (!restore && !journal.Started.Contains(file.Name)) Refuse("index-retention-file-outcome-unknown");
            if (!journal.Started.Contains(file.Name)) journal.Started.Add(file.Name);
            await SaveJournalAsync(journalPath, journal, ct).ConfigureAwait(false);
            await ProveOwnershipAsync(db, ct).ConfigureAwait(false);
            Canonical(directory); Owned(target);
            FailureInjector?.Invoke("before-file:" + file.Name);
            if (restore && !File.Exists(target))
            {
                var snapshot = Path.Combine(SnapshotEntry(manifest.ManifestId, entry.Generation.Id), file.Name);
                var temporary = Path.Combine(SnapshotEntry(manifest.ManifestId, entry.Generation.Id), operationId.ToString("N") + ".restore.tmp");
                Owned(temporary);
                File.Copy(snapshot, temporary, overwrite: true);
                await MatchFileAsync(temporary, file.Bytes, file.Sha256, ct).ConfigureAwait(false);
                Owned(target); Canonical(directory); await ProveOwnershipAsync(db, ct).ConfigureAwait(false);
                File.Move(temporary, target, overwrite: false);
            }
            else if (!restore && File.Exists(target)) File.Delete(target);
            FailureInjector?.Invoke("after-file:" + file.Name);
            if (!journal.Completed.Contains(file.Name)) journal.Completed.Add(file.Name);
            await SaveJournalAsync(journalPath, journal, ct).ConfigureAwait(false);
        }
        if (!restore)
        {
            journal = journal with { DirectoryStarted = true }; await SaveJournalAsync(journalPath, journal, ct).ConfigureAwait(false);
            await ProveOwnershipAsync(db, ct).ConfigureAwait(false); Canonical(directory);
            FailureInjector?.Invoke("before-directory");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
            FailureInjector?.Invoke("after-directory");
        }
        journal = journal with { DirectoryCompleted = true }; await SaveJournalAsync(journalPath, journal, ct).ConfigureAwait(false);
    }

    private async Task VerifyRestorePathsAsync(IndexRetentionEntry entry, CancellationToken ct, bool requireAll = false)
    {
        var directory = Canonical(entry.Generation.IndexPath);
        if (File.Exists(directory)) Refuse("index-retention-restore-conflict");
        if (!Directory.Exists(directory)) { if (requireAll) Refuse("index-retention-restore-conflict"); return; }
        if (!storage.IsSafeIndexDirectory(directory) || Directory.EnumerateDirectories(directory).Any() ||
            Directory.EnumerateFiles(directory).Any(path => !entry.Files.Any(file => file.Name == Path.GetFileName(path)))) Refuse("index-retention-restore-conflict");
        foreach (var file in entry.Files)
        {
            var path = Path.Combine(directory, file.Name);
            if (File.Exists(path)) await MatchFileAsync(path, file.Bytes, file.Sha256, ct).ConfigureAwait(false);
            else if (requireAll) Refuse("index-retention-restore-conflict");
        }
    }

    private async Task<(IndexRetentionManifest, IndexRetentionEntry)> ReadBoundAsync(Payload payload, CancellationToken ct)
    {
        var manifest = await ReadManifestFileAsync(Path.Combine(SnapshotRoot(payload.ManifestId), "manifest.json"), ct).ConfigureAwait(false);
        ValidateManifest(manifest);
        if (manifest.ManifestId != payload.ManifestId || ManifestHash(manifest) != payload.ManifestHash) Refuse("index-retention-manifest-changed");
        var entry = manifest.Entries.SingleOrDefault(value => value.Generation.Id == payload.GenerationId) ?? throw new NativeOperationException("index-retention-target-not-approved");
        if (manifest.ProtectedGenerationIds.Contains(payload.GenerationId)) Refuse("index-retention-protected");
        return (manifest, entry);
    }
    private static void ValidateManifest(IndexRetentionManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.ManifestId == Guid.Empty || manifest.DatabaseIncarnation == Guid.Empty ||
            manifest.UnitDeadlineSeconds != 20 || manifest.ProtectedGenerationIds is null || manifest.Entries is not { Count: > 0 } ||
            manifest.Entries.Any(value => value is null || value.Generation is null || value.Generation.Id == Guid.Empty) ||
            manifest.Entries.Select(value => value.Generation.Id).Distinct().Count() != manifest.Entries.Count ||
            manifest.MaximumMembersPerUnit != manifest.Entries.Max(value => value.MemberCount) || manifest.Entries.Any(value => value.MemberCount is <= 0 or > long.MaxValue / sizeof(long) ||
                value.MemberCount != value.Generation.VectorCount || value.MemberCount > manifest.MaximumMembersPerUnit ||
                !IsHash(value.MemberSha256) || !IsHash(value.VectorBindingSha256) || value.Files is not { Count: > 0 } ||
                value.Files.Any(file => file is null || string.IsNullOrWhiteSpace(file.Name) || file.Name != Path.GetFileName(file.Name) ||
                    file.Name is "." or ".." || file.Name.Equals("members.bin", StringComparison.OrdinalIgnoreCase) ||
                    file.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    file.Name.EndsWith(".journal.json", StringComparison.OrdinalIgnoreCase) || file.Bytes < 0 || !IsHash(file.Sha256)) ||
                value.Files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Files.Count)) Refuse("index-retention-manifest-invalid");
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private async Task VerifySnapshotAsync(IndexRetentionManifest manifest, IndexRetentionEntry entry, CancellationToken ct)
    {
        var directory = SnapshotEntry(manifest.ManifestId, entry.Generation.Id);
        await MatchFileAsync(Path.Combine(directory, "members.bin"), entry.MemberCount * sizeof(long), entry.MemberSha256, ct).ConfigureAwait(false);
        foreach (var file in entry.Files) await MatchFileAsync(Path.Combine(directory, file.Name), file.Bytes, file.Sha256, ct).ConfigureAwait(false);
    }
    private static bool SameRow(IndexGenerationEntity first, IndexGenerationEntity second, bool includeVersion) =>
        first.Id == second.Id && first.CorpusEpoch == second.CorpusEpoch && first.CorpusVersion == second.CorpusVersion &&
        first.EmbeddingJobId == second.EmbeddingJobId && first.ModelFingerprint == second.ModelFingerprint && first.Dimensions == second.Dimensions &&
        first.IndexPath == second.IndexPath && first.MetadataChecksum == second.MetadataChecksum && first.VectorCount == second.VectorCount &&
        first.CreatedAtUtc == second.CreatedAtUtc && first.ValidatedAtUtc == second.ValidatedAtUtc && first.RetiredAtUtc == second.RetiredAtUtc &&
        (!includeVersion || first.RowVersion.AsSpan().SequenceEqual(second.RowVersion));
    private async Task<IReadOnlyList<IndexRetentionFile>> DescribeFilesAsync(string directory, CancellationToken ct)
    {
        Canonical(directory);
        if (!storage.IsSafeIndexDirectory(directory) || Directory.EnumerateDirectories(directory).Any()) Refuse("index-retention-path-unsafe");
        var files = new List<IndexRetentionFile>();
        foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            Owned(path);
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            files.Add(new(Path.GetFileName(path), stream.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false))));
        }
        if (files.Count == 0) Refuse("index-retention-path-unsafe");
        return files;
    }
    private async Task CopyVerifiedAsync(string source, string target, long bytes, string hash, CancellationToken ct)
    {
        await MatchFileAsync(source, bytes, hash, ct).ConfigureAwait(false); Owned(target);
        if (File.Exists(target)) { await MatchFileAsync(target, bytes, hash, ct).ConfigureAwait(false); return; }
        var temporary = target + ".copy.tmp"; Owned(temporary);
        await using (var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var output = File.Open(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
        await MatchFileAsync(temporary, bytes, hash, ct).ConfigureAwait(false);
        Owned(target); File.Move(temporary, target, overwrite: false);
    }
    private async Task MatchFileAsync(string path, long bytes, string hash, CancellationToken ct)
    {
        SafeComponents(path);
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != bytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)) != hash) Refuse("index-retention-snapshot-or-file-changed");
    }
    private async Task<IndexRetentionManifest> ReadManifestFileAsync(string path, CancellationToken ct)
    {
        Owned(path);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<IndexRetentionManifest>(stream, cancellationToken: ct).ConfigureAwait(false)
            ?? throw new NativeOperationException("index-retention-manifest-invalid");
    }
    private async Task SaveJournalAsync(string path, FileJournal journal, CancellationToken ct)
    {
        Owned(path); var temporary = path + ".tmp"; Owned(temporary);
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(journal), ct).ConfigureAwait(false);
        Owned(path); File.Move(temporary, path, overwrite: true);
    }
    private static async Task<Guid> DatabaseIdentityAsync(FluxKnowledgeDbContext db, CancellationToken ct)
    {
        await using var command = Command(db, "SELECT database_guid FROM sys.database_recovery_status WHERE database_id=DB_ID()");
        return (Guid)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? throw new NativeOperationException("index-retention-database-unavailable"));
    }
    private static async Task ProveOwnershipAsync(FluxKnowledgeDbContext db, CancellationToken ct)
    {
        await using var command = Command(db, "SELECT APPLOCK_MODE('public',@resource,'Transaction')");
        command.Parameters.Add(new SqlParameter("@resource", SqlDerivedIndexRecoveryStore.LockResource));
        if ((string?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false) != "Exclusive") Refuse("index-retention-ownership-lost");
    }
    private static SqlCommand Command(FluxKnowledgeDbContext db, string text) => new(text,
        (SqlConnection)db.Database.GetDbConnection(), (SqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
    private async Task<FluxKnowledgeDbContext> NewOperatorContextAsync(CancellationToken ct)
    {
        await using var configured = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var connectionString = configured.Database.GetConnectionString() ?? throw new NativeOperationException("index-retention-database-unavailable");
        // Filesystem effects must never be replayed by an EF retry strategy.
        return new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options);
    }
    private string SnapshotRoot(Guid id) { var path = Path.Combine(storage.RecoveryRoot, "IndexRetention", id.ToString("N")); Owned(path); return path; }
    private string SnapshotEntry(Guid manifestId, Guid id) { var path = Path.Combine(SnapshotRoot(manifestId), id.ToString("N")); Owned(path); return path; }
    private string Canonical(string path)
    {
        var result = storage.CanonicalGenerationPath(path);
        if (result is null || !IsUnder(result, storage.IndexRoot)) Refuse("index-retention-path-unsafe");
        SafeComponents(result); storage.ValidateOwnedPath?.Invoke(result); return result;
    }
    private void Owned(string path) { SafeComponents(path); storage.ValidateOwnedPath?.Invoke(path); }
    private static void SafeComponents(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) Refuse("index-retention-path-unsafe");
    }
    private static bool IsUnder(string path, string root) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Overlaps(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase) || IsUnder(first, second) || IsUnder(second, first);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Refuse(string reason) => throw new NativeOperationException(reason);
}
