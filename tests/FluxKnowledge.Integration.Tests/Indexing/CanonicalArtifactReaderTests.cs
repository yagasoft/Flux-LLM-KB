using System.Data;
using System.Diagnostics;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class CanonicalArtifactReaderTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Unfenced_reader_keeps_the_configured_transient_read_strategy()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Retry reader seed.", embed: false);
        await using var setup = environment.Factory.CreateDbContext();
        var artifact = await setup.Artifacts.SingleAsync(x => x.Stage == (int)PipelineStage.CanonicalIndex);
        await using var connection = new FailFirstReadConnection(fixture.ConnectionString);
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection)
            .ReplaceService<IExecutionStrategyFactory, ReadRetryFactory>().Options;
        await using var context = new FluxKnowledgeDbContext(options);
        var actual = await SqlCanonicalArtifactReader.ReadAsync(context, artifact.PipelineRecordId, artifact.SourceRevision, default);
        Assert.Equal(artifact.SearchText, actual!.SearchText);
        Assert.Equal(2, connection.Attempts);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        // An existing execution strategy owns the transaction and its retries.
        await using var fenced = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(fixture.ConnectionString).ReplaceService<IExecutionStrategyFactory, ReadRetryFactory>().Options);
        await fenced.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await fenced.Database.BeginTransactionAsync();
            Assert.Equal(artifact.SearchText, (await SqlCanonicalArtifactReader.ReadAsync(fenced, artifact.PipelineRecordId, artifact.SourceRevision, default))!.SearchText);
            await transaction.RollbackAsync();
        });
    }
    [NativeSqlServerTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"label\":\"Arabic العربية and 😀\"}")]
    public async Task Reader_preserves_every_untracked_field_and_connection_ownership(string? metadata)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Canonical reader seed.", embed: false);
        await using var setup = environment.Factory.CreateDbContext();
        var expected = await setup.Artifacts.SingleAsync(x => x.Stage == (int)PipelineStage.CanonicalIndex);
        expected.SearchText = "Exact Unicode: العربية 😀\r\n\uFFFE\uFFFF e\u0301.  ";
        expected.ContentHash = CodeDisclosureIntegrity.Hash(expected.SearchText);
        expected.DocumentMetadataJson = metadata;
        await setup.SaveChangesAsync();
        await using var context = environment.Factory.CreateDbContext();
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        var actual = await SqlCanonicalArtifactReader.ReadAsync(context, expected.PipelineRecordId, expected.SourceRevision, default);
        Assert.NotNull(actual);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        await context.Database.OpenConnectionAsync();
        _ = await SqlCanonicalArtifactReader.ReadAsync(context, expected.PipelineRecordId, expected.SourceRevision, default);
        Assert.Equal(ConnectionState.Open, context.Database.GetDbConnection().State);
        Assert.Null(await SqlCanonicalArtifactReader.ReadAsync(context, Guid.NewGuid(), expected.SourceRevision, default));
        Assert.Null(await SqlCanonicalArtifactReader.ReadAsync(context, expected.PipelineRecordId, expected.SourceRevision + 1, default));
    }

    [NativeSqlServerFact]
    public async Task Large_canonical_input_is_read_within_the_worker_budget_without_changing_text()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Large canonical reader seed.", embed: false);
        await using var setup = environment.Factory.CreateDbContext();
        var expected = await setup.Artifacts.SingleAsync(x => x.Stage == (int)PipelineStage.CanonicalIndex);
        expected.SearchText = new string('x', 21_161_221) + " العربية 😀\uFFFE\uFFFF\r\n";
        expected.ContentHash = CodeDisclosureIntegrity.Hash(expected.SearchText);
        await setup.SaveChangesAsync();
        await using var context = environment.Factory.CreateDbContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var timer = Stopwatch.StartNew();
        var actual = await SqlCanonicalArtifactReader.ReadAsync(context, expected.PipelineRecordId, expected.SourceRevision, deadline.Token);
        timer.Stop();
        Assert.NotNull(actual);
        Assert.Equal(expected.SearchText, actual.SearchText);
        Assert.Equal(expected.ContentHash, CodeDisclosureIntegrity.Hash(actual.SearchText));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), $"Canonical read took {timer.Elapsed}.");
    }

    [NativeSqlServerFact]
    public async Task Reader_observes_the_current_transaction_and_preserves_cancellation()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Transaction reader seed.", embed: false);
        await using var context = environment.Factory.CreateDbContext();
        var artifact = await context.Artifacts.SingleAsync(x => x.Stage == (int)PipelineStage.CanonicalIndex);
        var original = artifact.SearchText;
        await using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            artifact.SearchText = "Uncommitted canonical input.";
            artifact.ContentHash = CodeDisclosureIntegrity.Hash(artifact.SearchText);
            await context.SaveChangesAsync();
            var actual = await SqlCanonicalArtifactReader.ReadAsync(context, artifact.PipelineRecordId, artifact.SourceRevision, default);
            Assert.Equal(artifact.SearchText, actual!.SearchText);
            await transaction.RollbackAsync();
        }
        Assert.Equal(original, (await SqlCanonicalArtifactReader.ReadAsync(context, artifact.PipelineRecordId, artifact.SourceRevision, default))!.SearchText);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqlCanonicalArtifactReader.ReadAsync(context, artifact.PipelineRecordId, artifact.SourceRevision, cancelled.Token));
    }

    private sealed class SyntheticTransientRead : Exception;
    private sealed class FailFirstReadConnection(string connectionString) : DbConnection
    {
        private readonly SqlConnection inner = new(connectionString);
        public int Attempts { get; private set; }
        [AllowNull] public override string ConnectionString { get => inner.ConnectionString; set => inner.ConnectionString = value; }
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;
        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
        public override void Close() => inner.Close();
        public override Task CloseAsync() => inner.CloseAsync();
        public override void Open() => inner.Open();
        public override Task OpenAsync(CancellationToken cancellationToken) => inner.OpenAsync(cancellationToken);
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => inner.BeginTransaction(isolationLevel);
        protected override DbCommand CreateDbCommand() => new FailFirstReadCommand(inner.CreateCommand(), this);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        private sealed class FailFirstReadCommand(SqlCommand command, FailFirstReadConnection owner) : DbCommand
        {
            [AllowNull] public override string CommandText { get => command.CommandText; set => command.CommandText = value; }
            public override int CommandTimeout { get => command.CommandTimeout; set => command.CommandTimeout = value; }
            public override CommandType CommandType { get => command.CommandType; set => command.CommandType = value; }
            public override bool DesignTimeVisible { get => command.DesignTimeVisible; set => command.DesignTimeVisible = value; }
            public override UpdateRowSource UpdatedRowSource { get => command.UpdatedRowSource; set => command.UpdatedRowSource = value; }
            protected override DbConnection? DbConnection { get => owner; set => command.Connection = value is FailFirstReadConnection wrapped ? wrapped.inner : (SqlConnection?)value; }
            protected override DbTransaction? DbTransaction { get => command.Transaction; set => command.Transaction = (SqlTransaction?)value; }
            protected override DbParameterCollection DbParameterCollection => command.Parameters;
            public override void Cancel() => command.Cancel();
            public override int ExecuteNonQuery() => command.ExecuteNonQuery();
            public override object? ExecuteScalar() => command.ExecuteScalar();
            public override void Prepare() => command.Prepare();
            protected override DbParameter CreateDbParameter() => command.CreateParameter();
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => command.ExecuteReader(behavior);
            protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
            {
                if (++owner.Attempts == 1) throw new SyntheticTransientRead();
                return await command.ExecuteReaderAsync(behavior, cancellationToken);
            }
            protected override void Dispose(bool disposing) { if (disposing) command.Dispose(); base.Dispose(disposing); }
        }
    }
    public sealed class ReadRetryFactory(ExecutionStrategyDependencies dependencies) : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() => new ReadRetryStrategy(dependencies);
    }
    private sealed class ReadRetryStrategy(ExecutionStrategyDependencies dependencies) : ExecutionStrategy(dependencies, 1, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is SyntheticTransientRead;
    }
}
