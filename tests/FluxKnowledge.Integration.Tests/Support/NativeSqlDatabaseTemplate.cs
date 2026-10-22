using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Data.SqlClient;

namespace FluxKnowledge.Integration.Tests.Support;

// One migration-derived backup per test process, never a cross-run cache.
internal sealed class NativeSqlDatabaseTemplate : IDisposable
{
    private static readonly Preparation Shared = new(CreateAsync);
    private readonly object _lifetime = new();
    private readonly string _server;
    private readonly string _root;
    private readonly string _backup;
    private readonly string _backupHash;
    private readonly long _backupLength;
    private readonly Guid _migrationAssembly = typeof(FluxKnowledgeDbContext).Module.ModuleVersionId;
    private int _activeRestores;
    private bool _preservedRestore;
    private bool _disposed;
    internal string BackupPath => _backup;
    internal Guid CorpusEpoch { get; }

    private NativeSqlDatabaseTemplate(string server, string root, string backup, Guid corpusEpoch)
    {
        _server = server; _root = root; _backup = backup;
        CorpusEpoch = corpusEpoch;
        _backupLength = new FileInfo(backup).Length;
        _backupHash = HashFile(backup);
    }

    internal static Task<NativeSqlDatabaseTemplate> GetAsync(string server, string root, CancellationToken cancellationToken = default)
        => Shared.GetAsync(server, root, cancellationToken);

    internal sealed class Preparation(Func<string, string, Task<NativeSqlDatabaseTemplate>> create)
    {
        private readonly object _sync = new();
        private Task<NativeSqlDatabaseTemplate>? _task;
        private string? _configuration;

        internal Task<NativeSqlDatabaseTemplate> GetAsync(string server, string root, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configuration = server + "\n" + root;
            Task<NativeSqlDatabaseTemplate> task;
            lock (_sync)
            {
                if (_configuration is not null && _configuration != configuration)
                    throw new InvalidOperationException("Disposable SQL template configuration changed within this test process.");
                _configuration = configuration;
                task = _task ??= StartAsync(server, root);
            }
            // Cancellation belongs to the waiter, never the shared preparation.
            return task.WaitAsync(cancellationToken);
        }

        private async Task<NativeSqlDatabaseTemplate> StartAsync(string server, string root) =>
            await create(server, root).ConfigureAwait(false);
    }

    internal static async Task<NativeSqlDatabaseTemplate> CreateAsync(string server, string root)
    {
        NativeSqlServerFixture.ValidateServerConnectionString(server);
        root = NativeSqlServerFixture.ValidateTestFileRoot(root);
        var source = new NativeSqlServerFixture(server, root) { UseDirectMigrations = true };
        var backup = Path.Combine(root, source.DatabaseName + "_" + Guid.NewGuid().ToString("N") + ".bak");
        var partial = backup + ".partial";
        Guid corpusEpoch;
        await source.InitializeAsync().ConfigureAwait(false);
        try
        {
            await using var connection = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = "master", Pooling = false }.ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using (var identity = new SqlCommand($"SELECT CorpusEpoch FROM [{source.DatabaseName}].dbo.IndexState WHERE Id=1;", connection))
                corpusEpoch = (Guid)(await identity.ExecuteScalarAsync().ConfigureAwait(false))!;
            if (File.Exists(backup) || File.Exists(partial)) throw new InvalidOperationException("Template destination must be new.");
            await using (var command = new SqlCommand($"BACKUP DATABASE [{source.DatabaseName}] TO DISK=@backup WITH COPY_ONLY, CHECKSUM;", connection))
            {
                command.Parameters.AddWithValue("@backup", partial);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await using (var command = new SqlCommand("RESTORE VERIFYONLY FROM DISK=@backup WITH CHECKSUM;", connection))
            {
                command.Parameters.AddWithValue("@backup", partial);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            try { await source.DisposeAsync().ConfigureAwait(false); }
            catch { Console.Error.WriteLine("Template preparation cleanup failed; preserve the owned catalogue and backup evidence."); }
            throw;
        }
        await source.DisposeAsync().ConfigureAwait(false);
        File.Move(partial, backup); // Same-volume atomic publication; never overwrite.
        var template = new NativeSqlDatabaseTemplate(server, root, backup, corpusEpoch);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => template.Dispose();
        return template;
    }

    internal async Task RestoreAsync(string name, CancellationToken cancellationToken = default,
        Func<Task>? afterRestore = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeSqlServerFixture.ValidateGeneratedCatalog(name);
        _ = NativeSqlServerFixture.ValidateTestFileRoot(_root);
        var data = Path.Combine(_root, name + ".mdf");
        var log = Path.Combine(_root, name + "_log.ldf");
        // This is required recovery evidence, independent of optional timings.
        var manifest = Path.Combine(_root, "template-restore-" + name + ".json");
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeRestores++;
        }
        var attempted = false;
        try
        {
            if ((File.GetAttributes(_backup) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Template backup cannot be a reparse point.");
            if (_migrationAssembly != typeof(FluxKnowledgeDbContext).Module.ModuleVersionId ||
                new FileInfo(_backup).Length != _backupLength || HashFile(_backup) != _backupHash)
                throw new InvalidOperationException("This process's template identity changed; refuse restore.");
            if (File.Exists(data) || File.Exists(log)) throw new InvalidOperationException("Restore files must be absent.");
            await using var connection = new SqlConnection(new SqlConnectionStringBuilder(_server) { InitialCatalog = "master", Pooling = false }.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand($"""
                IF DB_ID(N'{name}') IS NOT NULL THROW 51000, 'Restore catalogue must be absent.', 1;
                RESTORE DATABASE [{name}] FROM DISK=@backup
                    WITH MOVE @logicalData TO @data, MOVE @logicalLog TO @log, CHECKSUM, RECOVERY;
                """, connection);
            // The source logical names are recorded by the backup, not guessed from a destination.
            await using (var files = new SqlCommand("RESTORE FILELISTONLY FROM DISK=@backup;", connection))
            {
                files.Parameters.AddWithValue("@backup", _backup);
                await using var reader = await files.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var names = new List<(string Name, string Type)>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) names.Add((reader.GetString(0), reader.GetString(2)));
                if (names.Count != 2 || names.Count(value => value.Type == "D") != 1 || names.Count(value => value.Type == "L") != 1)
                    throw new InvalidOperationException("Template file manifest is not the verified two-file layout.");
                command.Parameters.AddWithValue("@logicalData", names.Single(value => value.Type == "D").Name);
                command.Parameters.AddWithValue("@logicalLog", names.Single(value => value.Type == "L").Name);
            }
            command.Parameters.AddWithValue("@backup", _backup);
            command.Parameters.AddWithValue("@data", data);
            command.Parameters.AddWithValue("@log", log);
            void Receipt(string outcome) => File.WriteAllText(manifest, JsonSerializer.Serialize(new
                { name, data, log, backup = _backup, sha256 = _backupHash, bytes = _backupLength, migration_assembly = _migrationAssembly, outcome }));
            Receipt("attempting");
            attempted = true;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (afterRestore is not null) await afterRestore().ConfigureAwait(false);
            await NativeSqlServerFixture.VerifyOwnedDatabaseAsync(_server, name, _root).ConfigureAwait(false);
            await using (var identity = new SqlConnection(new SqlConnectionStringBuilder(_server) { InitialCatalog = name, Pooling = false }.ConnectionString))
            {
                await identity.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var refresh = new SqlCommand("UPDATE dbo.IndexState SET CorpusEpoch=NEWID() WHERE Id=1;", identity);
                if (await refresh.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Restored corpus identity was not refreshed.");
            }
            Receipt("verified-fresh-identity");
        }
        catch (Exception exception)
        {
            if (attempted)
            {
                lock (_lifetime) _preservedRestore = true;
                throw new InvalidOperationException($"Restore outcome requires preservation; no automatic cleanup. Inspect {manifest}.", exception);
            }
            throw;
        }
        finally { lock (_lifetime) _activeRestores--; }
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            if (_activeRestores != 0 || _preservedRestore) return;
            try
            {
                _ = NativeSqlServerFixture.ValidateTestFileRoot(_root);
                if (!File.Exists(_backup)) return;
                if ((File.GetAttributes(_backup) & FileAttributes.ReparsePoint) != 0) return;
                if (File.Exists(_backup) && new FileInfo(_backup).Length == _backupLength && HashFile(_backup) == _backupHash)
                    File.Delete(_backup);
            }
            catch (IOException) { Console.Error.WriteLine("The owned SQL template backup is preserved because cleanup was unavailable."); }
            catch (UnauthorizedAccessException) { Console.Error.WriteLine("The owned SQL template backup is preserved because cleanup was unavailable."); }
            catch (InvalidOperationException) { Console.Error.WriteLine("The owned SQL template backup is preserved because its storage boundary changed."); }
        }
    }

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
