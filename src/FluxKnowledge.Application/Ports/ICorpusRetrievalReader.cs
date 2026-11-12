using FluxKnowledge.Application.Visibility;

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
    int OriginKind = 0,
    Guid CorpusEpoch = default,
    string ContextHeader = "",
    string PassagePolicyFingerprint = "",
    string SearchInputHash = "",
    CodeDisclosureWindow? DisclosureProof = null);

public sealed record EligibleContext(
    EligiblePassageCandidate Candidate,
    int StartOffset,
    string Text,
    bool ContextBounded,
    string? DocumentMetadataJson,
    string? DisclosureText,
    CodeDisclosureWindow? TextProof = null,
    CodeDisclosureWindow? GuardProof = null);

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

    /// <summary>
    /// Bounded preflight of complete candidate bindings. Citation metadata may be
    /// omitted; callers must apply disclosure and still perform final fresh reads.
    /// </summary>
    ValueTask<IReadOnlyDictionary<long, EligibleContext>> ReadForDisclosureAsync(
        IReadOnlyList<CorpusEvidenceBinding> bindings, CancellationToken cancellationToken) =>
        ReadDisclosureBatchAsync(bindings, 200, cancellationToken);

    /// <summary>
    /// Explicit expanded-candidate preflight: at most 200 baseline bindings and
    /// 50 additional representatives. Independent disclosure and final reads still apply.
    /// </summary>
    ValueTask<IReadOnlyDictionary<long, EligibleContext>> ReadExpandedForDisclosureAsync(
        IReadOnlyList<CorpusEvidenceBinding> bindings, CancellationToken cancellationToken) =>
        ReadDisclosureBatchAsync(bindings, 250, cancellationToken);

    private async ValueTask<IReadOnlyDictionary<long, EligibleContext>> ReadDisclosureBatchAsync(
        IReadOnlyList<CorpusEvidenceBinding> bindings, int maximumBindings, CancellationToken cancellationToken)
    {
        if (bindings.Count > maximumBindings || bindings.Select(binding => binding.ChunkId).Distinct().Count() != bindings.Count)
            throw new ArgumentOutOfRangeException(nameof(bindings));
        var result = new Dictionary<long, EligibleContext>();
        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await ReadAsync(binding, 0, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (current is not null) result.Add(binding.ChunkId, current);
        }
        return result;
    }

    /// <summary>Revalidates the returned bindings at one publication point where supported.</summary>
    async ValueTask<IReadOnlySet<long>> ValidateCurrentBindingsAsync(
        IReadOnlyList<CorpusEvidenceBinding> bindings, CancellationToken cancellationToken)
    {
        var valid = new HashSet<long>();
        foreach (var binding in bindings)
            if (await ReadAsync(binding, 0, cancellationToken).ConfigureAwait(false) is not null) valid.Add(binding.ChunkId);
        return valid;
    }
}
