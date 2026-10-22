using System.Text;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Support;

public sealed class SqlDatabaseTemplateTests
{
    [NativeSqlServerFact]
    public async Task Concurrent_restores_match_real_migrations_and_have_independent_corpus_identity_data_and_sessions()
    {
        var direct = new NativeSqlServerFixture { UseDirectMigrations = true };
        var first = new NativeSqlServerFixture { UseDirectMigrations = false };
        var second = new NativeSqlServerFixture { UseDirectMigrations = false };
        await direct.InitializeAsync();
        try
        {
            try
            {
                await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());
                var expected = await FingerprintAsync(direct.ConnectionString);
                Assert.Equal(expected, await FingerprintAsync(first.ConnectionString));
                Assert.Equal(expected, await FingerprintAsync(second.ConnectionString));
                var epochs = await Task.WhenAll(new[] { direct, first, second }.Select(value => ScalarAsync(value.ConnectionString, "SELECT CorpusEpoch FROM dbo.IndexState WHERE Id=1;")));
                Assert.Equal(3, epochs.Distinct().Count());
                var (server, root) = Configuration();
                var templateEpoch = (await NativeSqlDatabaseTemplate.GetAsync(server, root)).CorpusEpoch;
                Assert.DoesNotContain(templateEpoch, epochs.Cast<Guid>());
                Assert.DoesNotContain(Guid.Empty, epochs.Cast<Guid>());
                var firstVersion = (long)(await ScalarAsync(first.ConnectionString, "SELECT CONVERT(bigint,RowVersion) FROM dbo.IndexState WHERE Id=1;"))!;
                var secondVersion = await ScalarAsync(second.ConnectionString, "SELECT CONVERT(bigint,RowVersion) FROM dbo.IndexState WHERE Id=1;");
                await ScalarAsync(first.ConnectionString, "UPDATE dbo.IndexState SET CorpusVersion=11 WHERE Id=1; SELECT CorpusVersion FROM dbo.IndexState WHERE Id=1;");
                Assert.True((long)(await ScalarAsync(first.ConnectionString, "SELECT CONVERT(bigint,RowVersion) FROM dbo.IndexState WHERE Id=1;"))! > firstVersion);
                Assert.Equal(secondVersion, await ScalarAsync(second.ConnectionString, "SELECT CONVERT(bigint,RowVersion) FROM dbo.IndexState WHERE Id=1;"));
                Assert.Equal(0L, await ScalarAsync(second.ConnectionString, "SELECT CorpusVersion FROM dbo.IndexState WHERE Id=1;"));
                await first.DisposeAsync();
                Assert.Equal(0L, await ScalarAsync(second.ConnectionString, "SELECT CorpusVersion FROM dbo.IndexState WHERE Id=1;"));
            }
            finally { await Task.WhenAll(first.DisposeAsync(), second.DisposeAsync()); }
        }
        finally { await direct.DisposeAsync(); }
    }

    [NativeSqlServerFact]
    public async Task A_cancelled_waiter_does_not_cancel_shared_preparation_and_configuration_changes_fail_closed()
    {
        var (server, root) = Configuration();
        var completion = new TaskCompletionSource<NativeSqlDatabaseTemplate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var preparation = new NativeSqlDatabaseTemplate.Preparation((_, _) => { starts++; return completion.Task; });
        var original = preparation.GetAsync(server, root);
        using var cancellation = new CancellationTokenSource();
        var waiting = preparation.GetAsync(server, root, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(original.IsCompleted);
        var template = await NativeSqlDatabaseTemplate.GetAsync(server, root);
        completion.SetResult(template);
        Assert.Same(template, await original);
        Assert.Same(template, await preparation.GetAsync(server, root));
        var changed = new SqlConnectionStringBuilder(server) { ApplicationName = "changed-template-config" }.ConnectionString;
        await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.GetAsync(changed, root));
        Assert.Same(template, await preparation.GetAsync(server, root));
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task A_failed_preparation_is_retained_without_publishing_or_restarting_it()
    {
        var expected = new InvalidOperationException("injected preparation failure");
        var starts = 0;
        var preparation = new NativeSqlDatabaseTemplate.Preparation((_, _) =>
        {
            starts++;
            return Task.FromException<NativeSqlDatabaseTemplate>(expected);
        });
        var first = preparation.GetAsync("synthetic-server", "synthetic-root");
        var second = preparation.GetAsync("synthetic-server", "synthetic-root");
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => second));
        Assert.Equal(1, starts);
    }

    [NativeSqlServerFact]
    public async Task Corruption_is_rejected_and_a_post_restore_failure_preserves_the_exact_catalogue_and_original_error()
    {
        var (server, root) = Configuration();
        using var template = await NativeSqlDatabaseTemplate.CreateAsync(server, root);
        var original = template.BackupPath + ".original";
        File.Move(template.BackupPath, original);
        try
        {
            await File.WriteAllTextAsync(template.BackupPath, "corrupt backup probe");
            var rejectedName = new NativeSqlServerFixture().DatabaseName;
            await Assert.ThrowsAsync<InvalidOperationException>(() => template.RestoreAsync(rejectedName));
            Assert.False(File.Exists(Path.Combine(root, rejectedName + ".mdf")));
            Assert.False(File.Exists(Path.Combine(root, rejectedName + "_log.ldf")));
        }
        finally { File.Delete(template.BackupPath); File.Move(original, template.BackupPath); }
        var name = new NativeSqlServerFixture().DatabaseName;
        var expected = new InvalidOperationException("injected after restore");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => template.RestoreAsync(name, afterRestore: () => Task.FromException(expected)));
        Assert.Same(expected, error.InnerException);
        Assert.Contains("preservation", error.Message, StringComparison.Ordinal);
        // The fixture does not automatically remove an uncertain restore. This
        // test explicitly proves ownership/files before removing its own probe.
        await NativeSqlServerFixture.VerifyOwnedDatabaseAsync(server, name, root);
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = "master", Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await NativeSqlServerFixture.ExecuteDropAsync(connection, name, root, new SqlFixtureTimings(null));
        template.Dispose();
        Assert.True(File.Exists(template.BackupPath));
        // Explicit cleanup is confined to this test's private template after its
        // preserved catalogue has been positively verified and removed.
        File.Delete(template.BackupPath);
    }

    [NativeSqlServerFact]
    public async Task Disposing_a_template_preserves_its_backup_while_an_owned_restore_is_active()
    {
        var (server, root) = Configuration();
        using var template = await NativeSqlDatabaseTemplate.CreateAsync(server, root);
        var name = new NativeSqlServerFixture().DatabaseName;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoring = template.RestoreAsync(name, afterRestore: () => { entered.SetResult(); return release.Task; });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            template.Dispose();
            Assert.True(File.Exists(template.BackupPath));
            release.SetResult();
            await restoring.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { release.TrySetResult(); await restoring; }
        await NativeSqlServerFixture.VerifyOwnedDatabaseAsync(server, name, root);
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = "master", Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await NativeSqlServerFixture.ExecuteDropAsync(connection, name, root, new SqlFixtureTimings(null));
        Assert.True(File.Exists(template.BackupPath));
        File.Delete(template.BackupPath);
    }

    [NativeSqlServerFact]
    public async Task Hostile_or_existing_destinations_are_rejected_without_overwriting_their_bytes()
    {
        var (server, root) = Configuration();
        var template = await NativeSqlDatabaseTemplate.GetAsync(server, root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => template.RestoreAsync("FluxKnowledge"));
        var name = new NativeSqlServerFixture().DatabaseName;
        var path = Path.Combine(root, name + ".mdf");
        await File.WriteAllTextAsync(path, "owned sentinel");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => template.RestoreAsync(name));
            Assert.Equal("owned sentinel", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    private static (string Server, string Root) Configuration() => (
        NativeSqlServerFixture.ValidateRestrictedTestConnection(Environment.GetEnvironmentVariable(NativeSqlServerFixture.ConnectionEnvironmentVariable)),
        NativeSqlServerFixture.ValidateTestFileRoot(Environment.GetEnvironmentVariable(NativeSqlServerFixture.FileRootEnvironmentVariable)));

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<string> FingerprintAsync(string connectionString)
    {
        // Compare schema, indexes, constraints, migration history, full-text
        // definitions and every seed row. Epochs and database-local rowversion
        // values are checked by their identity/concurrency semantics above.
        const string sql = """
            SELECT s.name AS [schema],t.name,c.name AS [column],ty.name AS [type],c.max_length,c.precision,c.scale,c.is_nullable,c.is_identity,dc.definition
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
            JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id ORDER BY s.name,t.name,c.column_id FOR JSON PATH;
            SELECT OBJECT_NAME(i.object_id) AS [table],i.name,i.is_unique,i.type_desc,c.name AS [column],ic.key_ordinal,ic.is_included_column
            FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE OBJECTPROPERTY(i.object_id,'IsUserTable')=1 ORDER BY [table],i.name,ic.index_column_id FOR JSON PATH;
            SELECT OBJECT_NAME(parent_object_id) AS [table],name,definition FROM sys.check_constraints ORDER BY [table],name FOR JSON PATH;
            SELECT OBJECT_NAME(parent_object_id) AS [table],name,OBJECT_NAME(referenced_object_id) AS referenced_table,delete_referential_action_desc
            FROM sys.foreign_keys ORDER BY [table],name FOR JSON PATH;
            SELECT * FROM dbo.__EFMigrationsHistory ORDER BY MigrationId FOR JSON PATH;
            SELECT name,is_default,is_accent_sensitivity_on FROM sys.fulltext_catalogs ORDER BY name FOR JSON PATH;
            SELECT OBJECT_NAME(i.object_id) AS [table],c.name,i.change_tracking_state_desc,f.language_id
            FROM sys.fulltext_indexes i JOIN sys.fulltext_index_columns f ON f.object_id=i.object_id
            JOIN sys.columns c ON c.object_id=f.object_id AND c.column_id=f.column_id ORDER BY [table],c.name FOR JSON PATH;
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var result = new StringBuilder();
        await using (var command = new SqlCommand(sql, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                while (await reader.ReadAsync()) result.Append(reader.GetString(0));
                result.AppendLine();
            } while (await reader.NextResultAsync());
        }
        var tables = new List<(string Schema, string Table)>();
        await using (var command = new SqlCommand("SELECT s.name,t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id ORDER BY s.name,t.name;", connection))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) tables.Add((reader.GetString(0), reader.GetString(1)));
        foreach (var (schema, table) in tables)
        {
            var columns = new List<string>();
            await using (var columnCommand = new SqlCommand("SELECT name FROM sys.columns WHERE object_id=OBJECT_ID(@table) AND system_type_id<>189 ORDER BY column_id;", connection))
            {
                columnCommand.Parameters.AddWithValue("@table", $"[{schema}].[{table}]");
                await using var columnReader = await columnCommand.ExecuteReaderAsync();
                while (await columnReader.ReadAsync()) columns.Add("[" + columnReader.GetString(0).Replace("]", "]]", StringComparison.Ordinal) + "]");
            }
            await using var command = new SqlCommand($"SELECT {string.Join(',', columns)} FROM [{schema}].[{table}] FOR JSON PATH;", connection);
            await using var reader = await command.ExecuteReaderAsync();
            var json = new StringBuilder();
            while (await reader.ReadAsync()) json.Append(reader.GetString(0));
            // The only database-specific seed is the IndexState epoch.
            var normalised = System.Text.RegularExpressions.Regex.Replace(json.ToString(), "\"CorpusEpoch\":\"[^\"]+\"", "\"CorpusEpoch\":\"fresh\"");
            result.Append(table).Append(':').AppendLine(normalised);
        }
        return result.ToString();
    }
}
