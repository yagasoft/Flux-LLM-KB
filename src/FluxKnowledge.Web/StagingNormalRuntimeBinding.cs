using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Web.Configuration;
using Microsoft.Data.SqlClient;

namespace FluxKnowledge.Web;

/// <summary>Normal services must never see the copied production execution ledger.</summary>
internal sealed partial record StagingNormalRuntimeBinding(string ReceiptPath, Guid Generation, long Version)
{
    internal const string Catalogue = "FluxKnowledge_Staging_ScopedSemantic_20261029";
    private const string Incarnation = "1ebf9b01-1c71-4ad4-96e7-bc51abf72383";
    internal static void ValidateReceipt(JsonElement receipt)
    {
        if (receipt.GetProperty("catalogue").GetString() != Catalogue || receipt.GetProperty("incarnation").GetString() != Incarnation ||
            !receipt.GetProperty("copiedMetadataOnly").GetBoolean() || receipt.GetProperty("nativeCleanupClaimedFromRowDeletion").GetBoolean() ||
            receipt.GetProperty("productionOperations").GetInt32() != 0 || receipt.GetProperty("operation").GetGuid() == Guid.Empty ||
            receipt.GetProperty("completedUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow ||
            receipt.GetProperty("planSha256").GetString()?.Length != 64 || receipt.GetProperty("backupReceiptSha256").GetString()?.Length != 64)
            throw new InvalidOperationException("exact-staging-reset-receipt-required");
    }
    internal static async Task<StagingNormalRuntimeBinding> LoadVerifiedAsync(StagingHostSettings settings, string path, CancellationToken ct,string? continuation=null)
    {
        path = Path.GetFullPath(path);
        const string owned = @"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\scoped-semantic-search-20261029\staging-expanded-gpu-api-20261110\normal-runtime-ledger-";
        if (!path.StartsWith(owned, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) != "reset-completed.json" || settings.CatalogName != Catalogue)
            throw new InvalidOperationException("owned-staging-reset-receipt-required");
        using var receipt = Read(path, 16384); ValidateReceipt(receipt.RootElement);
        var directory = Path.GetDirectoryName(path)!;
        var planPath = Path.Combine(directory, "plan.json");
        var backupReceipt = Path.Combine(directory, "backup-verified.json");
        using var plan = Read(planPath, 131072);
        using var backup = Read(backupReceipt, 16384);
        var r = receipt.RootElement; var p = plan.RootElement; var b = backup.RootElement;
        if (Hash(planPath) != r.GetProperty("planSha256").GetString() || Hash(backupReceipt) != r.GetProperty("backupReceiptSha256").GetString() ||
            p.GetProperty("operation").GetGuid() != r.GetProperty("operation").GetGuid() || b.GetProperty("operation").GetGuid() != r.GetProperty("operation").GetGuid() ||
            p.GetProperty("settingsSha256").GetString() != Hash(Path.Combine(settings.Layout.ConfigRoot, "staging.json")) ||
            p.GetProperty("canonicalSha256").GetString() != r.GetProperty("canonicalSha256").GetString())
            throw new InvalidOperationException("staging-reset-receipt-binding-changed");
        if(continuation is not null)return await VerifyContinuationAsync(settings,path,r,p,continuation,ct);
        var publication = p.GetProperty("canonical").GetProperty("Publication")[0];
        var generation = publication.GetProperty("Id").GetGuid(); var version = publication.GetProperty("CorpusVersion").GetInt64();
        await using var connection = new SqlConnection(settings.RuntimeConnectionString); await connection.OpenAsync(ct);
        await using var command = new SqlCommand("""
            SELECT DB_NAME(),database_guid FROM sys.database_recovery_status WHERE database_id=DB_ID();
            SELECT ActiveIndexGenerationId,CorpusVersion,CorpusRebuildOperationId FROM IndexState WHERE Id=1;
            SELECT (SELECT COUNT_BIG(*) FROM GpuMiniTasks)+(SELECT COUNT_BIG(*) FROM GpuBatches)+
                (SELECT COUNT_BIG(*) FROM GpuExecutorDispatches)+(SELECT COUNT_BIG(*) FROM GpuExecutorResultReceipts)+
                (SELECT COUNT_BIG(*) FROM GpuExecutorEvidence)+(SELECT COUNT_BIG(*) FROM GpuSchedulerOperationReceipts)+
                (SELECT COUNT_BIG(*) FROM EmbeddingGpuRequests)+(SELECT COUNT_BIG(*) FROM DocumentOcrRequests)+
                (SELECT COUNT_BIG(*) FROM NativeWorkerInstances)+(SELECT COUNT_BIG(*) FROM NativeWorkerLifecycleEvidence);
            SELECT SlotKey,State,ActiveBatchId,OwnerKey,LastHeartbeatAtUtc FROM GpuCapacitySlots;
            SELECT COUNT_BIG(*) FROM SourceRootConfigurations WHERE State<>1;
            """, connection) { CommandTimeout = 10 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetString(0) != Catalogue || reader.GetGuid(1) != Guid.Parse(Incarnation) ||
            !await reader.NextResultAsync(ct) || !await reader.ReadAsync(ct) || reader.GetGuid(0) != generation || reader.GetInt64(1) != version || !reader.IsDBNull(2) ||
            !await reader.NextResultAsync(ct) || !await reader.ReadAsync(ct) || reader.GetInt64(0) != 0 ||
            !await reader.NextResultAsync(ct) || !await reader.ReadAsync(ct) || reader.GetString(0) != "paddleocr-vl-local-gpu-0" || reader.GetInt32(1) != 0 ||
            !reader.IsDBNull(2) || !reader.IsDBNull(3) || !reader.IsDBNull(4) || await reader.ReadAsync(ct) ||
            !await reader.NextResultAsync(ct) || !await reader.ReadAsync(ct) || reader.GetInt64(0) != 0)
            throw new InvalidOperationException("fresh-empty-isolated-staging-execution-ledger-required");
        return new(path, generation, version);
    }
    private static JsonDocument Read(string path, long bound)
    {
        for (var part = Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("linked-staging-ledger-proof-refused");
        if (!File.Exists(path) || new FileInfo(path).Length > bound) throw new InvalidOperationException("staging-ledger-proof-bound");
        return JsonDocument.Parse(File.ReadAllBytes(path));
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
}
