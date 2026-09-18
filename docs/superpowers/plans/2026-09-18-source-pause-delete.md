# Source pause, resume and delete implementation plan

> For agentic workers: use `superpowers:executing-plans`. Use one Terra implementation
> owner for each coherent slice and Astra only for architecture/invariant escalation
> and the production gate. Do not split serial shared-file edits among agents.

**Goal:** UI-triggered source-wide Pause/Resume and complete source deletion,
including owned SQL records and app-managed files.

**Architecture:** Extend existing root state, native fenced commands and Blazor
pages. Reuse the reconciliation loop for a small resumable deletion operation,
with shared-data checks and native no-follow file removal.

**Tech stack:** Existing .NET, SQL Server/EF Core, Blazor, embedded USearch and
Windows handle-relative filesystem helper; no new dependencies or model assets.

**Spec:** [Source pause, resume and delete](../specs/2026-09-18-source-pause-delete-design.md).

## Execution boundary

- Implementation approval has been received. The delivery remains in its dedicated
  worktree until Release/full-suite verification, incremental migration deployment
  and the authorised disposable-source live validation have passed.
- Work on `codex/source-delete-plan` in its dedicated worktree, starting from
  `8306fb0`; reconcile newer main commits before implementation.
- Source originals remain read-only; never touch `J:\Models` or acquire models.
- Scope is local filesystem sources. Explicitly refuse deletion of Outlook-bound
  roots or roots with external/GPU execution ownership before changing their state.
- Preserve ordinary processor/pipeline terminal ownership and idempotency. The only
  terminal-record removal is the explicitly authorised source deletion.
- No backup, undo, generic workflow engine, new pipeline stage, manual update,
  external executor, PDF/VSDX/OCR work or unrelated refactor.
- Two delivery-bearing tasks below; do not create separate milestones for fixtures,
  scaffolding, individual tests or evidence files. Budget checkpoint: after these
  two tasks, both UI paths must work end to end in the disposable environment.

## Existing seams to reuse

| Responsibility | Existing files |
| --- | --- |
| Root commands and fencing | `src/FluxKnowledge.Application/IntegrationV1/Corpus/NativeCorpusCommandService.cs`; `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlNativeCorpusActionStore.cs`; `SqlNativeOperationStore.cs` in that directory |
| Scan/watch/restart | `src/FluxKnowledge.Application/Sources/SourceReconciliationService.cs`; `SourceScanWorker.cs`; SQL `SqlSourceScanStore.cs`, `SqlSourceRootWatchStore.cs`, `SqlRetainedTextRegistrationStore.cs` |
| Execution admission | SQL `SqlOutboxStore.cs`, `SqlJobClaimStore.cs`, `SqlRetainedProcessorBranchStore.cs`, `SqlStageTransitionStore.cs`, `SqlGpuSchedulerStore.cs`; `src/FluxKnowledge.Infrastructure.SqlServer/Workers/OutboxPumpService.cs` |
| Retained bytes | `src/FluxKnowledge.Integrations/Files/ContentAddressedSourceArtifactStore.cs`; SQL `SqlRetainedArtifactWriter.cs` |
| Index and safe filesystem | `src/FluxKnowledge.Infrastructure.Usearch/UsearchGenerationBuilder.cs`, `UsearchAnnIndex.cs`, `DerivedIndexFileSystem.cs`; SQL `SqlPipelineStore.cs`, `SqlDerivedIndexRecoveryStore.cs`; `src/FluxKnowledge.Integrations/Windows/NativeGoLive/HandleRelativeNativeFileSystem.cs` |
| User controls | `src/FluxKnowledge.Web/Components/Pages/Sources.razor`, `SourceRootDetail.razor`; `Components/Sources/SourceRootProjectionReader.cs` and page-state classes |
| Visibility during deletion | SQL `SqlCorpusProjectionReader.cs`, `SqlNativeV1ProjectionReader.cs`, `SqlLocalRetainedDetailReader.cs`; `src/FluxKnowledge.Infrastructure.SqlServer/Search/SqlFullTextSearch.cs`, `SqlSearchHydrator.cs`; retained C# detail/search readers |
| Schema | `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContext.cs`, `Configurations/CanonicalSchemaConfigurations.cs`, `Entities/`, `Migrations/` |

SQL filenames abbreviated above all live under
`src/FluxKnowledge.Infrastructure.SqlServer/Persistence/`.

## Task 1: Pause/resume through UI and native commands

Deliverable: a populated source can be paused from either page, remains searchable,
survives a host restart, and resumes queued work without duplicate processing.

Create `src/FluxKnowledge.Application/Sources/SourceLifecycleService.cs` for the
shared UI/application entry, a small execution-gate port under Application/Ports,
and its SQL implementation alongside the existing stores. Keep root-state and
claim predicates in one internal SQL helper where practical. Reuse the current
Paused value; no new persistence is required for this task.

Proposed execution interface (used by task 2 as well):

```csharp
public interface ISourceExecutionGate
{
    ValueTask<IAsyncDisposable?> TryEnterAsync(Guid rootId, CancellationToken token);
    ValueTask<IAsyncDisposable?> TryDrainAsync(Guid rootId, CancellationToken token);
}
```

`TryEnterAsync` holds shared root execution ownership and checks Enabled before
claiming; `TryDrainAsync` holds exclusive ownership only when running execution
has exited. Neither treats elapsed time as ownership release. Hold the execution
lease through actual IO and the worker's final fenced SQL transition.

- [ ] Add meaningful failing tests in new
  `tests/FluxKnowledge.Integration.Tests/Sources/SourceLifecycleIntegrationTests.cs`
  and extend `IntegrationV1/NativeCorpusActionMatrixTests.cs`. Seed two roots and
  retain the second root as an unchanged control. Assert the matrix below.
- [ ] Run the focused tests and confirm failure is missing lifecycle behaviour.
- [ ] Add `root_pause` and `root_resume` to native action validation, effects and
  commit handling. Reuse preview/confirmation/idempotency; keep compatibility
  actions under the same state guard. Resume preserves held-request semantics,
  coalesces exactly one reconciliation and wakes previously runnable work.
- [ ] Gate scan/watch claims, dispatch/job claims, retained/force claims,
  promotion/restart and GPU admission. Allow an already claimed operation to
  finish and persist its successor without admitting that successor while paused.
  Integrate the execution lease at the actual worker entry points.
- [ ] Fix the existing wake path: `SourceReconciliationService` currently does not
  necessarily call `RunAvailableAsync` for a wake without a released watcher batch.
  Pump resumed work promptly, preserving the existing deployment-validation hold.
- [ ] Add Pause/Resume buttons to both pages via a small shared
  `Components/Sources/SourceLifecycleControls.razor`. Preserve keyboard focus,
  disable double submission, announce status and show draining active count.
- [ ] Add component tests in `tests/FluxKnowledge.Web.Tests/Components/SourceLifecycleTests.cs`
  and browser tests in `Browser/SourceLifecycleBrowserTests.cs`; click the actual
  controls against disposable SQL, then refresh and verify persisted state.
- [ ] Run the focused matrix, review the slice once and commit implementation/tests.

Pause/resume assertions:

```text
pause + queued scan/job -> same queued rows and attempt counts; zero new claims
pause racing a claim -> either already-owned work drains or the claim is refused
claimed stage finishes -> successor exists but is not dispatched while paused
paused root -> existing corpus/search hits remain; second root keeps progressing
restart while paused -> no admission; resume twice -> one coalesced scan, no replay
lease loss -> worker publication is fenced; timeout alone never proves drain
UI list/detail -> correct Pause/Resume button and durable result after refresh
```

Focused command after adding the tests:

```powershell
dotnet test FluxKnowledge.slnx -c Release --filter 'FullyQualifiedName~SourceLifecycle|FullyQualifiedName~NativeCorpusActionMatrix'
```

## Task 2: Complete delete through UI, SQL and physical storage

Deliverable: deleting one populated local source removes its complete owned graph
and eligible files, leaves a second source usable, and resumes safely after an
interrupted or failed cleanup. Re-adding the original folder creates a fresh source.

Create `Application/Sources/SourceDeletionCoordinator.cs`, a deletion-store port,
SQL `SqlSourceDeletionStore.cs`, and deletion entity/configuration files. Add one
migration with operation/cleanup tables, `IndexGenerations.RetiredAtUtc` and the
narrow C# trigger exception.
Add a small Integrations adapter around the existing handle-relative helper for
owned artifact/index removal. Reuse existing generation builder, validator, native
operation ledger and reconciliation loop rather than introducing parallel engines.

The coordinator exposes `RunOneAsync(CancellationToken)`; commands accept a
root ID, not a physical deletion path. Status exposes operation ID, phase, counts
and a bounded reason through the existing corpus query family and UI projections.
The source row remains Deleting until completion; a repeated delete resolves to
the same operation. Only the minimal receipt remains after success.

- [ ] Write failing `SourceDeletionIntegrationTests.cs` in Integration.Tests/Sources
  and `SourceDeletionFileTests.cs` in Integration.Tests/Operations. Seed a realistic
  owned graph with nested members, failed/deferred jobs, C# facts, and a second
  source sharing a blob and both current/historical mixed index generations.
- [ ] Add operation/cleanup persistence, `Deleting`, `root_delete` preview/commit
  and a bounded status projection. Preview estimates affected counts without
  mutation. Acceptance fences the root, blocks new work and persists deletion.
  Reject unsupported roots before acceptance. Pause/Resume/Sync aliases must not
  override Deleting.
- [ ] Exclude Deleting roots consistently from search, corpus, retained detail and
  code reads, including already-cached candidate hydration. Keep Paused roots
  visible. Do not rely solely on hiding a row in the Sources page.
- [ ] Implement drain and explicit root ownership selection under SQL locks.
  Follow foreign keys, reject cross-root dependencies, delete children first,
  and retain physical cleanup rows before discarding their original SQL owner.
  Retain shared native receipts/policies and independent knowledge notes/claims.
- [ ] Add the six trigger DELETE exceptions for the exact operation's root and
  purge phase, retaining all UPDATE/cross-root denials and constraint checks.
  Update expected trigger-definition hashes in `SqlRetainedProcessorBranchStore`
  and the existing retained-C# lifecycle/readiness tests together.
- [ ] Coordinate all retained writers across publication and SQL registration,
  including `SourceScanWorker` and streamed children via `SqlRetainedArtifactWriter`.
  Use a shared storage publication lease and exclusive cleanup lease; recheck
  surviving references under the latter. Do not rely on a lock released by Put.
- [ ] Hold shared recovery/publication ownership from normal index build/placement
  through SQL commit or registered disposal. Deletion takes exclusive ownership
  before final cleanup-target capture, survivor build and cutover. A late stale
  publisher must not place an untracked file after the deletion capture. Retain
  snapshot comparison at activation and the existing recovery exclusion.
- [ ] Build the survivor index from existing vector payloads. Retire every old
  generation containing deleted vectors: remove memberships/files, set
  `RetiredAtUtc`, clear its path and deny rebuild/reactivation. Preserve original
  generation IDs only where surviving FKs/provenance need them. Do not fabricate
  unplaced drafts or rewrite surviving vector payloads or terminal artifacts.
  Extend `SqlDerivedIndexRecoveryStore` to recognise retired descriptors separately;
  wait on genuinely active survivor ownership and delete unreferenced descriptors.
- [ ] Add zero-vector generation support to `UsearchGenerationBuilder`,
  `UsearchGenerationValidator`, `DerivedIndexRecoveryCoordinator` and the readiness
  validator: a valid metadata/checksum and zero physical entries replace the
  non-empty cosine probe for this case only. Use this when eligible vectors are
  empty but suppressed/draft canonical rows remain. Use the existing stricter
  empty-catalogue marker only after proving its three tables empty.
- [ ] Add explicit reader retirement to `UsearchAnnIndex`, including the null
  active-generation case; coordinate search readers before deleting old paths.
  Physically remove exact app-owned targets through pinned no-follow handles.
  Missing is success; sharing/permission errors stay pending; unsafe paths require
  attention. No backup or quarantine copy is a successful deletion.
- [ ] Pump one bounded deletion step on wake/tick/startup using the existing
  reconciliation loop. Persist phase/retry information, recover interrupted
  execution, and leave unrelated roots schedulable while cleanup is blocked.
  Honour the deployment-validation hold in this loop and reject lifecycle commits
  from UI/REST/MCP/CLI while held; the existing hold only gates hosted workers.
- [ ] Add Delete to both UI locations using the shared controls: one preview and
  confirmation, Cancel, inline progress/reason, Retry cleanup, and completion
  removal. Test double click, stale preview, reload mid-delete, and deleted links.
- [ ] Run the focused tests below. Review the whole invariant family once; escalate
  to Astra if ownership, shared-index or trigger assumptions cannot be met.
- [ ] Include the minimal incremental schema deployment support specified below
  in this delivery; it is a release prerequisite, not a third product milestone.
- [ ] Update `docs/architecture.md` and the affected roadmap entry to actual
  delivered behaviour, then commit the complete slice.

Delete acceptance matrix:

```text
populated local root -> all owned SQL descendants and exclusive physical files gone
second root/shared blob -> second root rows/search unchanged; shared bytes retained
mixed current + old indexes -> no deleted vectors remain in any retained index file
retired descriptor with survivor FK -> original evidence preserved; rebuild denied
only ineligible survivor vectors -> validated zero-vector index; old hits absent
last source -> SQL validated-empty state, native handle released, ready/search healthy
concurrent source execution -> no purge until actual drain; late publication fenced
shared blob writer between Put and registration -> no deleted/referenced-file race
concurrent index publisher -> stale candidate cannot reactivate deleted content
late stale placement -> publication barrier prevents new untracked deleted content
crash before/after SQL cutover + between file deletes -> same operation resumes
locked/unsafe/replaced path -> no false success and no deletion outside owned roots
C# deletion -> exact operation succeeds; ordinary UPDATE/DELETE and mixed roots fail
repeat request/cancel/stale preview -> idempotent result/no effect/fenced refusal
UI -> list and detail controls work, status survives reload, source disappears last
re-add same folder after completion -> new source ID and fresh jobs; originals unchanged
```

```powershell
dotnet test FluxKnowledge.slnx -c Release --filter 'FullyQualifiedName~SourceDeletion|FullyQualifiedName~SourceLifecycle|FullyQualifiedName~NativeCorpusActionMatrix|FullyQualifiedName~RetainedCsharpCodeLifecycleCorrection|FullyQualifiedName~SqlToUsearchRebuild|FullyQualifiedName~EmptyCatalogueReadiness'
```

## Whole-feature verification and handoff

- [ ] Inspect the final diff for scope, source ownership, privacy, query filtering
  and irreversible-delete recovery. Confirm both UI-triggered paths have browser
  evidence; use existing cached browser tooling and do not acquire model assets.
  Browser facts are opt-in, so an ordinary `dotnet test` pass alone is insufficient:

```powershell
pwsh -NoProfile -File scripts/dev/test-browser.ps1 -TestFilter 'FullyQualifiedName~SourceLifecycleBrowser|FullyQualifiedName~SourceDeletionBrowser'
```

The helper requires an existing local browser and will not download one. Use the
existing disposable SQL fixture; never point integration/browser tests at production.
Report executed and skipped test counts separately; required SQL/browser cases
must actually execute before the release gate.

- [ ] Run locked restore, Release build with warnings as errors and the full suite:

```powershell
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build --logger 'console;verbosity=minimal'
git diff --check
```

## Minimal incremental deployment support

Current gap: `scripts/deploy/update-native-iis-incremental.ps1` explicitly reports
`migrations = false` and supports no migration switch. Application-only rollback
would fail old trigger-hash readiness after this feature's schema update. Extend
this existing script and `incremental-iis-payload-swap.psm1` only as needed:

- [ ] Add explicit opt-in `-ApplyMigrations` to both PlanOnly and Apply. Without it,
  preserve the current application-only contract; refuse an incompatible candidate
  needing a schema change. The switch is proposed, not available in current code.
- [ ] PlanOnly remains read-only: report immutable candidate commit, exact prior
  migration history, this release's permitted migration IDs/script hashes, target
  schema, required existing permissions and rollback boundary. Reject unexpected
  pending migrations, trigger drift or insufficient permissions. Apply rechecks
  the same inputs under the existing deployment mutex; no grants or auto-bootstrap.
- [ ] Reuse the existing isolated EF migration-only pattern with an exact target,
  without starting the web host, providers or workers. Do not invoke the unrestricted
  `--apply-native-go-live-migrations` route. Keep connection details process-local
  and out of arguments, output and Git. Apply the reviewed feature migration in a
  transaction while the pool is stopped and the validation hold is owned.
- [ ] During candidate validation, keep all lifecycle commits and deletion cleanup
  held. Extend the existing unchanged-data checks to operation/cleanup tables,
  generation retirement and zero-vector state; readiness must also pass. Stop the
  candidate before any rollback and prove no new lifecycle state exists.
- [ ] For a failed candidate before hold release only, reverse this unused migration,
  verify exact prior migration history and old trigger hashes, then restore/start
  the old payload. If reversal or verification fails, retain the hold and stopped
  pool with an actionable failure. Remove the current unconditional hold release
  for this failure case. After release, use forward repair, never schema downgrade
  or source restoration. No Test backup; normal app-payload recovery remains.
- [ ] Extend `tests/native/incremental-iis-update-contract.ps1` and
  `incremental-iis-payload-swap.ps1`; add disposable-SQL migration coverage. Prove
  default no-migration behaviour, read-only planning, exact allowlist/permission
  refusal, successful held update, failure before/after migration, old-payload
  readiness after rollback, and retained hold when rollback cannot be proved.
  No production IIS operations in these tests.

```powershell
pwsh -NoProfile -File tests/native/incremental-iis-update-contract.ps1 -SourceRoot .
pwsh -NoProfile -File tests/native/incremental-iis-payload-swap.ps1 -SourceRoot .
dotnet test FluxKnowledge.slnx -c Release --filter 'FullyQualifiedName~SourceLifecycleMigration'
```

## Release and live verification runbook

Execute only after the user's implementation/deployment approval and the independent
production gate. No command in this section is to run during the plan-only turn.

1. Reconcile main, finish focused/Release/full/browser verification and commit a
   clean candidate. Resolve the exact live Test root by ID and canonical path;
   refuse ambiguity. It is currently paused: do not silently enable it during
   deployment. Record schema/trigger state, source-owned counts, app-managed file
   identities, search probes, and original-file count/length/mtime/SHA-256 in a
   private bounded evidence record, not source copies or public Git content.
2. Run the extended updater below from the committed feature worktree. Review its
   actual PlanOnly JSON, migration allowlist and rollback boundary before Apply.
   Obtain the independent production gate against that exact plan. Both commands
   use the same immutable SourceRoot and proposed migration opt-in:

   ```powershell
   pwsh -NoProfile -File scripts/deploy/update-native-iis-incremental.ps1 -SourceRoot . -ApplyMigrations -PlanOnly
   pwsh -NoProfile -File scripts/deploy/update-native-iis-incremental.ps1 -SourceRoot . -ApplyMigrations -Apply
   ```

   Retain exit status, commit, release directory, applied migration IDs and hold
   result. The script owns IIS stops/starts; never restart IIS manually. Require
   `/health/live`, `/health/ready` and `/api/index-health` success during held
   validation and after release. Stop on failure; no speculative live cleanup.
3. In the actual Sources/detail UI, confirm the paused state survives reload and
   existing content is searchable. Resume this source, verify one reconciliation
   and eligible work admission, then Pause while work is available. Wait for owned
   work to drain; verify two subsequent scheduler cycles admit no new scan/job/
   processor claims or attempts. Exercise the controls from both UI locations.
   Resume and allow normal scoped work to settle before taking the deletion baseline.
4. Delete through the UI. First cancel once and prove no effect, then confirm the
   exact root/path/counts. Reload during Deleting and observe persisted progress.
   Await completion; do not force state or delete files manually if cleanup blocks.
   Verify old-root owned SQL graph counts are zero, exclusive app-managed files
   and all old index files containing its vectors are absent, source-specific
   search/corpus/detail results are gone, and only the permitted minimal receipts
   or genuinely shared references remain. A success toast alone is not evidence.
5. Before re-adding, require healthy readiness/index probes and unchanged original
   folder fingerprint. If another live source exists, confirm its owned data and
   search remain intact without replaying it. Shared/mixed-index cases must already
   be proven in disposable tests even if live Test is the only source.
6. Re-add that exact original folder through the UI with its captured source
   settings. Let its single normal reconciliation finish; do not request an extra
   replay. Require a new source ID, fresh jobs and visible corpus/status from the
   new ownership graph, with no resurrection of old rows. Report actual indexed,
   deferred, blocked and error results, including any unchanged parser limitations.
   Recheck original-file hashes and all health probes. No PDF/VSDX/OCR fix is claimed.
7. Report fresh test totals, deployment JSON, both root IDs, deletion counts/file
   evidence, new scan status and preserved originals. On a live deletion failure,
   leave the operation safely Deleting with its reason and use scoped forward
   repair; never restore backups, restart manually, or replay unrelated/dead-letter
   work. Accept no other production-data changes for this validation.
8. Use `scripts/dev/complete-feature.ps1` without `-GoLive` for authorised squash
   merge/push/closeout; it is not the deployment command. Run its DryRun first and
   use `-KeepWorktree` for the initial closeout so merging cannot purge the checkout
   before deployed-tree equivalence is checked:

   ```powershell
   pwsh -NoProfile -File scripts/dev/complete-feature.ps1 -FeatureWorktree . -CommitMessage 'Add source pause, resume and complete deletion' -KeepWorktree -DryRun
   pwsh -NoProfile -File scripts/dev/complete-feature.ps1 -FeatureWorktree . -CommitMessage 'Add source pause, resume and complete deletion' -KeepWorktree
   ```

   Verify the merged tree matches the deployed candidate; a changed payload needs
   a fresh deployment review. Retain the worktree if any deployment, probe or
   closeout gate has failed. Rerun closeout without `-KeepWorktree` for purge only
   after all required gates succeed. On script failure,
   report its JSON `failed_step` and `log_path`, remediate and rerun the script.

Plan self-review: both requested controls are explicit UI deliverables; pause
preserves searchable content; delete covers SQL plus exclusive physical files;
shared ownership, native-reader lifetime, publication races, trigger enforcement,
restart and idempotency each have retained acceptance cases. No implementation,
live deletion, rescan or deployment is part of this documentation-only handoff.
