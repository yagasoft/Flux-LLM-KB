# Pipeline visibility implementation plan

Date: 14 October 2026. Status: implementation, focused SQL/browser checks,
active-write availability and canonical closeout pass. Production deployment and
live validation are held at independent review; see the
[acceptance and gate record](../operations/2026-10-14-pipeline-visibility.md).

Implementation resumed from the saved scoped plan after the requested pause.

## Outcome and boundaries

The overview must answer whether anything is running, which source owns it, what
known work remains, whether it is progressing/waiting/blocked, and what needs
attention. Keep the existing Blazor shell, typography, native controls and CSS.
This is a read-only status improvement, not scheduler repair or historical cleanup.
No job reset, replay, deletion, success marking, model acquisition, source change,
schema migration or recovery action is part of this plan. Manuals stay untouched.

One implementation owner works in `codex/pipeline-visibility`, initially based on
`1e81e9bb8bc60bc1e20cbe805abafea0928faada`. Other chats own OneDrive path handling,
source-deletion recovery and Release 1 staging. Inspect their current scope before
shared-file changes, closeout and deployment; do not edit their worktrees. Serialise
integration into Main and production deployment. Reconcile shared documentation
only after the final integration base is known.

## Evidence behind the design

The reviewed overview counted every row of `Jobs` by `PublicState`, independently
of current source revisions, rebuild supersession, leases or GPU child outcomes.
Its source summary is less complete than the source page: it omits retained-branch
and logical-publication classifications. `StatusCount` renders `999+` above 999.
The overview refreshes for pipeline, index, scheduler and reconnect events, but
not source events, and exposes no successful-read time or refresh failure state.

The captured desktop page gave approximately 30 metrics equal visual weight.
At 390 px, the scheduler's actual active count and uncertain outcomes are below
many rows of pipeline and unused Outlook counters. The source page and native
`details` disclosures provide useful existing patterns. This justifies a modest
reorganisation and truthful copy; it does not justify a new design system.

Private production snapshots and screenshots are retained outside Git. They are
diagnostic samples, not test fixtures or proof of post-change behaviour.

## Read-only classification contract

Keep execution ownership and current applicability as separate dimensions.

| Dimension | Required interpretation |
| --- | --- |
| Confirmed activity | Observe current worker claims, retained-processor/scan leases, GPU mini-task/batch/dispatch ownership and relevant capacity state. Use the existing identity/generation fences. A persisted processing label alone is insufficient. |
| Current source work | Match the job's record/revision/stage/operation to the authoritative binding; resolve physical/logical source ownership, suppression, root lifecycle/configuration eligibility, publication selection and rebuild supersession. Conflicting unsuppressed file bindings remain provisional until authoritative reconciliation; neither time nor the highest revision number selects a winner. Preserve the existing active-rebuild exception for paused intake. |
| Historical applicability | A superseded, suppressed, deleted or replaced binding can establish historical applicability. Age cannot. A historically applicable job with confirmed live ownership must still appear in activity, labelled as previous-revision work. |
| Unknown ownership | Missing, conflicting, expired or unavailable evidence produces an explicit unconfirmed/needs-attention state. It must not silently become running, complete, historical or zero. Native release cannot be inferred from SQL bookkeeping alone. |
| Queued versus waiting | Separate eligible due work, future retries, paused intake, deployment/rebuild holds, missing capability and explicit source deferral/blocking. A job with a terminal or blocked reason is not a runnable queue item. |
| File completion | Count distinct physical file owners at their current revision. Require the appropriate successful publication/completion evidence. Registration, a completed extraction activity or a completed GPU task alone does not prove a searchable file. Account for logical children, metadata-only and no-extractable-text outcomes explicitly. |
| Unscoped work | Rootless direct ingress, interactive search GPU tasks and maintenance remain visible in separately named scopes. Do not attribute them to a workspace by path guessing or hide them under a source filter. |

Reference the existing predicates in `SqlJobClaimStore`,
`SqlCorpusRebuildEligibility`, `SqlGpuSchedulerStore`, `SqlCorpusProjectionReader`
and `SourceRootProjectionReader`; do not change their mutation behaviour.
Do not simply replace the overview's GPU counter with a second global counter.

Count files, pipeline jobs and GPU tasks separately. Attention counts overlap
across these units, so do not add them into a purported unique issue total.
Retain every failed job and uncertain outcome in accessible detail, including
those on historical or deleting sources. Paginate details and state the exact
total; do not cap the total at the displayed page size.

Read aggregate counts and attribution from a coherent bounded SQL projection.
Use one captured time for lease/retry comparisons, set an as-of/read window, and
avoid serializable dashboard reads, `NOLOCK`, per-row queries and loading the
whole historical graph. Reuse a consistent read mechanism supported by staging;
if cross-read consistency cannot be established, report refreshing/unconfirmed
instead of presenting contradictory values as a single authoritative snapshot.

## Dashboard design

1. **Current activity.** A short state sentence and precise supporting counts:
   running pipeline jobs, runnable queued jobs and active GPU tasks, each with its
   unit. Show source scans and retained processing when those are the active
   work. Name the source and stage for active work. Do not sum these units. Keep
   a labelled attention summary/link here so it remains reachable above source
   details on narrow screens.
2. **Source progress.** A labelled scope selector defaults to all sources with
   their names and lifecycle states visible. Per-source rows show known current
   files, successfully published files, pending/waiting files and attention.
   Provide last successful scan and last confirmed progress times. A scan in
   progress says its inventory is incomplete. Test/acceptance roots remain
   explicitly named; do not infer a test designation from an arbitrary name.
3. **Needs attention.** Keep current blocked/failed files and lifecycle failures
   visible. Offer an accessible disclosure for unresolved historical pipeline
   jobs and GPU outcomes with exact totals, reason and ownership classification.
   Link to existing source/corpus/operator details where they resolve the same
   entity. Add read-only paginated detail only where existing pages cannot expose
   the relevant records. No recovery button is added by this change.
4. **Search-index health.** Label it explicitly as the derived search index.
   Explain that a current index does not imply all source work has completed.
   Keep copyable generation/recovery information in the existing disclosure.
5. **Scheduler diagnostics.** Move detailed slots, lanes, batches and retries into
   a native `details` section. Critical uncertainty remains visible in attention.
   Inactive Outlook summaries can move below the principal source activity.

Use `Running`, `Catching up`, `Queued`, `Waiting`, `Blocked`, `Idle` and `Status
unavailable` only when the underlying evidence supports them. Distinguish no
confirmed work running from all work completed. Historical attention can coexist
with an idle current pipeline. Current blocked work cannot be summarised as all
caught up. Partial progress can coexist with current attention.

For catching up, use confirmed current cohort completions/publications while
earlier discovered revisions remain pending in the same actively processing source.
Use revision-bound successful Publish artifacts and selected publication receipts,
never `DueAtUtc` or arbitrary stage audit events as a completion timestamp.
The current application does not write `JobAttempts.CompletedAtUtc`; it cannot
support this signal. Lifetime completed-job deltas are not file progress.
Do not estimate percentages, completion time or undiscovered remaining files.

Display exact grouped numbers, with matching accessible labels. Show the scope
and last successful read time. Refresh on source events as well as existing
events/reconnect; provide Refresh and a bounded, non-overlapping 10-second
fallback refresh while mounted. Coalesce event bursts. Retain the last good
snapshot on failure, visibly stale, with unknown values on initial failure.
Use a polite live region for meaningful state changes, not every timer tick.

Use existing focus styling, semantic headings/tables and native controls.
At narrow widths use compact stacked source summaries rather than 30 metric
cards. Avoid page-wide horizontal overflow, preserve keyboard operation, and
make disclosures and links usable at 390 px and 200% zoom.

## Implementation and checks

### 1. Deliver the authoritative projection through a real isolated interface

- Extend `src/FluxKnowledge.Application/Contracts/StatusContracts.cs` additively
  with activity, per-source progress, attention and freshness data. Preserve
  existing fields/API consumers and default new state to unknown, not healthy.
- Implement the bounded projection alongside
  `src/FluxKnowledge.Web/Components/Status/SqlProjectionReader.cs`. Keep source
  owner/publication classification consistent with
  `Components/Sources/SourceRootProjectionReader.cs`; extract only the shared
  query/classification needed to prevent the observed disagreement. Coordinate
  edits to that reader with the source-deletion chat.
- Add focused SQL cases in a separate `PipelineActivitySqlTests` fixture/catalog,
  with source-reader regression cases alongside existing source projection tests,
  and pure classifier cases in a focused activity-projection test class.
- First executable acceptance: seeded isolated SQL -> actual projection ->
  rendered Blazor overview shows active current work and historical attention
  together, with exact counts and no production access.

### 2. Render and refresh the overview

- Update `Components/Pages/Overview.razor`,
  `Components/Status/OverviewProjectionState.cs`,
  `Components/Shared/StatusCount.razor` and scoped additions in
  `wwwroot/css/app.css`. Reuse existing components; no unrelated page restyle.
- Implement working source selection, attention paging/detail links, diagnostic
  disclosure and refresh. Preserve source scope across refreshes.
- Update `OverviewProjectionTests.cs` and `PipelineRecordsProjectionTests.cs`;
  the latter currently asserts the obsolete `999+` behaviour.
- Add focused browser cases using the existing isolated BrowserHost. Verify
  desktop and 390 px, keyboard/focus, source selection, disclosures, detail
  navigation, stale/error recovery and reconnect. Inspect saved screenshots.

Required regression scenarios:

- Mixed current and suppressed/superseded jobs, including a young historical
  record and an old genuinely active record; live previous-revision work stays
  visible. Include unmatched ownership and expired claims.
- GPU handoff, multiple GPU tasks per job, interactive tasks without parent jobs,
  conflicting batch/dispatch evidence and uncertain capacity. No double counting.
- Current file registration/extraction complete but publication pending; retained
  logical children, replaced selection and blocked newer work with an older
  successful publication. File counts remain distinct from jobs and tasks.
- Backlog progress without new discovery, waiting for retry/paused/hold,
  current blocked work, genuinely idle current work with historical failures,
  and incomplete scan inventory. No fabricated progress or global all-clear.
- Source deletion attention, non-root ingress, mixed workspace/test-source scope,
  source-only status events, failed refresh, reconnect and burst coalescing.
- Exact counts at 999, 1,000 and larger values; visible and accessible text agree.

Run narrow tests first, then affected Web/SQL/browser checks and a zero-new-warning
build. Reuse unchanged valid evidence. Reassess after these two implementation
batches: a runnable mixed-state overview must exist; otherwise address the single
blocking issue before adding more infrastructure.

### 3. Review, integrate, deploy and validate

- Reuse Release 1's verified separate SQL, configuration, runtime, storage and
  functional host on port 5140. Its restricted disposable fixture fails closed
  without the configured isolated `FLUXKNOWLEDGE_TEST_SQL_CONNECTION`; the full
  suite and closeout use that same explicit test target. An environment-name
  override alone cannot isolate the normal published app: `Program.cs` loads
  canonical production configuration. Use the separate functional StagingHost,
  and preserve its existing source/data/configuration. Do not duplicate provisioning.
- Obtain independent higher-model review of the complete classification/query
  change, tests and rendered states. Astra has approved isolated staging validation
  and the bounded-read correction; retain the separate final production gate.
- Update only relevant architecture/setup notes and the affected roadmap
  Progress/Remaining Work entries when capability is delivered. No progress
  increase is claimed for this planning turn.
- Use `scripts/dev/complete-feature.ps1 -FeatureWorktree <this-worktree>
  -MainRoot 'E:\LLM KB' -KeepWorktree -CommitMessage <focused-message>` with the
  isolated test target. Do not substitute manual merge/push or use `-GoLive`.
  On failure retain/report `failed_step` and `log_path`, fix in scope, then rerun.
- After other deployments are clear, prepare the canonical incremental IIS
  updater's `-PlanOnly` output for the exact verified candidate and get the
  required independent production review. The user's current request explicitly
  authorises this dashboard release's deployment/live validation; reuse that
  authority for the same action and target. Changed operational scope needs
  direction. Use only `update-native-iis-incremental.ps1 -Apply` for that approved
  candidate. Preserve the scheduled task and hidden launcher; no migration,
  recovery or clean-slate flags.
- Retain the updater's predecessor payload/configuration and rollback evidence.
  With no schema change, rollback is the canonical updater's recorded payload
  restoration. Do not invent a separate repair/restart path on failure.
- Validate the deployed identity and bounded read-only live dashboard/SQL
  agreement, named source scope, freshness, exact totals, historical detail,
  current attention and index-health distinction at desktop and narrow widths.
  Do not create a production workload to manufacture a running-state screenshot;
  use staging evidence for states absent during live validation.
- Close out only with observed test, review, closeout, deployment and validation
  results. Report historical recovery as unresolved, with original records,
  checkpoints, embeddings, model cache and native-cleanup evidence preserved.
