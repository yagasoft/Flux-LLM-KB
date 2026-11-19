using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class DeletedSourceRecoveryIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Quiet_recovery_uses_only_the_bounded_fallback_candidate_read()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var observer = new RediscoveryCalls();
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection).AddInterceptors(observer).Options;
        var factory = new RediscoveryFactory(options);
        var clock = new RediscoveryClock(DateTimeOffset.UtcNow);
        var observations = new WindowsSourceObservationReader();
        var progress = new SqlSourceWorkRecoveryProgress();
        var recovery = new SqlSourceWorkRecovery(factory, scenario.Admission,
            new SqlSourceRemovalReconciler(factory, clock, observations), observations, clock, recoveryProgress: progress);
        const int quietTicks = 20;
        for (var i = 0; i < quietTicks; i++)
        {
            await recovery.AdvanceAsync(default);
            clock.Now = clock.Now.AddSeconds(1);
        }
        Assert.Equal(1, observer.Count);
        var startup = await SumLogicalReadsAsync(1);
        var baseline = await SumLogicalReadsAsync(quietTicks);
        Assert.True(startup > 0);
        Assert.Equal(quietTicks * startup, baseline);
        clock.Now = clock.Now.AddMinutes(2);
        await recovery.AdvanceAsync(default);
        Assert.Equal(2, observer.Count);
        Console.WriteLine($"Rediscovery staging, {quietTicks} quiet recovery ticks: prior cadence replay {quietTicks} candidate executions, {baseline} logical reads. Startup plus quiet interval: 1 candidate execution, {startup} logical reads.");

        async Task<long> SumLogicalReadsAsync(int executions)
        {
            var messages = new List<string>();
            connection.InfoMessage += ReadMessage;
            try
            {
                for (var i = 0; i < executions; i++)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SET STATISTICS IO ON;\n" + observer.Sql + "\nSET STATISTICS IO OFF;";
                    foreach (var parameter in observer.Parameters)
                        command.Parameters.Add((SqlParameter)((ICloneable)parameter).Clone());
                    await using var rows = await command.ExecuteReaderAsync();
                    do { Assert.False(await rows.ReadAsync()); }
                    while (await rows.NextResultAsync());
                }
                return messages.SelectMany(message => Regex.Matches(message, @"(?<!lob )logical reads (\d+),"))
                    .Sum(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }
            finally { connection.InfoMessage -= ReadMessage; }

            void ReadMessage(object sender, SqlInfoMessageEventArgs args) => messages.Add(args.Message);
        }
    }

    private sealed class RediscoveryFactory(DbContextOptions<FluxKnowledgeDbContext> options) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(options);
    }

    private sealed class RediscoveryCalls : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public string Sql { get; private set; } = "";
        public SqlParameter[] Parameters { get; private set; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("source-rediscovery-candidate-page", StringComparison.Ordinal))
            {
                Count++;
                Sql = command.CommandText;
                Parameters = command.Parameters.Cast<SqlParameter>()
                    .Select(parameter => (SqlParameter)((ICloneable)parameter).Clone()).ToArray();
            }
            return ValueTask.FromResult(result);
        }
    }
}
