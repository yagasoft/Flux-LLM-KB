namespace FluxKnowledge.Domain.Knowledge;

/// <summary>A locally authored knowledge note whose active content is safe for retained projection.</summary>
public sealed record KnowledgeItem(
    Guid Id,
    string Title,
    string Body,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ForgottenAtUtc = null)
{
    public bool IsActive => ForgottenAtUtc is null;

    public static KnowledgeItem Create(string title, string body, DateTimeOffset createdAtUtc, Guid? id = null)
    {
        var canonicalTitle = KnowledgeText.Canonicalise(title, "invalid-knowledge-title", 256);
        var canonicalBody = KnowledgeText.Canonicalise(body, "invalid-knowledge-body", 16 * 1024, preserveCase: true);
        return new KnowledgeItem(id ?? Guid.NewGuid(), canonicalTitle, canonicalBody, createdAtUtc);
    }

    public KnowledgeItem Forget(DateTimeOffset forgottenAtUtc) => this with { ForgottenAtUtc = forgottenAtUtc };
}

internal static class KnowledgeText
{
    public static string Canonicalise(string value, string reasonCode, int maximumLength, bool preserveCase = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new KnowledgeDomainException(reasonCode);
        }

        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length > maximumLength)
        {
            throw new KnowledgeDomainException(reasonCode);
        }

        return preserveCase ? compact : compact.ToLowerInvariant();
    }
}

public sealed class KnowledgeDomainException(string reasonCode) : InvalidOperationException(reasonCode)
{
    public string ReasonCode { get; } = reasonCode;
}
