using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Windows;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Cli.Commands;

/// <summary>Model-free maintenance entry point, called only by the reviewed incremental updater.</summary>
public static class OcrRecoveryCommand
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static async Task<int> ExecuteFromEnvironmentAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken token = default)
    {
        if (args.Length != 5 || args[0] != "apply" || args[1] != "--plan" || args[3] != "--sha256" ||
            !Path.IsPathFullyQualified(args[2]) || !IsHash(args[4]))
        {
            await error.WriteLineAsync("Usage: ocr-recovery apply --plan <reviewed-local-plan> --sha256 <SHA-256>. Requires stopped maintenance through the incremental updater.");
            return 2;
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var heldPlan = OpenRead(args[2]);
            if (heldPlan.Length > 1024 * 1024 || !Convert.ToHexString(SHA256.HashData(heldPlan)).Equals(args[4], StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("document-ocr-maintenance-plan-changed");
            heldPlan.Position = 0;
            var plan = await JsonSerializer.DeserializeAsync<OcrRecoveryPlan>(heldPlan, Json, budget.Token)
                ?? throw new InvalidOperationException("document-ocr-maintenance-plan-invalid");
            if (plan.Version != 1 || plan.Binding is null || string.IsNullOrWhiteSpace(plan.ReleaseId) ||
                !Path.IsPathFullyQualified(plan.OperationRoot)) throw new InvalidOperationException("document-ocr-maintenance-plan-invalid");
            using var hold = OpenRead(plan.HoldPath);
            if (hold.Length > 1024 || await JsonSerializer.DeserializeAsync<string>(hold, cancellationToken: budget.Token) != plan.ReleaseId)
                throw new InvalidOperationException("document-ocr-maintenance-hold-changed");
            using var configuration = OpenRead(plan.ConfigurationPath);
            if (!Convert.ToHexString(SHA256.HashData(configuration)).Equals(plan.ConfigurationSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("document-ocr-maintenance-configuration-changed");
            using var verifierSource = OpenRead(plan.VerifierScriptPath);
            if (!Convert.ToHexString(SHA256.HashData(verifierSource)).Equals(plan.VerifierScriptSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("document-ocr-maintenance-verifier-changed");
            var connection = Environment.GetEnvironmentVariable("ConnectionStrings__FluxKnowledge");
            if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("document-ocr-maintenance-database-unavailable");
            var binding = new SqlConnectionStringBuilder(connection) { ConnectRetryCount = 0, ConnectTimeout = 5 };
            var factory = new Factory(binding.ConnectionString);
            await using (var db = factory.CreateDbContext())
            {
                var actual = await db.Database.SqlQueryRaw<string>("SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')) + '|' + DB_NAME() + '|' + CONVERT(nvarchar(36), database_guid) AS [Value] FROM sys.database_recovery_status WHERE database_id=DB_ID()")
                    .SingleAsync(budget.Token);
                if (!string.Equals(actual, plan.DatabaseIdentity, StringComparison.OrdinalIgnoreCase) ||
                    (await db.Database.GetAppliedMigrationsAsync(budget.Token)).LastOrDefault() != "20261024061418_AddDocumentOcrExecutionLifecycle")
                    throw new InvalidOperationException("document-ocr-maintenance-database-binding-changed");
            }
            var store = new SqlDocumentOcrStore(factory, scheduler: null);
            var verifier = new StoppedVerifier(plan, args[2], args[4]);
            var verified = await store.AcceptLegacyCleanupAsync(plan.Binding, verifier, budget.Token);
            if (!verified.Accepted) throw new InvalidOperationException(verified.ReasonCode);
            if (await verifier.VerifyStoppedAndCleanAsync(budget.Token) is null)
                throw new InvalidOperationException("document-ocr-native-cleanup-unproven");
            var settled = await store.SettleCompletionAsync(plan.Binding.Handle, plan.Binding.MiniTaskId, plan.Binding.ResultDigest, budget.Token);
            if (!settled.Accepted) throw new InvalidOperationException(settled.ReasonCode);
            var requeued = await store.RequeueCompletedAsync(plan.Binding.Handle, plan.Binding.MiniTaskId, budget.Token);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { Settled = true, Requeued = requeued,
                plan.Binding.Handle.BatchId, plan.Binding.RecoveryInstanceId, ResultDigest = Convert.ToHexString(plan.Binding.ResultDigest) }));
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            var reason = exception is InvalidOperationException && exception.Message.StartsWith("document-ocr-", StringComparison.Ordinal) &&
                exception.Message.Length <= 128 ? exception.Message : "document-ocr-maintenance-unavailable";
            await error.WriteLineAsync(reason);
            return 1;
        }
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static FileStream OpenRead(string path)
    {
        if (!Path.IsPathFullyQualified(path) || new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
            throw new InvalidOperationException("document-ocr-maintenance-local-path-required");
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("document-ocr-maintenance-reparse-path-refused");
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private sealed class StoppedVerifier(OcrRecoveryPlan plan, string planPath, string hash) : IDocumentOcrMaintenanceVerifier
    {
        public async ValueTask<DocumentOcrMaintenanceVerification?> VerifyStoppedAndCleanAsync(CancellationToken ct)
        {
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", plan.VerifierScriptPath,
                "-PlanPath", planPath, "-ExpectedPlanSha256", hash }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("document-ocr-maintenance-verifier-unavailable");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            var evidence = await stdout;
            _ = await stderr; // Never expose private process/driver output in operational errors.
            if (process.ExitCode != 0 || evidence.Length > 1024 * 1024)
                throw new InvalidOperationException("document-ocr-native-cleanup-unproven");
            using var document = JsonDocument.Parse(evidence);
            if (!document.RootElement.GetProperty("CleanupVerified").GetBoolean()) return null;
            var observed = document.RootElement.GetProperty("ObservedAtUtc").GetDateTimeOffset();
            var bytes = Encoding.UTF8.GetBytes(evidence);
            var evidencePath = Path.Combine(plan.OperationRoot, $"native-cleanup-{Guid.NewGuid():N}.json");
            using (var stream = new FileStream(evidencePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            { await stream.WriteAsync(bytes, ct); stream.Flush(flushToDisk: true); }
            return new(new(Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current), observed,
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    private sealed record OcrRecoveryPlan(int Version, string ReleaseId, string DatabaseIdentity,
        DocumentOcrLegacyRecoveryBinding Binding, string OperationRoot, string HoldPath,
        string ConfigurationPath, string ConfigurationSha256, string VerifierScriptPath, string VerifierScriptSha256);
    private sealed class Factory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection, sql => sql.CommandTimeout(30)).Options);
    }
}
