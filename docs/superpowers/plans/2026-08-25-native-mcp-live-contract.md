# Native MCP, REST, CLI and Codex plugin v1 implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a clean native v1 knowledge-service contract over MCP, direct-loopback REST and CLI, including native Codex plugin preparation, confirmation-safe mutations, I: data layout, VSS recovery preparation and an unavailable clean-slate plan.

**Architecture:** The Application layer owns a closed set of v1 queries and commands. SQL Server persists knowledge, graph, command-preview and idempotent-commit records; retained source/job stores remain the authority for corpus work and fencing. Web, MCP and CLI bind the same v1 handlers directly, with no legacy request translation.

**Tech stack:** .NET 10/C#, ASP.NET Core minimal APIs, Model Context Protocol C# SDK, EF Core SQL Server, SQL Server full-text search, xUnit, disposable loopback SQL, PowerShell.

**Spec:** [docs/superpowers/specs/2026-08-25-native-mcp-live-contract-design.md](../specs/2026-08-25-native-mcp-live-contract-design.md)

## Global constraints

- Work only in `codex/native-mcp-live-contract` at `E:\LLM KB\.worktrees\native-mcp-live-contract`; preserve unrelated changes.
- Do not use Flux tools or a legacy runtime. Do not add compatibility adapters, credential bridges, custom Git executors or manual closeout.
- Direct-loopback only; protect actual secrets, credentials, connection strings and private keys in every durable write and disclosure.
- Parser work uses application-owned checksum-verified retained bytes only; no source-original path reaches a parser.
- Do not activate Outlook, models, GPU, OCR, vision, ASR, FFmpeg or cloud/network parsing. No browser test unless a UI change requires one.
- Every knowledge, memory, graph, code and corpus mutation uses preview-then-commit confirmation plus an idempotency key. Every destructive or expensive operation does too.
- Do not execute a clean-slate operation, configure VSS, deploy, push, or apply any production migration in this feature branch.
- The target app-owned hierarchy is `I:\FluxKnowledge`; MDF and LDF reside in `I:\FluxKnowledge\Data\Sql\Data` and `I:\FluxKnowledge\Data\Sql\Log` respectively. VSS is unencrypted, capped at 10% of `I:`, and OS-managed.
- Start each behavioural change with a red test. Run locked restore, zero-warning Release build, focused Domain/Integration/Web/CLI checks, full native Release suite and EF no-pending-model verification before closeout.
- Update `docs/architecture.md`, `docs/roadmap.md` and integration/setup documentation only after verified implementation. Use `scripts/dev/complete-feature.ps1` for the eventual closeout.

## File structure and responsibility map

| Area | Primary files | Responsibility |
| --- | --- | --- |
| V1 contracts and services | `src/FluxKnowledge.Application/IntegrationV1/*` | Canonical queries, preview/commit commands, response envelopes, action allowlist and validation. |
| Native knowledge model | `src/FluxKnowledge.Domain/Knowledge/*`, `src/FluxKnowledge.Application/Knowledge/*` | Notes, claims, lifecycle, graph relations and bounded query semantics. |
| Durable operation ledger | `src/FluxKnowledge.Application/Ports/INativeOperationStore.cs`, `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlNativeOperationStore.cs`, `.../Entities/NativeOperationEntities.cs` | Confirmation intents, idempotency receipts and atomic target-version validation. |
| SQL mapping | `FluxKnowledgeDbContext.cs`, `CanonicalSchemaConfigurations.cs`, new EF migration | Schema, indexes, check constraints and disposable-SQL behaviour. |
| Corpus/status views | `src/FluxKnowledge.Application/IntegrationV1/Corpus/*`, `.../Operations/*`, `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlNativeV1ProjectionReader.cs` | Bounded retained projection, source/job command adapters, capability/status and immutable audit reads. |
| Web/MCP | `src/FluxKnowledge.Web/Endpoints/NativeV1Endpoints.cs`, `src/FluxKnowledge.Web/Mcp/NativeV1McpTools.cs`, `Program.cs`, `WebHostComposition.cs` | Direct-loopback v1 REST and MCP registrations, shared envelope/error mapping. |
| CLI/plugin | `src/FluxKnowledge.Cli/Commands/NativeV1Command.cs`, `CodexPluginCommand.cs`, `src/FluxKnowledge.Integrations/Codex/*` | CLI client, generated native plugin material, registration status and repair. |
| Storage/recovery/cutover | `src/FluxKnowledge.Infrastructure.SqlServer/Configuration/SqlServerOptions.cs`, `Provisioning/SqlServerProvisioner.cs`, `scripts/deploy/*`, new `FluxKnowledge.Cli/Commands/FreshStartCommand.cs` | I: hierarchy, VSS policy command construction and guarded disposable-only clean-slate simulation. |

## Task 1: v1 operation protocol and durable fencing foundation

**Files:**
- Create: `src/FluxKnowledge.Application/IntegrationV1/NativeOperationContracts.cs`
- Create: `src/FluxKnowledge.Application/IntegrationV1/NativeOperationService.cs`
- Create: `src/FluxKnowledge.Application/Ports/INativeOperationStore.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/NativeOperationEntities.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlNativeOperationStore.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContext.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Configurations/CanonicalSchemaConfigurations.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/<timestamp>_AddNativeV1OperationLedger.cs`
- Create: `tests/FluxKnowledge.Domain.Tests/IntegrationV1/NativeOperationServiceTests.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/IntegrationV1/SqlNativeOperationStoreTests.cs`

**Interfaces:**

```csharp
public sealed record NativeActionPreviewRequest(
    string Action, string CanonicalPayload, string ActorSurface);
public sealed record NativeActionPreview(
    Guid IntentId, string ConfirmationId, string RequestFingerprint,
    DateTimeOffset ExpiresAtUtc, IReadOnlyList<NativeTargetVersion> Targets,
    string EffectSummary);
public sealed record NativeActionCommitRequest(
    string Action, string CanonicalPayload, string ConfirmationId,
    string IdempotencyKey, string ActorSurface);
public sealed record NativeActionReceipt(
    Guid OperationId, bool WasReplay, string Outcome, string? ReasonCode);
public interface INativeOperationStore
{
    ValueTask<NativeActionPreview> CreatePreviewAsync(NativeActionPreviewRequest request, CancellationToken cancellationToken);
    ValueTask<NativeActionReceipt> CommitAsync(NativeActionCommitRequest request, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write red domain tests for canonicalisation, confirmation and idempotency.**

  Cover a valid preview/commit, expired confirmation, altered payload, changed
  target row version, duplicate matching key and duplicate colliding key. The
  expected failures are `confirmation-expired`, `confirmation-mismatch`,
  `operation-fenced` and `idempotency-key-conflict`.

  ```csharp
  [Fact]
  public async Task CommitAsync_replays_matching_idempotency_key_without_second_mutation()
  {
      var first = await service.CommitAsync(commit, CancellationToken.None);
      var second = await service.CommitAsync(commit, CancellationToken.None);
      Assert.False(first.WasReplay);
      Assert.True(second.WasReplay);
      Assert.Equal(first.OperationId, second.OperationId);
  }
  ```

- [ ] **Step 2: Run the new domain tests and confirm they fail because the v1 operation types do not exist.**

  Run: `dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter FullyQualifiedName~NativeOperationServiceTests`

- [ ] **Step 3: Add the operation contracts, canonical JSON fingerprinting and bounded preview service.**

  Canonicalise property ordering, action discriminator and target identifiers
  before SHA-256 hashing. Generate a cryptographically random confirmation ID;
  persist only its hash. Enforce a five-minute expiry, non-empty idempotency key
  bounded to 128 ASCII characters, and an action allowlist supplied by the
  caller-specific handler.

- [ ] **Step 4: Add SQL entities, mapping and migration.**

  Persist `NativeOperationIntent` and `NativeOperationReceipt` with unique
  indexes on confirmation hash and `(ActorSurface, IdempotencyKey)`. Store only
  the fingerprint, action name, bounded safe target JSON, expiry and receipt
  reference. Use rowversion fields and one transaction to validate target
  versions, consume the intent and insert/replay the receipt.

- [ ] **Step 5: Add disposable-SQL race, cancellation and replay tests.**

  Start two commits for one confirmation, assert one durable outcome and one
  replay/conflict; cancel before transaction entry and assert no rows; cancel
  after persistence and assert retry returns the original receipt.

- [ ] **Step 6: Run the focused tests and EF model check.**

  Run:
  `dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter FullyQualifiedName~NativeOperationServiceTests`

  Run:
  `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~SqlNativeOperationStoreTests`

  Run:
  `dotnet tool run dotnet-ef migrations has-pending-model-changes --project src/FluxKnowledge.Infrastructure.SqlServer/FluxKnowledge.Infrastructure.SqlServer.csproj --startup-project src/FluxKnowledge.Web/FluxKnowledge.Web.csproj --configuration Release`

- [ ] **Step 7: Commit the reviewed foundation.**

  `git commit -am "feat: add native v1 operation protocol"`

## Task 2: native knowledge, claim and graph capability

**Files:**
- Create: `src/FluxKnowledge.Domain/Knowledge/KnowledgeItem.cs`
- Create: `src/FluxKnowledge.Domain/Knowledge/KnowledgeClaim.cs`
- Create: `src/FluxKnowledge.Domain/Knowledge/KnowledgeRelation.cs`
- Create: `src/FluxKnowledge.Application/Knowledge/KnowledgeCommandService.cs`
- Create: `src/FluxKnowledge.Application/Knowledge/KnowledgeQueryService.cs`
- Create: `src/FluxKnowledge.Application/Ports/IKnowledgeStore.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/KnowledgeEntities.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlKnowledgeStore.cs`
- Modify: `WebHostComposition.cs`, `FluxKnowledgeDbContext.cs`, `CanonicalSchemaConfigurations.cs`
- Create: EF migration and Domain/Integration tests under `Knowledge`

**Interfaces:**

```csharp
public sealed record KnowledgeMutation(
    string Action, string? ItemId, string? Title, string? Body,
    string? Subject, string? Predicate, string? ObjectText,
    string? Transition, string? RelatedClaimId, string? Reason);
public interface IKnowledgeCommandService
{
    ValueTask<NativeActionPreview> PreviewAsync(KnowledgeMutation command, string surface, CancellationToken cancellationToken);
    ValueTask<NativeActionReceipt> CommitAsync(KnowledgeMutation command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write red tests for the complete knowledge lifecycle.**

  Cover redacted note creation, secret-containing input rejection before any
  durable write, claim upsert idempotence, lifecycle transition audit, forget
  tombstone, bounded graph traversal and graph depth/count limits.

- [ ] **Step 2: Run the knowledge domain tests and confirm they fail.**

  Run: `dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter FullyQualifiedName~Knowledge`

- [ ] **Step 3: Implement the domain and application services.**

  Model notes and claims separately. Derive typed graph edges from canonical
  claim subject/predicate/object values; preserve claim lifecycle history rather
  than rewriting it. Forgetting removes content from the active projection,
  retains only the minimum tombstone/audit evidence, and is confirmation-bound.
  Apply `ILocalPrivateContentDisclosure` before preview persistence, durable
  knowledge writes and all result projection; extend its enum with explicit
  knowledge-write and knowledge-read kinds rather than bypassing it.

- [ ] **Step 4: Implement SQL persistence and integrate indexed knowledge search.**

  Add separate native knowledge tables and full-text indexed safe projection.
  Extend the lexical/hydration query path so `knowledge.search` returns retained
  source facts and active knowledge items with explicit provenance, never a
  source-original read. Keep deterministic embedding only; do not introduce a
  model provider.

- [ ] **Step 5: Add disposable-SQL integration tests.**

  Prove concurrent upsert, supersession, fencing, replay, cancellation,
  provenance, tombstone visibility and secret/public-output boundaries.

- [ ] **Step 6: Run focused checks and commit.**

  Run Domain and Integration filters for `Knowledge`; then commit with
  `git commit -am "feat: add native knowledge and graph capability"`.

## Task 3: corpus, code, operations and audit application services

**Files:**
- Create: `src/FluxKnowledge.Application/IntegrationV1/Corpus/NativeCorpusQueryService.cs`
- Create: `src/FluxKnowledge.Application/IntegrationV1/Corpus/NativeCorpusCommandService.cs`
- Create: `src/FluxKnowledge.Application/IntegrationV1/Code/NativeCodeQueryService.cs`
- Create: `src/FluxKnowledge.Application/IntegrationV1/Code/NativeCodeFeedbackService.cs`
- Create: `src/FluxKnowledge.Application/IntegrationV1/Operations/NativeOperationsStatusService.cs`
- Create: `src/FluxKnowledge.Application/IntegrationV1/Operations/NativeAuditQueryService.cs`
- Create: `src/FluxKnowledge.Application/IntegrationV1/NativeV1Facade.cs`
- Create: `src/FluxKnowledge.Application/Ports/INativeV1ProjectionReader.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlNativeV1ProjectionReader.cs`
- Modify: `WebHostComposition.cs`, `ILocalPrivateContentDisclosure.cs`, `LocalPrivateContentDisclosure.cs`
- Create: Domain, Integration and Web-independent application tests under `IntegrationV1`

**Interfaces:**

```csharp
public sealed record NativeCorpusQuery(string View, Guid? RootId, Guid? BranchId, Guid? JobId, int Limit, string? Cursor);
public sealed record NativeCorpusMutation(string Action, JsonElement Payload);
public sealed record NativeCodeQuery(string View, string? Query, Guid? BranchId, int Limit, string? Cursor);
public sealed record NativeOperationsStatus(string View, Guid? RootId, Guid? JobId, int Limit);
public interface INativeV1Facade
{
    ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken);
    ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken);
    ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write red tests for every accepted view and action.**

  Query tests cover bounded retained asset/branch/job facts, code status/symbols/
  matches, status views and immutable audit pagination. Command tests cover
  root creation/update/disable, scan release, watcher state
  changes and supported job retry. Reject every unrecognised view/action before
  it reaches persistence.

- [ ] **Step 2: Run the focused red tests.**

  Run: `dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter FullyQualifiedName~IntegrationV1`

- [ ] **Step 3: Implement query services using current native stores.**

  Reuse `ISourceRootStore`, `ISourceScanControlStore`, retained detail readers,
  C# code reader, source-root projection reader and immutable audit records.
  Add bounded query pages and opaque query-bound cursors. Do not reopen source
  originals or create a secondary source-processing path.

- [ ] **Step 4: Implement closed corpus actions through the preview/commit protocol.**

  Bind each action to a target-version provider and the current SQL lease/
  fencing behaviour. Queue long-running work and return accepted operation
  receipts; never execute scanning inline. Reject arbitrary paths outside the
  existing source-root policy and reject unregistered processor actions.

- [ ] **Step 5: Implement operations status and audit.**

  The status service returns only the `overview`, `sources`, `jobs`, `workers`,
  `processors` and `recovery` views from native stores. Capability fields use
  the canonical state/reason model in the specification. Audit projects only
  immutable, bounded, secret-filtered evidence.

- [ ] **Step 6: Add integration tests and run them.**

  Prove retained-only reads, missing/checksum-invalid artifact reasons,
  action confirmation/replay, expired lease fencing, cancellation/supersession,
  queue-only source synchronisation and secret filtering in every local projection.

- [ ] **Step 7: Commit the completed application slice.**

  `git commit -am "feat: add native v1 corpus and operations services"`

## Task 4: direct-loopback REST and MCP contract host

**Files:**
- Create: `src/FluxKnowledge.Web/Endpoints/NativeV1Endpoints.cs`
- Create: `src/FluxKnowledge.Web/Mcp/NativeV1McpTools.cs`
- Create: `src/FluxKnowledge.Web/NativeV1/NativeV1RequestMapper.cs`
- Modify: `src/FluxKnowledge.Web/Mcp/McpResultFactory.cs`
- Modify: `src/FluxKnowledge.Web/Mcp/McpServiceCollectionExtensions.cs`
- Modify: `src/FluxKnowledge.Web/Program.cs`
- Modify: `src/FluxKnowledge.Web/WebHostComposition.cs`
- Create: `tests/FluxKnowledge.Web.Tests/Endpoints/NativeV1EndpointTests.cs`
- Create: `tests/FluxKnowledge.Web.Tests/Mcp/NativeV1McpToolsTests.cs`

**Interfaces:**

```csharp
public static class NativeV1Endpoints
{
    public static IEndpointRouteBuilder MapFluxKnowledgeNativeV1(this IEndpointRouteBuilder endpoints);
}
public sealed class NativeV1McpTools(INativeV1Facade facade) { }
```

- [ ] **Step 1: Write failing REST and MCP equivalence tests.**

  For each of the nine canonical tools, assert the route/tool exists, accepts the
  same query/command shape, emits the same success envelope and maps identical
  reason codes. Assert every mutation rejects non-loopback, absent confirmation
  and absent idempotency key before dispatch.

- [ ] **Step 2: Run the new Web tests and confirm they fail.**

  Run: `dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter FullyQualifiedName~NativeV1`

- [ ] **Step 3: Add thin REST and MCP bindings.**

  Bind `/api/v1` only. Reuse the application DTOs and response envelope, with a
  single mapper for HTTP headers/body and MCP arguments. REST commit reads
  `Idempotency-Key`; MCP commit accepts `idempotency_key`. Both require the
  opaque confirmation ID and preserve request cancellation.

- [ ] **Step 4: Remove the old MCP tool registration from the live v1 host path.**

  Register only `NativeV1McpTools` at `/mcp`. Existing non-v1 UI endpoints may
  remain internal while the UI is migrated, but no prior external MCP contract
  is registered or documented as compatibility behaviour.

- [ ] **Step 5: Add hostile input, transient failure and disclosure tests.**

  Cover malformed JSON, oversize body, negative/oversize limits, invalid cursor,
  cancellation, retries for reads only, no retry after uncertain commit and
  secret withholding in MCP/REST output.

- [ ] **Step 6: Run focused Web tests and commit.**

  `git commit -am "feat: expose native v1 REST and MCP contract"`

## Task 5: native CLI and Codex plugin preparation

**Files:**
- Create: `src/FluxKnowledge.Cli/Commands/NativeV1Command.cs`
- Create: `src/FluxKnowledge.Cli/Commands/CodexPluginCommand.cs`
- Create: `src/FluxKnowledge.Integrations/Codex/NativeCodexPluginManifestWriter.cs`
- Create: `src/FluxKnowledge.Integrations/Codex/NativeCodexPluginRegistrar.cs`
- Create: `src/FluxKnowledge.Integrations/Codex/CodexRegistrationPaths.cs`
- Modify: `src/FluxKnowledge.Cli/Program.cs`
- Modify: relevant project references and locked package files only if a required managed dependency is proven necessary
- Create: `tests/FluxKnowledge.Integration.Tests/Cli/NativeV1CommandTests.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/Codex/NativeCodexPluginRegistrarTests.cs`

- [ ] **Step 1: Write red CLI contract tests.**

  Invoke each command with synthetic stdin/HTTP fakes and assert the same v1
  JSON envelope, exit code and error reason as REST. Confirm `--preview` never
  commits and `--commit` requires both confirmation and idempotency inputs.

- [ ] **Step 2: Write red isolated-filesystem registrar tests.**

  Assert generated plugin files live under `I:\FluxKnowledge\CodexPlugin` in
  production configuration, reference only `http://127.0.0.1:5137/mcp`, replace
  only the app’s known registration, detect drift, repair idempotently and leave
  unrelated Codex configuration byte-for-byte unchanged.

- [ ] **Step 3: Implement the CLI as a local v1 client.**

  Use `fluxknowledge` commands exactly as named in the specification. Do not
  duplicate domain logic in CLI handlers. Print stable JSON with no protected
  values and preserve non-zero exit codes for non-success envelopes.

- [ ] **Step 4: Implement native manifest generation and registrar.**

  Follow the current Codex plugin manifest schema at implementation time using
  the available plugin-creation guidance. Generate only native files, add
  `codex plugin status` and `codex plugin repair`. Keep repair and registration
  unavailable in normal composition; do not specify an automatic installation
  or go-live execution path in this plan.

- [ ] **Step 5: Run focused CLI/plugin tests and commit.**

  Run:
  `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~NativeV1CommandTests|FullyQualifiedName~NativeCodexPluginRegistrarTests`

  Commit: `git commit -am "feat: add native CLI and Codex registration"`.

## Task 6: I: hierarchy, recovery policy and guarded fresh-start preparation

**Files:**
- Create: `src/FluxKnowledge.Application/Operations/LiveRootLayout.cs`
- Create: `src/FluxKnowledge.Application/Operations/FreshStartPlan.cs`
- Create: `src/FluxKnowledge.Integrations/Windows/VssRecoveryPolicy.cs`
- Create: `src/FluxKnowledge.Integrations/Windows/FreshStartExecutor.cs`
- Create: `src/FluxKnowledge.Cli/Commands/FreshStartCommand.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Configuration/SqlServerOptions.cs`
- Modify: `SqlServerOptionsValidator.cs`, `Provisioning/SqlServerProvisioner.cs`, `WebHostComposition.cs`
- Modify: deployment configuration/scripts that set application, index, retained-artifact, spool and temporary paths
- Create: `tests/FluxKnowledge.Domain.Tests/Operations/LiveRootLayoutTests.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/Operations/FreshStartExecutorTests.cs`

- [ ] **Step 1: Write red layout and provisioning tests.**

  Assert every app-owned location resolves beneath `I:\FluxKnowledge`; assert
  data and log paths are exactly the specified `Data\Sql` locations; assert
  source artifact fallback no longer uses ProgramData; assert provisioning does
  not require a file-copy backup target.

- [ ] **Step 2: Write red VSS/fresh-start tests against disposable fake hosts.**

  Assert the generated VSS policy limits `I:` shadow storage to 10%, never
  includes an encryption action, and never performs a restore. Assert fresh
  start refuses an unexpected database, path, attached file, plugin identity or
  inherited volume snapshot; assert the disposable fake seam would reject all
  unowned targets and touches only pre-created app-owned test files after
  `fresh-start` is explicitly selected. This is not live execution evidence.

- [ ] **Step 3: Implement canonical root-layout validation.**

  Centralise path construction in `LiveRootLayout`; use it from SQL options,
  provisioning, Usearch, retained artifact store, runtime spool/temp/logs and
  plugin material. Reject symlink/reparse escapes and all roots outside the
  canonical hierarchy.

- [ ] **Step 4: Implement policy planning and disposable fresh-start simulation.**

  `VssRecoveryPolicy` returns a validated command plan only. `FreshStartExecutor`
  requires explicit `fresh-start` mode, validates exact ownership before each
  target operation and uses injected filesystem/SQL/Codex/VSS interfaces so
  tests never touch the live host. Production execution is unavailable; a
  separate future operational design is required before any workflow can invoke
  lifecycle actions.

- [ ] **Step 5: Run focused tests and commit.**

  Run Domain and Integration filters for `LiveRootLayout` and `FreshStart`; then
  commit with `git commit -am "feat: prepare native live root and recovery"`.

## Task 7: documentation, whole-slice verification and independent review

**Files:**
- Modify: `docs/architecture.md`
- Modify: `docs/roadmap.md`
- Replace: obsolete external-integration guidance in `docs/integrations.md` with the verified native v1 contract
- Modify: `docs/setup.md` only for verified native CLI/plugin and recovery prerequisites
- Create: release evidence under the existing non-public run-log location only; do not commit private output

- [x] **Step 1: Update documentation only after all feature tests pass.**

  Document the native v1 tool set, direct-loopback endpoint, confirmation flow,
  plugin status/repair, I: hierarchy, VSS policy and the fact that fresh-start is
  a preparation-only plan with execution unavailable. Update affected roadmap
  progress and remaining-work cells using verified evidence only.

- [x] **Step 2: Run the focused matrix.**

  Run locked restore, then Domain, Integration, Web and CLI filters covering
  confirmation/idempotency/fencing, retained-only projection, no-source-path,
  cancellation/supersession, secret boundaries, plugin repair, root guards and
  disposable fresh-start simulation.

- [x] **Step 3: Run milestone-wide verification.**

  Run:
  `dotnet restore FluxKnowledge.slnx --locked-mode`

  Run:
  `dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror`

  Run:
  `dotnet test FluxKnowledge.slnx -c Release --no-build --logger "console;verbosity=minimal"`

  Run the EF no-pending-model command from Task 1. Run browser validation only
  if the implementation changed an interactive UI route.

- [ ] **Step 4: Request one fresh independent whole-slice review.**

  The reviewer checks retained-only input, absence of source-original parser
  paths, confirmation/idempotency/fencing, cancellation/supersession, SQL
  migration safety, privacy/secret filtering, closed action allowlist, no
  unapproved runtime activation, plugin registration scope, I: root guards and
  fresh-start/VSS safeguards. Address findings with focused tests and rerun the
  affected matrix.

- [ ] **Step 5: Close out only after explicit live-operation authority.**

  Use `scripts/dev/complete-feature.ps1` as the sole branch closeout path. Do
  not pass migration/deployment flags without new current-turn approval. Report
  the script JSON result and live loopback probes only when they have actually
  run.

### Task 7 verification evidence

On 2026-08-26, the focused Domain/Integration/Web/CLI coverage and native
deployment/Phase 5 plan-only safety checks passed. The final non-live gate
passed locked restore, a zero-warning Release build, the full Release suite
(Domain 611/611, Integration 914/914, Web 198/211 with 13 browser skips, and
Outlook host 72/72), and EF no-pending-model verification. The key-ring
regression was independently reviewed and fixed in `addf758` before that run.
No browser validation or live operation was performed. The only remaining Task
7 gate is a fresh independent whole-slice review; closeout remains contingent
on explicit live-operation authority.

## Plan self-review

- **Spec coverage:** Tasks 1–4 implement the nine v1 commands, error envelopes,
  loopback boundary, retained-only reads and mutation invariants. Task 5 covers
  native plugin preparation and CLI parity. Task 6 covers I: layout, MDF/LDF,
  an unencrypted 10% VSS command plan and an execution-unavailable clean-slate
  plan. Task 7 covers documentation, verification, review and mandated closeout.
- **No unsupported activation:** Every task keeps model/GPU/media expansion,
  Outlook activation, network parsing and FFmpeg out of scope.
- **Placeholder scan:** The plan contains no unfinished design markers; the
  plugin-manifest implementation step deliberately directs the executor to the
  current Codex manifest guidance rather than assuming a stale schema.
- **Type consistency:** All mutation handlers converge on `NativeActionPreview`,
  `NativeActionCommitRequest` and `NativeActionReceipt`; transports are direct
  callers of that protocol, while corpus commands delegate to existing source
  and job stores for lease/fencing authority.
