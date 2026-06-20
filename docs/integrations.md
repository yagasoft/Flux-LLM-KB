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
- `kb.mail_status`

## REST

Install API dependencies:

```powershell
python -m pip install -e .[api]
uvicorn flux_llm_kb.rest_api:create_app --factory --host 127.0.0.1 --port 8765
```

Endpoints:

- `GET /api/health`
- `GET /api/settings`
- `GET /api/settings/{key}`
- `PUT /api/settings/{key}`
- `POST /api/settings/apply`
- `POST /api/settings/{key}/reset`
- `GET /api/mail/status`
- `GET /api/mail/profiles`
- `POST /api/mail/profiles`
- `POST /api/mail/sync`
- `POST /api/mail/watch`
- `POST /api/search`
- `POST /api/brief`
- `POST /api/remember`
- `GET /api/audit`
- `POST /api/forget`

## Runtime Settings

Runtime settings are registry-backed and available through CLI and REST. Use the
dashboard settings tab for interactive edits; it shows whether a value comes from
the environment, database, or default registry. Sensitive values are masked.

```powershell
flux-kb settings list
flux-kb settings get retrieval.token_budget
flux-kb settings set retrieval.token_budget 1600
flux-kb settings set embedding.model flux-hash-v2 --confirm
flux-kb settings reset retrieval.token_budget
flux-kb settings apply --component watcher
```

## Mail Capture

IMAP is the preferred ongoing capture path. Configure a Gmail label or IMAP
folder as the capture queue, then export into a private spool that Flux indexes.

```powershell
flux-kb mail profile add-imap `
  --name gmail-capture `
  --account me@gmail.com `
  --server imap.gmail.com `
  --folder FluxCapture `
  --spool private\mail-spool\gmail-capture

flux-kb mail watch run --profile gmail-capture
```

Classic Outlook COM catch-up is scoped to selected folder paths:

```powershell
flux-kb mail profile add-outlook `
  --name outlook-catchup `
  --folder "Mailbox - Me\Inbox\Flux Capture" `
  --spool private\mail-spool\outlook-catchup

flux-kb mail sync --profile outlook-catchup
```

Mailbox credentials, OAuth tokens, raw messages, and attachments stay local and
must remain outside Git.

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
