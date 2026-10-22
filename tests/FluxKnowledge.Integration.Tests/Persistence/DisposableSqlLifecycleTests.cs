using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class DisposableSqlLifecycleTests
{
    [Fact]
    public async Task Cleanup_forces_an_active_variant_session_to_disconnect_without_touching_its_sibling()
    {
        var fixture = new NativeSqlServerFixture();
        var sibling = new NativeSqlServerFixture();
        await fixture.InitializeAsync();
        try
        {
            await sibling.InitializeAsync();
            try
            {
                await using var active = new SqlConnection(new SqlConnectionStringBuilder(fixture.ConnectionString)
                    { Pooling = false, ApplicationName = "DisposableCleanupActiveVariant" }.ConnectionString);
                await active.OpenAsync();
                await using var transaction = await active.BeginTransactionAsync();
                await using (var mutation = new SqlCommand("UPDATE dbo.IndexState SET CorpusVersion=17 WHERE Id=1;", active, (SqlTransaction)transaction))
                    await mutation.ExecuteNonQueryAsync();
                await using var untouched = new SqlConnection(sibling.ConnectionString);
                await untouched.OpenAsync();
                var original = untouched.ClientConnectionId;
                await untouched.CloseAsync();

                await fixture.DisposeAsync();

                await Assert.ThrowsAsync<SqlException>(async () =>
                    await new SqlCommand("SELECT 1", active, (SqlTransaction)transaction).ExecuteScalarAsync());
                await untouched.OpenAsync();
                Assert.Equal(original, untouched.ClientConnectionId);
                Assert.Equal(1, await new SqlCommand("SELECT 1", untouched).ExecuteScalarAsync());
                await AssertAbsentAsync(fixture.DatabaseName);
            }
            finally { await sibling.DisposeAsync(); }
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Cleanup_rejects_a_foreign_manifest_and_an_ambient_transaction_before_forced_disconnection()
    {
        var fixture = new NativeSqlServerFixture();
        await fixture.InitializeAsync();
        try
        {
            await using var connection = new SqlConnection(new SqlConnectionStringBuilder(
                NativeSqlServerFixture.ValidateRestrictedTestConnection(Environment.GetEnvironmentVariable(NativeSqlServerFixture.ConnectionEnvironmentVariable)))
                { InitialCatalog = "master", Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            var rejected = await Assert.ThrowsAsync<SqlException>(() => NativeSqlServerFixture.ExecuteDropAsync(connection,
                fixture.DatabaseName, @"E:\Codex Workspaces\Staging\FluxKnowledge\SqlTests-WrongManifest\Data\Sql\Disposable", new SqlFixtureTimings(null)));
            Assert.Equal(51000, rejected.Number);
            await new SqlCommand("BEGIN TRANSACTION;", connection).ExecuteNonQueryAsync();
            try
            {
                rejected = await Assert.ThrowsAsync<SqlException>(() => NativeSqlServerFixture.ExecuteDropAsync(connection,
                    fixture.DatabaseName, NativeSqlServerFixture.ValidateTestFileRoot(Environment.GetEnvironmentVariable(NativeSqlServerFixture.FileRootEnvironmentVariable)),
                    new SqlFixtureTimings(null)));
                Assert.Equal(51000, rejected.Number);
            }
            finally { await new SqlCommand("ROLLBACK TRANSACTION;", connection).ExecuteNonQueryAsync(); }
            await using var verification = new SqlConnection(fixture.ConnectionString);
            await verification.OpenAsync();
            Assert.Equal("MULTI_USER", await new SqlCommand("SELECT user_access_desc FROM sys.databases WHERE database_id=DB_ID();", verification).ExecuteScalarAsync());
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Disposing_one_catalogue_preserves_a_sibling_catalogues_pooled_connection()
    {
        var target = new NativeSqlServerFixture();
        var sibling = new NativeSqlServerFixture();
        await target.InitializeAsync();
        try
        {
            await sibling.InitializeAsync();
            try
            {
                await using var connection = new SqlConnection(sibling.ConnectionString);
                await connection.OpenAsync();
                var originalConnection = connection.ClientConnectionId;
                await connection.CloseAsync();
                await target.DisposeAsync();
                await connection.OpenAsync();
                Assert.Equal(originalConnection, connection.ClientConnectionId);
                Assert.Equal(1, await new SqlCommand("SELECT 1", connection).ExecuteScalarAsync());
            }
            finally { await sibling.DisposeAsync(); }
        }
        finally { await target.DisposeAsync(); }
    }

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
        var fixture = new NativeSqlServerFixture { UseDirectMigrations = true };
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
