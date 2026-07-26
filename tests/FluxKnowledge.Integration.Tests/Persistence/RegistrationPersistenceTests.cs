using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class RegistrationPersistenceTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Same_bytes_return_original_ids_and_changed_bytes_append_a_linked_revision()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var ingressRoot = Path.Combine(
            Path.GetTempPath(),
            $"FluxKnowledgeRegistration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        var path = Path.Combine(ingressRoot, "a.txt");
        await File.WriteAllTextAsync(path, "first");
        var factory = SqlTestData.CreateFactory(_fixture);
        var handler = new RegisterUtf8FileHandler(
            new Utf8FileSourceReader(new LocalIngressOptions([ingressRoot])),
            new SqlPipelineStore(factory));

        try
        {
            var first = await handler.HandleAsync(
                new RegisterUtf8FileCommand(path, "integration-test", "a.txt"),
                CancellationToken.None);
            var duplicate = await handler.HandleAsync(
                new RegisterUtf8FileCommand(path, "integration-test", "a.txt"),
                CancellationToken.None);
            await File.WriteAllTextAsync(path, "second");
            var revision = await handler.HandleAsync(
                new RegisterUtf8FileCommand(path, "integration-test", "a.txt"),
                CancellationToken.None);

            Assert.Equal(first.PipelineRecordId, duplicate.PipelineRecordId);
            Assert.Equal(first.InitialJobId, duplicate.InitialJobId);
            Assert.Equal(first.InitialDispatchMessageId, duplicate.InitialDispatchMessageId);
            Assert.True(duplicate.ExistingReceipt);
            Assert.NotEqual(first.PipelineRecordId, revision.PipelineRecordId);
            Assert.False(revision.ExistingReceipt);

            await using var context = await factory.CreateDbContextAsync();
            Assert.Single(await context.SourceIdentities.ToListAsync());
            var records = await context.PipelineRecords
                .OrderBy(record => record.Revision)
                .ToListAsync();
            Assert.Equal(2, records.Count);
            Assert.Equal(records[0].Id, records[0].RootLineageRecordId);
            Assert.Equal(records[0].Id, records[1].RootLineageRecordId);
            Assert.Equal(records[0].Id, records[1].ParentRevisionRecordId);
            Assert.Equal(2, await context.Jobs.CountAsync());
            Assert.Equal(2, await context.OutboxMessages.CountAsync());
        }
        finally
        {
            Directory.Delete(ingressRoot, recursive: true);
        }
    }
}
