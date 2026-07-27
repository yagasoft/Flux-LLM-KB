using FluxKnowledge.Application.Pipeline;
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

        var reader = new SqlProjectionReader(factory);
        var projection = await reader.ReadPipelineRecordAsync(
            receipt.PipelineRecordId.Value,
            CancellationToken.None);

        Assert.NotNull(projection);
        Assert.Equal(receipt.PipelineRecordId.Value, projection.Id);
        Assert.Equal("WorkerQueued", projection.Status);
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
