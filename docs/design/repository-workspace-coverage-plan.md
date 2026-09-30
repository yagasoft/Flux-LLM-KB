# Automatic repository code and documentation implementation plan

Date: 30 September 2026. Status: dispatch correction `3b92f17b` deployed; the single repository source is enabled and has discovered 1,032 current files. Complete live publication/brief/freshness acceptance remains pending.

**Goal:** configure one repository source once, automatically maintain Git-tracked code/docs coverage, preserve searchable source text and C# facts, and remove the aggregate application request cap without replacing it with an arbitrary limit.

**Specification:** [Automatic repository code and documentation coverage](repository-workspace-coverage.md).

## Handoff and boundaries

- Reuse `C:\Users\os008\.codex\worktrees\workspace-brief-coverage\LLM KB`, branch `codex/workspace-brief-coverage-plan`. Inspect status before editing. The eventual live source is the single main repository root `E:\LLM KB`.
- This revision replaces the fixed-file-list approach. No 512-entry limit, no replacement aggregate byte/count ceiling, no partitioned roots, no frozen include manifest and no manual follow-up whenever a file is added.
- Keep one implementation owner. Independent review covers consequential Git reconciliation, C# concurrency and ingress safety, plus the required production gate.
- Preserve SQL Server/Full-Text/USearch, preview/commit/idempotency, scope containment, source lifecycle fences and content disclosure checks. Keep unrelated file-size, syntax/parser, response and semantic field protections.
- Existing filesystem roots retain their behaviour. New source text requires explicit Git-tracked mode and opt-in; unknown modes fail closed. Do not retrofit deferred roots or create worktree/main aliases.
- Use verified offline artifacts only in `J:\Models`; ordinary tests use synthetic/disposable data without production access or model acquisition.
- No AGENTS changes are needed. Index existing dashboard documentation without editing/regenerating its manual assets. OCR, Outlook desktop and wider lifecycle acceptance remain deferred; affected lifecycle regressions stay in scope.
- Use `scripts/dev/complete-feature.ps1` for authorised feature closeout and retain the worktree until required closeout/live checks pass. Do not use the full-installation `-GoLive` path.
- The user approved implementation after the requested model-switch pause. A new production release still needs concrete evidence, explicit authority and independent operational review; approval to implement does not start live registration or deployment.

## 1. Create one Git-tracked source and accept larger configuration requests

Deliver consistent native/UI creation with no individual file list and no aggregate application request cap. Use focused failing behaviour tests before changing the contract.

| Area | Files to inspect/change |
| --- | --- |
| Discovery mode/configuration | `src/FluxKnowledge.Domain/Sources/SourceRootConfiguration.cs`; source create/draft contracts; `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceRootConfigurationEntity.cs` |
| Shared creation and persistence | `src/FluxKnowledge.Application/Sources/SourceRootService.cs`; `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceRootStore.cs`, `SourceRootControlAuditEvidence.cs` |
| Native admission/commit | `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlNativeCorpusActionStore.cs`, `SqlNativeOperationStore.cs` |
| Request reading/mapping | `src/FluxKnowledge.Application/IntegrationV1/NativeV1ContractLimits.cs`, `NativeOperationContracts.cs`; `src/FluxKnowledge.Web/NativeV1/NativeV1RequestMapper.cs`; `Endpoints/NativeV1Endpoints.cs`, `Endpoints/NativeCodexHookEndpoints.cs`; `Mcp/NativeV1McpTools.cs`; `src/FluxKnowledge.Cli/Commands/NativeV1Command.cs` |
| Sources preview/save | `src/FluxKnowledge.Application/Contracts/SourceRootViewContracts.cs`; `src/FluxKnowledge.Web/Components/Pages/Sources.razor`; `Components/Sources/SourceRootProjectionReader.cs`, `SourceRootPageState.cs` |

- [x] Define a validated discovery-mode contract: existing filesystem mode maps to stored zero; recognised Git-tracked mode maps to its explicit stored value. Propagate it through create, restore, preview, watcher/store projections and scan execution. Unknown values must never fall back to filesystem enumeration.
- [x] Add native `discoveryMode`, `indexSourceText`, optional `includePatterns` and `excludePatterns`. Default omitted mode/opt-in to existing behaviour. Git mode with source text needs no includes; optional patterns only narrow eligible tracked files.
- [x] Keep `root_update` name-only. File additions/removals are discovered automatically rather than through policy edits. Bind source-text opt-in to existing root marker fields without changing accepted-content classification or the extraction MIME capability.
- [x] Validate canonical repository/control-directory identity under the service account. Admission and commit must build and persist the same configuration/evidence. Confirmations bind the root identity and ongoing policy, not a snapshot of tracked filenames. Revalidate identity at commit without requiring a new confirmation for ordinary Git membership changes.
- [x] Add the corresponding Sources mode/opt-in controls with preview invalidation on policy changes. Explain automatic tracked-file discovery and mandatory exclusions in plain wording; do not ask users to enumerate files.
- [x] Remove `MaximumRequestBytes` and equivalent aggregate checks from REST, mapper, CLI and dependent hook readers. Do not retain the same fixed ceiling under another name or replace it with a rule count. Preserve independent operation/domain validation and response bounds.
- [x] Introduce/reuse asynchronous request ingress with incremental validation, cancellation and resource-aware admission/backpressure. Avoid repeated full JSON/string/UTF-8 copies; control any necessary materialisation. Audit canonicalisation/fingerprint code and preserve canonical binding semantics for existing clients.
- [x] Inspect MCP HTTP/SDK parsing before mapper invocation and apply the ingress/resource protection where allocation occurs. CLI must consume stdin incrementally. Keep actual requests intact through typed mapping, confirmation and persistence.
- [x] Validate the complete command before any durable mutation. Reject duplicate properties, trailing/late invalid input and semantic violations. Cancellation/resource pressure must leave no partial source/configuration/intent commit; retries retain normal idempotency semantics.
- [x] Submit larger Sources configuration over the shared HTTP configuration path instead of one large Blazor circuit message. Keep notifications/confirmation tokens small; do not raise a hub constant as a substitute API cap.
- [x] Keep previews/responses/logging compact: counts, summaries and fingerprints rather than echoed arrays. Existing operation target metadata remains one root identity/version for `root_create`; prove its separate bounded schema does not reject valid larger configuration.

Extend existing native mapper/endpoint/MCP/CLI, source projection and source persistence tests. Add a valid configuration above 32 KiB with more than 512 optional rules and verify exact preview/commit/save/readback plus replay. Those fixture sizes demonstrate removal of the old restrictions; they are not production maxima. Test UTF-8 multibyte/escaping, missing Content-Length/chunked input, slow/cancelled streams, resource-pressure refusal, malformed final tokens and payload modification after preview. Preserve unrelated query, response and hook behaviour.

## 2. Discover locally and publish automatically through the full pipeline

Deliver the first disposable end-to-end slice: one configured Git source with searchable code/docs, C# facts and automatic add/edit/rename/delete behaviour. No bulk live ingestion precedes this result.

| Area | Files to inspect/change |
| --- | --- |
| Git inventory and filesystem integration | A focused `GitTrackedSourceDiscovery` adapter under `src/FluxKnowledge.Integrations/Files`; `LocalSourceEnumerator.cs`; a small application port/receipt contract for authoritative inventory |
| Eligibility/classification | `src/FluxKnowledge.Application/Sources/SourceClassifier.cs`; shared repository/source-text policy beside existing source contracts |
| Scan/reconciliation authority | `src/FluxKnowledge.Application/Sources/SourceScanWorker.cs`, `SourceReconciliationService.cs`; `src/FluxKnowledge.Application/Ports/ISourceScanner.cs`, `ISourceScanStore.cs`; `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceScanStore.cs` |
| Watch hints | `src/FluxKnowledge.Integrations/Files/LocalSourceRootWatchHostedService.cs`; `src/FluxKnowledge.Application/Sources/SourceWatchCoordinator.cs`; relevant watch store readers |
| Text route and replay | `src/FluxKnowledge.Application/Sources/RetainedTextActivityPlanner.cs`; `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedTextRegistrationStore.cs` |
| C# coexistence | `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedProcessorBranchStore.cs`; preserve `RetainedCsharpCodeProcessor.cs` syntax-only processing and limits |

- [x] Implement local NUL-delimited Git index enumeration with a trusted executable and explicit arguments. Disable executable Git helpers, especially configured fsmonitor; sanitise inherited Git path/config/object overrides. No shell, pager, prompt, hooks, filters, network, checkout, submodule recursion or model acquisition.
- [x] Validate the admitted worktree and Git metadata relationship. For an ownership mismatch, allow only exact process-local repository trust after physical identity validation, or fail actionably. Never write global/persistent trust or set `safe.directory=*`.
- [x] Read tracked regular stage-zero paths; use stable current working-tree snapshots for content. Reject invalid/traversal/rooted/unrepresentable paths and ambiguous case collisions. Handle linked worktree metadata without content traversal outside the admitted root. Report symlinks/gitlinks; treat conflicted index output as incomplete rather than indexing merge stages.
- [x] Apply mandatory private/build/model/Git-metadata exclusions before file reads, hashes or retention, including force-added files. Optional filters only narrow. Discover all supported tracked code/docs paths without manual include lists or a count threshold.
- [x] Add only the specified source-text formats: C#, PowerShell/scripts/modules, Python, JavaScript, SQL, Razor, CSS, project/props/solution XML. Retain ordinary documentation formats and existing strict UTF-8, binary/control-byte, file and parser guards.
- [x] Stream/validate the complete inventory and successful process exit. Capture repository/index generation, configuration revision and scan claim authority in a receipt, using protected temporary staging if necessary. Keep staged process output out of public Git and remove it on cancellation/failure.
- [x] Pass authoritative scan/generation evidence into missing-path reconciliation. Validate root/configuration/claim freshness transactionally and fence expired or superseded scans. Before reconciliation, detect inventory-generation changes; mark incomplete, suppress nothing and enqueue a fresh scan. Do not assume an empty error list proves authority.
- [x] Renew active Git scan leases with current root/configuration/owner/generation checks, cancel on renewal loss and reject expired completion. Prove progress past the original lease and pause/resume recovery without widening enumeration.
- [x] Treat confirmed missing tracked files and untracked former paths as removal candidates; distinguish access failures. Preserve last good visibility on incomplete scans. A stable successful empty inventory must reconcile prior content correctly.
- [x] Reuse worktree watchers and add relevant validated Git index/HEAD/ref hints, including linked worktree control paths. Keep periodic scans authoritative for missed events/overflow and changes in tracking status. Do not build a separate scheduling service.
- [x] Define the versioned intentional code-text descriptor/fingerprint and schedule it alongside the existing C# branch. Ordinary docs retain their existing route. Retain normalised-text citations and original artifact provenance.
- [x] At registration/recovery/replay, verify exact route identity, Git/source-text policy, revision eligibility evidence, root/revision/suppression state and checksums. Do not invoke external Git from within SQL transactions or accept a client-supplied path list as authority.
- [x] Narrow C# promotion's text-conflict/supersession guard only for that validated intentional route. Preserve lock ordering, idempotency and all unrelated conflict safeguards.
- [x] Run a disposable repository through native creation, discovery, durable retention/dispatch, workers and published corpus/code interfaces. Include C#, PowerShell, Razor, project XML and Markdown plus excluded sentinels. Require cited code bodies, C# facts, documentation passages and no excluded retention.
- [x] Modify that same repository through tracked addition, unstaged edit, tracked rename, working-tree deletion and `git rm --cached`. Observe automatic convergence without altering root configuration. Prove new references/facts and stale old evidence using watchers, then repeat the key transition with watcher hints suppressed and periodic reconciliation alone.

The first two batches must produce this executable result. If absent, report the exact blocking boundary and make the smallest safe correction before adding live rollout work. Do not postpone executable capability for exhaustive fixture conversion or ranking tuning.

## 3. Prove discovery, concurrency, larger inputs and compatibility

Deliver meaningful focused integration evidence and independent review of the complete change. Reuse existing guarded SQL/test-host composition; skipped SQL tests do not satisfy the gate.

| Invariant | Tests and evidence |
| --- | --- |
| Unlimited-by-count discovery | More than 512 eligible files in one disposable repository, nested/new directories, no include list, all outcomes accounted for; exercise additional sizes to avoid an implicit replacement threshold |
| Exclusion precedence | Untracked private file plus tracked private/build/model sentinels are never retained; mandatory exclusions override optional includes; binary/symlink/gitlink inputs remain refused |
| Safe Git process | Configured executable-helper sentinel never runs; inherited `GIT_DIR`/index/worktree overrides cannot select another repository; ownership errors and malformed output have no filesystem fallback or network effect |
| Automatic lifecycle | Added-to-index file, tracked edit without staging, rename, delete, untrack-with-local-file-left, sparse absent path and stable empty repository; watcher and periodic-only convergence |
| Complete scan fencing | Git unavailable/nonzero/partial output, permission denial, cancellation and index changes suppress nothing; an older scan finishing after a newer generation cannot remove newer results |
| C# coexistence | Text-first, code-first, simultaneous promotion and replay create one text record plus the intended branch; unrecognised descriptors retain existing conflict safeguards |
| Source lifecycle | Pause/delete, eligibility loss, suppression and stale worker completion cannot republish old text or facts; restart/recovery remains idempotent |
| Disclosure and provenance | Synthetic secret/chunk-boundary refusals through search/read and C# query; correct retained hashes, canonical offsets and source/revision binding |
| Larger request integrity | Valid >32 KiB configuration with >512 rules succeeds intact through REST, CLI, MCP and shared UI HTTP admission; malformed suffix, cancellation, pressure and changed confirmation payload make no partial mutation |
| Resource/transport behaviour | Known/unknown content length, multibyte/escaped JSON, slow streams and competing requests show controlled memory and responsive cancellation; transport rejection is distinguished from API semantic failure |
| Mode and downgrade | Unknown modes fail closed at all boundaries; old binaries cannot start with an active Git root unless verified safe; quiesce/pause and claim-fencing recovery is exercised disposably |

Use/extend `LocalSourceFileTests`, `SourceClassifierTests`, `SourceScanWorkerTests`, `SourceReconciliationIntegrationTests`, `SourceRootWatchStoreIntegrationTests`, `RetainedTextPipelineIntegrationTests`, `RetainedCsharpCodeReplayIntegrationTests`, `RetainedCsharpCodeLifecycleCorrectionIntegrationTests`, `ScopedCorpusRetrievalTests` and existing native/UI suites. Add focused Git-discovery and larger-request classes where clearer. Keep one meaningful test per distinct failure invariant rather than duplicating implementation details.

Record the remaining transport restrictions instead of replacing the removed product cap:

- Live read-only inspection found IIS in-process hosting with effective `maxAllowedContentLength=30000000`. Recheck it in release preflight; no production change is authorised during planning.
- Inspect actual IIS/Kestrel request-body settings for each host. A transport can reject input before Flux can return its normal envelope; preserve useful status/diagnostics without logging payloads.
- Blazor's documented default 32 KB incoming message boundary is separate. Verify larger source configuration uses HTTP and does not hit this circuit boundary.
- Record MCP SDK/client/proxy restrictions observed in actual end-to-end transport tests; mapper-only unit success is insufficient. Unknown client ceilings must remain labelled unverified.
- Use streamed stdin for CLI payloads. Preserve JSON depth, legitimate per-field/domain constraints, response bounds and the existing bounded SQL operation-target metadata, which does not contain the root configuration body.

Do not invent new aggregate byte, pattern-length or rule/file-count constants. Capacity tests should measure allocations and cancellation using existing runtime tooling; admission uses actual resource availability and configured host operating headroom. If a real external transport setting prevents an agreed valid workload, identify that exact setting and prepare its narrow operational change; never respond by splitting the repository or requiring file lists.

Obtain independent review of the meaningful automatic-discovery milestone and the full final diff; one review may cover both when evidence is complete. Blocking findings must identify a failed invariant or credible scenario. Recheck only changed/affected evidence after remediation.

## 4. Prepare verified code, documentation and the release packet

- [x] Update only affected parts of `docs/architecture.md`, `docs/integrations.md`, `docs/file-type-coverage.md` and `docs/setup.md`: single repository creation, tracked working-tree semantics, automatic membership, exclusions, code text versus C# facts, larger request handling and remaining transport constraints.
- [x] Create `docs/operations/2026-09-30-repository-workspace-coverage-acceptance.md` with actual local verification and live checks explicitly pending. No fabricated delivery claims or private raw traces.
- [x] Update the affected roadmap progress/remaining-work entry narrowly, distinguishing local capability from pending deployment/indexing/acceptance. Do not mark delivery complete before live outcomes.
- [x] Run focused checks after coherent changes, then Release warning-as-error build, combined affected SQL/retrieval/interface suites and the relevant guarded Sources browser checks. Follow `docs/setup.md` and `scripts/dev/test-browser.ps1`; report required skips and unverified gates.
- [x] Validate links/whitespace and run `pwsh -NoProfile -File tests/native/repository-contract.ps1 -SourceRoot .`. Introduce no warnings. Confirm no unrelated AGENTS, manual, model or installed-plugin changes.
- [x] Initial feature integration completed through `scripts/dev/complete-feature.ps1` with `-KeepWorktree` and without `-GoLive`: all 21 steps passed, including both full suites and squash merge/push. Supporting corrective code follows the same required closeout sequence.
- [x] The integrated `5926a24c` release received incremental `-PlanOnly`, candidate/payload checks, live/ready/index baseline, model-store inspection and independent operational review. No migration or index rebuild was used.
- [x] Make recovery concrete: if older code ignores the mode, quiesce the new Git source and fence pending claims before starting old workers, and keep it paused/disabled until compatible code returns. Application rollback must never turn it into a broad filesystem source. Exercise the sequence disposably before approval.
- [x] The user approved deployment and validation; incremental Apply deployed `5926a24c`, installed hash matched the reviewed candidate, and health/readiness/index probes passed. Preserve recovery. Any corrective release requires its own reviewed final diff, verified payload and applicable production authority.

## 5. Register once, observe automatic coverage and complete acceptance

Deliver live repository search and code facts with a single durable source configuration. No temporary docs/code subroots or manual configuration manifests.

- [x] Final integrated main and existing rendered canonical root paths were inspected; neither existing validation source overlaps `E:\LLM KB`. No existing source was converted.
- [x] Verify Git executable/service-account admission, root/control metadata identity, mandatory exclusions, permissions, verified local models and live health. The resumed scan completed without enumeration errors and corrective release readiness is healthy. The scan-generated path/hash inventory is an audit receipt, not an input the user must maintain.
- [x] Twelve document-grounded expected-answer questions and representative code-body/symbol queries were frozen from current files before live retrieval.
- [x] One `E:\LLM KB` root was previewed and committed with `discoveryMode="git-tracked"`, source text enabled, no patterns/file list, no links, 900-second cadence and unchanged 16 MiB file protection. Confirmation/idempotency receipts were retained. Its first complete scan discovered 1,028 files. After corrective release `597bf0be`, native resume queued a complete 1,030-file scan, including the two newly tracked tests without a policy edit. The source was subsequently paused to contain the reference-validation failure; resume this same root after its verified correction.
- [ ] Observe discovery, retained revisions, text publication and C# branches. Establish a bounded observation window from actual queue/progress and report meaningful stage/count changes. A scan summary alone is not publication proof.
- [ ] Account for every eligible/discovered outcome, distinguishing excluded/untracked, unsupported/absent, retained, text-published, structured, withheld and failed. Enumerate all inventory pages. Do not treat lack of search hits as exclusion proof or withheld content as searchable coverage.
- [ ] Verify cited implementation bodies across C#, scripts, Razor/CSS, SQL and project/build files present in the repository, plus document passages and C# symbols/references. Check MCP, CLI and REST source/revision bindings.
- [x] Verify main workspace containment, nested scopes, sibling prefixes and the separate task worktree. Obtain branch IDs through verified root inventory. Do not silently substitute main for an unregistered worktree.
- [ ] Run three genuine brief requests, including one explicit skill invocation, each retaining three searches/eight reads. Review every factual claim against readback and the expected-answer sheet. Distinguish proposals/tests/historical acceptance from delivered capability or current live state.
- [ ] Prove live freshness with genuine tracked documentation changes, such as the verified later-deployment addendum to `docs/operations/2026-09-30-native-workspace-brief-acceptance.md`. Read the private supporting receipt before writing facts, preserve historical context and retain an old reference for comparison.
- [ ] Update/create the truthful final acceptance record and integrate it through required feature closeout. If a document is newly tracked after source creation, its automatic discovery must require no root edit; already tracked edits must replace evidence. Observe normal watcher/reconciliation processing, allowing one cadence plus five minutes before diagnosing a stalled single-file refresh. Documentation changes need no application redeployment.
- [ ] Require newly discovered/changed documentation to be searchable with correct current provenance; replaced evidence must return `evidence-stale`. Live add/edit evidence complements disposable rename/delete/untrack and race tests. Do not manufacture permanent code changes or delete real project files solely for acceptance.
- [ ] Record measured single-root configuration, discovery/update results, counts, retrieval/brief quality, transport checks, healthy final state and any withheld/unsupported content. Update only the affected roadmap progress/remaining-work entry using observed results, then complete final documentation checks/integration.
- [ ] Preserve worktree/recovery until all in-scope closeout and live checks succeed. Cleanup follows current authority and never triggers another deployment. Report actual commit/release, admitted/searchable/structured/withheld/failed outcomes and usable scoped queries.

## Stop and recovery rules

Live C# processing of `SqlRetainedTextRegistrationStore.cs` produced reference displays exceeding the existing 4,096-unit bound; SQL correctly rejected the non-canonical completion, but the unhandled exception stopped the host. Enforce that existing protection before the processor returns success, use its normal blocked outcome and preserve SQL validation. Verify exact-bound acceptance, over-bound refusal and durable activation continuing to another file. Re-observe publication after correction before inferring another queue defect; do not suppress host exceptions or raise parser limits.

Release `8a8f578f` passed full closeout and independent operational review, deployed with all 272 payload files verified, and resumed the same source. C# now accounts for 880 completed branches and one normal `csharp-code-signature-limit` outcome, without another host stop. The new operational document was discovered without a policy change. Publication reached 33 records, then delayed behind GPU-owned deliveries: dispatch selection claimed a GPU-queued/processing parent's message, the authoritative worker claim refused it, and the pump deferred the message and ended that iteration. Narrow selection to exactly bound, currently worker-claimable jobs before choosing the next delivery; retain root/rebuild/hold fences, authoritative claim checks and the existing race fallback. Verify GPU ownership remains untouched, later publication proceeds, expired worker recovery and concurrent claims, then apply the reviewed incremental correction within the approved delivery scope. No replay, lease clearing or schema change is needed.

The initial live run reached retained/pipeline work but failed acceptance: the C# readiness probe rejected the existing migration's exact scripted `CREATE`/CRLF blocked-diagnostic guard, while ordinary occupied GPU capacity raised an admission exception. The source was paused through native controls. Independently reviewed narrow corrections retain exact-definition checking and return Busy only for a valid, uniquely owned reservation; their full closeout integrated `59c41158` without deployment. Further preflight identified a cleaned, settled embedding attempt whose parent remained GPU-processing after pause. The additional correction preserves paused continuation and recovers only the latest exact attempt under existing cleanup, capacity, source, epoch and checkpoint fences; historical replay cannot disturb newer work. Disposable reproductions and regression checks precede the combined corrective review, integration/release and resume. No production SQL alteration, missing-slot repair, rebuild or model acquisition is needed for these corrections.

Unexpected private inclusion, cross-scope publication, false removal by a failed/stale scan, lifecycle violation, uncontrolled input allocation or persistent service regression stops the affected approach. Pause only the new repository source through supported controls, retain evidence and make the smallest safe correction. Pausing does not erase retention or revoke prior disclosure. Any necessary production deletion/rollback still requires its concrete review and authority; no dead-letter processing, cache purge, lease clearing or broad index reset.

A required concurrency/disclosure/transport check that cannot run remains unverified. Do not label the outcome complete or deploy around a blocked gate. Routine implementation decisions within the agreed contracts need no new design ceremony.

The requested model-switch pause ended with the user's implementation approval. Complete local verification, review and authorised Git integration; prepare the concrete incremental release before requesting any outstanding production authority. Do not claim live repository coverage until registration, publication and retrieval acceptance have been observed.
