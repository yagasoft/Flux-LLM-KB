# Stop-hook root-cause investigation plan

> **For agentic workers:** Use `superpowers:executing-plans` and `superpowers:systematic-debugging` when the user resumes this investigation. Execute sequentially; do not dispatch agents or implement a fix merely because this plan exists. The user requested a pause to switch Codex model.

**Goal:** Identify the exception, failing operation and triggering condition behind the Stop-hook warning, supported by a reproducible test or directly correlated runtime evidence.

**Architecture:** Trace the installed hook adapter through the loopback HTTP endpoint, Stop service, native knowledge command and SQL persistence transaction. Start with existing read-only evidence; use a disposable reproduction only when needed. Keep diagnosis separate from remediation and production actions.

**Tech stack:** PowerShell, ASP.NET Core, EF Core, SQL Server, xUnit and native FluxKnowledge audit/query surfaces. Do not introduce a persistence migration or change the backend for this investigation.

**Spec:** The user's request in this task is to design an investigation procedure and pause for a Codex model switch. This document is the investigation specification and handoff, not approval to execute or deploy a fix.

## Global constraints

- Pause after saving this plan. No investigation execution, tests, service changes or model switch are part of the planning turn.
- After the user resumes, start with read-only checks. Obtain explicit authority before diagnostic code changes or a production test write, logging/configuration change, restart, deployment, migration or permission change.
- Use a dedicated `codex/` worktree for any subsequently authorised code or diagnostic-test changes. Preserve unrelated work.
- Do not replay a real user's Stop payload, retry an uncertain capture, or create/delete production knowledge as a diagnostic shortcut. Inspect its existing receipt first.
- Do not log or publish prompts, assistant responses, raw session/turn identifiers, credentials, connection strings or SQL parameter values. The user has expressly authorised bounded caught-exception text in the internal `codex_hook.processing_failed` audit and Events view; do not copy request-body fields into that text or return it through `operations.audit`. Keep private diagnostic evidence outside Git with restricted access. Public evidence must be sanitised.
- Preserve fail-open hook behaviour and atomic note/receipt/audit persistence. Do not suppress the warning or relax validation to make a test pass.
- No model acquisition or model-cache manipulation is needed. Normal operation and tests must remain offline for model acquisition; `J:\Models` is the only permitted real-model store. Cache misses are stop conditions, not download permission.
- No `dotnet format`, Docker builder-cache pruning, production database reset, dashboard manual regeneration or unrelated refactoring.

## Starting evidence and limits

These are observations from the preceding diagnostic turn, not fresh verification of the next execution:

- The screenshot places `Native Codex hook could not access local knowledge; continuing.` under the Stop hook. The service emits this generic message after a processing exception; it does not identify the exception itself.
- Live audit contained `codex_hook.processing_failed` with `reasonCode: unexpected`, alongside successful `codex_hook.preflight_completed` events. The generic failures were not conclusively correlated to the screenshot's particular turn or phase.
- The deployed assembly reported visibility-fix commit `5964542087996c0a6b6de1e34fd82eceebac01f4`. The checkout was clean at `9aa3a58`, a subsequent model-cache policy change. Recheck both rather than assume they still match this baseline.
- IIS stdout logging was disabled. The prior search found no matching Stop phase warning in the Application event log or runtime log files. Missing logs are not evidence that the Stop path succeeded.
- Earlier live deployment verification demonstrated prompt retrieval and Events visibility, not successful Stop persistence. Existing tests are useful comparators, not proof of production success.
- Prompt retrieval succeeding excludes a continuous total outage at that moment, but does not establish write permissions, schema compatibility or successful transaction commit.
- Corpus is a retained-source view. Its row count is not an acceptance test for saved knowledge notes.
- Legacy `kb.brief`, `kb.code_status`, `kb.remember` and `kb.finalize_turn` tool names were unavailable. Native `knowledge_search`, `code_query` and `operations_audit` were callable. Retrieve tool schemas again if needed; do not invent legacy calls or use a knowledge mutation as a diagnostic probe.

## Source and test map

Paths below are repository-relative to `E:\LLM KB`; read the deployed revision where behaviour could differ from the checkout.

| Boundary | Relevant file |
| --- | --- |
| Generated native hook manifest, PowerShell adapter, response handling and timeout | `src/FluxKnowledge.Integrations/Codex/NativeCodexPluginManifestWriter.cs` |
| HTTP route, loopback gate and JSON/body limits | `src/FluxKnowledge.Web/Endpoints/NativeCodexHookEndpoints.cs` |
| Stop receipt lookup, preview, commit and fail-open handling | `src/FluxKnowledge.Web/Mcp/NativeCodexHookService.cs` |
| Native family routing | `src/FluxKnowledge.Application/IntegrationV1/NativeV1Facade.cs` |
| Knowledge command preparation | `src/FluxKnowledge.Application/Knowledge/KnowledgeCommandService.cs` |
| Confirmation/idempotency orchestration | `src/FluxKnowledge.Application/IntegrationV1/NativeOperationService.cs` |
| SQL receipt lookup, preview intent, transaction, locks and atomic save | `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlNativeOperationStore.cs` |
| Service contract and safe logging tests | `tests/FluxKnowledge.Web.Tests/Mcp/NativeCodexHookServiceTests.cs` |
| HTTP contract tests | `tests/FluxKnowledge.Web.Tests/Endpoints/NativeCodexHookEndpointTests.cs` |
| Real-SQL Stop persistence/replay/rollback tests | `tests/FluxKnowledge.Integration.Tests/Mcp/NativeCodexHookPersistenceIntegrationTests.cs` |
| Native transaction tests | `tests/FluxKnowledge.Integration.Tests/IntegrationV1/SqlNativeOperationStoreTests.cs` |
| Disposable SQL database lifecycle and connection safety | `tests/FluxKnowledge.Integration.Tests/Support/NativeSqlServerFixture.cs` |

Do not assume `plugins/flux-llm-kb/scripts/invoke_hook.ps1` is the active adapter: that file invokes the legacy Python integration. Resolve the installed native plugin and inspect its actual `hooks/invoke-native-hook.ps1` and manifest before testing.

## Task 1: Establish a correlated, read-only failure record

**Deliverable:** A sanitised evidence record identifying the active components, failure time window, known durable outcome and the first unresolved boundary.

- [ ] Record UTC time, checkout status/commit, deployed assembly informational version, installed native plugin/adapter versions and hashes. Identify the serving process, IIS application pool identity and effective database target without printing secrets. Inspect configuration only for relevant settings; do not dump it.
- [ ] Read bounded `operations_audit(view="events")` pages and retain only `codex_hook` metadata for the failure window. Record event IDs, UTC timestamps, safe reason codes and available correlations. Use existing operator query filters where supported. Do not infer a matching session from timestamp proximity alone.
- [ ] Inspect existing IIS request records and application logs in that window. Establish whether the request reached `/native/v1/codex/hooks/Stop`, the response status/duration and whether a phase warning exists. HTTP 200 or `continue: true` is not proof of a save because the endpoint fails open.
- [ ] Trace the active adapter's stdin/body handling, endpoint, timeout and output projection. Compare only payload field presence, JSON types and lengths with the endpoint and service requirements. Inspect private originals locally only if necessary; never copy them into the plan or tests.
- [ ] Inspect SQL metadata read-only: applied migrations, required tables/columns, relevant principal roles/permissions, and existing intents/receipts/capture-event metadata. If a legitimate failed turn can be matched safely, derive its existing Stop idempotency key using the source algorithm and look up that receipt without printing the raw identities or note body.
- [ ] Classify the observed outcome as saved, not saved or unknown. Require correlated note/receipt/capture evidence for saved. An unconsumed preview intent alone is not a saved note. Report any unexpected partial durable state as an invariant concern; do not clean it up.

**Decision:** If an existing exception and phase are available, proceed to Task 3. If not, explicitly record the missing evidence and proceed to the gated diagnostic design in Task 2. Do not call `unexpected` the root cause.

## Task 2: Expose the failing boundary without contaminating live data

**Deliverable:** One minimal reproduction and an exception fingerprint tied to a specific operation, or a precise request for the missing diagnostic authority.

- [ ] Before modifying code or running a stateful probe, present its scope and obtain authority. Prefer an isolated worktree, disposable database and synthetic note. No production transcript, connection string or production test write is required for the first reproduction.
- [ ] Follow the Stop sequence: input validation -> receipt lookup -> knowledge preview -> command preparation/replay check -> transaction/locks -> mutation/save -> transaction commit -> Events notification -> adapter response. A failure before the transaction differs materially from an uncertain commit.
- [ ] Use existing phase logging first. If insufficient, use the approved bounded `codex_hook.processing_failed` exception text alongside hook event, phase, exception/inner-exception type and numeric SQL error metadata. In a synthetic local reproduction, capture stack method names to locate the throwing statement. Do not enable broad sensitive EF logging or request-body logging.
- [ ] Exercise the deployed revision through the real HTTP endpoint, then through the installed adapter or an exact copied adapter pointed at the disposable endpoint. Use a synthetic payload with `session_id`, `turn_id` and `last_assistant_message`, such as `Synthetic Stop capture investigation.` Generate unique test identities; reuse the same pair only for the intentional replay check.
- [ ] Compare the standard test host with a safely isolated IIS-equivalent environment only if the first reproduction passes. Match effective principal privileges, relevant configuration and schema deliberately; do not grant the production principal extra rights or assume developer/admin tests cover it.
- [ ] If the problem occurs only live, stop and propose one bounded diagnostic capture: exact metadata, duration, restart/deployment requirement, expected side effects and restoration/rollback steps. A live synthetic Stop requires explicit permission to create its note; agree whether that labelled note remains or is subsequently forgotten. Do not perform it under earlier deployment approval.

## Task 3: Test one evidence-backed hypothesis

**Deliverable:** A causal explanation naming the throwing operation, exception, trigger and why existing tests or prompt retrieval did not detect it.

Select the hypothesis from the observed failure boundary, not from this list's order:

| Evidence | Discriminating check |
| --- | --- |
| Adapter or HTTP-only failure | Compare the same synthetic payload through direct HTTP and the active adapter; check encoding, field projection, timeout and response handling. |
| Receipt lookup or preview failure | Compare relevant schema and effective SELECT/INSERT permissions under equivalent identities; inspect command preparation and exact exception metadata. |
| Commit/save failure | Use the SQL error number and stack location to distinguish constraints, permission failures, transaction/lock behaviour and serialization problems. Check the note, receipt and capture event as one invariant. |
| Input-sensitive failure | Minimise the synthetic payload while preserving lengths, Unicode/Markdown structure and the failing condition; compare transport limits with command/domain/database limits. |
| Intermittent or deadline-related failure | Correlate phase durations with the actual adapter timeout, request cancellation, retries and database contention. Do not increase timeouts or add retries without this evidence. |

- [ ] State one hypothesis in the form: “Operation X throws exception Y when condition Z holds; evidence A supports it and observation B would falsify it.”
- [ ] Change one factor in the disposable reproduction. Preserve the failing case and compare against a control. A one-off passing request is not sufficient to establish causality.
- [ ] Before any remediation, retain a focused failing regression test if reproducible. If the failure is environmental and cannot be reproduced safely, retain directly correlated runtime evidence and state the remaining limitation rather than inventing a test result.
- [ ] If a test fails for another reason, investigate that reason rather than weaken assertions. After two focused hypothesis attempts without localisation, stop, report the missing evidence and revise the smallest next probe. Escalate immediately for security, partial-commit or other invariant concerns.

## Verification commands for the authorised execution stage

These commands are specified, not executed in the planning turn. Run from the diagnostic worktree. Read fixture setup first, confirm offline model behaviour, and use the repository's normal build prerequisites; do not silently substitute production connections.

```powershell
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --filter "FullyQualifiedName~NativeCodexHookServiceTests|FullyQualifiedName~NativeCodexHookEndpointTests"
```

For real-SQL verification, `NativeSqlServerFixture` uses `FLUXKNOWLEDGE_TEST_SQL_CONNECTION`, validates a local server-level connection and creates/drops generated `FluxKnowledge_Phase1Tests_...` databases. Inspect the entire fixture and its fallback/server setup before running it. Use an explicitly verified disposable SQL instance, keep credentials out of output, and stop if a required local dependency is missing rather than download it automatically.

```powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~NativeCodexHookPersistenceIntegrationTests|FullyQualifiedName~SqlNativeOperationStoreTests"
```

Require the SQL tests to execute: skipped tests do not establish persistence. Record fresh exit codes, warnings and pass/fail/skip counts. Existing green tests establish only their covered conditions; the new reproduction must cover the actual trigger.

## Completion, remediation boundary and live acceptance

The investigation is complete when it identifies the failing operation and exception, demonstrates the triggering condition with controlled or directly correlated evidence, states the affected data/outcome, and proposes a narrowly scoped remedy. Otherwise report the exact missing evidence and next safe probe; do not declare a root cause from a generic audit event.

The first eventual end-to-end acceptance slice is: synthetic Stop input -> exactly one durable knowledge note -> exactly one operation receipt and `codex_hook.capture_saved` event -> visible Events row -> exact replay creates no duplicates. Prompt retrieval remains working, failed pre-commit writes leave no partial capture, and no hook-payload content leaks into audit/logs; bounded caught-exception text is the explicitly approved operator diagnostic exception. This is a later fix-verification requirement, not proof already obtained.

At the end of the investigation, pause with the evidence and proposed correction. Implementation, review, deployment and live validation require their applicable authority. Any approved routine deployment must follow the incremental IIS updater's reviewed `-PlanOnly`/`-Apply` path and repository release gates; no clean-slate deployment or speculative migration. Preserve the previous release/configuration for rollback. No release or cleanup action is authorised by this plan.

## Model-switch handoff

Status: procedure prepared; root cause remains unconfirmed; execution paused at the user's request. On resumption, read this plan and start Task 1. Do not treat prior observations, test results or deployments as fresh evidence, and do not automatically continue a production mutation.
