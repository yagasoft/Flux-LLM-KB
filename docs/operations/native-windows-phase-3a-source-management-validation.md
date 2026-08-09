# Phase 3A local source management validation

Reviewed: 2026-08-09

## Scope and result

This record covers the approved Phase 3A local-only source-management checkpoint
and its authorised disposable loopback deployment. The detailed source-root
corpus exercise remains separate from the deployment readiness check.

## 2026-08-09 operator corpus completion

The disposable Sources UI previewed a synthetic local root with one UTF-8 text
file and one signature-classified PDF: one planned text item and one deferred
item. Save-only created a held request; Save and scan released a request for a
separate synthetic root. That root completed with one indexed and one deferred
item. The local search API returned the text sentinel first with its retained
source identity and exact snippet. A controlled IIS application-pool restart
preserved the root's two revisions and two activities, and readiness returned
200. The PDF remains explicitly deferred because no matching local capability
is registered; no executor, GPU or external replay was activated.

## 2026-08-09 disposable deployment and live readiness

The required closeout workflow completed successfully after Phase 3A repairs:
the two additive migrations were applied to the local disposable catalogue,
the IIS payload was deployed to the loopback-only site, SQL validation passed,
and `/health/live`, `/health/ready`, `/api/index-health`, `/api/gpu-status`,
`/api/search?query=native%20deployment` and `/sources` each returned HTTP 200.
The live catalogue confirms both Phase 3A migration identifiers. No external,
model, GPU, process-management or legacy action was performed. A real operator
root Save/Save-and-scan/sentinel-search exercise remains the next Phase 3A
acceptance slice.

The implementation adds SQL-authoritative source-root and scan-control
contracts, retained source revisions and checksum-verified artifacts, the
in-process UTF-8 retained-text path, deferred activity planning/replay, and
Sources/Indexing plus bounded Overview diagnostics. SQL remains canonical and
USearch remains derived and rebuildable. The code checkpoint is not evidence
that the generated schema is installed or that the end-to-end loopback slice is
operational.

## Focused verification matrix

| Command | Result | Interpretation |
| --- | --- | --- |
| `dotnet build FluxKnowledge.slnx --configuration Release -warnaserror` | Passed; 0 warnings, 0 errors. | All projects compile at the Phase 3A head. |
| `dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~Sources\|FullyQualifiedName~Pipeline\|FullyQualifiedName~Indexing"` | Passed; 100 passed, 0 skipped. | Domain invariants for source policy, retained-text planning, pipeline and indexing are covered. |
| `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~Sources\|FullyQualifiedName~Indexing\|FullyQualifiedName~Persistence\|FullyQualifiedName~Workers"` | Passed where runnable; 99 passed, 109 skipped of 208. | Native SQL cases compiled and were discovered, but skipped because `FLUXKNOWLEDGE_TEST_SQL_CONNECTION` was not configured. This is not SQL execution evidence. |
| `dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~SourceRoot\|FullyQualifiedName~Overview\|FullyQualifiedName~Browser"` | Passed where runnable; 19 passed, 6 skipped of 25. | SourceRoot and Overview projections are covered; the disposable SQL/browser cases were skipped because that environment was unavailable. |

The native SQL and browser skips were intentional environment gates. This
checkpoint did not connect to a non-disposable local SQL instance.

## Whole-branch correction

The corrected branch head tightens the source activity lifecycle to the exact
linked pipeline receipt claim and fenced terminal transitions. It also corrects
immutable rename provenance and current-revision suppression, and makes the
Overview's `Recovering` diagnostic truthful. The focused matrix above includes
these corrections. They do not replace the outstanding disposable SQL/IIS/local
root proof and do not evidence a migration or live validation.

The durable source-scan lease identifier is opaque and PID-free; it is not
process-management or termination evidence.

## Migration inspection

The generated migrations were inspected in the feature diff and remain
unapplied:

| Migration | Inspected schema effect | Execution status |
| --- | --- | --- |
| `20260806120000_AddPhase3ALocalSources` | Adds source-root configurations, scan requests/jobs/outbox, revisions, artifacts, activities and capabilities; includes the retained-content hash and exact activity-key constraints, and prevents a `NativeExecutorLater` capability from being runnable. | Not applied. |
| `20260808191700_AddRetainedTextPipelineLink` | Adds nullable `PipelineRecords.SourceRevisionId`, a filtered unique index and a restrictive foreign key to `SourceRevisions`. | Not applied. |

No migration against live, development, or existing local SQL was attempted.

## Acceptance traceability

“Passed” means the stated local code/test or static-scope evidence exists.
“Not run” means the acceptance condition needs the authorised disposable
SQL/IIS/local-root checkpoint and has not been inferred from compilation or
skipped tests. No criterion failed in the focused code checkpoint.

| # | Approved requirement | Status | Evidence or gap |
| ---: | --- | --- | --- |
| 1 | Add a valid local root through Sources/Indexing. | Not run | Page/projection tests passed, but no authorised SQL-backed browser/local-root action ran. |
| 2 | Reject missing, inaccessible, non-local, unsafe or excluded-store paths. | Passed | Focused Domain source-policy tests passed; SQL/browser validation remains to be exercised. |
| 3 | Save holds one durable request/Job/outbox; Save and scan releases that same request transactionally. | Not run | Native SQL transaction proof was discovered but skipped without the disposable connection. |
| 4 | Recursive/include/exclude policy gives truthful preview and persisted policy. | Not run | Projection tests passed, but the SQL-backed scan/preview checkpoint was not run. |
| 5 | Capture permission evidence in preview, root health and audit data. | Not run | Requires an authorised local-root and SQL checkpoint. |
| 6 | Produce an immutable UTF-8 revision, searchable projection and hydrated root/path provenance. | Not run | Retained-pipeline and rebuild cases were discovered but the native SQL/search proof was skipped. |
| 7 | Keep PDF, image, media, archive, unknown and unsupported code out of text indexing. | Passed | Focused Domain source/classification and deferred-activity tests passed. |
| 8 | Retain/reopen immutable bytes, revision identity, hash and provenance. | Not run | Artifact and reader behaviour has focused test coverage, but the SQL-backed retained-store proof was skipped. |
| 9 | Make unchanged rescans idempotent. | Not run | Requires the native SQL reconciliation matrix. |
| 10 | Create a linked new revision for changed bytes without overwriting the old revision. | Not run | Requires the native SQL reconciliation matrix. |
| 11 | Suppress unseen files from retrieval while preserving retention evidence. | Not run | Immutable rename provenance and current-revision suppression were corrected, but the native SQL reconciliation/search matrix remains outstanding. |
| 12 | Reconcile restart during discovery/planning/processing without loss or duplication. | Not run | The linked receipt claim/fenced terminal lifecycle was corrected; restart-recovery cases still require disposable SQL execution. |
| 13 | Rebuild USearch from durable SQL/source state without watcher/in-memory dependence. | Not run | Rebuild cases were compiled/discovered but skipped without disposable SQL. |
| 14 | Keep partial or failed candidates from the active generation and search results. | Not run | Requires native SQL-to-USearch candidate/publication evidence. |
| 15 | Replay matching deferred work exactly once, additively, after capability registration. | Not run | Domain replay coverage passed, but the end-to-end SQL replay matrix was skipped. |
| 16 | Keep activity/scan counts and reasons truthful after refresh/reconnect. | Passed | Focused SourceRoot/Overview Web projection tests, including the corrected `Recovering` diagnostic, passed; SQL/browser reconnect remains outstanding. |
| 17 | Keep opaque generation IDs out of overflowing Overview summary cards. | Passed | Focused Overview projection tests passed; guarded browser layout proof remains outstanding. |
| 18 | Perform no model/GPU/process/PID/runtime/external/legacy/RabbitMQ/Docker/Vespa action. | Passed | Scope review and focused checkpoint performed no such action; no deployment or external action was authorised. |
| 19 | Write only `InProcess`, `DeferredCapability` and non-runnable `NativeExecutorLater` descriptors, without executor or GPU admission activation. | Passed | Focused source tests passed and migration inspection confirms the non-runnable `NativeExecutorLater` constraint; no executor/GPU activation was attempted. |

## Explicit non-actions

This checkpoint did not start, stop, supervise or inspect a process; record or
trust a PID or termination signal; probe a runtime or driver; change GPU
admission; download or activate a model; access an external system; or invoke
legacy, RabbitMQ, Docker or Vespa components. It did not deploy, restart IIS,
apply either migration, or perform live validation.

## Remaining validation and release work

Separate current approval is required before a disposable SQL/IIS checkpoint.
That checkpoint must apply the two migrations only to a disposable catalogue,
exercise root Save and Save and scan, safe-path rejection, a sentinel search
with provenance, deferred counts/replay, restart reconciliation, active-index
checksum/rollback evidence and the guarded browser slice. It must not expand
into process management, runtime/GPU/model activation, external access or
legacy actions. Phase 3B processor branches, Phase 3C contract parity, native
process management, model adapters, external access and legacy retirement
remain separate future work.
