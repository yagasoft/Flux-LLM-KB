using System.Security.Cryptography;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class DerivedIndexRecoveryIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Recovery_snapshot_reads_active_immutable_membership_and_referenced_generations()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var activeGenerationId = Guid.NewGuid();
        var referencedGenerationId = Guid.NewGuid();
        var vectorIds = await SeedSnapshotAsync(factory, activeGenerationId, referencedGenerationId);
        var store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);

        var snapshot = await store.ReadActiveAsync(CancellationToken.None);

        Assert.Equal(activeGenerationId, snapshot.ActiveGenerationId);
        Assert.NotNull(snapshot.Generation);
        Assert.Equal(activeGenerationId, snapshot.Generation!.Id);
        Assert.Equal(vectorIds.Order(), snapshot.Membership.Select(member => member.VectorId));
        Assert.Contains(activeGenerationId, snapshot.ReferencedGenerationIds);
        Assert.Contains(referencedGenerationId, snapshot.ReferencedGenerationIds);
    }

    [NativeSqlServerFact]
    public async Task Recovery_snapshot_retains_a_shared_sql_index_path_reference()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var activeGenerationId = Guid.NewGuid();
        var referencedGenerationId = Guid.NewGuid();
        const string activeIndexPath = "pending";
        const string sharedIndexPath = @"C:\flux\indexes\shared";
        const string sharedIndexPathWithDifferentCasing = @"c:\FLUX\INDEXES\SHARED";
        await SeedSnapshotAsync(factory, activeGenerationId, referencedGenerationId);
        await using (var context = await factory.CreateDbContextAsync())
        {
            var referencedGeneration = await context.IndexGenerations
                .SingleAsync(generation => generation.Id == referencedGenerationId);
            referencedGeneration.IndexPath = @"C:\flux\indexes\referenced";
            context.IndexGenerations.AddRange(
                CreateGeneration(Guid.NewGuid(), sharedIndexPath),
                CreateGeneration(Guid.NewGuid(), sharedIndexPathWithDifferentCasing));
            await context.SaveChangesAsync();
        }

        var store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);

        var snapshot = await store.ReadActiveAsync(CancellationToken.None);

        Assert.Contains(activeIndexPath, snapshot.ReferencedIndexPaths);
        Assert.Contains(snapshot.ReferencedIndexPaths, path =>
            string.Equals(path, sharedIndexPathWithDifferentCasing, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, snapshot.ReferencedIndexPaths.Count(path =>
            string.Equals(path, sharedIndexPath, StringComparison.OrdinalIgnoreCase)));
    }

    [NativeSqlServerFact]
    public async Task Exclusive_recovery_lease_allows_only_one_holder()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);
        var otherStore = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);

        var first = await store.TryAcquireExclusiveLeaseAsync(
            TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(first);
        await using var heldLease = first!;
        var second = await otherStore.TryAcquireExclusiveLeaseAsync(
            TimeSpan.Zero, CancellationToken.None);

        Assert.Null(second);
    }

    [NativeSqlServerFact]
    public async Task Disposed_recovery_lease_can_be_reacquired_and_double_disposed()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);

        var first = await store.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(first);
        await first!.DisposeAsync();
        await first.DisposeAsync();

        var second = await store.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(second);
        await second!.DisposeAsync();
    }

    [NativeSqlServerFact]
    public async Task Lease_disposal_closes_a_broken_session_before_the_next_acquisition()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);
        var lease = await store.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(lease);
        var field = lease!.GetType().GetField(
            "_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var connection = Assert.IsType<SqlConnection>(field!.GetValue(lease));
        await connection.CloseAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.DisposeAsync());

        var reacquired = await store.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(reacquired);
        await reacquired!.DisposeAsync();
    }

    [NativeSqlServerFact]
    public async Task Cancelled_recovery_lease_wait_preserves_cancellation()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);
        var otherStore = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);
        var held = await store.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(held);
        var heldLease = held!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await otherStore.TryAcquireExclusiveLeaseAsync(TimeSpan.FromSeconds(5), cancellation.Token));
        await heldLease.DisposeAsync();

        var recovered = await otherStore.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(recovered);
        await recovered!.DisposeAsync();
    }

    [NativeSqlServerFact]
    public async Task Recovery_audit_persists_only_safe_fields()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var store = new SqlDerivedIndexRecoveryStore(
            SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var generationId = Guid.NewGuid();

        await store.AppendAuditAsync(
            new DerivedIndexRecoveryAuditEvent(
                "rebuild_succeeded", generationId, null, 1,
                TimeSpan.FromSeconds(1), null, 0),
            CancellationToken.None);
        await using var context = await SqlTestData.CreateFactory(_fixture)
            .CreateDbContextAsync();
        var audit = await context.AuditEvents
            .OrderByDescending(item => item.Id)
            .FirstAsync();

        Assert.Null(audit.PipelineRecordId);
        Assert.Equal("derived_index_recovery", audit.EventType);
        Assert.Equal("DerivedIndexRecoveryService", audit.Actor);
        Assert.Contains("rebuild_succeeded", audit.DetailsJson, StringComparison.Ordinal);
        Assert.Contains(generationId.ToString("D"), audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [NativeSqlServerFact]
    public async Task Recovery_audit_bounds_and_sanitises_hostile_category_input()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var store = new SqlDerivedIndexRecoveryStore(
            SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var hostileCategory = "C:\\recovery\\password=secret\\" + new string('x', 4_000);

        await store.AppendAuditAsync(
            new DerivedIndexRecoveryAuditEvent(
                hostileCategory, Guid.NewGuid(), null, int.MaxValue,
                TimeSpan.MaxValue, null, int.MaxValue),
            CancellationToken.None);
        await using var context = await SqlTestData.CreateFactory(_fixture)
            .CreateDbContextAsync();
        var audit = await context.AuditEvents.OrderByDescending(item => item.Id).FirstAsync();

        Assert.True(audit.DetailsJson.Length < 512);
        Assert.DoesNotContain("C:\\", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("password", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"category\":\"unknown\"", audit.DetailsJson, StringComparison.Ordinal);
    }

    private static async Task<long[]> SeedSnapshotAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        Guid activeGenerationId,
        Guid referencedGenerationId)
    {
        await using var context = await factory.CreateDbContextAsync();
        var sourceId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var values = new byte[1024];
        context.IndexGenerations.AddRange(
            CreateGeneration(activeGenerationId),
            CreateGeneration(referencedGenerationId));
        context.SourceIdentities.Add(new SourceIdentityEntity
        {
            Id = sourceId,
            SourceKind = "test",
            StableKey = $"snapshot-{sourceId:N}",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        context.PipelineRecords.Add(new PipelineRecordEntity
        {
            Id = recordId,
            SourceIdentityId = sourceId,
            Revision = 1,
            ContentHash = new string('a', 64),
            RootLineageRecordId = recordId,
            RegisteredAtUtc = DateTimeOffset.UtcNow
        });
        context.Artifacts.Add(new ArtifactEntity
        {
            Id = artifactId,
            PipelineRecordId = recordId,
            SourceRevision = 1,
            Stage = 3,
            ContentHash = new string('a', 64),
            ContentType = "text/plain",
            SearchText = "snapshot",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        var chunk = new TextChunkEntity
        {
            ArtifactId = artifactId,
            SourceRevision = 1,
            Ordinal = 0,
            StartOffset = 0,
            Length = 8,
            Content = "snapshot",
            ContentHash = new string('a', 64)
        };
        context.TextChunks.Add(chunk);
        await context.SaveChangesAsync();
        var firstVector = new VectorEntity
        {
            TextChunkId = chunk.Id,
            SourceRevision = 1,
            ModelFingerprint = "deterministic-tokenhash-v1:256",
            Dimensions = 256,
            Values = values,
            TextChunkContentHash = chunk.ContentHash,
            PayloadChecksum = Convert.ToHexStringLower(SHA256.HashData(values)),
            IndexGenerationId = referencedGenerationId,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var secondVector = new VectorEntity
        {
            TextChunkId = chunk.Id,
            SourceRevision = 1,
            ModelFingerprint = "deterministic-tokenhash-v1:256",
            Dimensions = 256,
            Values = values,
            TextChunkContentHash = chunk.ContentHash,
            PayloadChecksum = Convert.ToHexStringLower(SHA256.HashData(values)),
            IndexGenerationId = activeGenerationId,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        context.Vectors.AddRange(firstVector, secondVector);
        await context.SaveChangesAsync();
        context.IndexGenerationVectors.AddRange(
            new IndexGenerationVectorEntity { GenerationId = activeGenerationId, VectorId = secondVector.VectorId },
            new IndexGenerationVectorEntity { GenerationId = activeGenerationId, VectorId = firstVector.VectorId });
        var state = await context.IndexState.SingleAsync(item => item.Id == 1);
        state.ActiveIndexGenerationId = activeGenerationId;
        await context.SaveChangesAsync();
        return [firstVector.VectorId, secondVector.VectorId];
    }

    private static IndexGenerationEntity CreateGeneration(Guid id, string indexPath = "pending") => new()
    {
        Id = id,
        ModelFingerprint = "deterministic-tokenhash-v1:256",
        Dimensions = 256,
        IndexPath = indexPath,
        MetadataChecksum = new string('0', 64),
        CreatedAtUtc = DateTimeOffset.UtcNow
    };
}
