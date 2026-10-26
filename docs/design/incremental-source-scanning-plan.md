# Incremental source scanning implementation plan

Status: implemented and released on 26 October 2026 after independent GPT-6 Astra/high review, complete staging qualification and canonical verification. The user authorised production deployment, journal configuration, live validation, squash merge and task-worktree purge. The reviewed release passed bounded production acceptance; the initial production baseline and namespace establishment continued as normal runtime work. Production steady-state content-byte savings and natural-change latency remain unmeasured. Earlier dated checkpoints below record the evidence and unresolved work at that time; this delivery record supersedes their outstanding-work statements.

**Goal:** stop ordinary whole-source scans and unchanged-content reads while preserving discovery, integrity, physical scope and recovery.

**Architecture:** bounded non-waiting per-volume journal intake with coalesced watcher wakes selects durable path/subtree work. Verified per-entry capture and complete journal coverage allow unchanged content reuse; namespace completeness remains a separate authority. Scoped scans use the existing retained pipeline. Configurable weekly verification supplies an independently paced integrity check. Permanent overlapped waits are deferred pending measured need.

**Stack:** existing .NET/C#, Windows NTFS/Cloud Files, SQL Server/EF Core, IIS, protected native helper and existing REST/CLI/MCP/UI surfaces. No new external package is assumed.

**Specification:** [Incremental source scanning](incremental-source-scanning.md). Read it before execution; this plan does not waive its failure/recovery rules.

## Delivery record, 26 October

All four equally weighted delivery gates are met: journal-to-current-publication capability; integrity/concurrency and configurable paced weekly verification; full staging qualification and canonical checks; reviewed production activation and bounded live acceptance.

The mandatory closeout script verified and published application commit `c8c39755151949487579d9efea7636a83719ccd3`. Its Release build had zero warnings; 4,851 .NET tests passed with zero failures and 37 environment-dependent opt-in skips. Native repository, deployment, recovery, closeout and timing contracts passed. Earlier failed canonical runs remain failed evidence. Their corrections cover composed-schema offline maintenance, isolated semantic projection tests with the production two-second budget unchanged, and documentation terminology.

Three alternating staging pairs per case used identical 26,406-file, 13,519,872-byte synthetic fixtures with text indexing disabled:

| Case | Full-scan median | Journal median | Full-scan / journal source bytes |
|---|---:|---:|---:|
| Unchanged manual sync, content-work drain | 599.874 s | 2.445 s | 13,519,872 / 0 |
| One edit, current retained publication | 444.713 s | 4.158 s | 13,520,384 / 1,024 |
| 100 edits, current retained publication | 306.199 s | 15.465 s | 13,571,072 / 102,400 |

Content-work drain is the start of a subsequently confirmed eight-second quiet interval; its counters include that confirmation tail. Publication timings are retained-current readback, not search-index completion. Namespace debt was retained separately. The excluded contaminated single-edit pair remains excluded. HTML sometimes still took about two seconds, so no general dashboard or browser-render speed-up is claimed.

Separate full-size qualification passed metadata reconciliation with zero source/retained reads, a 264-file directory move, missed-watcher restart catch-up and preservation of a later edit. The paced weekly cycle completed all 26,406 items as 26,405 verified plus one resolved, with zero deferred items, complete counters and 26,406 receipts. Its full duration was 87 minutes 32.902 seconds at a 256 KiB/s combined staging limit; a foreground edit published in 2.065 seconds. Resumption retained the previous 151 item IDs and source-content hashes; exact receipt immutability is independently proved by the earlier four-file crash test.

After exact-target review, both existing journals reached 1 GiB maximum and 128 MiB allocation delta with unchanged IDs and all required cursors readable. The routine updater deployed the reviewed Web/Outlook/helper payloads, preserved repair policy and task security, applied the exact additive SQL to the bound database incarnation, confirmed actual IIS-identity journal transport and released the deployment hold after unchanged-state validation. Bounded live observations showed HTTP 200 health, advancing baseline progress and 2,083 sealed files with zero revision/artifact/hash/length/profile binding mismatches. The repository request was normally queued behind that baseline. Full production baseline completion, production steady-state byte savings and natural-change latency were not acceptance claims. Live HTML responses were about 2.0–2.3 seconds during baseline work.

The initial baseline is required to establish guarded reuse evidence. Later routine reconciliation checks metadata and namespace authority; unchanged qualified content is reused. Weekly integrity verification remains configurable on the existing source page, with default Sunday 02:00 installation-local time, a six-hour quiet window and a 2 MiB/s combined source/retained budget. Enlarged journal capacity is not a fixed retention-duration guarantee; short release/baseline window observations do not characterise every idle or build workload.

## Execution constraints

The next correction uses a separate synchronous no-follow/no-recall handle while the original capture guard remains held. Version3 receipts bind the exact returned CLOSE, qualified root/environment profile and the immutable plan generation fixed before capture. Add distinct last-content-USN and unjournaled-generation fields; both cut-offs must be satisfied before a receipt can discharge content work, while namespace/recovery checks remain independent. Missing pre-journal fields are dirty. Explicit per-root qualification fences existing reuse and advisory-only watcher handling as well as new sealing; unqualified roots retain scoped ordinary checks. Test several repeated native cycles, both notification orders, synthetic-record replay without recapture loops, later journal/watcher generations, crash-before-receipt, refusal/cancellation and qualification changes before enabling. Actual OneDrive qualification remains a separate release condition.

Controlled native testing also reproduced repeated directory-security aggregation. Retain independently due NamespaceMetadata reconciliation, with zero unchanged qualified content reads and no enumeration between passes. Native qualification then proved that even shared metadata-only descendant handles block ancestor rename. Astra approved buffering one directory's names in a bounded-memory native burst (65,536 entries/16 MiB maximum), disposing every cursor/ancestor/outer-root handle before pacing or recursion, and pacing managed processing plus per-file metadata/reuse at 64 entries/second. Reacquire current list access, original root/directory identity, path/reparse/cloud checks after pauses and at final validation. Overflow is explicit incomplete/deferred debt. Keep short guarded native pages, correct restart/on-disk-only flags, guarded EOF, newer namespace/content work and individual absence. Test multi-page completeness, permission/cloud changes, rename/replace/delete, cancellation/restart, handle-free pauses and largest-directory burst/rename latency. Native enumeration is not paced at 64 entries/second. No directory/ancestor mutation capability is added.

- One implementation owner. Use `superpowers:executing-plans` as targeted guidance, with focused tests before behaviour changes and independent reviews at the stated consequential gates. No per-file agent handoffs.
- Reuse `I:\Codex Workspaces\Worktrees\incremental-source-scanning\LLM KB`, branch `codex/incremental-source-scanning`, initial base `b9b70170a0ba2a452b7d5d3f130a8f73c1e3c20a`.
- The other coordinated chat has finished; no inter-chat merge or deployment coordination remains.
- Testing uses isolated runtime/configuration/endpoints/storage, synthetic sources and disposable SQL. Never point tests or benchmarks at production or real source content. Models remain offline; none is required.
- Existing no-follow/no-recall, exact root/lease/configuration authority, immutable retention and native ownership policies remain requirements. No pagefile changes, page moves or user-manual regeneration.
- The user separately approved this implementation, E:/I: journal configuration, deployment/live validation, squash merge and task-worktree purge. Exact-target independent review preceded each production action. Production was kept stopped during preparation and enabled only for the reviewed deployment and live acceptance. The user's stop-when-idle instruction applied until fix deployment; the deployed application can now run its initial baseline.

## Intake review decisions and first corrections

The independent GPT-6 Astra/high review inspected the current design, plan, dirty implementation and Microsoft native documentation. Adopt its bounded-polling recommendation: `BytesToWaitFor=0`, `ReturnOnlyOnClose=0`, one owner per volume, bounded pages/bursts and a configurable two-second starting interval. Watchers wake intake sooner; they do not independently schedule healthy content scans. Use fair volume rounds and retain periodic health/Git membership checks. An asynchronous tail wakes on unrelated volume records too; no agreed latency requirement warrants a subscription/lease/helper redesign now.

The helper keeps short query/read RPCs. Its three-second deadline bounds transport waits, not stalled native I/O. Do not implement waiting reads merely by changing native `Timeout` or the byte threshold: `Timeout` is ignored for asynchronous handles and synchronous intervals can repeat. Final native completion, not token cancellation alone, owns buffer/handle disposal. If measurements later justify permanent waits, review renewable ownership or advisory subscriptions, per-volume concurrency, control-path availability, explicit cancellation/disconnect/restart and completion lifetime first. The specification records that alternative contract and primary sources.

Keep Win32 `OpenFileById` flags separate from NT `NtCreateFile` options. Parent-directory routing is metadata-only and uses Win32 `FILE_FLAG_OPEN_NO_RECALL` (`0x00100000`), `FILE_FLAG_OPEN_REPARSE_POINT` and directory backup semantics. The NT no-recall option has a different numeric value. Retain a native routing regression and cloud no-hydration qualification; a flag-contract check alone does not establish provider behaviour.

Before adding scheduler/UI scope, correct these verified current paths with focused failing tests:

1. `SqlSourceIncrementalCoordinator.Completion.cs`: acknowledge exact successful manifest obligations and generations only. Due A, deferred B and an empty manifest must leave B pending; later B generations survive old completion. Do not remove debounce or add sleeps to hide the race.
2. `SqlSourceChangeJournalStore.cs`: distinguish acknowledged page replay from a fresh owned empty read; release the latter lease without new dirty work. A real edit after repeated empty reads must not wait for the 30-second lease expiry.
3. Watcher/domain/buffer/SQL paths and `SourceChangeJournalService`: retain bounded paths and both rename sides; healthy hints wake intake only, degraded hints merge force-capture generations into the same pending table. An unavailable volume must not block another volume or durable dispatch. Treat an internal helper deadline as per-volume unavailability when the caller is still active; propagate genuine caller cancellation. Keep journal and scan wake channels separate, preserve the periodic timer across hints and bound wake frequency.
4. Coordinator/store recovery: durably queue baseline-required roots even with no pending paths, refresh ancestor enrolment after invalidation, retain unresolved namespaces across partial states and reject stale completion after a newer recovery marker. Classify native gaps/unsupported records into recovery.
5. Enumerator/coordinator predicates: treat root subtree `.` consistently in selection, reuse, deferred matching and withdrawal without weakening absence authority.
6. Intake/scheduling: fair bounded volume rounds, first-change-based 30-second maximum debounce, and independent health/Git, recovery/degraded and weekly-verification deadlines.

Review-time evidence included a failed edit/read-count case and a deliberately red root-scope regression. After the report, focused corrections passed 39 domain and 32 SQL/native integration checks, including baseline/edit/quiet byte assertions, exact pending acknowledgement, empty-read lease release, stable replay, maximum debounce and volume scheduling. This is foundation evidence, not full qualification: watcher convergence/fallback, recovery/namespace fencing, independent recurring deadlines, weekly verification and real-interface staging acceptance remain outstanding. The private review report and logs remain outside Git.

Subsequent focused checks passed 59 pure watcher/intake cases, 25 isolated-staging configuration cases, five SQL deadline/cancellation/scheduling cases and 11 scoped enumeration cases. These runs overlap; do not add their counts as unique coverage. The deadline test proves other-volume progress and durable dispatch after a helper timeout, while caller cancellation propagates without cursor advance or dispatch. Watcher batches now preserve bounded rename/path hints and ambiguous retries; the intake loop has an independent timer and bounded advisory wakes. Targeted hints cannot broaden a non-recursive root. SQL healthy/degraded convergence and real-interface staging remain the next gate; no production change has occurred.

The next focused run passed 11 SQL/native watcher/order/restart and existing regression cases. A separate two-file staging deployment then proved the healthy journal path through real CLI preview/commit, durable capture, processing/publication and current CLI/HTTP/MCP readback. Its unchanged sibling retained the same identity/hash. This satisfies the first observable-result checkpoint, not full release qualification or the paired large-fixture benchmark. The stage was stopped and its port verified closed. Recovery/metadata fallback, uncertain namespaces, Git control generations, independent cadence and weekly integrity work remain incomplete. Preserve the staged state and private receipts for continuation; production remains unchanged.

Astra approved that bounded watcher/intake/native-flags milestone without a new blocking defect. Subsequent focused pure checks passed 20 parser/protocol cases and 23 worker cases after correcting typed recovery for unknown/deleted journal history, the data-truncation flag and false retention-failure reporting from incremental bookkeeping. Recovery/ownership/cadence, registration-race and metadata-fallback integration tests are prepared and await the coordinated SQL window. While the parallel chat owns that window, 16 pure calendar/shared-read-budget cases pass, including DST, daily continuation, bounded burst credit and cancellation. The weekly verifier is not wired into runtime; these checks do not qualify it or the incomplete feature.

The subsequent bounded GPT-6 Astra/high recovery review approved the focused approach with required details now included in the specification: separate baseline debt from availability, coalesce required recovery while updating/fencing its boundaries, and preserve namespace debt through finite generations rather than repeatedly invalidating content epochs. Capture bookkeeping takes the immutable plan and proves its post-acquisition barrier; fallback captures remain unproved and are queued for recapture on continuous restoration. Only a higher owned claim can replace a recovery/availability-invalidated manifest. Acceptance/reset both discover root IDs before serializable work, then lock and revalidate ownership/registration. No larger architecture is needed; implementation/release approval remains outstanding.

The first coordinated red run reproduced six failures and one pass across recovery, fallback, ownership, cadence and registration. One-owner and cadence corrections then passed 41 combined source-control/Git coverage cases. Before qualifying the remaining correction, add meaningful cases for newer namespace uncertainty during completion, repeated unrelated unresolved records without epoch churn or continuous walks, registration racing reset, gap/helper failure between capture and barrier, journal-to-fallback manifest replacement with later edits, continuous restoration recapture, and unavailable startup without a cursor. Preserve baseline/reconciliation obligations and every unacknowledged pending generation in each case.

## Review focus

The bounded recovery implementation review found two blocking gaps despite the previous 29 focused integration checks: fallback baselines did not select unseen revisions for individually guarded absence, and the Windows absence lease did not test cloud membership completeness. Add failing regressions for a deletion without a watcher hint while preserving an inaccessible sibling, initially incomplete root/intermediate parent membership, and membership becoming incomplete after acquiring the absence lease. Correct only scope selection and absence authority; do not set fallback `NamespaceComplete`, clear baseline debt or introduce whole-root unseen suppression. Recheck the corrected milestone independently before claiming it approved.

Those corrections and feature-off proof fencing now have observed red/green evidence: two SQL cases and 25 cloud-path cases passed, zero skips. Astra approved their bounded implementation scope after inspecting code and logs. The disconnected provider already refused missing-child lookup, so the cloud regression specifically requires explicit `SourceNamespaceIncomplete` rejection from held membership before consulting the child, both initially and at final validation. This is not a claim that this synthetic provider reproduced false withdrawal before the guard.

The native aggregation qualification then exposed a separate ordinary-freshness defect: the persistent-reader repeated-write case produced no new interval content record, while its no-reader control passed. Stop relying on barrier-only version2 `JournalCovered`. The user selected guarded post-capture per-file CLOSE, subject to staging and actual OneDrive qualification. Implement the specification's per-physical-file sealed/unsealed distinction and the independently approved qualification/generation fences; unsupported or unknown files retain scoped ordinary forced checks.

Two synthetic forced-close probes passed on the supported local environment: regular file and hydrated disconnected Cloud Files under restricted non-admin read-only authority. They demonstrate fresh repeated-write evidence after forced CLOSE, not universal API or actual OneDrive support. Extend to several capture/seal/write cycles; retain the counterexample. After direction, qualify same uninterrupted capture/seal guard, exact receipt/cursor/identity/epoch/generation binding, post-seal metadata refresh, synthetic-record accounting without loops, crash/unknown outcome, replay, later edits and refusal/provider fallback. Natural/pruned-history proof is an optimisation; do not invent sealing for old records. Add actual capture→ack→later hidden edit→current readback, both notification orders, missed hints/restart, older CLOSE then newer non-close in one page and replay, hidden writes before eventual close, aliases, namespace/ancestor aggregation, and zero reads of sealed siblings while scoped fallback meets its ordinary deadline.

- A journal cursor commits but the client sees a timeout: replay neither loses nor duplicates dirty work.
- A repeated empty read has a new owner: release its lease; an empty manifest cannot consume not-yet-due changes.
- Watcher/journal arrival order changes: both converge on one pending pipeline; later genuine edits remain distinct from in-flight work.
- Directory moves/renames and journal wrap during baseline: no silent skipped namespace and no unrelated withdrawal.
- Source metadata stays unchanged while content is overwritten: dirty reasons force capture; weekly verification detects unjournaled corruption.
- A worker loses its lease or new events arrive mid-scan: no stale observation or generation acknowledgement commits.
- Cloud availability, helper permissions and older-binary rollback: no recall, privilege escalation, hidden repair or misinterpretation of targeted work.

## Proposed interfaces

Use these boundaries rather than making fake `SourceDiscoveredFile` captures from cached hashes. Final record fields follow the specification; changing the stated authority semantics requires design review.

```csharp
enum SourceScanScopeKind { FullBaseline, Targeted, MetadataReconciliation, Verification }
record SourceScanScope(SourceScanScopeKind Kind, Guid ManifestId, long BaselineEpoch);
record JournalCursor(string VolumeIdentity, ulong JournalId, long NextUsn);
record JournalPage(JournalCursor Start, JournalCursor Next, IReadOnlyList<JournalChange> Changes);

interface ISourceChangeJournalReader {
    ValueTask<JournalCapability> QueryAsync(string approvedVolumeIdentity, CancellationToken ct);
    ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken ct);
}
interface ISourceChangeJournalStore {
    ValueTask<JournalReadClaim?> ClaimAsync(DateTimeOffset now, CancellationToken ct);
    ValueTask AcceptAsync(JournalReadClaim claim, JournalPage page, CancellationToken ct);
}
interface ISourceIncrementalScanStore {
    ValueTask<SourceScanManifest> ReadManifestAsync(SourceScanRequest request, CancellationToken ct);
    ValueTask<SourceReuseCandidate?> ReadReuseCandidateAsync(SourceRootConfiguration root, string relativePath, CancellationToken ct);
    ValueTask<bool> ConfirmReuseAsync(SourceRootConfiguration root, SourceScanRequest request,
        SourceReuseCandidate candidate, ISourceObservationLease observation, CancellationToken ct);
    ValueTask CompleteTargetsAsync(SourceRootConfiguration root, SourceScanRequest request,
        SourceTargetCompletion completion, CancellationToken ct);
}
interface ISourceVerificationStore {
    ValueTask<VerificationBatch?> ClaimBatchAsync(DateTimeOffset now, CancellationToken ct);
    ValueTask CompleteBatchAsync(VerificationBatch batch, VerificationBatchResult result, CancellationToken ct);
}
```

`JournalChange` contains validated version, USN, file/parent identity, reason flags, name and directory metadata; the reader must not claim every record belongs to a source. `JournalCapability` contains status, identity, readable bounds and supported record versions. `JournalReadClaim` adds reader owner/generation/expiry and the expected cursor. `SourceScanManifest` fixes exact paths/subtrees, binding/dirty generations, recovery/coverage epoch and policy identity; a global generation ceiling cannot acknowledge omitted paths. `SourceReuseCandidate` binds the exact current revision/artifact and committed coverage. `ISourceObservationLease` owns current no-follow metadata handles and validates identity/scope; it confers no new content checksum or supersession proof. `SourceTargetCompletion` lists exact successful/deferred outcomes and Git membership evidence where needed. Verification records bind an immutable cycle and its claimed item generations. Persisted types are versioned and fail closed on unknown versions. Keep stable native journal/range evidence separate from transient parent-path resolution when deciding replay identity.

## Milestone 1 — journal to durable, scoped work

**Deliverable:** a real isolated NTFS edit produces one durable pending path, with restart-safe cursor advancement and no production access.

**Files:** create domain journal/inventory/manifest records under `src/FluxKnowledge.Domain/Sources/`; create the journal reader/store ports above under `src/FluxKnowledge.Application/Ports/`; create `SourceChangeJournalService.cs` under application `Sources/`, `WindowsSourceChangeJournalReader.cs` under integrations `Files/`, and `SqlSourceChangeJournalStore.cs` plus matching entities/configuration under SQL persistence. Add the specification's additive migration and model snapshot. Extend the protected helper host through a distinct `SourceJournalPipeProtocol.cs`/server/client, with journal-only settings separate from repair. Inspect current `SourceAccessPipeProtocol.cs`, `SourceAccessPipeServer.cs`, `WindowsSourceAccessAdministration.cs` and `SourceAccessCommand.cs` before changing their hosting entry points.

- [x] Add failing parser/protocol tests: malformed sizes/offsets, V2/V3, unsupported versions, unknown flags, 1 MiB cap, wrong SID/volume/deadline, direct-access denied, and no repair/journal-mutation operation.
- [x] Add disposable SQL tests for atomic page+pending-work+cursor commit, duplicate/ambiguous commit, crash before commit, expired reader generation, concurrent roots/volumes, pending-path overflow and deterministic root-lock order. Assert exact cursor/work fingerprints and lease state. Repeat an empty page under a new claim, append after that second empty page and prove prompt acceptance without waiting for lease expiry. Retry the same native range after transient parent-path resolution changes without treating it as a new journal event.
- [x] Implement documented non-waiting native reads under the proven staging identity, otherwise the bounded protected journal capability. Select all relevant reasons and do not wait only for close. In an isolated native fixture, prove zero-byte-threshold EOF returns no progress promptly and a later append returns records/continuation without requiring final close. Follow explicit V0/V1 field semantics, not their contradictory later example. Do not execute journal creation/resizing here.
- [x] Implement startup catch-up, fair bounded volume rounds, coalesced wake/timer scheduling and independent health checks. Test sustained unrelated traffic on A while B has a real edit, unavailable A with due B/fallback work, and no-progress backoff. Repeated hints must not defeat burst bounds. Preserve first-pending time and the 30-second maximum debounce under continuous writes; open writers safely defer capture.
- [x] Test cancellation before/after native completion and SQL commit, disconnect/helper restart, application restart, ownership loss and deployment hold. Unaccepted pages replay from the durable cursor; no replacement native calls/tasks accumulate after a timed-out/stalled request. Keep one completion/disposal owner. The short RPC cannot host permanent native waits or claim hard native deadlines.
- [x] Implement atomic root-epoch enrolment under the shared reader/cursor fence, registration-generation recheck at page acceptance, cursor S, immediate durable intake, ancestor/directory/file inventory, unresolved-work/recovery markers and replay to E. Test concurrent root registration while an existing reader is advancing, a crash between registration and enumeration, wrap/reset/unknown records, directory changes during enumeration, incomplete cloud directories and empty roots. Recovery with no pending paths must still queue a baseline. Refresh ancestors and fence older completion against newer invalidation. No unresolved epoch marker may be cleared without evidence covering its barrier.
- [x] Run the focused native/domain tests, then the disposable SQL journal tests. Save the first real edit's request/cursor/work evidence. No schema-only milestone counts as the end-to-end result.

## Milestone 2 — targeted publication and unchanged reuse

**Deliverable:** edit one fixture file and obtain its new current publication through real interfaces, with zero reads of untouched sealed content. A healthy sealed unchanged drain performs no tree enumeration between independent authority reconciliations. Those paced metadata passes read no unchanged qualified content; unsealed freshness debt remains scoped and measured.

**Files:** modify `LocalSourceRootWatchHostedService.cs`, `LocalSourceWatchSignalBuffer.cs`, `SourceWatchState.cs`, `ISourceRootWatchStore.cs`, `SqlSourceRootWatchStore.cs`, `SourceScanRequest.cs`, `SourceScanWorker.cs`, `LocalSourceEnumerator.cs`, `WindowsSourcePathAccess.cs`, `LocalSourceFileCapture.cs`, `SqlSourceScanStore.cs` and `SqlSourceScanStore.IssueCompaction.cs`. Add `ISourceIncrementalScanStore.cs` and its implementation; keep large new SQL responsibilities in a focused partial/new file. Adapt `GitTrackedSourceDiscovery.cs` and `SqlRepositoryWorkRecovery.cs` only for the explicit targeted stable-membership proof. Wire through the actual existing composition root located at execution time.

- [x] Test first: quiet sealed drain between authority reconciliations enumerates/reads zero; an independently due metadata pass enumerates at its configured rate without reading unchanged qualified content; one stable edit hashes only that file; same-size timestamp-restored overwrite remains dirty; no new per-file SQL writes for an unchanged sealed cycle. Do not assert exactly one read per human save: aggregated records, open writers and later edits can require another capture. Unsealed/unknown entries retain the explicitly bounded ordinary-freshness fallback.
- [x] Implement advisory-only watcher handling for proved sealed entries and a shared durable pending path. Unknown/unsealed hints force capture without a second processing architecture. Test watcher-before-journal with an intentionally empty first read then later record, journal-before-watcher, repeated watcher signals, repeated journal pages and combined duplicates. The continuation advances over duplicate/irrelevant records and advisory watcher noise creates no independent content-dirty generation.
- [x] Preserve both rename endpoints and bounded normalised path/subtree hints, directory uncertainty and overflow. Keep events during claim as newer generations. Route by file identity and ancestry before filtering outside events. Test a content write through an outside hard-link alias, ancestor rename/reparse/security changes above the root and during completion, excluded boundaries, case changes, directory move-in/out, same-content physical replacement, path traversal and cloud hydration/dehydration. Assert no recall and no unrelated suppression.
- [x] Test unavailable journal before a watcher hint: equal metadata still forces targeted capture. Restore continuous history before and after fallback capture; consume the whole range, preserve unproved generations and permit a conservative second capture. Lost history/overflow must queue recovery, invalidate affected proofs and preserve publication/uncertain freshness. Fallback completion cannot silently certify restored coverage.
- [x] Split metadata/target discovery from content capture without weakening current handles. For reuse, hold a write/delete-excluding no-follow observation handle, capture and durably drain a post-acquisition journal barrier, and transactionally confirm the exact candidate/dirty generation/coverage/policy while retaining source and retained-object authority. Add a timestamp-restored write between the earlier drain and handle acquisition and an open writer that aggregates USNs; both must prevent unsafe reuse. Never synthesise a content receipt. Fall back to verified capture for dirty/untrusted/missing/changed objects.
- [x] Add explicit scan scopes/manifests and exact generation completion. With due A and not-yet-due B, complete A and a separate empty manifest; B survives. Edit after claim, during capture contention and after completion; newer generations survive. Test root `.` scope in enumeration, dirty/reuse checks, deferred matching and withdrawal. Apply deletion only with exact absence authority; retain incomplete/permission-blocked entries. Keep old requests conservative full-scope.
- [x] Test `JournalPartial` explicitly: an inaccessible sibling does not force repeated reads of covered files, does not grant directory membership/deletion authority and stays durable deferred work. A newer recovery marker cannot be cleared by targeted success; uncertain routing covers every potentially affected entry. Weekly verification retains the unresolved issue.
- [x] For Git, validate full tracked membership at start/end but process only dirty/membership-delta paths. Run the stable membership check on the configured health backstop even without in-root dirty work. Authorise touched observations only, keep untouched proofs and test missed external linked-worktree control hints with no other source changes, untracked exclusions, concurrent index changes and restart recovery. Never send a partial seen set to whole-root suppression.
- [x] Retain an owned live Git capture through existing PutFileAsync retention, convergence and sealing only on incremental scans; PutCapturedFileAsync keeps rejecting Git inventory and gains no Git publication authority. Reject reuse when the existing authoritative discovery binding is absent/stale, so a crash or inventory failure after seal cannot strand an unauthorised revision. Use the same validated repository-identity/generation snapshot for completion and withdrawal. Test qualified baseline/quiet/edit with persistent readers, no sibling reads, existing retention calls, post-seal crash/higher-claim recovery, track/untrack and identity changes, and capture disposal on failure/cancellation.
- [x] Separate targeted scan metrics/issues from complete-root inventory and cadence. Verify one-file success does not erase unrelated failures, postpone recovery/verification or run partial-authority issue compaction.
- [x] With continuous successful targets, prove health/Git membership, baseline/degraded reconciliation and weekly verification still become due on their independent deadlines. Keep the existing post-acquisition reuse barrier, namespace/deletion and weekly integrity guarantees intact.
- [x] Queue a journal request before claiming an overdue root and repeat that order across multiple deadlines. Promote each fresh owned manifest to a paced reconciliation so continuous targets cannot bypass authority checks. Preserve a frozen targeted retry unchanged when its deadline passes, then promote the next fresh manifest. Keep one owner and the existing completion timestamp contract.
- [x] Merge a newer uncertain metadata signal into an existing root pending row during a genuine frozen namespace sweep. Reuse qualified content without source/retained reads or a fresh checksum time; complete the older scan while preserving the newer row/generation and incomplete namespace. Negative cases retain recapture for later content, ScopedNamespace or GitMembership merged with metadata. Do not couple metadata-only debt to content hashing.
- [x] Run existing physical convergence, cloud path/pipeline, scan lease, source lifecycle/restart, watcher, repository recovery and retained integrity regressions plus the new cases. Exercise source edit → SQL durable work → worker → retained/current publication → REST, CLI and MCP readback with deterministic processors.

**Effort checkpoint:** after milestones 1 and 2, demonstrate the real interface result and byte/enumeration counters. If it is not executable, stop adding UI/scheduler infrastructure and resolve the specific blocker. Review the journal/cursor, scope/withdrawal and reuse invariants independently before continuing.

## Milestone 3 — paced weekly verification and operational visibility

**Deliverable:** configurable weekly verification runs quietly, resumes after interruption and reports incomplete work truthfully.

**Core checkpoint, 25 October:** the independent assembled-core review is approved after the dispatch-first authority-deadline correction: three focused and 109 combined SQL/native checks pass without skips. The latest isolated app smoke retained exact current text across three same-size/timestamp-restored edits with a persistent reader; CLI/HTTP/MCP agree and the sibling revision/artifact is unchanged. Measured publication times were 3,463.5/2,924.7/3,194.6 ms. These are small-fixture smoke measurements, not the paired large-fixture comparison or actual OneDrive qualification. The unexplained reread was diagnosed as later metadata-only debt forcing content capture and is covered by positive/negative generation tests. The native owner, source digest and paced retained reader have independent approval with 37 native checks, zero skips. Four initially failing cancellation cases now reject observations and release handles; paced retained reads disable stream read-ahead. Durable weekly execution and UI are not implemented yet.

**Native implementation checkpoint:** independently review the design's single dedicated completion-thread owner before implementation. First deliver an executable RH lifetime probe through real native calls: grant, unbroken native-state check, prompt external-break release while the verifier is stalled, clean local cancellation and terminal resource disposal. Then add chunk hashing, authority revalidation and persistence; do not build scheduler/UI ahead of this integrity blocker. A native cancellation request is never completion evidence.

**Files:** add verification policy/run domain types, `ISourceVerificationStore.cs`, `SourceVerificationService.cs` and SQL implementation/entities. Extend existing source configuration/view contracts, source settings/detail components at their current paths, and corresponding REST/CLI/MCP adapters. Modify content capture/retained verification to consume a shared read-budget abstraction without weakening checksum or cancellation checks. Update setup/API documentation and architecture; do not regenerate the user manual.

### Durable execution slice, independently approved approach, 25 October

The next observable result is a manual two-file verification cycle through the existing SQL request/job/outbox dispatcher and root lease, with durable progress available through the application service and then CLI/REST/MCP. It remains behind the incremental feature flag. Calendar and existing-page controls follow this executable integrity result. Check effort after the first two coherent implementation batches; do not build scheduler/UI ahead of a failed ownership or proof invariant.

- Reuse `SourceScanRequest` kind 4 with a nullable verification run binding. A composite scanner delegates ordinary requests to the existing worker and verification requests to the bounded verifier. Root lease renewal, retries and shutdown remain in `SourceReconciliationService`. Verification results never claim complete inventory/reconciliation, update ordinary scan timestamps/totals/issues, withdraw revisions or acknowledge pending journal work. All source projections must exclude kind 4 from ordinary scan summaries.
- A serializable root-fenced transaction creates at most one unfinished cycle per root, freezes the current epoch's finite file membership and expected path/file/revision/artifact bindings, and enqueues its first batch atomically. A missing/incomplete baseline cannot create an empty successful cycle. Persist configuration/policy/recovery/namespace generations and the original policy as audit evidence; new policy revisions, pause and windows control subsequent work, without rewriting original audit evidence. New inventory membership belongs to a later cycle. Root-wide completeness/deferred evidence remains a prerequisite for a clean cycle result.
- One fixed exclusive-open lock file in the existing protected installation Runtime directory holds the single machine reader slot for the whole bounded batch, including native cancellation/terminal disposal and retained-handle disposal. Never delete/recreate it or use delete-on-close; normal SQL loss cannot release this physical ownership. Acquire non-blockingly and requeue a busy batch rather than waiting under its root lease. No SQL transaction spans file I/O or pacing. Check root/job/policy authority and foreground work between chunks, fail closed on uncertainty, and release the slot only after all native ownership ends. All instances of one deployment use the same canonical protected path; isolated staging has its own slot and data.
- Give ordinary released jobs and due reconciliation priority. Do not enqueue a batch while the independent authority deadline is due. Between chunks check current policy/pause/window/lease and new foreground obligations; yield unfinished files with no successful checkpoint. A completed batch schedules a fresh ordinary control request no sooner than five seconds later, or the next daily quiet window. Do not hold a root lease, native handle, reader slot or transaction between windows. Successful files remain checkpointed across job failure/reclaim. Reuse existing journal-service cadence for due-cycle/batch enqueueing rather than introducing another hosted scheduling framework.
- Capture the owned item/current revision and incremental generations, then hash source and retained content using the approved native reader and the same token budget. Record actual successful read bytes separately from granted credit, including bytes from cancelled/failed attempts. Checkpoint the historical digest at attestation time T only after the closing journal barrier has drained and a fresh transaction confirms the exact owned job, unchanged root configuration/recovery/epoch, item binding, current revision and physical identity. Keep later pending generations untouched; content obligations that invalidate the candidate send the item to normal capture/debt rather than falsely verifying it.
- Retained-blob deduplication is limited to this cycle and exact hash/length/physical identity/stamps. A cached receipt retains its actual hash time, is revalidated by a metadata observation before use, and is never represented as a new content read. Missing/corrupt blobs remain preserved and visible issues; verification cannot overwrite or repair them. An unsuccessful or cancelled hash never refreshes artifact checksum time.
- A revision changed during a cycle may resolve its frozen item only after a successful successor capture during that cycle with current authoritative discovery and retained checksum evidence. Git capture before failed membership completion is insufficient. Authoritatively deleted/out-of-scope items have distinct resolved states, not `Verified`. Access, unsupported native grants, cloud availability, gaps and persistent errors remain debt; finish as `CompletedWithIssues` or overdue instead of `Verified`. No opportunistic item completion clears baseline/recovery/namespace obligations.
- Persist installation `TimeZoneInfo.Local.Id` when initialising an absent policy (`{}` is not a UTC choice). Existing pure schedule helpers govern weekly cycles and daily continuation. Manual `Verify now` explicitly overrides the quiet window for that cycle; pause/resume is separate from automatic-weekly enablement. Preserve one active cycle and show actual policy, next window, completed/verified/resolved/deferred counts, source/retained bytes and reasons on the existing source detail page and equivalent native surfaces.

Required focused integration checks: atomic cycle/request creation and concurrent start; both ordinary/verification claim orders; two roots competing for the global slot; lease/session loss, process restart and cancellation with delayed native disposal; no ordinary timestamp/count/issue changes; partial-read/cancellation byte accounting; completed-item resume and unfinished-item retry; source writes before/during/after hashing, including restored timestamps and persistent readers; later pending generations; policy/pause/window changes; missing/corrupt/deduplicated retained objects; failed Git completion and successful successor capture; journal unavailability/history loss; incomplete baseline/namespace and authoritative deletion. Use native readers for the first real two-file slice and controlled boundaries for deterministic SQL races. Wider performance and actual OneDrive qualification remain the next gate.

The independent review approved this bounded architecture after replacing the SQL-session slot with physical file ownership. Apply explicit kind-4 exclusions in fallback watcher merging, journal dispatch, recurring scheduling and ordinary projections, while retaining same-root active lease exclusion. Pre-emption notices durable work even before its debounce expires. Source attestation time and retained hash time are separate historical evidence; a successor needs a real in-cycle source/retained capture receipt with current Git discovery, not merely an old artifact checksum timestamp. Two-process competition during SQL disconnection and delayed native disposal is a required runtime test. Architecture approval does not qualify the implementation or authorise a production action.

**Manual milestone, independently approved:** 74 combined SQL/native checks and 27 source/pipeline projection checks pass without skips. A two-file cycle uses the actual native readers and existing root claim, reads exactly 160 source bytes plus 160 retained bytes, persists both receipts and releases its handles/physical slot. Busy-slot refusal reads nothing and requeues after five seconds. Immutable artifact acquisition timestamps and ordinary scan summaries remain unchanged. Source and retained actual hash times are recorded separately. Pause/policy changes at the final checkpoint reject the candidate; later edits retain normal pending work.

Future parked failures carry an exact `RetryGeneration`; new journal/watch work advances `Generation` and immediately invalidates that exemption. Due debt and fresh foreground changes pre-empt even during debounce, while future parked debt and pure namespace-metadata backstops cannot starve unrelated healthy roots. Only pure metadata work receives blanket namespace pacing; mixed scoped work remains live. Existing unmarked rows stay conservative. Batch limits of 30 seconds/64 MiB apply between files, allowing at most one admitted file to finish beyond them; chunk checks still enforce cancellation, priority, ownership, pause/policy and the quiet window. Scheduler/DI/native/UI wiring, successor/deletion resolution and wider fault/scale qualification remain incomplete.

**Runtime/control checkpoint, independently approved, 25 October:** calendar scheduling, application composition, audited native controls and the read-only verification view are executable. Frozen membership can resolve through a newly hashed authoritative successor or a guarded deletion/exclusion witness without changing its original binding. Fairly rotate awaiting items, retire obsolete runs before native journal access, recalculate changed window deadlines and check pre-emption during low-rate token waits. The latest combined verifier/native/recovery checks passed 99 tests, focused domain checks passed 48 and web/native projection checks passed 165, all without skips or build warnings. Independent Astra review approved this bounded milestone; it does not approve UI, provider/scale qualification or release. Existing-page controls, remaining fault checks and the release gates below remain open.

**Existing-page control checkpoint, independently approved:** the source detail page now shows SQL-only scanning and verification progress, with a separate ten-second refresh, explicit policy editing and pause/manual controls. Audited saves retain the exact confirmation/key/payload after an uncertain result; a hold cannot discard it, while SQL's locked no-receipt expiry refusal permits a fresh action. Request sequencing prevents older successful or failed polls from overwriting newer committed status. Nine focused state/render cases, 175 combined web/native checks and two real browser cases pass without skips or warnings. Browser tests verify persisted day/time/zone/rate, invalid timezone handling, pause separation, draft preservation and desktop/narrow rendering. Remaining weekly work includes native process/session-loss qualification and actual process restart; provider/scale and release gates remain open.

- [x] Test schedules with a fake `TimeProvider`: installation timezone, local start, DST ambiguity/gap, weekly new cycles, daily continuation windows, pause/resume, non-overlap and overdue status. Proposed defaults: Sunday 02:00, six hours, 2 MiB/s combined, one reader, 100 ms between files.
- [x] Test token-bucket byte accounting across source and retained reads, 128 KiB burst, batches of 100 files/64 MiB/30 seconds, five-second inter-batch yield, priority for normal changes, cancellation between chunks and no open handles/SQL transaction between windows. Timing tests use controlled clocks; one real smoke verifies actual behaviour.
- [x] Qualify native reason aggregation before implementing a journal-only chunk certificate: retain a read-sharing handle across a pre-S overwrite/timestamp reason and repeated reopened writes between chunks; inspect `[S,E)`. Also test an already-open writer and a write after final release. If interval evidence is insufficient, implement the reviewed verifier-only RH oplock contract, with one completion/disposal owner and independent prompt break handling. Test restored timestamps, delayed managed callbacks, breaks during reads/pacing/finalisation/retirement, file/ancestor rename and deletion, existing/new writable mappings, grant refusal, cancellation/shutdown and provider no-recall. Measure writer latency while journal RPC or SQL is stalled; do not hold source guards during those waits or certify an unprovable item.
- [x] Freeze cycle membership, checkpoint successful files only, deduplicate shared retained blobs, and handle revisions changing during verification. Inject same-length source/blob corruption, missing blobs, unavailable placeholders and persistent errors; assert no false `Verified` status or refreshed checksum time without a hash.
- [x] Keep read-on-consumption integrity verification. Preserve corrupt retained bytes and existing failure reporting; no unapproved repair policy.
- [x] Expose mode, lag, last baseline, last verification, next window, progress/debt and actual enumerated/read counters. Browser/API tests verify settings persist and targeted counts do not replace root totals. Run narrow interface/browser checks and combined source/persistence checks after the coherent batch.

### Observed crash counter correction

Before closing milestone 3, atomically checkpoint the current attempt's cumulative
actual read counters with each successful file receipt; finalisation persists only
the remaining delta and its finished marker. An additive run-level incomplete
counter flag survives abandonment of an unfinished attempt, including authority
invalidation and stale-cycle replacement before preparation, and is exposed on the
existing page/native status. Previously stored feature runs are conservatively
flagged, without rewriting their receipts. Add focused finaliser-loss, replay,
deduplication and monotonicity tests, then repeat the actual staging-host kill and
restart. Obtain independent review of this bounded correction. Do not add a new
job framework, per-chunk SQL writes or changes to native source integrity rules.

**Qualified milestone 3:** the assembled weekly integrity/recovery slice has
independent Astra approval. Six focused counter cases, ten panel cases and 120
combined verifier/native/recovery/targeted cases pass without skips. Actual staging
termination after one successful receipt resumes the same four-file cycle under a
higher lease, preserves the original receipt and records 4 MiB source plus 4 MiB
retained reads as lower bounds. The test deliberately expires only the dead
fixture's lease after exclusive native ownership is available; it does not measure
natural lease-timeout latency. Actual OneDrive, scale and release gates remain.

## Milestone 4 — staging comparison, review and release packet

**Deliverable:** measured staging evidence, reviewed complete branch and a concrete incremental deployment/rollback packet. Production execution uses the existing explicit authorisation only where it covers the concrete targets/actions, after the required independent review and coordination.

### Focused qualification and release implementation

The scale gate exposed self-generated CLOSE noise after a valid capture. The
independently approved correction consumes only an exact matched seal-USN row
with pure CLOSE, no subtree and explicitly zero content/unjournalled debt in the
same transaction as the attested receipt. Intake ignores duplicate pure CLOSEs
only for the same sealed path/file ID in the current epoch, while advancing
cursor/latest-record evidence. Unknown/new paths, unsealed entries, mixed reasons,
older/later records, uncertain provenance and existing work generations remain.
Five focused failures were reproduced; 38 focused and 147 combined cases then
passed without skips, including the 10,000-row boundary and SQL rollback before
attestation commit. The actual 26,406-file journal baseline remains the scale proof.

Measure the existing full-content path and journal-enabled path in the
same frozen candidate build, alternating three paired trials against identical
26,406-file fixtures in separate prepared staging catalogues. This isolates the feature switch from unrelated binary
changes. Report this explicitly as a controlled full-scan comparison, rather
than presenting it as a historical production measurement. Keep cold enrolment
separate. Add diagnostics only to the isolated staging host: existing actual-read
hooks, completed scan enumeration, journal calls/records and counted SQL commands.
During concurrent dashboard/worker activity, SQL counts describe EF command
completion/failure events in the interval, not all SQL operations or exclusively
commands issued by the scanner. Enumeration counts managed entries processed,
not native directory names/pages buffered before cancellation. Read counters are
process-wide logical interval totals; do not sum overlapping scan deltas or claim
exclusive attribution. Do not record SQL
text or source names in diagnostic traces. Preserve normal production registration.

Extend the existing incremental updater for this exact release. Reuse the
protected SourceAccess helper host, adding its already implemented read-only
journal policy and a verified side-by-side CLI payload while preserving existing
repair policy, read grants and repair journal. Preserve task principal/settings;
change only the reviewed action and restore it on pre-activation rollback. Stop
the app before helper replacement; verify terminal process exit, payload hashes,
protected namespace and policy/configuration hashes before restart. A missing or
mismatched helper remains visible fallback, never healthy coverage. No installer,
recursive ACL grant or clean-slate deployment is involved.

The existing generic migration flag targets an old source-deletion migration and
does not cover this schema. Add a narrowly reviewed additive migration option
with exact history/target/script checks. Retain the additive schema and journal
state on application rollback; do not run its destructive Down methods. Hold
writers until the compatible candidate/helper/configuration and readiness probes
pass. Feature-off on the current candidate is the primary rollback: retire queued
verification safely without receipts and let ordinary work converge through the
full-scan path. An older binary must not start until targeted/verification work is
drained or explicitly converted under stopped, fenced ownership. Prove this
compatibility boundary in staging before a production release packet is accepted.

Accepted independent rollback review (25 October): use one explicit model-free
candidate maintenance command while writers are stopped and the deployment hold
is retained, rather than a new hosted retirement service. Bind the hold, stopped
writer evidence, database, configuration and runtime directory to the same
deployment. Acquire the existing physical verification reader slot through SQL
commit; a busy slot permits no mutation. Lock all roots, including paused roots,
before request/job ranges. Invalidate active runs with a feature-disabled reason,
preserving existing receipts and last-verification timestamps and marking any
unfinished measurement attempt incomplete. Retire held, released, running and
already-failed retryable kind-4 requests/jobs, fence old leases and make the
explicit retirement reason terminal to retry/release controls. Repeated retirement
is idempotent. Preserve kind-3 work, pending content obligations and both outboxes;
source scan claims use their control jobs directly. Test stale completion,
retry/release refusal, paused roots, busy-slot zero mutation and byte-for-byte
preservation of receipts/outboxes/content debt. This does not permit predecessor
startup: retain the older-binary prohibition and additive schema on failure.

The implemented retirement operation reuses the existing in-process candidate
operator-loader pattern; it does not add a standalone CLI route. Production roots
are fixed, isolated-test roots reject the exact production root and descendants,
and the updater's mutex/live stop callback stay on the owning PowerShell runspace.
Pinned files and native C# database/evidence guards remain valid through the SQL
commit. The caller must still supply fresh genuine stopped-owner evidence and
retain the deployment hold; a receipt or a database edit alone proves no native
cleanup. This primitive is qualified in isolation; canonical updater wiring and
full rollback/release qualification remain unfinished.

Accepted release-admission refinement: independent review accepts exact reviewed
Web/CLI/OutlookHost payload bindings instead of a new capability marker. Bind the
database incarnation, exact 66-migration baseline, twelve additive successors,
generated SQL hash and complete feature schema. Record migration intent durably
before SQL; partial or unknown states retain the hold and prohibit predecessor
startup even if the final migration row is absent. Reject the old generic
migration/Down path when intent or feature evidence exists. Compose this contract
with the existing segmented-index checks explicitly. Guard every post-feature
start and task re-enable, including ordinary updates and pre-start rollback.
Current-candidate feature-off plus stopped verification retirement is the supported
recovery. Do not infer compatibility from restored predecessor files. Migration and
payload-admission primitives have independent approval after real disposable SQL
tests passed both normal/idempotent application and failure after the first
committed migration. Existing content metadata and completed verification totals
remain intact. Recovery admission honours the durable intent even after loss of
the in-memory attempted flag; native payload-swap tests prove refusal before any
predecessor start or task re-enable. The idempotent counter backfill uses dynamic
SQL so SQL Server does not parse the new column before its conditional creation.
Canonical updater/helper wiring remains a separate release requirement.

The first controlled manual-sync trial found a separate dispatch defect: the
native command committed its released request but omitted the local scan wake.
An idle host could wait for the 15-minute scheduler tick. The failed trial is
retained. The command now uses the existing post-commit wake path, including a
durable replay after an uncertain commit; cancelled commits emit no wake. The
full 50-case native corpus action suite passes without skips, and independent
review approved the correction. Restart controlled comparisons on the same new
frozen candidate in both modes.

Scale stop-loss correction (25 October): the real 26,406-file baseline captured
all files but its completion query timed out after 30 seconds while excluding the
large seen-ID array through `NOT IN (OPENJSON(...))`. The failed run is retained as
failure evidence, not a completed cold timing. Independent review approved
root-scoped SQL GUID keyset pages of revision ID/path candidates, with the immutable
seen set filtered in managed memory. Advance from the last raw page row even when
the entire page is seen or outside scope; keep comparison and ordering in SQL and
fully materialise each bounded page. Preserve existing per-256 absence guards,
publication/root/manifest fences, final namespace generations and exact pending
acknowledgements. Prove filtered first pages/unseen tails, mixed/excluded records,
reappearance/cloud access, cancellation and ownership loss. Add no index unless
the measured paged plan requires one. Same-epoch partial-baseline sealed receipt
reuse is a separate bounded optimisation and must not be folded into this deletion
query correction without its own invariants and tests.

The correction passes six focused SQL/native cases and the 65-case combined
completion, journal-store, sealing, targeted-scan and recovery group with no skips.
Independent review found no blocking defect in the bounded implementation.
The real staging-host completion remains a release qualification requirement.

Same-epoch baseline retry refinement: independent review accepts reuse of an
individually qualified version-3 sealed receipt while state remains `Baselining`,
provided the existing root, lease, epoch/recovery, profile, source/retained binding,
post-acquisition journal barrier and pending-content fences all pass. This changes
content reuse only: every baseline still walks the whole namespace and obtains its
normal completion authority. A receipt cannot certify namespace completeness or
acknowledge newer work. Preserve the Git discovery-authorisation restriction.
Prove failed completion followed by unchanged zero-read retry, later edits,
qualification/version/epoch loss, and an incomplete retry namespace.

Seven focused variants and the combined 72-case SQL/native group pass with zero
skips and no build warnings. Independent implementation review found no blocking
correction. Keep the actual full-size retry and paired timings as remaining gates.

The full-size retry exposed 101 older directory watcher obligations classified as
unknown content while baselining. These correctly refuse reuse and must not be
rewritten or discarded. Independent review accepts a fresh full-baseline manifest
on the first Prepare of every higher lease, bound to that lease and superseding
the prior manifest even when generation has not advanced. Subsequent Prepare in
the same claim remains immutable; targeted retries retain their existing contract.
This permits one real recapture and exact acknowledgement of existing debt, while
later generations survive. Future watcher classification may treat
`Baselining && RecoveryInProgress` as available journal coverage only with the
existing available-checkpoint, approved-volume and current-policy conditions.
Subtree hints then retain scoped namespace debt, without certifying completeness;
unsealed files still require capture. Keep all pending-content and Git fences.
Prove both notification orders, fresh-claim versus same-claim freezing, exact and
later-generation acknowledgement, unavailable journals, policy mismatch,
unsealed captures, Git membership and incomplete namespaces.

The ten focused cases pass after the semantic regression reproduced seven
failures; the combined completion, journal, sealing, watcher, targeted and restart
group passes 95 cases with zero skips. Independent implementation review found no
blocking defect. The frozen full-size baseline still owes its existing content
captures; do not present that conservative recovery cost as unchanged steady state.

- [x] Build identical synthetic 26,406-file fixtures on I: with declared total bytes, directories and deterministic 512-byte content. Measure capture/current retained publication with text indexing disabled, separately from previously qualified full indexing interfaces. Keep binary, blocked, excluded and cloud-simulation correctness cases separate from this controlled scale fixture, and report their results independently. Reuse existing instrumentation hooks (`SourceBytesRead`, `RetainedBytesRead`) and add enumeration/journal/SQL counters. Use independent staging configuration, endpoint and source/artifact stores; keep test principals unable to access production.
- [x] Prepare and independently review the operator-only in-place journal resize for E:/I:. The user requested 1,024 MiB maximum and this design selects 128 MiB allocation delta. Validate an existing journal, stable exact volume/journal identity, cursor range, no deletion-in-progress and at least 2 GiB free before each mutation. Capture before/after receipts; preserve any already larger configuration; verify the active application's cursor remains readable. The runtime helper cannot resize. Keep the larger journals on application rollback. Execute only within the existing exact-scope authority after production-operation review and coordination; a changed target requires direction. Test guard/failure handling against a disposable volume or simulated operator boundary before live use.
- [x] Compare full-scan mode and journal mode in the same frozen candidate on identical separately prepared fixtures in three alternating paired runs for unchanged/manual sync, one edit and a 100-file burst. These support the comparative performance claim. Keep journal capacity consistent within each pair and record it. Separate cold baseline cost; record median/range, bytes, enumeration, native/SQL work, current-publication latency, journal lag and dashboard HTML responses. The historical 1,157-second production scan is context only.
- [x] Separately qualify candidate directory moves with old/new membership and publication, no unrelated suppression, and moved/unaffected read counts. Edit a sealed file while watchers are unavailable, restart and prove native journal catch-up, exact current content and preservation of later work. Reuse unchanged focused gap/fault evidence with the completed full-scale baseline: coverage invalidation, durable recovery and eventual completion remain mandatory; only report full-scale recovery timing when measured to completion. Exercise weekly verification through multiple batches of the large inventory, measure foreground publication and HTML responses during hashing, then prove resumption through a completed cycle with exact receipts and byte accounting. Report these checks separately from the paired comparisons. Existing controlled lease-expiry and crash evidence does not establish natural-expiry latency, and HTML timings do not establish browser-render performance. Independent Astra/high approved this focused qualification-method amendment on 25 October; no paired recovery comparison is required.
- [x] Acceptance is zero unchanged content reads for qualified coverage, zero enumeration between authority reconciliations, and bounded measured metadata work at the independent backstop, no unrelated reads for a single edit, correct visible results, bounded verification rate and preserved integrity/recovery. Measure unsealed/unknown fallback cost separately; do not hide it within the sealed claim. Target publication latency should improve materially over the paired baseline, but publish actual timings and residual queueing rather than inventing a percentage. Record timestamped before/after journal bounds and required cursor readability. The accepted release/baseline window supplies a short same-ID append observation; idle/build workload retention remains uncharacterised and is optional operational follow-up. Never promise fixed retained days or resize automatically.
- [x] Obtain independent complete-change review, including migration, concurrent intake, baseline races, namespace/privacy, rollback and the specific permission capability. Resolve blocking findings; repeat only affected tests/review areas.
- [x] Update affected roadmap progress with weighted delivered gates and architecture with implemented behaviour. Integrate current main and run canonical `scripts/dev/complete-feature.ps1 -FeatureWorktree <task path> -MainRoot 'E:\LLM KB' -CommitMessage <final scope> -KeepWorktree` without `-GoLive`. The Improve chat has finished; its merge/deployment coordination requirement is retired. Load isolated test bindings from the private fixture configuration in-process. Report/fix `failed_step` and `log_path` on failure; never substitute manual squash/merge/push.
- [x] Extend and inspect the routine incremental updater's `-PlanOnly` for the application, additive migration and exact helper capability/payload. Preserve repair configuration and helper state unless the approved packet explicitly changes them. Prepare feature-off rollback and old-binary request drainage. Get independent exact-target technical review and the required explicit operational approval before Apply.
- [x] After authorised deployment, use bounded live journal/queue/health, exact capture/readback bindings and preserved ownership checks. Observe natural changes when available; do not infer steady-state byte savings or natural-change latency from a still-running initial baseline. Independent review accepted this bounded live scope on 26 October. Do not force a production full scan or inject/delete production files without its specific approval. Only then close out and clean this task's worktree through the required mechanism; preserve other chats' worktrees.

## Verification commands and evidence

Focused test class names to create or extend: `SourceJournalReaderTests`, `SourceJournalProtocolTests`, `SourceJournalStoreIntegrationTests`, `SourceJournalRecoveryIntegrationTests`, `SourceJournalCompletionIntegrationTests`, `IncrementalSourceScanTests`, `TargetedSourceScanIntegrationTests`, `SourceVerificationSchedulerTests` and `SourceVerificationIntegrationTests`. Put native/SQL cases in the integration project using the existing opt-in fixtures; parser, state and controlled-clock cases belong in domain tests. Use controlled clocks/barriers for debounce and concurrency, not arbitrary sleeps. The executor may split a class by responsibility without dropping any specified invariant. Optional permanent-wait tests apply only if that architecture is selected later; do not add the feature merely to satisfy a test list.

```powershell
# Run from the task worktree. Read isolated test-sql.json into process variables;
# do not print credentials or put them on the command line.
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter 'FullyQualifiedName~SourceJournal|FullyQualifiedName~IncrementalSourceScan|FullyQualifiedName~SourceVerification'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~SourceJournal|FullyQualifiedName~TargetedSourceScan|FullyQualifiedName~SourceVerification'
```

Expected: all selected cases pass, native/SQL tests actually execute under explicit staging bindings, and no new build warnings. Do not count skipped opt-in cases as evidence. Existing regression class filters should be selected from current files after integration, not guessed. The canonical closeout supplies the complete suite once the branch and dependencies are settled.

Keep private measurements, raw diagnostics and operational receipts outside Git under `I:\Codex Workspaces\Operational Evidence\FluxKnowledge\incremental-source-scanning-20261024`. If reusable scripts are needed, read the scripts-library index once and keep parameterised helpers indexed under `E:\Codex Workspaces\Scripts`; task-private data stays on I:.

## Implementation checkpoints (historical)

The bounded helper-upgrade flow preserves the existing task definition/security, source policy, root-read plan and repair journal. Seventeen physical-file/fake-task cases pass, including post-start policy drift and ambiguous predecessor recovery; neither case may leave an unconfirmed SYSTEM helper running. Shared installer support is extracted from the existing installer and loaded from exact hash-pinned source bytes. The journal client now uses minimal rights and authenticates SYSTEM before disclosure: 42 pipe/broker cases and six scheduling/SQL cases pass, including a non-SYSTEM fake server receiving zero bytes and unchanged durable cursor/coverage. Independent Astra review approved these bounded changes. Actual SYSTEM-task qualification and canonical updater activation remain outstanding.

The native task adapter has passed 16 pure XML/security/binding cases and independent review for disposable qualification. It preserves task security and every definition property except global enablement, captures native process incarnations before stopping the exact task, and requires fresh task/CIM absence plus terminal captured handles before replacement. Callback execution, actual task-definition stability and native cleanup still require real staging proof. The `Cli` admission role binds the existing protected helper payload, also used by stopped retirement; no new CLI installation is introduced. Admission covers the updater-managed installed Web/helper-CLI/Outlook set and does not claim control over manually launched CLI copies.

Canonical pool-start, Outlook-reenable and hold-release callbacks now nest journal admission inside the existing index-format fence. Feature schema/history, recovered intent or enabled journal configuration require exact reviewed hashes for all three installed payloads and the database incarnation. Configured journals cannot start against a restored baseline database. Eleven canonical callback cases pass with actual fixture files and substituted SQL observations; independent review approved this bounded gate. Actual composed SQL admission, generic migration/rollback rejection and activation remain to qualify. The fresh timing harness has independently approved pending-work classification, monotonic completed-tail confirmation and failure accounting. Single-edit pair four is excluded because a local contract-test recursion overlapped its full-scan run; replace the pair with trial seven under the same frozen measurement protocol.

The exact 24,343-byte EF-generated additive SQL artifact is retained in `scripts/deploy/source-journal-up.sql` with byte-preserving attributes and the existing pinned SHA-256. A bounded held read and private clone supply immutable decoded text to execution; replacing the path or mutating the input array cannot change the commands. The actual executor checks the opened connection's exact database incarnation before DDL and does not retry or run Down. Four byte-contract cases and three real SQL migration cases pass, including initial migration through the actual executor, idempotent verification, wrong-target refusal and first committed migration failure. The pre-journal counter-backfill case remains separately covered. Independent review approved this executor; canonical activation must still invoke it through the stopped durable-intent boundary.

The first paired 26,406-file observations were manual-sync drain 464.197 seconds full-scan versus 3.975 seconds journal, and single-edit publication 273.596 versus 4.232 seconds. These are individual diagnostic pairs, not final medians. The private multi-table observer deadlocked against intake during the first burst. Its replacement uses separate autocommit observations, bounded SQL-1205 retries with elapsed time retained, and generation/version-stable quiet tails; samples are explicitly non-atomic. Preserve the original failure and results. Run fresh trials four to six with one frozen final candidate and the revised observer; do not pool mixed measurement protocols. Recover the interrupted request through normal lease expiry and startup before measurement, without editing its lease.

The original design and token-shortage pauses were lifted. Initial amended decisions were committed as `400c65e9`; subsequent implementation remains in the dedicated task worktree. Astra approved the assembled core after the deadline correction described above. The user selected guarded per-file CLOSE subject to staging and actual OneDrive qualification. Weekly runtime/UI, paired benchmark, actual OneDrive and complete-branch qualification, merge and deployment remain unfinished. The user has disabled the production app pool and authorised enabling it when required; leave it disabled while isolated work suffices. Production operations still require their exact-target packet and independent review. The Improve chat has finished and needs no further coordination; coordinate merges/deployments only with any still-relevant authorised chat. This documentation step provides no new operational approval.

### Accepted successor implementation refinement

Add a nullable candidate binding alongside the immutable frozen binding. Resolve current candidates through exact inventory/path lineage and rehash through the existing verifier. Classify original versus successor by revision/artifact/file identity, not incidental observation time. Preserve original bindings and real receipt timestamps. Add versioned guarded deletion/exclusion witnesses, closing barrier/current-state validation and preference for a current successor. Tests cover preparation/checkpoint races, same-revision recapture, failed Git completion, deduplication, immutable audit binding, deletion/recreation, untrack/retrack, pre-journal/pre-cycle/supersession rejection and access/cloud debt. Independent Astra/high approved this focused alternative with these conditions; implementation and release qualification remain outstanding.

### Runtime liveness review corrections

Independent review required fair resolution pages, retirement before native journal reads and refreshed cached windows. New regressions reproduced all three failures, plus a low-rate token wait that postponed pause. Resolution now checks `Pending` then the least recently attempted `AwaitingCapture` items through a matching index; metadata resolution attempts advance past the previous maximum timestamp so a full unavailable page cannot hide a ready successor, including after a clock reversal. Preparation validates the owned active/current run before native barrier work, then revalidates afterwards. Policy changes recompute the cached window deadline while preserving the five-second floor. Insufficient-credit waits retain token arithmetic and the initial128KiB burst, use the existing authority/priority check and cap each sleep at one second. All5 focused liveness cases pass,0skips; combined qualification is pending. These checks do not count metadata attempts as checksum reads or refresh receipts.

### Canonical release composition and held transport proof

The recovery gate runs after native application stop and before any payload file
restoration. Any refusal, including an unavailable database observation, latches
forward recovery for the whole operation. A later baseline observation cannot
clear that refusal or restore only the companion. Generic generated SQL and Down
are refused before opening their input when feature/configuration/intent evidence
exists. Failure after mutation keeps the compatible candidate set, predecessors
for inspection and the deployment hold; a stop failure is reported without
claiming native cleanup. The safe intact pre-intent baseline rollback remains.

Independent Astra/high review accepted this placement and required the sticky
unavailable-first/baseline-second correction. Physical-file regression coverage
is prepared; execution is deferred until the controlled timing trials finish.

The existing protected helper remains the managed `Cli` role. Pre-migration
helper validation binds its exact task/process incarnation, payload, repair policy
and journal policy; it reports transport validation as deferred. An administrator
cannot substitute their own identity for the configured IIS identity. Before hold
release, a narrow direct-loopback, held-only diagnostic must run through the actual
configured public clients under IIS, successfully query and read each approved
journal volume within an absolute deadline, and confirm authenticated repair
transport. It advances no durable cursor and performs no source-content read.
An intentionally unresolvable repair request must return `NotApproved`: an
approved readable request could retire an existing repair intent, so it is not a
read-only diagnostic. The query/read success and pinned shared policy SID establish
the caller identity; positive repair behaviour remains qualified on disposable
synthetic staging files. The diagnostic returns counts/status and caller identity,
not journal records or private source paths. Missing hold, forwarded/non-loopback
requests, timeouts, cursor mismatch and changed hold all refuse success. Astra
accepted this two-stage composition; it adds no helper authorisation or
impersonation privilege. Endpoint, composed SQL and release-flow tests remain
required before any production packet is accepted.

The canonical composition also binds repair mode before activation, preserves an explicitly disabled recovery mode, and rejects only an enabled app with a disabled helper. Feature-off retirement remains independent of helper policy/transport health. The CLI writer fence exempts only one exact SYSTEM source-access serve instance; same-path SQL commands, dotnet-hosted CLI commands and unknown identities refuse. Interrupted helper phases are recovered from the protected checkpoint for the existing held release after process loss. Both locators accept the existing scoped rebuild hold shape as well as the ordinary string form. Prepared and verified terminal helper phases are distinguished from interrupted or malformed records. Independent Astra/high approved these bounded corrections statically; all new execution and live evidence remain pending until the controlled timing matrix ends. The paired dashboard HTML observations retain staging's LegacyFenced read mode, so they are not production Snapshot-mode or interactive-render timings.

### OneDrive compatibility reassessment

The user clarified that qualification covers compatibility risks introduced by
guarded CLOSE; general OneDrive sync troubleshooting is outside scope. Independent
GPT-6 Astra/high reassessed the existing native receipts and matched controls and
accepted this narrow qualification without another provider probe. Eight actual
hydrated-file capture/seal cycles preserved bytes, timestamps and physical identity,
matched the returned CLOSE and detected subsequent timestamp-restored edits with
a persistent reader. The matched sealed/unsealed files both became provider-confirmed
synced after the readers closed, and unchanged captures retained sync for both.
Both later ordinary-write controls timed out. The separate unpaired final-sync
timeout remains unresolved; the original composite failures remain failures.

This is empirical compatibility evidence for the tested hydrated-file workflow,
Windows/filter fingerprint and OneDrive Business 26.189.0928.0007. It is not a
general synchronisation guarantee. Retain exact physical-root, provider/version
and environment qualification checks, with scoped fallback when qualification
is absent or changes. Production root binding remains part of the reviewed release
packet. Reopen this question if matched evidence shows sealed-only sync impairment,
changed bytes/identity, missing later change records or a changed fingerprint.
This decision supersedes the pending general-sync delivery-scope question; wider
staging, native helper, complete-branch and production release gates remain open.

### Namespace debt and qualification scope

A future pure root `NamespaceMetadata` row with zero content cut-offs is not, by
shape alone, a harmless scheduler marker. Journal intake can create it for an
unresolved parent and mark namespace uncertainty. Routine reconciliation instead
adds a generation-zero manifest target. Content publication/drain timings may
exclude the explicitly deferred obligation, but must report `JournalPartial` and
namespace completeness separately; they do not establish an empty queue or clean
namespace. Verification must preserve this uncertainty rather than exempt the row
from its clean-cycle check.

Before the large weekly qualification, complete native tests and then let the
ordinary staged host establish complete namespace, no unresolved bound, no pending
work and no deferred evidence. Independent Astra/high approved advancing only the
exact isolated future obligation's deadline with one conditional, version-bound
update, retaining its generation, reason, authority fields and before/after proof.
This avoids the fixture's 86,400-second wait without manufacturing eligibility.
Run it outside accepted timing windows and report it as controlled fixture time
advancement. If ordinary reconciliation cannot prove completeness, stop that
qualification and diagnose the retained evidence. Production remains separate.

### Latest staged qualification, 25 October

The accepted comparison now contains three alternating pairs per case in the
same frozen candidate: manual-sync and burst trials four to six, single-edit
trials five to seven. Single-edit pair four remains excluded because an unrelated
test consumed CPU during its full-scan run. Median content-work drain was
599.874 versus 2.445 seconds for manual sync, 824.406 versus 7.231 seconds for one
edit, and 684.286 versus 19.627 seconds for the burst (full-scan versus journal).
Median current-content publication was 444.713 versus 4.158 seconds for one edit,
and 306.199 versus 15.465 seconds for the burst. Manual sync has no publication
latency. Journal source reads were zero, 1,024 and 102,400 bytes respectively,
versus approximately 13.5 MB in each full-scan case; journal namespace enumeration
was zero. These synthetic text-indexing-disabled measurements report content-work
drain with a deferred namespace obligation retained, not clean namespace or empty
queues. HTML responses still sometimes took about two seconds; do not claim a
universal dashboard response improvement. Preserve the complete median/range,
counter, failure and protocol receipts outside Git.

The actual composed SQL check exposed incompatible cleanup and journal database
identity formats. Keep the existing cleanup identity unchanged and observe a
separate nullable database-incarnation identity in the same serialisable read.
Journal composition requires that identity to be present and match exactly;
missing, null and wrong values refuse admission. All three actual SQL migration
and composed-admission cases pass, with no skips. Twenty-six held helper-health
cases and the focused native recovery, activation and checkpoint cases pass.

The task adapter now accepts Task Scheduler's normalised COM security descriptor
without requiring its protected control flag, while requiring a present non-null
DACL, trusted owner and no untrusted allow ACEs, including inherited grants.
Full returned security and definition fingerprints remain pinned before actions;
installer and filesystem ACL checks are unchanged. Every observation requires
native service-account logon type 5; absent optional XML LogonType is accepted,
but conflicting XML or unavailable/wrong native values refuse admission. The
returned XML is not rewritten. Twenty-seven pure assertions and eighteen
physical-file/helper-control cases pass, including descriptor drift refusal.

Disposable SYSTEM qualification passed successful upgrade with actual restricted
caller journal query/read and repair transport, policy drift after candidate
confirmation and external proved stop, and failed predecessor-probe confirmation.
The live policy lock prevented the original concurrent-write injection; retain
that observation without calling it drift qualification. Revised drift preserves
unknown policy, candidate and inspection predecessor, leaves the task disabled
with native exit proved, and survives a fresh-process recovery check. Failed
rollback confirmation also proves terminal disabled/exited state. Qualified tasks
are removed; payloads and receipts remain for inspection. This is staging proof,
not production deployment approval. Combined native regression, candidate move
and restart scenarios, large weekly completion, complete-branch review and the
exact production release packet remain required. Keep the production pool stopped
whenever production is not actively needed until the fix is deployed.

Final composed review found that complete schema and matching candidate hashes
could conceal an interrupted helper upgrade. Boundary observations now retain
`HelperRecoveryRequired` separately and refuse pool start, companion enablement
and hold release while recovery remains unresolved. The sole explicit exception
is feature-off recovery after successful bound stopped-reader retirement: each
action rechecks both disabled configuration flags, the exact configuration hash
and the existing three-role/database/schema admission. It introduces no helper
health dependency. The regression reproduced the defect; twenty-seven installed
admission cases now pass, including fourteen helper-recovery checks across both
in-memory and persisted recovery, all three actions and negative escape proofs.

The release-binding observation found OneDrive had updated to 26.189.0928.0009.
The version fence correctly rejected the older qualification. Fresh unchanged
native tests reproduced eight actual capture/seal/later-change cycles on 0009,
and the matched no-edit capture control passed with both files provider-confirmed
synced. Independent Astra/high accepts narrow compatibility for this tested
hydrated workflow, with exact physical-root/provider/environment binding. The
composite remains failed at final synchronisation; the older edited matched
control is context rather than current-version post-edit sync equivalence. No
general sync guarantee or further general sync investigation follows. Production
configuration remains unchanged; only the private proposed packet is updated.

The first candidate move began under the retained namespace uncertainty and
therefore does not qualify clean-start deletion completion: 264 renamed files
published with 135,168 source bytes and a 264-entry targeted scan. Its aborted
result is retained. Independent review accepts completing
ordinary metadata reconciliation before repeating the move from a complete
namespace. No trust flag, pending obligation or inventory row may be edited to
manufacture that starting state. Provider-fixture cleanup must finish before the
final namespace pass and large weekly run.

Ordinary reconciliation subsequently completed in 831.952 seconds for 26,507
managed entries, with zero source or retained content bytes read and zero failed
SQL commands. Namespace authority is complete, with no unresolved bound, deferred
evidence or pending work. The inventory retains 264 historical entries whose
exact bound source revisions are suppressed: 26,670 raw inventory rows represent
26,406 current eligible files. These historical records must remain intact.
Candidate qualification uses the integrity verifier's complete exclusion rule,
including root, baseline epoch, binary canonical path, origin and matching
artifact owner/hash/length. Missing or mismatched bindings remain eligible debt.
Report raw, eligible, suppressed-history and other-epoch counts separately. The
same rule applies to moved and unaffected membership, with an independent check
that no old-path source revision remains active. Fourteen read-only SQL classifier
cases pass. This correction affects only private candidate qualification; the
frozen comparative timing protocol and its eighteen observations are unchanged.

The clean-start directory move now passes: 264 current files publish at their new
paths, only those 264 entries are enumerated, no old-path revision remains active,
and unaffected current revision/artifact bindings remain unchanged. The targeted
scan took 25.061 seconds and read 135,168 source bytes; complete quiet-tail
qualification took 42.236 seconds. Restart catch-up also passes: an edit made
with the staged host and watchers stopped publishes in 6.858 seconds after
replacement startup, and a genuine subsequent edit remains visible. Total
logical source reads across both edits are 2,048 bytes, with zero failed SQL
commands and no remaining namespace or pending-work debt. These are single
candidate qualification observations, not comparative recovery benchmarks.

The 26,406-file integrity cycle completed after replacement-process resume:
26,405 items are verified and one changed-file successor is resolved, with no
deferred items. All 26,406 durable receipts are present, the 151 pre-pause
item IDs and content hashes are retained, and the clean verification timestamp
is set. That comparison did not hash receipt JSON or prove timestamp immutability;
the earlier four-file process-crash qualification separately proves receipt
immutability. Actual
verification reads total 13,520,384 source bytes and 13,519,872 retained bytes;
counter completeness is true. The extra source read is retained in the actual
accounting. The cycle took 87 minutes 32.902 seconds from creation to completion;
the resumed qualification interval, including its final quiet tail, took
5,194.133 seconds. A foreground edit published in 2.065 seconds during hashing.
Final namespace is complete, journal lag and pending work are zero, and no SQL
command failed. This is a paced correctness/foreground-priority qualification,
not a comparative integrity-throughput benchmark. The staged automatic policy
was disabled and the cycle was manually requested at a 256 KiB/s combined cap;
production retains its separately reviewed schedule and limit.

The journal operator's two-drive partial-Apply regression also passes. It runs
the real wrapper and durable receipt code with only native volume construction
substituted: a verified first-drive increase survives a second-drive failure,
with both attempts/disposals recorded and no retry, shrink or rollback. This
does not qualify a native production resize. Canonical warnings-as-errors,
full-suite/native checks and the exact production release gate remain required.

The first canonical full-suite run exposed deployment fixture dependency drift,
a current-source declaration selector, and a real offline-code recovery
compatibility gap. The recovery binding now uses the existing composed schema
check for recognised journal history, preserving exact incarnation, history and
both schema contracts; pre-journal admission and three-role start guards remain.
Both pre-journal and current-journal SQL scenarios, actual journal schema drift,
projection lag and retired-generation refusals pass in the seven-case focused
regression run with no skips. The native offline-code contract passes all 34
checks. The canonical aggregate must pass before production release.
