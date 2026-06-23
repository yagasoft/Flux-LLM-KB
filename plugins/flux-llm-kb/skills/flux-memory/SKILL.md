---
name: flux-memory
description: Use before non-trivial Codex work to retrieve compact Flux-LLM-KB context and after work to store durable, redacted outcomes.
---

# Flux Memory Workflow

1. Before non-trivial work, call the Flux-LLM-KB MCP brief tool with the user's current task. In Codex, this may be exposed as the wrapper `mcp__flux_llm_kb.kb_brief`; in raw MCP clients the underlying tool name is `kb.brief`.
2. Use only compact, relevant returned context; do not inject broad memory dumps.
3. After durable findings, decisions, fixes, or reusable procedures emerge, call the finalize tool. In Codex, this may be exposed as `mcp__flux_llm_kb.kb_finalize_turn`; in raw MCP clients the underlying tool name is `kb.finalize_turn`.
4. Make final responses indexable: include concrete decisions, files changed or referenced, commands/tests run, important web/file references, and unresolved gaps.
5. Never store secrets, raw transcripts, private customer data, or unredacted credentials.
6. If retrieval or capture fails, report the failure and continue without fabricating memory.
