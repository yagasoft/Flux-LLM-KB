# Phase 2 unplaced-draft recovery projection implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow a valid active USearch generation to remain healthy when SQL also
contains a durably proven, inactive unplaced Embed draft, while rejecting every
near-miss row as a terminal configuration fault.

**Architecture:** Keep the recovery interface and active-membership validation
unchanged. Extend the serializable SQL snapshot so it separates placed path
references from recognised draft provenance; the empty path is omitted only
after an exact lifecycle proof. Centralise the Embed draft sentinel values in
the application layer so the writer and SQL recovery reader cannot silently
drift.

**Tech Stack:** .NET, C#, EF Core SQL Server, USearch, xUnit integration tests.

## Global constraints

- SQL remains authoritative; no migration, data repair, generated-pointer change
  or active-pointer update is part of this correction.
- A failed classification is `ConfigurationInvalid`, never retryable, and causes
  no filesystem or SQL mutation.
- Preserve active, placed SQL-referenced and recognised unplaced records; cleanup
  remains limited to aged direct children of `staging` and `quarantine`.
- No model/GPU work, external access, legacy/RabbitMQ action, configuration
  change or deployment change is part of implementation.
- Use test-first changes and run with the local SQL test connection. Do not use
  Flux tools, `dotnet format`, destructive Git recovery, or cache-prune commands.

---

### Task 1: Prove the real pipeline draft behaviour in recovery tests

**Files:**
- Modify: `tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs`

**Interfaces:**
- Consumes: `PipelineEnvironment.CreateAsync`, `AddAndPumpAsync`,
  `SqlDerivedIndexRecoveryStore`, `DerivedIndexRecoveryCoordinator`.
- Produces: real Embed-to-Publish integration coverage for a valid non-zero and
  zero-vector unplaced draft.

- [x] **Step 1: Write the failing non-zero draft test**

  Add `Valid_active_generation_with_a_recognised_unplaced_embed_draft_remains_healthy`.
  Build the existing `PipelineEnvironment` with `"first source"`, locate the
  inactive generation whose path is `string.Empty`, then run a recovery
  coordinator using the environment's SQL factory and index root. Assert the
  active pointer and active path are unchanged, the coordinator is `Healthy`,
  and `ReferencedIndexPaths` does not contain the empty string.

  The production change this catches is restoring the old
  `IndexGenerations.Select(generation => generation.IndexPath)` projection: that
  must make the coordinator terminal with `ConfigurationInvalid`.

- [x] **Step 2: Run the test to verify RED**

  Run:

  ```powershell
  $env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
  dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~Valid_active_generation_with_a_recognised_unplaced_embed_draft_remains_healthy' --nologo
  ```

  Expected: fail with `OperatorActionRequired` / `ConfigurationInvalid` because
  the current recovery snapshot includes the draft's empty path.

- [x] **Step 3: Add the zero-vector lifecycle test before implementation**

  Add `Zero_vector_unplaced_embed_draft_with_valid_provenance_remains_healthy`.
  Start with a non-empty source to establish a valid active generation, pump an
  empty source through the existing pipeline, and assert the second record's
  draft has `VectorCount == 0`, no vector references, no immutable membership,
  and remains excluded from `ReferencedIndexPaths` while its generation ID
  remains referenced. Run the same coordinator and assert `Healthy` without
  SQL or filesystem mutation.

- [x] **Step 4: Add a local recovery-provider helper in the test file**

  Register the environment factory as `IDbContextFactory<FluxKnowledgeDbContext>`,
  `SqlDerivedIndexRecoveryStore`, scoped `SqlPipelineStore` /
  `IIndexGenerationStore`, `UsearchIndexOptions.FromConfiguredRoot(root)`,
  `UsearchGenerationValidator`, `UsearchGenerationBuilder`,
  `DerivedIndexFileSystem`, `TimeProvider.System`, and
  `DerivedIndexRecoveryCoordinator`. Do not mock the recovery store, builder or
  filesystem for these tests.

### Task 2: Centralise the Embed-draft sentinel contract

**Files:**
- Create: `src/FluxKnowledge.Application/Indexing/EmbedDraftDefaults.cs`
- Modify: `src/FluxKnowledge.Application/Indexing/EmbedStageWorker.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlStageTransitionStore.cs`

**Interfaces:**
- Produces `EmbedDraftDefaults` with `ModelFingerprint`, `Dimensions`,
  `MetadataChecksum`, `ArtifactContentType`, and `EmptyArtifactContentHash`.
- Consumed by the Embed writer, SQL stage-transition writer and recovery SQL
  reader in Task 3.

- [x] **Step 1: Add the shared defaults**

  Define the public application-layer contract with these values:

  ```csharp
  public const string ModelFingerprint = "deterministic-tokenhash-v1:256";
  public const int Dimensions = 256;
  public const string ArtifactContentType = "application/vnd.fluxknowledge.embedding-set+binary";
  public static readonly string MetadataChecksum = new('0', 64);
  public static readonly string EmptyArtifactContentHash =
      Convert.ToHexStringLower(SHA256.HashData([]));
  ```

- [x] **Step 2: Replace the writer's duplicated empty defaults**

  Make `EmbedStageWorker` use `EmbedDraftDefaults.ModelFingerprint` for an
  empty vector set and `EmbedDraftDefaults.ArtifactContentType` for the Embed
  artefact. Make `SqlStageTransitionStore.WriteIndexingOutput` use
  `EmbedDraftDefaults.Dimensions` and `EmbedDraftDefaults.MetadataChecksum`.
  Preserve all non-empty vector behaviour.

- [x] **Step 3: Re-run the RED tests**

  Re-run both Task 1 tests. They must still fail at recovery classification;
  changing constants alone must not mask the missing projection behaviour.

### Task 3: Classify drafts inside the serializable recovery snapshot

**Files:**
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlDerivedIndexRecoveryStore.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs`

**Interfaces:**
- Consumes: `EmbedDraftDefaults`, `PipelineStage`, `PipelineOperations`,
  `PublicJobState`, and the existing `DerivedIndexRecoverySqlSnapshot`.
- Produces: `ReferencedIndexPaths` containing only placed paths and
  `ReferencedGenerationIds` retaining recognised draft IDs.

- [x] **Step 1: Read all generation descriptors and candidate provenance under the existing serializable transaction**

  Introduce private no-tracking projections for generation rows, draft vectors
  joined through their text chunks and canonical artefacts, matching Embed
  artefacts, pipeline records, jobs, and outbox messages. Restrict the extra
  queries to exact-empty-path candidate IDs; keep all reads in
  `ReadActiveWithinTransactionAsync` before its commit.

- [x] **Step 2: Implement the fail-closed predicate**

  A candidate is recognised only when it is not active; has the exact empty
  path, null validation timestamp and shared zero checksum; has no immutable
  membership; has exactly one matching Embed artefact with the shared content
  type and canonical `Guid.ToString("D")`; belongs to a record at Publish; has
  one completed Embed job whose own outbox has a non-null durable dispatch
  completion; plus successor Publish job/outbox pair with dispatch generation
  incremented by one. A queued or processing Publish job requires an
  undispatched outbox; a completed or failed job requires a durably dispatched
  outbox.

  For non-zero drafts, require vector-reference count to equal `VectorCount`,
  every vector's fingerprint/dimensions/source revision to match, and every
  vector's canonical artefact to be the Embed artefact's record and revision.
  For zero drafts, require no vector references, no canonical chunks, the shared
  empty defaults, and `EmptyArtifactContentHash`.

  Throw `InvalidOperationException` before returning the snapshot if any empty
  row fails the predicate. That preserves the coordinator's terminal
  `ConfigurationInvalid` classification without exposing raw paths or data.

- [x] **Step 3: Build the two reference sets correctly**

  Include every non-empty `IndexPath` in the case-insensitive path set, including
  whitespace and malformed values so existing filesystem validation rejects
  them. Omit only recognised exact-empty drafts. Union recognised draft IDs into
  `ReferencedGenerationIds`, including a valid zero-vector draft that has no
  vector foreign-key reference.

- [x] **Step 4: Run the Task 1 tests to verify GREEN**

  Run the two focused tests from Task 1. Expected: both pass; the coordinator
  is `Healthy`, no replacement path is made, and the active pointer stays
  unchanged.

### Task 4: Lock down near-miss and existing recovery safety behaviour

**Files:**
- Modify: `tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Indexing/DerivedIndexRecoveryIntegrationTests.cs`

**Interfaces:**
- Consumes: real pipeline-generated draft data and the coordinator's existing
  terminal failure policy.
- Produces: a one-condition-at-a-time negative matrix with no-mutation checks.

- [x] **Step 1: Add the negative matrix using a real valid active generation**

  For each isolated mutation of a genuine unplaced draft — active pointer,
  whitespace path, validation timestamp, non-sentinel checksum, immutable
  membership, missing/mismatched Embed provenance, contradictory job/outbox
  dispatch evidence, vector count/fingerprint/dimensions/deletion or
  source-revision mismatch, and missing zero-vector evidence — run the real
  coordinator and assert `OperatorActionRequired` with
  `ConfigurationInvalid`.

- [x] **Step 2: Assert the safety boundaries for each matrix case**

  Capture the active pointer, active generation path and all SQL evidence read
  by the projection (state, generations, records, artefacts, chunks, vectors,
  immutable membership, jobs and outbox rows) before the run. Assert it is
  unchanged afterwards; assert no recovery staging or quarantine directory is
  created and no retry time is scheduled. Retain the existing invalid non-active
  path, SQL checksum, ACL, SQL schema/catalogue and reparse-path tests unchanged.

- [x] **Step 3: Run focused recovery verification**

  Run:

  ```powershell
  $env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
  dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~DerivedIndexRecoveryIntegrationTests|FullyQualifiedName~SqlToUsearchRebuildTests' --nologo
  ```

  Expected: all recovery and SQL-to-USearch tests pass with no warnings.

### Task 5: Verify, review and complete the explicitly authorised native closeout

**Files:**
- Verify: solution and focused test projects only; no deployment files or
  configuration changes.

- [x] **Step 1: Run repository verification**

  Run locked restore, Release `-warnaserror` build, non-browser domain,
  integration and web test suites, the guarded browser slice, and
  `git diff --check`. Record fresh output.

- [x] **Step 2: Review the completed branch**

  Review spec compliance, lifecycle predicate completeness, negative-matrix
  coverage, SQL transaction scope, no-mutation boundaries, cleanup containment,
  generated-output exclusion, and scope creep. Obtain the required pre-live
  independent gate before deployment.

- [x] **Step 3: Commit, merge and purge through the repository closeout script**

  Commit the approved specification and implementation on the native feature
  branch. Use `scripts/dev/complete-feature.ps1`; do not manually substitute
  its merge/purge sequence. Do not touch either protected legacy Python /
  RabbitMQ branch or worktree.

- [x] **Step 4: Deploy and validate the loopback IIS checkpoint**

  Preserve the exact target-only production settings bytes/hash, loopback
  binding, rollback payload and current SQL active pointer. Deploy only after
  the gate approves; verify `/`, `/health/live`, `/health/ready` and
  `/api/index-health`, plus read-only SQL and filesystem invariants proving the
  recognised draft remains healthy. Then perform the authorised controlled
  missing-metadata recovery check without an IIS restart: the active pointer and
  generation count must remain unchanged while only the derived path may be
  safely replaced, readiness and ANN search must return 200, and sanitised audit
  evidence must show detection, rebuild, cleanup and healthy completion. Roll
  back before cleanup if any probe fails.
