# Automatic repository code and documentation coverage

Date: 30 September 2026. Status: release `3b92f17b` is deployed; the single repository source is paused after a readiness regression. A narrow retained-checkpoint validation correction is undergoing verification; complete live acceptance remains pending.

## Outcome and scope

Configure `E:\LLM KB` once as one repository source. Flux discovers eligible Git-tracked code and documentation locally, indexes their current working-tree content and keeps coverage current through the existing watchers and periodic rescans. Additions, edits, renames, deletions and changes in tracking status require no individual file-list maintenance or root repartitioning.

Preserve searchable implementation text, the additional code formats, C# symbols/references and cited documentation retrieval from the previous proposal. Remove the native API's hard-coded aggregate 32 KiB request limit. Introduce no replacement fixed byte ceiling or file/rule-count cap. File-size, parser, disclosure, query and other unrelated semantic protections remain in force.

The first observable result is one disposable Git repository: source creation -> local tracked-file discovery -> retained revisions -> text publication plus C# facts -> scoped search/read and symbol queries -> automatic refresh after Git/worktree changes. A completed scan alone does not prove searchable coverage.

## Evidence and decisions

Baseline inspection used main revision `30a986a8a24083003950b87c47e938e109f78ab1`. The findings below describe that pre-implementation baseline. Workspace KB retrieval returned `scope-unavailable`; current files supplied the design evidence.

- `SourceClassifier` and `SourceScanWorker` do not yet provide corpus text for all the requested code formats. C# currently follows the structured route exclusively.
- `SqlRetainedProcessorBranchStore.PromoteRetainedCsharpAsync` conflicts with or supersedes text work on the same revision. The intentional new text route needs a narrowly validated exemption.
- Existing SQL structures can hold a retained-text pipeline record and a C# branch for the same revision. No separate code-copy directory or parser framework is needed.
- `LocalSourceRootWatchHostedService` sends created/changed/deleted/renamed/overflow hints to the existing coordinator. Periodic reconciliation remains authoritative. Extend this path to recognise Git metadata changes; do not introduce a second scheduler.
- `SourceScanWorker` currently suppresses unseen revisions when enumeration evidence is empty. `SuppressUnseenAsync` receives a root ID and observed revisions, without a Git inventory generation or scan authority. Git discovery must strengthen that reconciliation boundary.
- `SourceRootConfigurationEntity.CrawlMode` exists, with inspected writers setting zero; it is not currently propagated as a validated discovery mode. Reuse it for the new mode only with explicit restore/execution validation and downgrade controls.
- The aggregate 32 KiB guard appears in `NativeV1ContractLimits`, REST body reading, MCP request mapping, CLI input and hook body reading. Removing only the REST check would leave the restriction elsewhere.

One Git-aware source is preferable to hand-maintained exact lists or partitions. Git supplies membership locally; optional patterns only narrow it. Repository size is therefore independent of the creation request size. The initial scan produces an observed inventory receipt, not an operator-maintained configuration manifest.

## Configure once

Extend native `root_create` and Sources creation with `discoveryMode="git-tracked"` and `indexSourceText=true`. Keep existing filesystem mode as the default for callers that omit the new option. Add optional `includePatterns` and `excludePatterns` to native creation to match existing source controls; repository setup needs no include list. Do not add the previously proposed 512-file, rule-count or 512-character rule caps.

Persist a typed mode through the application/domain contracts, existing `CrawlMode` field, source store, restore paths, preview and watch/scan readers. Define zero as the current filesystem behaviour and a recognised value for Git-tracked discovery. Unknown values fail closed everywhere; they never become filesystem scans. Existing roots retain their current behaviour. Source-text opt-in in this increment requires Git-tracked mode; do not create an unrestricted code-text filesystem path.

Keep `text/x-source-code` as an internal explicit root-policy marker alongside `text/plain`, not a replacement extraction MIME type. Accepted retained input remains `AcceptedUtf8Text`, and the UTF-8 capability remains `text/plain`. Empty classifications do not imply code-text opt-in.

Admission verifies the canonical worktree root, its physical identity and associated Git control directories using the service identity. Record the discovery mode and admitted repository identity with existing configuration/audit evidence. A directory inside a larger repository must not silently expand to its parent; configure the worktree root explicitly. Linked worktrees may have Git metadata outside their content root: validate that metadata relationship, read it only as control information and never index its contents.

The repository form displays the effective mode, mandatory exclusions and optional narrowing-rule counts after successful admission; failures report safe refusal reasons. Discovery counts belong to subsequent scans. Preview authorises ongoing tracked-file discovery, not a frozen list. Bind confirmation to repository identity and configuration, not to the incidental set of files present during preview. A newly tracked file after preview is covered by the chosen policy; a different repository/path or policy cannot reuse that confirmation. Persist and audit the same canonical policy admission reviewed.

The normal live configuration is one root at `E:\LLM KB`, recursive, Git-tracked, source text enabled, no individual include list, no followed links, the existing 16 MiB text/file bound and normal 900-second reconciliation. Do not create subroots to work around request limits. Retain name-only `root_update`; automatic file membership does not require a new policy-editing endpoint.

## Local Git discovery and eligibility

Use a trusted installed Git executable and explicit argument-list invocation without a shell. Obtain NUL-delimited stage information equivalent to `git ls-files --cached --stage -z --full-name`. Git documents that the index supplies cached membership and `-z` avoids pathname quoting ambiguity. Read metadata locally; never fetch, checkout, restore, run filters, recurse into submodules or acquire model artifacts. [Git ls-files documentation](https://git-scm.com/docs/git-ls-files).

Disable executable helpers, especially `core.fsmonitor`, and sanitise inherited Git directory/worktree/index/object/config overrides. Use no pager, no prompts and read-only operations with cancellation and controlled stdout/stderr consumption. Do not run repository hooks, credential helpers or external commands supplied by Git configuration. Ownership checks remain in force: an exact process-local trust entry is permissible only for the explicitly admitted canonical repository after physical/control-directory validation; never set wildcard trust or persist global Git configuration. If that trust cannot be established, fail admission with an actionable reason.

Eligible membership is the current Git index's tracked regular files, with current working-tree bytes as content:

- A new file becomes eligible when added to Git's index; a commit is not required. Untracked files remain excluded even if their extension is supported.
- Edits to a tracked file refresh without requiring another `git add`. Publication provenance refers to the retained working-tree bytes and hash, not necessarily the committed blob.
- A rename converges as removal of the old path and addition of the new tracked path, preserving source/revision safeguards. Unstaged new destinations remain excluded until tracked.
- A confirmed working-tree deletion suppresses previous visibility even if the index still contains the path. `git rm --cached` also removes eligibility while the local file remains.
- Sparse-checkout paths absent locally are not fetched or materialised; report them as absent and reconcile previous visibility. Skip-worktree flags must not create an untracked-file fallback.
- Symlink entries, gitlinks/submodules, reparse paths and nested external content are not followed. Report unsupported modes. An unmerged/conflicted index is not an authoritative successful scan; preserve the last good state and resume normal reconciliation when it is resolved.

Apply mandatory exclusions before content reads/hashing/retention, even when a file was force-added to Git. Exclude private working areas/exports, Git metadata, build/publish output, dependency caches and real-model stores/staging/payloads. Use the repository's actual directory conventions, including `.agents`, `private`, `exports`, `artifacts`, `bin`, `obj`, `node_modules` and `__pycache__` where applicable. Binary model extensions and paths are excluded; public acquisition specifications and small synthetic fixtures remain subject to normal code/docs rules. Optional include/exclude patterns may only narrow eligibility. A tracked flag is not a privacy classification.

Include application source, tests, scripts, probes, project/build configuration and maintained docs anywhere inside the admitted repository when their formats qualify. Include checked-in migration designers/model snapshots as source. Existing dashboard-manual text may be indexed without editing or regenerating it. Planning documents are proposals, never evidence of completed delivery. Report unsupported formats separately rather than claiming all tracked files were indexed.

Support source text for `.cs`, `.ps1`, `.psm1`, `.py`, `.js`, `.sql`, `.razor`, `.css`, `.csproj`, `.props` and `.slnx`, alongside existing ordinary text formats such as Markdown, JSON and YAML. Keep complete strict UTF-8, binary-signature/control-byte checks, the 16 MiB text limit and C#'s separate 4 MiB/syntax-complexity limits. Do not execute or compile indexed inputs. Original bytes/hash remain provenance; corpus offsets refer to the existing newline/Unicode-normalised text.

## Watchers, complete scans and removals

Reuse the existing watch coordinator, debounce, durable scan claims and periodic rescan cadence. Worktree events and relevant index/HEAD/ref changes trigger a fresh membership scan. For linked worktrees, watch their validated Git control locations as hints, excluding locks/spools created by Flux itself. Watch loss/overflow or unsupported metadata notification must recover through periodic rescans without operator file-list changes.

Stream the inventory in manageable batches, using protected temporary staging if needed for complete-output validation rather than buffering arbitrary process output in memory. A complete inventory receipt includes repository/control-directory identity, membership generation, source configuration revision and scan request/claim authority. Validate the entire Git command's exit/output before treating membership as authoritative. Do not truncate entries at a count threshold.

Recheck the generation and scan/configuration authority before destructive missing-path reconciliation. Extend the scan-store boundary to fence expired, cancelled or superseded scans; `LastEvidence.Count == 0` alone is insufficient. An older/slower scan cannot remove results from a newer generation. If Git changes during enumeration, output is malformed/partial, a process fails, or an unrelated permission/I/O error prevents complete observation, suppress nothing from that incomplete pass and schedule a fresh scan. Do not convert failure into an empty repository.

Only one Git scan owns a root at a time. Active scans renew the existing lease while retaining owner/generation and current configuration; loss or uncertain renewal cancels work. Convergence, reconciliation and completion independently validate authority. This permits healthy large/slow scans to outlive the initial lease without accepting stale workers.

Distinguish confirmed path absence from inability to read a path. Missing tracked files are legitimate removal candidates after a successful stable inventory; access denial is incomplete evidence. This distinction is required because treating every missing file as an enumeration error would prevent normal deletions from converging.

Use existing publication/root/revision fences and suppression for both corpus text and C# facts. Validate scan authority at the SQL reconciliation transaction and preserve lock order. Filesystem, Git and SQL cannot form one atomic transaction: promise convergence after a stable successful scan, expose incomplete/stale status and avoid claiming zero transient staleness during concurrent edits. Successful scans of an actually empty eligible set must safely suppress prior visibility.

Do not retain arbitrary file lists as immutable source policy. Receipts describe a completed observation and can be paged/streamed; subsequent scans derive membership again. Existing source retention/deletion semantics apply after a path loses eligibility; hiding an old publication is not immediate erasure of its retained bytes.

## Code text and C# coexistence

Retain one shared versioned source-text descriptor, proposed processor version `repository-source-text-v1`, with a deterministic fingerprint of its input/normalisation/safety contract. Supported opted-in code receives that `TextExtraction` activity; C# also receives its current structured processing activity. Ordinary documentation uses its existing route.

Registration, recovery and replay recognise the exact version/fingerprint and verify Git/source-text policy, eligible revision provenance, supported extension, root state and current suppression status. Eligibility is bound to an authoritative source observation, not a user-supplied file list or an independently invoked Git process inside a SQL transaction. Preserve retained checksum/length, input fingerprint and revision checks. A marker, `.cs` suffix or accepted classification alone cannot bypass the intentional-route requirement.

Exempt only this validated intentional route from C# promotion's text-conflict/supersession guard. Keep all unrelated safeguards. Real SQL tests must prove text-first, code-first, concurrent processing and replay produce one text pipeline record and the expected C# branch, with no stale resurrection after changed content or eligibility removal.

Keep existing disclosure checks on both projections and every native surface, including synthetic-secret and chunk-boundary coverage. Some repository fixtures may be withheld; record safe reasons/counts without echoing matched content. Do not weaken checks to manufacture complete searchable coverage.

## Larger configuration requests

Remove `MaximumRequestBytes = 32 * 1024` and its aggregate request-size enforcement throughout the shared native request path, including REST, MCP mapping, CLI input and dependent hook reading. Do not rename the same guard, impose a file/rule-count substitute, enlarge it to another fixed application cap, or partition the repository. Preserve operation-specific field semantics, valid-path checks, JSON depth, query limits, response limits and parser/file protections that serve unrelated contracts.

Use asynchronous incremental reads and validation with cancellation. Avoid repeated `GetRawText`, UTF-8 copies and full-body DOM cloning. Where existing typed configuration/canonicalisation needs materialisation, retain one controlled representation, reserve resources before allocation and compute fingerprints without redundant copies. Use resource-aware admission and concurrency/backpressure based on actual available memory and process operating headroom, not an arbitrary per-request byte/count ceiling. On pressure or cancellation, stop safely with an actionable/retryable failure and no partially committed command; do not silently truncate data.

Complete syntactic and semantic validation, duplicate/trailing-input rejection, canonicalisation and confirmation binding before durable mutation. Keep large policy arrays intact through preview, commit, persistence and replay. Do not echo large payloads into responses or logs; use compact summaries, counts and fingerprints. The root-create operation has one identity/version target, so its request size need not inflate bounded operation target metadata. Preserve existing target/response protections and prove they do not accidentally impose the removed request cap.

MCP's SDK may materialise JSON before the mapper, so assess ingress/resource admission before that point too; deleting the mapper check alone is insufficient. CLI must support streaming stdin rather than requiring oversized command-line arguments. Large Sources form configuration should travel over the HTTP configuration path with the same admission service, keeping Blazor circuit messages small; do not replace the removed API cap with a larger hub-message constant.

### Remaining transport restrictions

| Layer | Observed or documented restriction and treatment |
| --- | --- |
| Live IIS | Read-only inspection found in-process hosting and effective `requestFiltering.requestLimits.maxAllowedContentLength=30000000`. Requests above that are rejected before application handling. This is an existing deployment setting, not a new API or file-count limit; record/recheck it and change only for a demonstrated requirement with deployment authority. |
| ASP.NET hosting | Kestrel/IIS request-body settings can impose independent limits. The documented Kestrel default is 30,000,000 bytes, but the inspected deployment is in-process IIS; do not assume both are active. Inspect effective settings in each tested host. |
| Blazor/SignalR | The framework's default incoming hub-message limit is 32 KB. The current app registers interactive server components without a visible override. Use streamed HTTP for larger source configuration; do not claim the API change also changes this framework limit. |
| MCP/client/proxy | HTTP hosting plus SDK/client/proxy buffering or message constraints may precede Flux mapping. Identify actual configured/version-specific restrictions in end-to-end checks; client maximums are not yet verified. Report them explicitly rather than silently splitting roots. |
| CLI and platform | Use stdin/streamed HTTP to avoid shell argument-length limits. Runtime memory, cancellation, timeouts and storage constraints still exist; removing a product cap is not a claim of infinite capacity. |

Microsoft documents the hosting/body-size distinction and the SignalR message boundary. These are transport observations, not proposed replacement constants. [ASP.NET request handling](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0), [Blazor SignalR guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/signalr?view=aspnetcore-10.0).

## Acceptance and verification

| Requirement | Required evidence |
| --- | --- |
| Configure once | One Git-tracked source indexes eligible code/docs with no include list; later tracked additions are picked up without configuration or confirmation changes |
| Automatic updates | Add, edit, tracked rename, working-tree deletion and untracking converge through watcher hints and separately through periodic rescan with hints lost |
| Complete scan authority | Git failure, invalid output, permission errors, cancellation, index mutation and expired/older scan completion cannot cause false removals; a stable empty repository does remove prior visibility |
| Exclusions | Both untracked private sentinels and tracked excluded build/private/model sentinels are absent from retained inventory; links/submodules never broaden scope |
| Safe Git invocation | Repository-configured helper sentinel is never executed; inherited path overrides, ownership mismatch and malformed filenames fail safely without fallback or network/model acquisition |
| Larger requests and discovery | Valid source configurations exceeding 32 KiB and containing more than the previously proposed 512 rules succeed intact; one repository with more than 512 eligible files is fully discovered. These are regression fixtures, not new maxima. |
| Transport/resource safety | Known/unknown content length, escaped/multibyte JSON, slow/cancelled input, resource pressure, late invalid data and oversized transport rejection yield correct outcomes with no partial writes or duplicate roots |
| Dual projections | Text-first, code-first, concurrency/replay and lifecycle/eligibility changes preserve both intended outputs and disclosure safeguards |
| Useful retrieval | Representative code-body and documentation search/read have exact citations; C# symbols/references remain available; MCP, CLI and REST agree on source/revision binding |
| Brief and scope | Main workspace brief is grounded in document evidence; sibling/unregistered worktree scopes do not receive main content; proposals/tests are not reported as delivered/live facts |
| Freshness and status | Replacement revisions invalidate old evidence; discovery, retained, published, structured, withheld, excluded and failed counts are distinct; final health is observed |

Before brief checks, prepare 12 expected-answer questions from current documents: three each for state, decisions, issues and next actions. Run three genuine brief requests, one explicit skill invocation and two natural-language requests, each retaining the current three-search/eight-read budget. Require successful exact readback for factual claims and no unsupported claims. Do not widen to unscoped memory or start ranking work to hide evidence gaps.

Use `cwd="E:\\LLM KB"`, `scope="workspace"` for corpus queries. Verify root IDs and obtain branch IDs from `corpus.query(view="branches", root_id=...)` before code matches. Global code status is not coverage proof. Worktree-to-main aliases remain outside scope.

## Delivery, recovery and later work

The user resumed and approved implementation after the requested model switch. The disposable automatic-discovery slice is implemented; final local verification and independent review precede authorised Git integration. Production apply and live source registration remain pending their concrete operational gate. Keep one implementation owner and preserve the recorded focused evidence.

No schema change is expected from using existing mode/configuration/evidence fields, but verify that assumption. Unknown modes fail closed in new code. Older binaries ignore `CrawlMode`; therefore application rollback is unsafe unless Git roots are quiesced and disabled/paused with pending claims fenced before older workers start, or compatibility is proven. Include this concrete control in the deployment/recovery review. A paused root does not revoke earlier disclosures or erase retention.

Prepare required checks, full-diff review and authorised Git integration using `scripts/dev/complete-feature.ps1`, retaining the worktree. Prepare the incremental updater's `-PlanOnly` output, exact revision/target, current live/ready/index baseline and rollback before seeking any outstanding new-release authority. Production apply requires explicit approval and independent operational review. No full installation, migration, index rebuild or worker-concurrency change is implied.

Verify configured local artifacts against `J:\Models` before any real embedding run; keep offline enforcement and stop on a cache miss. The repository source excludes model data, and ordinary tests never acquire models. After an authorised deployment, create the single live repository source once, observe its pipeline and account for every discovered outcome. Do not create temporary docs/code subroots to stage ingestion. Use normal scheduler controls and a bounded observation window based on actual progress.

On unexpected inclusion, cross-scope publication, stale-scan suppression or persistent service regression, stop the affected approach and pause only the new repository source. Preserve receipts, diagnose and prepare any required deletion or rollback separately. Never process dead letters, clear leases to force progress or purge model caches.

Keep raw traces/inventory receipts outside public Git. Update the affected roadmap progress/remaining-work entries and developer contracts from observed results. No AGENTS edit or manual regeneration is needed. Retain deferred OCR, Outlook desktop and wider source-lifecycle acceptance work as later items; the focused lifecycle tests required by this change remain in scope. Additional language parsers and automatic submodule traversal are separate work. Automatic tracked-file membership is part of this delivery, not a deferred follow-up.

See the [implementation plan](repository-workspace-coverage-plan.md).
