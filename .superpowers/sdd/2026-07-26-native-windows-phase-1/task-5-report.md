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
