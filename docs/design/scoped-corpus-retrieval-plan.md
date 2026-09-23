# Scoped corpus retrieval implementation plan

Date: 2026-09-20
Last aligned: 2026-09-23, main `52742d998e07dc21415444629c710c9bbf55b8cd`.
Status: documentation aligned with current main; implementation has not started.

**Goal:** Search published ordinary and OCR-derived text by source/workspace and follow verified citations to retained context through MCP, REST and CLI.

**Architecture:** Add explicit corpus search/read operations to the native facade, with shared SQL eligibility and passage ranking. Reuse retained chunks and normalised provenance; add chunk Full-Text indexing and authenticated evidence references. Preserve existing mixed knowledge search and specialised code queries.

**Tech stack:** Existing .NET/C#, EF Core, SQL Server Full-Text, ASP.NET Core Data Protection, native MCP and CLI.

**Spec:** [Scoped corpus retrieval and semantic search](corpus-retrieval.md).

**Execution:** One implementation owner; use `superpowers:executing-plans` as relevant guidance. User/repository gates take precedence over generic skill ceremony. No implementation, migration, deployment or model work is authorised by this planning document.

## Global constraints

- Both ordinary and OCR-derived published text are first-class inputs. Never make an OCR flag a retrieval eligibility condition.
- Scope before candidate limits; check lifecycle again before disclosure. Preserve last-good document publication while a successor is pending.
- Read retained canonical text only; no source-original access or new processor runs.
- Preserve existing native operations/envelopes; secret disclosure and direct-loopback rules apply to both new operations.
- New search: query 1–2,048 UTF-16 units; limit 1–20; scope `all`, `root` or `workspace`. No arbitrary filters.
- Passage at most 1,024 UTF-16 units; read adds at most 4,096 context units; evidence token at most 2,048 characters; native request 32 KiB and new response 256 KiB ceilings.
- Delivery 1 reports `retrieval_mode=lexical` and does not activate or acquire models. No new extraction/chunking strategy is required.
- Include existing Office, JPEG/PNG OCR and Visio results, and distinguish metadata fallback from extracted text. Same-page region OCR, extraction fidelity fixes and terminal OCR retry remain separate work.
- Preserve `DocumentPublications` selection for internal kinds 2 and 3, physical-owner identity and independent kind 1 archive members. Never broaden child visibility globally.
- Read bounded chunks/context from large Office results (up to 200 MiB); do not load or rehash an entire canonical artefact for every hit or context read.
- Do not update dashboard manuals, screenshots or DOCX assets.

## Review focus

1. A high-ranked match in another source must not displace the correct scoped passage.
2. A matching word near the end of a long document must not return its unrelated opening text.
3. A retained reference must fail after publication replacement/deletion, including during concurrent requests.
4. Normalisation and multi-page spans must not create plausible but incorrect citations.
5. Native envelope protection, plugin allowlists and Full-Text population can fail independently of application unit tests.

## Baseline reconciliation and entry check

- [x] Reconciled the design with main `52742d9` and its published document/coverage records on 2026-09-23. The OCR/Visio and logical Office/image publication changes have merged. Their source/runtime limitations remain recorded in the design; this checkbox is a document review, not a fresh runtime test.
- [x] Inspected current publication selection, kinds 2/3, canonical metadata propagation, Visio/image fields, existing search shape, large Office bounds and native repository verification. The design's baseline table records the consequences.
- [ ] At implementation start, compare the then-current main with the pinned baseline and reconcile further relevant changes only. Read current `AGENTS.md`, `global.json` and [setup](../setup.md). Do not recreate the removed `docs/superpowers` or top-level plugin tree.
- [ ] Use the current generated plugin source `src/FluxKnowledge.Integrations/Codex/NativeCodexPluginManifestWriter.cs` and relevant native discovery/authority fixtures. Locate exact operation allowlists with `rg -n 'knowledge.search|corpus.query' src tests scripts`; edit only the repository owners that actually list operations, not an installed plugin or an assumed static plugin manifest.

## Batch 1: scoped lexical search through REST

**Observable result:** A retained plain-text passage can be searched through `/api/v1/corpus/search` within a registered root/workspace, with correct source identity and no other-source matches.

**Existing files:**

- `src/FluxKnowledge.Application/Search/HybridSearchService.cs` and `ReciprocalRankFusion.cs`: reuse ranking infrastructure without changing existing native result contracts.
- `src/FluxKnowledge.Application/IntegrationV1/NativeV1Facade.cs`: additive query dispatch.
- `src/FluxKnowledge.Web/NativeV1/NativeV1RequestMapper.cs` and `Endpoints/NativeV1Endpoints.cs`: closed request mapping and REST binding.
- `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContext.cs`, `Configurations/CanonicalSchemaConfigurations.cs`: only mappings needed by the migration/read model.
- `src/FluxKnowledge.Infrastructure.SqlServer/Search/SqlFullTextSearch.cs`: share candidate logic where the existing contract permits; preserve existing native callers.
- `src/FluxKnowledge.Infrastructure.SqlServer/Search/SqlSearchHydrator.cs`, `Persistence/SqlCorpusProjectionReader.cs` and `Persistence/SqlStageTransitionStore.cs`: read their exact physical-owner/selected-publication joins and selection order; do not change publication policy as part of adding a read path.
- `src/FluxKnowledge.Web/WebHostComposition.cs`: register new application and SQL readers.

**New files:**

- `src/FluxKnowledge.Application/Contracts/CorpusRetrievalContracts.cs`: complete spec request/response shapes and limits.
- `src/FluxKnowledge.Application/Search/CorpusRetrievalService.cs`: transport-neutral orchestration.
- `src/FluxKnowledge.Application/Ports/ICorpusRetrievalReader.cs`: resolved-scope and eligible-chunk queries.
- `src/FluxKnowledge.Infrastructure.SqlServer/Search/SqlCorpusRetrievalReader.cs`: shared SQL eligibility, scope resolution, passage Full-Text and exact candidates.
- An EF-generated `AddCorpusChunkFullText` migration in `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/`: use generated timestamp, nontransactional Full-Text operations, reversible index-only Down.

**Interfaces:** `CorpusSearchRequest` and `ICorpusRetrievalService` are defined in the spec. Add `ResolvedCorpusScope` containing kind, sorted root IDs and optional canonical path boundary; `EligiblePassageCandidate` contains logical owner, pipeline/artifact/chunk binding, text offsets, canonical hashes and lexical/exact rank. The SQL reader receives resolved scope and query; it must not accept raw SQL or arbitrary filesystem paths. These are internal representations, not additional API fields.

- [ ] Add failing Domain validation tests for every scope combination and limit. Add generated-SQL tests with nested and sibling registered roots, a cwd within a root, a cwd containing multiple roots, sibling path prefixes, a deleting root and a paused root. Preserve each retained record's root/owner identity across nested roots. Keep exact-path registration deduplication and protected-location exclusions unchanged. Test selected internal kinds 2 and 3, unselected children, late metadata after OCR and independent archive members. Apply filters before TOP; verify unknown workspace refuses.
- [ ] Implement validation, registered-root/path resolution and current publication eligibility. Keep unknown or malformed scope errors consistent across the native facade. Extract/reuse pure syntax and component comparison only: `SourceRootPathPolicy.ValidateAndCanonicalise` probes the filesystem and must not be called from retrieval. Assert search still works when original directories cannot be opened.
- [ ] Add chunk Full-Text using existing integer key/catalogue and `suppressTransaction: true`, following `InitialPhase1`. Wait for populated test fixtures using the existing bounded Full-Text helper; expose unavailable/populating readiness instead of interpreting it as an empty corpus.
- [ ] Implement lexical and exact-phrase candidate queries and stable ranking. Parameterise SQL, quote Full-Text terms, and verify exact claims ordinally. Exclude ineligible content before candidate limits. Test SQL collation near-matches, lifecycle/disclosure rejections and diversity exhausting the candidate budget; any resulting completeness limit must be reported explicitly.
- [ ] Wire REST and exercise real HTTP against a disposable host/database. Test punctuation/identifier input, a 100-chunk document with its only match in the final chunk, stronger out-of-scope matches and current/last-good publication. Reuse the retained Office large-result fixture boundary; instrument the read path to prove it projects bounded chunk/context text rather than loading a whole 200 MiB artefact or recalculating its full hash.

Representative behavioural assertions (use existing xUnit/`NativeSqlServerFixture` patterns; the seeded records are synthetic):

```csharp
// After seeding root A with a late-chunk marker and root B with stronger matches:
var request = new CorpusSearchRequest("INV-2026/0042", 5, "root", rootA, null);
var response = await service.SearchAsync(request, CancellationToken.None);
Assert.All(response.Results, hit => Assert.Equal(rootA, hit.RootId));
Assert.Contains(response.Results, hit => hit.Passage.Contains("INV-2026/0042", StringComparison.Ordinal));
Assert.Equal("lexical", response.RetrievalMode);
```

**Tests:** extend `tests/FluxKnowledge.Domain.Tests/Search/SearchQueryValidatorTests.cs` only for preserved native behaviour; add `CorpusRetrievalValidationTests.cs`. Extend `tests/FluxKnowledge.Integration.Tests/Search/HybridSearchIntegrationTests.cs` regression coverage and add `ScopedCorpusRetrievalTests.cs`. Reuse publication/large Office fixtures from `tests/FluxKnowledge.Integration.Tests/Persistence/DocumentPublicationIntegrationTests.cs` and `tests/FluxKnowledge.Integration.Tests/Sources/RetainedTextPipelineIntegrationTests.cs`. Extend `tests/FluxKnowledge.Web.Tests/Endpoints/NativeV1EndpointTests.cs`.

**Checkpoint:** The first HTTP result must work before adding semantic contracts or richer UI. If two batches have passed without it, reassess the blocker and smallest delivery path.

## Batch 2: citations and bounded retained passage reading

**Observable result:** Every search hit can retrieve exact retained context, with trustworthy locations for both native and OCR text.

**Existing files:** current `src/FluxKnowledge.Application/Documents/DocumentOcrProvenance.cs`, `Documents/VisioDocumentProvenance.cs`, `Workers/NormaliseTextStageWorker.cs`, `Indexing/CanonicalIndexStageWorker.cs` and `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/ArtifactEntity.cs` are consumed as contracts. Read metadata from the canonical artefact after normalisation; current propagation is implemented. Reuse `NativeV1ProjectionCursorCodec` as a pattern, not its cursor purpose or payload. No new typed table-cell/confidence model is part of this batch.

**New files:**

- `src/FluxKnowledge.Application/Search/CorpusCitationMapper.cs`: canonical span intersection and optional page/block/line locations.
- `src/FluxKnowledge.Application/Ports/ICorpusEvidenceCodec.cs`: encode/decode immutable evidence binding.
- `src/FluxKnowledge.Infrastructure.SqlServer/Search/CorpusEvidenceCodec.cs`: dedicated Data Protection purpose and bounded token parsing.
- `tests/FluxKnowledge.Domain.Tests/Search/CorpusCitationMapperTests.cs`.
- `tests/FluxKnowledge.Integration.Tests/Search/CorpusPassageReadTests.cs`.

**Interfaces:** `CorpusEvidenceBinding` contains version, nullable owner revision/root ID, source identity binding, pipeline ID/revision, canonical artefact ID/hash, chunk ID/hash and cited start/length. Null owner/root fields are valid only for eligible unrooted records; source and pipeline bindings remain mandatory. `ICorpusEvidenceCodec.Encode(binding)` returns the token; `Decode(token)` either returns this binding or a named invalid-reference failure. `CorpusReadRequest` goes through the same service and SQL eligibility definition as search. The mapper produces a bounded list of typed locations plus provenance warnings; offsets always refer to canonical text.

- [ ] Write failing citation tests for FormKC length changes already handled by current normalisation, CRLF, surrogate pairs, zero-length/invalid spans, multiple pages, native/OCR blocks, absent metadata and unsupported/corrupt metadata. Add repeated shape IDs on different Visio pages, background-page identity, and rotated JPEG/PNG source dimensions/transforms. Assert spans against retained substrings, not copied expected offsets from implementation; SQL slice counting must agree with .NET UTF-16 offsets.
- [ ] Map supported locations without guessing. Show one-based document pages, preserve page index and distinct `VisioPageId`, include bounded page/shape/master/connector IDs only when relevant, and label image locations separately. Preserve image coordinate-frame metadata rather than applying an unverified transform. Treat Office headings and `Kind=table` as text/block evidence, not page/sheet/cell coordinates. Do not copy arbitrary Visio `Data` values or invent confidence. Calculate canonical lines only within a bounded read, otherwise omit them. Limit location count/size within the response budget.
- [ ] Implement authenticated immutable references. Test bit flips, unsupported version, excessive length, missing key, changed artefact/chunk hash and unrelated index rebuild. Never accept a path from the token as authority to open a file.
- [ ] Add `corpus.read` REST application route, bounded same-artefact context selection and final disclosure/lifecycle checks. Do not materialise whole Office text for context or optional line counts. Verify publication replacement and deletion after search produce `evidence-stale`; old completed jobs and pending `DocumentOcrRequests` results cannot be used as alternative text sources.
- [ ] Validate current Office/image/Visio owner selection, metadata-to-OCR replacement and independent archive-member identity. Test a selected fallback reference becomes stale when higher-priority extraction publishes; a newer fallback cannot supersede that extraction. Reads never modify suppression or replay failed terminal OCR work.

```csharp
var read = await service.ReadAsync(new CorpusReadRequest(hit.EvidenceRef, 1024), token);
Assert.Equal(canonicalText.Substring(read.StartOffset, read.Length), read.Text);
Assert.Equal(hit.OwnerSourceRevisionId, read.OwnerSourceRevisionId);
// Replace publication or commit source deletion; resolving the same token must now refuse.
```

**Verification:** run Domain citation/evidence validation and generated-SQL search/read tests, including current source-deletion/document-publication regressions and existing `DocumentOcrProvenanceTests`/`VisioDocumentProvenanceTests`. No real OCR inference or interactive COM is needed for these retrieval checks: seed accepted canonical text/metadata using the current fixture shapes. Existing extraction delivery evidence is context, not a test run by this task.

## Batch 3: all interfaces, compatibility and evidence

**Observable result:** MCP and CLI execute the same scoped search/read flow, and users of existing knowledge/code operations see unchanged contracts.

**Files:** `src/FluxKnowledge.Web/Mcp/NativeV1McpTools.cs`, `src/FluxKnowledge.Cli/Commands/NativeV1Command.cs`, `src/FluxKnowledge.Application/IntegrationV1/NativeV1EnvelopeProtector.cs`, `NativeV1ContractLimits.cs`, corresponding mapper/facade files, `tests/FluxKnowledge.Web.Tests/Mcp/NativeV1McpToolsTests.cs`, `tests/FluxKnowledge.Integration.Tests/Cli/NativeV1CommandTests.cs`, `tests/FluxKnowledge.Integration.Tests/IntegrationV1/NativeV1EnvelopeProtectorTests.cs`, `tests/FluxKnowledge.Domain.Tests/Knowledge/KnowledgeQueryServiceTests.cs` and native operation discovery/authority fixtures located in baseline reconciliation.

- [ ] Add failing contract tests for both operations across discovery, request parsing, facade routing, protected envelope shapes, CLI JSON input/output and read-only retry classification. Test 32 KiB requests, 256 KiB responses, unknown fields, secret-containing passages/paths, cancellation and forwarded/non-loopback access.
- [ ] Add thin transport mappings and operation-specific response budgeting; do not enlarge the general native response ceiling or silently drop location fields. Apply the existing local disclosure policy to text, title and source identity. A withheld span must not be recoverable through `corpus.read`.
- [ ] Preserve `knowledge.search` interleaving and old fields, `corpus.query` views and code/symbol search. Verify old clients need no new arguments. Keep `/api/search` defaults and its existing unsupported-scope handling unless separately adapted through a backward-compatible mapping.
- [ ] Run the spec's R1–R6 matrix: plain text, native PDF, scanned PDF, PDF with separate native/scanned pages, DOCX/XLSX/PPTX, JPEG/PNG, Visio, selected metadata fallback and published code text. Record the actual locations each supplies, including span-only Office results. Same-page region OCR remains outside this phase.
- [ ] Freeze bounded retrieval questions and record extraction coverage before model rankings are reviewed. Preserve source expectations and report extraction gaps separately from retrieval misses, with denominators disclosed; do not repair repeated/missing OCR text or tune questions to pass. Private evidence remains outside Git.
- [ ] Update affected `docs/architecture.md`, `docs/roadmap.md` and `docs/integrations.md` when the operations actually ship. The planned additions bring discovery from nine to eleven tools; current documentation must not describe them as already available. Transfer the page-field retrieval gap out of the OCR row only when accepted, leaving its extraction/retry gaps intact. No manuals or production claims.

## Verification commands and closeout boundary

Use the existing `NativeSqlServerFixture` with generated databases only. `FLUXKNOWLEDGE_TEST_SQL_CONNECTION` must identify an approved local test server; never print it or connect a test directly to the production catalogue. Required SQL tests that skip do not constitute a passed gate.

```powershell
pwsh -NoProfile -File tests/native/repository-contract.ps1 -SourceRoot .
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter 'FullyQualifiedName~CorpusRetrieval|FullyQualifiedName~CorpusCitation|FullyQualifiedName~SearchQueryValidator|FullyQualifiedName~KnowledgeQueryService|FullyQualifiedName~DocumentOcrProvenance|FullyQualifiedName~VisioDocumentProvenance'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~ScopedCorpusRetrieval|FullyQualifiedName~CorpusPassageRead|FullyQualifiedName~HybridSearch|FullyQualifiedName~DocumentPublication|FullyQualifiedName~RetainedTextPipeline|FullyQualifiedName~SourceDeletion|FullyQualifiedName~NativeV1EnvelopeProtector|FullyQualifiedName~NativeV1Command|FullyQualifiedName~NativeCodexPlugin'
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter 'FullyQualifiedName~NativeV1|FullyQualifiedName~SearchEndpoint'
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build
```

These are future implementation checks; documentation-only alignment runs the native repository contract and document/diff checks, without a product build or inference. Run narrow implementation checks after coherent batches, then Release/full suite and an independent complete-change review. Verify migration/model consistency and Full-Text population using current repository tools. Use `scripts/dev/complete-feature.ps1` for feature closeout; its default path verifies/integrates without deployment. There is no remaining OCR-thread merge dependency. Production migration/incremental deployment still requires explicit authority, a reviewed concrete plan and rollback. Semantic implementation follows verification of this first capability.
