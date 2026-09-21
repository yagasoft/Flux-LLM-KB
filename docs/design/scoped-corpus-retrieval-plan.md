# Scoped corpus retrieval implementation plan

Date: 2026-09-20
Status: planning complete only; verify the current native document contracts before implementation.

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
- Do not update dashboard manuals, screenshots or DOCX assets.

## Review focus

1. A high-ranked match in another source must not displace the correct scoped passage.
2. A matching word near the end of a long document must not return its unrelated opening text.
3. A retained reference must fail after publication replacement/deletion, including during concurrent requests.
4. Normalisation and multi-page spans must not create plausible but incorrect citations.
5. Native envelope protection, plugin allowlists and Full-Text population can fail independently of application unit tests.

## Baseline reconciliation

- [ ] Start from current main in a dedicated worktree and record its commit. Reconcile the design with the completed native document implementation and its scoped delivery evidence.
- [ ] Inspect final `DocumentPublicationEntity`, `DocumentOcrProvenance`, `NormaliseTextStageWorker`, `SqlSearchHydrator`, `SqlFullTextSearch` and source-deletion tests. Confirm actual metadata, current publication and accepted-child rules against the spec. Do not assume worktree checkboxes prove shipped behaviour.
- [ ] Read current `AGENTS.md`, `global.json`, native test instructions and `NativeCodexPluginManifestWriter`. Locate the operation ledger with `rg -n 'knowledge.search|corpus.query' src tests scripts`; update repository source files rather than an installed plugin copy.

## Batch 1: scoped lexical search through REST

**Observable result:** A retained plain-text passage can be searched through `/api/v1/corpus/search` within a registered root/workspace, with correct source identity and no other-source matches.

**Existing files:**

- `src/FluxKnowledge.Application/Search/HybridSearchService.cs` and `ReciprocalRankFusion.cs`: reuse ranking infrastructure without changing existing native result contracts.
- `src/FluxKnowledge.Application/IntegrationV1/NativeV1Facade.cs`: additive query dispatch.
- `src/FluxKnowledge.Web/NativeV1/NativeV1RequestMapper.cs` and `Endpoints/NativeV1Endpoints.cs`: closed request mapping and REST binding.
- `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContext.cs`, `Configurations/CanonicalSchemaConfigurations.cs`: only mappings needed by the migration/read model.
- `src/FluxKnowledge.Infrastructure.SqlServer/Search/SqlFullTextSearch.cs`: share candidate logic where the existing contract permits; preserve existing native callers.
- `src/FluxKnowledge.Web/WebHostComposition.cs`: register new application and SQL readers.

**New files:**

- `src/FluxKnowledge.Application/Contracts/CorpusRetrievalContracts.cs`: complete spec request/response shapes and limits.
- `src/FluxKnowledge.Application/Search/CorpusRetrievalService.cs`: transport-neutral orchestration.
- `src/FluxKnowledge.Application/Ports/ICorpusRetrievalReader.cs`: resolved-scope and eligible-chunk queries.
- `src/FluxKnowledge.Infrastructure.SqlServer/Search/SqlCorpusRetrievalReader.cs`: shared SQL eligibility, scope resolution, passage Full-Text and exact candidates.
- An EF-generated `AddCorpusChunkFullText` migration in `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/`: use generated timestamp, nontransactional Full-Text operations, reversible index-only Down.

**Interfaces:** `CorpusSearchRequest` and `ICorpusRetrievalService` are defined in the spec. Add `ResolvedCorpusScope` containing kind, sorted root IDs and optional canonical path boundary; `EligiblePassageCandidate` contains logical owner, pipeline/artifact/chunk binding, text offsets, canonical hashes and lexical/exact rank. The SQL reader receives resolved scope and query; it must not accept raw SQL or arbitrary filesystem paths. These are internal representations, not additional API fields.

- [ ] Add failing Domain validation tests for every scope combination and limit. Add generated-SQL tests with two roots, nested/sibling path prefixes, a deleting root, a paused root and an accepted internal document revision. Apply filters before TOP; verify unknown workspace refuses.
- [ ] Implement validation, registered-root/path resolution and current publication eligibility. Keep unknown or malformed scope errors consistent across the native facade.
- [ ] Add chunk Full-Text using existing integer key/catalogue and `suppressTransaction: true`, following `InitialPhase1`. Wait for populated test fixtures using the existing bounded Full-Text helper; expose unavailable/populating readiness instead of interpreting it as an empty corpus.
- [ ] Implement lexical and exact-phrase candidate queries and stable ranking. Parameterise SQL, quote Full-Text terms, and verify exact claims ordinally. Exclude ineligible content before candidate limits. Test SQL collation near-matches, lifecycle/disclosure rejections and diversity exhausting the candidate budget; any resulting completeness limit must be reported explicitly.
- [ ] Wire REST and exercise real HTTP against a disposable host/database. Test punctuation/identifier input, a 100-chunk document with its only match in the final chunk, stronger out-of-scope matches and current/last-good publication.

Representative behavioural assertions (use existing xUnit/`NativeSqlServerFixture` patterns; the seeded records are synthetic):

```csharp
// After seeding root A with a late-chunk marker and root B with stronger matches:
var request = new CorpusSearchRequest("INV-2026/0042", 5, "root", rootA, null);
var response = await service.SearchAsync(request, CancellationToken.None);
Assert.All(response.Results, hit => Assert.Equal(rootA, hit.RootId));
Assert.Contains(response.Results, hit => hit.Passage.Contains("INV-2026/0042", StringComparison.Ordinal));
Assert.Equal("lexical", response.RetrievalMode);
```

**Tests:** extend `tests/FluxKnowledge.Domain.Tests/Search/SearchQueryValidatorTests.cs` only for preserved native behaviour; add `CorpusRetrievalValidationTests.cs`. Extend `tests/FluxKnowledge.Integration.Tests/Search/HybridSearchIntegrationTests.cs` regression coverage and add `ScopedCorpusRetrievalTests.cs`. Extend `tests/FluxKnowledge.Web.Tests/Endpoints/NativeV1EndpointTests.cs`.

**Checkpoint:** The first HTTP result must work before adding semantic contracts or richer UI. If two batches have passed without it, reassess the blocker and smallest delivery path.

## Batch 2: citations and bounded retained passage reading

**Observable result:** Every search hit can retrieve exact retained context, with trustworthy locations for both native and OCR text.

**Existing files:** current `src/FluxKnowledge.Application/Documents/DocumentOcrProvenance.cs`, `Workers/NormaliseTextStageWorker.cs` and `Infrastructure.SqlServer/Persistence/Entities/ArtifactEntity.cs` are consumed as contracts. Change them only if a demonstrated retrieval requirement is not already met, with explicit dependency reconciliation. Reuse `NativeV1ProjectionCursorCodec` as a pattern, not its cursor purpose or payload.

**New files:**

- `src/FluxKnowledge.Application/Search/CorpusCitationMapper.cs`: canonical span intersection and optional page/block/line locations.
- `src/FluxKnowledge.Application/Ports/ICorpusEvidenceCodec.cs`: encode/decode immutable evidence binding.
- `src/FluxKnowledge.Infrastructure.SqlServer/Search/CorpusEvidenceCodec.cs`: dedicated Data Protection purpose and bounded token parsing.
- `tests/FluxKnowledge.Domain.Tests/Search/CorpusCitationMapperTests.cs`.
- `tests/FluxKnowledge.Integration.Tests/Search/CorpusPassageReadTests.cs`.

**Interfaces:** `CorpusEvidenceBinding` contains version, nullable owner revision/root ID, source identity binding, pipeline ID/revision, canonical artefact ID/hash, chunk ID/hash and cited start/length. Null owner/root fields are valid only for eligible unrooted records; source and pipeline bindings remain mandatory. `ICorpusEvidenceCodec.Encode(binding)` returns the token; `Decode(token)` either returns this binding or a named invalid-reference failure. `CorpusReadRequest` goes through the same service and SQL eligibility definition as search. The mapper produces a bounded list of typed locations plus provenance warnings; offsets always refer to canonical text.

- [ ] Write failing citation tests for FormKC length changes already handled by current normalisation, CRLF, surrogate pairs, zero-length/invalid spans, multiple pages, native and OCR blocks, absent metadata and corrupt metadata. Assert spans against retained substrings, not copied expected offsets from implementation.
- [ ] Map supported locations without guessing. Show one-based page numbers, preserve zero-based metadata identity, and label calculated line numbers as canonical lines. Limit location count/size within the response budget.
- [ ] Implement authenticated immutable references. Test bit flips, unsupported version, excessive length, missing key, changed artefact/chunk hash and unrelated index rebuild. Never accept a path from the token as authority to open a file.
- [ ] Add `corpus.read` REST application route, bounded context selection and final disclosure/lifecycle checks. Verify publication replacement and deletion after search produce `evidence-stale`; old completed jobs and pending OCR results cannot be used as alternative text sources.
- [ ] Validate exact source ownership for generated Office/archive/document outputs using the final incoming ownership graph; retain the current logical document identity rather than flattening every child to a parent filename.

```csharp
var read = await service.ReadAsync(new CorpusReadRequest(hit.EvidenceRef, 1024), token);
Assert.Equal(canonicalText.Substring(read.StartOffset, read.Length), read.Text);
Assert.Equal(hit.OwnerSourceRevisionId, read.OwnerSourceRevisionId);
// Replace publication or commit source deletion; resolving the same token must now refuse.
```

**Verification:** run Domain citation/evidence validation and generated-SQL search/read tests. Include source-deletion and document-publication regression tests from the incoming branch. No real OCR inference is necessary: seed accepted canonical text/metadata and separately consume the incoming integration fixture.

## Batch 3: all interfaces, compatibility and evidence

**Observable result:** MCP and CLI execute the same scoped search/read flow, and users of existing knowledge/code operations see unchanged contracts.

**Files:** `src/FluxKnowledge.Web/Mcp/NativeV1McpTools.cs`, `src/FluxKnowledge.Cli/Commands/NativeV1Command.cs`, `src/FluxKnowledge.Application/IntegrationV1/NativeV1EnvelopeProtector.cs`, `NativeV1ContractLimits.cs`, corresponding mapper/facade files, `tests/FluxKnowledge.Web.Tests/Mcp/NativeV1McpToolsTests.cs`, `tests/FluxKnowledge.Integration.Tests/Cli/NativeV1CommandTests.cs`, `tests/FluxKnowledge.Integration.Tests/IntegrationV1/NativeV1EnvelopeProtectorTests.cs`, `tests/FluxKnowledge.Domain.Tests/Knowledge/KnowledgeQueryServiceTests.cs` and native operation discovery/authority fixtures located in baseline reconciliation.

- [ ] Add failing contract tests for both operations across discovery, request parsing, facade routing, protected envelope shapes, CLI JSON input/output and read-only retry classification. Test 32 KiB requests, 256 KiB responses, unknown fields, secret-containing passages/paths, cancellation and forwarded/non-loopback access.
- [ ] Add thin transport mappings and operation-specific response budgeting; do not enlarge the general native response ceiling or silently drop location fields. Apply the existing local disclosure policy to text, title and source identity. A withheld span must not be recoverable through `corpus.read`.
- [ ] Preserve `knowledge.search` interleaving and old fields, `corpus.query` views and code/symbol search. Verify old clients need no new arguments. Keep `/api/search` defaults and its existing unsupported-scope handling unless separately adapted through a backward-compatible mapping.
- [ ] Run the spec's R1–R6 matrix: plain text, native PDF, scanned PDF, mixed PDF, Office and published code-text regression. Record which locations each format actually supplies, including deliberate span-only results.
- [ ] Freeze the bounded retrieval evaluation questions before model rankings are reviewed. Record the lexical/token-hash baseline and exact-value failures honestly; private evidence remains outside Git.
- [ ] Update only affected architecture/roadmap entries with actual implementation/test status. No manuals or production claims.

## Verification commands and closeout boundary

Use the existing `NativeSqlServerFixture` with generated databases only. `FLUXKNOWLEDGE_TEST_SQL_CONNECTION` must identify an approved local test server; never print it or connect a test directly to the production catalogue. Required SQL tests that skip do not constitute a passed gate.

```powershell
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter 'FullyQualifiedName~CorpusRetrieval|FullyQualifiedName~CorpusCitation|FullyQualifiedName~SearchQueryValidator|FullyQualifiedName~KnowledgeQueryService'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~ScopedCorpusRetrieval|FullyQualifiedName~CorpusPassageRead|FullyQualifiedName~HybridSearch|FullyQualifiedName~DocumentPublication|FullyQualifiedName~SourceDeletion|FullyQualifiedName~NativeV1EnvelopeProtector|FullyQualifiedName~NativeV1Command'
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter 'FullyQualifiedName~NativeV1|FullyQualifiedName~SearchEndpoint'
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build
```

The commands describe future checks, not checks run by this planning task. Run the narrow checks after coherent batches; run Release/full suite and an independent complete-change review at delivery. Verify migration/model consistency and Full-Text population using existing repository tools. Use `scripts/dev/complete-feature.ps1` for authorised feature closeout; keep this planning worktree and do not merge/push/deploy it now. Any future production migration/incremental deployment requires separate explicit authority, a reviewed concrete plan and rollback. The semantic plan can begin only after this first capability is verified.
