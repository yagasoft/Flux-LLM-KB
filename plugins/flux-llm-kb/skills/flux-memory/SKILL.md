---
name: flux-memory
description: Use before non-trivial Codex work to retrieve compact Flux-LLM-KB context and after work to store durable, redacted outcomes.
---

# Flux Memory Workflow

1. Before non-trivial work, call the Flux-LLM-KB MCP `kb.brief` tool with the user's current task.
2. Use only compact, relevant returned context; do not inject broad memory dumps.
3. After durable findings, decisions, fixes, or reusable procedures emerge, call `kb.finalize_turn`.
4. Never store secrets, raw transcripts, private customer data, or unredacted credentials.
5. If retrieval or capture fails, report the failure and continue without fabricating memory.

