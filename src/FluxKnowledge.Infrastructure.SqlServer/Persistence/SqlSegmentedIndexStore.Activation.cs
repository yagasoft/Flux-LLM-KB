using System.Data;
using System.Security.Cryptography;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSegmentedIndexStore
{
    public async ValueTask AbandonAsync(Guid candidateId, CancellationToken cancellationToken)
    {
        await using var strategyContext = await contexts.CreateDbContextAsync(cancellationToken);
        await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireSharedTransactionAsync(context, cancellationToken);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken);
            var candidate = await context.IndexPublicationCandidates.FromSqlInterpolated(
                $"SELECT * FROM [IndexPublicationCandidates] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {candidateId}")
                .SingleOrDefaultAsync(cancellationToken);
            if (candidate is null) return; // Preparation can conflict before its first committed row.
            if (candidate.BuildPurpose != 0)
                throw new InvalidOperationException("index-rebuild-requires-explicit-abandonment");
            var owner = ownerProbe.Current;
            owner.Validate();
            if (candidate.OwnerProcessId != owner.ProcessId || candidate.OwnerStartedAtUtc != owner.StartedAtUtc ||
                candidate.OwnerMachineFingerprint != owner.MachineFingerprint)
                throw new InvalidOperationException("index-candidate-abandonment-owner-mismatch");
            if (candidate.State is 2 or 3) return; // A committed activation wins; replay cannot revoke it.
            if (candidate.State is not (0 or 1)) throw new InvalidOperationException("index-candidate-state-unsupported");
            candidate.State = 3;
            candidate.CompletedAtUtc = clock.GetUtcNow();
            await context.IndexCandidateMembers.Where(value => value.CandidateId == candidateId).ExecuteDeleteAsync(cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    public async ValueTask<SegmentedProjectionState> ReadProjectionAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var state = await context.IndexState.AsNoTracking().SingleAsync(value => value.Id == 1, cancellationToken);
        var generation = await context.IndexGenerations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == state.ActiveIndexGenerationId, cancellationToken);
        if (state.ActiveIndexGenerationId is not null && (generation is null || generation.FormatVersion != 2 || generation.RetiredAtUtc is not null))
            throw new InvalidOperationException("active-segmented-manifest-invalid");
        var empty = state.ActiveIndexGenerationId is null && state.EmptyCatalogueValidatedAtUtc is not null &&
            !await context.Vectors.AnyAsync(cancellationToken) && !await context.IndexGenerations.AnyAsync(cancellationToken) &&
            !await context.CurrentIndexMembers.AnyAsync(cancellationToken);
        return new(new(state.CorpusEpoch, state.CorpusVersion), generation is null ? null : Describe(generation), state.CorpusRebuildOperationId is not null, empty);
    }

    public async ValueTask<bool> TryActivateRefreshAsync(IndexGenerationCandidateSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.SegmentedCandidateId is not { } id || snapshot.Generation.FormatVersion != 2 || snapshot.Vectors.Count != 0)
            throw new InvalidOperationException("segmented-refresh-proof-invalid");
        await using var strategyContext = await contexts.CreateDbContextAsync(cancellationToken);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireSharedTransactionAsync(context, cancellationToken);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken);
            if (deploymentHold?.IsHeld == true) return false;
            var candidate = await LockCandidateAsync(context, id, cancellationToken);
            if (candidate.BuildPurpose != 0 || candidate.DraftGenerationId is not null || candidate.ResultGenerationId != snapshot.Generation.Id)
                throw new InvalidOperationException("segmented-refresh-proof-invalid");
            if (candidate.State == 2) return true;
            try { candidate = await ValidateRefreshBeforeAsync(context, snapshot, cancellationToken); }
            catch (PublicationSnapshotConflictException) { return false; }
            var state = await context.IndexState.SingleAsync(value => value.Id == 1, cancellationToken);
            await ActivateAsync(context, candidate, state, clock.GetUtcNow(), cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }

    internal static async Task<IndexPublicationCandidateEntity> ValidateRefreshBeforeAsync(FluxKnowledgeDbContext context,
        IndexGenerationCandidateSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.SegmentedCandidateId is not { } id || snapshot.Generation.FormatVersion != 2 || snapshot.Vectors.Count != 0)
            throw new InvalidOperationException("segmented-refresh-proof-invalid");
        var candidate = await LockCandidateAsync(context, id, cancellationToken);
        await ValidateBaseAsync(context, candidate, cancellationToken);
        if (candidate.BuildPurpose != 0 || candidate.State != 1 || candidate.DraftGenerationId is not null || candidate.ResultGenerationId != snapshot.Generation.Id ||
            !candidate.WorklistComplete || snapshot.ExpectedCorpusStamp != new CorpusPublicationStamp(candidate.CorpusEpoch, candidate.ExpectedCorpusVersion) ||
            snapshot.Generation.CorpusStamp != snapshot.ExpectedCorpusStamp ||
            Describe(await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == snapshot.Generation.Id, cancellationToken)) != snapshot.Generation ||
            await HashCurrentAsync(context, candidate, cancellationToken) != (candidate.BeforeCount, candidate.BeforeChecksum))
            throw new InvalidOperationException("segmented-refresh-proof-invalid");
        return candidate;
    }

    public async ValueTask SealManifestAsync(Guid candidateId, IndexManifestDescriptor manifest, CancellationToken cancellationToken)
    {
        await using var strategyContext = await contexts.CreateDbContextAsync(cancellationToken);
        await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(
            () => SealManifestCoreAsync(candidateId, manifest, cancellationToken));
    }

    private async Task SealManifestCoreAsync(Guid candidateId, IndexManifestDescriptor manifest, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await AcquireSharedTransactionAsync(context, cancellationToken);
        await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken);
        var candidate = await LockCandidateAsync(context, candidateId, cancellationToken);
        await ValidateBaseAsync(context, candidate, cancellationToken);
        var descriptor = manifest.Generation;
        if (candidate.State == 1 && candidate.ResultGenerationId == descriptor.Id)
        {
            var committed = await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == descriptor.Id, cancellationToken);
            var committedIds = await context.IndexGenerationSegments.AsNoTracking().Where(value => value.GenerationId == descriptor.Id)
                .OrderBy(value => value.Ordinal).Select(value => value.SegmentId).ToArrayAsync(cancellationToken);
            if (Describe(committed) != descriptor || !committedIds.SequenceEqual(manifest.Segments.Select(value => value.Id)))
                throw new InvalidOperationException("index-manifest-identity-conflict");
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (candidate.BuildPurpose == 2 || candidate.State != 0 || !candidate.WorklistComplete || descriptor.FormatVersion != 2 ||
            descriptor.CorpusStamp != new CorpusPublicationStamp(candidate.CorpusEpoch, candidate.PublicationCorpusVersion) ||
            descriptor.SegmentCount != manifest.Segments.Count || descriptor.VectorCount != manifest.Segments.Sum(value => value.VectorCount))
            throw new InvalidOperationException("index-manifest-seal-invalid");
        var touched = TouchedSegments(context, candidate);
        var reused = await context.IndexGenerationSegments.AsNoTracking().Where(value => value.GenerationId == candidate.BaseGenerationId &&
            !touched.Contains(value.SegmentId)).Select(value => value.SegmentId).ToArrayAsync(cancellationToken);
        var ids = manifest.Segments.Select(value => value.Id).ToArray();
        if (ids.Distinct().Count() != ids.Length || reused.Except(ids).Any()) throw new InvalidOperationException("index-manifest-reuse-invalid");
        var sealedSegments = await context.IndexSegments.AsNoTracking().Where(value => ids.Contains(value.Id)).ToArrayAsync(cancellationToken);
        if (sealedSegments.Length != ids.Length || sealedSegments.Any(value => value.State != 1 ||
            Describe(value) != manifest.Segments.Single(segment => segment.Id == value.Id) ||
            value.ModelFingerprint != descriptor.ModelFingerprint || value.Dimensions != descriptor.Dimensions))
            throw new InvalidOperationException("index-manifest-segment-proof-invalid");
        var replacements = ids.Except(reused).ToArray();
        var built = context.IndexSegmentVectors.Where(value => replacements.Contains(value.SegmentId)).Select(value => value.VectorId);
        if (await BuildIds(context, candidate).Except(built).AnyAsync(cancellationToken) ||
            await built.Except(BuildIds(context, candidate)).AnyAsync(cancellationToken) ||
            await built.GroupBy(value => value).AnyAsync(group => group.Count() != 1, cancellationToken))
            throw new InvalidOperationException("index-manifest-replacement-membership-invalid");
        var old = await context.IndexGenerations.SingleOrDefaultAsync(value => value.Id == descriptor.Id, cancellationToken);
        if (old is null)
        {
            context.IndexGenerations.Add(new()
            {
                Id = descriptor.Id, FormatVersion = 2, SegmentCount = descriptor.SegmentCount,
                CorpusEpoch = candidate.CorpusEpoch, CorpusVersion = candidate.PublicationCorpusVersion,
                ModelFingerprint = descriptor.ModelFingerprint, Dimensions = descriptor.Dimensions, IndexPath = descriptor.IndexPath,
                MetadataChecksum = descriptor.MetadataChecksum, VectorCount = descriptor.VectorCount,
                CreatedAtUtc = clock.GetUtcNow(), ValidatedAtUtc = clock.GetUtcNow()
            });
            context.IndexGenerationSegments.AddRange(ids.Select((id, ordinal) => new IndexGenerationSegmentEntity
                { GenerationId = descriptor.Id, SegmentId = id, Ordinal = ordinal }));
        }
        else
        {
            var existingIds = await context.IndexGenerationSegments.Where(value => value.GenerationId == descriptor.Id)
                .OrderBy(value => value.Ordinal).Select(value => value.SegmentId).ToArrayAsync(cancellationToken);
            if (Describe(old) != descriptor || !ids.SequenceEqual(existingIds))
                throw new InvalidOperationException("index-manifest-identity-conflict");
        }
        candidate.ResultGenerationId = descriptor.Id;
        candidate.State = 1;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<IndexManifestDescriptor> ReadManifestAsync(Guid generationId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var generation = await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == generationId, cancellationToken);
        if (generation.FormatVersion != 2 || generation.RetiredAtUtc is not null || generation.ValidatedAtUtc is null)
            throw new InvalidOperationException("index-manifest-unavailable");
        var segments = await (from member in context.IndexGenerationSegments.AsNoTracking()
            join segment in context.IndexSegments.AsNoTracking() on member.SegmentId equals segment.Id
            where member.GenerationId == generationId orderby member.Ordinal select segment).ToArrayAsync(cancellationToken);
        if (segments.Length != generation.SegmentCount || segments.Any(value => value.State != 1) ||
            segments.Sum(value => value.VectorCount) != generation.VectorCount)
            throw new InvalidOperationException("index-manifest-metadata-invalid");
        return new(Describe(generation), segments.Select(Describe).ToArray());
    }

    internal static IndexGenerationDescriptor Describe(IndexGenerationEntity value) => new(value.Id, value.ModelFingerprint, value.Dimensions,
        value.IndexPath, value.MetadataChecksum, value.VectorCount,
        value.CorpusEpoch is { } epoch && value.CorpusVersion is { } version ? new(epoch, version) : null, value.FormatVersion, value.SegmentCount);

    internal static async Task<IndexPublicationCandidateEntity> ValidateActivationBeforeAsync(FluxKnowledgeDbContext context,
        Guid candidateId, IndexGenerationDescriptor generation, Guid recordId, long revision, CancellationToken cancellationToken)
    {
        var candidate = await LockCandidateAsync(context, candidateId, cancellationToken);
        await ValidateBaseAsync(context, candidate, cancellationToken);
        if (candidate.BuildPurpose != 0 || candidate.State != 1 || !candidate.WorklistComplete || candidate.ResultGenerationId != generation.Id ||
            candidate.DraftGenerationId is not { } draft || Describe(await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == generation.Id, cancellationToken)) != generation ||
            await SqlPipelineStore.ResolveDraftOriginAsync(context, draft, cancellationToken) != recordId ||
            !await context.PipelineRecords.AnyAsync(value => value.Id == recordId && value.Revision == revision, cancellationToken))
            throw new PublicationSnapshotConflictException("The sealed candidate does not match the current publication.");
        var before = await HashCurrentAsync(context, candidate, cancellationToken);
        if (before != (candidate.BeforeCount, candidate.BeforeChecksum))
            throw new PublicationSnapshotConflictException("The affected base membership changed.");
        return candidate;
    }

    internal static async Task ActivateAsync(FluxKnowledgeDbContext context, IndexPublicationCandidateEntity candidate,
        IndexStateEntity state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (candidate.BuildPurpose != 0) throw new InvalidOperationException("index-rebuild-requires-explicit-activation");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0, last = 0;
        while (true)
        {
            var page = await QueryEligible(context, candidate.AffectedScopesJson, last).ToArrayAsync(cancellationToken);
            if (page.Length == 0) break;
            AppendHash(hash, page); count += page.Length; last = page[^1].VectorId;
        }
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (count != candidate.AfterCount || digest != candidate.AfterChecksum)
            throw new PublicationSnapshotConflictException("The affected publication changed; rebuild the candidate.");
        // Replace only changed ledger rows; shared segment membership is never copied per publication.
        var id = candidate.Id;
        var epoch = candidate.CorpusEpoch;
        var result = candidate.ResultGenerationId!.Value;
        var touched = await TouchedSegments(context, candidate).Distinct().ToArrayAsync(cancellationToken);
        var touchedJson = System.Text.Json.JsonSerializer.Serialize(touched);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE [current] FROM [CurrentIndexMembers] AS [current]
            WHERE [current].[CorpusEpoch] = {epoch} AND EXISTS (
                SELECT 1 FROM [IndexCandidateMembers] AS [before]
                WHERE [before].[CandidateId] = {id} AND [before].[Phase] = 0 AND [before].[VectorId] = [current].[VectorId]
                  AND NOT EXISTS (SELECT 1 FROM [IndexCandidateMembers] AS [after]
                    WHERE [after].[CandidateId] = {id} AND [after].[Phase] = 1 AND [after].[VectorId] = [current].[VectorId]));
            UPDATE [current] SET [SegmentId] = [member].[SegmentId]
            FROM [CurrentIndexMembers] AS [current]
            INNER JOIN [IndexSegmentVectors] AS [member] ON [member].[VectorId] = [current].[VectorId]
            INNER JOIN [IndexGenerationSegments] AS [manifest] ON [manifest].[SegmentId] = [member].[SegmentId]
            WHERE [current].[CorpusEpoch] = {epoch} AND [manifest].[GenerationId] = {result}
              AND [current].[SegmentId] IN (SELECT [Id] FROM OPENJSON({touchedJson}) WITH ([Id] char(64) '$'))
              AND [current].[SegmentId] <> [member].[SegmentId];
            UPDATE [current] SET [SourceIdentityId] = [after].[SourceIdentityId], [OwnerSourceRevisionId] = [after].[OwnerSourceRevisionId], [SourceRootId] = [after].[SourceRootId]
            FROM [CurrentIndexMembers] AS [current] INNER JOIN [IndexCandidateMembers] AS [after] ON [current].[VectorId] = [after].[VectorId]
            WHERE [current].[CorpusEpoch] = {epoch} AND [after].[CandidateId] = {id} AND [after].[Phase] = 1;
            INSERT INTO [CurrentIndexMembers] ([CorpusEpoch], [VectorId], [SegmentId], [SourceIdentityId], [OwnerSourceRevisionId], [SourceRootId])
            SELECT {epoch}, [after].[VectorId], [member].[SegmentId], [after].[SourceIdentityId], [after].[OwnerSourceRevisionId], [after].[SourceRootId]
            FROM [IndexCandidateMembers] AS [after]
            INNER JOIN [IndexSegmentVectors] AS [member] ON [member].[VectorId] = [after].[VectorId]
            INNER JOIN [IndexGenerationSegments] AS [manifest] ON [manifest].[SegmentId] = [member].[SegmentId]
            WHERE [after].[CandidateId] = {id} AND [after].[Phase] = 1 AND [manifest].[GenerationId] = {result}
              AND NOT EXISTS (SELECT 1 FROM [CurrentIndexMembers] AS [current] WHERE [current].[CorpusEpoch] = {epoch} AND [current].[VectorId] = [after].[VectorId]);
            """, cancellationToken);
        // Eligibility changes are logged with the same commit, even when activation also catches up earlier changes.
        if (state.CorpusVersion != candidate.PublicationCorpusVersion)
        {
            var scopes = System.Text.Json.JsonSerializer.Deserialize<IndexAffectedScope[]>(candidate.AffectedScopesJson)!;
            state.CorpusVersion = candidate.PublicationCorpusVersion;
            context.IndexProjectionChanges.AddRange(scopes.Select(scope => new IndexProjectionChangeEntity
            {
                CorpusEpoch = epoch, CorpusVersion = state.CorpusVersion, ScopeKind = scope.Kind, ScopeId = scope.Id, CreatedAtUtc = now
            }));
        }
        state.ActiveIndexGenerationId = result;
        state.EmptyCatalogueValidatedAtUtc = null;
        state.UpdatedAtUtc = now;
        candidate.State = 2;
        candidate.CompletedAtUtc = now;
        // The committed ledger and candidate digests are the durable proof. Drop the
        // temporary preview rows in the activation transaction so they cannot grow
        // with every successful publication; uncommitted candidates retain theirs.
        await context.IndexCandidateMembers.Where(value => value.CandidateId == candidate.Id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task<(long Count, string Digest)> HashCurrentAsync(FluxKnowledgeDbContext context,
        IndexPublicationCandidateEntity candidate, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0, last = 0;
        while (true)
        {
            var page = await QueryCurrent(context, candidate, last).ToArrayAsync(cancellationToken);
            if (page.Length == 0) break;
            AppendHash(hash, page); count += page.Length; last = page[^1].VectorId;
        }
        return (count, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}
