namespace FluxKnowledge.Domain.Knowledge;

/// <summary>A typed directed graph edge deterministically derived from a canonical claim.</summary>
public sealed record KnowledgeRelation(Guid ClaimId, string Subject, string Predicate, string ObjectText)
{
    public static KnowledgeRelation FromClaim(KnowledgeClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return new KnowledgeRelation(claim.Id, claim.Subject, claim.Predicate, claim.ObjectText);
    }
}
