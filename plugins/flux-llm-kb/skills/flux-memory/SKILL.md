---
name: flux-memory
description: Use before non-trivial Codex work to retrieve compact Flux-LLM-KB context and after work to store durable, redacted outcomes.
---

# Flux Memory Workflow

In Codex, Flux MCP tools may be exposed as wrappers like `mcp__flux_llm_kb.kb_brief`; in raw MCP clients the underlying names are `kb.brief`, `kb.search`, `kb.remember`, and `kb.finalize_turn`.

| Tool | Use when | Guardrails |
| --- | --- | --- |
| `mcp__flux_llm_kb.kb_brief` | Before non-trivial work, or mid-turn when you need a compact workspace-scoped brief. | Pass the user's current task and active workspace `cwd` when available. |
| `mcp__flux_llm_kb.kb_search` | Mid-turn targeted discovery when local files, the prompt, or current tool output do not answer the question. | Use normal kb.brief/search for broad context. Broad `kb.search`, `kb.brief`, and `kb.explain` exclude code results by default; when broad lookup should return code, pass `filters={"file_kinds":["code"]}` as the only file kind. Use `scope_mode="workspace_boosted"` for prior decisions, unresolved project context, cross-workspace patterns, general indexed documents, previous fixes, or user-referenced history. |
| `mcp__flux_llm_kb.kb_code_search` | Code-specific lookup for symbols, definitions, paths, parser metadata, or implementation evidence. | Prefer `filters={"file_kinds":["code"]}` alone with broad lookup when you need code corpus results, or use `kb.code_search` / `kb.code_symbol_lookup` for symbol-specific lookup. |
| `mcp__flux_llm_kb.kb_remember` | During work for durable atomic saves: verified decisions, fixes, reusable procedures, commands, or project facts that should be retrievable before the turn ends. | Keep each save concise, redacted, and scoped with active workspace `cwd` or `root_name`; never store secrets, raw transcripts, private customer data, or unredacted credentials. |
| `mcp__flux_llm_kb.kb_finalize_turn` | At the end of meaningful work to store a redacted durable summary. | Include concrete outcomes and unresolved gaps, but avoid duplicating every prior `kb_remember` item from the same turn. |

Additional rules:

1. Do not query mid-turn when local files, the prompt, or current tool output already answer the question.
2. Use only compact, relevant returned context; do not inject broad memory dumps.
3. Broad `kb.brief`, `kb.search`, and `kb.explain` exclude code by default. If you want code from a broad lookup, pass `filters={"file_kinds":["code"]}` as the only file kind; mixed code plus non-code file kinds are rejected. For mixed memory and code context, make separate broad non-code and code-specific calls. Free-text code-looking words are not enough.
4. Make final responses indexable: include concrete decisions, files changed or referenced, commands/tests run, important web/file references, and unresolved gaps.
5. If retrieval or capture fails, report the failure and continue without fabricating memory.
