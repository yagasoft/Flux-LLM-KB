using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text.Json;
using System.Runtime.CompilerServices;
using FluxKnowledge.Application.Sources;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed record TextChunkWindowBackfillResult(int Inspected, int Recomputed, bool Completed);

public sealed class SqlTextChunkWindowEligibilityStore(IDbContextFactory<FluxKnowledgeDbContext> factory,
    TimeProvider? timeProvider = null, string ruleVersion = CanonicalWindowEligibility.RuleVersion, IDeploymentValidationHold? hold = null)
{
    internal string RuleVersion => ruleVersion;

    public ValueTask<TextChunkWindowBackfillResult> RunOneAsync(CancellationToken cancellationToken, int batchSize = 128)
    {
        if (batchSize is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (hold?.IsHeld == true) return ValueTask.FromResult(new TextChunkWindowBackfillResult(0,0,false));
        return RunCoreAsync(batchSize,cancellationToken);
    }

    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();

    public async ValueTask RequestRevisitAsync(long chunkId, CancellationToken ct)
    {
        if (chunkId <= 0) throw new ArgumentOutOfRangeException(nameof(chunkId));
        if (hold?.IsHeld == true) return;
        await using (var eligibility = await factory.CreateDbContextAsync(ct))
            if (!(await ReadInputsAsync(eligibility,0,long.MaxValue,1,[chunkId],ct)).Any(input => !input.ProofMatches)) return;
        await using var driver = await factory.CreateDbContextAsync(ct);
        await driver.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);
            var state = await ReadStateForUpdateAsync(db,ct);
            if (state is null)
            {
                state = new() { Id=1,RuleVersion=ruleVersion,UpdatedAtUtc=Now };
                db.TextChunkWindowBackfillStates.Add(state);
            }
            if (state.RuleVersion == ruleVersion)
            {
                state.RevisitFromChunkId = Math.Min(state.RevisitFromChunkId ?? chunkId,chunkId);
                state.Completed = false;
                state.UpdatedAtUtc = Now;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    private async ValueTask<TextChunkWindowBackfillResult> RunCoreAsync(int size, CancellationToken ct)
    {
        var capturedState = await InitialisePageAsync(ct);
        if (capturedState.Completed) return new(0,0,true);
        await using var read = await factory.CreateDbContextAsync(ct);
        var inputs = await ReadInputsAsync(read,capturedState.LastChunkId,capturedState.EndChunkId,size,null,ct);
        var stale = inputs.Where(value => !value.ProofMatches).ToArray();
        var contents = stale.Length == 0 ? [] : await read.Database.SqlQuery<ChunkContent>($"""
            /* window-eligibility-chunk-content */
            SELECT c.Id AS TextChunkId, CASE WHEN DATALENGTH(c.Content)<=4096 THEN c.Content ELSE NULL END AS Content
            FROM TextChunks c WHERE c.Id IN (SELECT Id FROM OPENJSON({JsonSerializer.Serialize(stale.Select(value => value.TextChunkId))}) WITH(Id bigint '$'))
            """).ToArrayAsync(ct);
        var contentById = contents.ToDictionary(value => value.TextChunkId,value => value.Content);
        var bodies = new Dictionary<Guid,CanonicalBody>();
        var computed = new Dictionary<long,TextChunkWindowEligibilityEntity>();
        long retainedCharacters = 0;
        var processed = new List<ChunkInput>();
        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            if (!input.ProofMatches)
            {
                var available = false;
                if (input.Length is >= 1 and <= 2048 && input.StartOffset >= 0 && input.CanonicalLength <= int.MaxValue &&
                    input.StartOffset <= input.CanonicalLength && input.Length <= input.CanonicalLength-input.StartOffset &&
                    contentById.TryGetValue(input.TextChunkId,out var content) && content is not null)
                {
                    if (!bodies.TryGetValue(input.ArtifactId,out var body))
                    {
                        // Bound whole-text retention to 16 MiB; large documents use one bounded window instead.
                        if (bodies.Count >= 4 || retainedCharacters+Math.Min(input.CanonicalLength,4_000_000) > 8_000_000) break;
                        body = await read.Database.SqlQuery<CanonicalBody>($"""
                            /* window-eligibility-canonical-body */
                            SELECT Id,SourceRevision,ContentHash,WindowEligibilityVersion,
                                CONVERT(bigint,DATALENGTH(SearchText))/2 AS CanonicalLength,
                                CASE WHEN DATALENGTH(SearchText)<=8000000 THEN SearchText ELSE NULL END AS Text
                            FROM Artifacts WHERE Id={input.ArtifactId}
                            """).SingleOrDefaultAsync(ct);
                        if (body is null) { processed.Add(input); continue; }
                        bodies.Add(input.ArtifactId,body);
                        retainedCharacters += body.Text?.Length ?? 0;
                    }
                    if (!body.Matches(input)) { processed.Add(input); continue; }
                    if (body.Text is not null)
                        available = CanonicalWindowEligibility.Evaluate(body.Text,input.StartOffset,input.Length,content);
                    else
                    {
                        var origin = Math.Max(0,input.StartOffset-4096);
                        var window = await read.Database.SqlQuery<string>($"""
                            /* window-eligibility-canonical-window */
                            SELECT SUBSTRING(SearchText COLLATE Latin1_General_100_BIN2,{origin+1},16384) AS Value
                            FROM Artifacts WHERE Id={input.ArtifactId} AND SourceRevision={input.CanonicalSourceRevision}
                                AND ContentHash COLLATE Latin1_General_100_BIN2={input.CanonicalContentHash}
                                AND WindowEligibilityVersion={input.ArtifactVersion}
                            """).SingleOrDefaultAsync(ct);
                        if (window is null) { processed.Add(input); continue; }
                        available = CanonicalWindowEligibility.EvaluateWindow(window,origin,input.CanonicalLength,input.StartOffset,input.Length,content);
                    }
                }
                computed.Add(input.TextChunkId,input.Proof(available,ruleVersion,Now));
            }
            processed.Add(input);
        }
        await using var driver = await factory.CreateDbContextAsync(ct);
        return await driver.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);
            await SqlPublishedPassageSelection.AcquireFenceAsync(db,ct);
            var state = await ReadStateForUpdateAsync(db,ct) ?? throw new InvalidOperationException("window-backfill-state-missing");
            // A competing page commit cannot advance this capture again. Hint-only writes
            // merge without discarding still-current proofs; their revisit cursor is preserved.
            if (!state.RowVersion.AsSpan().SequenceEqual(capturedState.RowVersion) &&
                (state.RuleVersion!=capturedState.RuleVersion || state.LastChunkId!=capturedState.LastChunkId || state.EndChunkId!=capturedState.EndChunkId))
                return new TextChunkWindowBackfillResult(processed.Count,0,state.Completed);
            var ids = processed.Select(value => value.TextChunkId).ToArray();
            var current = ids.Length == 0 ? [] : await ReadInputsAsync(db,0,long.MaxValue,128,ids,ct);
            var currentById = current.ToDictionary(value => value.TextChunkId);
            var stored = await db.TextChunkWindowEligibilities.Where(value => ids.Contains(value.TextChunkId)).ToDictionaryAsync(value => value.TextChunkId,ct);
            var changedArtifacts = new HashSet<Guid>();
            var changed = 0;
            foreach (var input in processed)
            {
                if (!currentById.TryGetValue(input.TextChunkId,out var live)) continue; // Deleted chunks stay deleted.
                if (!live.SameBinding(input) || !input.ProofMatches && !computed.ContainsKey(input.TextChunkId))
                {
                    state.RevisitFromChunkId = Math.Min(state.RevisitFromChunkId ?? input.TextChunkId,input.TextChunkId);
                    continue;
                }
                if (!computed.TryGetValue(input.TextChunkId,out var proof) || live.ProofMatches) continue;
                if (stored.TryGetValue(input.TextChunkId,out var existing)) db.Entry(existing).CurrentValues.SetValues(proof);
                else db.TextChunkWindowEligibilities.Add(proof);
                changedArtifacts.Add(input.ArtifactId);
                changed++;
            }
            if (changedArtifacts.Count > 0)
                await db.Artifacts.Where(value => changedArtifacts.Contains(value.Id))
                    .ExecuteUpdateAsync(set => set.SetProperty(value => value.WindowEligibilityVersion,Guid.NewGuid()),ct);
            state.LastChunkId = processed.Count == inputs.Length && inputs.Length < size ? state.EndChunkId :
                processed.Count > 0 ? processed[^1].TextChunkId : state.LastChunkId;
            state.Completed = state.LastChunkId >= state.EndChunkId && state.RevisitFromChunkId is null;
            state.UpdatedAtUtc = Now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new TextChunkWindowBackfillResult(processed.Count,changed,state.Completed);
        });
    }

    private async Task<TextChunkWindowBackfillStateEntity> InitialisePageAsync(CancellationToken ct)
    {
        await using var driver = await factory.CreateDbContextAsync(ct);
        return await driver.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);
            await SqlPublishedPassageSelection.AcquireFenceAsync(db,ct);
            var state = await ReadStateForUpdateAsync(db,ct);
            if (state is null) { state = new() { Id=1,RuleVersion=ruleVersion }; db.TextChunkWindowBackfillStates.Add(state); }
            var last = await db.TextChunks.MaxAsync(value => (long?)value.Id,ct) ?? 0;
            if (state.Completed && state.RuleVersion == ruleVersion && state.RevisitFromChunkId is null && last <= state.LastChunkId)
            {
                await tx.CommitAsync(ct);
                return state;
            }
            if (state.RuleVersion != ruleVersion)
            {
                state.RuleVersion=ruleVersion;state.LastChunkId=0;state.Completed=false;state.RevisitFromChunkId=null;
            }
            if (state.RevisitFromChunkId is { } revisit)
            {
                state.LastChunkId=Math.Min(state.LastChunkId,revisit-1);state.RevisitFromChunkId=null;state.Completed=false;
            }
            state.EndChunkId=Math.Max(state.EndChunkId,last);
            if (last > state.LastChunkId) state.Completed=false;
            state.UpdatedAtUtc=Now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return state;
        });
    }

    private static Task<TextChunkWindowBackfillStateEntity?> ReadStateForUpdateAsync(FluxKnowledgeDbContext db,CancellationToken ct) =>
        db.TextChunkWindowBackfillStates.FromSqlRaw("SELECT * FROM TextChunkWindowBackfillStates WITH(UPDLOCK,HOLDLOCK) WHERE Id=1").SingleOrDefaultAsync(ct);

    internal static async Task InvalidateAsync(FluxKnowledgeDbContext db,IQueryable<Guid> artifactIds,CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Window invalidation requires the canonical mutation transaction.");
        await SqlPublishedPassageSelection.AcquireFenceAsync(db,ct);
        var first = await db.TextChunks.Where(value => artifactIds.Contains(value.ArtifactId)).MinAsync(value => (long?)value.Id,ct);
        await db.Artifacts.Where(value => artifactIds.Contains(value.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(value => value.WindowEligibilityVersion,Guid.NewGuid()),ct);
        await db.TextChunkWindowEligibilities.Where(value => artifactIds.Contains(value.ArtifactId)).ExecuteDeleteAsync(ct);
        if (first is { } id)
            await db.TextChunkWindowBackfillStates.Where(value => value.Id==1).ExecuteUpdateAsync(set => set
                .SetProperty(value => value.RevisitFromChunkId,value => value.RevisitFromChunkId==null || value.RevisitFromChunkId>id ? id : value.RevisitFromChunkId)
                .SetProperty(value => value.Completed,false),ct);
    }

    private Task<ChunkInput[]> ReadInputsAsync(FluxKnowledgeDbContext db,long after,long end,int count,long[]? ids,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (count is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(count));
        if (ids is not null)
        {
            if (ids.Length > 128 || ids.Any(id => id <= 0)) throw new ArgumentOutOfRangeException(nameof(ids));
            ids=ids.Distinct().ToArray();
            if (ids.Length == 0) return Task.FromResult<ChunkInput[]>([]);
        }
        FormattableString query=$"""
            /* window-eligibility-inputs: metadata only */
            SELECT TOP({count}) c.Id AS TextChunkId,c.ArtifactId,c.SourceRevision AS ChunkSourceRevision,c.ContentHash AS ChunkContentHash,
                c.StartOffset,c.Length,a.SourceRevision AS CanonicalSourceRevision,a.ContentHash AS CanonicalContentHash,
                CONVERT(bigint,DATALENGTH(a.SearchText))/2 AS CanonicalLength,a.WindowEligibilityVersion AS ArtifactVersion,
                CAST(CASE WHEN e.TextChunkId IS NOT NULL AND e.ArtifactId=c.ArtifactId AND e.CanonicalSourceRevision=a.SourceRevision
                    AND e.CanonicalContentHash COLLATE Latin1_General_100_BIN2=a.ContentHash COLLATE Latin1_General_100_BIN2
                    AND e.CanonicalLength=CONVERT(bigint,DATALENGTH(a.SearchText))/2 AND e.ChunkSourceRevision=c.SourceRevision
                    AND e.ChunkContentHash COLLATE Latin1_General_100_BIN2=c.ContentHash COLLATE Latin1_General_100_BIN2
                    AND e.StartOffset=c.StartOffset AND e.Length=c.Length AND e.RuleVersion={ruleVersion} THEN 1 ELSE 0 END AS bit) AS ProofMatches
            FROM TextChunks c JOIN Artifacts a ON a.Id=c.ArtifactId LEFT JOIN TextChunkWindowEligibilities e ON e.TextChunkId=c.Id
            WHERE /* input-selection */
                AND NOT EXISTS (
                    SELECT 1 FROM PipelineRecords record
                    LEFT JOIN SourceRevisions retained ON retained.Id=record.SourceRevisionId
                    LEFT JOIN SourceRootConfigurations retainedRoot ON retainedRoot.Id=retained.SourceRootId
                    LEFT JOIN DocumentPublications publication ON publication.PipelineRecordId=record.Id
                        AND publication.PipelineRecordRevision=record.Revision AND publication.DocumentInputSourceRevisionId=retained.Id
                    LEFT JOIN SourceRevisions owner ON owner.Id=publication.OwnerSourceRevisionId
                    LEFT JOIN SourceRootConfigurations ownerRoot ON ownerRoot.Id=owner.SourceRootId
                    WHERE record.Id=a.PipelineRecordId AND (retainedRoot.State=2 OR ownerRoot.State=2))
            ORDER BY c.Id
            """;
        // A range plan must never be reused for a sparse exact-ID recheck while
        // the publication fence is held. Keep their predicates and cache entries distinct.
        var arguments=query.GetArguments().ToList();
        var parameter=arguments.Count;
        string selection;
        if (ids is null)
        {
            selection="c.Id>{"+parameter+"} AND c.Id<={"+(parameter+1)+"}";
            arguments.Add(after);arguments.Add(end);
        }
        else
        {
            selection="c.Id IN (SELECT Id FROM OPENJSON({"+parameter+"}) WITH(Id bigint '$'))";
            arguments.Add(JsonSerializer.Serialize(ids));
        }
        return db.Database.SqlQuery<ChunkInput>(FormattableStringFactory.Create(
            query.Format.Replace("/* input-selection */",selection,StringComparison.Ordinal),arguments.ToArray())).ToArrayAsync(ct);
    }

    private sealed class ChunkContent
    {
        public long TextChunkId { get; init; }
        public string? Content { get; init; }
    }

    private sealed class CanonicalBody
    {
        public Guid Id { get; init; }
        public long SourceRevision { get; init; }
        public string ContentHash { get; init; } = string.Empty;
        public Guid WindowEligibilityVersion { get; init; }
        public long CanonicalLength { get; init; }
        public string? Text { get; init; }
        internal bool Matches(ChunkInput input) => Id==input.ArtifactId && SourceRevision==input.CanonicalSourceRevision &&
            ContentHash==input.CanonicalContentHash && CanonicalLength==input.CanonicalLength && WindowEligibilityVersion==input.ArtifactVersion;
    }

    private sealed class ChunkInput
    {
        public long TextChunkId { get; init; }
        public Guid ArtifactId { get; init; }
        public long CanonicalSourceRevision { get; init; }
        public string CanonicalContentHash { get; init; } = string.Empty;
        public long CanonicalLength { get; init; }
        public Guid ArtifactVersion { get; init; }
        public long ChunkSourceRevision { get; init; }
        public string ChunkContentHash { get; init; } = string.Empty;
        public int StartOffset { get; init; }
        public int Length { get; init; }
        public bool ProofMatches { get; init; }
        internal bool SameBinding(ChunkInput other) => TextChunkId==other.TextChunkId && ArtifactId==other.ArtifactId &&
            CanonicalSourceRevision==other.CanonicalSourceRevision && CanonicalContentHash==other.CanonicalContentHash &&
            CanonicalLength==other.CanonicalLength && ArtifactVersion==other.ArtifactVersion &&
            ChunkSourceRevision==other.ChunkSourceRevision && ChunkContentHash==other.ChunkContentHash && StartOffset==other.StartOffset && Length==other.Length;
        internal TextChunkWindowEligibilityEntity Proof(bool available,string policy,DateTimeOffset now) => new()
        {
            TextChunkId=TextChunkId,ArtifactId=ArtifactId,CanonicalSourceRevision=CanonicalSourceRevision,CanonicalContentHash=CanonicalContentHash,
            CanonicalLength=CanonicalLength,ChunkSourceRevision=ChunkSourceRevision,ChunkContentHash=ChunkContentHash,StartOffset=StartOffset,
            Length=Length,RuleVersion=policy,Available=available,CalculatedAtUtc=now
        };
    }
}
