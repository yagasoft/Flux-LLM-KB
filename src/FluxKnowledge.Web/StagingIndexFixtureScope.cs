namespace FluxKnowledge.Web;

internal sealed record StagingFixtureRoot(Guid Id,int State,string Version);
internal sealed record StagingFixtureDeletion(Guid Id,Guid RootId,int State,DateTimeOffset? LeaseExpiresUtc,string Version);

/// <summary>Private closed-fixture witness. It never grants a native claim or replaces native cleanup eligibility.</summary>
internal sealed class StagingIndexFixtureScope
{
    private readonly StagingFixtureRoot[] _roots;
    private readonly StagingFixtureDeletion[] _deletions;

    internal StagingIndexFixtureScope(IReadOnlyList<StagingFixtureRoot> roots,IReadOnlyList<StagingFixtureDeletion> deletions)
    {
        CheckShape(roots,deletions);
        if(roots.Any(r=>r.State!=1) || deletions.Any(d=>d.State is not (2 or 3))) Refuse();
        _roots=roots.OrderBy(r=>r.Id).ToArray();
        _deletions=deletions.OrderBy(d=>d.Id).ToArray();
    }

    internal void Validate(IReadOnlyList<StagingFixtureRoot> roots,IReadOnlyList<StagingFixtureDeletion> deletions,
        Guid? ownRoot,Guid? ownOperation,bool exclusiveWriterWitness)
    {
        CheckShape(roots,deletions);
        if(!exclusiveWriterWitness || ownRoot==Guid.Empty || ownOperation==Guid.Empty || ownOperation.HasValue && !ownRoot.HasValue) Refuse();
        var foreignRoots=roots.Where(r=>r.Id!=ownRoot).OrderBy(r=>r.Id).ToArray();
        var foreignDeletions=deletions.Where(d=>d.Id!=ownOperation).OrderBy(d=>d.Id).ToArray();
        // A running foreign lease can become claimable by time alone. No such row is admitted,
        // even if it is currently unexpired and there are no concurrent writers.
        if(foreignRoots.Any(r=>r.State!=1) || foreignDeletions.Any(d=>d.State is not (2 or 3)) ||
            !foreignRoots.SequenceEqual(_roots) || !foreignDeletions.SequenceEqual(_deletions)) Refuse();
        if(ownOperation.HasValue && deletions.Any(d=>d.Id==ownOperation && d.RootId!=ownRoot)) Refuse();
    }

    internal bool CanClaimDeletion(IReadOnlyList<StagingFixtureRoot> roots,IReadOnlyList<StagingFixtureDeletion> deletions,
        Guid ownRoot,Guid ownOperation,DateTimeOffset now,bool exclusiveWriterWitness)
    {
        Validate(roots,deletions,ownRoot,ownOperation,exclusiveWriterWitness);
        var owned=deletions.SingleOrDefault(d=>d.Id==ownOperation);
        // Returning no work must never fall through to the global native claimant.
        return owned is not null && (owned.State==0 || owned.State==1 && (owned.LeaseExpiresUtc is null || owned.LeaseExpiresUtc<=now));
    }

    private static void CheckShape(IReadOnlyList<StagingFixtureRoot> roots,IReadOnlyList<StagingFixtureDeletion> deletions)
    {
        if(roots.Count>64 || deletions.Count>64 || roots.Select(r=>r.Id).Distinct().Count()!=roots.Count ||
            deletions.Select(d=>d.Id).Distinct().Count()!=deletions.Count ||
            roots.Any(r=>r.Id==Guid.Empty || string.IsNullOrEmpty(r.Version)) ||
            deletions.Any(d=>d.Id==Guid.Empty || d.RootId==Guid.Empty || string.IsNullOrEmpty(d.Version))) Refuse();
    }
    private static void Refuse()=>throw new InvalidOperationException("staging-fixture-exclusivity-unproved");
}
