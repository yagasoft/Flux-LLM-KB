# J drive runtime default implementation plan

> **Execution:** use the existing `codex/default-j-drive-runtime` worktree.
> Do not deploy the full application unless focused validation proves it is
> necessary.

## Acceptance criteria

1. Omitting `-InstallRoot` selects `J:\FluxLLMKB` in install, update, start,
   stop, status, migration, and closeout paths.
2. Explicit `-InstallRoot` and `FLUX_KB_INSTALL_ROOT` values continue to take
   precedence where supported.
3. Closeout passes its selected root to `update-flux.ps1` and derives default
   npm/wheelhouse locations from that root.
4. A dangling Codex plugin directory link is replaced with a link or copy of
   the live J: plugin source.
5. Docs describe J: as the default and no stale D: default remains.

## Steps

1. [x] Add failing static-contract tests for script defaults and closeout root
   forwarding, plus a Codex integration test for dangling links.
2. [x] Implement the smallest script and installer changes that satisfy them.
3. [x] Update setup, architecture, integrations, and roadmap wording.
4. [x] Run focused tests and review the diff.
5. [ ] Run `complete-feature.ps1 -SkipDeploy`, then validate the Codex plugin
   link/cache without a Docker deployment.
