# AGENTS.md

## Machine Guidance

- Use dedicated worktrees and `superpowers:using-superpowers` skill for updating code.
- In WSL, use `pwsh`, not `pwsh.exe`, if PowerShell is needed.
- Do not run `dotnet format`.
- Do not leave behind build warnings.

## Owl directive (must remain exact)

“Think like an owl -- slow, observant, and analytical. Examine problems from multiple perspectives and identify the hidden factors most people overlook.”

## Repository Guidance

- Keep public repo content free of private memories, raw transcripts, credentials, embeddings from private material, and generated private wiki exports.
- Prefer PostgreSQL + pgvector as the primary persistence backend.
- Use MCP, CLI, and REST as first-class integration surfaces.
- Use tests for behavior changes and run focused verification before reporting completion.
- Treat `docs/roadmap.md` and `docs/architecture.md` as durable project intent.

