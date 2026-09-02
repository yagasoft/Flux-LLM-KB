# Native clean-slate activation design

> **Status: Superseded.** This historical design is replaced by the approved [2 September 2026 native one-shot hard-cutover design](2026-09-02-native-hard-cutover-design.md).

## Status

Approved for implementation on 1 September 2026.  The user has explicitly
authorised deletion of `I:\FluxKnowledge` and the `FluxKnowledge` SQL
catalogue for this cutover.

## Goal

Replace the active legacy Python/PostgreSQL Codex plugin with the native
loopback plugin, make the native Sources and Outlook dashboard pages usable in
the strict production host, activate the implemented native background work,
and enable only native runtime capabilities with provisioned providers.

## Scope

### Included

- A clean-slate deployment to the fixed `I:\FluxKnowledge` live root and a
  freshly provisioned `FluxKnowledge` SQL catalogue.
- Native Codex plugin registration at `I:\FluxKnowledge\CodexPlugin`.
- Unregistration and uninstall of the legacy `flux-llm-kb@flux-llm-kb-local`
  Codex plugin.
- Native compatible implementations for the existing `UserPromptSubmit`,
  `PreCompact`, and `Stop` hooks.
- Strict-production registrations for the Sources and Outlook page control
  planes, source scanning/indexing services, and native background workers.
- Enabling the native worker and Outlook scheduled tasks.
- Enabling only declared runtime features whose native provider is provisioned
  and passes readiness.
- Live validation of the native dashboard, MCP integration, hooks, scheduled
  worker health, Sources and Outlook pages, and every provisioned capability.

### Explicitly deferred

- Downloading models or provisioning model runners, OCR engines, ASR engines,
  GPU runtimes, FFmpeg, or network-parsing providers that are not already part
  of the implemented native runtime.
- Enabling future provider flags before their native provider is implemented
  and passes readiness.  They remain disabled rather than creating a
  misleading partial capability.
- Migrating or deleting `J:\FluxLLMKB` data.  The legacy plugin is removed from
  Codex, but its files remain untouched unless separately authorised.
- Non-loopback exposure, weakened application-owned path checks, or removal of
  input validation.

## Current facts and root causes

1. Codex currently loads `flux-llm-kb@flux-llm-kb-local` version `0.1.0`.
   Its hooks run `python -m flux_llm_kb.cli hook ...` and require PostgreSQL at
   `127.0.0.1:5432`, which is unavailable after the native migration.
2. Native plugin material already exists at `I:\FluxKnowledge\CodexPlugin` and
   targets `http://127.0.0.1:5137/mcp`, but Codex has not registered it.
3. Production composition sets `strictProductionPaths` and therefore skips
   registration of `SourceRootService`, `OutlookPageState`, their SQL stores,
   and associated workers while the navigation still exposes `/sources` and
   `/outlook`.  Those pages therefore fail with dependency-injection errors.
4. The deployed production configuration marks every operational and runtime
   feature disabled.  The native go-live validation and deployment code reject
   an enabled runtime configuration.

## Architecture

### Native hook boundary

The native web host will expose a local-only hook operation for the three
Codex events.  It accepts the same JSON input shape as the existing hook
scripts and returns the same Codex hook output envelope.  `UserPromptSubmit`
uses the native knowledge-query path to add a bounded, sanitised context when
available; `PreCompact` continues without mutation; `Stop` persists the
allowed final-turn summary through the native knowledge command path with
idempotency keyed by the Codex turn identity.

The generated native plugin includes the hook declaration and a PowerShell
adapter that forwards stdin to the loopback host.  It does not invoke Python,
PostgreSQL, Docker, or any legacy Flux executable.

### Production composition

Strict production retains its canonical root and data-protection checks, but
it must register the same persisted source and Outlook control-plane services
as the isolated composition.  Worker registrations are driven by explicit
enabled options instead of being omitted merely because the host uses strict
paths.  Every service keeps its existing SQL fencing, root policy, operator
policy, and Outlook COM session constraints.

The runtime configuration parser permits an enabled capability only when its
concrete provider is provisioned and passes readiness.  Future-phase flags
remain disabled until their providers are implemented and available.  Status
therefore never presents an unavailable provider as partially active.

### Clean-slate cutover

The production closeout path becomes an explicitly confirmed, one-shot
operation.  It stops the Flux application pool, removes the owned native root
and the named SQL catalogue, creates the canonical root/ACL/configuration,
provisions the empty native catalogue, publishes the signed application
payload, starts the pool and approved scheduled tasks, registers the native
Codex plugin, and removes the legacy Codex plugin registration.

The operation is deliberately constrained to the explicitly authorised
`I:\FluxKnowledge` root and `FluxKnowledge` catalogue.  It does not delete
the legacy `J:\FluxLLMKB` directory.

## Behaviour and edge cases

- `/sources` and `/outlook` return a rendered page rather than HTTP 500 in the
  strict production host.
- A fresh catalogue contains no roots or Outlook profiles.  The pages remain
  usable for configuration; an enabled Outlook task performs no COM work until
  a valid profile exists.
- An enabled source worker performs no source-original read until an operator
  adds and releases an allowed source root.
- Future runtime flags remain disabled while the provider is absent; the
  status surface states that the capability is unavailable and leaves no retry
  loop or fake-ready state.
- Hook errors must not block a Codex turn.  They return `continue: true` with
  a sanitised diagnostic message and record a native audit event.
- Legacy-plugin removal occurs only after the native plugin is registered and
  its MCP and hook contracts pass live loopback checks.  If native registration
  fails, the closeout stops before legacy removal.

## Verification

1. Red/green unit and integration tests cover native hook envelopes,
   idempotent final-turn capture, strict-production page dependency resolution,
   disabled-unprovisioned runtime projection, and generated plugin material.
2. Browser tests cover `/sources` and `/outlook` against strict production
   composition.
3. Deployment tests prove the clean-slate target is exactly the authorised
   native root/catalogue, native registration precedes legacy removal, and the
   operation starts enabled worker/Outlook tasks.
4. The release build has zero warnings.  Focused tests run before the broader
   native suite.
5. Live checks run after deployment: loopback health/readiness, `/sources`,
   `/outlook`, native MCP initialise/tools-list, all three hook events,
   scheduled task state, source-worker health, Outlook-host health, and each
   provisioned runtime provider.  Future unprovisioned providers must remain
   disabled and must not pass readiness.

## Rollback and data handling

There is no data rollback for the authorised clean-slate deletion.  Before the
destructive step, the deployment command must print the exact root and SQL
catalogue it will remove and require the existing explicit confirmations.  If
deployment fails after the wipe, the recovery action is a fresh native
provision from the immutable build payload; it does not restore legacy or
pre-cutover native data.

## Acceptance criteria

- Codex calls only the registered native `fluxknowledge` plugin; no active
  Codex hook invokes `flux_llm_kb` or PostgreSQL.
- Sources and Outlook pages load successfully in the live strict-production
  host.
- Implemented native background services and the native Outlook scheduled task
  are enabled and observable.
- Every provisioned runtime feature is enabled and observable; every future
  provider remains disabled and is never falsely reported as ready.
- The authorised clean-slate deletes only `I:\FluxKnowledge` and the
  `FluxKnowledge` catalogue; `J:\FluxLLMKB` data is retained.
- Deployment, plugin registration, legacy plugin removal, and live checks all
  have fresh command evidence.
