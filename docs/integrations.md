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

| Tool | Purpose |
| --- | --- |
| `kb.search` | Search Flux memory and corpus evidence, optionally scoped and filtered by workspace/root, evidence kind, lifecycle, or current-state policy. |
| `kb.explain` | Search with query-aware snippets, ranking signals, filters, suppression metadata, and brief-packing rationale. |
| `kb.brief` | Build a compact task brief for non-trivial work. |
| `kb.remember` | Store a concise redacted durable atomic save with optional workspace provenance. |
| `kb.finalize_turn` | Store a redacted end-of-turn summary for meaningful agent work. |
| `kb.claim_upsert` | Create or update an atomic claim. |
| `kb.claim_transition` | Move a claim through lifecycle states with audit-visible rationale. |
| `kb.graph_traverse` | Traverse typed knowledge graph relations from an entity. |
| `kb.capture_review` | List pending capture-review jobs without raw capture payloads. |
| `kb.capture_review_decide` | Approve or reject a capture-review job with rationale. |
| `kb.retention_policies` | List retention policies for claims, episodes, and corpus assets. |
| `kb.retention_quality` | Report retention and memory-quality candidates without raw content. |
| `kb.semantic_duplicates_refresh` | Refresh advisory semantic duplicate clusters for corpus chunks, episodes, or claims. |
| `kb.semantic_duplicates_list` | List active semantic duplicate clusters without raw suppressed content. |
| `kb.acceleration_status` | Return local capability, cache layout, and worker-family queue telemetry. |
| `kb.audit` | List recent audit events. |
| `kb.forget` | Forget a memory item by id with an audit reason. |
| `kb.status` | Return Flux health and runtime status. |
| `kb.crawl_status` | Return corpus crawler, watcher, job, and retrieval status. |
| `kb.crawl_sync` | Sync monitored corpus roots or paths, optionally as a dry run. |
| `kb.crawl_watch_status` | List watched roots and watcher runtime state. |
| `kb.crawl_watch_enable` | Enable filesystem watching for one root or all roots. |
| `kb.crawl_watch_disable` | Disable filesystem watching for one root or all roots. |
| `kb.crawl_jobs` | List recent corpus extraction and capture jobs. |
| `kb.mail_status` | Return mail ingestion, OAuth, profile, and scheduler status. |

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
- `GET /api/acceleration/status`
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
- `POST /api/explain`
- `GET /api/explain?query=<q>&limit=<n>&token_budget=<n>`
- `GET /api/claims?review=<all|needs_review|current>&state=<state>&q=<q>&limit=<n>`
- `POST /api/claims`
- `GET /api/claims/{claim_id}`
- `POST /api/claims/{claim_id}/transitions`
- `GET /api/graph/traverse?entity_id=<id>&relation_type=<type>&max_depth=<n>`
- `GET /api/capture/review?limit=<n>`
- `POST /api/capture/review/{job_id}/decision`
- `POST /api/semantic-duplicates/refresh`
- `GET /api/semantic-duplicates?memory_class=<corpus|episode|claim>&root_name=<name>&limit=<n>`
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
  `GET /api/brief?query=customer%20RFP&token_budget=1200`. Use
  `GET /api/explain?query=customer%20RFP&limit=5` when a consumer needs snippets,
  ranking signals, filters, suppression metadata, and the brief-packing trace.
- MCP for agent runtimes: `kb.search`/`kb.explain`/`kb.brief` in raw MCP clients, or Codex
  wrapper names such as `mcp__flux_llm_kb.kb_search` and
  `mcp__flux_llm_kb.kb_explain` and `mcp__flux_llm_kb.kb_brief`.
- CLI for local shell automation: `flux-kb search "customer RFP" --limit 5` or
  `flux-kb explain "customer RFP" --limit 5`.

Search, explain, and brief reads accept optional `cwd`, `root_name`, and `scope_mode`
parameters. They also accept per-query retrieval filters without changing global
settings: `logical_kinds` (`episode`, `file`, `mail`), `current_only`,
`lifecycle_states`, and `include_suppressed`. REST POST bodies use a `filters`
object; REST GET accepts `kind`, `current_only`, `lifecycle_state`, and
`include_suppressed` query parameters; MCP tools accept an optional `filters`
object; CLI search/explain use `--kind`, `--current-only`,
`--lifecycle-state`, and `--include-suppressed`.
`scope_mode=local_first` is the default: Flux searches matching
workspace/root evidence first, then falls back to global memory only when local
results have no lexical or fuzzy evidence. Use `local_only` to forbid global
fallback, or `global` for deliberate cross-workspace retrieval. Explicit
mid-turn searches can use `scope_mode=workspace_boosted` to blend local
workspace/root evidence with strong cross-workspace or general indexed evidence
while suppressing weak trust-only global matches. Briefing should keep the
default `local_first` mode unless the caller intentionally requests a broader
scope.

Memory writes accept optional `cwd` and `root_name` as workspace provenance.
Pass the active workspace `cwd` when calling `kb.remember`,
`kb.finalize_turn`, `/api/remember`, or `flux-kb remember`; the CLI defaults
manual remembers to its current directory. Use `kb.remember` for concise
redacted durable atomic saves when a verified decision, fix, reusable
procedure, command, or project fact should be retrievable before the turn ends.
Use `kb.finalize_turn` at the end of meaningful work for the turn summary, and
avoid duplicating every prior `kb.remember` item. Explicit repair of older
unscoped episodes is available through `flux-kb episodes scope-backfill --cwd
<path> --id <episode-id> [--dry-run]`; it only updates caller-selected IDs.

Claim lifecycle and graph primitives are available through the same surfaces for
kernel-level automation:

```powershell
flux-kb claim upsert --subject-type project --subject Flux --predicate uses --object PostgreSQL --confidence 0.8
flux-kb claim transition <claim-id> confirm --reason "verified"
flux-kb graph traverse <entity-id> --relation-type depends_on --max-depth 2
flux-kb capture review list --limit 50
flux-kb capture review decide <job-id> --decision approve --rationale "Verified metadata and source."
flux-kb semantic-duplicates refresh --memory-class all --limit 1000
flux-kb semantic-duplicates list --memory-class corpus --limit 50
flux-kb acceleration status
```

Lifecycle transitions append audit-visible events. Superseded, contradicted,
stale, and retired claims remain available for review but normal brief packing
prefers current evidence. `include_suppressed` returns sanitized counts, paths,
canonical identifiers, and reasons for exact duplicate, same-document version,
and semantic near-duplicate suppression; it does not return raw suppressed
content. Semantic duplicate clusters are advisory metadata only and do not delete
or rewrite source assets, episodes, or claims.

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

Acceleration settings define the permanent cache root, localhost-only local
model probing, and per-family worker caps. Local inference probing is disabled
by default and rejects non-loopback URLs. The read-only acceleration status is
available through `flux-kb acceleration status`, `GET /api/acceleration/status`,
`kb.acceleration_status`, and the dashboard Health tab.

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
  --spool private\mail-spool\gmail-capture `
  --post-process remove_label `
  --processed-folder FluxProcessed

flux-kb mail oauth gmail start `
  --profile gmail-capture `
  --client-config private\google-oauth-client.json

flux-kb mail oauth status --profile gmail-capture
flux-kb mail post-process dry-run --profile gmail-capture --limit 5
flux-kb mail post-process events --profile gmail-capture --limit 20
flux-kb mail watch run --profile gmail-capture
```

Open the returned authorization URL, approve the local desktop app, and let the
loopback callback complete through the local dashboard/API. Flux stores the
refresh token locally, masks it in all responses, and refreshes short-lived
access tokens before XOAUTH2 IMAP login.

Mail post-processing is policy-driven per profile:

- `none`: export only and leave the message in place.
- `remove_label`: Gmail-only; remove the capture label with Gmail IMAP label
  commands.
- `move_to_processed`: Gmail adds the processed label and removes the capture
  label; generic IMAP copies to the processed folder, marks the source deleted,
  and expunges.
- `trash`: confirmation-gated; Gmail applies Trash semantics and generic IMAP
  copies to `trash_folder` when configured, then deletes and expunges the
  source message.

Use `flux-kb mail post-process dry-run` or
`POST /api/mail/profiles/{profile}/post-process/dry-run` before enabling a new
policy. Recent outcomes are available through
`flux-kb mail post-process events` and
`GET /api/mail/post-process/events?profile_name=<name>`. Audit views include
profile, provider, policy, action, status, command metadata, and errors, but not
raw mail bodies.

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
  `kb.remember`, `kb.finalize_turn`, or as Codex wrappers such as
  `mcp__flux_llm_kb.kb_brief`, `mcp__flux_llm_kb.kb_search`, and
  `mcp__flux_llm_kb.kb_remember`, and
  `mcp__flux_llm_kb.kb_finalize_turn`. Models may query mid-turn when they need
  prior decisions, unresolved project context, patterns from other workspaces,
  general indexed documents, previous fixes, or user-referenced history. Use
  `kb.brief` for compact workspace-scoped context and `kb.search` with
  `scope_mode=workspace_boosted` for expanded discovery; skip KB retrieval when
  local files, the prompt, or current tool output already answer the question.
  Use `kb.remember` for concise redacted durable atomic saves during work, with
  active `cwd` or `root_name` provenance, and use `kb.finalize_turn` for the
  end-of-turn summary without repeating every mid-turn save.
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

`flux-kb codex status` also checks the Codex plugin discovery cache under
`~/.codex/plugins/cache`. A cache entry is considered discoverable only when the
cached Flux manifest, skills, hooks, and scripts match the installed plugin
source. If the cache is stale, status reports `ready_restart_required` with a
stale-cache message instead of `ready`. Running `flux-kb codex install-plugin`
safely invalidates stale Flux-owned cache directories and leaves unrelated
plugin caches untouched; restart Codex Desktop afterward so it rebuilds the
cache from the current plugin source.

For an end-to-end Codex smoke test, verify that at least the status, brief, and
finalize tools are callable through either naming form. A successful test should
call `kb.status`/`mcp__flux_llm_kb.kb_status`, call
`kb.brief`/`mcp__flux_llm_kb.kb_brief` with a harmless smoke-test task, and
store only a redacted outcome through
`kb.finalize_turn`/`mcp__flux_llm_kb.kb_finalize_turn`, passing the active
workspace `cwd` so the saved memory remains locally retrievable.

Codex hooks run a configurable local policy by default:

- `UserPromptSubmit` skips empty, short, slash-command, and trivial prompts; for
  non-trivial prompts it injects guidance for indexable final responses and
  retrieves a compact workspace-scoped Flux brief when search results include
  lexical or fuzzy evidence. If only global fallback evidence is available, the
  injected context is labeled as global fallback memory and audited as such.
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
