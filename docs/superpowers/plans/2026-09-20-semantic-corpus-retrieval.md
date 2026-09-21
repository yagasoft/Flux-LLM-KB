# Semantic corpus retrieval implementation plan

Date: 2026-09-20
Status: design-stage plan with explicit model and migration gates; no implementation or activation performed.

**Goal:** Improve paraphrase retrieval across ordinary and OCR-derived text using one verified local embedding model, without losing exact retrieval, citation correctness or service continuity.

**Architecture:** Extend the verified corpus retrieval contract with optional semantic candidates. Bind query embedding to one immutable model/index generation, embed retained canonical chunks through durable model-specific work, and switch profiles only after a complete current candidate generation is validated. Lexical search remains independently usable.

**Tech stack:** Existing .NET, SQL Server, USearch, local model store and scheduler; model-specific runtime selection is an explicit first checkpoint, not an assumed dependency.

**Spec:** [Scoped corpus retrieval and semantic search](../specs/2026-09-20-corpus-retrieval-design.md).

**Prerequisite:** [Scoped corpus search/read](2026-09-20-scoped-corpus-retrieval.md), verified against merged OCR publication/provenance contracts.

**Execution:** One implementation owner. Obtain independent high-risk review of the concrete profile/schema/transaction design before its implementation, and review the final whole change. These are technical gates; they do not authorise production actions.

## Global constraints

- No new OCR/extraction or source-original reads to build embeddings; consume eligible retained canonical chunks for all supported text inputs.
- Canonical model payloads stay under `J:\Models`; exact identities, held leases and zero implicit downloads are mandatory. No model acquisition is authorised here.
- Query/document prompts, tokenizer, pooling, normalisation, metric and dimensions form the profile fingerprint. Equal dimensions are not compatibility. Admit only cosine (`cos`) profiles in this increment.
- Existing SQL Full-Text and exact evidence ranking remain available. Missing/busy semantic capability yields explicit lexical degradation.
- Never mutate completed extraction jobs, admission receipts or old immutable membership to enable a new model.
- Preserve deletion, suppression, current publication and other-source ownership under concurrency and recovery.
- Do not merge/activate this plan before the incoming OCR task and the first retrieval increment are complete.

## Review focus

1. A query embedded immediately before a profile switch must not search the new incompatible index.
2. A source replaced/deleted during re-embedding must not reappear through late results, activation or rollback.
3. An old-profile normal Publish job must not regress the active pointer or strand a document after cutover.
4. Recovery and deletion survivor rebuilds must not enumerate mixed profiles or erase current eligible content.
5. OCR occupying GPU capacity, an unavailable J: drive or provider refusal must leave honest lexical behaviour and zero downloads.

## Batch 1: select a bounded local embedding contract

**Deliverable:** An exact, evidence-backed model/runtime binding decision, or a concrete cache/runtime approval blocker. This decision is required before implementing a provider; model names and version numbers are deliberately not invented in this plan.

**Inspect:** `src/FluxKnowledge.Application/Ports/IEmbeddingProvider.cs`, `src/FluxKnowledge.Infrastructure.Inference/DeterministicTokenHashEmbeddingProvider.cs`, `src/FluxKnowledge.Application/Models/` (locate the final `ILocalModelStore` and lease contracts), final OCR scheduler/runtime composition and the existing central inventory. Inspect available metadata without loading providers or initiating acquisition.

- [ ] Recheck repository model rules, central availability/containment and inventory, plus relevant configured legacy caches. Record exact reusable artifacts and unverified/missing dependencies privately. Do not copy/adopt or hash/load payloads outside the applicable verification procedure.
- [ ] Select at most two plausible already provisioned complete embedding routes for a bounded comparison; prefer one supported route compatible with the approved runtime. If no route is locally complete, present exact missing artifacts/revisions/bytes/destination or runtime change and stop that dependent work. Continue model-free contract/migration design where safe.
- [ ] For the selected route, record immutable weights/tokenizer identity, versioned query/document prefixes, pooling, normalisation, dimensions, metric, token limit, execution provider and runtime. Check current primary documentation if selecting a new library/model; do not infer support from an old cache filename.
- [ ] Define long-chunk handling before inference: preserve all canonical text through bounded model-token windows and a documented normalised pooling rule, fingerprinted with the profile. No silent provider truncation. If multiple subchunk vectors are actually needed, amend the schema/retrieval design before implementation rather than quietly changing vector identity.
- [ ] Measure ordinary text and OCR samples against the frozen question set without rewriting expectations. Record whether semantic relevance is demonstrated, peak CPU/GPU memory, warm/cold latency and OCR contention. Follow separate authority for any real-model execution/runtime activation.
- [ ] Retain `docs/operations/<execution-date>-embedding-selection.md` with only public model identities, methods, aggregate results and the chosen profile; keep private queries/text/results outside Git. Record a named refusal if no candidate meets the gate.

**Acceptance:** One complete route fits the selected resource budget and offline controls, or no provider is enabled. The spec's R10 evaluation decides activation; successful inference alone does not.

## Batch 2: generation-bound semantic query and scope correctness

**Deliverable:** With deterministic fake providers and disposable indexes, a query cannot cross embedding spaces during concurrent generation changes. Real provider activation is separate from this invariant proof.

**Existing files:**

- `src/FluxKnowledge.Application/Ports/IEmbeddingProvider.cs`, `IAnnIndex.cs`, `IIndexGenerationStore.cs`.
- `src/FluxKnowledge.Infrastructure.Usearch/Search/UsearchNearestNeighbourQuery.cs`, `UsearchAnnIndex.cs`, `UsearchGenerationValidator.cs`, `DerivedIndexRecoveryCoordinator.cs`.
- `src/FluxKnowledge.Application/Search/HybridSearchService.cs` and the first increment's `CorpusRetrievalService.cs`.
- `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlPipelineStore.cs`.

**New files:** `src/FluxKnowledge.Application/Ports/ISearchGenerationLease.cs`, `src/FluxKnowledge.Application/Search/EmbeddingProfile.cs`, and provider-specific adapter files only after Batch 1 identifies the runtime.

**Interfaces:**

```csharp
public sealed record EmbeddingProfile(
    string Fingerprint, int Dimensions, string Metric, string InputContractFingerprint);
public interface ISearchGenerationLease : IAsyncDisposable
{
    Guid GenerationId { get; }
    EmbeddingProfile Profile { get; }
    ValueTask<IReadOnlyList<float>> EmbedQueryAsync(string query, CancellationToken token);
    ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> vector, int limit, CancellationToken token);
}
```

The lease factory resolves SQL profile/generation together and holds the corresponding provider/index references. It exposes no setter for a mutable active generation. Corpus scoped ranking consumes vectors from this same captured generation, never a fresh global pointer. Preserve an internal compatibility wrapper for existing callers while routing them through the binding.

- [ ] Add failing tests that switch profiles between query embedding and ANN search, including different fingerprints with equal dimensions. Old request uses old binding or safely degrades; it never queries the new space with the old vector.
- [ ] Implement explicit generation acquisition, reference-counted handle lifetime and immutable provider binding. Do not hold `ReaderWriterLockSlim` across awaits or inference. Keep lifecycle filtering at final hydration.
- [ ] Rank the complete scoped vector set up to 10,000 eligible vectors; beyond that return lexical with `scope-capacity-exceeded`. Test a correct match ranked below the global top-k by unrelated roots. No fixed oversampling/postfilter shortcut.
- [ ] Implement profile-specific offline adapter and held leases only after the model gate. Prove cache hit/miss/unavailable-drive/concurrent requests cause zero implicit transfers, detect nonfinite/dimension-mismatched results and preserve cancellation ownership.
- [ ] Use existing interactive/background scheduler lanes for GPU work. Test a query deadline while OCR owns capacity: return lexical degradation without starting unadmitted inference or releasing OCR resources. Reuse the admitted-run completion pattern from merged OCR; a request timeout is not execution termination.

**Tests:** new `tests/FluxKnowledge.Integration.Tests/Search/SearchGenerationBindingTests.cs`, `ScopedSemanticRetrievalTests.cs`; extend `tests/FluxKnowledge.Integration.Tests/Indexing/UsearchGenerationTests.cs`, model-gate tests and GPU scheduler tests. Domain fake-provider tests cover profile equality, window coverage and vector validation.

```csharp
// A and B intentionally have equal dimensions but different fingerprints.
await using var lease = await factory.AcquireAsync(token);
var vector = await lease.EmbedQueryAsync("recover a failed import", token);
await fixture.ActivateProfileBAsync(token);
var matches = await lease.SearchAsync(vector, 5, token);
Assert.Equal(profileA.Fingerprint, lease.Profile.Fingerprint);
Assert.All(matches, match => Assert.Contains(match.VectorId, profileAVectorIds));
```

`factory` and `fixture` in these examples are test setup helpers to be added alongside the tests, not claimed existing production APIs.

## Batch 3: current-corpus re-embedding, publication and rollback

**Deliverable:** A complete replacement profile can be built while the old one serves, activated against current SQL truth, and recovered or rolled back without losing current documents or reviving deleted content.

**Review before implementation:** finalise a short schema/transaction amendment using merged code. It must identify exact locks/row versions, current-publication set definition, stale-work continuation and interaction with source deletion. An independent reviewer must approve that concrete design before schema/worker changes. Do not use this generic plan as proof those details are already solved.

**Existing files:** `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/VectorEntity.cs`, `IndexStateEntity.cs`, `IndexGenerationEntity.cs`, `IndexGenerationVectorEntity.cs`, `Configurations/CanonicalSchemaConfigurations.cs`, `SqlPipelineStore.cs`, `SqlStageTransitionStore.cs`, `SqlSourceDeletionStore.cs`, `SqlDerivedIndexRecoveryStore.cs`, `src/FluxKnowledge.Infrastructure.Usearch/UsearchGenerationBuilder.cs`, `src/FluxKnowledge.Application/Indexing/EmbedStageWorker.cs`, `PublishStageWorker.cs` and runtime composition. Add EF-generated migrations; never hand-edit the snapshot alone.

**New responsibilities:** a SQL-durable embedding-upgrade operation, bounded upgrade worker and profile-specific publication continuation. Provisional files are `Persistence/Entities/EmbeddingUpgradeEntity.cs`, `Persistence/SqlEmbeddingUpgradeStore.cs`, `Application/Indexing/EmbeddingUpgradeWorker.cs` and `Application/Ports/IEmbeddingUpgradeStore.cs`. Exact schema is the reviewed amendment's output; do not create a general workflow/provider-management subsystem.

**Operation contract:** source profile, target profile, captured source-set digest, candidate generation, state, resumable progress, failure reason and fencing token. The logical unit of work is `(canonical chunk ID, content hash, source revision, target profile)`; repeat success reuses identical results, conflicting payloads refuse. Candidate/profile selection must cover builder, activation, normal Publish, deletion survivor rebuild and crash recovery together.

- [ ] Add failing generated-SQL tests for simultaneous upgrade requests, duplicate/out-of-order completion, source replacement/deletion during embedding, stale candidate activation, crash before/after pointer change, and ordinary publication during catch-up.
- [ ] Implement additive upgrade state and resumable bounded batches. Store immutable vectors in existing canonical storage; never replay Extract/OCR. Use current lifecycle fences at scheduling, result acceptance and activation.
- [ ] Build and validate a single-profile candidate, then compare exact eligible membership under a short publication fence. Retry deltas at most three activation comparisons; thereafter retain explicit pending/retryable status. Normal search/publication remains on the old active profile until atomic switch succeeds.
- [ ] Switch profile and generation together. Recheck profile on normal Publish; old-profile work must enter a durable profile-specific continuation, not activate a stale profile or rewrite completed ownership. Prove new content becomes visible after this race, rather than merely proving the pointer did not regress.
- [ ] Reconcile deletion survivor rebuild and derived-index recovery with profile filtering. Preserve another source's model work, shared index/model artifacts and admitted OCR capacity. Unknown execution remains subject to existing refusal.
- [ ] Implement rollback as lexical-first degradation plus a newly validated deterministic generation from current eligible text. Do not reactivate an old snapshot that omits post-switch content. Test new content, deleted content and replaced document publications across rollback.
- [ ] Document restart recovery and binary downgrade restrictions. Preserve model store and last-good derived data; do not perform destructive migrations or cache cleanup.

**Tests:** extend `tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs`, `UsearchGenerationTests.cs`, source-deletion and publication integration suites; add `EmbeddingUpgradeIntegrationTests.cs`. Each distinct interleaving must be deterministic through test barriers, not sleep-based timing. Assert semantic search/read outcomes and authoritative SQL membership after restart, not just an operation status.

## Batch 4: measured usefulness and completion evidence

- [ ] Run the frozen R10 set separately for ordinary text, native PDFs and scanned/mixed PDFs. Report baseline and candidate Recall@5, reciprocal rank, exact checks, citation correctness and latency; no aggregate score may hide an input-class regression.
- [ ] Require zero scope/lifecycle leakage, zero wrong-reference resolution, all designated exact checks, recovery of a majority of predeclared paraphrase misses and no input-class Recall@5 decline. If this fails, keep the existing profile/default and report gaps; do not broaden the model survey automatically.
- [ ] Test warm/cold query latency and concurrent OCR plus background embedding. Confirm the two-second semantic deadline, honest degraded mode and actual resource placement; do not infer GPU use from configured flags.
- [ ] Run focused model-free/SQL/composition tests, locked restore, zero-warning Release build, full native suite, migration/model consistency and one independent full-change review. Record unavailable/skipped real-model evidence separately.
- [ ] Update the relevant roadmap entries with verified capability and remaining gaps. Prepare exact activation target, resource/profile binding, candidate/rollback evidence and scoped live validation proposal for explicit user approval. Do not acquire, migrate, deploy, restart or broadly re-embed production merely because this plan is approved.

Focused commands after tests are added:

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter 'FullyQualifiedName~Embedding|FullyQualifiedName~Search'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~SearchGenerationBinding|FullyQualifiedName~ScopedSemantic|FullyQualifiedName~EmbeddingUpgrade|FullyQualifiedName~SqlToUsearchRebuild|FullyQualifiedName~UsearchGeneration|FullyQualifiedName~SourceDeletion|FullyQualifiedName~DocumentPublication'
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build
```

Use only the repository's generated disposable SQL catalogues. Required tests that skip are not a pass. Operational activation uses the existing incremental deployment mechanism only with current authority; feature closeout uses `scripts/dev/complete-feature.ps1`. Preserve this branch/worktree until its required in-scope closeout succeeds.
