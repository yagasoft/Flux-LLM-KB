namespace FluxKnowledge.Domain.Knowledge;

/// <summary>A canonical subject-predicate-object assertion with immutable lifecycle revisions.</summary>
public sealed record KnowledgeClaim(
    Guid Id,
    string Subject,
    string Predicate,
    string ObjectText,
    string CanonicalIdentity,
    decimal Confidence,
    int Revision,
    string LifecycleState,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ForgottenAtUtc = null)
{
    public bool IsActive => ForgottenAtUtc is null && LifecycleState != "retracted";

    public static KnowledgeClaim Create(
        string subject,
        string predicate,
        string objectText,
        decimal confidence,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        var canonicalSubject = KnowledgeText.Canonicalise(subject, "invalid-claim-subject", 512);
        var canonicalPredicate = KnowledgeText.Canonicalise(predicate, "invalid-claim-predicate", 128);
        var canonicalObject = KnowledgeText.Canonicalise(objectText, "invalid-claim-object", 2048);
        if (confidence is < 0m or > 1m)
        {
            throw new KnowledgeDomainException("invalid-claim-confidence");
        }

        var timestamp = now ?? DateTimeOffset.UtcNow;
        return new KnowledgeClaim(
            id ?? Guid.NewGuid(), canonicalSubject, canonicalPredicate, canonicalObject,
            $"{canonicalSubject}\u001f{canonicalPredicate}\u001f{canonicalObject}", confidence, 1, "active", timestamp, timestamp);
    }

    public KnowledgeClaim Revise(decimal confidence, DateTimeOffset updatedAtUtc)
    {
        if (confidence is < 0m or > 1m)
        {
            throw new KnowledgeDomainException("invalid-claim-confidence");
        }

        return this with { Confidence = confidence, Revision = Revision + 1, UpdatedAtUtc = updatedAtUtc };
    }

    public KnowledgeClaim Transition(string transition, DateTimeOffset updatedAtUtc)
    {
        var state = KnowledgeText.Canonicalise(transition, "invalid-claim-transition", 64);
        if (state is not ("active" or "superseded" or "retracted"))
        {
            throw new KnowledgeDomainException("invalid-claim-transition");
        }

        return this with { LifecycleState = state, Revision = Revision + 1, UpdatedAtUtc = updatedAtUtc };
    }

    public KnowledgeClaim Forget(DateTimeOffset forgottenAtUtc) => this with { ForgottenAtUtc = forgottenAtUtc, UpdatedAtUtc = forgottenAtUtc };
}
