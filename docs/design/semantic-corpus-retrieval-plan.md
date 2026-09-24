# Semantic corpus retrieval implementation plan

Date: 2026-09-20
Last aligned: 2026-09-24, main `0b2606ecb8a2db1b4d7a6eee251a6d933bcc3d91`.
Status: design-stage plan with explicit model and migration gates; lexical corpus
retrieval is deployed, but no learned embedding provider is selected or active.
The active model evaluation is BGE-M3 ONNX at revision
`5617a9f61b028005a4858fdac845db406aefb181`, using the existing .NET ONNX
Runtime and DirectML setup offline. The alternative Python inference route is
paused; its earlier shortlist entry is not an acquisition or implementation
instruction. Production selection still depends on the frozen local relevance,
execution, latency, memory, OCR contention and source-coverage gates. The
[2026-09-24 ONNX pilot](../operations/2026-09-24-bge-m3-onnx-evaluation.md)
verified offline DirectML execution but failed the frozen relevance gate;
ordinary plain-text and scanned/mixed-PDF coverage remain absent. Keep this
plan's implementation batches gated until a new held-out evaluation passes.

**Goal:** Improve paraphrase retrieval across ordinary and OCR-derived text using one verified local embedding model, without losing exact retrieval, citation correctness or service continuity.

**Architecture:** Extend the verified corpus retrieval contract with optional semantic candidates. Bind query embedding to one immutable model/index generation, embed retained canonical chunks through durable model-specific work, and switch profiles only after a complete current candidate generation is validated. Lexical search remains independently usable.

**Tech stack:** Existing .NET, SQL Server, USearch, local model store and scheduler; model-specific runtime selection is an explicit first checkpoint, not an assumed dependency.

**Spec:** [Scoped corpus retrieval and semantic search](corpus-retrieval.md).

**Prerequisite:** [Scoped corpus search/read](scoped-corpus-retrieval-plan.md), verified against the current published Office/PDF/image/Visio contracts. The OCR implementation has already merged; this is a retrieval-capability dependency, not a wait for the other task.

**Execution:** One implementation owner. Obtain independent high-risk review of the concrete profile/schema/transaction design before its implementation, and review the final whole change. These are technical gates; they do not authorise production actions.

## Global constraints

- No new OCR/extraction or source-original reads to build embeddings; consume eligible retained canonical chunks for all supported text inputs.
- Canonical model payloads stay under `J:\Models`; exact identities, held leases and zero implicit downloads are mandatory. No model acquisition is authorised here.
- Query/document prompts, tokenizer, pooling, normalisation, metric and dimensions form the profile fingerprint. Equal dimensions are not compatibility. Admit only cosine (`cos`) profiles in this increment.
- Existing SQL Full-Text and exact evidence ranking remain available. Missing/busy semantic capability yields explicit lexical degradation.
- Never mutate completed extraction jobs, admission receipts or old immutable membership to enable a new model.
- Preserve deletion, suppression, current publication and other-source ownership under concurrency and recovery.
- Retain selected internal kind 2/3 owner identity and independent archive members; metadata fallback must not be reported as image/OCR content.
- Enumerate large Office results in bounded chunk batches. Existing broader vector enumerators are not proof of selected publication eligibility.
- Complete and verify the first retrieval increment against the current native document pipeline before implementing semantic retrieval.

## Review focus

1. A query embedded immediately before a profile switch must not search the new incompatible index.
2. A source replaced/deleted during re-embedding must not reappear through late results, activation or rollback.
3. An old-profile normal Publish job must not regress the active pointer or strand a document after cutover.
4. Recovery and deletion survivor rebuilds must not enumerate mixed profiles or erase current eligible content.
5. OCR occupying GPU capacity, an unavailable J: drive or provider refusal must leave honest lexical behaviour and zero downloads.

## Batch 1: select a bounded local embedding contract

**Deliverable:** An exact, evidence-backed model/runtime binding decision, or a concrete cache/runtime approval blocker. This decision is required before implementing a provider; model names and version numbers are deliberately not invented in this plan.

**Inspect:** `src/FluxKnowledge.Application/Ports/IEmbeddingProvider.cs`, `src/FluxKnowledge.Infrastructure.Inference/DeterministicTokenHashEmbeddingProvider.cs`, `src/FluxKnowledge.Application/Models/ILocalModelStore.cs`, `src/FluxKnowledge.Web/Configuration/NativeGoLiveRuntimeOptions.cs`, `WebHostComposition.cs`, current OCR scheduler/runtime composition and the central inventory. Existing OCR activation/manifest checks do not enable embeddings; preserve that provisioned route while designing the distinct embedding binding. Inspect metadata without loading providers or initiating acquisition.

- [ ] Recheck repository model rules, central availability/containment and inventory, plus relevant configured provider caches. Record exact reusable artifacts and unverified/missing dependencies privately. Do not copy/adopt or hash/load payloads outside the applicable verification procedure.
- [ ] Evaluate the separately approved pinned BGE-M3 ONNX route first with the existing .NET DirectML runtime. If a required tokenizer or inference runtime component is still missing, inventory it and request separate exact acquisition authority before transfer. Do not broaden to the paused alternative unless measured BGE shortcomings justify a Python worker and its operational cost.
- [ ] For the selected route, record immutable weights/tokenizer identity, versioned query/document prefixes, pooling, normalisation, dimensions, metric, token limit, execution provider and runtime. Check current primary documentation if selecting a new library/model; do not infer support from an old cache filename.
- [ ] Define long-chunk handling before inference: preserve all canonical text through bounded model-token windows and a documented normalised pooling rule, fingerprinted with the profile. No silent provider truncation. If multiple subchunk vectors are actually needed, amend the schema/retrieval design before implementation rather than quietly changing vector identity.
- [ ] Measure ordinary text and OCR samples against the frozen question set and its pre-recorded extraction-coverage subset, without rewriting expectations. Report upstream omissions/repetition separately from retrieval misses and disclose both denominators. Include long Office content and image/Visio correctness fixtures. Record semantic relevance, peak CPU/GPU memory, warm/cold latency and OCR contention. Follow separate authority for real-model execution/runtime activation; this alignment task authorises none.
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
- [ ] Rank the complete scoped vector set up to 10,000 eligible vectors; beyond that return lexical with `scope-capacity-exceeded`. Count/bound before materialising vectors and stream bounded batches for large Office inputs. Test a correct match ranked below global top-k by unrelated roots, selected kinds 2/3 and a large Office scope exceeding the bound. No fixed oversampling/postfilter shortcut.
- [ ] Implement profile-specific offline adapter and held leases only after the model gate. Prove cache hit/miss/unavailable-drive/concurrent requests cause zero implicit transfers, detect nonfinite/dimension-mismatched results and preserve cancellation ownership.
- [ ] Use existing interactive/background scheduler lanes for GPU work. Test a query deadline while OCR owns capacity: return lexical degradation without starting unadmitted inference or releasing OCR resources. Reuse the admitted-run completion pattern, not the PaddleOCR payload/adapter identity or its runtime flags. Add explicit composition checks for independent embedding readiness and continued OCR operation. A request timeout is not execution termination, and an OCR deletion exception cannot be applied automatically to another executor type.

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

**Review before implementation:** finalise a short schema/transaction amendment using the then-current code. It must identify exact locks/row versions, selected publication set, profile fencing, stale-work continuation and source deletion. Specifically account for `PublishDocumentIfApplicableAsync` selecting owner/branch within the final serialisable transaction after the candidate has been built; model replacement must not omit that owning Publish's prospective result or expose it early. An independent reviewer must approve the concrete design before schema/worker changes. This plan does not claim that current broad vector enumeration or prior planning review solves those details.

**Existing files:** `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/VectorEntity.cs`, `IndexStateEntity.cs`, `IndexGenerationEntity.cs`, `IndexGenerationVectorEntity.cs`, `DocumentPublicationEntity.cs`, `Configurations/CanonicalSchemaConfigurations.cs`, `SqlPipelineStore.cs`, `SqlStageTransitionStore.cs`, `SqlSourceDeletionStore.cs`, `SqlDerivedIndexRecoveryStore.cs`, `src/FluxKnowledge.Infrastructure.Usearch/UsearchGenerationBuilder.cs`, `src/FluxKnowledge.Application/Indexing/EmbedStageWorker.cs`, `PublishStageWorker.cs` and runtime composition. Add EF-generated migrations; never hand-edit the snapshot alone. Retain the existing selector's content-over-metadata priority rather than introducing a second document winner algorithm.

**New responsibilities:** a SQL-durable embedding-upgrade operation, bounded upgrade worker and profile-specific publication continuation. Provisional files are `Persistence/Entities/EmbeddingUpgradeEntity.cs`, `Persistence/SqlEmbeddingUpgradeStore.cs`, `Application/Indexing/EmbeddingUpgradeWorker.cs` and `Application/Ports/IEmbeddingUpgradeStore.cs`. Exact schema is the reviewed amendment's output; do not create a general workflow/provider-management subsystem.

**Operation contract:** source profile, target profile, captured source-set digest, candidate generation, state, resumable progress, failure reason and fencing token. The logical unit of work is `(canonical chunk ID, content hash, source revision, target profile)`; repeat success reuses identical results, conflicting payloads refuse. Candidate/profile selection must cover builder, activation, normal Publish, deletion survivor rebuild and crash recovery together.

- [ ] Add failing generated-SQL tests for simultaneous upgrade requests, duplicate/out-of-order completion, source replacement/deletion during embedding, stale candidate activation, crash before/after pointer change, and ordinary publication during catch-up. Include metadata-to-OCR replacement, a late lower-priority metadata result, Office logical publication, independent archive members and paused last-good reads.
- [ ] Implement additive upgrade state and resumable bounded batches. Store immutable vectors in existing canonical storage; never replay Extract/OCR. Define selected published/profile membership explicitly rather than forwarding unchanged `ReadEligibleVectorsAsync` results. Use current lifecycle fences at scheduling, result acceptance and activation; large Office documents must not require whole-text/vector materialisation in one batch.
- [ ] Build and validate a single-profile candidate, then compare exact eligible membership under a short publication fence. Retry deltas at most three activation comparisons; thereafter retain explicit pending/retryable status. Normal search/publication remains on the old active profile until atomic switch succeeds.
- [ ] Switch profile and generation together. Recheck profile on normal Publish; old-profile work must enter a durable profile-specific continuation, not activate a stale profile or rewrite completed ownership. Validate both the last committed publication set and the owning transition's prospective set under its fence. Prove the new selected document becomes searchable exactly once after this race, rather than merely proving the pointer did not regress.
- [ ] Reconcile deletion survivor rebuild and derived-index recovery with profile filtering. Preserve another source's model work, shared index/model artifacts and admitted OCR capacity. Unknown execution remains subject to existing refusal.
- [ ] Implement rollback as lexical-first degradation plus a newly validated deterministic generation from current eligible text. Do not reactivate an old snapshot that omits post-switch content. Test new content, deleted content and replaced document publications across rollback.
- [ ] Document restart recovery and binary downgrade restrictions. Preserve model store and last-good derived data; do not perform destructive migrations or cache cleanup.

**Tests:** extend `tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs`, `UsearchGenerationTests.cs`, source-deletion and publication integration suites; add `EmbeddingUpgradeIntegrationTests.cs`. Each distinct interleaving must be deterministic through test barriers, not sleep-based timing. Assert semantic search/read outcomes and authoritative SQL membership after restart, not just an operation status.

## Batch 4: measured usefulness and completion evidence

- [ ] Run the frozen R10 set separately for ordinary text, native PDFs and supported scanned/mixed PDFs. Report extraction coverage and source-to-result failures alongside baseline/candidate retrieval metrics on the declared retained-evidence subset. Include image/Visio/large Office correctness fixtures. No aggregate score may hide an input-class regression or label an extraction omission a ranking success.
- [ ] Require zero scope/lifecycle leakage, zero wrong-reference resolution, all designated exact checks, recovery of a majority of predeclared paraphrase misses and no input-class Recall@5 decline. If this fails, keep the existing profile/default and report gaps; do not broaden the model survey automatically.
- [ ] Test warm/cold query latency and concurrent OCR plus background embedding. Confirm the two-second semantic deadline, honest degraded mode and actual resource placement; do not infer GPU use from configured flags.
- [ ] Run focused model-free/SQL/composition tests, locked restore, zero-warning Release build, full native suite, migration/model consistency and one independent full-change review. Record unavailable/skipped real-model evidence separately.
- [ ] Update the relevant roadmap entries with verified capability and remaining gaps. Prepare exact activation target, resource/profile binding, candidate/rollback evidence and scoped live validation proposal for explicit user approval. Do not acquire, migrate, deploy, restart or broadly re-embed production merely because this plan is approved.

Focused commands after tests are added:

```powershell
pwsh -NoProfile -File tests/native/repository-contract.ps1 -SourceRoot .
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter 'FullyQualifiedName~Embedding|FullyQualifiedName~Search'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~SearchGenerationBinding|FullyQualifiedName~ScopedSemantic|FullyQualifiedName~EmbeddingUpgrade|FullyQualifiedName~SqlToUsearchRebuild|FullyQualifiedName~UsearchGeneration|FullyQualifiedName~SourceDeletion|FullyQualifiedName~DocumentPublication'
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build
```

Use only the repository's generated disposable SQL catalogues. Required tests that skip are not a pass. Operational activation uses the existing incremental deployment mechanism only with current authority; feature closeout uses `scripts/dev/complete-feature.ps1`. Preserve this branch/worktree until its required in-scope closeout succeeds.

The 2026-09-23 alignment is documentation-only: native repository/link checks and independent design review apply now; product tests above apply during implementation. Do not rerun the completed OCR model survey, acquisition or deployment as a prerequisite to reading this plan.
