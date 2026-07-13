# J drive runtime default design

## Goal

Make `J:\FluxLLMKB` the default Windows production install root for Flux
deployment, lifecycle, and feature-closeout scripts. Repair Codex plugin
installation when an old production path leaves a dangling directory link.

## Scope

- Default every deploy lifecycle script to `J:\FluxLLMKB` while retaining
  `-InstallRoot` and `FLUX_KB_INSTALL_ROOT` overrides.
- Make feature closeout derive its cache paths from the same default and pass
  the selected root explicitly to `update-flux.ps1`.
- Replace a dangling `~/.codex/plugins/flux-llm-kb` directory link before
  recreating the link to the selected deployed plugin.
- Update public deployment documentation and the affected roadmap entry.

## Non-goals

- Do not migrate or delete any existing D: runtime data.
- Do not change container-internal `/app` paths.
- Do not redeploy or restart the application unless focused validation proves
  the script and Codex-link repair cannot take effect independently.

## Verification

- Regression tests assert J: defaults and explicit-root forwarding.
- A Codex integration test creates a dangling plugin link and verifies that
  installation replaces it.
- Focused deployment, closeout, and Codex integration tests pass, followed by
  the project closeout script with `-SkipDeploy`.
- Validate only the Codex plugin link/cache after closeout; do not run Docker
  deployment unless required by a failed focused validation.
