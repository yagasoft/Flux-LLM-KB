# Native one-shot hard-cutover design

## Status

Approved by the user on 2 September 2026. This replaces the abandoned
challenge, evidence, finalisation and compatibility design.

## Outcome

The existing clean-slate deployment publishes the native service, removes
only `flux-llm-kb@flux-llm-kb-local`, registers only
`fluxknowledge@fluxknowledge` in the ordinary Codex profile, and completes
the release without a legacy or compatibility path.

## Scope and invariants

- The only destructive targets remain `I:\FluxKnowledge` and the
  `FluxKnowledge` SQL catalogue; `J:\FluxLLMKB` is untouched.
- `scripts/dev/complete-feature.ps1` is the sole live-deployment path.
- Native deployment occurs before exact legacy removal. Exact removal and
  absence occur before native plugin registration. A failure never restores
  the legacy plugin.
- The cutover uses the normal Codex profile only. It creates no separate
  `CODEX_HOME`, profile, copied credentials, secrets, SQL proof state,
  compatibility layer or finalisation mode.
- After registration, the user performs the ordinary Codex `/hooks` trust
  interaction once for `UserPromptSubmit`, `PreCompact` and `Stop`. The
  release code neither automates nor attests that interaction.
- Only the provisioned source worker and Outlook capture are enabled. Model,
  GPU, OCR, ASR, FFmpeg and network parsing remain disabled.
- Loopback binding, owned-path checks, input validation and exact destructive
  target safeguards remain in force.
- No Flux plugin tool, hook, MCP tool, Python, PostgreSQL or legacy runtime
  component participates in the cutover.

## One-shot execution

1. The existing acknowledged clean-slate procedure wipes and provisions the
   approved native root and SQL catalogue, then publishes, starts and checks
   the native service and its provisioned worker/task capability.
2. The same go-live executor proves the exact legacy package absent or removes
   that exact package and proves it absent. It then registers only the native
   package in the normal Codex profile and completes its existing native
   validation.
3. The operator opens Codex `/hooks` once, trusts and enables the three native
   handlers. No custom prompt, environment variable, secret or follow-up
   command is required.
4. Live smoke checks exercise fixed native hook endpoints, dashboard,
   Sources, Outlook, source worker and Outlook scheduled task. They also prove
   the native registration and the absence of the exact legacy package.

The final user interaction is intentionally ordinary Codex UI behaviour, not
a second attestation framework. If it is not completed, hooks remain disabled
by Codex and the native deployment still has no legacy fallback.

## Acceptance criteria

- The legacy package identifier is removed exactly and never reinstalled.
- `fluxknowledge@fluxknowledge` is the sole registered package in the normal
  profile.
- The `-GoLive` path requires explicit confirmation of legacy removal as well
  as the existing destructive acknowledgements.
- Tests prove deployment → legacy absence/removal → native registration;
  no registration occurs first and no code path restores legacy.
- No challenge/evidence classes, database persistence, special app-server
  client, isolated profile, temporary bridge or finaliser remains.
- The three hook adapters retain only loopback forwarding and input
  validation.
- Sources and Outlook compose successfully through the native dashboard, and
  only the provisioned source-worker and Outlook flags are enabled.

## External dependency

Actual go-live requires the existing
`FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP` environment value. If it is not
available at deployment time, the process stops before any destructive action
and reports that specific external blocker.
