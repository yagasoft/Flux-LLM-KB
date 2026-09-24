using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;

namespace FluxKnowledge.Application.Search;

public interface ICorpusRetrievalService
{
    ValueTask<CorpusSearchResponse> SearchAsync(CorpusSearchRequest request, CancellationToken token);
    ValueTask<CorpusPassageResponse> ReadAsync(CorpusReadRequest request, CancellationToken token);
}

public sealed class CorpusRetrievalService(
    ICorpusRetrievalReader reader,
    ICorpusEvidenceCodec evidenceCodec,
    ILocalPrivateContentDisclosure disclosure) : ICorpusRetrievalService
{
    private static readonly CompareInfo EnglishCompare = CultureInfo.GetCultureInfo("en-US").CompareInfo;
    private const CompareOptions FullTextCompareOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public async ValueTask<CorpusSearchResponse> SearchAsync(
        CorpusSearchRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.Query?.Trim().Normalize(NormalizationForm.FormC);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2048)
            throw new NativeOperationException("invalid-query");
        if (request.Limit is < 1 or > 20)
            throw new NativeOperationException("invalid-limit");
        if (request.Scope is not ("all" or "root" or "workspace") ||
            request.Scope == "all" && (request.RootId.HasValue || request.Cwd is not null) ||
            request.Scope == "root" && (!request.RootId.HasValue || request.Cwd is not null) ||
            request.Scope == "workspace" && (request.RootId.HasValue || string.IsNullOrWhiteSpace(request.Cwd)))
            throw new NativeOperationException("invalid-request");

        var scope = await reader.ResolveScopeAsync(request.Scope, request.RootId, request.Cwd, token)
            .ConfigureAwait(false) ?? throw new NativeOperationException("scope-unavailable");
        var readiness = await reader.GetLexicalReadinessAsync(token).ConfigureAwait(false);
        if (!readiness.IndexPresent) throw new NativeOperationException("lexical-unavailable");
        var candidates = await reader.SearchAsync(query, scope, request.Limit, token).ConfigureAwait(false);
        var lexicalTerms = candidates.Any(static candidate => candidate.FullTextRank > 0)
            ? await reader.GetLexicalTermsAsync(query, token).ConfigureAwait(false)
            : [];
        var hits = new List<CorpusSearchHit>(request.Limit);
        var passagesPerDocument = new Dictionary<(Guid? Owner, Guid UnrootedRecord), int>();
        foreach (var candidate in candidates)
        {
            if (hits.Count == request.Limit) break;
            var documentKey = candidate.OwnerSourceRevisionId.HasValue
                ? (candidate.OwnerSourceRevisionId, Guid.Empty)
                : ((Guid?)null, candidate.PipelineRecordId);
            if (passagesPerDocument.GetValueOrDefault(documentKey) >= 2) continue;
            if (candidate.Content.Length != candidate.Length || candidate.Length is < 1 or > 2048 ||
                candidate.StartOffset < 0 ||
                !string.Equals(Hash(candidate.Content), candidate.ChunkHash, StringComparison.Ordinal))
                continue;

            var match = candidate.Content.IndexOf(query, StringComparison.Ordinal);
            var lexicalAnchor = match >= 0 ? match : FindLexicalAnchor(candidate.Content, lexicalTerms);
            if (lexicalAnchor < 0 || match < 0 && candidate.FullTextRank <= 0) continue;
            var localStart = match >= 0
                ? match
                : Math.Min(lexicalAnchor, Math.Max(0, candidate.Length - 1024));
            if (localStart > 0 && char.IsLowSurrogate(candidate.Content[localStart])) localStart--;
            var localLength = Math.Min(1024, candidate.Length - localStart);
            if (localStart + localLength < candidate.Length &&
                char.IsHighSurrogate(candidate.Content[localStart + localLength - 1])) localLength--;
            var passage = candidate.Content.Substring(localStart, localLength);
            var source = disclosure.Evaluate(candidate.SourceIdentity, LocalDisclosureKind.CorpusMetadata);
            var title = disclosure.Evaluate(Path.GetFileName(candidate.SourceIdentity), LocalDisclosureKind.CorpusMetadata);
            var text = disclosure.Evaluate(passage, LocalDisclosureKind.RetainedDetail);
            if (source.Withheld || title.Withheld || text.Withheld) continue;

            var binding = new CorpusEvidenceBinding(
                1, candidate.RootId, candidate.OwnerSourceRevisionId,
                Hash(candidate.SourceIdentity), candidate.PipelineRecordId,
                candidate.PipelineRecordRevision, candidate.ArtifactId,
                candidate.ArtifactHash, candidate.ChunkId, candidate.ChunkHash,
                candidate.StartOffset + localStart, localLength);
            var current = await reader.ReadAsync(binding, 0, token).ConfigureAwait(false);
            if (current is null) continue;
            if (current.DisclosureText is null ||
                disclosure.Evaluate(current.DisclosureText, LocalDisclosureKind.RetainedDetail).Withheld ||
                !NativeV1EnvelopeProtector.CanDiscloseResult(JsonSerializer.SerializeToElement(current.DisclosureText)))
                continue;
            var citation = CorpusCitationMapper.Map(current.DocumentMetadataJson,
                binding.CitedStart, binding.CitedLength, candidate.SourceIdentity);
            var hit = new CorpusSearchHit(
                evidenceCodec.Encode(binding), source.Value!, candidate.RootId,
                candidate.OwnerSourceRevisionId, candidate.PipelineRecordId,
                candidate.PipelineRecordRevision, title.Value!, candidate.ChunkId,
                candidate.ChunkHash, binding.CitedStart, localLength, text.Value!,
                citation.Locations, candidate.OriginKind == 3 ? "metadata" : citation.ExtractionMethod,
                [match >= 0 ? "exact:ordinal" : "lexical:full-text",
                 ..(match >= 0 && query.Length > localLength ? new[] { "passage-bounded" } : []),
                 ..citation.Warnings]);
            if (!NativeV1EnvelopeProtector.CanDiscloseResult(JsonSerializer.SerializeToElement(hit))) continue;
            hits.Add(hit);
            passagesPerDocument[documentKey] = passagesPerDocument.GetValueOrDefault(documentKey) + 1;
        }

        var warnings = new List<string>();
        if (!readiness.PopulationComplete) warnings.Add("full-text-populating");
        if (candidates.Count >= 200) warnings.Add("candidate-budget-reached");
        if (lexicalTerms.Count >= 512) warnings.Add("lexical-term-budget-reached");
        var response = new CorpusSearchResponse(hits,
            new CorpusResolvedScope(scope.Kind, scope.RootIds, scope.CanonicalCwd),
            "lexical", "not-enabled", null, warnings);
        if (!NativeV1EnvelopeProtector.CanDiscloseResult(JsonSerializer.SerializeToElement(response)))
            throw new NativeOperationException("content-withheld");
        return response;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static int FindLexicalAnchor(string content, IReadOnlyList<string> terms)
    {
        var best = -1;
        foreach (var term in terms)
        {
            if (term.Length == 0) continue;
            var searchFrom = 0;
            while (searchFrom < content.Length)
            {
                var index = EnglishCompare.IndexOf(content, term, searchFrom, FullTextCompareOptions);
                if (index < 0) break;
                var wordStart = index;
                while (wordStart > 0 && IsWordPart(content[wordStart - 1])) wordStart--;
                var wordEnd = index;
                while (wordEnd < content.Length && IsWordPart(content[wordEnd])) wordEnd++;
                if (EnglishCompare.Compare(content.AsSpan(wordStart, wordEnd - wordStart),
                        term.AsSpan(), FullTextCompareOptions) == 0 &&
                    (best < 0 || wordStart < best)) best = wordStart;
                searchFrom = index + 1;
            }
        }
        return best;
    }

    private static bool IsWordPart(char character) => char.IsLetterOrDigit(character) ||
        CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    public async ValueTask<CorpusPassageResponse> ReadAsync(CorpusReadRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContextCharacters is < 0 or > 4096)
            throw new NativeOperationException("invalid-request");
        var binding = evidenceCodec.Decode(request.EvidenceRef);
        var context = await reader.ReadAsync(binding, request.ContextCharacters, token).ConfigureAwait(false)
            ?? throw new NativeOperationException("evidence-stale");
        var candidate = context.Candidate;
        var citedLocalStart = binding.CitedStart - candidate.StartOffset;
        if (candidate.Length != candidate.Content.Length || candidate.Length is < 1 or > 2048 ||
            !string.Equals(Hash(candidate.Content), binding.ChunkHash, StringComparison.Ordinal) ||
            citedLocalStart < 0 || citedLocalStart + binding.CitedLength > candidate.Length ||
            binding.CitedStart < context.StartOffset ||
            binding.CitedStart + binding.CitedLength > context.StartOffset + context.Text.Length ||
            !string.Equals(candidate.Content.Substring(citedLocalStart, binding.CitedLength),
                context.Text.Substring(binding.CitedStart - context.StartOffset, binding.CitedLength),
                StringComparison.Ordinal))
            throw new NativeOperationException("evidence-stale");

        var source = disclosure.Evaluate(candidate.SourceIdentity, LocalDisclosureKind.CorpusMetadata);
        var title = disclosure.Evaluate(Path.GetFileName(candidate.SourceIdentity), LocalDisclosureKind.CorpusMetadata);
        var text = disclosure.Evaluate(context.Text, LocalDisclosureKind.RetainedDetail);
        var surroundingWithheld = context.DisclosureText is null ||
            disclosure.Evaluate(context.DisclosureText, LocalDisclosureKind.RetainedDetail).Withheld ||
            !NativeV1EnvelopeProtector.CanDiscloseResult(JsonSerializer.SerializeToElement(context.DisclosureText));
        if (source.Withheld || title.Withheld || text.Withheld || surroundingWithheld)
            throw new NativeOperationException("content-withheld");
        var citation = CorpusCitationMapper.Map(context.DocumentMetadataJson,
            context.StartOffset, context.Text.Length, candidate.SourceIdentity);
        var response = new CorpusPassageResponse(
            request.EvidenceRef, source.Value!, candidate.RootId,
            candidate.OwnerSourceRevisionId, candidate.PipelineRecordId,
            candidate.PipelineRecordRevision, title.Value!, candidate.ChunkId,
            candidate.ChunkHash, binding.CitedStart, binding.CitedLength,
            context.StartOffset, context.Text.Length, text.Value!,
            context.ContextBounded, citation.Locations,
            candidate.OriginKind == 3 ? "metadata" : citation.ExtractionMethod,
            citation.Warnings);
        if (!NativeV1EnvelopeProtector.CanDiscloseResult(JsonSerializer.SerializeToElement(response)))
            throw new NativeOperationException("content-withheld");
        return response;
    }
}
