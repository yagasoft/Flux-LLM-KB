namespace FluxKnowledge.Application.Ports;

public sealed record ResolvedCorpusScope(
    string Kind,
    IReadOnlyList<Guid> RootIds,
    string? CanonicalCwd);

public sealed record CorpusLexicalReadiness(bool IndexPresent, bool PopulationComplete);

public sealed record EligiblePassageCandidate(
    Guid? RootId,
    Guid? OwnerSourceRevisionId,
    Guid PipelineRecordId,
    long PipelineRecordRevision,
    Guid ArtifactId,
    string ArtifactHash,
    long ChunkId,
    string ChunkHash,
    int StartOffset,
    int Length,
    string Content,
    string SourceIdentity,
    int FullTextRank = 0,
    int OriginKind = 0);

public sealed record EligibleContext(
    EligiblePassageCandidate Candidate,
    int StartOffset,
    string Text,
    bool ContextBounded,
    string? DocumentMetadataJson,
    string? DisclosureText);

public interface ICorpusRetrievalReader
{
    ValueTask<CorpusLexicalReadiness> GetLexicalReadinessAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<string>> GetLexicalTermsAsync(string query, CancellationToken cancellationToken);
    ValueTask<ResolvedCorpusScope?> ResolveScopeAsync(
        string kind, Guid? rootId, string? cwd, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<EligiblePassageCandidate>> SearchAsync(
        string query, ResolvedCorpusScope scope, int limit, CancellationToken cancellationToken);

    ValueTask<EligibleContext?> ReadAsync(
        CorpusEvidenceBinding binding, int contextCharacters, CancellationToken cancellationToken);
}
