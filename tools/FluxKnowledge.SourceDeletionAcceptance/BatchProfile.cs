using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

internal static class BatchProfile
{
    // Called only after Program's exact catalogue, restricted login, storage and
    // endpoint verification. The stage host must be stopped. Every delete rolls back.
    internal static async Task RunAsync(string root,SqlConnectionStringBuilder verified,IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        var manifestFile = Directory.GetFiles(Path.Combine(root,"Recovery","acceptance"),"manifest.json",SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestFile));
        var rootId = manifest.RootElement.GetProperty("rootIds")[0].GetGuid();
        await using var context = await factory.CreateDbContextAsync();
        var owned = from v in context.Vectors join c in context.TextChunks on v.TextChunkId equals c.Id
                    join a in context.Artifacts on c.ArtifactId equals a.Id join p in context.PipelineRecords on a.PipelineRecordId equals p.Id
                    join r in context.SourceRevisions on p.SourceRevisionId equals r.Id where r.SourceRootId == rootId select v.VectorId;
        if (!await context.SourceRootConfigurations.AnyAsync(r => r.Id == rootId && r.State == 0)) throw new InvalidOperationException("Profile requires the newly seeded, undeleted source.");
        var vectorId = await owned.MinAsync();
        var before = await context.IndexGenerationVectors.LongCountAsync(m => owned.Contains(m.VectorId));
        var vectorBefore = await context.IndexGenerationVectors.CountAsync(m => m.VectorId == vectorId);
        var profileBinding = new SqlConnectionStringBuilder(verified.ConnectionString) {
            ApplicationName = "FluxKnowledge.SourceDeletionAcceptanceProfile" };
        await using var connection = new SqlConnection(profileBinding.ConnectionString);
        await connection.OpenAsync();
        await using (var identity = new SqlCommand("SELECT DB_NAME();",connection))
            if ((string?)await identity.ExecuteScalarAsync() != verified.InitialCatalog) throw new InvalidOperationException("Profile catalogue changed.");
        await using (var permission = new SqlCommand("SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','SHOWPLAN');",connection))
            if (Convert.ToInt32(await permission.ExecuteScalarAsync()) != 1)
                throw new InvalidOperationException("Staging SHOWPLAN must be granted through the reviewed staging provisioning path before profiling.");
        var diagnostics = new StringBuilder();
        connection.InfoMessage += (_,args) => diagnostics.AppendLine(args.Message);
        var durations = new List<double>(); var affected = new List<int>(); string? plan = null;
        for (var sample = 0; sample < 51; sample++)
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            var query = "DELETE TOP (@limit) FROM [IndexGenerationVectors] WHERE [VectorId] = @vectorId";
            if (sample == 0) query = "SET STATISTICS IO ON; SET STATISTICS TIME ON; SET STATISTICS XML ON; " + query + "; SET STATISTICS XML OFF; SET STATISTICS TIME OFF; SET STATISTICS IO OFF;";
            await using var command = new SqlCommand(query,connection,transaction) { CommandTimeout = 30 };
            command.Parameters.AddWithValue("@limit",1000); command.Parameters.AddWithValue("@vectorId",vectorId);
            var watch = Stopwatch.StartNew();
            if (sample == 0)
            {
                await using var reader = await command.ExecuteReaderAsync();
                do { while (await reader.ReadAsync()) if (reader.FieldCount == 1) plan = reader.GetString(0); } while (await reader.NextResultAsync());
            }
            else { affected.Add(await command.ExecuteNonQueryAsync()); durations.Add(watch.Elapsed.TotalMilliseconds); }
            await transaction.RollbackAsync();
        }
        var after = await context.IndexGenerationVectors.LongCountAsync(m => owned.Contains(m.VectorId));
        if (before != after || affected.Any(n => n != Math.Min(1000,vectorBefore))) throw new InvalidOperationException("Profile rollback or batch bound failed.");
        var evidence = Path.GetDirectoryName(manifestFile)!;
        await File.WriteAllTextAsync(Path.Combine(evidence,"batch-actual-plan.sqlplan"),plan ?? throw new InvalidOperationException("No actual plan returned."));
        await File.WriteAllTextAsync(Path.Combine(evidence,"batch-statistics.txt"),diagnostics.ToString());
        var document = XDocument.Parse(plan); var ns = document.Root!.Name.Namespace;
        var access = document.Descendants(ns + "RelOp").Select(n => n.Attribute("PhysicalOp")?.Value).ToArray();
        var sorted = durations.Order().ToArray();
        var result = new { samples = durations.Count,scalarCommandP95Milliseconds = sorted[(int)Math.Ceiling(sorted.Length*.95)-1],
            membershipRowsUnchanged = before == after,ownedMemberships = before,vectorId,physicalOperators = access,
            milliseconds = durations,affectedRows = affected };
        await File.WriteAllTextAsync(Path.Combine(evidence,"batch-profile.json"),JsonSerializer.Serialize(result));
        Console.WriteLine(JsonSerializer.Serialize(new { result.samples,result.scalarCommandP95Milliseconds,result.membershipRowsUnchanged,result.ownedMemberships,result.physicalOperators }));
    }
}
