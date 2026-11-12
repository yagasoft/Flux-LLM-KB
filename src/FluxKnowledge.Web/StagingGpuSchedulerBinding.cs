using System.Text.Json;
using System.Text.RegularExpressions;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web;

/// <summary>One disposable GPU ledger; corpus SQL and copied scheduler/recovery rows remain separate.</summary>
internal sealed class StagingGpuSchedulerBinding : IDbContextFactory<FluxKnowledgeDbContext>
{
    private readonly string _connection;
    internal static string FilePath(StagingHostSettings settings) => Path.Combine(settings.Layout.RuntimeRoot,"expanded-gpu-scheduler-binding.json");
    internal StagingGpuSchedulerBinding(string connection)
    {
        var parsed=new SqlConnectionStringBuilder(connection);
        if (!Regex.IsMatch(parsed.InitialCatalog,@"\AFluxKnowledge_Phase1Tests_[0-9a-f]{32}\z") ||
            !string.Equals(parsed.DataSource,"localhost",StringComparison.OrdinalIgnoreCase) || parsed.IntegratedSecurity ||
            string.IsNullOrWhiteSpace(parsed.UserID) || string.IsNullOrWhiteSpace(parsed.Password) ||
            !string.IsNullOrEmpty(parsed.AttachDBFilename) || parsed.UserInstance || !string.IsNullOrEmpty(parsed.FailoverPartner) ||
            parsed.MultiSubnetFailover || parsed.ApplicationIntent!=ApplicationIntent.ReadWrite)
            throw new ArgumentException("Explicit restricted disposable GPU staging ledger required.");
        parsed.ConnectTimeout=5; parsed.ConnectRetryCount=0; parsed.ApplicationName="FluxKnowledge.StagingGpuTrial";
        _connection=parsed.ConnectionString;
    }
    internal static StagingGpuSchedulerBinding Load(StagingHostSettings settings)
    {
        var path=FilePath(settings);
        for(var current=Path.GetFullPath(path);!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidOperationException("Linked GPU staging binding refused.");
        if(new FileInfo(path).Length>16384) throw new InvalidOperationException("GPU staging binding exceeds bound.");
        using var json=JsonDocument.Parse(File.ReadAllText(path));
        if(json.RootElement.GetProperty("SchemaVersion").GetInt32()!=1) throw new InvalidOperationException("Unknown GPU staging binding.");
        return new(json.RootElement.GetProperty("ConnectionString").GetString()!);
    }
    internal async Task VerifyAsync(CancellationToken ct)
    {
        await using var connection=new SqlConnection(_connection); await connection.OpenAsync(ct);
        await using var command=new SqlCommand("""
            SELECT DB_NAME(),IS_ROLEMEMBER('db_owner'),IS_SRVROLEMEMBER('sysadmin'),
                HAS_PERMS_BY_NAME(NULL,NULL,'CONTROL SERVER'),HAS_DBACCESS('FluxKnowledge');
            SELECT COUNT_BIG(*) FROM dbo.GpuMiniTasks;
            SELECT SlotKey,State,ActiveBatchId,OwnerKey FROM dbo.GpuCapacitySlots;
            """,connection){CommandTimeout=5};
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct) || reader.GetString(0)!=new SqlConnectionStringBuilder(_connection).InitialCatalog ||
            reader.GetInt32(1)!=1 || Enumerable.Range(2,3).Any(i => reader.IsDBNull(i) || reader.GetInt32(i)!=0))
            throw new InvalidOperationException("GPU staging owner/production boundary unverified.");
        if(!await reader.NextResultAsync(ct) || !await reader.ReadAsync(ct) || reader.GetInt64(0)!=0)
            throw new InvalidOperationException("Fresh empty GPU staging ledger required.");
        if(!await reader.NextResultAsync(ct) || !await reader.ReadAsync(ct) || reader.GetString(0)!="gpu-0" || reader.GetInt32(1)!=0 ||
            !reader.IsDBNull(2) || !reader.IsDBNull(3) || await reader.ReadAsync(ct))
            throw new InvalidOperationException("One fresh ownerless GPU staging slot required.");
    }
    public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_connection).Options);
}
