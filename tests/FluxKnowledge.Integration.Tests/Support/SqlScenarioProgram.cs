using System.Text.Json;
using System.Text.RegularExpressions;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Integration.Tests.Support;

/// <summary>One disposable SQL-store unit in a fresh CLR; never a production operator.</summary>
internal static class SqlScenarioProgram
{
    public static async Task<int> Main(string[] args)
    {
        if(args is not ["source-delete-unit"])return 2;
        var connection=Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_SCENARIO_SQL")??"";
        var builder=new SqlConnectionStringBuilder(connection);
        if(!Regex.IsMatch(builder.InitialCatalog,@"\AFluxKnowledge_Phase1Tests_[a-f0-9]{32}\z")||builder.IntegratedSecurity||
            string.IsNullOrWhiteSpace(builder.UserID)||builder.UserID.Equals("sa",StringComparison.OrdinalIgnoreCase)||
            builder.AttachDBFilename.Length!=0||builder.UserInstance)throw new InvalidOperationException("Disposable restricted SQL binding required.");
        builder.ConnectTimeout=5;builder.ConnectRetryCount=0;
        var expected=Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_SCENARIO_DATABASE_GUID");
        if(!Guid.TryParse(expected,out var databaseGuid)||!Guid.TryParse(Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_SCENARIO_OPERATION"),out var operation))
            throw new InvalidOperationException("Exact scenario identities required.");
        await using(var sql=new SqlConnection(builder.ConnectionString))
        {
            await sql.OpenAsync();await using var command=sql.CreateCommand();command.CommandTimeout=5;
            command.CommandText="SELECT database_guid FROM sys.database_recovery_status WHERE database_id=DB_ID()";
            if((Guid)(await command.ExecuteScalarAsync())! !=databaseGuid)throw new InvalidOperationException("Scenario database incarnation changed.");
        }
        var options=new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(builder.ConnectionString,sql=>sql.CommandTimeout(30)).Options;
        var store=new SqlSourceDeletionStore(new ScenarioFactory(options),TimeProvider.System);
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var work=await store.ClaimNextAsync(budget.Token);
        if(work is null||work.OperationId!=operation)throw new InvalidOperationException("Scenario operation changed.");
        var result=await store.PurgeAsync(work,null,budget.Token);
        Console.WriteLine(JsonSerializer.Serialize(new{ProcessId=Environment.ProcessId,result.Completed,result.Phase,result.ReasonCode,result.ContinueImmediately}));
        return 0;
    }

    private sealed class ScenarioFactory(DbContextOptions<FluxKnowledgeDbContext> options):IDbContextFactory<FluxKnowledgeDbContext>
    {public FluxKnowledgeDbContext CreateDbContext()=>new(options);}
}
