# Task 5 report: rebuildable USearch projections

## Delivered capability

Implemented a CPU-only deterministic token-hash embedding provider, bounded canonical chunking, CanonicalIndex/Embed/Publish outbox workers, SQL-backed chunk/vector/generation retrieval, immutable staged USearch placement, and an active-generation ANN reader. The SQL transition contract carries chunk/vector writes and the active pointer change so status publication remains after the committed transition.

The web composition requires `Usearch:RootPath`; it rejects a missing root and any root inside the current repository or deployment directory. No writable repository/deployment default was added.

## Verification

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore
```

Passed: 33; failed: 0; skipped: 0.

```powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Usearch
```

Passed: 1; failed: 0; skipped: 1. The passing local test exercised USearch save, reopen, metadata/ID/count/dimension validation and immutable placement. The skipped test is the opt-in native SQL rebuild fixture.

```powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Indexing
```

Passed: 2; failed: 0; skipped: 1 for the same absent `FLUXKNOWLEDGE_TEST_SQL_CONNECTION` opt-in.

```powershell
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-restore
dotnet build FluxKnowledge.slnx --configuration Release --no-restore
git diff --check
```

Web tests passed 2/2; the Release build succeeded with zero warnings and zero errors; `git diff --check` succeeded.

The focused active-reader replacement check also passed: it reuses the loaded immutable generation while the SQL pointer is unchanged and replaces it when that pointer changes.

## Native SQL evidence not run

`FLUXKNOWLEDGE_TEST_SQL_CONNECTION` was not supplied, so no SQL Server connection was opened and the guarded disposable-database rebuild evidence was skipped. No target database, migration, provisioning, I: ACL, deployment, service restart, model/runtime or GPU asset was touched.

## Changed areas

- `FluxKnowledge.Application`: deterministic indexing contracts, chunking and three stage workers.
- `FluxKnowledge.Infrastructure.SqlServer`: canonical chunk/vector/generation reads and transactional stage output persistence.
- `FluxKnowledge.Infrastructure.Inference`: model-free FormKC/FNV-1a provider.
- `FluxKnowledge.Infrastructure.Usearch`: configured root validation, staged immutable generations, validation and active reader.
- Web composition and focused Domain, Integration and Web composition tests.

## Correction round 1

- Added immutable `IndexGenerationVectors` membership with an EF migration and
  compatibility backfill from the existing vector-origin generation. The
  migration is source only and was not executed against a database.
- A new candidate is formed from all current, non-deleted latest-revision
  vectors. Its deterministic identity and checksum cover fingerprint, cosine
  metric, dimensions, stable vector IDs and vector content hashes. The SQL
  transition re-reads the eligible corpus, persists matching membership, then
  updates the active pointer; a changed corpus raises a stale-candidate error
  before pointer regression.
- Existing final directories are validated and reused for the same deterministic
  candidate rather than overwritten. The reader rechecks the SQL pointer around
  cache use and holds a host-wide immutable native handle cache.
- Text chunking now keeps surrogate pairs intact and the deterministic provider
  has a FormKC-text fallback contribution when ASCII token processing produces
  no signal, including exact signed cancellation.

Fresh correction evidence:

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Indexing
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-restore
dotnet build FluxKnowledge.slnx --configuration Release --no-restore
```

Domain: 35 passed. Integration Indexing: 2 passed and 1 expected guarded SQL
skip. Web: 2 passed. Release build: zero warnings and errors.

## Guarded native SQL test source completion

`SqlToUsearchRebuildTests` now contains three complete guarded disposable-SQL
tests rather than a fixture-open placeholder: SQL-membership rebuild after
deleting the entire index root, injected candidate validation failure preserving
the prior active pointer/directory, and the full Register → Extract → Normalise
→ CanonicalIndex → Embed → Publish hosted-pump slice with durable artefact,
chunk, stable vector-ID, membership, pointer and index assertions. The rebuild
path now reads the immutable SQL membership snapshot for the specified active
generation rather than using a deleted directory or a fresh corpus query.

The current environment did not supply `FLUXKNOWLEDGE_TEST_SQL_CONNECTION`, so
these three bodies compiled and skipped cleanly (no disposable database was
created). They remain unexecuted live-SQL evidence.

## Correction round 2

Publication now carries the exact `IndexGenerationCandidateSnapshot` returned
by the builder into the transition rather than re-reading eligibility in the
worker. The transition compares vector IDs, model, dimensions and content
hashes against its SQL current-corpus query and recomputes the snapshot
checksum before it creates the generation metadata/membership and swaps the
pointer. A changed corpus completes the superseded Publish work without moving
the active pointer. Eligibility now selects the newest revision before
excluding deleted rows, preventing a deleted latest revision from reviving old
content. Rebuild uses the persisted membership snapshot.

Candidate validation additionally recomputes vector-byte hashes and checksum;
migration downgrade rejects snapshot-only history, and test cleanup clears the
active pointer before removing referenced generation rows.

Fresh local source evidence: Integration USearch/Indexing passed 2 with 3
guarded native-SQL skips; Release build passed with zero warnings/errors. No
SQL connection was opened.
