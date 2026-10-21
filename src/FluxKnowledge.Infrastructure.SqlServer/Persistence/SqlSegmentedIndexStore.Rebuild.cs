using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSegmentedIndexStore : IIndexProjectionRebuildStore
{
    public ValueTask<IndexProjectionRebuildPlan> ReadRebuildPlanAsync(Guid operationId, int targetFormat, CancellationToken token)
    {
        if (operationId == Guid.Empty || targetFormat is not (1 or 2)) throw new ArgumentException("index-rebuild-plan-invalid");
        return RebuildTransactionAsync<IndexProjectionRebuildPlan>(async context =>
        {
            var existing = await context.IndexPublicationCandidates.AsNoTracking().SingleOrDefaultAsync(value => value.Id == operationId, token);
            if (existing is not null)
            {
                if (existing.BuildPurpose != Purpose(targetFormat) || existing.BaseGenerationId is null)
                    throw new InvalidOperationException("index-rebuild-operation-conflict");
                var original = await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == existing.BaseGenerationId, token);
                return new(operationId, existing.ExpectedIndexFormat, targetFormat, new(existing.CorpusEpoch, existing.ExpectedCorpusVersion),
                    original.Id, original.ModelFingerprint, original.Dimensions);
            }
            var state = await context.IndexState.AsNoTracking().SingleAsync(token);
            var active = await context.IndexGenerations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == state.ActiveIndexGenerationId, token);
            if (state.RequiredIndexFormat is not (1 or 2) || state.CorpusRebuildOperationId is not null ||
                active is null || active.FormatVersion != state.RequiredIndexFormat || active.ValidatedAtUtc is null || active.RetiredAtUtc is not null)
                throw new InvalidOperationException("index-rebuild-active-profile-required");
            return new(operationId, state.RequiredIndexFormat, targetFormat, new(state.CorpusEpoch, state.CorpusVersion),
                active.Id, active.ModelFingerprint, active.Dimensions);
        }, token);
    }

    public async ValueTask<IndexProjectionRebuildProgress> PrepareRebuildAsync(IndexProjectionRebuildPlan plan, CancellationToken token)
    {
        ValidatePlan(plan);
        var ready = await RebuildTransactionAsync<IndexProjectionRebuildProgress?>(async context =>
        {
            var candidate = await context.IndexPublicationCandidates.SingleOrDefaultAsync(value => value.Id == plan.OperationId, token);
            if (candidate is not null)
            {
                await ValidateRebuildBindingAsync(context, candidate, plan, token);
                if (candidate.State == 2) return await RebuildProgressAsync(context, candidate, plan, token);
                if (candidate.State == 3) throw new InvalidOperationException("index-rebuild-operation-abandoned-use-new-id");
                ClaimRebuildOwner(candidate);
                await ValidateBaseAsync(context, candidate, token);
                if (candidate.WorklistComplete)
                {
                    await context.SaveChangesAsync(token);
                    return await RebuildProgressAsync(context, candidate, plan, token);
                }
                // Native building cannot precede a sealed worklist. Restart only incomplete
                // metadata capture, in bounded deletes, preserving all sealed native progress.
                int deleted;
                do
                {
                    deleted = await context.Database.ExecuteSqlInterpolatedAsync($"""
                        DELETE TOP ({PageSize}) FROM [IndexCandidateMembers] WHERE [CandidateId] = {candidate.Id};
                        """, token);
                } while (deleted != 0);
                candidate.LastAppendOrdinal = -1; candidate.LastPageChecksum = new('0', 64);
            }
            else
            {
                if (await context.IndexPublicationCandidates.AnyAsync(value => value.State < 2 && value.BuildPurpose != 0, token))
                    throw new InvalidOperationException("index-rebuild-other-operation-pending-resume-or-abandon-it");
                var owner = ownerProbe.Current; owner.Validate();
                candidate = new()
                {
                    Id = plan.OperationId, BuildPurpose = Purpose(plan.TargetFormat), ExpectedIndexFormat = plan.SourceFormat,
                    CorpusEpoch = plan.Stamp.CorpusEpoch, ExpectedCorpusVersion = plan.Stamp.CorpusVersion,
                    PublicationCorpusVersion = plan.Stamp.CorpusVersion, BaseGenerationId = plan.BaseGenerationId,
                    BeforeChecksum = EmptyDigest, AfterChecksum = EmptyDigest, CreatedAtUtc = clock.GetUtcNow(),
                    OwnerInstanceId = Guid.NewGuid(), OwnerProcessId = owner.ProcessId,
                    OwnerStartedAtUtc = owner.StartedAtUtc, OwnerMachineFingerprint = owner.MachineFingerprint
                };
                await ValidateRebuildBindingAsync(context, candidate, plan, token);
                await ValidateBaseAsync(context, candidate, token);
                context.IndexPublicationCandidates.Add(candidate);
            }
            await context.SaveChangesAsync(token);
            return null;
        }, token);
        if (ready is not null) return ready;

        // Stabilise canonical eligibility with the publication fence. The append connection
        // touches metadata only, without FKs to rows held by this capture transaction.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0, last = 0, ordinal = -1;
        await RebuildTransactionAsync(async context =>
        {
            var capture = new IndexPublicationCandidateEntity
            {
                Id = plan.OperationId, BuildPurpose = Purpose(plan.TargetFormat), ExpectedIndexFormat = plan.SourceFormat,
                CorpusEpoch = plan.Stamp.CorpusEpoch, ExpectedCorpusVersion = plan.Stamp.CorpusVersion, BaseGenerationId = plan.BaseGenerationId
            };
            await ValidateBaseAsync(context, capture, token);
            while (true)
            {
                var page = await QueryAllEligible(context, last).ToArrayAsync(token);
                if (page.Length == 0) break;
                AppendHash(hash, page); count += page.Length; last = page[^1].VectorId;
                await AppendPageAsync(plan.OperationId, 1, ++ordinal, page, token, explicitRebuild: true);
            }
            return true;
        }, token);
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        return await RebuildTransactionAsync(async context =>
        {
            var candidate = await LockCandidateAsync(context, plan.OperationId, token);
            await ValidateBaseAsync(context, candidate, token);
            if (candidate.State != 0 || candidate.WorklistComplete || candidate.LastAppendOrdinal != ordinal ||
                await HashWorklistAsync(context, candidate.Id, 1, token) != (count, digest))
                throw new InvalidOperationException("index-rebuild-worklist-proof-invalid");
            candidate.AfterCount = count; candidate.AfterChecksum = digest; candidate.WorklistComplete = true;
            await context.SaveChangesAsync(token);
            return await RebuildProgressAsync(context, candidate, plan, token);
        }, token);
    }

    public async ValueTask SealLegacyRebuildAsync(Guid operationId, IndexGenerationDescriptor generation, CancellationToken token)
    {
        await RebuildTransactionAsync(async context =>
        {
            var candidate = await LockCandidateAsync(context, operationId, token);
            await ValidateBaseAsync(context, candidate, token);
            var profile = await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == candidate.BaseGenerationId, token);
            if (candidate.BuildPurpose != 2 || !candidate.WorklistComplete || candidate.State is not (0 or 1) ||
                (candidate.State == 1 && candidate.ResultGenerationId != generation.Id) ||
                generation.FormatVersion != 1 || generation.SegmentCount != 0 || generation.CorpusStamp != new CorpusPublicationStamp(candidate.CorpusEpoch, candidate.ExpectedCorpusVersion) ||
                generation.ModelFingerprint != profile.ModelFingerprint || generation.Dimensions != profile.Dimensions ||
                generation.VectorCount != candidate.AfterCount || generation.VectorCount == 0 ||
                generation.MetadataChecksum != await LegacyWorklistChecksumAsync(context, candidate, profile, token))
                throw new InvalidOperationException("index-legacy-rebuild-proof-invalid");
            var existing = await context.IndexGenerations.SingleOrDefaultAsync(value => value.Id == generation.Id, token);
            if (existing is null)
            {
                context.IndexGenerations.Add(new()
                {
                    Id = generation.Id, ModelFingerprint = generation.ModelFingerprint, Dimensions = generation.Dimensions,
                    IndexPath = generation.IndexPath, MetadataChecksum = generation.MetadataChecksum, VectorCount = generation.VectorCount,
                    FormatVersion = 1, CorpusEpoch = candidate.CorpusEpoch, CorpusVersion = candidate.ExpectedCorpusVersion,
                    CreatedAtUtc = clock.GetUtcNow(), ValidatedAtUtc = clock.GetUtcNow()
                });
                await context.SaveChangesAsync(token);
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT [IndexGenerationVectors] ([GenerationId], [VectorId])
                    SELECT {generation.Id}, [VectorId] FROM [IndexCandidateMembers] WHERE [CandidateId] = {candidate.Id} AND [Phase] = 1;
                    """, token);
            }
            else if (Describe(existing) != generation || existing.RetiredAtUtc is not null)
                throw new InvalidOperationException("index-legacy-rebuild-identity-conflict");
            var built = context.IndexGenerationVectors.Where(value => value.GenerationId == generation.Id).Select(value => value.VectorId);
            if (await BuildIds(context, candidate, []).Except(built).AnyAsync(token) || await built.Except(BuildIds(context, candidate, [])).AnyAsync(token))
                throw new InvalidOperationException("index-legacy-rebuild-membership-invalid");
            candidate.ResultGenerationId = generation.Id; candidate.State = 1;
            await context.SaveChangesAsync(token);
            return true;
        }, token);
    }

    public ValueTask<IndexGenerationDescriptor> ActivateRebuildAsync(Guid operationId, CancellationToken token) =>
        RebuildTransactionAsync(async context =>
        {
            var candidate = await LockCandidateAsync(context, operationId, token);
            if (candidate.BuildPurpose is not (1 or 2) || candidate.State is not (1 or 2) || !candidate.WorklistComplete || candidate.ResultGenerationId is null)
                throw new InvalidOperationException("index-rebuild-sealed-proof-required");
            var generation = await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == candidate.ResultGenerationId, token);
            if (candidate.State == 2) return Describe(generation); // Acknowledgement replay never reactivates historical state.
            ClaimRebuildOwner(candidate);
            await ValidateBaseAsync(context, candidate, token);
            var profile = await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == candidate.BaseGenerationId, token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long count = 0, last = 0;
            while (true)
            {
                var page = await QueryAllEligible(context, last).ToArrayAsync(token);
                if (page.Length == 0) break;
                AppendHash(hash, page); count += page.Length; last = page[^1].VectorId;
            }
            if (count != candidate.AfterCount || Convert.ToHexStringLower(hash.GetHashAndReset()) != candidate.AfterChecksum ||
                await HashWorklistAsync(context, candidate.Id, 1, token) != (candidate.AfterCount, candidate.AfterChecksum) ||
                generation.ModelFingerprint != profile.ModelFingerprint || generation.Dimensions != profile.Dimensions ||
                generation.FormatVersion != (candidate.BuildPurpose == 1 ? 2 : 1) || generation.VectorCount != count || generation.RetiredAtUtc is not null ||
                generation.CorpusEpoch != candidate.CorpusEpoch || generation.CorpusVersion != candidate.ExpectedCorpusVersion)
                throw new InvalidOperationException("index-rebuild-canonical-membership-changed");
            await context.CurrentIndexMembers.ExecuteDeleteAsync(token);
            if (generation.FormatVersion == 2)
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT [CurrentIndexMembers] ([CorpusEpoch], [VectorId], [SegmentId], [SourceIdentityId], [OwnerSourceRevisionId], [SourceRootId])
                    SELECT {candidate.CorpusEpoch}, [after].[VectorId], [member].[SegmentId], [after].[SourceIdentityId], [after].[OwnerSourceRevisionId], [after].[SourceRootId]
                    FROM [IndexCandidateMembers] AS [after] INNER JOIN [IndexSegmentVectors] AS [member] ON [member].[VectorId] = [after].[VectorId]
                    INNER JOIN [IndexGenerationSegments] AS [manifest] ON [manifest].[SegmentId] = [member].[SegmentId]
                    WHERE [after].[CandidateId] = {candidate.Id} AND [after].[Phase] = 1 AND [manifest].[GenerationId] = {generation.Id};
                    """, token);
            if (generation.FormatVersion == 2 && await context.CurrentIndexMembers.LongCountAsync(token) != count)
                throw new InvalidOperationException("index-rebuild-ledger-count-invalid");
            var state = await context.IndexState.SingleAsync(token);
            state.RequiredIndexFormat = generation.FormatVersion; state.ActiveIndexGenerationId = generation.Id;
            state.ProjectionChangeFloorVersion = state.CorpusVersion; state.EmptyCatalogueValidatedAtUtc = null; state.UpdatedAtUtc = clock.GetUtcNow();
            candidate.State = 2; candidate.CompletedAtUtc = clock.GetUtcNow();
            await context.IndexCandidateMembers.Where(value => value.CandidateId == candidate.Id).ExecuteDeleteAsync(token);
            await context.SaveChangesAsync(token);
            return Describe(generation);
        }, token);

    public async ValueTask AbandonRebuildAsync(Guid operationId, CancellationToken token)
    {
        await RebuildTransactionAsync(async context =>
        {
            var candidate = await LockCandidateAsync(context, operationId, token);
            if (candidate.BuildPurpose is not (1 or 2)) throw new InvalidOperationException("index-rebuild-operation-conflict");
            if (candidate.State == 2) return true;
            if (candidate.State != 3)
            {
                if (candidate.WorklistComplete && ownerProbe.Observe(new(candidate.OwnerProcessId, candidate.OwnerStartedAtUtc, candidate.OwnerMachineFingerprint)) != GpuInteractiveOwnerObservation.Exited)
                    throw new InvalidOperationException("index-rebuild-abandonment-requires-proved-owner-exit");
                if (!candidate.WorklistComplete) ClaimRebuildOwner(candidate);
                candidate.State = 3; candidate.CompletedAtUtc = clock.GetUtcNow();
            }
            int deleted;
            do
            {
                deleted = await context.Database.ExecuteSqlInterpolatedAsync($"DELETE TOP ({PageSize}) FROM [IndexCandidateMembers] WHERE [CandidateId] = {operationId};", token);
            } while (deleted != 0);
            await context.SaveChangesAsync(token);
            return true;
        }, token);
    }

    private static IQueryable<MemberRow> QueryAllEligible(FluxKnowledgeDbContext context, long last) =>
        context.Database.SqlQuery<MemberRow>(SqlPublishedPassageSelection.Bind($"""
            SELECT TOP (256) [vector].[VectorId], [vector].[PayloadChecksum], [record].[SourceIdentityId],
                COALESCE([owner].[Id], CASE WHEN [retained].[OriginKind] <> 0 THEN [retained].[ParentSourceRevisionId] END, [retained].[Id]) AS [OwnerSourceRevisionId],
                COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) AS [SourceRootId]
            FROM [Vectors] AS [vector] INNER JOIN [TextChunks] AS [chunk] ON [chunk].[Id] = [vector].[TextChunkId]
            INNER JOIN [Artifacts] AS [artifact] ON [artifact].[Id] = [chunk].[ArtifactId]
            INNER JOIN [PipelineRecords] AS [record] ON [record].[Id] = [artifact].[PipelineRecordId]
            /*publication-bindings*/
            WHERE /*publication-eligibility*/ AND [vector].[VectorId] > {last} AND [vector].[IsDeleted] = 0
              AND [vector].[SourceRevision] = [chunk].[SourceRevision]
              AND [vector].[TextChunkContentHash] COLLATE Latin1_General_100_BIN2 = [chunk].[ContentHash] COLLATE Latin1_General_100_BIN2
              AND ([vector].[SearchInputHash] IS NULL OR [vector].[SearchInputHash] COLLATE Latin1_General_100_BIN2 = [chunk].[SearchInputHash] COLLATE Latin1_General_100_BIN2)
            ORDER BY [vector].[VectorId]
            """));

    private async ValueTask<T> RebuildTransactionAsync<T>(Func<FluxKnowledgeDbContext, Task<T>> action, CancellationToken token)
    {
        // Cross-connection metadata appends/native effects are resumed by operation ID;
        // an EF strategy must not blindly replay an interrupted capture/activation.
        await using var context = await NewRebuildContextAsync(token);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await AcquireSharedTransactionAsync(context, token);
        await SqlPublishedPassageSelection.AcquireFenceAsync(context, token);
        var result = await action(context);
        await transaction.CommitAsync(token);
        return result;
    }

    private async Task<FluxKnowledgeDbContext> NewRebuildContextAsync(CancellationToken token)
    {
        await using var configured = await contexts.CreateDbContextAsync(token);
        var connection = configured.Database.GetConnectionString() ?? throw new InvalidOperationException("index-rebuild-database-unavailable");
        return new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection).Options);
    }

    private static int Purpose(int targetFormat) => targetFormat == 2 ? 1 : 2;

    private static void ValidatePlan(IndexProjectionRebuildPlan plan)
    {
        if (plan.OperationId == Guid.Empty || plan.BaseGenerationId == Guid.Empty || plan.SourceFormat is not (1 or 2) || plan.TargetFormat is not (1 or 2) ||
            plan.Stamp.CorpusEpoch == Guid.Empty || plan.Stamp.CorpusVersion < 0 || string.IsNullOrWhiteSpace(plan.ModelFingerprint) || plan.Dimensions <= 0)
            throw new ArgumentException("index-rebuild-plan-invalid");
    }

    private static async Task ValidateRebuildBindingAsync(FluxKnowledgeDbContext context, IndexPublicationCandidateEntity candidate,
        IndexProjectionRebuildPlan plan, CancellationToken token)
    {
        var profile = await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == plan.BaseGenerationId, token);
        if (candidate.BuildPurpose != Purpose(plan.TargetFormat) || candidate.ExpectedIndexFormat != plan.SourceFormat || candidate.BaseGenerationId != plan.BaseGenerationId ||
            candidate.CorpusEpoch != plan.Stamp.CorpusEpoch || candidate.ExpectedCorpusVersion != plan.Stamp.CorpusVersion || candidate.DraftGenerationId is not null ||
            profile.ModelFingerprint != plan.ModelFingerprint || profile.Dimensions != plan.Dimensions || profile.FormatVersion != plan.SourceFormat)
            throw new InvalidOperationException("index-rebuild-operation-conflict");
    }

    private void ClaimRebuildOwner(IndexPublicationCandidateEntity candidate)
    {
        var current = ownerProbe.Current; current.Validate();
        if (candidate.OwnerProcessId == current.ProcessId && candidate.OwnerStartedAtUtc == current.StartedAtUtc &&
            candidate.OwnerMachineFingerprint == current.MachineFingerprint) return;
        if (ownerProbe.Observe(new(candidate.OwnerProcessId, candidate.OwnerStartedAtUtc, candidate.OwnerMachineFingerprint)) != GpuInteractiveOwnerObservation.Exited)
            throw new InvalidOperationException("index-rebuild-owner-alive-or-unknown");
        candidate.OwnerInstanceId = Guid.NewGuid(); candidate.OwnerProcessId = current.ProcessId;
        candidate.OwnerStartedAtUtc = current.StartedAtUtc; candidate.OwnerMachineFingerprint = current.MachineFingerprint;
    }

    private static async Task<IndexProjectionRebuildProgress> RebuildProgressAsync(FluxKnowledgeDbContext context,
        IndexPublicationCandidateEntity candidate, IndexProjectionRebuildPlan plan, CancellationToken token) =>
        new(new(candidate.Id, plan.Stamp, plan.Stamp, plan.ModelFingerprint, plan.Dimensions, []),
            candidate.ResultGenerationId is { } result ? Describe(await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == result, token)) : null,
            candidate.State == 2);

    private static async Task<string> LegacyWorklistChecksumAsync(FluxKnowledgeDbContext context, IndexPublicationCandidateEntity candidate,
        IndexGenerationEntity profile, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{profile.ModelFingerprint}|cos|{profile.Dimensions}|")));
        long last = 0; var first = true;
        while (true)
        {
            var page = await context.IndexCandidateMembers.AsNoTracking().Where(value => value.CandidateId == candidate.Id && value.Phase == 1 && value.VectorId > last)
                .OrderBy(value => value.VectorId).Take(PageSize).ToArrayAsync(token);
            if (page.Length == 0) break;
            foreach (var row in page)
            {
                hash.AppendData(Encoding.UTF8.GetBytes((first ? "" : ",") + row.VectorId.ToString(CultureInfo.InvariantCulture) + ":" + row.PayloadChecksum));
                first = false;
            }
            last = page[^1].VectorId;
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
