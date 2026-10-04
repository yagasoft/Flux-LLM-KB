# Native v1 integrations

FluxKnowledge exposes one private, direct-loopback native v1 contract for
Codex and local scripts. All supported clients share the native application facade.

## Boundary

- HTTP and MCP bind only to `http://127.0.0.1:5137`.
- REST routes live under `/api/v1`; MCP is served at `/mcp`.
- The CLI is a thin HTTP client of that same loopback contract. It disables
  redirects and proxies.
- Requests can read only bounded, application-retained projections. Parser
  inputs never accept a source-original path, and every returned value is
  secret-filtered.
- The contract has stable lower-camel-case JSON envelopes and reason codes.
  A response contains `ok`, `result`, `reasonCode`, `message`, `retryable`,
  and, where applicable, an operation identifier.

## Native tools

`corpus.write` action `root_create` accepts `discoveryMode: "git-tracked"` and
`indexSourceText: true`, with optional `includePatterns` and `excludePatterns`
arrays. Configure the repository path once; patterns narrow automatic Git
membership rather than list individual files. Additions enter when added to the
Git index, edits use working-tree bytes, and renames/deletions/untracking converge
through watcher hints and periodic scans. Defaults preserve filesystem sources.

Native REST, MCP, CLI stdin and Codex hook ingress have no aggregate 32 KiB
application cap or rule/file-count cap. Incremental reads reserve estimated
representation memory against runtime headroom. REST/MCP/CLI report retryable
`resource-pressure` before durable mutation; hooks keep their existing invalid-input
response behaviour. JSON depth, field validation,
response bounds and file/parser protections remain. The repository browser form
sends JSON directly over HTTP, avoiding Blazor's separate default 32 KB incoming
message boundary. Deployment preflight observed IIS in-process
`maxAllowedContentLength=30000000`; a host can reject larger bodies before Flux
handles them. Kestrel deployments have their own configured body boundary.
The disposable MCP HTTP transport accepts the tested >32 KiB / 900-rule payload;
unmeasured Codex/client/proxy ceilings remain unverified.

| MCP tool | REST | CLI |
| --- | --- | --- |
| `knowledge.search` | `POST /api/v1/knowledge/search` | `FluxKnowledge.Cli knowledge search` |
| `knowledge.write` | `POST /api/v1/knowledge/actions/preview` or `/commit` | `FluxKnowledge.Cli knowledge write --preview|--commit` |
| `knowledge.graph` | `POST /api/v1/knowledge/graph/query` | `FluxKnowledge.Cli knowledge graph` |
| `code.query` | `POST /api/v1/code/query` | `FluxKnowledge.Cli code query` |
| `code.write` | `POST /api/v1/code/actions/preview` or `/commit` | `FluxKnowledge.Cli code feedback --preview|--commit` |
| `corpus.query` | `POST /api/v1/corpus/query` | `FluxKnowledge.Cli corpus query` |
| `corpus.search` | `POST /api/v1/corpus/search` | `FluxKnowledge.Cli corpus search` |
| `corpus.read` | `POST /api/v1/corpus/read` | `FluxKnowledge.Cli corpus read` |
| `corpus.write` | `POST /api/v1/corpus/actions/preview` or `/commit` | `FluxKnowledge.Cli corpus write --preview|--commit` |
| `operations.status` | `GET /api/v1/operations/status` | `FluxKnowledge.Cli operations status` |
| `operations.audit` | `POST /api/v1/operations/audit/query` | `FluxKnowledge.Cli operations audit` |

The trusted-local `corpus-rebuild` CLI is an operator interface used by
the incremental IIS updater. It accepts `plan --operation <guid>`,
`commit --manifest <path>`, `prepare --operation <guid>`,
`status --operation <guid>` and `finish --operation <guid>`. Its
`verify-models` command checks pinned local files without inference or downloads.
The rebuild requires the updater's owned hold and scheduler drain; these commands
do not grant production authority or replace normal public search/write contracts.

`corpus.write` also supports explicit terminal Publish recovery with
`action: "publication_retry"` and `payload: { "jobId": "<guid>" }` through the same
preview/confirmed commit routes. It only admits one current retained record,
enabled root, terminal failed Publish job, completed unsuccessful delivery and
verified immutable Embed inputs. It requeues the same job/delivery, preserves
failure evidence and attempts, advances both lease generations and restores its
exact text activity for normal publication. Pause, deletion, suppression, live
ownership, competing work, contradictory success or invalid inputs refuse
without scheduling. Confirmation binds current versions and inputs; exact
idempotent replay returns its original receipt even after a later failure.
Another recovery requires a new preview and explicit authority. Deployment
holds and corpus rebuilds fence recovery. Existing `job_retry` still applies to
supported source-scan controls. Production terminal replay needs separate
approval; installing this action does not authorise a replay.

Explicit terminal Embed recovery uses `action: "embedding_retry"` with the same
single `jobId` payload and native preview/confirmed commit routes. It requires a
compatible retained checkpoint, current enabled source, completed unsuccessful
delivery and fully settled, cleanup-confirmed GPU history whose input/result
bindings match saved vectors. It rejects active slots/owners, contradictory
downstream success, rebuilds or changed inputs. Recovery advances both lease
generations while preserving job/delivery keys, attempts, draft and vectors;
normal workers process only missing chunks and then publish. Confirmation binds
current versions and hash commitments, and idempotent replay returns its receipt.
Hold and terminal-processing approval requirements are the same as Publish recovery.

`operations.status` accepts the bounded views `overview`, `sources`, `jobs`,
`workers`, `processors`, and `recovery`. Code, corpus and audit queries use
bounded pages and opaque query-bound cursors.

`corpus.search` searches published retained text across `all`, one registered
`root_id`, or a canonical Windows `cwd` workspace. It returns bounded canonical
passages, source and owner identity, typed locations where retained provenance
supports them, and an opaque `evidence_ref`. `corpus.read` accepts that reference
and returns up to 4,096 UTF-16 units of surrounding retained context while
rechecking current publication and deletion state. Both operations have a
256 KiB response limit. Search responses identify the actual `retrieval_mode` as
`hybrid` or `lexical`, together with semantic status and warnings. The installed
app uses the learned hybrid passage runtime described below.

The `Search:HybridPassagesEnabled` runtime shares coherent passage
retrieval across corpus, existing search and knowledge-source search. Successful
wire shapes and public limits remain unchanged. Corpus responses identify hybrid
or lexical mode, semantic status and warnings. Existing search keeps complete fallback
passages with warnings on each hit, but a degraded empty response becomes an explicit
refusal. Knowledge's list contract cannot carry degradation metadata, so degraded
source retrieval refuses instead of silently returning an incomplete union.
Native REST/MCP/remote CLI share named `search-busy`, `search-timeout`,
`search-unavailable`, `search-index-updating` and `search-rebuilding` failures;
these transient failures are retryable (REST HTTP 503). Input-length and scoped
capacity refusals are non-retryable. `/api/search` exposes equivalent problem
codes. Its numeric score is reciprocal result position, not a relevance probability.
The runtime is active in the installed app and has passed scoped English
staging acceptance across MCP, REST and CLI. The final two-caller holdout
returned 96/96 ready searches and 480/480 exact citation reads at 16.46-second
full REST p95, within the measured 20-second staging target and 25-second
request deadline. This does not establish answer abstention or performance
under sustained OCR, cold CPU warmup or higher concurrency. See the
[live acceptance record](operations/2026-09-28-hybrid-search-live-acceptance.md).

## Mutations

`knowledge.write`, `code.write`, and `corpus.write` are closed allowlists.
They cannot execute arbitrary programs, URLs, database commands or paths.

1. Send the complete command to its `preview` route or tool mode.
2. Keep the returned opaque `confirmationId`; it expires after a short period
   and is bound to the command fingerprint and target row versions.
3. Send the identical command to `commit` with that confirmation and a
   caller-provided idempotency key. REST uses the `Idempotency-Key` header;
   MCP uses `idempotency_key`; CLI uses `--confirmation-id` and
   `--idempotency-key`.

Replaying the same key and request returns the original receipt. A key reused
for a different request conflicts. Existing corpus lease and fencing rules
remain authoritative. A cancelled request creates no mutation before commit;
after a durable commit, retrying the same idempotency key recovers the receipt.

## Codex plugin

The application-owned marketplace is `I:\FluxKnowledge\CodexPlugin`. Its
native manifest references only `http://127.0.0.1:5137/mcp`. The registration
is designed to be idempotent and to replace only the known FluxKnowledge
registration, leaving unrelated Codex configuration unchanged.

`FluxKnowledge.Cli codex plugin status` is the normal non-mutating diagnostic.
`FluxKnowledge.Cli codex plugin repair` requires a typed, separately authorised
go-live authority; ordinary CLI composition denies it. Normal application
startup does not register, repair or otherwise alter Codex plugin state.

### Automatic prompt context

`UserPromptSubmit` reads the client's `cwd` and uses only published retained
passages beneath a registered Windows workspace. It uses the model-free lexical
corpus path, makes one search and at most five zero-context citation reads, and
injects at most three complete JSON records within 4,096 UTF-16 units. Every
record includes an opaque `evidence_ref`, source identity, revision and exact
passage. The preamble labels excerpts as untrusted, potentially incomplete
source data. A missing or unavailable workspace, a vague prompt or unsuitable
evidence produces only `{"continue":true}`; it never searches all sources or
falls back to unscoped saved notes. Explicit `knowledge.search` and
`corpus.search` keep their wider contracts and hybrid behaviour.

The conservative `workspace-lexical-v1` selector requires two meaningful prompt
terms or an exact identifier and checks the passage body, not its title or
header. It can omit useful paraphrases and short follow-ups. The hook has a
1,750 ms cooperative retrieval budget inside a two-second overall budget;
`Stop`, `PreCompact` and the command adapter's ten-second transport backstop
are unchanged. `Codex:PromptContextEnabled=false` suppresses automatic context
without disabling explicit tools or other hooks. The operator Events projection
records only a closed reason, policy version, counts and elapsed milliseconds,
never the prompt, workspace path, passage or reference. Unexpected prompt
failures omit exception text. See the [design](design/workspace-codex-context.md).

This behaviour was deployed to the installed IIS app on 30 September 2026 after
independent operational review. Live loopback and installed-adapter checks
verified exact citations and empty context for unregistered workspaces; an
actual Codex CLI prompt from the public acceptance workspace recorded one
injected context event. See the [design](design/workspace-codex-context.md) for
release and validation evidence.

## On-demand workspace brief

The personal `flux-workspace-brief` Codex skill combines the existing
`corpus.search` and `corpus.read` tools into a cited snapshot of current state,
decisions, open issues and next actions. Invoke `$flux-workspace-brief` or ask
for a cited workspace brief, optionally supplying a directory and focus. The
repository source is [SKILL.md](../scripts/codex/skills/flux-workspace-brief/SKILL.md); personal
installation is described in [setup](setup.md#personal-workspace-brief-skill).

Each brief stays within the exact requested workspace, with at most three
searches at limit five and eight reads at 1,024 context characters. Claims need
successful readback and source/revision/location/reference details. Unavailable
scope ends the KB brief; conflicts, partial evidence and material retrieval
warnings are reported as gaps. Documented actions and proposed recommendations
are distinguished. This is retained-document evidence, not live health.

The workflow adds no native server tool or explicit knowledge write. Its budgets
are model instructions; the existing server scope/disclosure checks remain
authoritative. The existing Stop hook retains its usual capture behaviour.

## Operational actions and planned extensions

External listeners and remote MCP are outside this private local contract.
Deployment, migration, VSS configuration and plugin lifecycle changes are
operational actions governed by [setup](setup.md), rather than normal application
startup or development verification.

The native plugin is verified by its exact identity and installed/enabled state.
Unrelated plugin registrations are not removed. The application has no plugin
retirement or migration task.

The [retrieval design](design/corpus-retrieval.md) records the scoped lexical
contract history. The delivered [hybrid passage design](design/hybrid-passage-retrieval.md)
defines the shared learned retrieval path; the deterministic embedding provider
remains the non-hybrid baseline.
