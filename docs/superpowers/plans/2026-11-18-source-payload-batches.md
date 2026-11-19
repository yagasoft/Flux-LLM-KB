# Source payload batches implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Amortise full execution-history proofs during source removal without weakening them.

**Architecture:** Introduce a separate 16,000-row purge ceiling for vectors and chunks. Keep initial withdrawal at 4,096 rows, execution pages at 1,000 tasks, all membership limits and every fresh serializable ownership/cleanup proof unchanged. There is no schema, API or persisted-phase change.

**Tech stack:** .NET, EF Core and disposable native SQL Server integration tests.

**Spec:** [Startup and indexing recovery](../../operations/2026-11-18-startup-and-indexing-recovery.md).

## Global constraints

- Full closeout runs once all implementation, deployment and live acceptance finish.
- No production test fixture, model acquisition, source backfill or original-file mutation.
- Preserve the whole-unit 30-second bound; reject the experiment if it fails.
- Existing production approval covers this core performance correction; independent release review remains required.

## Approaches

The current 4,096-row purge is correct but repeats full history proofs for millions of payload rows. A separate bounded purge ceiling is the smallest reversible correction. A new set-based ownership implementation would have a much larger correctness surface and is deferred. The independent reviewer supports the bounded experiment; improvement is not assumed.

## Review focus

- Combined 217,685-task/20,000-parent history with large payloads, rather than independent scale dimensions.
- Exact full and partial vector/chunk pages, including eligibility cascades and vectors-before-chunks ordering.
- Lost acknowledgements, cancellation rollback and continuation in a fresh CLR.
- Foreign records, source/native anchors and concurrent publication waiting.
- Measured whole calls and SQL statements, without increasing timeouts.

## Task 1: Qualify and deliver the separate purge ceiling

Reassessment: the combined purge case passed, but enlarging the existing fixture exposed a sequence-dependent timeout in initial withdrawal. A fresh database passed; running the preceding mixed-history case first reproduced the timeout. Its update unnecessarily repeats all five ownership joins for the already selected 4,096 IDs. Update and postcheck only that frozen set within the same serializable transaction, retain the final root-wide cursor refusal, and prove that a concurrent ownership change remains blocked. No timeout or withdrawal-limit change is permitted.

**Files:** `SqlSourceDeletionStore.cs`, `SqlSourceDeletionStore.Progress.cs`, the existing source-deletion integration partials and a focused payload-scale partial.

**Interfaces:** Existing `PurgeAsync` and `SourceDeletionWorkItem` remain unchanged. Reuse the exact production-sized synthetic history seed and the restricted fresh-process test harness.

- [x] Extract the unchanged 217,685-task fixture seed for reuse; add a combined payload regression that expects 16,000 rows per full page and a final partial page.
- [x] Observe the expected 4,096-versus-16,000 failure before changing product code.
- [x] Add the separate purge constant and change only the two purge payload queries. Keep withdrawal and execution ceilings unchanged.
- [x] Prove combined history/payload timing, preservation, replay, cancellation, fresh-process continuation and publication waiting; run affected source tests and a zero-warning Release build.
- [x] Review the final diff and exact incremental release plan; deploy only if qualified. Preserve rollback payloads and the existing native removal operation. Release `a669a51d` is installed with schema 80 unchanged.
- [x] Observe native removal through completion, compare original size/date metadata, finish normal retrieval checks and update operational evidence/roadmap.
- Deferred by the user: run the prescribed feature closeout only after all participating chats finish and their reviewed changes are integrated.

## Task 2: Preserve capture authority while removing repeat verification

Reassessment: ten older capture assertions failed on both integration bases. Most still expected the explicitly removed retained checksum reads. One exposed a real defect: retention used the mutable classification array after discovery had computed its digest. Preserve the first-ingestion identity by privately copying only a full bounded capture before exposure, then retaining through a read-only stream. Prefix-only captures continue streaming the held physical handle. This adds at most 32 MiB of temporary memory per full capture and no extra file read or hash.

**Files:** `LocalSourceFileCapture.cs`, `ContentAddressedSourceArtifactStore.cs` and `SourceCaptureLeaseTests.cs`.

- [x] Observe the original classification-mutation failure before the correction.
- [x] Protect the buffered bytes independently of the public classification array; preserve all path, handle and lease checks.
- [x] Assert zero retained-body rechecks, buffered-copy reuse, retained-length refusal, cancellation and both complete/prefix classification mutation. The 30 capture and 213 affected domain checks pass.
- [x] Complete the combined source/gate/replay checks and Release build before the shared deployment gate in task 1.

## Task 3: Correct startup contention classification and preserve diagnostics

Live acceptance found the index recovery coordinator in `ConfigurationInvalid` after an initially successful no-draft refresh. Current SQL metadata shows a valid active generation and contiguous projection changes. The original exception was discarded, so the historical cause cannot be established from that audit. Independent review identified a demonstrated bug: refusal of a shared maintenance lease throws a generic configuration exception and permanently stops probing. A separate unfenced state/generation read can also misinterpret concurrent retirement as invalid configuration.

Use the existing conflict type for that specific lease refusal. Read projection metadata under a short transaction with recovery ownership before the existing publication fence; release it before native work. Log the original recovery exception with process/attempt/generation identifiers, keeping public diagnostics sanitised and genuine invalid metadata terminal. Do not blanket-retry native failures or reset production state manually.

- [x] Prove maintenance refusal reprobes without candidate work and then reaches healthy; prove concurrent retirement cannot split the projection snapshot and genuinely retired metadata remains invalid. All three focused SQL/logging regressions pass.
- [x] Apply the narrow fixes and retain original exception logging; run affected recovery, segmented publication and composition checks. The combined 115 SQL checks and 276 web checks pass; eight existing opt-in web checks are skipped.
- [x] Independently review and deploy through the incremental updater; inspect live recovery and diagnostic output. Release `4004ae4c` is healthy with normal publication progressing.

CPU search warm-up separately refused its established memory headroom check and caches that fault for the worker lifetime. Independent review approved retrying only initial pre-allocation refusal after confirmed owner release. The service waits 60 seconds, coalesces concurrent attempts and cancels retries on shutdown. Later allocation/native/cleanup failures remain terminal. All 14 warm-up/shutdown checks pass. Do not lower memory thresholds, stop other workloads or acquire models.

## Task 4: Bound final audit cleanup and distinguish maintenance ownership

Reassessment: release `4004ae4c` is healthy and nine current Publish failures from the same maintenance-refusal defect have completed through normal confirmed recovery, preserving their original Embed artifacts. The native removal drained every vector, chunk and GPU task. Its final graph transaction then timed out specifically at the unbounded five-way `AuditEvents` deletion. The remaining graph has 34,891 revisions, 15,539 records, 53,439 activities, 13,116 branches, 76,334 artifacts and 76,871 jobs; the audit table has 654,575 rows. This is another core scale defect, not a document-specific issue.

Select at most 16,000 distinct audit IDs using capped ownership branches joined through the still-live source anchors. Preserve the existing any-owned-reference OR semantics, including overlapping and mixed references. Delete the frozen IDs under the same full serializable ownership/lease/execution proof and commit without deleting principals. A fresh zero-page check and final graph deletion must share one transaction. Qualify the selector, DML and complete final unit against the production-shaped metadata fanout; change other statements only if qualification demonstrates another bottleneck.

The new projection-read fence also reports maintenance lock timeout through the generic I/O retry budget. Preserve the actual application-lock return code at that boundary: only `-1` is a snapshot conflict; cancellation and other negative outcomes remain distinct. Genuine configuration and native failures retain their existing policy.

- [x] Add failing audit-page tests for all five ownership branches, overlap, unrelated controls, new events between pages, cancellation, acknowledgement loss and final completion.
- [x] Implement the bounded audit page and qualify the remaining graph with realistic metadata/audit fanout. The 450,000 owned audit events and 100,000 artifacts completed; the longest unit was 15.6 seconds, with the artifact DELETE at 8.2 seconds. The unchanged 217,685-task history is reserved for final closeout.
- [x] Prove maintenance beyond five probe cycles remains nonterminal, then recovers; retain cancellation and non-timeout refusal behaviour.
- [x] Bound final artifact pages by both 4,096 rows and 64 MiB of text plus metadata, retaining source/revision/record/job/source-artifact anchors while artifacts remain. Five focused checks prove byte accounting, one oversized item, cancellation, acknowledgement loss, replay and fresh continuation. Combined qualification passes 274 SQL checks without skips; the final graph's maximum unit is 5.324 seconds and maximum statement is 1.804 seconds. Its 100,000 artifacts are removed in pages of at most 4,096 rows. The full Release build has zero warnings and errors.
- [x] Independently review the complete change and exact incremental plan, deploy, then use a fresh native resume for the existing removal operation. Release `5e9af73e` reached the separately diagnosed final graph query defect in task 5.
- [x] Complete removal/original metadata comparison, retrieval and final documentation before the prescribed one-time closeout.

Ruling: the graph-scale fixture contained 409.6 MB of text, whereas metadata-only production length measurements show 8.30 GB plus 109.7 MB of artifact metadata. The artifact DELETE is therefore also a demonstrated unbounded payload risk. Preserve the original auxiliary deletion order, then delete a frozen artifact prefix with a 64 MiB payload budget and 4,096-row ceiling. A single oversized first item may exceed the payload budget so removal cannot stall. This is a selection bound, not a transaction-log bound; retain the measured statement and whole-unit limits. Final artifact emptiness and principal deletion share the same transaction. Independent Astra design review approved this split; no timeout, schema or persisted-phase changes are needed.

## Task 5: Keep final graph ownership sets on SQL

Reassessment: release `5e9af73e` drained all owned audit events and committed its first artifact page. Artifact selection took 279 ms and deletion 772 ms. A later unit timed out before artifact work, at the repeated `DocumentPublications` DELETE. Its cached plan scans 562 unrelated publications and reparses a 34,891-ID JSON array through two nested-loop OR arms. The preceding scale fixture contained no publications, so did not represent this cost. No further resume is authorised technically until the query family is corrected and qualified.

Use server-side root/revision/record/branch/activity/force/action/job queries while their principals remain in the same serializable unit. Preserve every OR arm, branch-presence condition, FK deletion order and fresh root/lease/native/cross-root proof. Freeze distinct source identity IDs before deleting records, then delete them in bounded chunks with the existing no-reference guard. Artifact page IDs remain frozen and bounded. No timeout, schema or persisted-phase changes.

- [x] Add a failing large-root GUID-parameter regression with 35,000 owned revisions, 600 unrelated publications, all publication ownership arms, artifact continuations and both owned-first/foreign-first cache orders. Both cases fail before correction.
- [x] Correct the complete final graph query family and prove publication, force/action/relation OR equivalence, shared/unshared identities, rollback/replay and timing with foreign controls. Five final-graph checks pass; the expanded metadata fixture reaches a 4.969-second maximum unit and 1.359-second maximum statement. Both cache orders retain 600 unrelated publications.
- [x] Independently review, qualify and deploy the exact incremental change, then seal the new recovery inventory and use a fresh native resume. Release `2bd0898c`, audit31 and a fresh native resume completed source removal at 20:46:35 UTC. Original directory identity and all baseline paths remain; the metadata comparison records eleven changed entries and six additions over the investigation period without attributing them to this task.
- [x] Complete removal, original metadata comparison, final normal UI/retrieval and documentation before the one-time closeout.

## Task 6: Preserve document Extract delivery after classified SQL failure

The final live file review found one image Extract job permanently failed after a SQL timeout while consuming a successful, settled OCR result. The existing outbox SQL deferral catch covered Normalise, CanonicalIndex, Embed and Publish but omitted ExtractDocument. Independent review approved adding only that exact stage/operation pair to the existing error allowlist and lease-preserving catch. Visio, UTF-8, cancellation, unclassified SQL and file/native failures retain their existing handling.

- [x] Reproduce the missing catch with transition rollback/commit acknowledgement and real OCR request/handoff loss cases. All six new failure cases reproduce before correction; the real handoff cases verify that the worker was called.
- [x] Prove lease reclaim/old-generation refusal, one request/task, no premature output, cancellation and excluded failures after the narrow correction. All 23 focused checks and all 166 combined affected checks pass without skips.
- [x] Decide the separately reviewed recovery for the exact historical job from its current request/task/result/native-cleanup bindings. Eleven isolated scenarios qualify the private operation, including exact-plan reconciliation and terminal successor refusal. No public API or source re-ingestion is added.
- [x] Include prevention and authorised historical recovery in final release/normal-workflow acceptance before closeout. One reviewed original-job requeue consumed the saved result and completed all five stages through publication, with original native/task/result bindings unchanged and no new OCR handoff.

## Task 7: Collect earlier generation references before materialisation

Two broader runs reproduced a timeout in unchanged initial generation-target capture. The single test and its immediate predecessor sequence passed alone, demonstrating a plan-sensitive query. The correlated generation scan asks whether each generation contains any root-owned vector. Discover distinct referenced generation IDs from the unchanged root vector query first, union the owned draft/checkpoint ID branch, then join to Format1 generation rows. Keep empty-branch checks inside the same fenced transaction. Preserve retirement timestamps, shared-reference authority, target creation and Format2 handling. Independent review approved set equivalence; a better plan must be measured rather than assumed.

- [x] Qualify membership-only, owned-only, overlap, unrelated/shared generations, both empty inputs and Format2 exclusion. Six focused checks pass; measured capture queries take 25–29 ms.
- [x] Run the broader reproducer and both cache orders under unchanged timing limits; review the final complete diff and Release build before deployment. All 166 affected checks pass without skips, the Release build has zero warnings/errors and independent implementation review approves the combined change.

## Task 8: Classify settled OCR records without a stored result

The final normal Overview check shows seven older OCR records held with uncertain task outcomes and no queued work. All were released as source-withdrawn on 17 November; each has settled native cleanup but no result. Their current logical source eligibility and retained publication lineage are valid. Before further mutation, independently assess whether missing/stale physical admission was treated as definitive withdrawal and identify the smallest safe correction/recovery. Preserve every original unknown outcome and execution proof; do not reset old tasks to Ready or Completed or call the stored-result operation for these records.

- [x] Read bounded current metadata and verify exact current logical eligibility/lineage without content reads or mutation.
- [x] Complete independent design assessment and regression qualification. Missing/stale admission is not durable withdrawal. Owned result persistence is separate from execution/continuation permission. A distinct no-result branch of explicit document reprocessing terminalises only the old parent/activity/delivery, fences both generations and preserves native uncertainty. All 341 affected checks pass without skips; the Release build has zero warnings/errors. Old binaries fail closed on the new proof, so retain compatible code after successor creation.
- [x] Review and deploy the exact incremental release, then independently review and execute the seven-record recovery through the existing document command with current physical/lineage/native proof. All seven successors have completed every stage and published; original uncertain executions remain historical evidence.
- [x] Complete the remaining four Visio files through the exact one-document command, refusing any existing user Visio session. The existing companion processed one of the original five. Its current enabled state is preserved; the earlier disabled receipt is historical and does not establish the state immediately before deployment.
- [x] Finish normal interface acceptance, documentation and independent final review before the one-time closeout. Independent Astra review approves the focused recovery handoff; shared closeout remains deferred.

## Task 9: Route prepared Visio CLI requests to the existing exact runner

The first scoped command refused `document-reprocess-not-eligible` without extraction. The CLI always uses previous reprocessing, although ordinary discovery already completed a current Visio preparation branch without a previous supersession relation. The companion runner supports this shape. Independent design review approves delegating only an exact source revision, input hash, processor fingerprint, parent processor version and Completed branch. Check extractor availability before delegation because the companion runner may clean an expired execution before its own availability check. Preserve the previous fallback.

- [x] Reproduce the CLI defect with a physical isolated source fixture; prove exact-binding refusals, availability before recovery and idempotent replay.
- [x] Add the narrow delegation, run the affected SQL tests and build, then obtain independent implementation review. All 23 scoped SQL checks pass; the Release build has zero warnings/errors.
- [x] Publish a fresh private CLI and review a new exact operation packet. Reconcile the first refusal before any new invocation; preserve spent markers and never replay an ambiguous outcome. No app restart is required for the CLI operation.
- [x] Deploy the final application payload including the integrated Overview path change and complete normal interface validation and documentation. The full closeout is deferred by the user until all participating chats finish.

## Task 10: Identify physical Visio owners by origin, including revision history

Release `5c99e918` deployed the CLI routing correction. The first exact command then reached its prepared pipeline record but returned `visio-not-claimed`, leaving attempt count zero and no extraction artifact. The claim predicate required a null owner parent, although physical file revisions retain their previous revision in that field. Use `OriginKind = 0` to identify the physical owner, as the shared ownership rules do. Keep every other exact input, hash, root, branch, suppression, admission and lease check. Independent design review approves this correction. Both old failed batches remain spent.

- [x] Reproduce refusal of an edited physical owner and its normal CLI extraction. Fourteen negative and unchanged-history controls pass before correction.
- [x] Replace the one exact Visio owner predicate; qualify suppressed predecessor history, archive/derived/null-parent refusals, suppression, wrong root/hash/branch and idempotent completion, then review the complete diff and Release build. All 53 affected checks pass without skips, the Release build has zero warnings/errors, and independent implementation review approves the correction.
- [x] Deploy the forward correction through the ordinary incremental updater, review a fresh exact Visio operation and finish the four inputs through publication or ordinary verified backlog processing.
- [x] Complete final normal interfaces, documentation and independent review before the single prescribed full closeout. Independent Astra review approves the focused recovery handoff; shared closeout remains deferred.

## Shared closeout gate

The user postponed full closeout until all participating chats finish their work. Runtime recovery is complete: all seven OCR successors and four Visio records are published, normal interfaces pass, and the final release is deployed. Preserve the tested branch and evidence; use focused checks for subsequent work. The one prescribed complete-feature run, including full solution tests and the large history fixture, remains deferred. Do not promote main or run an intermediate closeout to finish this chat alone.
