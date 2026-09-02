# Native one-shot hard-cutover implementation plan

> **For agentic workers:** Use `superpowers:subagent-driven-development` one
> task at a time, with an independent review after each delivery-bearing task.

**Goal:** Ship one native clean-slate deployment with no legacy fallback:
deploy native, remove `flux-llm-kb@flux-llm-kb-local`, then register only
`fluxknowledge@fluxknowledge` in Codex’s ordinary profile.

**Spec:** `docs/superpowers/specs/2026-09-02-native-hard-cutover-design.md`

## Global constraints

- Never invoke Flux plugin tools, hooks or MCP; never use Python, PostgreSQL
  or legacy runtime components.
- Preserve `J:\FluxLLMKB`; delete only the exact legacy Codex package.
- No isolated `CODEX_HOME`, profile, copied credentials, challenge secret,
  SQL-backed evidence, compatibility layer or finalisation mode.
- Retain loopback, path, input-validation and destructive-target safeguards.
- Keep only source-worker and Outlook capture enabled. Keep model, GPU, OCR,
  ASR, FFmpeg and network-parsing flags false.
- Run live changes only through `scripts/dev/complete-feature.ps1` and only
  after review plus focused/full checks pass.

### Task 1: Remove the superseded proof protocol

**Files:**

- Modify: `src/FluxKnowledge.Integrations/Codex/NativeCodexPluginManifestWriter.cs`
- Modify: `src/FluxKnowledge.Web/Endpoints/NativeCodexHookEndpoints.cs`
- Modify: `src/FluxKnowledge.Web/Program.cs`
- Delete: proof/evidence/challenge code and its focused tests
- Test: existing native hook endpoint/manifest tests, updated to prove simple
  loopback forwarding without challenge headers or persistence.

- [ ] Write focused failing tests for hook forwarding without challenge data and
  for rejection of invalid loopback/input requests.
- [ ] Remove challenge secrets, proof records, evidence storage, canonical
  attestation and related dependency registration without changing the three
  native hook names or normal plugin identity.
- [ ] Retain only the native hook adapter’s safe loopback forwarding.
- [ ] Run focused Release tests, commit the delivery-bearing removal, then
  obtain independent review.

### Task 2: Make go-live a single hard cutover

**Files:**

- Modify: `src/FluxKnowledge.Integrations/Windows/NativeGoLive/NativeGoLiveExecutor.cs`
- Modify: `src/FluxKnowledge.Integrations/Windows/NativeGoLive/NativeGoLivePorts.cs`
- Modify: `scripts/dev/complete-feature.ps1`
- Modify: `scripts/dev/update-native-windows.ps1`
- Modify: `scripts/deploy/update-native-windows.ps1`
- Test: `tests/FluxKnowledge.Integration.Tests/Operations/NativeGoLiveOneShotAdmissionTests.cs`
- Test: `tests/native/complete-feature-dryrun.ps1`,
  `tests/native/native-go-live-contract.ps1`, and relevant binding tests.

- [ ] First write tests requiring `ConfirmRemoveLegacyPlugin` with `-GoLive`.
- [ ] Implement ordered behaviour: publish/start/activate native → remove or
  prove absence of exact legacy package → register native package → validate.
- [ ] Remove cutover/finaliser abstractions and tests. No code path may
  reinstall legacy or wait for a challenge proof.
- [ ] Run focused Release and PowerShell contracts, commit, then obtain
  independent review.

### Task 3: Verify and prepare the authorised live cutover

- [ ] Update `docs/roadmap.md` where the former staged cutover is described.
- [ ] Run restore, Release build, full Release tests, native contract tests and
  `git diff --check`.
- [ ] Obtain a whole-branch review for exact ordering, lack of fallback,
  disabled future capabilities and retained safeguards.
- [ ] If the required bootstrap value exists, execute the sole closeout script,
  have the user perform the one normal `/hooks` trust action, then capture
  fixed hook, dashboard, Sources, Outlook, worker and scheduled-task smoke
  evidence. If it is absent, report that specific external blocker before any
  destructive action.
