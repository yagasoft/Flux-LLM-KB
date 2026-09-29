# Workspace-aware Codex context implementation plan

Date: 29 September 2026. Baseline: `b3c80182`.
Status: implemented, closed out and live-validated on 30 September 2026.

**Goal:** Inject a bounded, workspace-scoped set of exact cited passages into
Codex prompts, or continue without context when the workspace or evidence is
unsuitable.

**Architecture:** Reuse the existing corpus lexical branch through an explicit
internal interface. A scoped application service applies the pure matching,
deduplication and packet policy; the existing hook adapter passes `cwd`, emits
the existing envelope and records metadata-only outcomes. Manual hybrid retrieval
and saved-note capture retain their current contracts.

**Technology:** .NET 10, existing SQL Server Full-Text/retained corpus services,
ASP.NET Core, xUnit and the current guarded disposable-SQL fixtures. No new
package, model or persistent schema.

**Spec:** [Workspace-aware Codex context](workspace-codex-context.md).

**Execution:** One implementation owner. Follow the repository's proportionate
workflow and test changed behaviour first. No automatic per-task agent handoffs.
The user approved implementation, then separately authorised deployment and live
validation. The design remains the source for release acceptance.

## Constraints and fixed values

- Preserve canonical SQL state, retained-only access and all disclosure,
  publication, epoch and evidence checks. Do not read source originals.
- Only `UserPromptSubmit` context selection changes. Keep Stop capture/replay,
  PreCompact, public search contracts and native tools intact.
- Workspace is the supplied canonical Windows directory subtree. Missing or
  unavailable scope never widens to all sources or triggers registration.
- Keep 32 KiB request, 2,048-unit prompt/cwd and 4,096-unit output bounds.
- One lexical search at limit 10; at most five zero-context reads; at most three
  whole records and one per document. Keep the existing 200-candidate SQL budget.
- Use the exact `workspace-lexical-v1` policy and stop list in the spec. Matching
  rank is not a confidence probability or proof of answerability.
- Retrieval cancellation budget: 1,750 ms. Audit budget: at most 250 ms inside
  two seconds total. Preserve the adapter's ten-second transport backstop.
- `Codex:PromptContextEnabled` defaults to true; false means no automatic
  context, with no global fallback.
- No model acquisition, production action, source registration, manual changes
  or scoped-note migration. The canonical model store remains `J:\Models`.

## Files and interfaces

| File | Responsibility |
| --- | --- |
| `src/FluxKnowledge.Application/Search/CorpusRetrievalService.cs` | Add the internal lexical interface; retain one implementation of lexical evidence/disclosure rules |
| `src/FluxKnowledge.Application/IntegrationV1/CodexPromptContextService.cs` (new) | Bounded request orchestration, citation readback and typed outcome |
| `src/FluxKnowledge.Application/IntegrationV1/CodexPromptContextPolicy.cs` (new) | Pure token eligibility, body matching, deduplication and complete-record rendering |
| `src/FluxKnowledge.Web/Mcp/NativeCodexHookService.cs` | Read cwd, invoke context service, preserve envelopes and record safe outcomes |
| `src/FluxKnowledge.Web/Program.cs` and `WebHostComposition.cs` | Register scoped dependencies and bind the boolean setting in both normal and strict host compositions |
| `src/FluxKnowledge.Application/IntegrationV1/CodexHookAuditEvent.cs` | Closed preflight reasons and bounded metadata |
| `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlCodexHookAuditWriter.cs` | Extend preflight JSON while keeping historical states readable |
| `docs/integrations.md`, `docs/architecture.md`, `docs/setup.md`, `docs/roadmap.md` | Record delivered hook contract, setting, acceptance and remaining work |

Proposed contracts, kept internal to application composition rather than added
to the public MCP/REST/CLI request schema:

```csharp
public interface ICorpusLexicalRetrievalService
{
    ValueTask<CorpusSearchResponse> SearchLexicalAsync(
        CorpusSearchRequest request, CancellationToken cancellationToken);
    ValueTask<CorpusPassageResponse> ReadAsync(
        CorpusReadRequest request, CancellationToken cancellationToken);
}

public sealed record CodexPromptContextOptions(bool Enabled = true);

public sealed record CodexPromptContextResult(
    string? AdditionalContext, string ReasonCode,
    int ExaminedCount, int InjectedCount, long ElapsedMilliseconds);

public interface ICodexPromptContextService
{
    ValueTask<CodexPromptContextResult> BuildAsync(
        string prompt, string? cwd, CancellationToken cancellationToken);
}
```

`CodexPromptContextService` consumes `ICorpusLexicalRetrievalService`,
`CodexPromptContextOptions` and `TimeProvider`. At prompt-handler entry, the hook
creates a two-second cancellation source linked to caller cancellation. The
service links its 1,750 ms child budget to that token; the subsequent audit links
its 250 ms child budget to the same overall token. Use the same `TimeProvider`
for all three timers. Do not start audit after the overall budget expires.
Choose private helper names as needed; do not create additional public APIs.

Use one scoped `CorpusRetrievalService` instance for its existing interface and
the new lexical interface. `SearchAsync` preserves the hybrid dispatch; the
explicit lexical method cannot reach it. Avoid constructing an independent
copy of the retrieval algorithm in the hook.

## Milestone 1: safe cited context through the actual hook

**Deliverable:** A disposable registered workspace produces one exact cited
excerpt through the loopback hook. Missing scope, sibling files and stale or
withheld evidence cannot enter the packet. No inference is invoked.

**Tests:**

- New `tests/FluxKnowledge.Domain.Tests/IntegrationV1/CodexPromptContextServiceTests.cs`.
- Existing `tests/FluxKnowledge.Integration.Tests/Search/ScopedCorpusRetrievalTests.cs`.
- Existing `tests/FluxKnowledge.Web.Tests/Mcp/NativeCodexHookServiceTests.cs` and
  `tests/FluxKnowledge.Web.Tests/Endpoints/NativeCodexHookEndpointTests.cs`.
- New `tests/FluxKnowledge.Web.Tests/Endpoints/WorkspaceCodexContextAcceptanceTests.cs`.

- [x] Write failing tests for a precise two-term query, real source identities
  and exact readback. Assert one workspace lexical call, zero hybrid calls,
  valid cited text and a true `continue` envelope. Attach a throwing hybrid
  implementation in tests so accidental routing cannot pass silently.
- [x] Add missing cwd, invalid/unknown scope, nested roots, parent root with a
  narrower cwd, sibling prefix and unindexed-worktree cases. Assert zero global
  queries and zero source/knowledge mutations. Reuse the existing real-SQL
  workspace fixtures rather than replacing them with only mock filtering.
- [x] Add stale revision, deletion and disclosure-withholding between search
  and read; mismatched source/span/root; hostile instruction text; and complete
  record bounds. Preserve the exact cited body and JSON-escape source fields.
- [x] Run the narrow new tests and observe the intended failures before code.
- [x] Extract the lexical entry point without changing manual hybrid dispatch.
  Implement the context service, initial pure policy and Web composition. Add
  cwd handling to the hook; keep the generated adapter bytes unchanged.
- [x] Return no-context on insufficient queries, disabled context, missing scope
  and supported retrieval refusals. Preserve caller cancellation and fail-open
  envelopes. For unexpected prompt-context failures, suppress exception text;
  retain the existing Stop-specific diagnostic behaviour.
- [x] Make the first real-SQL/HTTP vertical test pass, including exact readback
  and a metadata-only audit event. Verify the existing manual hybrid routing
  tests and loopback guard still pass.

**Checkpoint:** Confirm an executable end-to-end result exists before expanding
the fixture cohort. If lexical search requires bypassing current publication or
disclosure safeguards, stop and revise the design.

## Milestone 2: measured selection quality, limits and compatibility

**Deliverable:** Automatic context meets the frozen relevance, duplication,
resource and latency gates, including no-context outcomes and cancellation.

**Tests and fixtures:**

- New `tests/FluxKnowledge.Domain.Tests/IntegrationV1/CodexPromptContextPolicyTests.cs`.
- Extend the service and end-to-end tests from milestone 1.
- Existing `tests/FluxKnowledge.Integration.Tests/Mcp/NativeCodexHookPersistenceIntegrationTests.cs`.
- Existing `tests/FluxKnowledge.Integration.Tests/Codex/NativeCodexPluginMarketplaceTests.cs`.
- New `tests/FluxKnowledge.Integration.Tests/Fixtures/WorkspaceCodexContext/cohort.json`;
  only public/synthetic source material, prompts and fixed expected labels.

- [x] Freeze 36 acceptance cases before tuning: 12 positive evidence requests,
  12 negative/vague/unanswerable requests and 12 boundary cases. Keep development
  cases separate. Include short follow-ups, nearby but irrelevant text, exact
  identifiers, conflicting current documents and a source containing instructions.
- [x] Write failing tests pinning the specified token rule, original-case
  identifier detection, token boundaries, exact stop list and body-only matches.
  A title/header-only match must not satisfy evidence eligibility.
- [x] Write duplicate-body/document tests, maximum ten hits/five reads/three
  records tests, Unicode bounds, and an oversized first record followed by a
  smaller valid one. Assert whole-record skipping and no altered citation text.
- [x] Write clock-controlled deadline tests and a real-SQL cancellation check.
  Assert no retries, no unresolved owned calls after completion, and a distinct
  `retrieval-timeout` outcome. Audit failure must not discard an otherwise valid
  packet or delay it beyond the remaining cooperative budget.
- [x] Complete the pure policy and bounded orchestration. Extend preflight audit
  reason/metadata validation and SQL writer output without changing historical
  event states or adding a migration. Test that prompts, cwd, retained text and
  tokens are absent from audit and unexpected-error diagnostics.
- [x] Verify the adapter forwards cwd exactly in both supported PowerShell
  flavours; test missing cwd compatibility and UTF-8 failure handling. Do not
  alter its trust/registration or extend its ten-second timeout.
- [x] Run the frozen cohort through the HTTP hook and re-read every injected
  reference. Require at least 10/12 useful positive results, zero irrelevant
  injections across 12 negative cases, and all 12 boundary checks passing.
- [x] Run 40 public/synthetic HTTP hook calls with two callers in the disposable
  indexed environment. Record all responses, including empty and timed-out ones;
  require p95 at most 2.5 seconds and positive evidence delivery under healthy
  conditions. Treat missed positive context as a failure, not a latency win.
- [x] Run existing Stop idempotency/persistence, PreCompact, manual corpus
  retrieval and transport checks. Update current capability docs and roadmap
  using the observed results; preserve deferred items and manuals.

**Checkpoint:** Reassess after this second coherent batch. A failed selection or
latency gate permits a bounded correction against development cases; a second
failure stops this approach for a design decision. Do not tune held-out labels,
add new models, or widen workspace scope to increase apparent coverage.

## Focused verification commands

Use the existing locked restore/build prerequisites if the reused worktree lacks
outputs. Run the narrow test by name while developing; after each coherent
milestone run the relevant group below. These commands use disposable/model-free
fixtures, not the installed service.

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter 'FullyQualifiedName~CodexPromptContext'
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter 'FullyQualifiedName~NativeCodexHook'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~WorkspaceCodexContext|FullyQualifiedName~ScopedCorpusRetrieval|FullyQualifiedName~NativeCodexHookPersistence|FullyQualifiedName~NativeCodexPluginMarketplace'
pwsh -NoProfile -File tests/native/repository-contract.ps1 -SourceRoot .
git diff --check
```

Expected: all selected checks pass, with zero new warnings. The cohort and
40-call latency assertions belong to the opt-in local acceptance test in
`WorkspaceCodexContextAcceptanceTests`; do not impose a wall-clock latency
assertion on the ordinary CI suite. Its documented enabling variable is
`FLUXKNOWLEDGE_RUN_WORKSPACE_CONTEXT_ACCEPTANCE=1`, scoped to the test process,
with a disposable database and no production connection.

## Review focus and closeout

The complete review must cover these concrete failure cases, already assigned
to the milestones above:

1. A cwd below a broad registered root exposes a sibling project.
2. A normal DI registration routes the hook through the 25-second hybrid engine.
3. A duplicate, altered or stale passage is presented as exact current evidence.
4. Source instructions or exception text become trusted instructions or audit data.
5. A cancelled retrieval/audit continues after its scoped dependencies are disposed.

- [x] Self-review the complete diff against the spec and preserve evidence.
  Obtain one focused independent review for the boundaries above before
  operational activation; resolve blocking findings within this scope.
- [x] Use `scripts/dev/complete-feature.ps1` for feature closeout on the task
  branch; do not manually substitute its commit/merge/push sequence. Default
  closeout does not authorise production actions. Report `failed_step` and
  `log_path` if it fails, and repair/rerun within authorised scope.
- [x] Review and apply an incremental deployment plan after deployment enters
  the user's requested scope. Obtain the required independent operational review
  and explicit user authority before applying or restarting. Validate
  plugin-material handling; do not assume registration/trust changes are covered
  by the updater.
- [x] For the authorised activation, verify the actual client sends cwd and that
  an existing public acceptance workspace injects exact citations while an
  unindexed workspace stays empty. The disabling setting is the operational fallback;
  do not restore global injection as the new feature's fallback.

## Implementation evidence

The branch uses the existing `hybrid-status-docs/LLM KB` worktree. A disposable
SQL/HTTP hook test returned an exact re-readable citation while missing and
unknown workspaces returned no packet. The frozen public/synthetic cohort ran
through the HTTP hook on 30 September 2026: 12/12 positive cases supplied useful
evidence, 0/12 negatives injected irrelevant context, and 12/12 boundary cases
passed. Forty requests with two callers measured 16 ms p95 in that test host.
These are disposable test-host results. Branch checks, independent code review
and `complete-feature.ps1` closeout passed. A separately authorised incremental
IIS release `20260929T215634Z-2c6712dcd77a` deployed commit `2c6712dc` with
no migrations and preserved `CodexPlugin`. The independent operational review
approved the exact no-migration update. Live probes returned HTTP 200 for live,
ready and index health; the validation hold was released, and IIS site and pool
remained started. The installed adapter returned one exact re-readable public
Mercury passage; missing and unindexed workspaces returned no context. An actual
read-only Codex CLI run from the registered workspace recorded one injected
context event and answered the 176-Earth-day question. The metadata-only audit
contained no prompt, passage or reference.
