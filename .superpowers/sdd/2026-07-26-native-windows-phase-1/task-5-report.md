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

## Correction round 2 follow-up

Activation transitions now use serialisable isolation. The active reader
rechecks the SQL pointer after an out-of-lock native open and before query use,
discarding a stale opened handle. Immutable placement handles a concurrent
`Directory.Move` collision by validating/reusing the compatible winner and
cleaning only its staging directory. Root resolution walks to the nearest
existing ancestor and resolves its reparse target before reconstructing an
absent configured leaf. A non-SQL concurrent-placement test passed (1/1).

## Regression matrix expansion

Added EF composite-key/FK mapping coverage for `IndexGenerationVectors`,
updated the guarded native schema table expectation to include it, and added a
guarded two-current-source corpus assertion proving that the next active
generation retains all canonical vector IDs. The non-SQL schema mapping subset
passed 7/7. The guarded corpus test remains source-complete but skipped without
the explicit disposable SQL connection.

Additional guarded regression source covers deleted-latest revision eligibility
and repeated active-generation fixture cleanup. Both compile and skip locally
without the disposable SQL connection; no database was opened.

## Correction round 3: stale and replay evidence

Completed source coverage for the requested publication and reader races:

- A guarded disposable-SQL test builds a first-corpus candidate, publishes a
  newer two-source corpus, then executes a real durable Publish transition for
  the prebuilt candidate. The transition completes its artefact/delivery but
  leaves the newer active pointer unchanged.
- A guarded disposable-SQL test executes a placed Publish transition twice with
  the same completed delivery. The replay returns the original artefact,
  reports `ExistingTransition`, keeps the active pointer, and verifies the
  generation membership has no duplicate vector IDs.
- A deterministic non-SQL test opens generation one, changes the pointer to
  generation two, blocks the replacement load, and disposes the reader before
  release. The in-flight replacement and all later searches throw
  `ObjectDisposedException`; no empty or wrong-generation result is returned.

Fresh focused evidence:

```powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~UsearchGenerationTests|FullyQualifiedName~SqlToUsearchRebuildTests"
dotnet build FluxKnowledge.slnx --configuration Release --no-restore
git diff --check
```

The focused suite passed 4/4 non-SQL tests and skipped 6/6 guarded native-SQL
tests because `FLUXKNOWLEDGE_TEST_SQL_CONNECTION` remains unset. The Release
build passed with zero warnings and errors; `git diff --check` passed. No SQL
connection, target migration, deployment, restart, model/runtime or GPU asset
was touched.

## Final migration behaviour evidence

Added guarded native-SQL migration behaviour coverage using a second uniquely
generated `FluxKnowledge_Phase1Tests_<guid>` catalogue. The fixture applies
only `20260726221653_EnforceCanonicalSqlSafety`, seeds an origin
`IndexGeneration` and stable `Vector`, then applies
`20260726235718_AddIndexGenerationMembership` and asserts the backfilled
membership. It adds a second active snapshot-only membership and attempts the
actual migrator Down path; SQL Server must raise error 51000 and retain the
membership table/data.

Fresh focused evidence:

```powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~SchemaMappingTests|FullyQualifiedName~Membership_migration_backfills_origins"
dotnet build FluxKnowledge.slnx --configuration Release --no-restore
git diff --check
```

The non-SQL schema subset passed 7/7. The one native migration test compiled
and skipped because `FLUXKNOWLEDGE_TEST_SQL_CONNECTION` is unset. The Release
build passed with zero warnings and errors; `git diff --check` passed. No SQL
connection, target catalogue, migration, deployment, restart, I: action,
model/runtime or GPU asset was touched.
