# Pipeline visibility acceptance and production gate

The dashboard and its failed-request diagnostics are implemented, independently
reviewed, verified and deployed. The installed Main candidate is
`a583d9ba641ad75753adaf8b156bb0ae1e22209b`, released through the normal incremental
IIS updater as `20261014T224441Z-a583d9ba641a`. Live scope, freshness, attention,
job/GPU detail and job pagination passed at desktop and narrow widths. All four
feature acceptance gates are complete. Earlier job-detail failures remain
unclassified; the diagnostic addition supports attribution of future failures.

## User-visible behaviour

The overview puts current activity and named source progress first. Files,
pipeline jobs, GPU tasks and source scans have separate labelled counts. Exact
totals replace `999+`. Source scope and the successful status-read time are visible.
Current queues, retries, blocked work, confirmed execution and unresolved historical
records are distinguished using revision, publication, lease and scheduler evidence.
Age alone never establishes history. Ambiguous file bindings remain provisional.

Historical failures and uncertain GPU outcomes remain accessible through read-only
paged detail. Search-index health describes the derived projection separately.
Detailed scheduler and lifetime totals are expandable. The existing Blazor shell,
components, native controls and styling are retained. No estimated completion
percentage or undiscovered remaining total is presented.

## Verification

- Independent Astra review approved the complete feature for canonical closeout.
- `complete-feature.ps1` passed all 21 steps, including feature/Main integration
  and push. Both checkouts passed 3,340 tests with 25 opt-in skips. Release builds
  had zero warnings and errors. The dedicated worktree was retained.
- Focused SQL checks passed 24 cases. State/render checks passed 53 dashboard
  cases; the combined source-preview regression run passed 62 cases.
- The separate 1,000-file backlog check accepted 10 of exactly 12 reads during
  four heartbeat writes: 40.5 ms median and 45.8 ms maximum. Every accepted read
  retained exactly 1,000 pending files. Rejected reads remain unavailable/stale.
- A cold diagnostic read observed stable fence values advancing from 3029 to
  3031 and correctly rejected the sample. The allocator remains unproved. The
  single compilation warm-up is recorded/discarded; measured thresholds, runtime
  acceptance and pending/intervening transaction tests were not relaxed.
- The opt-in browser scenario passed desktop, 390 px and 200% zoom, including
  source selection, paging, keyboard operation, stale/error recovery and scope races.
- Final Main was published into the reused, isolated Release 1 functional stage.
  Actual SQL-backed checks passed fresh idle status, one known/published file,
  scope, refresh, job/GPU detail and keyboard diagnostic expansion at desktop and
  390 px. No page-wide overflow or console warnings/errors were observed. Source
  and configuration hashes were unchanged; models, GPU, OCR and mail remained off.
- Live desktop and 390 px checks passed source selection, refresh, freshness and
  stale-state handling, separate index health, keyboard diagnostics and exact GPU
  task detail. The pipeline-job detail request displayed its generic failure and
  retained the previously loaded GPU rows with their original caption and time.
  No page-wide overflow or browser console warnings/errors were observed.
- A later bounded live check loaded 110 repository pipeline-job records: 34
  current queued jobs and 76 unresolved historical jobs. Pages 1 and 2 each showed
  25 rows; Previous returned to page 1 without changing source scope. At 390 px,
  the 542 px table remained within its 351 px scrolling container and the document
  width was 375 px. The console remained clear. The original failures are retained.
- At 01:04 Cairo on 15 October, the all-sources view showed two confirmed OneDrive
  pipeline jobs and one active GPU task. OneDrive's known inventory remained
  1,308 files, while published files increased from 188 in the earlier sample to
  280 and pending files fell from 567 to 475. These are timestamped samples;
  unchanged inventory size does not prove that no source revisions changed.
- The follow-up diagnostic regression passed separately and in the two-case
  dashboard browser run; 49 focused rendering/state checks also passed. Each failed
  attention request now logs its exception, unique request ID, requested unit,
  original source and one-based page. Late failures cannot replace a newer scope;
  private exception details remain absent from the rendered error. This adds no
  query retry or change to coherence, classification or persisted records.
- The diagnostic candidate passed all 21 canonical closeout steps. Both feature
  and Main passed 3,351 tests with 26 opt-in skips and zero failures; both Release
  builds had zero warnings and errors. An earlier full-suite attempt returned an
  unclassified HTTP 503 in an existing retrieval test. The exact HTTP 200
  assertion remains intact and now reports the response body on failure. Its two
  focused cases, complete six-case class and both subsequent full suites passed;
  no runtime fix or established cause is claimed for that earlier failure.
- Independent Astra review approved the exact candidate and normal production
  action before deployment. Installation verification matched all 149 app files
  and 124 companion files. Both predecessor payloads, production configuration,
  full scheduled task definition and hidden launcher were preserved.
- The post-release observation ran from 22:46:22 to 22:51:30 UTC on 14 October
  (01:46:22 to 01:51:30 Cairo on 15 October). All 11 liveness/readiness samples
  returned HTTP 200, worker 58656 retained its creation identity, and no candidate
  fault was detected. This is bounded runtime acceptance.
- Post-release desktop and 390 px checks passed scope, refresh, separate index
  health, keyboard diagnostics and job/GPU detail. Repository jobs numbered 115:
  35 current queued and 80 unresolved historical records. Pages 1 and 2 showed
  25 rows; Previous returned to page 1 with scope preserved. GPU detail showed
  79 scoped records. At 390 px, its 486 px table stayed within a 351 px scrolling
  container; document width was 375 px. The browser console was clear. A coherence
  refusal visibly labelled the retained last successful status as possibly stale.
- At 01:52:53 Cairo on 15 October, all sources showed one confirmed OneDrive
  publishing job, 411 queued pipeline jobs, zero active GPU tasks and three ready
  GPU tasks. OneDrive had 1,308 known, 330 published and 425 pending files, with
  11 unconfirmed bindings excluded from published/pending totals and an incomplete
  scan warning. These are timestamped observations, not a fixed remaining total.

Private receipts, TRX files, earlier controlled browser images and original failed
diagnostics are retained outside Git. Staging evidence is not production acceptance.

## Production gate and unresolved work

The incident owner completed an authorised normal incremental deployment after
independent review. Its ten-minute observation recorded 21 successful samples over
621.5 seconds with the same worker and no candidate fault. The scheduled task,
hidden launcher, configuration and predecessor rollback payload were preserved.
This is bounded runtime acceptance; the native IIS cause and prevention remain
unproved. See the targeted incident acceptance record.

The earlier live job-detail failures have no established cause. An earlier SQL deadlock log
has no request correlation and cannot be attributed to this button. The diagnostic
correction records the failed request before the UI's stale-response guard, so a
future live validation can distinguish a coherence refusal from a database
or timeout failure. Successful job detail and pagination are now observed on the
deployed dashboard; this does not attribute or erase its earlier failed requests.
The diagnostic addition passed canonical closeout, independent exact-candidate
review, normal incremental deployment and post-release validation. Forced
termination, reconciliation, replay and stopped-pool repair
remain outside this dashboard change. A recurrence is failed validation, not
authority for recovery retries.

Historical jobs, uncertain outcomes and source-deletion attention remain unresolved.
The final all-sources sample retained 342 blocked files, 126 failed files and 90
historical pipeline jobs. All 217 pre-deployment uncertain GPU task identities
were unchanged in the post-release comparison. Two outcomes had become uncertain
before this deployment; their native cleanup and cause remain unproved. No recovery
was attempted. Native IIS crash prevention and full frozen semantic acceptance
remain separate Release 1 work. Parallel source-deletion work was excluded from
this candidate and keeps its own migration and production gates.
No record was reset, replayed, deleted or marked successful to tidy counts. Any
separate recovery still needs exact eligible manifests, compatible bindings, known
prior outcomes, confirmed native cleanup and idempotency. Source content, canonical
embeddings, checkpoints, the model store and recovery evidence are preserved.
Manuals were not changed.
