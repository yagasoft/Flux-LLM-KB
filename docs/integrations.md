# Integrations

Flux-LLM-KB exposes the same memory kernel through CLI, MCP, REST, and Codex
hooks.

## MCP

Install optional MCP dependencies:

```powershell
python -m pip install -e .[mcp]
python -m flux_llm_kb.mcp_server
```

Tools:

- `kb.brief`
- `kb.search`
- `kb.remember`
- `kb.finalize_turn`
- `kb.audit`
- `kb.forget`
- `kb.status`

## REST

Install API dependencies:

```powershell
python -m pip install -e .[api]
uvicorn flux_llm_kb.rest_api:create_app --factory --host 127.0.0.1 --port 8765
```

Endpoints:

- `GET /api/health`
- `POST /api/search`
- `POST /api/brief`
- `POST /api/remember`
- `GET /api/audit`
- `POST /api/forget`

## Codex Plugin

The personal plugin scaffold lives in `plugins/flux-llm-kb`.

The hook scripts call:

```powershell
python -m flux_llm_kb.cli hook user-prompt-submit
python -m flux_llm_kb.cli hook pre-compact
python -m flux_llm_kb.cli hook stop
```

Set `FLUX_KB_PYTHON` if Codex should use a specific Python executable:

```powershell
$env:FLUX_KB_PYTHON = "C:\Path\To\python.exe"
```

The current hooks emit compact context instructions. The service layer already
supports durable capture and retrieval, so the next step is wiring the hook
payloads to call `kb.brief` and `kb.finalize_turn` automatically once the Codex
hook runtime contract is finalized.
