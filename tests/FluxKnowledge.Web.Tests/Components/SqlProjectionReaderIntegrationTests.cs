using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class SqlProjectionReaderIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Single_record_projection_translates_and_returns_the_registered_record()
    {
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        var store = new SqlPipelineStore(factory);
        var receipt = await store.RegisterAsync(
            new Utf8FileRegistration(
                $"C:\\ingress\\{Guid.NewGuid():N}.txt",
                new string('a', 64),
                "integration-test",
                null),
            CancellationToken.None);

        var reader = new SqlProjectionReader(factory, new FixedRecoveryStatus(new DerivedIndexRecoverySnapshot(
            DerivedIndexRecoveryState.Healthy,
            null,
            null,
            null,
            null,
            0)));
        var projection = await reader.ReadPipelineRecordAsync(
            receipt.PipelineRecordId.Value,
            CancellationToken.None);

        Assert.NotNull(projection);
        Assert.Equal(receipt.PipelineRecordId.Value, projection.Id);
        Assert.Equal("WorkerQueued", projection.Status);
    }

    [NativeSqlServerFact]
    public async Task Overview_projection_includes_the_safe_derived_index_recovery_summary()
    {
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        var reader = new SqlProjectionReader(factory, new FixedRecoveryStatus(new DerivedIndexRecoverySnapshot(
            DerivedIndexRecoveryState.RetryScheduled,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
            DateTimeOffset.Parse("2026-07-27T08:00:05Z"),
            DerivedIndexRecoveryFailureCategory.TransientIo,
            4)));

        var overview = await reader.ReadOverviewAsync(CancellationToken.None);

        Assert.Equal("RetryScheduled", overview.IndexRecovery.State);
        Assert.Equal("aaaaaaaabbbbccccddddeeeeeeeeeeee", overview.IndexRecovery.ActiveGeneration);
        Assert.Equal(DateTimeOffset.Parse("2026-07-27T08:00:05Z"), overview.IndexRecovery.NextRetryAtUtc);
        Assert.Equal("TransientIo", overview.IndexRecovery.FailureCategory);
        Assert.Equal(4, overview.IndexRecovery.CleanedCandidateCount);
    }

    private sealed class FixedRecoveryStatus(DerivedIndexRecoverySnapshot snapshot)
        : IDerivedIndexRecoveryStatus
    {
        public DerivedIndexRecoverySnapshot Snapshot { get; } = snapshot;
    }

    private sealed class TestDbContextFactory(string connectionString)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connectionString)
                .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }
}
