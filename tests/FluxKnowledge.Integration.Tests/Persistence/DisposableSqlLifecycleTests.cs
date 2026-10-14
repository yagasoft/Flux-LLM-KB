using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class DisposableSqlLifecycleTests
{
    [Fact]
    public async Task Session_interruption_disconnects_only_the_verified_fixture_and_restores_multi_user()
    {
        var fixture = new NativeSqlServerFixture();
        await fixture.InitializeAsync();
        try
        {
            await using var target = new SqlConnection(new SqlConnectionStringBuilder(fixture.ConnectionString)
            { Pooling = false }.ConnectionString);
            await using var untouched = new SqlConnection(NativeSqlServerFixture.ValidateRestrictedTestConnection(
                Environment.GetEnvironmentVariable(NativeSqlServerFixture.ConnectionEnvironmentVariable)));
            await target.OpenAsync();
            await untouched.OpenAsync();
            await using (var refused = new SqlCommand(NativeSqlServerFixture.BuildSessionInterruptionSql(
                fixture.DatabaseName, @"E:\Codex Workspaces\Staging\FluxKnowledge\SqlTests-WrongManifest\Data\Sql\Disposable"), untouched))
            {
                var error = await Assert.ThrowsAsync<SqlException>(async () => await refused.ExecuteNonQueryAsync());
                Assert.Equal(51000, error.Number);
            }
            Assert.Equal(1, await new SqlCommand("SELECT 1", target).ExecuteScalarAsync());
            await fixture.InterruptDatabaseSessionsAsync();
            await Assert.ThrowsAsync<SqlException>(async () => await new SqlCommand("SELECT 1", target).ExecuteScalarAsync());
            Assert.Equal(1, await new SqlCommand("SELECT 1", untouched).ExecuteScalarAsync());
            await using var restored = new SqlConnection(fixture.ConnectionString);
            await restored.OpenAsync();
            await using var mode = new SqlCommand("SELECT user_access_desc FROM sys.databases WHERE database_id=DB_ID()", restored);
            Assert.Equal("MULTI_USER", await mode.ExecuteScalarAsync());
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Restricted_fixture_migrates_and_removes_only_its_acknowledged_ordinary_and_previous_catalogues()
    {
        var fixture = new NativeSqlServerFixture();
        await fixture.InitializeAsync();
        string previousName;
        try
        {
            await using var context = new FluxKnowledge.Infrastructure.SqlServer.Persistence.FluxKnowledgeDbContext(
                new DbContextOptionsBuilder<FluxKnowledge.Infrastructure.SqlServer.Persistence.FluxKnowledgeDbContext>()
                    .UseSqlServer(fixture.ConnectionString).Options);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            var previous = await fixture.CreatePreviousMigrationDatabaseAsync();
            previousName = previous.DatabaseName;
            await previous.DisposeAsync();
            await previous.DisposeAsync();
            await AssertAbsentAsync(previousName);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
        await fixture.DisposeAsync();
        await AssertAbsentAsync(fixture.DatabaseName);
    }

    private static async Task AssertAbsentAsync(string name)
    {
        var connectionString = NativeSqlServerFixture.ValidateRestrictedTestConnection(
            Environment.GetEnvironmentVariable(NativeSqlServerFixture.ConnectionEnvironmentVariable));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT DB_ID(@name);", connection);
        command.Parameters.AddWithValue("@name", name);
        Assert.IsType<DBNull>(await command.ExecuteScalarAsync());
        var root = NativeSqlServerFixture.ValidateTestFileRoot(Environment.GetEnvironmentVariable(NativeSqlServerFixture.FileRootEnvironmentVariable));
        Assert.False(File.Exists(Path.Combine(root, name + ".mdf")));
        Assert.False(File.Exists(Path.Combine(root, name + "_log.ldf")));
    }
}
