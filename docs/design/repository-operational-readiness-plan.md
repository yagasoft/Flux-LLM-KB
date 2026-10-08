# Repository operational readiness plan

Status: user-approved local implementation prepared and independently reviewed on
8 October 2026. The operational approval packet must record the required feature
closeout evidence and its current outcome. Production release,
task activation, canaries, exact recovery and retention execution require the
concrete A/B/C packet and applicable execution gates.

Goal: deliver the bounded operational-readiness slice in the [design](repository-operational-readiness.md),
using the existing SQL Server, USearch, .NET, IIS and Task Scheduler mechanisms.
Use one implementation owner, with independent review at the consequential gates.
Local implementation is authorised. Continue required closeout and concrete packet
preparation; production execution remains subject to the existing approval and
verification boundaries.

Operational Plan bindings and Apply must use the same PowerShell 7 runtime as the
canonical updater. Windows PowerShell 5.1 observations are diagnostic; loader and
control-flow checks do not establish portable operational bindings. Apply refuses
that runtime before native observation or mutation.

Read-only preparation identifies four Embed candidates with 64 saved and 449 missing
positions, one Publish candidate with 22 completed positions, and 34 excluded
historical jobs. C proposes 17 generations containing 424,381 membership links, at
most 40,846 per generation, and 34 files totalling 1,801,220,024 bytes; 2,617
generations remain protected. Nothing has been staged or deleted. Production
activation, unattended transitions, normal task-trigger processing, exact recovery,
the live frozen 40-search workload and retention acceptance remain pending.

## Existing interfaces and evidence

Inspect these current files before changing their behaviour; the listed tests are
focused verification entry points, not commands authorised to run against production.

| Area | Existing implementation | Verification |
| --- | --- | --- |
| Runtime/IIS | `scripts/deploy/update-native-iis-incremental.ps1`, `scripts/deploy/incremental-iis-payload-swap.psm1` | `tests/native/unattended-iis-discovery.ps1`, `tests/native/incremental-iis-update-contract.ps1`, `tests/native/incremental-iis-payload-swap.ps1` |
| Healthy/query ownership | `src/FluxKnowledge.Infrastructure.Usearch/DerivedIndexRecoveryCoordinator.cs`, `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlDerivedIndexRecoveryStore.cs`, `SqlCorpusGenerationLeaseStore.cs`, `SqlCorpusQueryLeaseRecovery.cs` in the same persistence directory | `tests/FluxKnowledge.Integration.Tests/Indexing/DerivedIndexRecoveryIntegrationTests.cs`, `CorpusRebuildCommitTests.cs` in the same test directory |
| Retrieval diagnosis | `src/FluxKnowledge.Application/Search/HybridPassageRetrievalEngine.cs`, `src/FluxKnowledge.Web/BgeSearchRuntimeComposition.cs`, `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlPublishedPassageSelection.cs` | `tests/FluxKnowledge.Integration.Tests/Search/ScopedCorpusRetrievalTests.cs`, `HybridPassageRetrievalIntegrationTests.cs` in that directory; `tests/FluxKnowledge.Integration.Tests/Support/HybridSearchTraceListener.cs` |
| Outlook | `scripts/deploy/run-outlook-host.ps1`, `src/FluxKnowledge.OutlookHost/Program.cs`, `OutlookHostLoop.cs`, `OutlookExportIngestionBridge.cs` in that project | `tests/native/outlook-scheduled-host-contract.ps1`, `tests/FluxKnowledge.OutlookHost.Tests/DesktopHostApplicationTests.cs`, `OutlookHostLoopTests.cs`, `OutlookExportIngestionBridgeTests.cs` in that project; `tests/FluxKnowledge.Integration.Tests/Outlook/OutlookExportIngestionTests.cs` |
| Exact recovery | `src/FluxKnowledge.Application/IntegrationV1/Corpus/NativeCorpusCommandService.cs`, `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlEmbeddingRetry.cs`, `SqlPublicationRetry.cs`, `SqlEmbeddingCheckpointStore.cs` in that directory | `tests/FluxKnowledge.Integration.Tests/IntegrationV1/EmbeddingRetryTests.cs`, `PublicationRetryTests.cs` in that directory; `tests/FluxKnowledge.Integration.Tests/Sources/RepositoryWorkRecoveryIntegrationTests.cs` |
| Retention operation | `src/FluxKnowledge.Infrastructure.Usearch/DerivedIndexFileSystem.cs`, `DerivedIndexRecoveryOptions.cs`, `UsearchGenerationBuilder.cs` in that project; `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlNativeOperationStore.cs` and the native corpus/ownership stores above | `tests/FluxKnowledge.Integration.Tests/IntegrationV1/SqlNativeOperationStoreTests.cs`, `NativeCorpusActionMatrixTests.cs` in that directory; `tests/FluxKnowledge.Integration.Tests/Indexing/UsearchGenerationTests.cs`, `SqlToUsearchRebuildTests.cs`, `DerivedIndexRecoveryIntegrationTests.cs` in that directory |

Private evidence locators, to be inspected and hash-bound without publishing raw data:

- `.agents/release-preparation/repository-auto-recovery-20261004/live-validation-dc58eb91-20261007/release-closeout-completed.json`
  records completed release, exact code/test hashes, preserved failure evidence and
  the bounded six-case acceptance. Do not repeat unchanged implementation suites.
- `.agents/release-preparation/operational-readiness-20261008/repository-readonly.json`,
  `iis-readonly.json`, `outlook-task-readonly.json` and
  `index-retention-readonly-20261008.json` record the current starting observations.
- `.agents/run-logs/verify-live-retrieval-workload.ps1` accepts `WorkloadPath`,
  `OutputRoot`, `ExpectedGeneration` and checks the established frozen contract.
  It makes live cited-read requests: reading the script is not permission to run it.
  The frozen specification is `.agents/release-preparation/404a38b50c923c044848ba74c459555cce5d4600/live-workload.json`;
  the existing runner is `.agents/release-preparation/a248811a0611ab8df90a5905c6916e1eb8e441dc/measure-live-workload.ps1`.
  Its client timeout is 40 seconds, whereas the specification and Web engine use
  25 seconds; the verifier lacks a per-case elapsed bound. Correct this harness
  gap before measurement while preserving the manifest's queries and thresholds.
- `E:\Codex Workspaces\Scripts\index.json` indexes the hidden Outlook launcher.
  Keep reusable parameterised operational helpers in that library and index them
  if a proven gap requires one; retain run-specific manifests/results privately.

## Batch 1 — establish causes and the exact executable runtime action

- [ ] Verify clean task worktree/current Main and actual installed candidate,
  configuration and receipt. Confirm the Shared healthy-probe/fresh Exclusive-reread
  implementation already in candidate `e1d18352`; retain its existing evidence.
  Refresh only changed provenance or uncertain runtime facts. Do not infer a required
  redeployment from documentation commits or stale unchecked historical plan items.
- [ ] Diagnose retained timeout/index-updating evidence before proposing a runtime
  fix. Correlate queue/admission, lease ownership, SQL/dense retrieval, projection
  publication and native callback/cleanup phases with the original failures.
  Use existing trace support; instrument only missing timings. Reproduce the
  causal defect at representative disposable scale/concurrency with synthetic
  workers, write its failing invariant test, and make only the demonstrated fix.
  An unresolved cause gets a bounded diagnostic and explicit stop-loss, not an
  unchanged full workload or another implementation of the deployed Shared probe.
- [ ] Correct the runner to read the manifest's existing 25-second outer deadline
  and the verifier to reject every response exceeding it. Prove 20 queries/two
  rounds/two callers unchanged and no dropped failures; use a local fake endpoint
  to test boundary timing, cancellation and exact known cleanup before another
  request. Do not alter Web engine deadlines, p95 or production search behaviour
  to accommodate the old 40-second instrumentation.
- [ ] Prepare the existing ordinary `-EnableUnattendedDiscovery` PlanOnly action.
  Inspect original/desired IIS tuple, canonical path, Application Initialization,
  corrected native-start verifier and captured-tuple rollback. No migration/rebuild
  flags. If code needs no change, carry that finding into A instead of implementing
  another host. A must explain whether an exact payload update is needed.
- [ ] Define the smallest task-restoration operation preserving task identity and
  hidden launchers; explicitly name enable and bounded start separately. Inspect
  interactive session and eligible desktop companion work. The four Outlook tables
  are empty; inventory existing eligible Visio work or propose a harmless supported-
  format fixture in A. Actual mail capture needs a user-selected profile/folder/item
  scope and cannot be claimed from Visio success. The updater restores Disabled and the
  scheduled-host contract deliberately excludes activation from feature closeout.
  Do not alter that default or call `validate-native-outlook-ingress.ps1` to obtain
  activation: it belongs to an older full deployment flow.
- [ ] If a supported operator gap remains, add only a narrow opt-in operation to
  the existing path/library after implementation approval. Test-first with fake
  native ports: wrong task/action/principal/launcher or missing interactive session
  refuses; unchanged defaults stay Disabled; drift/duplicate/lost acknowledgement
  preserves identity and reports exact policy; failed application restores prior
  enabled state without fabricating ingestion success. Existing synthetic Outlook
  tests cover actual claim/export/ingestion, singleton and stale-lease behaviour.
- [ ] Assemble A with native startup-before-HTTP, one continuous no-HTTP window:
  fixture baselines before T0, 30-minute idle observation, then four distinct
  add/edit/rename/delete transitions with individual durable outcomes and the normal
  15-minute reconciliation/processing bound. Bind IIS access-log/event proof,
  exact canary paths/content and normal task-trigger observation,
  Outlook processing proof, safety observations and rollback. No production action
  occurs merely because this section or local tests exist; B/C must also be
  concretely prepared before the single packet is presented.

## Batch 2 — prove the complete bounded action and prepare one packet

- [ ] Run only affected local/disposable tests. For modified IIS/verifier paths,
  exercise the existing unattended and updater native contracts in relevant shells,
  including Windows PowerShell compatibility; verify stale/reused workers,
  missing startup evidence and HTTP-before-preload refusal. For task changes,
  run the named Outlook contracts and synthetic unit/integration cases. Inspect
  test configuration before invocation; ordinary tests must have no production
  access or real-model acquisition.
- [ ] Demonstrate the first safe end-to-end result in a disposable environment:
  supported input → durable revision/record → dispatch → synthetic worker result →
  publication → real-transport exact readback. Include the reproduced retrieval or
  ownership failure boundary and the correction; passing isolated mocks alone
  does not establish this capability.
- [ ] Account for all 39 observed terminal jobs with fresh native eligibility and
  an explicit eligible/refused/excluded disposition, including the 34 historical/
  suppressed entries. Prepare previews for every proposed current candidate as
  discovery only; do not commit them or publish private IDs. Record refusal and
  saved/missing positions. The superseded former sixth is excluded unless a fresh
  authoritative lifecycle later changes; never manufacture eligibility. Refresh
  these observations after A within the approved exact set. Before the packet,
  bind job IDs/actions and maximum missing-position sets, preserved vectors and
  expected effects. No future approval based solely on aggregate counts.
- [ ] Audit retention closure and actual rules without deleting or marking rows
  retired. Include logical references and release/rollback files, not just foreign
  keys. Separate the 1,336 checkpoint-bound drafts from 1,297 placed candidates;
  refresh counts rather than freezing these observations as future authority.
  Produce C's protected/candidate/refused classification and exact one-off rule,
  rollback/recovery protected set, exact candidate IDs and ordered streamed member
  child artifacts with per-generation digests/counts for approval
  together. `SqlSourceDeletionStore.cs` is a reference to audit, not a history
  retention shortcut; every nonempty IndexPath is protected by the existing recovery
  store. Do not force recovery or expand staging/quarantine cleanup to bypass it.
- [ ] Obtain independent integrity/concurrency review, then close that demonstrated
  operator gap locally before the packet: a bounded
  manifest-driven native preview/commit action reusing native operation receipts,
  confirmation/idempotency, Exclusive recovery ownership, publication fences and
  path guards. Keep one focused retention operation in the existing persistence/
  native-corpus flow; add no service, general policy framework or new database.
  Keep exact SQL row/link and file recovery snapshots under the existing recovery
  root. Reuse sequential retained-row fingerprint/snapshot readers; do not flatten
  millions of members into JSON/RAM or use OFFSET/repeated page rescans. Process one
  exact generation per manifest-bound transactional child receipt, with tested
  maximum link counts and cancellation bounds. Commit its SQL removals and receipt atomically, then
  perform only receipt-bound file removals under retained ownership/reference checks.
  Retain snapshots and all canonical vectors; no full-database rollback over newer writes.
- [ ] Prove disposable refusal of active, checkpoint/vector, query, shared-path,
  rollback and recovery references; running/unknown owner and unsafe/reparse paths;
  racing publication/reference creation and stale manifest/count/hash. Prove exact
  membership effects, protected-vector preservation, duplicate/resume convergence,
  interruption before/after SQL commit and every filesystem step, and selective
  restore refusal on newer conflicting state. Reuse existing native-store and
  index test patterns; add only focused cases for these changed invariants. Verify
  representative retained scale only for the affected operation and reuse unchanged
  sequential-scanner performance evidence; no new all-history benchmark ceremony.
- [ ] At this two-batch checkpoint, report local executable capability and evidence,
  A/B/C's readiness and unresolved material facts. If no safe result exists, identify the
  blocker and narrow the approach before another batch. Do not widen into a general
  retention subsystem, new scheduler, migration or unrelated cleanup.
- [ ] Independently review the full affected design/diff and checks. For actual
  feature changes, complete `scripts/dev/complete-feature.ps1 -KeepWorktree` with
  required checks and zero new warnings; reuse unaffected prior evidence where
  applicable, without manually substituting the mandatory closeout sequence.
  Present one concrete packet containing A's manifest/PlanOnly, B's exact cohort
  and maximum missing positions, C's exact protected/deletion sets, all checks and
  honest rollback limits. The user may approve the three named sections together.
  If B/C cannot be concrete, report that missing part; do not force an early A
  approval or invent eligibility. Fresh independent Astra/equivalent review is
  required immediately before production actions under the approved packet.

## Runtime result — execute A only when approved

- [ ] Refresh exact target/payload/configuration/task/hold/database/GPU/owner checks,
  then execute only A through the canonical updater and named task-restoration
  operation. Preserve receipt and rollback evidence. A failed or uncertain action
  stops; an incompatible predecessor remains stopped/held. No clean-slate fallback.
- [ ] Prove native fresh managed startup before HTTP. Run the design's one continuous
  idle window; all four create/modify/rename/withdrawal transitions occur after
  30 minutes with no HTTP until every durable outcome settles. Verify access logs/
  events show no hidden traffic, leave unrelated hosts untouched, and only then
  check survivor citations plus old rename/deletion refusals. SQL/process reads
  must not cause HTTP wake-ups; task/worker health
  alone does not prove automatic publication. Record exact durable transitions,
  current citation/withdrawal and absence of new crash/ownership uncertainty.
- [ ] Prove the named companion work through durable claim → worker/export →
  ingestion/publication → visible status/readback in the actual interactive session.
  Verify hidden execution, unchanged task/launcher identity and duplicate-trigger
  safety. Record actual processing; exit zero or task Enabled alone does not pass.
  Witness a normal configured trigger separately from an initial manual launch.
  Report mail capture separately and only for an explicitly approved mail scope;
  Visio success with Outlook NoDurableWork does not establish mail ingestion.
- [ ] Obtain fresh independent live-validation review before considering terminal
  processing. Stop/report any new integrity, ownership, GPU cleanup, crash or
  rollback uncertainty; do not use historical successful probes as current proof.

## Exact recovery and frozen acceptance

- [ ] Refresh B's native previews and exact source/checkpoint/vector/GPU evidence
  after runtime validation. Stay inside its already-approved job/action/maximum
  missing-position sets; drop no-longer-eligible entries, never add or substitute
  jobs/positions. Recheck confirmation versions and ownership before commit without
  reasking for unchanged approved scope. Exercise
  only native `embedding_retry`/`publication_retry` with recorded idempotency keys.
- [ ] Verify each original job/delivery/checkpoint and completed vector bytes remain;
  only approved missing positions are embedded, then normal publication and exact
  cited readback complete. Test affected retry changes for stale confirmation,
  duplicate commit, cancellation/lost acknowledgement, changed source/checkpoint,
  active/uncertain GPU state and publication collision before their release.
  Previously recovered 32 jobs/329 vectors are preserved and never replayed.
- [ ] Settle current source coverage/publication and bind a single expected generation,
  root/configuration, corpus stamps and source hashes. Explain any remaining
  suppressed/unsupported/ineligible source separately. Do not declare full coverage
  merely because the original five-job count decreased.
- [ ] Run the unchanged frozen 20 queries twice with two concurrent callers under
  authorised live-validation scope. Require 40/40 hybrid/semantic-ready, exact
  scope/citations/readback, stable current sources and expected generation, p95
  below 20 seconds and the existing 25-second outer deadline. Include full method
  and document readback on all three surfaces. Retain every failure and timing.
  Stop on a failed gate; no unchanged retry, selective success set or raised limit.

## Bounded retention and final acceptance

- [ ] After recovery and frozen acceptance, refresh C's full reference closure against
  its approved exact rule, protected set and generation/member/path/count manifest.
  Remove ineligible targets; never expand or replace them. If no eligible IDs remain
  or ownership/protection is uncertain, keep C blocked and report the result.
  Do not begin another implementation round silently after production acceptance.
- [ ] With C authorised in the packet, obtain fresh independent review immediately
  before execution. Revalidate under the existing mutation boundary, perform only
  the pretested bounded operation and retain before/after/recovery evidence.
  Same-scope freshness checks need no repeated human approval; material scope or
  recovery changes do. No terminal, checkpoint-draft or snapshot cleanup is implied.
- [ ] Verify exact effects, all protected references/vector bytes, unchanged active
  generation, citations, task/host health, known GPU cleanup and targeted search
  parity. Reuse frozen evidence when active code/configuration/corpus/query behaviour
  remains verified unchanged; repeat the full 40 only for a relevant change or
  failure that invalidates it. Retain failures; do not delete more history to pass.
- [ ] Independently review complete final scope/evidence. Update only affected durable
  architecture/roadmap/design statuses truthfully after execution; no manual assets
  unless separately requested. Preserve worktree/evidence until all required in-scope
  closeout succeeds. Report runtime, Outlook, exact recovery, semantic and retention
  outcomes separately, including any blocked or unapproved gate.

Local implementation is authorised. Continue required closeout and concrete packet
preparation; production execution remains subject to the existing approval and
verification boundaries.
