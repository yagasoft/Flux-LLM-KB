---
name: flux-memory
description: Use before non-trivial Codex work to retrieve compact Flux-LLM-KB context and after work to store durable, redacted outcomes.
---

# Flux Memory Workflow

1. Before non-trivial work, call the Flux-LLM-KB MCP brief tool with the user's current task. In Codex, this may be exposed as the wrapper `mcp__flux_llm_kb.kb_brief`; in raw MCP clients the underlying tool name is `kb.brief`.
2. Query mid-turn with `mcp__flux_llm_kb.kb_brief` or `mcp__flux_llm_kb.kb_search` when you need prior decisions, unresolved project context, previous fixes, or user-referenced history.
3. Do not query mid-turn when local files, the prompt, or current tool output already answer the question.
4. Use only compact, relevant returned context; do not inject broad memory dumps.
5. After durable findings, decisions, fixes, or reusable procedures emerge, call the finalize tool. In Codex, this may be exposed as `mcp__flux_llm_kb.kb_finalize_turn`; in raw MCP clients the underlying tool name is `kb.finalize_turn`.
6. Make final responses indexable: include concrete decisions, files changed or referenced, commands/tests run, important web/file references, and unresolved gaps.
7. Never store secrets, raw transcripts, private customer data, or unredacted credentials.
8. If retrieval or capture fails, report the failure and continue without fabricating memory.
