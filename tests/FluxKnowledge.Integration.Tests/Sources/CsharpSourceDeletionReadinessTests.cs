using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class CsharpSourceDeletionReadinessTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Script_created_source_deletion_guard_is_ready_but_changed_predicate_is_not()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var store = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);
        Assert.True(await store.IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));
        await using var context = await factory.CreateDbContextAsync();
        var original = await context.Database.SqlQuery<string>($"""
            SELECT [definition] AS [Value] FROM sys.sql_modules
            WHERE object_id = OBJECT_ID(N'dbo.TR_SourceProcessorCodeBlockedDiagnostics_Immutable')
            """).SingleAsync();
        var migrationSql = new AddSourceDeletionOperations().UpOperations.OfType<SqlOperation>()
            .Single(operation => operation.Sql.StartsWith("ALTER TRIGGER [dbo].[TR_SourceProcessorCodeBlockedDiagnostics_Immutable]", StringComparison.Ordinal)).Sql;
        var scripted = "CREATE" + migrationSql["ALTER".Length..];
        scripted = scripted.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.Equal("DE3C1AD1140C9FB7AD04ED29BAD4B54741E36744537CAFA7A49D99765F34B598",
            Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(scripted))));
        try
        {
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER [dbo].[TR_SourceProcessorCodeBlockedDiagnostics_Immutable]");
            await context.Database.ExecuteSqlRawAsync(scripted);
            Assert.True(await store.IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));
            var changed = ("ALTER" + scripted["CREATE".Length..]).Replace("[operation].[State] = 1", "[operation].[State] = 2", StringComparison.Ordinal);
            await context.Database.ExecuteSqlRawAsync(changed);
            Assert.False(await store.IsRetainedCsharpCodeWriterReadyAsync(CancellationToken.None));
        }
        finally
        {
            var restore = original.StartsWith("CREATE", StringComparison.Ordinal) ? "ALTER" + original["CREATE".Length..] : original;
            await context.Database.ExecuteSqlRawAsync(restore);
        }
    }
}
