using System.Text.Json;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Windows;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Cli.Commands;

/// <summary>Privileged metadata audit; IIS consumes its SQL seal without Recovery filesystem access.</summary>
public static class IndexRecoveryCommand
{
    public static async Task<int> ExecuteFromEnvironmentAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken token = default)
    {
        if (args.Length != 5 || args[0] is not ("audit" or "report") || args[1] != "--recovery-root" || args[3] != "--index-root" ||
            !Path.IsPathFullyQualified(args[2]) || !Path.IsPathFullyQualified(args[4]))
        {
            await error.WriteLineAsync("Usage: index-recovery report|audit --recovery-root <local-directory> --index-root <local-directory>. Report has no SQL access; audit requires ConnectionStrings__FluxKnowledge and seals SQL references. Neither reads index or source contents.");
            return 2;
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            foreach (var root in new[] { args[2], args[4] })
                if (new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed)
                    throw new InvalidOperationException("index-recovery-local-fixed-root-required");
            RecoveryIndexAudit audit;
            if (args[0] == "report") audit = new RecoveryIndexReferenceAudit(args[4]).Capture(args[2], budget.Token);
            else
            {
                var connection = Environment.GetEnvironmentVariable("ConnectionStrings__FluxKnowledge");
                if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("index-recovery-database-unavailable");
                var binding = new SqlConnectionStringBuilder(connection) { ConnectRetryCount = 0, ConnectTimeout = 5 };
                if (string.IsNullOrWhiteSpace(binding.DataSource) || string.IsNullOrWhiteSpace(binding.InitialCatalog) ||
                    new[] { "master", "model", "msdb", "tempdb" }.Contains(binding.InitialCatalog, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("index-recovery-database-target-invalid");
                var store = new SqlIndexRecoveryReferences(new Factory(binding.ConnectionString), TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
                audit = await store.AuditAsync(args[2], args[4], budget.Token);
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(new { SqlAuditSealed = args[0] == "audit", audit.RootPath, audit.IndexRoot,
                audit.RootFingerprint, audit.InventoryChecksum, audit.PacketCount, PinCount = audit.Pins.Count,
                ClassifierVersion = RecoveryIndexReferenceAudit.ClassifierVersion }));
            return 0;
        }
        catch (RecoveryIndexAuditException exception)
        {
            await error.WriteLineAsync(JsonSerializer.Serialize(new { ReasonCode = exception.Message,
                PacketPath = exception.RelativePath.Length <= 2048 ? exception.RelativePath : "." }));
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or SqlException or OperationCanceledException)
        {
            var reason = exception switch
            {
                OperationCanceledException => "index-recovery-audit-cancelled-or-budget-exceeded",
                SqlException => "index-recovery-database-unavailable",
                InvalidOperationException when exception.Message.StartsWith("index-recovery-", StringComparison.Ordinal) && exception.Message.Length <= 160 => exception.Message,
                _ => "index-recovery-audit-unavailable"
            };
            await error.WriteLineAsync(JsonSerializer.Serialize(new { ReasonCode = reason, PacketPath = (string?)null }));
            return 1;
        }
    }

    private sealed class Factory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection).Options);
    }
}
