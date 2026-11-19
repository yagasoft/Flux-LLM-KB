using System.Data.Common;
using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Public_delete_counts_distinct_and_shared_artifacts_without_per_artifact_queries()
    {
        var now = DateTimeOffset.UtcNow;
        var deleting = await SeedRootAsync("artifact-inventory-delete", SourceRootState.Enabled, now);
        var control = await SeedRootAsync("artifact-inventory-control", SourceRootState.Enabled, now);
        await using (var setup = CreateContext())
        {
            // Sixty-four identities, each retained twice; one shared with the
            // control root. Case and length differences remain separate targets.
            for (var i = 0; i < 128; i++) AddArtifact(deleting.RootId, "blob-" + (i % 64), (i % 64).ToString("x64"), 4);
            AddArtifact(control.RootId, "blob-0", 0.ToString("x64"), 4);
            AddArtifact(deleting.RootId, "Case", new string('a', 64), 4);
            AddArtifact(deleting.RootId, "case", new string('a', 64), 4);
            AddArtifact(deleting.RootId, "Case", new string('a', 64), 5);
            AddArtifact(control.RootId, "Case", new string('a', 64), 4);
            await setup.SaveChangesAsync();

            void AddArtifact(Guid root, string path, string hash, long length)
            {
                var id = Guid.NewGuid();
                setup.SourceRevisions.Add(new SourceRevisionEntity
                {
                    Id = id, SourceRootId = root, StableSourceIdentity = "artifact-inventory:" + id.ToString("N"), Revision = 1,
                    ContentSha256 = hash, CanonicalPath = "C:\\artifact-inventory\\" + id.ToString("N"),
                    Classification = "AcceptedUtf8Text", Extension = ".txt", ByteLength = length, DiscoveredAtUtc = now
                });
                setup.SourceArtifacts.Add(new SourceArtifactEntity
                {
                    Id = Guid.NewGuid(), SourceRevisionId = id, StoreRelativePath = path, ContentSha256 = hash,
                    ByteLength = length, ChecksumVerifiedAtUtc = now, ReferenceCount = 1
                });
            }
        }
        var observer = new ArtifactInventoryCommands();
        var factory = new ArtifactInventoryFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString).AddInterceptors(observer).Options);
        var service = new NativeCorpusCommandService(new SqlNativeOperationStore(factory, TimeProvider.System),
            new SqlNativeCorpusActionStore(factory, new NoRootCreationPolicy(), new LocalPrivateContentDisclosure()));
        var mutation = new NativeCorpusMutation("root_delete", JsonSerializer.SerializeToElement(new { rootId = deleting.RootId }));
        var preview = await service.PreviewAsync(mutation, "test", default);
        observer.Reads = 0;
        await service.CommitAsync(mutation, preview.ConfirmationId, "artifact-inventory-delete:" + deleting.RootId.ToString("N"), "test", default);

        await using var check = CreateContext();
        var deletion = await check.SourceDeletionOperations.SingleAsync(value => value.SourceRootId == deleting.RootId);
        Assert.Equal(67, deletion.SourceArtifactCount);
        Assert.Equal(2, deletion.SharedArtifactCount);
        Assert.InRange(observer.Reads, 1, 2);
        Assert.Equal(2, await check.SourceArtifacts.CountAsync(value => value.SourceRevision.SourceRootId == control.RootId));
    }

    private sealed class ArtifactInventoryFactory(DbContextOptions<FluxKnowledgeDbContext> options) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(options);
    }

    private sealed class ArtifactInventoryCommands : DbCommandInterceptor
    {
        public int Reads { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[SourceArtifacts]", StringComparison.Ordinal)) Reads++;
            return ValueTask.FromResult(result);
        }
    }
}
