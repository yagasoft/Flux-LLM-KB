using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Cli.Commands;

/// <summary>Privileged, explicitly hash-bound retirement; never starts the normal application or a model runtime.</summary>
internal static class IndexRecoveryRetirementCommand
{
    internal static Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter error, CancellationToken token)
    {
        // A named Mutex is thread-owned. Keep its entire lifetime on this calling
        // thread while the checked asynchronous SQL work runs on the pool.
        try
        {
            if (args.Length != 5 || args[1] != "--plan" || args[3] != "--plan-sha256" ||
                !Path.IsPathFullyQualified(args[2]) || args[4].Length != 64 || !OperatingSystem.IsWindows())
                throw new InvalidOperationException("index-recovery-retirement-explicit-plan-required");
            for (var path = args[2]; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("index-recovery-retirement-linked-plan");
            using var input = new FileStream(PhysicalFileIdentity.OpenReadNoFollow(args[2]), FileAccess.Read);
            if (input.Length is < 1 or > 1024 * 1024) throw new InvalidOperationException("index-recovery-retirement-plan-byte-budget");
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(args[4])))
                throw new InvalidOperationException("index-recovery-retirement-plan-hash-changed");
            var plan = JsonSerializer.Deserialize<RecoveryArchiveRetirementPlan>(bytes,
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 })
                ?? throw new InvalidOperationException("index-recovery-retirement-plan-invalid");
            var raw = Environment.GetEnvironmentVariable("ConnectionStrings__FluxKnowledge");
            if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("index-recovery-database-unavailable");
            var connection = new SqlConnectionStringBuilder(raw) { Pooling = false, ConnectTimeout = 5, ConnectRetryCount = 0 };
            if (connection.DataSource is not ("localhost" or "." or "(local)" or "127.0.0.1") ||
                string.IsNullOrWhiteSpace(connection.InitialCatalog) ||
                new[] { "master", "model", "msdb", "tempdb" }.Contains(connection.InitialCatalog, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("index-recovery-retirement-local-database-required");
            foreach (var root in new[] { plan.RecoveryRoot, plan.IndexRoot, plan.QuarantineRoot, plan.ApplicationEntry })
                if (new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed)
                    throw new InvalidOperationException("index-recovery-local-fixed-root-required");
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromMinutes(3));
            using var mutex = new Mutex(false, @"Global\FluxKnowledge.IncrementalIisUpdate.v1");
            var held = false;
            try
            {
                try { held = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) throw new InvalidOperationException("index-recovery-retirement-deployment-owned");
                var store = new SqlIndexRecoveryReferences(new Factory(connection.ConnectionString), TimeProvider.System,
                    new WindowsInteractiveGpuOwnerProbe());
                var audit = store.RetireSupersededArchivesAsync(plan, budget.Token).AsTask().GetAwaiter().GetResult();
                output.WriteLine(JsonSerializer.Serialize(new { plan.OperationId, plan.QuarantineRoot, SqlAuditSealed = true,
                    audit.PacketCount, PinCount = audit.Pins.Count, audit.InventoryChecksum, ReconciledWriterCount = plan.Writers.Count }));
                return Task.FromResult(0);
            }
            finally { if (held) mutex.ReleaseMutex(); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or JsonException or SqlException or OperationCanceledException)
        {
            var reason = exception is InvalidOperationException && exception.Message.StartsWith("index-recovery-", StringComparison.Ordinal) &&
                exception.Message.Length <= 160 ? exception.Message : exception is OperationCanceledException
                    ? "index-recovery-retirement-budget-exceeded" : "index-recovery-retirement-unavailable";
            error.WriteLine(JsonSerializer.Serialize(new { ReasonCode = reason, PreservedState = "retain plan, journal and quarantine; verify the native seal before retry" }));
            return Task.FromResult(1);
        }
    }

    private sealed class Factory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection).Options);
    }
}
