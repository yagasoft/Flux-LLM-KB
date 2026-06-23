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
- `kb.claim_upsert`
- `kb.claim_transition`
- `kb.graph_traverse`
- `kb.finalize_turn`
- `kb.audit`
- `kb.forget`
- `kb.status`
- `kb.mail_status`

Codex may expose these tools through MCP wrapper names rather than literal
top-level `kb.*` names. For example, `kb.status`, `kb.brief`, and
`kb.finalize_turn` can appear as `mcp__flux_llm_kb.kb_status`,
`mcp__flux_llm_kb.kb_brief`, and
`mcp__flux_llm_kb.kb_finalize_turn`. Treat either naming form as the same Flux
MCP surface.

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
- `POST /api/mail/oauth/gmail/start`
- `GET /api/mail/oauth/gmail/callback`
- `GET /api/mail/oauth/status`
- `GET /api/outlook-host/status`
- `POST /api/outlook-host/request-sync`
- `POST /api/outlook-host/profiles/{name}/enable`
- `POST /api/outlook-host/profiles/{name}/disable`
- `GET /api/host/status`
- `POST /api/host/browse-folder`
- `POST /api/host/validate-path`
- `POST /api/search`
- `GET /api/search?query=<q>&limit=<n>`
- `POST /api/brief`
- `GET /api/brief?query=<q>&token_budget=<n>`
- `GET /api/claims?review=<all|needs_review|current>&state=<state>&q=<q>&limit=<n>`
- `POST /api/claims`
- `GET /api/claims/{claim_id}`
- `POST /api/claims/{claim_id}/transitions`
- `GET /api/graph/traverse?entity_id=<id>&relation_type=<type>&max_depth=<n>`
- `GET /api/capture/review?limit=<n>`
- `POST /api/capture/review/{job_id}/decision`
- `GET /api/corpus/assets`
- `GET /api/corpus/assets/{asset_id}`
- `GET /api/corpus/chunks/{chunk_id}`
- `POST /api/remember`
- `GET /api/audit`
- `POST /api/forget`

## Consumer Access

External consumers should use one of three read paths:

- REST for simple tools and scripts:
  `GET /api/search?query=customer%20RFP&limit=5` or
  `GET /api/brief?query=customer%20RFP&token_budget=1200`.
- MCP for agent runtimes: `kb.search`/`kb.brief` in raw MCP clients, or Codex
  wrapper names such as `mcp__flux_llm_kb.kb_search` and
  `mcp__flux_llm_kb.kb_brief`.
- CLI for local shell automation: `flux-kb search "customer RFP" --limit 5`.

Claim lifecycle and graph primitives are available through the same surfaces for
kernel-level automation:

```powershell
flux-kb claim upsert --subject-type project --subject Flux --predicate uses --object PostgreSQL --confidence 0.8
flux-kb claim transition <claim-id> confirm --reason "verified"
flux-kb graph traverse <entity-id> --relation-type depends_on --max-depth 2
flux-kb capture review list --limit 50
flux-kb capture review decide <job-id> --decision approve --rationale "Verified metadata and source."
```

Lifecycle transitions append audit-visible events. Superseded, contradicted,
stale, and retired claims remain available for review but normal brief packing
prefers current evidence.

The dashboard Review tab uses `GET /api/claims` and `GET /api/graph/traverse`
to browse lifecycle review work and selected-entity graph edges. The
`needs_review` filter includes stale, contradicted, superseded, and retired
claims, plus claims with non-`keep` retention actions. `GET /api/capture/review`
returns pending capture-review job metadata only. Operators can approve or reject
pending review jobs with `POST /api/capture/review/{job_id}/decision` and a
required `rationale`; decisions update job status, store `payload.review`, keep
raw capture payload fields out of responses, and append audit-visible
`capture.review_approved` or `capture.review_rejected` events. Approved Codex
backfill ingestion remains future work.

Lookup endpoints are read-only and return stable JSON payloads for asset and
chunk inspection. The API binds to `127.0.0.1` by default; do not expose it to a
network interface without an explicit local access-control policy.

## Gmail OAuth

The default Gmail OAuth redirect URI is `http://127.0.0.1:8765`. Google returns
the authorization code to the Flux root route, which completes setup and shows a
small local result page. Keep the dashboard/API running before starting consent.

The explicit `GET /api/mail/oauth/gmail/callback` endpoint remains available for
custom clients or manually configured redirect URIs, but Flux will not silently
reuse a generic `http://localhost` redirect from a downloaded Google client JSON
because another local service, such as IIS, may already own that URL.

## Runtime Settings

Runtime settings are settings catalog-backed and available through CLI and REST.
Use the dashboard settings tab for interactive edits; it shows whether a value
comes from the environment, database, or catalog default. Sensitive values are
masked. This is cross-platform application configuration, not the Windows
Registry.

```powershell
flux-kb settings list
flux-kb settings get retrieval.token_budget
flux-kb settings set retrieval.token_budget 1600
flux-kb settings set embedding.model flux-hash-v2 --confirm
flux-kb settings reset retrieval.token_budget
flux-kb settings apply --component watcher
```

Crawler glob settings are global defaults. Monitored roots can inherit, extend,
or override them; effective globs are returned in dashboard crawl payloads.

## Host Filesystem Agent

Use the host agent when the dashboard/API is Docker-hosted but watched paths live
on the host filesystem:

```powershell
flux-kb host-agent status
flux-kb host-agent run
```

The agent exposes local-only status, path validation, native folder browse, and
host-side crawl sync endpoints. It stores no private content in Git.

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

flux-kb mail oauth gmail start `
  --profile gmail-capture `
  --client-config private\google-oauth-client.json

flux-kb mail oauth status --profile gmail-capture
flux-kb mail watch run --profile gmail-capture
```

Open the returned authorization URL, approve the local desktop app, and let the
loopback callback complete through the local dashboard/API. Flux stores the
refresh token locally, masks it in all responses, and refreshes short-lived
access tokens before XOAUTH2 IMAP login.

Classic Outlook COM catch-up is scoped to selected folder paths:

```powershell
flux-kb mail profile add-outlook `
  --name outlook-catchup `
  --folder "Mailbox - Me\Inbox\Flux Capture" `
  --spool private\mail-spool\outlook-catchup

flux-kb outlook-host sync --profile outlook-catchup
flux-kb outlook-host run
```

`flux-kb mail sync --profile <outlook-profile>` does not attempt COM from the
Docker-hosted worker. It reports that the Windows Outlook host is required. Run
`flux-kb outlook-host run` in the logged-in Windows session for scheduled pulls,
or queue a one-off request with `flux-kb outlook-host sync --profile <name>`.

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

Codex has three Flux integration surfaces:

- Plugin hooks and skills provide automatic context/capture behavior and user
  guidance inside Codex turns.
- MCP tools provide callable Flux tools when `[mcp_servers.flux_llm_kb]` is
  present in `~/.codex/config.toml`. Depending on Codex tool discovery, they may
  appear as raw MCP names such as `kb.brief`, `kb.search`, and
  `kb.finalize_turn`, or as Codex wrappers such as
  `mcp__flux_llm_kb.kb_brief`, `mcp__flux_llm_kb.kb_search`, and
  `mcp__flux_llm_kb.kb_finalize_turn`.
- REST remains the fallback surface for tools that can call the local API
  directly, for example `GET /api/brief?query=...`.

`flux-kb codex install-plugin` installs the plugin and writes the Flux MCP
server config block:

```toml
[mcp_servers.flux_llm_kb]
command = "<Flux Python>"
args = ["-m", "flux_llm_kb.mcp_server"]
cwd = "<Flux app root>"
enabled = true
startup_timeout_sec = 15
tool_timeout_sec = 60
```

The command prefers `FLUX_KB_PYTHON`, then the production app virtual
environment when available, then the active Python. `flux-kb codex status` and
dashboard health report whether this MCP block is configured, enabled, and able
to import the optional MCP dependency.

For an end-to-end Codex smoke test, verify that at least the status, brief, and
finalize tools are callable through either naming form. A successful test should
call `kb.status`/`mcp__flux_llm_kb.kb_status`, call
`kb.brief`/`mcp__flux_llm_kb.kb_brief` with a harmless smoke-test task, and
store only a redacted outcome through
`kb.finalize_turn`/`mcp__flux_llm_kb.kb_finalize_turn`.

Codex hooks run a configurable local policy by default:

- `UserPromptSubmit` skips empty, short, slash-command, and trivial prompts; for
  non-trivial prompts it injects guidance for indexable final responses and
  retrieves a compact Flux brief when search results include lexical or fuzzy
  evidence.
- `Stop` captures the final assistant message once per `session_id` and
  `turn_id`, subject to the global `capture.enabled` setting and Codex hook
  capture limits. It can also index bounded public web references and file
  references that already belong to enabled monitored roots.
- `PreCompact` remains non-blocking and does not parse transcript files because
  Codex transcript paths are not a stable hook contract.

Hook failures never block Codex. They return a warning and continue. Audit
events use the `codex_hook.*` prefix, and dashboard health shows hook policy
state plus recent hook events.

Runtime settings:

```powershell
flux-kb settings get codex.hooks.enabled
flux-kb settings set codex.hooks.preflight_enabled false
flux-kb settings set codex.hooks.capture_enabled false
flux-kb settings set codex.hooks.capture_guidance_enabled true
flux-kb settings set codex.hooks.reference_indexing_enabled true
flux-kb settings set codex.hooks.reference_max_count 5
flux-kb settings set codex.hooks.reference_max_bytes 1048576
flux-kb settings set codex.hooks.reference_fetch_timeout_seconds 3
flux-kb settings set codex.hooks.reference_allow_private_urls false
flux-kb settings set codex.hooks.token_budget 900
flux-kb settings set codex.hooks.min_prompt_chars 32
flux-kb settings set codex.hooks.capture_min_chars 160
flux-kb settings set codex.hooks.capture_max_chars 8000
```
