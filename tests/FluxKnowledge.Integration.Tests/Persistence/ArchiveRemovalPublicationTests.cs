using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class ArchiveRemovalPublicationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removing_the_physical_archive_withdraws_published_members_and_records_projection_scope(bool segmented)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Published archive content.", segmented: segmented);
        var path = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "ArchiveRemoval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using var authority = PhysicalFileIdentity.OpenDirectoryLease(path);
            await using var db = environment.Factory.CreateDbContext();
            var now = DateTimeOffset.UtcNow;
            var root = new SourceRootConfigurationEntity { Id = Guid.NewGuid(), CanonicalPath = path, DisplayName = "Archive removal fixture",
                State = (int)SourceRootState.Enabled, Recursive = true, MaximumFileBytes = 1024, ConfigurationRevision = 1,
                ReconciliationCadenceSeconds = 900, CreatedAtUtc = now, UpdatedAtUtc = now };
            root.HealthEvidenceJson = System.Text.Json.JsonSerializer.Serialize(new { physicalIdentity = new { IdentityFingerprint = authority.Identity.IdentityFingerprint } });
            var owner = new SourceRevisionEntity { Id = Guid.NewGuid(), SourceRootId = root.Id, OriginKind = 0, Revision = 1,
                CanonicalPath = Path.Combine(path, "removed.zip"), ContentSha256 = new string('a',64), ByteLength = 1,
                StableSourceIdentity = "archive-owner:" + Guid.NewGuid(), Classification = "Archive", Extension = ".zip", DiscoveredAtUtc = now };
            var member = new SourceRevisionEntity { Id = Guid.NewGuid(), SourceRootId = root.Id, OriginKind = 1, ParentSourceRevisionId = owner.Id,
                Revision = 1, CanonicalPath = owner.CanonicalPath + "!member.txt", ContentSha256 = new string('b',64), ByteLength = 1,
                StableSourceIdentity = "archive-member:" + Guid.NewGuid(), Classification = "AcceptedUtf8Text", Extension = ".txt", DiscoveredAtUtc = now };
            db.SourceRootConfigurations.Add(root); db.SourceRevisions.AddRange(owner, member);
            await db.SaveChangesAsync();
            // Bind the synthetic published fixture without changing the production immutability contract.
            await db.PipelineRecords.ExecuteUpdateAsync(set => set.SetProperty(x => x.SourceRevisionId, member.Id));
            Assert.NotEmpty(await SqlPublishedPassageSelection.ReadVectorIdsAsync(db, default));
            root.State = (int)SourceRootState.Paused;
            await db.SaveChangesAsync();
            Assert.NotEmpty(await SqlPublishedPassageSelection.ReadVectorIdsAsync(db, default));
            root.State = (int)SourceRootState.Enabled;
            db.SourcePendingChanges.Add(new() { Id = Guid.NewGuid(), SourceRootId = root.Id, RelativePath = "removed.zip",
                PathFingerprint = SqlSourceChangeJournalStore.PathKey("removed.zip"), Generation = 1, ReasonFlags = SourcePendingReasons.RemovalPending });
            await db.SaveChangesAsync();
            Assert.Equal(1, await new SqlSourceRemovalReconciler(environment.Factory, TimeProvider.System, new WindowsSourceObservationReader()).ReconcileAsync(default));
            Assert.Empty(await SqlPublishedPassageSelection.ReadVectorIdsAsync(db, default));
            Assert.Null((await db.SourceRevisions.AsNoTracking().SingleAsync(x => x.Id == member.Id)).SuppressedAtUtc);
            if (segmented) Assert.True(await db.IndexProjectionChanges.AnyAsync(x => x.ScopeKind == 3 && x.ScopeId == root.Id));
        }
        finally { Directory.Delete(path, true); }
    }
}
