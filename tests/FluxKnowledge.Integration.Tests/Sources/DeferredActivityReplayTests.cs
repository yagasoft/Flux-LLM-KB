using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class DeferredActivityReplayTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Deferred_replay_links_one_pipeline_record_job_and_outbox_without_mutating_the_deferred_activity_key()
    {
        using var seeded = await SeedAsync();
        var capability = new RegisteredSourceCapability(seeded.CapabilityId, "text-metadata", "phase-3a-v1",
            ExecutionClass.InProcess, "phase-3a-inprocess-text-metadata-v1", true);
        var request = new DeferredContentReplayRequest(seeded.ActivityId, seeded.IdempotencyKey, "text-metadata",
            seeded.CapabilityId, "phase-3a-v1", "phase-3a-inprocess-text-metadata-v1");

        var store = new SqlRetainedTextRegistrationStore(
            new ContextFactory(_fixture.ConnectionString),
            TimeProvider.System,
            seeded.ArtifactRoot);

        Assert.Equal(1, await store.ReplayActivityAsync(request, capability, CancellationToken.None));
        Assert.Equal(0, await store.ReplayActivityAsync(request, capability, CancellationToken.None));

        await using var verification = CreateContext();
        var activity = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
        Assert.Equal((int)ExecutionClass.DeferredCapability, activity.ExecutionClass);
        Assert.Equal((int)SourceActivityState.DeferredUnsupported, activity.State);
        Assert.Equal("phase-3a-v1", activity.ProcessorVersion);
        Assert.Equal(seeded.Hash, activity.InputFingerprint);
        var record = await verification.PipelineRecords.SingleAsync(value => value.Id == activity.ResultingPipelineRecordId);
        Assert.Equal(seeded.RevisionId, record.SourceRevisionId);
        Assert.Equal(1, await verification.Jobs.CountAsync(value => value.PipelineRecordId == record.Id));
        Assert.Equal(1, await verification.OutboxMessages.CountAsync(value => value.PipelineRecordId == record.Id));
    }

    [NativeSqlServerFact]
    public async Task Deferred_document_parsing_cannot_be_routed_to_the_retained_utf8_extract_operation()
    {
        using var seeded = await SeedAsync();
        await using (var setup = CreateContext())
        {
            var activity = await setup.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId);
            activity.ActivityKind = (int)SourceActivityKind.DocumentParsing;
            await setup.SaveChangesAsync();
        }
        var capability = new RegisteredSourceCapability(seeded.CapabilityId, "text-metadata", "phase-3a-v1",
            ExecutionClass.InProcess, "phase-3a-inprocess-text-metadata-v1", true);
        var request = new DeferredContentReplayRequest(seeded.ActivityId, seeded.IdempotencyKey, "text-metadata",
            seeded.CapabilityId, "phase-3a-v1", "phase-3a-inprocess-text-metadata-v1");

        Assert.Equal(0, await new SqlRetainedTextRegistrationStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System)
            .ReplayActivityAsync(request, capability, CancellationToken.None));
        await using var verification = CreateContext();
        Assert.Null((await verification.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId)).ResultingPipelineRecordId);
        Assert.Empty(await verification.Jobs
            .Where(value => value.PipelineRecord.SourceRevisionId == seeded.RevisionId)
            .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Deferred_replay_requires_matching_retained_capability_evidence()
    {
        using var seeded = await SeedAsync();
        var capability = new RegisteredSourceCapability(seeded.CapabilityId, "text-metadata", "phase-3a-v1",
            ExecutionClass.InProcess, "phase-3a-inprocess-text-metadata-v1", true);
        var request = new DeferredContentReplayRequest(seeded.ActivityId, seeded.IdempotencyKey, "text-metadata",
            seeded.CapabilityId, "phase-3a-v1", "phase-3a-inprocess-text-metadata-v1");

        await using (var setup = CreateContext())
        {
            setup.DeferredCapabilities.RemoveRange(await setup.DeferredCapabilities
                .Where(value => value.SourceRevisionId == seeded.RevisionId)
                .ToListAsync());
            await setup.SaveChangesAsync();
        }
        await using (var check = CreateContext())
        {
            Assert.Empty(await check.DeferredCapabilities.Where(value => value.SourceRevisionId == seeded.RevisionId).ToListAsync());
        }

        Assert.Equal(0, await new SqlRetainedTextRegistrationStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System)
            .ReplayActivityAsync(request, capability, CancellationToken.None));
        await using var verification = CreateContext();
        Assert.Null((await verification.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId)).ResultingPipelineRecordId);
    }

    private async Task<Seed> SeedAsync()
    {
        var bytes = new UTF8Encoding(false).GetBytes("test");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactRoot = Path.Combine(Path.GetTempPath(), "fluxknowledge-replay", Guid.NewGuid().ToString("N"));
        var retainedPath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(artifactRoot, retainedPath))!);
        await File.WriteAllBytesAsync(Path.Combine(artifactRoot, retainedPath), bytes);
        var now = DateTimeOffset.Parse("2026-08-08T00:00:00+00:00");
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var capabilityId = Guid.Parse("9c56d5b2-c931-4c8b-ab66-fd0601e9c1df");
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = rootId, CanonicalPath = $"C:\\replay\\{rootId:N}", DisplayName = "Replay", State = 0, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]", MaximumFileBytes = 16 * 1024 * 1024, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
        context.SourceRevisions.Add(new SourceRevisionEntity { Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"replay:{revisionId:N}", Revision = 1, ContentSha256 = hash, CanonicalPath = $"C:\\replay\\{revisionId:N}.txt", Classification = "AcceptedUtf8Text", Extension = ".txt", ByteLength = 4, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}" });
        context.SourceArtifacts.Add(new SourceArtifactEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash, StoreRelativePath = retainedPath, ByteLength = bytes.Length, ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
        context.SourceActivities.Add(new SourceActivityEntity { Id = activityId, SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.TextExtraction, ExecutionClass = (int)ExecutionClass.DeferredCapability, ProcessorVersion = "phase-3a-v1", InputFingerprint = hash, RequiredCapability = "text-metadata", State = (int)SourceActivityState.DeferredUnsupported, Reason = "missing", CreatedAtUtc = now, UpdatedAtUtc = now });
        context.DeferredCapabilities.Add(new DeferredCapabilityEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ArtifactFingerprint = hash, RequiredCapability = "text-metadata", Provenance = "test", CreatedAtUtc = now });
        await context.SaveChangesAsync();
        var activity = SourceActivity.Restore(new SourceActivityId(activityId), new SourceRevisionId(revisionId), SourceActivityKind.TextExtraction, ExecutionClass.DeferredCapability, "phase-3a-v1", hash, "text-metadata", SourceActivityState.DeferredUnsupported, "missing");
        return new Seed(revisionId, activityId, capabilityId, hash, activity.IdempotencyKey, artifactRoot);
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);
    private sealed class ContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()).Options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed record Seed(
        Guid RevisionId,
        Guid ActivityId,
        Guid CapabilityId,
        string Hash,
        string IdempotencyKey,
        string ArtifactRoot) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(ArtifactRoot))
            {
                Directory.Delete(ArtifactRoot, recursive: true);
            }
        }
    }
}
