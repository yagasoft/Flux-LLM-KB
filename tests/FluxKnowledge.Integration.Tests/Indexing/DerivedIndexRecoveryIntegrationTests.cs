using System.Security.Cryptography;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Immutable;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class DerivedIndexRecoveryIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Missing_active_directory_recovers_to_a_unique_path_without_changing_the_active_pointer()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var generationId = Guid.NewGuid();
        await SeedSnapshotAsync(factory, generationId, Guid.NewGuid());
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var indexStore = new SqlPipelineStore(factory);
            var vectors = await indexStore.ReadVectorsAsync(generationId, CancellationToken.None);
            await using (var context = await factory.CreateDbContextAsync())
            {
                var storedVectors = await context.Vectors.OrderBy(item => item.VectorId).ToListAsync();
                for (var index = 0; index < storedVectors.Count; index++)
                {
                    var values = new byte[1024];
                    BitConverter.GetBytes(1F).CopyTo(values, index * sizeof(float));
                    storedVectors[index].Values = values;
                    storedVectors[index].PayloadChecksum = Convert.ToHexStringLower(SHA256.HashData(values));
                }
                await context.SaveChangesAsync();
                vectors = await indexStore.ReadVectorsAsync(generationId, CancellationToken.None);
                var generation = await context.IndexGenerations.SingleAsync(item => item.Id == generationId);
                generation.IndexPath = Path.Combine(root, "generations", generationId.ToString("N"));
                generation.MetadataChecksum = UsearchGenerationValidator.ComputeChecksum(
                    generation.ModelFingerprint, generation.Dimensions, vectors);
                generation.VectorCount = vectors.Count;
                await context.SaveChangesAsync();
            }

            var options = UsearchIndexOptions.FromConfiguredRoot(root);
            var services = new ServiceCollection();
            services.AddSingleton(factory);
            services.AddSingleton<IDerivedIndexRecoveryStore, SqlDerivedIndexRecoveryStore>();
            services.AddScoped<SqlPipelineStore>();
            services.AddScoped<IIndexGenerationStore>(provider => provider.GetRequiredService<SqlPipelineStore>());
            services.AddSingleton(options);
            services.AddSingleton<UsearchGenerationValidator>();
            services.AddScoped<UsearchGenerationBuilder>();
            services.AddSingleton<DerivedIndexFileSystem>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<DerivedIndexRecoveryCoordinator>();
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

            await coordinator.RunOnceAsync(CancellationToken.None);

            var recovered = await indexStore.GetGenerationAsync(generationId, CancellationToken.None);
            Assert.NotNull(recovered);
            Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
            Assert.Equal(generationId, await indexStore.GetActiveGenerationIdAsync(CancellationToken.None));
            Assert.NotEqual(Path.Combine(root, "generations", generationId.ToString("N")), recovered!.IndexPath);
            Assert.True(File.Exists(Path.Combine(recovered.IndexPath, UsearchGenerationValidator.IndexFileName)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Corrupt_active_directory_is_replaced_before_the_old_path_is_quarantined()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var generationId = Guid.NewGuid();
        var duplicateId = Guid.NewGuid();
        await SeedSnapshotAsync(factory, generationId, duplicateId);
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        var oldPath = Path.Combine(root, "generations", generationId.ToString("N"));
        try
        {
            var store = new SqlPipelineStore(factory);
            var vectors = await MakeSnapshotRebuildableAsync(factory, store, generationId, oldPath);
            Directory.CreateDirectory(oldPath);
            await File.WriteAllTextAsync(Path.Combine(oldPath, UsearchGenerationValidator.IndexFileName), "corrupt");
            await File.WriteAllTextAsync(Path.Combine(oldPath, UsearchGenerationValidator.MetadataFileName), "{}");
            await using (var context = await factory.CreateDbContextAsync())
            {
                var duplicate = await context.IndexGenerations.SingleAsync(item => item.Id == duplicateId);
                duplicate.IndexPath = oldPath;
                await context.SaveChangesAsync();
            }
            using var provider = CreateRecoveryProvider(factory, root);
            var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

            await coordinator.RunOnceAsync(CancellationToken.None);

            var unchanged = await store.GetGenerationAsync(generationId, CancellationToken.None);
            Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
            Assert.NotNull(unchanged);
            Assert.NotEqual(oldPath, unchanged!.IndexPath);
            Assert.Equal(generationId, await store.GetActiveGenerationIdAsync(CancellationToken.None));
            Assert.True(Directory.Exists(oldPath));
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal(oldPath, (await verification.IndexGenerations.SingleAsync(item => item.Id == duplicateId)).IndexPath);
            Assert.Equal(vectors.Count, unchanged.VectorCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Permission_fault_becomes_operator_actionable_without_a_retry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            using var fixture = CreateFaultingCoordinator(root, new UnauthorizedAccessException("injected"));
            var coordinator = fixture.Coordinator;

            await coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.PermissionsDenied, coordinator.Snapshot.FailureCategory);
            Assert.Null(coordinator.Snapshot.NextRetryAtUtc);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Configuration_fault_becomes_operator_actionable_without_a_retry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            using var fixture = CreateFaultingCoordinator(root, new InvalidOperationException("configuration invalid"));
            var coordinator = fixture.Coordinator;

            await coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.ConfigurationInvalid, coordinator.Snapshot.FailureCategory);
            Assert.Null(coordinator.Snapshot.NextRetryAtUtc);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cleanup_retains_an_aged_candidate_when_sql_references_a_descendant()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var candidate = Path.Combine(root, "staging", "candidate");
            Directory.CreateDirectory(Path.Combine(candidate, "nested"));
            Directory.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddDays(-2));
            var fileSystem = new DerivedIndexFileSystem(new UsearchIndexOptions(root));

            var cleaned = fileSystem.Cleanup("staging", TimeSpan.FromHours(24), DateTimeOffset.UtcNow,
                [Path.Combine(candidate, "nested")]);

            Assert.Equal(0, cleaned);
            Assert.True(Directory.Exists(candidate));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cleanup_rejects_a_non_recovery_area()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var candidate = Path.Combine(root, "arbitrary", "candidate");
            Directory.CreateDirectory(candidate);
            Directory.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddDays(-2));

            var cleaned = new DerivedIndexFileSystem(new UsearchIndexOptions(root)).Cleanup(
                "arbitrary", TimeSpan.Zero, DateTimeOffset.UtcNow, []);

            Assert.Equal(0, cleaned);
            Assert.True(Directory.Exists(candidate));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cleanup_retains_a_candidate_when_any_sql_reference_is_outside_the_root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var candidate = Path.Combine(root, "staging", "candidate");
            Directory.CreateDirectory(candidate);
            Directory.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddDays(-2));

            var cleaned = new DerivedIndexFileSystem(new UsearchIndexOptions(root)).Cleanup(
                "staging", TimeSpan.Zero, DateTimeOffset.UtcNow, [Path.GetTempPath()]);

            Assert.Equal(0, cleaned);
            Assert.True(Directory.Exists(candidate));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cleanup_removes_only_aged_unreferenced_non_reparse_candidates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var stagingRemoved = CreateAgedDirectory(root, "staging", "remove");
            var quarantineRemoved = CreateAgedDirectory(root, "quarantine", "remove");
            var referenced = CreateAgedDirectory(root, "staging", "referenced");
            var fileSystem = new DerivedIndexFileSystem(new UsearchIndexOptions(root));

            Assert.Equal(1, fileSystem.Cleanup("staging", TimeSpan.FromHours(24), DateTimeOffset.UtcNow,
                [referenced, referenced]));
            Assert.Equal(1, fileSystem.Cleanup("quarantine", TimeSpan.FromDays(7), DateTimeOffset.UtcNow, []));
            Assert.False(Directory.Exists(stagingRemoved));
            Assert.False(Directory.Exists(quarantineRemoved));
            Assert.True(Directory.Exists(referenced));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cleanup_retains_a_reparse_bearing_candidate_when_windows_allows_creation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        var external = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecoveryExternal_{Guid.NewGuid():N}");
        try
        {
            var candidate = CreateAgedDirectory(root, "staging", "linked");
            Directory.CreateDirectory(external);
            var link = Path.Combine(candidate, "junction");
            try
            {
                Directory.CreateSymbolicLink(link, external);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }
            Directory.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddDays(-2));

            var cleaned = new DerivedIndexFileSystem(new UsearchIndexOptions(root)).Cleanup(
                "staging", TimeSpan.Zero, DateTimeOffset.UtcNow, []);

            Assert.Equal(0, cleaned);
            Assert.True(Directory.Exists(candidate));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Invalid_sql_checksum_requires_operator_action_without_filesystem_or_metadata_mutation()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var generationId = Guid.NewGuid();
        await SeedSnapshotAsync(factory, generationId, Guid.NewGuid());
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        var oldPath = Path.Combine(root, "generations", generationId.ToString("N"));
        try
        {
            await using (var context = await factory.CreateDbContextAsync())
            {
                var generation = await context.IndexGenerations.SingleAsync(item => item.Id == generationId);
                generation.IndexPath = oldPath;
                generation.MetadataChecksum = new string('f', 64);
                generation.VectorCount = 2;
                await context.SaveChangesAsync();
            }
            using var provider = CreateRecoveryProvider(factory, root);
            var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

            await coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.SqlMembershipInvalid, coordinator.Snapshot.FailureCategory);
            Assert.Null(coordinator.Snapshot.NextRetryAtUtc);
            Assert.False(Directory.Exists(root));
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal(oldPath, (await verification.IndexGenerations.SingleAsync(item => item.Id == generationId)).IndexPath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Two_coordinators_perform_at_most_one_rebuild_and_both_later_converge_healthy()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var generationId = Guid.NewGuid();
        await SeedSnapshotAsync(factory, generationId, Guid.NewGuid());
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var store = new SqlPipelineStore(factory);
            await MakeSnapshotRebuildableAsync(factory, store, generationId,
                Path.Combine(root, "generations", generationId.ToString("N")));
            using var firstProvider = CreateRecoveryProvider(factory, root);
            using var secondProvider = CreateRecoveryProvider(factory, root);
            var first = firstProvider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
            var second = secondProvider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

            await Task.WhenAll(first.RunOnceAsync(CancellationToken.None).AsTask(),
                second.RunOnceAsync(CancellationToken.None).AsTask());
            await first.RunOnceAsync(CancellationToken.None);
            await second.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.Healthy, first.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryState.Healthy, second.Snapshot.State);
            Assert.Single(Directory.EnumerateDirectories(Path.Combine(root, "generations")));
            Assert.Equal(generationId, await store.GetActiveGenerationIdAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Hosted_service_uses_exact_retry_schedule_then_converges_after_later_success()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var generationId = Guid.NewGuid();
        await SeedSnapshotAsync(factory, generationId, Guid.NewGuid());
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var pipelineStore = new SqlPipelineStore(factory);
            await MakeSnapshotRebuildableAsync(factory, pipelineStore, generationId,
                Path.Combine(root, "generations", generationId.ToString("N")));
            var sequenced = new SequencedRecoveryStore(
                new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System), failures: 4);
            using var provider = CreateRecoveryProvider(factory, root, sequenced);
            var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
            var waits = new List<TimeSpan>();
            using var cancellation = new CancellationTokenSource();
            var service = new DerivedIndexRecoveryService(
                coordinator, DerivedIndexRecoveryOptions.Default, TimeProvider.System,
                (delay, _) =>
                {
                    waits.Add(delay);
                    if (coordinator.Snapshot.State == DerivedIndexRecoveryState.Healthy)
                        cancellation.Cancel();
                    return Task.CompletedTask;
                });

            await service.RunForTestingAsync(cancellation.Token);

            Assert.Equal([2, 5, 15, 30], waits.Take(4).Select(item => (int)Math.Round(item.TotalSeconds)));
            Assert.Equal(5, sequenced.Acquisitions);
            Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Hosted_service_stops_after_five_recoverable_failures_without_a_sixth_attempt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            using var cancellation = new CancellationTokenSource();
            var store = new ExhaustingRecoveryStore(cancellation);
            using var provider = CreateFaultingRecoveryProvider(root, store);
            var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
            var waits = new List<TimeSpan>();
            var service = new DerivedIndexRecoveryService(coordinator,
                DerivedIndexRecoveryOptions.Default, TimeProvider.System,
                (delay, _) =>
                {
                    waits.Add(delay);
                    return Task.CompletedTask;
                });

            await service.RunForTestingAsync(cancellation.Token);

            Assert.Equal([2, 5, 15, 30], waits.Select(item => (int)Math.Round(item.TotalSeconds)));
            Assert.Equal(5, store.Acquisitions);
            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.RetryExhausted, coordinator.Snapshot.FailureCategory);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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
        values[0] = 1;
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

    private static ServiceProvider CreateRecoveryProvider(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        string root,
        IDerivedIndexRecoveryStore? recoveryStore = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(factory);
        if (recoveryStore is null) services.AddSingleton<IDerivedIndexRecoveryStore, SqlDerivedIndexRecoveryStore>();
        else services.AddSingleton(recoveryStore);
        services.AddScoped<SqlPipelineStore>();
        services.AddScoped<IIndexGenerationStore>(provider => provider.GetRequiredService<SqlPipelineStore>());
        services.AddSingleton(UsearchIndexOptions.FromConfiguredRoot(root));
        services.AddSingleton<UsearchGenerationValidator>();
        services.AddScoped<UsearchGenerationBuilder>();
        services.AddSingleton<DerivedIndexFileSystem>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DerivedIndexRecoveryCoordinator>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static ServiceProvider CreateFaultingRecoveryProvider(string root, IDerivedIndexRecoveryStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddScoped<IIndexGenerationStore, EmptyIndexGenerationStore>();
        services.AddSingleton(new UsearchIndexOptions(root));
        services.AddSingleton<UsearchGenerationValidator>();
        services.AddScoped<UsearchGenerationBuilder>();
        services.AddSingleton<DerivedIndexFileSystem>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DerivedIndexRecoveryCoordinator>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static string CreateAgedDirectory(string root, string area, string name)
    {
        var path = Path.Combine(root, area, name);
        Directory.CreateDirectory(path);
        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        return path;
    }

    private static async Task<IReadOnlyList<CanonicalVector>> MakeSnapshotRebuildableAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        SqlPipelineStore store,
        Guid generationId,
        string indexPath)
    {
        await using var context = await factory.CreateDbContextAsync();
        var storedVectors = await context.Vectors.OrderBy(item => item.VectorId).ToListAsync();
        for (var index = 0; index < storedVectors.Count; index++)
        {
            var values = new byte[1024];
            BitConverter.GetBytes(1F).CopyTo(values, index * sizeof(float));
            storedVectors[index].Values = values;
            storedVectors[index].PayloadChecksum = Convert.ToHexStringLower(SHA256.HashData(values));
        }
        await context.SaveChangesAsync();
        var vectors = await store.ReadVectorsAsync(generationId, CancellationToken.None);
        var generation = await context.IndexGenerations.SingleAsync(item => item.Id == generationId);
        generation.IndexPath = indexPath;
        generation.MetadataChecksum = UsearchGenerationValidator.ComputeChecksum(
            generation.ModelFingerprint, generation.Dimensions, vectors);
        generation.VectorCount = vectors.Count;
        await context.SaveChangesAsync();
        return vectors;
    }

    private static FaultingCoordinatorFixture CreateFaultingCoordinator(string root, Exception fault)
    {
        var services = new ServiceCollection();
        var options = new UsearchIndexOptions(root);
        services.AddSingleton<IDerivedIndexRecoveryStore>(new FaultingRecoveryStore(fault));
        services.AddScoped<IIndexGenerationStore, EmptyIndexGenerationStore>();
        services.AddSingleton(options);
        services.AddSingleton<UsearchGenerationValidator>();
        services.AddScoped<UsearchGenerationBuilder>();
        services.AddSingleton<DerivedIndexFileSystem>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DerivedIndexRecoveryCoordinator>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return new FaultingCoordinatorFixture(provider, provider.GetRequiredService<DerivedIndexRecoveryCoordinator>());
    }

    private sealed class FaultingRecoveryStore(Exception fault) : IDerivedIndexRecoveryStore
    {
        public ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(CancellationToken cancellationToken) => throw fault;
        public ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(TimeSpan lockTimeout, CancellationToken cancellationToken) => throw fault;
        public ValueTask AppendAuditAsync(DerivedIndexRecoveryAuditEvent auditEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class SequencedRecoveryStore(IDerivedIndexRecoveryStore inner, int failures)
        : IDerivedIndexRecoveryStore
    {
        public int Acquisitions { get; private set; }
        public ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(CancellationToken cancellationToken) =>
            inner.ReadActiveAsync(cancellationToken);
        public ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(
            TimeSpan lockTimeout, CancellationToken cancellationToken)
        {
            Acquisitions++;
            if (Acquisitions <= failures) throw new IOException("injected transient failure");
            return inner.TryAcquireExclusiveLeaseAsync(lockTimeout, cancellationToken);
        }
        public ValueTask AppendAuditAsync(DerivedIndexRecoveryAuditEvent auditEvent, CancellationToken cancellationToken) =>
            inner.AppendAuditAsync(auditEvent, cancellationToken);
    }

    private sealed class ExhaustingRecoveryStore(CancellationTokenSource cancellation)
        : IDerivedIndexRecoveryStore
    {
        public int Acquisitions { get; private set; }
        public ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(
            TimeSpan lockTimeout, CancellationToken cancellationToken)
        {
            Acquisitions++;
            if (Acquisitions == 5) cancellation.Cancel();
            throw new IOException("injected transient failure");
        }
        public ValueTask AppendAuditAsync(DerivedIndexRecoveryAuditEvent auditEvent, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class EmptyIndexGenerationStore : IIndexGenerationStore
    {
        public ValueTask<IReadOnlyList<CanonicalTextChunk>> ReadChunksAsync(FluxKnowledge.Domain.Common.PipelineRecordId pipelineRecordId, long sourceRevision, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CanonicalTextChunk>>([]);
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadVectorsAsync(Guid indexGenerationId, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CanonicalVector>>([]);
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadEligibleVectorsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CanonicalVector>>([]);
        public ValueTask<IndexGenerationDescriptor?> GetGenerationAsync(Guid indexGenerationId, CancellationToken cancellationToken) => ValueTask.FromResult<IndexGenerationDescriptor?>(null);
        public ValueTask<Guid?> GetActiveGenerationIdAsync(CancellationToken cancellationToken) => ValueTask.FromResult<Guid?>(null);
        public ValueTask UpdateGenerationMetadataAsync(IndexGenerationDescriptor generation, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed record FaultingCoordinatorFixture(ServiceProvider Provider, DerivedIndexRecoveryCoordinator Coordinator) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }
}
