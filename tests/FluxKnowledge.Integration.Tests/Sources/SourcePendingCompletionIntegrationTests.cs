using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class SourcePendingCompletionIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Completion_acknowledges_only_manifest_paths_at_the_claimed_generation(bool includeDuePath, bool laterEdit)
    {
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "PendingCompletion-" + Guid.NewGuid().ToString("N"));
        var files = Path.Combine(area, "Sources");
        Directory.CreateDirectory(files);
        try
        {
            var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
            var commands = new NativeCorpusCommandService(new NativeOperationService(new SqlNativeOperationStore(factory, TimeProvider.System), []),
                new SqlNativeCorpusActionStore(factory, new SourceRootPathPolicy(new LocalIngressOptions([files])), new LocalPrivateContentDisclosure()));
            var mutation = new NativeCorpusMutation("root_create", JsonSerializer.SerializeToElement(new { path = files, displayName = "Pending completion", recursive = true, maximumFileBytes = 1024 }));
            var preview = await commands.PreviewAsync(mutation, "test", default);
            await commands.CommitAsync(mutation, preview.ConfirmationId, "pending-" + Guid.NewGuid().ToString("N"), "test", default);
            var control = new SqlSourceScanStore(factory, TimeProvider.System);
            var claim = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("test", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
            await using (var setup = factory.CreateDbContext())
            {
                setup.SourceRootScanStates.Add(new() { SourceRootId = claim.SourceRoot.Id.Value, BaselineEpoch = 1, Mode = "Journal", Generation = laterEdit ? 3 : 2 });
                setup.SourcePendingChanges.Add(new() { Id = Guid.NewGuid(), SourceRootId = claim.SourceRoot.Id.Value,
                    RelativePath = "due.txt", PathFingerprint = "due", Generation = laterEdit ? 3 : 1, ReasonFlags = 1 });
                setup.SourcePendingChanges.Add(new() { Id = Guid.NewGuid(), SourceRootId = claim.SourceRoot.Id.Value,
                    RelativePath = "deferred.txt", PathFingerprint = "deferred", Generation = 2, ReasonFlags = 1, NotBeforeUtc = DateTimeOffset.UtcNow.AddMinutes(1) });
                await setup.SaveChangesAsync();
            }
            using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "Retained"));
            var coordinator = new SqlSourceIncrementalCoordinator(factory, TimeProvider.System, new WindowsSourceChangeJournalReader(),
                new WindowsSourceObservationReader(), artifacts, new SourceJournalOptions { Enabled = true });
            var plan = new SourceIncrementalPlan(Guid.NewGuid(), 1, false, "Journal", null, 2,
                includeDuePath ? [new("due.txt", 1, false, 1)] : []);
            await coordinator.CompleteAsync(claim.SourceRoot, claim.ScanRequest, plan, [], [], new HashSet<SourceRevisionId>(), null, default);
            await using var verify = factory.CreateDbContext();
            var pending = await verify.SourcePendingChanges.Where(x => x.SourceRootId == claim.SourceRoot.Id.Value)
                .OrderBy(x => x.RelativePath).Select(x => new { x.RelativePath, x.Generation }).ToArrayAsync();
            Assert.Contains(pending, x => x.RelativePath == "deferred.txt" && x.Generation == 2);
            if (!includeDuePath || laterEdit)
                Assert.Contains(pending, x => x.RelativePath == "due.txt" && x.Generation == (laterEdit ? 3 : 1));
            else Assert.DoesNotContain(pending, x => x.RelativePath == "due.txt");
            Assert.Equal(includeDuePath && !laterEdit ? 1 : 2, pending.Length);
        }
        finally { Directory.Delete(area, true); }
    }
}
