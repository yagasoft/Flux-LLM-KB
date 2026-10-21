using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class SegmentedIndexMigrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Rollback_builds_from_current_SQL_after_replacement_and_withdrawal_and_replay_cannot_revert_later_work()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The original text will be replaced after format conversion.", segmented: true, initialIndexFormat: 1);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var first = await db.PipelineRecords.Select(value => value.SourceIdentityId).SingleAsync();
        await environment.AddAndPumpAsync("This other source will be withdrawn after conversion.");
        var withdrawn = await db.PipelineRecords.Where(value => value.SourceIdentityId != first).Select(value => value.SourceIdentityId).SingleAsync();
        var store = Store(environment);
        var rebuilder = Rebuilder(environment, store);
        var conversionPlan = await store.ReadRebuildPlanAsync(Guid.NewGuid(), 2, CancellationToken.None);
        var converted = await rebuilder.RunAsync(conversionPlan, CancellationToken.None);
        Assert.Equal(2, converted.FormatVersion);
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
        await environment.AddAndPumpAtPathAsync("The replacement text is the current, required content.", "initial.txt");
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await SqlPublishedPassageSelection.AcquireFenceAsync(db, CancellationToken.None);
            await db.PipelineRecords.Where(value => value.SourceIdentityId == withdrawn).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.IsDeleted, true));
            await SqlPublishedPassageSelection.AdvanceVersionAsync(db, DateTimeOffset.UtcNow, CancellationToken.None, [new(1, withdrawn)]);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var expected = await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None);
        Assert.NotEmpty(expected);
        var rollbackPlan = await store.ReadRebuildPlanAsync(Guid.NewGuid(), 1, CancellationToken.None);
        var rolledBack = await rebuilder.RunAsync(rollbackPlan, CancellationToken.None);
        Assert.Equal(1, rolledBack.FormatVersion);
        Assert.NotEqual(conversionPlan.BaseGenerationId, rolledBack.Id);
        Assert.Equal(expected.Select(value => value.VectorId), await db.IndexGenerationVectors.Where(value => value.GenerationId == rolledBack.Id)
            .OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync());
        Assert.Empty(await db.CurrentIndexMembers.ToArrayAsync());
        var vector = expected[0]; var query = new float[vector.Dimensions]; Buffer.BlockCopy(vector.Values, 0, query, 0, vector.Values.Length);
        Assert.Equal(expected.Select(value => value.VectorId).Order(), (await environment.Reader.SearchAsync(query, 100, CancellationToken.None)).Select(value => value.VectorId).Order());
        await environment.AddAndPumpAsync("A later publication must survive an old operation acknowledgement replay.");
        var later = await environment.ActiveGenerationAsync();
        Assert.Equal(rolledBack, await rebuilder.RunAsync(rollbackPlan, CancellationToken.None));
        Assert.Equal(later.Id, (await environment.ActiveGenerationAsync()).Id);
        Assert.Equal(2, await db.IndexPublicationCandidates.CountAsync(value => value.State == 2 && value.BuildPurpose != 0));
        Assert.Empty(await db.IndexCandidateMembers.Where(value => value.CandidateId == rollbackPlan.OperationId).ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_preparation_captures_the_current_legacy_corpus_without_switching_its_format(bool sqlRetries)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            string.Join(' ', Enumerable.Range(0, 26000).Select(value => $"syntheticword{value}")), segmented: true, initialIndexFormat: 1);
        await environment.AddAndPumpAsync("Another published source must appear in the complete conversion worklist.");
        IDbContextFactory<FluxKnowledgeDbContext> factory = environment.Factory;
        if (sqlRetries)
        {
            await using var binding = await factory.CreateDbContextAsync();
            factory = new Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory<FluxKnowledgeDbContext>(
                new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(binding.Database.GetConnectionString(), options => options.EnableRetryOnFailure()).Options);
        }
        var store = new SqlSegmentedIndexStore(factory, TimeProvider.System,
            environment.Services.GetRequiredService<FluxKnowledge.Application.Gpu.IGpuInteractiveOwnerProbe>());
        var plan = await store.ReadRebuildPlanAsync(Guid.NewGuid(), 2, CancellationToken.None);
        var prepared = await store.PrepareRebuildAsync(plan, CancellationToken.None);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var state = await db.IndexState.AsNoTracking().SingleAsync();
        Assert.Equal(1, state.RequiredIndexFormat);
        Assert.Equal(plan.BaseGenerationId, state.ActiveIndexGenerationId);
        Assert.Equal(plan.Stamp.CorpusVersion, state.CorpusVersion);
        Assert.Empty(prepared.Candidate.ReusedSegments);
        Assert.Empty(await db.CurrentIndexMembers.ToArrayAsync());
        var ids = await db.IndexCandidateMembers.Where(value => value.CandidateId == plan.OperationId && value.Phase == 1)
            .OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync();
        Assert.True(ids.Length > 256); // Exercise more than one metadata capture page.
        Assert.Equal(await db.IndexGenerationVectors.Where(value => value.GenerationId == plan.BaseGenerationId)
            .OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync(), ids);
        var replay = await store.PrepareRebuildAsync(plan, CancellationToken.None);
        Assert.Equal(prepared.Candidate.Id, replay.Candidate.Id);
        Assert.Equal(ids.Length, await db.IndexCandidateMembers.CountAsync(value => value.CandidateId == plan.OperationId));
    }

    [NativeSqlServerTheory]
    [InlineData("missing-manifest")]
    [InlineData("missing-segment")]
    [InlineData("corrupt-segment")]
    public async Task Explicit_repair_rebuilds_current_membership_without_overwriting_damaged_objects(string fault)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The canonical SQL vector remains available after loss of its derived file.", segmented: true);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var old = await environment.ActiveGenerationAsync();
        var oldSegment = await db.IndexSegments.AsNoTracking().SingleAsync();
        var damaged = fault == "missing-manifest" ? Path.Combine(old.IndexPath, "manifest.json") : Path.Combine(oldSegment.DirectoryPath, "index.usearch");
        if (fault == "corrupt-segment") await File.WriteAllTextAsync(damaged, "damaged derived bytes");
        else File.Delete(damaged);
        var store = Store(environment); var plan = await store.ReadRebuildPlanAsync(Guid.NewGuid(), 2, CancellationToken.None);
        var repaired = await Rebuilder(environment, store).RunAsync(plan, CancellationToken.None);
        Assert.NotEqual(old.Id, repaired.Id);
        Assert.NotEqual(oldSegment.Id, await db.CurrentIndexMembers.Select(value => value.SegmentId).Distinct().SingleAsync());
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
        var canonical = Assert.Single(await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None));
        var query = new float[canonical.Dimensions]; Buffer.BlockCopy(canonical.Values, 0, query, 0, canonical.Values.Length);
        Assert.Equal(canonical.VectorId, Assert.Single(await environment.Reader.SearchAsync(query, 100, CancellationToken.None)).VectorId);
        if (fault == "corrupt-segment") Assert.Equal("damaged derived bytes", await File.ReadAllTextAsync(damaged));
        else Assert.False(File.Exists(damaged));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Placement_interruption_reuses_validated_files_before_or_after_the_segment_SQL_seal(bool afterSeal)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            string.Join(' ', Enumerable.Range(0, 3500).Select(value => $"syntheticword{value}")), segmented: true, initialIndexFormat: 1);
        var store = Store(environment); var plan = await store.ReadRebuildPlanAsync(Guid.NewGuid(), 2, CancellationToken.None);
        await store.PrepareRebuildAsync(plan, CancellationToken.None);
        string? placed = null;
        var builder = new UsearchSegmentBuilder(new(environment.IndexRoot), new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot), new(2));
        await Assert.ThrowsAsync<IOException>(async () => await builder.BuildAsync(plan.ModelFingerprint, plan.Dimensions,
            store.ReadBuildVectorsAsync(plan.OperationId, CancellationToken.None), CancellationToken.None, async (segment, vectors, token) =>
            {
                placed = Path.Combine(segment.DirectoryPath, "index.usearch");
                if (afterSeal) await store.SealSegmentAsync(plan.OperationId, segment, vectors, token);
                throw new IOException("synthetic interruption after physical placement");
            }, plan.OperationId));
        Assert.NotNull(placed);
        var bytes = await File.ReadAllBytesAsync(placed); var written = File.GetLastWriteTimeUtc(placed);
        await using var db = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(afterSeal ? 1 : 0, await db.IndexSegments.CountAsync());
        await Rebuilder(environment, store).RunAsync(plan, CancellationToken.None);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(placed)); Assert.Equal(written, File.GetLastWriteTimeUtc(placed));
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerFact]
    public async Task An_exited_rebuild_is_preserved_for_resume_and_unknown_ownership_cannot_be_claimed_or_abandoned()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "A durable conversion worklist survives the operator process.", segmented: true, initialIndexFormat: 1);
        var probe = new ControlledProbe(); var store = new SqlSegmentedIndexStore(environment.Factory, TimeProvider.System, probe);
        var plan = await store.ReadRebuildPlanAsync(Guid.NewGuid(), 2, CancellationToken.None);
        await store.PrepareRebuildAsync(plan, CancellationToken.None);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var worklistCount = await db.IndexCandidateMembers.CountAsync(value => value.CandidateId == plan.OperationId);
        Assert.Equal("index-rebuild-requires-explicit-abandonment",
            (await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.AbandonAsync(plan.OperationId, CancellationToken.None))).Message);
        Assert.Equal(0, await db.IndexPublicationCandidates.Where(value => value.Id == plan.OperationId).Select(value => value.State).SingleAsync());
        Assert.Equal(worklistCount, await db.IndexCandidateMembers.CountAsync(value => value.CandidateId == plan.OperationId));
        await db.IndexPublicationCandidates.Where(value => value.Id == plan.OperationId).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.OwnerProcessId, int.MaxValue));
        var recovery = Path.Combine(environment.IndexRoot, "rebuild-recovery"); Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        var maintenance = new SqlSegmentRetirementOperations(environment.Factory, new WindowsIndexProjectionFileStore(environment.IndexRoot),
            environment.IndexRoot, recovery, TimeProvider.System, probe);
        probe.Observation = GpuInteractiveOwnerObservation.Exited;
        await maintenance.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, await db.IndexPublicationCandidates.Where(value => value.Id == plan.OperationId).Select(value => value.State).SingleAsync());
        Assert.NotEmpty(await db.IndexCandidateMembers.Where(value => value.CandidateId == plan.OperationId).ToArrayAsync());
        probe.Observation = GpuInteractiveOwnerObservation.Unknown;
        Assert.Equal("index-rebuild-owner-alive-or-unknown", (await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.PrepareRebuildAsync(plan, CancellationToken.None))).Message);
        Assert.Equal("index-rebuild-abandonment-requires-proved-owner-exit", (await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.AbandonRebuildAsync(plan.OperationId, CancellationToken.None))).Message);
        probe.Observation = GpuInteractiveOwnerObservation.Exited;
        await store.PrepareRebuildAsync(plan, CancellationToken.None);
        Assert.Equal(probe.Current.ProcessId, await db.IndexPublicationCandidates.Where(value => value.Id == plan.OperationId).Select(value => value.OwnerProcessId).SingleAsync());
        await store.AbandonRebuildAsync(plan.OperationId, CancellationToken.None);
        await store.AbandonRebuildAsync(plan.OperationId, CancellationToken.None);
        Assert.Empty(await db.IndexCandidateMembers.Where(value => value.CandidateId == plan.OperationId).ToArrayAsync());
        Assert.Equal("index-rebuild-operation-abandoned-use-new-id", (await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.PrepareRebuildAsync(plan, CancellationToken.None))).Message);
    }

    [NativeSqlServerFact]
    public async Task A_stale_or_rebound_operation_cannot_activate_or_change_the_format()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The captured source can change while conversion is outside the fence.", segmented: true, initialIndexFormat: 1);
        var store = Store(environment); var plan = await store.ReadRebuildPlanAsync(Guid.NewGuid(), 2, CancellationToken.None);
        await store.PrepareRebuildAsync(plan, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.PrepareRebuildAsync(plan with { TargetFormat = 1 }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.PrepareRebuildAsync(plan with { Dimensions = plan.Dimensions + 1 }, CancellationToken.None));
        var paths = new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot);
        var built = await new UsearchSegmentBuilder(new(environment.IndexRoot), paths, new(2)).BuildAsync(plan.ModelFingerprint, plan.Dimensions,
            store.ReadBuildVectorsAsync(plan.OperationId, CancellationToken.None), CancellationToken.None,
            (segment, vectors, token) => store.SealSegmentAsync(plan.OperationId, segment, vectors, token), plan.OperationId);
        var manifest = new UsearchManifestValidator(new(environment.IndexRoot), paths).Place(plan.Stamp, plan.ModelFingerprint, plan.Dimensions, built);
        await store.SealManifestAsync(plan.OperationId, manifest, CancellationToken.None);
        await environment.AddAndPumpAtPathAsync("This newer published replacement must remain authoritative.", "initial.txt");
        var current = await environment.ActiveGenerationAsync();
        await Assert.ThrowsAsync<PublicationSnapshotConflictException>(async () => await store.ActivateRebuildAsync(plan.OperationId, CancellationToken.None));
        Assert.Equal(current.Id, (await environment.ActiveGenerationAsync()).Id);
        await using var db = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.IndexState.Select(value => value.RequiredIndexFormat).SingleAsync());
        Assert.Empty(await db.CurrentIndexMembers.ToArrayAsync());
    }

    private static SqlSegmentedIndexStore Store(SqlToUsearchRebuildTests.PipelineEnvironment environment) => new(environment.Factory, TimeProvider.System,
        environment.Services.GetRequiredService<FluxKnowledge.Application.Gpu.IGpuInteractiveOwnerProbe>());

    private static UsearchProjectionRebuilder Rebuilder(SqlToUsearchRebuildTests.PipelineEnvironment environment, SqlSegmentedIndexStore store) =>
        new(new(environment.IndexRoot), new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot), store, store,
            new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System),
            new(new(environment.IndexRoot), new WindowsIndexGenerationReadLeaseFactory(environment.IndexRoot)), new(2));

    private sealed class ControlledProbe : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current { get; } = new WindowsInteractiveGpuOwnerProbe().Current;
        public GpuInteractiveOwnerObservation Observation { get; set; }
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner) => Observation;
    }
}
