# AGENTS.md

## Machine Guidance

- Use dedicated worktrees and `superpowers:using-superpowers` skill for updating code.
- In WSL, use `pwsh`, not `pwsh.exe`, if PowerShell is needed.
- Do not run `dotnet format`.
- Do not leave behind build warnings.
- Feature closeout for `codex/...` branches must use `scripts/dev/complete-feature.ps1`.
  Do not manually run the commit/squash-merge/push/deploy/purge sequence unless
  explicitly overridden. If the script fails, stop and report its JSON
  `failed_step` and `log_path`, fix only that failure, then rerun it. If a
  closeout failure or repeated manual workaround shows that
  `complete-feature.ps1` itself is missing required setup, validation,
  environment handling, or diagnostics, update the script in the active branch
  with focused tests or verification, then rerun the script instead of relying
  on ad hoc pre-steps. Use
  `-DryRun`, `-SkipDeploy`, or `-KeepWorktree` only when explicitly appropriate.
  Never use `git reset --hard`, force-push, or delete a worktree/branch before
  merge, push, deploy, and probes have succeeded.

## Owl directive (must remain exact)

“Think like an owl -- slow, observant, and analytical. Examine problems from multiple perspectives and identify the hidden factors most people overlook.”

## Repository Guidance

- Keep public repo content free of private memories, raw transcripts, credentials, embeddings from private material, and generated private wiki exports.
- Prefer PostgreSQL + pgvector as the primary persistence backend.
- Use MCP, CLI, and REST as first-class integration surfaces.
- Use tests for behavior changes and run focused verification before reporting completion.
- Treat `docs/roadmap.md` and `docs/architecture.md` as durable project intent.
- After each roadmap-significant session or turn, update `docs/roadmap.md`
  `Progress %` and `Remaining Work` entries for affected roadmap items before
  closeout.
- Do not update `docs/user-guide/dashboard-user-manual.md`, its DOCX/screenshots, or rendered manual assets unless the user explicitly asks for manual updates in the current turn. Dashboard UI, automation behavior, operator API, setup-doc, or screenshot changes may ship without manual regeneration when no explicit manual request is present.
