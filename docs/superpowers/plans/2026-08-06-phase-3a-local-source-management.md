# Phase 3A local source management and searchable content implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the first useful local corpus slice: SQL-authoritative source roots, immutable retained revisions, in-process UTF-8 indexing, durable deferred activities, and a truthful Sources/Indexing operator experience.

**Architecture:** Source roots, scan requests, retained source revisions, artifacts and activity plans are durable SQL records. A local reconciliation service enumerates authorised NTFS roots, writes checksum-verified bytes to an app-owned content store, and plans either the existing in-process text pipeline or an explicit deferred activity. The existing opaque executor/result boundary is represented only by an execution-class descriptor; no native process, executor activation or GPU admission is introduced.

**Tech Stack:** .NET 10, nullable C#, ASP.NET Core Interactive Server/Blazor, EF Core SQL Server migrations, the existing SQL outbox and pipeline workers, deterministic token-hash embeddings, USearch, xUnit, and the existing native SQL integration/browser test harness.

## Global Constraints

- SQL Server remains authoritative; USearch is a rebuildable derived projection.
- The application remains loopback/local-only and no public executor mutation route is added.
- Source bytes are immutable, checksum-verified and stored outside the IIS deployment and SQL data roots.
- Phase 3A accepts only local NTFS roots on fixed drives; UNC paths, deployment/SQL/cache/secret roots, and reparse/link traversal are rejected by default.
- Classification uses file signatures first, extension second, and records `unknown` on disagreement.
- The first supported content set is UTF-8 `.txt`, Markdown, logs and common structured text up to 16 MiB; PDF, image, media, archive, unknown and unsupported code remain deferred or blocked.
- Activity idempotency is `(source_revision_id, activity_kind, processor_version, input_fingerprint)`.
- The only execution descriptors are `InProcess`, `DeferredCapability` and non-runnable `NativeExecutorLater`.
- Text extraction, chunking, deterministic embedding and index publication remain in-process.
- Do not implement or activate process start/stop, supervision, PIDs, termination evidence, runtime/driver probes, GPU admission changes, model/cache activation, external access or legacy/RabbitMQ/Docker/Vespa work.
- Save-only persists a held scan request; Save and scan releases the same request transactionally. A held request is represented by scan-control metadata and a future due time, never by inventing a seventh public pipeline Job state.
- Preserve the existing Phase 1 single-file registration contract and all Phase 2 recovery, scheduler and executor/result invariants.

---

## File map and batch boundaries

The batches below are deliberately independently testable. The source-root and
activity contracts are the foundation; the retained-byte scanner and pipeline
adapter are the first executable vertical slice; the operator UI and final
reconciliation evidence follow only after those contracts pass focused SQL
tests.

### Task 1: Domain and application contracts

**Files:**

- Create: `src/FluxKnowledge.Domain/Sources/ExecutionClass.cs`
- Create: `src/FluxKnowledge.Domain/Sources/SourceRootState.cs`
- Create: `src/FluxKnowledge.Domain/Sources/SourceScanRequestState.cs`
- Create: `src/FluxKnowledge.Domain/Sources/SourceActivityKind.cs`
- Create: `src/FluxKnowledge.Domain/Sources/SourceActivityState.cs`
- Create: `src/FluxKnowledge.Domain/Sources/SourceRootConfiguration.cs`
- Create: `src/FluxKnowledge.Domain/Sources/SourceScanRequest.cs`
- Create: `src/FluxKnowledge.Domain/Sources/SourceRevision.cs`
- Create: `src/FluxKnowledge.Domain/Sources/SourceArtifact.cs`
- Create: `src/FluxKnowledge.Domain/Sources/SourceActivity.cs`
- Create: `src/FluxKnowledge.Application/Contracts/SourceRootContracts.cs`
- Create: `src/FluxKnowledge.Application/Ports/ISourceRootStore.cs`
- Create: `src/FluxKnowledge.Application/Ports/ISourceActivityStore.cs`
- Create: `src/FluxKnowledge.Application/Ports/ISourceArtifactStore.cs`
- Create: `src/FluxKnowledge.Application/Ports/ISourceScanner.cs`
- Test: `tests/FluxKnowledge.Domain.Tests/Sources/SourceRootConfigurationTests.cs`
- Test: `tests/FluxKnowledge.Domain.Tests/Sources/SourceActivityTests.cs`

**Interfaces:**

- `ExecutionClass` has exactly `InProcess`, `DeferredCapability` and `NativeExecutorLater`.
- `SourceActivity.Create(...)` accepts `SourceRevisionId`, `SourceActivityKind`, `ExecutionClass`, `processorVersion`, `inputFingerprint`, `requiredCapability` and `reason`; it computes the canonical idempotency key from those values and rejects a runnable state for `NativeExecutorLater`.
- `SourceActivity.DeferUnsupported(reason)` and `SourceActivity.DeferPolicy(reason)` are terminal-for-now transitions; no elapsed-time transition returns them to `Pending`.
- `ISourceRootStore.CreateAsync(SourceRootCreateRequest, ScanStartIntent, CancellationToken)` returns a `SourceRootReceipt` containing the root id, scan request id, control-job id, outbox id and whether the request is held.
- `ISourceActivityStore.FindOrCreateAsync(SourceActivityDraft, CancellationToken)` is idempotent on the exact four-part key and returns the existing row without changing a completed projection.
- `ISourceArtifactStore.PutAsync(ReadOnlyMemory<byte>, SourceArtifactMetadata, CancellationToken)` returns a content-addressed `SourceArtifactReceipt`; writing the same SHA-256 is a no-op after checksum verification.

- [ ] **Step 1: Write failing domain tests** for invalid root state transitions, the three execution classes, exact activity-key stability, duplicate activity drafts, unsupported deferral, and rejection of a runnable `NativeExecutorLater` activity.
- [ ] **Step 2: Run the focused domain tests**

  ```powershell
  dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~Sources"
  ```

  Expected: the new tests fail because the source contracts do not exist.

- [ ] **Step 3: Implement the records and ports** with invariant checks for canonical paths, SHA-256 hashes, positive revisions, non-empty processor fingerprints and bounded reasons.
- [ ] **Step 4: Re-run the focused domain tests** and the existing pipeline/job domain tests.
- [ ] **Step 5: Commit the contract batch**

  ```powershell
  git add src/FluxKnowledge.Domain/Sources src/FluxKnowledge.Application/Contracts/SourceRootContracts.cs src/FluxKnowledge.Application/Ports/ISource*.cs tests/FluxKnowledge.Domain.Tests/Sources
  git commit -m "feat: define phase 3a source and deferred activity contracts"
  ```

### Task 2: SQL source-root, scan-control and capability persistence

**Files:**

- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceRootConfigurationEntity.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceScanRequestEntity.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceScanJobEntity.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceScanOutboxEntity.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceRevisionEntity.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceArtifactEntity.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceActivityEntity.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceCapabilityEntity.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContext.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Configurations/CanonicalSchemaConfigurations.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/20260806120000_AddPhase3ALocalSources.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/20260806120000_AddPhase3ALocalSources.Designer.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/FluxKnowledgeDbContextModelSnapshot.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Persistence/SourceSchemaMappingTests.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Persistence/SourceRootPersistenceTests.cs`

**Interfaces and persistence decisions:**

- `SourceRootConfigurationEntity` stores canonical path, display name, enabled/paused state, recursion, JSON-serialised include/exclude rules, follow-links flag, maximum bytes, allowed classifications, crawl mode, cadence, last-scan evidence, permission/health evidence, configuration revision and rowversion. Canonical path is unique.
- `SourceScanRequestEntity` stores root id, request kind, requested actor, requested time, `IsReleased`, release time, state, counts and audit evidence. Save-only and Save and scan share one request row.
- `SourceScanJobEntity` and `SourceScanOutboxEntity` are control-plane records for root scans. They reuse the existing durable claim/outbox field conventions but do not pollute the six public pipeline `Job` states or expose a public executor route. A held request uses the canonical far-future due value; Save and scan changes both due values to the committed release time.
- `SourceRevisionEntity` stores stable source identity, root id, parent revision id, revision number, content hash, canonical path provenance, classification, extension, size/timestamps, discovery evidence and suppression/retention fields.
- `SourceArtifactEntity` stores the content hash, immutable store-relative path, byte length, checksum verification time and retention references. The app-owned root is configured separately and cannot be under deployment or SQL data directories.
- `SourceActivityEntity` stores the exact key components, execution class, required capability, state, reason, processor fingerprint, attempt evidence, source revision and optional resulting pipeline record. A unique index covers the four-part idempotency key.
- `SourceCapabilityEntity` is a SQL-visible registry of processor kind/version, accepted classifications, output contract, fingerprint, readiness and registration audit. Phase 3A registers only the in-process text/metadata capability; it never registers a native executor as runnable.
- All foreign keys use restrict/no-cascade semantics for immutable source revisions and artifacts. Physical cleanup is not part of this migration.

- [ ] **Step 1: Add failing mapping and transaction tests** for unique canonical roots, immutable revision hashes, activity-key uniqueness, held versus released scan control, and the no-cascade rule.
- [ ] **Step 2: Run the native SQL mapping tests**

  ```powershell
  dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SourceSchemaMapping|FullyQualifiedName~SourceRootPersistence"
  ```

  Expected: failure until the entities and migration are present.

- [ ] **Step 3: Add entities, DbSets and configurations** with explicit SQL lengths, binary/hash collations, rowversions and the indexes described above.
- [ ] **Step 4: Generate the migration from the native SQL project** and inspect its operations; it must only add Phase 3A tables/indexes/constraints and must not alter GPU, executor, legacy or Docker tables.
- [ ] **Step 5: Run mapping, restore and persistence tests** against the disposable native SQL fixture and verify the held request cannot be claimed before release.
- [ ] **Step 6: Commit the persistence batch**

  ```powershell
  git add src/FluxKnowledge.Infrastructure.SqlServer/Persistence tests/FluxKnowledge.Integration.Tests/Persistence
  git commit -m "feat: persist phase 3a source roots and activities"
  ```

### Task 3: Root validation, transactional save and scan-control service

**Files:**

- Create: `src/FluxKnowledge.Integrations/Files/SourceRootPathPolicy.cs`
- Create: `src/FluxKnowledge.Application/Sources/SourceRootService.cs`
- Create: `src/FluxKnowledge.Application/Sources/SourceScanControlService.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceRootStore.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceActivityStore.cs`
- Modify: `src/FluxKnowledge.Web/WebHostComposition.cs`
- Test: `tests/FluxKnowledge.Domain.Tests/Files/SourceRootPathPolicyTests.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Persistence/SourceRootTransactionTests.cs`

**Interfaces:**

- `SourceRootPathPolicy.ValidateAndCanonicalise(SourceRootCreateRequest)` rejects missing/inaccessible paths, UNC paths, reparse traversal, non-local roots, deployment/SQL/cache/secret locations and paths outside the fixed-drive policy. It returns canonical path, checked physical identity and sanitised permission evidence.
- `SourceRootService.CreateAsync(request, ScanStartIntent.SaveOnly|SaveAndScan, cancellationToken)` validates first, then executes one serializable SQL transaction that creates the root, scan request, control job and outbox. Save-only leaves `IsReleased=false`; Save and scan sets `IsReleased=true` and uses one committed release time. Repeated submits with the same canonical path and configuration fingerprint return the existing receipt.
- `SourceScanControlService.ReleaseAsync(rootId, requestId, actor, cancellationToken)` is idempotent, updates both control due values and emits the existing local outbox wake signal only after commit.
- No REST or MCP mutation endpoint is added; the service is called by the loopback Blazor operator component.

- [ ] **Step 1: Write failing policy and transaction tests** for path rejection, permission evidence, save-only hold, save-and-scan release, duplicate submit, rollback after an injected failure, and restart visibility from SQL.
- [ ] **Step 2: Run the focused tests** and confirm failure before implementation.
- [ ] **Step 3: Implement path policy and the serializable store transaction**. Serialize effective include/exclude policy canonically so a configuration revision is audit-comparable.
- [ ] **Step 4: Implement save/release orchestration** and register the services in `WebHostComposition`.
- [ ] **Step 5: Run the focused native SQL tests** and verify a released request is the only one visible to the scan dispatcher.
- [ ] **Step 6: Commit the root-control batch**

  ```powershell
  git add src/FluxKnowledge.Application/Sources src/FluxKnowledge.Integrations/Files/SourceRootPathPolicy.cs src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceRootStore.cs src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceActivityStore.cs src/FluxKnowledge.Web/WebHostComposition.cs tests/FluxKnowledge.Domain.Tests/Files/SourceRootPathPolicyTests.cs tests/FluxKnowledge.Integration.Tests/Persistence/SourceRootTransactionTests.cs
  git commit -m "feat: add transactional local source root control"
  ```

### Task 4: Retained-byte store, classification and authoritative reconciliation

**Files:**

- Create: `src/FluxKnowledge.Integrations/Files/ContentAddressedSourceArtifactStore.cs`
- Create: `src/FluxKnowledge.Integrations/Files/LocalSourceEnumerator.cs`
- Create: `src/FluxKnowledge.Application/Sources/SourceClassifier.cs`
- Create: `src/FluxKnowledge.Application/Sources/SourceReconciliationService.cs`
- Create: `src/FluxKnowledge.Application/Sources/SourceScanWorker.cs`
- Modify: `src/FluxKnowledge.Integrations/FluxKnowledge.Integrations.csproj`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Workers/OutboxWorkerRegistration.cs`
- Modify: `src/FluxKnowledge.Web/WebHostComposition.cs`
- Test: `tests/FluxKnowledge.Domain.Tests/Sources/SourceClassifierTests.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Sources/SourceReconciliationIntegrationTests.cs`

**Interfaces and behaviour:**

- `ContentAddressedSourceArtifactStore` writes to `<configured-source-store>\sha256\<first-two-hex>\<full-hash>.bin` using a temporary sibling file, flushes, reopens and verifies the SHA-256 before an atomic rename. It never deletes an active/reference-counted artifact.
- `LocalSourceEnumerator` performs the authoritative recursive crawl with the persisted policy, skips reparse/link traversal by default, records permission errors, uses signature-first classification and produces deterministic relative-path ordering.
- The first slice accepts UTF-8 text with or without BOM in `.txt`, `.md`, `.markdown`, `.log`, `.csv`, `.tsv`, `.json`, `.xml`, `.yaml` and `.yml` up to 16 MiB. Code, PDF, image, audio, video, archive and unknown inputs create `DeferredCapability` or `DeferredPolicy` activities with a bounded reason and no text projection.
- `SourceReconciliationService` runs a 15-minute `PeriodicTimer` and a manual channel wake. A watcher interface may provide a coalesced hint later, but the scan always rereads SQL and the filesystem; no watcher event is authoritative.
- `SourceScanWorker` claims only released control requests, records per-root counts, writes immutable revisions/artifacts and plans activities. It suppresses unseen files according to retention policy without deleting source rows or active index references.

- [ ] **Step 1: Write failing tests** for signature-first classification, UTF-8/BOM decoding, 16 MiB boundary, binary deferral, checksum mismatch, atomic artifact placement, include/exclude ordering, reparse rejection, permission evidence, unchanged rescan, changed revision and unseen-file suppression.
- [ ] **Step 2: Run the focused integration tests** and confirm the scanner/artifact seams fail before implementation.
- [ ] **Step 3: Implement the content-addressed store and classifier** with bounded file reads and no path-only processing fallback.
- [ ] **Step 4: Implement deterministic enumeration and SQL reconciliation** with idempotent revision/activity creation.
- [ ] **Step 5: Register the control worker and 15-minute/manual hosted service**; keep it local and in-process.
- [ ] **Step 6: Run the source reconciliation integration matrix** against disposable SQL, including restart during scan and a failed partial artifact write.
- [ ] **Step 7: Commit the retained-source batch**

  ```powershell
  git add src/FluxKnowledge.Integrations src/FluxKnowledge.Application/Sources src/FluxKnowledge.Infrastructure.SqlServer/Workers/OutboxWorkerRegistration.cs src/FluxKnowledge.Web/WebHostComposition.cs tests/FluxKnowledge.Domain.Tests/Sources tests/FluxKnowledge.Integration.Tests/Sources
  git commit -m "feat: reconcile local roots into immutable source revisions"
  ```

### Task 5: Connect retained text revisions to the existing in-process pipeline

**Files:**

- Create: `src/FluxKnowledge.Application/Sources/RetainedTextActivityPlanner.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedTextRegistrationStore.cs`
- Create: `src/FluxKnowledge.Application/Ports/IRetainedSourceReader.cs`
- Modify: `src/FluxKnowledge.Application/Pipeline/RegisterUtf8FileHandler.cs`
- Modify: `src/FluxKnowledge.Application/Workers/ExtractUtf8StageWorker.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlPipelineStore.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/PipelineRecordEntity.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Configurations/CanonicalSchemaConfigurations.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContext.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Workers/OutboxWorkerRegistration.cs`
- Test: `tests/FluxKnowledge.Domain.Tests/Pipeline/RegisterUtf8FileHandlerTests.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Sources/RetainedTextPipelineIntegrationTests.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs`

**Interfaces and behaviour:**

- `IRetainedSourceReader.ReadUtf8Async(SourceRevisionId, CancellationToken)` reads the checksum-verified artifact, validates the recorded content hash and returns bytes/text; it is the preferred extraction input for Phase 3A.
- The existing single-file registration path remains compatible, but the registration transaction also links its pipeline record to the immutable `SourceRevisionEntity` when the source was discovered by a root scan.
- `RetainedTextActivityPlanner` maps only `InProcess` text/metadata activities to the existing Extract → Normalise → CanonicalIndex → Embed → Publish operations. Each standard pipeline dispatch remains fenced by the existing Job/outbox idempotency rules.
- `ExtractUtf8StageWorker` never trusts a changed path when a retained revision is present. A missing or checksum-invalid artifact fails the activity with a terminal operator-visible reason; it does not reread arbitrary bytes or silently replace the revision.
- Deferred activities remain in SQL and do not enter the GPU scheduler or executor boundary. A future capability replay creates the same-key activity only when no completed projection exists and appends a new projection.

- [ ] **Step 1: Add failing tests** for retained-byte extraction after the original path is renamed, checksum mismatch, unchanged rescan no-op, changed revision lineage, unsupported activity non-dispatch, exact-once deferred replay and SQL-to-USearch rebuild from retained pipeline state.
- [ ] **Step 2: Run the existing registration/stage/rebuild tests** to establish the pre-change baseline.
- [ ] **Step 3: Add the retained revision link and reader** without changing the six public Job states or GPU admission code.
- [ ] **Step 4: Route Phase 3A text activities through the existing in-process workers** and preserve Phase 1 single-file behaviour.
- [ ] **Step 5: Run Domain, native SQL integration and rebuild tests**; verify no partial candidate becomes active and no unsupported activity reaches a worker.
- [ ] **Step 6: Commit the text vertical slice**

  ```powershell
  git add src/FluxKnowledge.Application/Pipeline src/FluxKnowledge.Application/Workers/ExtractUtf8StageWorker.cs src/FluxKnowledge.Application/Sources/RetainedTextActivityPlanner.cs src/FluxKnowledge.Application/Ports/IRetainedSourceReader.cs src/FluxKnowledge.Infrastructure.SqlServer/Persistence src/FluxKnowledge.Infrastructure.SqlServer/Workers/OutboxWorkerRegistration.cs tests/FluxKnowledge.Domain.Tests/Pipeline tests/FluxKnowledge.Integration.Tests/Sources tests/FluxKnowledge.Integration.Tests/Indexing/SqlToUsearchRebuildTests.cs
  git commit -m "feat: index retained phase 3a text revisions in process"
  ```

### Task 6: Sources/Indexing operator experience and safe Overview diagnostics

**Files:**

- Create: `src/FluxKnowledge.Application/Contracts/SourceRootViewContracts.cs`
- Create: `src/FluxKnowledge.Web/Components/Pages/Sources.razor`
- Create: `src/FluxKnowledge.Web/Components/Pages/SourceRootDetail.razor`
- Create: `src/FluxKnowledge.Web/Components/Sources/SourceRootPageState.cs`
- Create: `src/FluxKnowledge.Web/Components/Sources/SourceRootProjectionReader.cs`
- Modify: `src/FluxKnowledge.Web/Components/Layout/NavMenu.razor`
- Modify: `src/FluxKnowledge.Web/Components/Pages/Overview.razor`
- Modify: `src/FluxKnowledge.Web/Components/Status/OverviewProjectionState.cs`
- Modify: `src/FluxKnowledge.Web/Components/Status/SqlProjectionReader.cs`
- Modify: `src/FluxKnowledge.Application/Contracts/StatusContracts.cs`
- Modify: `src/FluxKnowledge.Web/wwwroot/css/app.css`
- Test: `tests/FluxKnowledge.Web.Tests/Components/SourceRootProjectionTests.cs`
- Test: `tests/FluxKnowledge.Web.Tests/Components/OverviewProjectionTests.cs`
- Test: `tests/FluxKnowledge.Web.Tests/Browser/Phase3ASourceManagementBrowserTests.cs`

**Interfaces and behaviour:**

- `/sources` shows an empty state with **Add folder**, then a root list with name, canonical path, state, last scan, indexed/deferred/blocked/error counts and a detail link.
- The Add folder form validates path/permissions, recursion and effective include/exclude rules, renders a read-only preview, then calls `SourceRootService.CreateAsync` with explicit **Save** or **Save and scan**. It displays the durable held/runnable result and never calls an executor route.
- `/sources/{rootId}` shows scan progress, activity counts, deferred/blocked reasons, last reconciliation and a local **Reprocess deferred content** command that uses the capability registry and the same idempotency key.
- Overview summary cards show `Healthy`, `Recovering` or `Blocked` plus a short reason. Full generation/source identifiers move to a copyable diagnostic detail element and are never rendered as unbounded card text.
- `SqlProjectionReader` reads all counts from SQL on initial load and SignalR reconnect. Status events remain presentation-only.
- CSS wraps or truncates long diagnostic values and keeps the status grid usable below 40rem.

- [ ] **Step 1: Write failing component and browser tests** for empty state, add-folder validation, preview counts, Save versus Save and scan, deferred reasons, refresh/reconnect truthfulness and the absence of overflowing generation IDs.
- [ ] **Step 2: Run the focused Web tests** and confirm the new pages/contracts fail before implementation.
- [ ] **Step 3: Implement projection contracts/readers and the two pages** using the existing scoped status/reconnect pattern.
- [ ] **Step 4: Add the navigation link and Overview diagnostic presentation/CSS** without adding a public mutation endpoint.
- [ ] **Step 5: Run Web tests and the guarded browser slice** against a disposable local root; capture the sentinel phrase in the top ten with correct provenance.
- [ ] **Step 6: Commit the operator-experience batch**

  ```powershell
  git add src/FluxKnowledge.Application/Contracts/SourceRootViewContracts.cs src/FluxKnowledge.Web/Components src/FluxKnowledge.Web/wwwroot/css/app.css tests/FluxKnowledge.Web.Tests/Components tests/FluxKnowledge.Web.Tests/Browser/Phase3ASourceManagementBrowserTests.cs
  git commit -m "feat: add local sources indexing operator experience"
  ```

### Task 7: Deferred capability replay, restart reconciliation and invariant matrix

**Files:**

- Create: `src/FluxKnowledge.Application/Sources/DeferredActivityReplayService.cs`
- Create: `src/FluxKnowledge.Application/Sources/SourceCapabilityService.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/Sources/DeferredActivityReplayTests.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/Sources/SourceRestartRecoveryTests.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceActivityStore.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Workers/OutboxWorkerRegistration.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Indexing/DerivedIndexRecoveryIntegrationTests.cs`

**Interfaces and behaviour:**

- `SourceCapabilityService.RegisterAsync` accepts only an explicit processor descriptor and fingerprint. `NativeExecutorLater` is stored as non-runnable and cannot make a native adapter available.
- `DeferredActivityReplayService.ReplayAsync(capabilityId, rootId?, cancellationToken)` selects matching `DeferredCapability` activities in stable key order, creates one same-key dispatch per activity, and records an additive projection receipt. Existing valid canonical data is never replaced.
- Restart reconciliation reconstructs pending scan controls, running source activities and retained-artifact references from SQL. It never uses heartbeat age or elapsed time to free GPU capacity, requeue uncertain executor work or replace results.
- The integration matrix proves missing notifications, duplicate callbacks/receipts at the existing executor boundary, process-free restart, failed partial candidate placement, immutable SQL membership and no active-pointer mutation. No real executor process is started.

- [ ] **Step 1: Write failing replay/restart/invariant tests** for exact-once deferred replay, duplicate replay requests, capability mismatch, restart during scan, lost wake, invalid artifact, SQL-to-USearch rebuild and no-GPU/process activation.
- [ ] **Step 2: Run the focused native SQL matrix** and confirm the new cases fail before implementation.
- [ ] **Step 3: Implement capability registration and same-key deferred replay** with additive projection fencing.
- [ ] **Step 4: Implement startup reconciliation of scan controls, activities and artifact references**; keep executor/capacity reconciliation unchanged.
- [ ] **Step 5: Run the combined Domain, Integration and Web native suites** with legacy/Docker/Python tests excluded.
- [ ] **Step 6: Commit the invariant/replay batch**

  ```powershell
  git add src/FluxKnowledge.Application/Sources src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceActivityStore.cs src/FluxKnowledge.Infrastructure.SqlServer/Workers/OutboxWorkerRegistration.cs tests/FluxKnowledge.Integration.Tests/Sources tests/FluxKnowledge.Integration.Tests/Indexing/DerivedIndexRecoveryIntegrationTests.cs
  git commit -m "test: prove phase 3a replay and restart invariants"
  ```

### Task 8: Closeout evidence and roadmap update

**Files:**

- Create: `docs/operations/native-windows-phase-3a-source-management-validation.md`
- Modify: `docs/roadmap.md`
- Modify: `docs/architecture.md`
- Modify: `docs/superpowers/specs/2026-07-26-native-windows-replacement-design.md` only if implementation evidence requires a clarified contract; do not broaden the approved scope.

- [ ] **Step 1: Run the focused verification matrix**

  ```powershell
  dotnet build FluxKnowledge.slnx --configuration Release -warnaserror
  dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~Sources|FullyQualifiedName~Pipeline|FullyQualifiedName~Indexing"
  dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~Sources|FullyQualifiedName~Indexing|FullyQualifiedName~Persistence|FullyQualifiedName~Workers"
  dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~SourceRoot|FullyQualifiedName~Overview|FullyQualifiedName~Browser"
  ```

- [ ] **Step 2: Validate the local disposable SQL/IIS checkpoint only if separately authorised**. Record migration id, readiness, source-root add/save/scan, sentinel search, deferred counts, restart reconciliation, active-generation checksum and rollback evidence. Do not deploy or restart IIS as part of this plan without a separate operational approval.
- [ ] **Step 3: Write the validation record** with each Phase 3A acceptance criterion marked passed, failed or not run, and explicitly record that no process/PID/runtime/GPU/model/external/legacy action occurred.
- [ ] **Step 4: Update roadmap progress and remaining work** only from fresh evidence. Keep Phase 3B/3C, native process management, model adapters, external access and legacy retirement separately planned.
- [ ] **Step 5: Run `git diff --check`, inspect the migration diff and complete a whole-branch review** for scope creep, SQL authority, idempotency, privacy, path safety, no partial publication and the separate process-management gate.
- [ ] **Step 6: Commit the evidence batch**

  ```powershell
  git add docs/operations/native-windows-phase-3a-source-management-validation.md docs/roadmap.md docs/architecture.md docs/superpowers/specs/2026-07-26-native-windows-replacement-design.md
  git commit -m "docs: record phase 3a local source validation"
  ```

## Acceptance traceability

| Approved Phase 3A requirement | Plan coverage |
| --- | --- |
| SQL-authoritative roots and transactional Save/Save and scan | Tasks 2–3 |
| Immutable source bytes, hashes, provenance and revisions | Tasks 2 and 4 |
| Signature-first classification and UTF-8 usefulness | Tasks 4–5 |
| Durable activity states, exact idempotency and deferred replay | Tasks 1, 2, 5 and 7 |
| Restart-safe reconciliation and SQL-to-USearch rebuild | Tasks 4, 5 and 7 |
| Sources/Indexing UI and truthful Overview diagnostics | Task 6 |
| No public executor mutation route | Tasks 3 and 6 |
| No process management, PIDs, probes, GPU admission or executor activation | Global constraints; Tasks 5 and 7 invariant tests |
| No external, legacy, RabbitMQ, Docker or Vespa action | Global constraints; Task 8 evidence |

## Self-review checklist

- The plan covers every Phase 3A acceptance item, including unseen-file suppression, restart, rebuild, no partial publication, deferred replay and responsive diagnostics.
- The control-plane scan job/outbox is separate from public pipeline Job states, so Save-only does not invent a seventh public state or accidentally claim work.
- The existing opaque executor/result boundary is referenced only as a future seam; no process-management design is hidden in source ingestion.
- Every code batch has focused failing tests, a concrete command, a passing-test checkpoint and a commit boundary.
- The plan contains no model/GPU execution, process start/stop, supervision, PID/termination evidence, runtime/driver probe, external-access, legacy or deployment step.

