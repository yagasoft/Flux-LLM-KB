# AGENTS.md

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
