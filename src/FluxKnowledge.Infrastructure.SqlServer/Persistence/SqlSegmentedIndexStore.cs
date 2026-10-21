using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Builds a metadata-only, stamped affected worklist. No ordinary publication reads the whole vector catalogue.</summary>
public sealed partial class SqlSegmentedIndexStore(
    IDbContextFactory<FluxKnowledgeDbContext> contexts, TimeProvider clock,
    IGpuInteractiveOwnerProbe ownerProbe, IDeploymentValidationHold? deploymentHold = null,
    IndexSegmentBuildOptions? segmentOptions = null) : ISegmentedIndexStore
{
    private const int PageSize = 256;
    private static readonly string EmptyDigest = Convert.ToHexStringLower(SHA256.HashData([]));

    internal static Task AcquireSharedTransactionAsync(FluxKnowledgeDbContext context, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = {SqlDerivedIndexRecoveryStore.LockResource}, @LockMode = 'Shared', @LockOwner = 'Transaction', @LockTimeout = 5000;
            IF @result < 0 THROW 51000, 'segment-recovery-ownership-unavailable', 1;
            """, cancellationToken);

    public async ValueTask<bool> IsRequiredAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var format = await context.IndexState.Where(value => value.Id == 1)
            .Select(value => value.RequiredIndexFormat).SingleAsync(cancellationToken);
        return format switch { 1 => false, 2 => true, _ => throw new InvalidOperationException("index-format-unsupported") };
    }

    public async ValueTask<SegmentedIndexCandidate> PrepareAsync(Guid draftGenerationId, CancellationToken cancellationToken)
    {
        await using var strategyContext = await contexts.CreateDbContextAsync(cancellationToken);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(
            () => PrepareCoreAsync(draftGenerationId, cancellationToken));
    }

    private async Task<SegmentedIndexCandidate> PrepareCoreAsync(Guid draftGenerationId, CancellationToken cancellationToken)
    {
        // Each attempt has a distinct identity. Retry never combines partial worklists.
        var id = Guid.NewGuid();
        try { return await PrepareOwnedAsync(id, draftGenerationId, cancellationToken); }
        catch (PublicationSnapshotConflictException exception)
        {
            // Metadata preview has unwound and no native handle was opened by this phase.
            try { await AbandonAsync(id, cancellationToken); }
            catch (Exception cleanup) when (!cancellationToken.IsCancellationRequested)
            { exception.Data["candidate-abandonment-failure"] = cleanup.GetType().Name; }
            throw;
        }
    }

    private async Task<SegmentedIndexCandidate> PrepareOwnedAsync(Guid id, Guid draftGenerationId, CancellationToken cancellationToken)
    {
        IndexPublicationCandidateEntity candidate;
        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        await using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
        {
            await AcquireSharedTransactionAsync(context, cancellationToken);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken);
            var state = await context.IndexState.SingleAsync(value => value.Id == 1, cancellationToken);
            if (state.RequiredIndexFormat != 2 || state.CorpusRebuildOperationId is not null)
                throw new PublicationSnapshotConflictException("segmented-publication-not-admitted");
            var scopes = draftGenerationId == Guid.Empty ? [] : (await ResolveDraftScopesAsync(context, draftGenerationId, cancellationToken)).ToList();
            var active = await context.IndexGenerations.AsNoTracking().SingleOrDefaultAsync(
                value => value.Id == state.ActiveIndexGenerationId, cancellationToken);
            if (active is not null)
            {
                if (active.FormatVersion != 2 || active.CorpusEpoch != state.CorpusEpoch ||
                    active.CorpusVersion is null || active.RetiredAtUtc is not null || active.ValidatedAtUtc is null)
                    throw new InvalidOperationException("segmented-bootstrap-required");
                var version = active.CorpusVersion.Value;
                if (version < state.ProjectionChangeFloorVersion || version > state.CorpusVersion)
                    throw new InvalidOperationException("index-change-log-gap");
                if (version < state.CorpusVersion)
                {
                    var changes = await context.IndexProjectionChanges.AsNoTracking().Where(value => value.CorpusEpoch == state.CorpusEpoch &&
                        value.CorpusVersion > version && value.CorpusVersion <= state.CorpusVersion).ToArrayAsync(cancellationToken);
                    if (changes.Select(value => value.CorpusVersion).Distinct().LongCount() != state.CorpusVersion - version ||
                        changes.Any(value => value.ScopeKind == 4)) throw new InvalidOperationException("index-change-log-gap");
                    scopes.AddRange(changes.Select(value => new IndexAffectedScope(value.ScopeKind, value.ScopeId)));
                }
            }
            else
            {
                if (draftGenerationId == Guid.Empty) throw new InvalidOperationException("segmented-refresh-requires-base-manifest");
                // This is initial publication only. An existing corpus needs explicit conversion, never an implicit full rebuild.
                if (await context.CurrentIndexMembers.AnyAsync(cancellationToken) ||
                    await SqlPublishedPassageSelection.HasEligibleVectorsAsync(context, cancellationToken))
                    throw new InvalidOperationException("segmented-bootstrap-required");
            }
            var owner = ownerProbe.Current;
            owner.Validate();
            candidate = new()
            {
                Id = id, CorpusEpoch = state.CorpusEpoch, ExpectedCorpusVersion = state.CorpusVersion,
                PublicationCorpusVersion = state.CorpusVersion, BaseGenerationId = active?.Id,
                DraftGenerationId = draftGenerationId == Guid.Empty ? null : draftGenerationId, AffectedScopesJson = JsonSerializer.Serialize(scopes.Distinct().OrderBy(value => value.Kind).ThenBy(value => value.Id)),
                RepackedSegmentsJson = JsonSerializer.Serialize(await SelectSmallMergeAsync(context, active, cancellationToken)),
                BeforeChecksum = EmptyDigest, AfterChecksum = EmptyDigest, CreatedAtUtc = clock.GetUtcNow(),
                OwnerInstanceId = Guid.NewGuid(), OwnerProcessId = owner.ProcessId,
                OwnerStartedAtUtc = owner.StartedAtUtc, OwnerMachineFingerprint = owner.MachineFingerprint
            };
            context.IndexPublicationCandidates.Add(candidate);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        long ordinal = -1;
        (long Count, string Digest) before;
        (long Count, string Digest) after;
        await using (var preview = await contexts.CreateDbContextAsync(cancellationToken))
        await using (var transaction = await preview.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
        {
            await AcquireSharedTransactionAsync(preview, cancellationToken);
            await SqlPublishedPassageSelection.AcquireFenceAsync(preview, cancellationToken);
            await ValidateBaseAsync(preview, candidate, cancellationToken);
            before = await CaptureAsync(0);
            if (draftGenerationId != Guid.Empty)
                await SqlPipelineStore.PreviewDraftAsync(preview, draftGenerationId, clock.GetUtcNow(), cancellationToken);
            after = await CaptureAsync(1);
            await transaction.RollbackAsync(cancellationToken);

            async Task<(long Count, string Digest)> CaptureAsync(int phase)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long count = 0, last = 0;
                while (true)
                {
                    // Before comes from the indexed active ledger, including entries invalidated by intervening changes.
                    var page = await (phase == 0 ? QueryCurrent(preview, candidate, last) : QueryEligible(preview, candidate.AffectedScopesJson, last))
                        .ToArrayAsync(cancellationToken);
                    if (page.Length == 0) break;
                    AppendHash(hash, page);
                    count += page.Length;
                    last = page[^1].VectorId;
                    await AppendPageAsync(id, phase, ++ordinal, page, cancellationToken);
                }
                return (count, Convert.ToHexStringLower(hash.GetHashAndReset()));
            }
        }
        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        await using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
        {
            await AcquireSharedTransactionAsync(context, cancellationToken);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken);
            await ValidateBaseAsync(context, candidate, cancellationToken);
            var row = await LockCandidateAsync(context, id, cancellationToken);
            if (row.State != 0 || row.WorklistComplete || row.LastAppendOrdinal != ordinal)
                throw new InvalidOperationException("index-worklist-seal-conflict");
            foreach (var phase in new[] { 0, 1 })
            {
                var actual = await HashWorklistAsync(context, id, phase, cancellationToken);
                if (actual != (phase == 0 ? before : after)) throw new InvalidOperationException("index-worklist-proof-mismatch");
            }
            row.BeforeCount = before.Count; row.BeforeChecksum = before.Digest;
            row.AfterCount = after.Count; row.AfterChecksum = after.Digest; row.WorklistComplete = true;
            row.PublicationCorpusVersion = checked(row.ExpectedCorpusVersion + (draftGenerationId == Guid.Empty || before == after ? 0 : 1));
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var profileId = row.DraftGenerationId ?? row.BaseGenerationId;
            var draft = await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == profileId, cancellationToken);
            if (draft.VectorCount == 0 && row.BaseGenerationId is { } profileBase)
                draft = await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == profileBase, cancellationToken);
            var touched = TouchedSegments(context, row);
            var reused = await (from member in context.IndexGenerationSegments.AsNoTracking()
                join segment in context.IndexSegments.AsNoTracking() on member.SegmentId equals segment.Id
                where member.GenerationId == row.BaseGenerationId && !touched.Contains(segment.Id)
                orderby member.Ordinal select segment).ToArrayAsync(cancellationToken);
            return new(id, new(row.CorpusEpoch, row.ExpectedCorpusVersion), new(row.CorpusEpoch, row.PublicationCorpusVersion),
                draft.ModelFingerprint, draft.Dimensions, reused.Select(Describe).ToArray());
        }
    }

    private async Task AppendPageAsync(Guid id, int phase, long ordinal, MemberRow[] page, CancellationToken cancellationToken, bool explicitRebuild = false)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHash(hash, page);
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        // This connection deliberately touches ONLY the candidate and its staging rows.
        // A vector/source FK here can wait on preview writes while preview waits for this append.
        await using var context = explicitRebuild ? await NewRebuildContextAsync(cancellationToken) : await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var candidate = await LockCandidateAsync(context, id, cancellationToken);
        if (candidate.State != 0 || candidate.WorklistComplete) throw new InvalidOperationException("index-worklist-closed");
        if (candidate.LastAppendOrdinal == ordinal && candidate.LastPageChecksum == digest) return;
        if (candidate.LastAppendOrdinal + 1 != ordinal) throw new InvalidOperationException("index-worklist-page-conflict");
        context.IndexCandidateMembers.AddRange(page.Select(value => new IndexCandidateMemberEntity
        {
            CandidateId = id, Phase = phase, VectorId = value.VectorId, PayloadChecksum = value.PayloadChecksum,
            SourceIdentityId = value.SourceIdentityId, OwnerSourceRevisionId = value.OwnerSourceRevisionId, SourceRootId = value.SourceRootId
        }));
        candidate.LastAppendOrdinal = ordinal;
        candidate.LastPageChecksum = digest;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task<IndexAffectedScope[]> ResolveDraftScopesAsync(FluxKnowledgeDbContext context, Guid draftId, CancellationToken cancellationToken)
    {
        var origin = await SqlPipelineStore.ResolveDraftOriginAsync(context, draftId, cancellationToken)
            ?? throw new InvalidOperationException("segmented-publication-requires-one-source-draft");
        var record = await context.PipelineRecords.AsNoTracking().SingleAsync(value => value.Id == origin, cancellationToken);
        var scopes = new List<IndexAffectedScope> { new(1, record.SourceIdentityId) };
        if (record.SourceRevisionId is { } revisionId)
        {
            var revision = await context.SourceRevisions.AsNoTracking().SingleAsync(value => value.Id == revisionId, cancellationToken);
            scopes.Add(new(2, revisionId));
            // Physical revisions link to history too; only retained children use their parent as publication owner.
            if (revision.OriginKind != 0 && revision.ParentSourceRevisionId is { } parent) scopes.Add(new(2, parent));
        }
        return scopes.Distinct().OrderBy(value => value.Kind).ThenBy(value => value.Id).ToArray();
    }

    internal static async Task ValidateBaseAsync(FluxKnowledgeDbContext context, IndexPublicationCandidateEntity candidate, CancellationToken cancellationToken)
    {
        var state = await context.IndexState.AsNoTracking().SingleAsync(value => value.Id == 1, cancellationToken);
        if (state.RequiredIndexFormat != candidate.ExpectedIndexFormat || state.CorpusEpoch != candidate.CorpusEpoch ||
            state.CorpusVersion != candidate.ExpectedCorpusVersion || state.ActiveIndexGenerationId != candidate.BaseGenerationId ||
            state.CorpusRebuildOperationId is not null)
            throw new PublicationSnapshotConflictException("The corpus changed while preparing a segmented publication.");
    }

    internal static Task<IndexPublicationCandidateEntity> LockCandidateAsync(FluxKnowledgeDbContext context, Guid id, CancellationToken cancellationToken) =>
        context.IndexPublicationCandidates.FromSqlInterpolated($"SELECT * FROM [IndexPublicationCandidates] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}")
            .SingleAsync(cancellationToken);

    private const string ScopeFilter = """
        EXISTS (SELECT 1 FROM OPENJSON({0}) WITH ([Kind] int '$.Kind', [Id] uniqueidentifier '$.Id') AS [scope]
            WHERE ([scope].[Kind] = 1 AND [scope].[Id] = [record].[SourceIdentityId])
               OR ([scope].[Kind] = 2 AND [scope].[Id] = COALESCE([owner].[Id],
                   CASE WHEN [retained].[OriginKind] <> 0 THEN [retained].[ParentSourceRevisionId] END, [retained].[Id]))
               OR ([scope].[Kind] = 3 AND [scope].[Id] = COALESCE([owner].[SourceRootId], [retained].[SourceRootId])))
        """;

    // Seed from indexed source/owner/root relationships before touching chunks or
    // vectors. The final ScopeFilter still checks the authoritative coalesced owner;
    // this intentionally inclusive seed never replaces publication eligibility.
    private const string AffectedRecords = """
        SELECT [record].[Id] FROM OPENJSON({0}) WITH ([Kind] int '$.Kind', [Id] uniqueidentifier '$.Id') AS [scope]
        INNER JOIN [PipelineRecords] AS [record] ON [record].[SourceIdentityId] = [scope].[Id] WHERE [scope].[Kind] = 1
        UNION
        SELECT [record].[Id] FROM OPENJSON({0}) WITH ([Kind] int '$.Kind', [Id] uniqueidentifier '$.Id') AS [scope]
        INNER JOIN [PipelineRecords] AS [record] ON [record].[SourceRevisionId] = [scope].[Id] WHERE [scope].[Kind] = 2
        UNION
        SELECT [record].[Id] FROM OPENJSON({0}) WITH ([Kind] int '$.Kind', [Id] uniqueidentifier '$.Id') AS [scope]
        INNER JOIN [SourceRevisions] AS [retained] ON [retained].[ParentSourceRevisionId] = [scope].[Id]
        INNER JOIN [PipelineRecords] AS [record] ON [record].[SourceRevisionId] = [retained].[Id] WHERE [scope].[Kind] = 2
        UNION
        SELECT [publication].[PipelineRecordId] FROM OPENJSON({0}) WITH ([Kind] int '$.Kind', [Id] uniqueidentifier '$.Id') AS [scope]
        INNER JOIN [DocumentPublications] AS [publication] ON [publication].[OwnerSourceRevisionId] = [scope].[Id] WHERE [scope].[Kind] = 2
        UNION
        SELECT [record].[Id] FROM OPENJSON({0}) WITH ([Kind] int '$.Kind', [Id] uniqueidentifier '$.Id') AS [scope]
        INNER JOIN [SourceRevisions] AS [retained] ON [retained].[SourceRootId] = [scope].[Id]
        INNER JOIN [PipelineRecords] AS [record] ON [record].[SourceRevisionId] = [retained].[Id] WHERE [scope].[Kind] = 3
        UNION
        SELECT [publication].[PipelineRecordId] FROM OPENJSON({0}) WITH ([Kind] int '$.Kind', [Id] uniqueidentifier '$.Id') AS [scope]
        INNER JOIN [SourceRevisions] AS [owner] ON [owner].[SourceRootId] = [scope].[Id]
        INNER JOIN [DocumentPublications] AS [publication] ON [publication].[OwnerSourceRevisionId] = [owner].[Id] WHERE [scope].[Kind] = 3
        """;

    internal static IQueryable<MemberRow> QueryEligible(FluxKnowledgeDbContext context, string scopes, long last) =>
        context.Database.SqlQuery<MemberRow>(SqlPublishedPassageSelection.Bind(FormattableStringFactory.Create("""
            SELECT TOP (256) [vector].[VectorId], [vector].[PayloadChecksum], [record].[SourceIdentityId],
                COALESCE([owner].[Id], CASE WHEN [retained].[OriginKind] <> 0 THEN [retained].[ParentSourceRevisionId] END, [retained].[Id]) AS [OwnerSourceRevisionId],
                COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) AS [SourceRootId]
            FROM (
            """ + AffectedRecords + """
            ) AS [affected]
            INNER LOOP JOIN [PipelineRecords] AS [record] ON [record].[Id] = [affected].[Id]
            INNER LOOP JOIN [Artifacts] AS [artifact] ON [artifact].[PipelineRecordId] = [record].[Id]
            INNER LOOP JOIN [TextChunks] AS [chunk] ON [chunk].[ArtifactId] = [artifact].[Id]
            INNER LOOP JOIN [Vectors] AS [vector] ON [vector].[TextChunkId] = [chunk].[Id]
            /*publication-bindings*/
            WHERE /*publication-eligibility*/ AND
            """ + " " + ScopeFilter + """
              AND [vector].[VectorId] > {1} AND [vector].[IsDeleted] = 0
              AND [vector].[SourceRevision] = [chunk].[SourceRevision]
              AND [vector].[TextChunkContentHash] COLLATE Latin1_General_100_BIN2 = [chunk].[ContentHash] COLLATE Latin1_General_100_BIN2
              AND ([vector].[SearchInputHash] IS NULL OR [vector].[SearchInputHash] COLLATE Latin1_General_100_BIN2 = [chunk].[SearchInputHash] COLLATE Latin1_General_100_BIN2)
            ORDER BY [vector].[VectorId]
            """, scopes, last)));

    internal static IQueryable<MemberRow> QueryCurrent(FluxKnowledgeDbContext context, IndexPublicationCandidateEntity candidate, long last) =>
        context.Database.SqlQuery<MemberRow>($"""
            SELECT TOP (256) [member].[VectorId], [vector].[PayloadChecksum], [member].[SourceIdentityId], [member].[OwnerSourceRevisionId], [member].[SourceRootId]
            FROM (
                SELECT [current].[VectorId] FROM OPENJSON({candidate.AffectedScopesJson}) WITH ([Kind] int '$.Kind', [Id] uniqueidentifier '$.Id') AS [scope]
                INNER LOOP JOIN [CurrentIndexMembers] AS [current] ON [current].[CorpusEpoch] = {candidate.CorpusEpoch}
                    AND [current].[SourceIdentityId] = [scope].[Id] AND [current].[VectorId] > {last} WHERE [scope].[Kind] = 1
                UNION
                SELECT [current].[VectorId] FROM OPENJSON({candidate.AffectedScopesJson}) WITH ([Kind] int '$.Kind', [Id] uniqueidentifier '$.Id') AS [scope]
                INNER LOOP JOIN [CurrentIndexMembers] AS [current] ON [current].[CorpusEpoch] = {candidate.CorpusEpoch}
                    AND [current].[OwnerSourceRevisionId] = [scope].[Id] AND [current].[VectorId] > {last} WHERE [scope].[Kind] = 2
                UNION
                SELECT [current].[VectorId] FROM OPENJSON({candidate.AffectedScopesJson}) WITH ([Kind] int '$.Kind', [Id] uniqueidentifier '$.Id') AS [scope]
                INNER LOOP JOIN [CurrentIndexMembers] AS [current] ON [current].[CorpusEpoch] = {candidate.CorpusEpoch}
                    AND [current].[SourceRootId] = [scope].[Id] AND [current].[VectorId] > {last} WHERE [scope].[Kind] = 3
            ) AS [affected]
            INNER LOOP JOIN [CurrentIndexMembers] AS [member] ON [member].[CorpusEpoch] = {candidate.CorpusEpoch} AND [member].[VectorId] = [affected].[VectorId]
            INNER LOOP JOIN [Vectors] AS [vector] ON [member].[VectorId] = [vector].[VectorId]
            ORDER BY [member].[VectorId]
            """);

    private static void AppendHash(IncrementalHash hash, IEnumerable<MemberRow> rows)
    {
        foreach (var row in rows)
            hash.AppendData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"{row.VectorId}|{row.PayloadChecksum}|{row.SourceIdentityId:N}|{row.OwnerSourceRevisionId:N}|{row.SourceRootId:N}\n")));
    }

    private static async Task<(long Count, string Digest)> HashWorklistAsync(FluxKnowledgeDbContext context, Guid id, int phase, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0, last = 0;
        while (true)
        {
            var page = await context.IndexCandidateMembers.AsNoTracking().Where(value => value.CandidateId == id && value.Phase == phase && value.VectorId > last)
                .OrderBy(value => value.VectorId).Take(PageSize).Select(value => new MemberRow
                {
                    VectorId = value.VectorId, PayloadChecksum = value.PayloadChecksum, SourceIdentityId = value.SourceIdentityId,
                    OwnerSourceRevisionId = value.OwnerSourceRevisionId, SourceRootId = value.SourceRootId
                }).ToArrayAsync(cancellationToken);
            if (page.Length == 0) break;
            AppendHash(hash, page); count += page.Length; last = page[^1].VectorId;
        }
        return (count, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    internal static IQueryable<string> TouchedSegments(FluxKnowledgeDbContext context, IndexPublicationCandidateEntity candidate)
    {
        if (candidate.BuildPurpose != 0)
            return context.IndexGenerationSegments.Where(value => value.GenerationId == candidate.BaseGenerationId).Select(value => value.SegmentId);
        var repacked = JsonSerializer.Deserialize<string[]>(candidate.RepackedSegmentsJson)
            ?? throw new InvalidOperationException("index-merge-proof-invalid");
        if (repacked.Length > 4 || repacked.Distinct(StringComparer.Ordinal).Count() != repacked.Length ||
            repacked.Any(id => id is null || id.Length != 64 || id.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new InvalidOperationException("index-merge-proof-invalid");
        var removed = from before in context.IndexCandidateMembers where before.CandidateId == candidate.Id && before.Phase == 0 &&
            !context.IndexCandidateMembers.Any(after => after.CandidateId == candidate.Id && after.Phase == 1 && after.VectorId == before.VectorId)
        join current in context.CurrentIndexMembers on new { candidate.CorpusEpoch, before.VectorId } equals new { current.CorpusEpoch, current.VectorId }
        select current.SegmentId;
        return removed.Union(context.IndexSegments.Where(value => repacked.Contains(value.Id)).Select(value => value.Id));
    }

    private async Task<string[]> SelectSmallMergeAsync(FluxKnowledgeDbContext context, IndexGenerationEntity? active, CancellationToken token)
    {
        if (active is null) return [];
        if (active.Dimensions <= 0) throw new InvalidOperationException("index-merge-profile-invalid");
        var options = segmentOptions ?? new IndexSegmentBuildOptions();
        options.Validate();
        // Four small segments fit in one bounded native build. Piggyback on an
        // ordinary stamped publication; no separate compactor or full-corpus copy.
        var smallLimit = Math.Min(options.MaximumVectors / 4L, options.MaximumPayloadBytes / (4L * sizeof(float) * active.Dimensions));
        if (smallLimit == 0) return [];
        var selected = await (from member in context.IndexGenerationSegments
                              join segment in context.IndexSegments on member.SegmentId equals segment.Id
                              where member.GenerationId == active.Id && segment.State == 1 && segment.VectorCount <= smallLimit
                              orderby segment.VectorCount, segment.Id select segment.Id).Take(4).ToArrayAsync(token);
        return selected.Length == 4 ? selected : [];
    }

    private static IQueryable<long> BuildIds(FluxKnowledgeDbContext context, IndexPublicationCandidateEntity candidate) =>
        candidate.BuildPurpose != 0
        ? context.IndexCandidateMembers.Where(value => value.CandidateId == candidate.Id && value.Phase == 1).Select(value => value.VectorId)
        : context.IndexCandidateMembers.Where(after => after.CandidateId == candidate.Id && after.Phase == 1 &&
            !context.CurrentIndexMembers.Any(current => current.CorpusEpoch == candidate.CorpusEpoch && current.VectorId == after.VectorId))
            .Select(value => value.VectorId)
        .Union(context.CurrentIndexMembers.Where(current => current.CorpusEpoch == candidate.CorpusEpoch && TouchedSegments(context, candidate).Contains(current.SegmentId) &&
            !context.IndexCandidateMembers.Any(before => before.CandidateId == candidate.Id && before.Phase == 0 && before.VectorId == current.VectorId &&
                !context.IndexCandidateMembers.Any(after => after.CandidateId == candidate.Id && after.Phase == 1 && after.VectorId == current.VectorId)))
            .Select(value => value.VectorId));

    public async IAsyncEnumerable<CanonicalVector> ReadBuildVectorsAsync(Guid candidateId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var candidate = await context.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == candidateId, cancellationToken);
        if ((candidate.State != 0 && !(candidate.BuildPurpose != 0 && candidate.State == 1)) || !candidate.WorklistComplete)
            throw new InvalidOperationException("index-worklist-unsealed");
        long last = 0;
        while (true)
        {
            var page = await QueryBuildVectors(context, candidate, last).ToArrayAsync(cancellationToken);
            if (page.Length == 0) yield break;
            foreach (var value in page)
                yield return new(value.VectorId, value.TextChunkId, value.ModelFingerprint, value.Dimensions, value.Values,
                    value.TextChunkContentHash, value.PayloadChecksum, value.SourceRevision);
            last = page[^1].VectorId;
        }
    }

    internal static IQueryable<VectorEntity> QueryBuildVectors(FluxKnowledgeDbContext context, IndexPublicationCandidateEntity candidate, long last) =>
        context.Vectors.AsNoTracking().Where(value => value.VectorId > last && BuildIds(context, candidate).Contains(value.VectorId))
            .OrderBy(value => value.VectorId).Take(PageSize);

    public async ValueTask SealSegmentAsync(Guid candidateId, IndexSegmentDescriptor segment, IReadOnlyList<CanonicalVector> vectors, CancellationToken cancellationToken)
    {
        await using var strategyContext = await contexts.CreateDbContextAsync(cancellationToken);
        await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(
            () => SealSegmentCoreAsync(candidateId, segment, vectors, cancellationToken));
    }

    private async Task SealSegmentCoreAsync(Guid candidateId, IndexSegmentDescriptor segment, IReadOnlyList<CanonicalVector> vectors, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await AcquireSharedTransactionAsync(context, cancellationToken);
        await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken);
        var candidate = await LockCandidateAsync(context, candidateId, cancellationToken);
        await ValidateBaseAsync(context, candidate, cancellationToken);
        if (candidate.State != 0 || !candidate.WorklistComplete || segment.VectorCount != vectors.Count || vectors.Count == 0 ||
            segment.FormatVersion != 2 || (segment.PhysicalIncarnation != Guid.Empty && segment.PhysicalIncarnation != candidate.Id) ||
            segment.Id != IndexSegmentDescriptor.ComputeId(segment.NativeFormat, segment.MembershipChecksum, segment.PhysicalIncarnation))
            throw new InvalidOperationException("index-segment-seal-invalid");
        var ids = vectors.Select(value => value.VectorId).ToArray();
        var allowed = await context.Vectors.AsNoTracking().Where(value => ids.Contains(value.VectorId) && BuildIds(context, candidate).Contains(value.VectorId))
            .Select(value => new { value.VectorId, value.PayloadChecksum }).ToArrayAsync(cancellationToken);
        if (allowed.Length != ids.Length || allowed.Any(value => vectors.Single(vector => vector.VectorId == value.VectorId).PayloadChecksum != value.PayloadChecksum))
            throw new InvalidOperationException("index-segment-membership-invalid");
        var existing = await context.IndexSegments.SingleOrDefaultAsync(value => value.Id == segment.Id, cancellationToken);
        if (existing is not null)
        {
            var existingIds = await context.IndexSegmentVectors.Where(value => value.SegmentId == segment.Id)
                .OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync(cancellationToken);
            if (existing.State != 1 || Describe(existing) != segment ||
                !existingIds.SequenceEqual(ids.Order()))
                throw new InvalidOperationException("index-segment-identity-conflict");
        }
        else
        {
            context.IndexSegments.Add(new()
            {
                Id = segment.Id, ModelFingerprint = segment.ModelFingerprint, Dimensions = segment.Dimensions,
                NativeFormat = segment.NativeFormat, DirectoryPath = segment.DirectoryPath, MembershipChecksum = segment.MembershipChecksum,
                FileChecksum = segment.FileChecksum, FileLength = segment.FileLength, VectorCount = segment.VectorCount,
                State = 1, ProducingCandidateId = candidateId, CreatedAtUtc = clock.GetUtcNow(), SealedAtUtc = clock.GetUtcNow()
            });
            context.IndexSegmentVectors.AddRange(ids.Select(id => new IndexSegmentVectorEntity { SegmentId = segment.Id, VectorId = id }));
            await context.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    internal static IndexSegmentDescriptor Describe(IndexSegmentEntity value) => new(value.Id, value.ModelFingerprint, value.Dimensions,
        value.DirectoryPath, value.MembershipChecksum, value.FileChecksum, value.FileLength, value.VectorCount, value.NativeFormat,
        PhysicalIncarnation: value.Id == IndexSegmentDescriptor.ComputeId(value.NativeFormat, value.MembershipChecksum)
            ? Guid.Empty : value.ProducingCandidateId ?? Guid.Empty);

    internal sealed class MemberRow
    {
        public long VectorId { get; init; }
        public string PayloadChecksum { get; init; } = "";
        public Guid SourceIdentityId { get; init; }
        public Guid? OwnerSourceRevisionId { get; init; }
        public Guid? SourceRootId { get; init; }
    }
}
