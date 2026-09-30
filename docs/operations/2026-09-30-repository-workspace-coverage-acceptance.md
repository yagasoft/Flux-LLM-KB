# Automatic repository coverage acceptance

Date: 30 September 2026. Initial deployment and main-repository registration succeeded; the source is paused during correction of live publication blockers. Live retrieval/freshness acceptance remains incomplete.

## Delivered locally

One `git-tracked` source discovers eligible Git index members and retains their working-tree bytes. The configuration contains optional narrowing rules, not individual filenames. The existing watcher coordinator and periodic scans handle additions, edits, renames, deletion, untracking and a stable empty inventory. Source configuration stays unchanged during those transitions.

Searchable source text covers C#, PowerShell/scripts/modules, Python, JavaScript, SQL, Razor, CSS and project/props/solution XML alongside documentation. C# retains its separate syntax-only symbols/references route. The intentional text descriptor is validated against durable root/revision evidence; mismatched descriptors, capabilities and older text routes cannot bypass admission.

Git scans have serialised root ownership, renewed leases and transactional configuration/lease checks at convergence, missing-path reconciliation and completion. Incomplete or unreadable inventories preserve prior visibility. Local Git helpers and inherited Git configuration overrides cannot select another inventory or execute a repository helper. No models were acquired; verification used synthetic data and generated disposable SQL databases.

Private untracked files and tracked private/build/runtime/model sentinels remain excluded before retention. Authored `src/**/Models/*.cs` remains eligible source. Existing file-size, UTF-8, parser, response, content-disclosure and scope protections remain.

The aggregate 32 KiB native request guard is removed without another fixed byte/count ceiling. Incremental ingress uses cancellation and resource-aware reservations, and validates commands before mutation. Repository form fields travel directly over HTTP. Preview changes invalidate confirmation; retries use the same command and idempotency key.

## Observed verification

| Check | Observed evidence |
| --- | --- |
| Release build | Zero warnings and errors with `-warnaserror`. |
| Initial feature closeout | All 21 required script steps passed; full suites in worktree and main each passed 2,761 tests with zero failures and 20 opt-in skips: 18 browser cases and two offline model probes. Affected browser checks ran separately; skipped real-model probes are not new-feature requirements. Squash merge and push succeeded. |
| Automatic discovery without a count cap | One disposable repository returned all 601 eligible files, including authored `Models` code, without an include list. Mandatory exclusions and untracked sentinels were accounted for. |
| Large input | A valid >32 KiB configuration with 900 optional rules passed native SQL preview/commit/persistence/replay, CLI request preservation, REST admission and real HTTP MCP transport. Live preview-only checks also passed REST (59,570 bytes), direct MCP (59,679 bytes) and CLI with the same fingerprint and no source-policy change. Escaped multibyte input, malformed suffixes, duplicate command properties, cancellation and competing resource reservations were checked. Fixture sizes are examples, not production maxima. |
| Observable pipeline | Real Git discovery, durable retention/dispatch, synthetic extraction/index publication, cited code/docs reads, structured C# symbols/references and native symbol matching were exercised. Every added source-text format was included. |
| Automatic freshness | Watcher hints picked up tracked additions and working-tree edits; periodic-only scans handled rename, deletion, untracking and an empty inventory. Replaced evidence became stale; removed content disappeared from search. |
| Ownership and recovery | Expired/superseded/configuration-stale scans were refused; concurrent root claims were serialised; healthy scans renewed past their initial expiry; renewal loss cancelled work. Pause prevented claims/renewal/completion, preserved Git mode and allowed compatible resume. |
| C# coexistence | The final combined SQL run passed 51/51 with no skips, including text-first/code-first/replay, concurrent promotion/reconciliation, native symbol matching and mismatched-route refusal. Paused/suppressed promotion preserved its holding reason. |
| Sources browser | All three affected browser checks passed, including the existing add-folder flow and the large repository configuration over direct HTTP with real MCP transport. |
| Repository contract | Link/layout/document contract and diff whitespace checks passed; no unrelated AGENTS or manual changes. |
| Independent review | Complete initial code/documentation review and the operational release review approved their respective gates with no blockers. The user subsequently approved deployment/validation. Corrective code receives focused checks and independent review before its required closeout/release. |

Fresh local logs are retained outside public Git under the task's ignored run-log directory. Earlier failed runs exposed missing expiry checks, renewal requirements and fixture/UI issues; corrections received focused checks rather than weakening affected invariants.

## Transport and operational limits

Read-only deployment inspection found IIS in-process hosting with effective `maxAllowedContentLength=30000000`. This remains a hosting restriction before Flux handling. Kestrel hosts have their own body settings. The new repository form avoids Blazor's separate default 32 KB incoming-message boundary. The tested HTTP MCP server accepts the 900-rule request; actual Codex/client/proxy maximums remain unmeasured. Runtime memory, cancellation, timeouts, disk capacity and unchanged per-file/parser limits still constrain operation.

No schema or migration file changed. No model-store modification, installed-plugin change, AGENTS edit or dashboard-manual regeneration occurred. Local coverage does not establish complete live searchable coverage.

## Initial live release and observed blockers

The authorised incremental update deployed commit `5926a24c29ca6c401c85c3c9de801618756a1faf` in release `20260930T143038Z-5926a24c29ca`. The installed Web DLL matched the reviewed candidate hash; live, readiness and index probes returned HTTP 200 with index state Healthy. No migration/index rebuild was used; application and interactive-host recovery payloads were retained.

One Git-tracked `E:\LLM KB` source was registered with code text enabled, empty optional patterns, no links, the standard 900-second cadence and unchanged 16 MiB per-file protection. The first scan completed in approximately 49 seconds, discovering/retaining 1,028 files with complete enumeration and no reported enumeration errors. It included 879 C# files and 37 Markdown files. Source admission and retained bytes demonstrate application-pool access; scan completion does not prove publication.

At the blocker checkpoint, five text pipeline records had completed, while C# branches had not been created. All 879 C# holding activities named `csharp-code-writer-not-ready`. Read-only inspection and a generated-SQL reproduction proved that the existing migration's exact scripted `CREATE`/CRLF blocked-diagnostic trigger representation was rejected by the readiness hash allowlist. Separately, repeated embedding admission exceptions occurred while the expected physical slot was legitimately reserved by another batch. The source was paused through supported native controls; retained state and recovery were preserved.

Narrow corrections accept only the additional exact migration-derived trigger hash under the existing migration/name checks, and return Busy within the existing admission transaction only for a valid reservation with a matching resident owner/batch and no competing active batch. Missing, uncertain or inconsistent capacity still fails closed. Disposable red runs reproduced both failures; the subsequent combined GPU, C#, repository and readiness regression run passed 89/89 without skips. Corrective full closeout/release and resumed live acceptance remain pending. No production SQL alteration, lease clearing, dead-letter processing or model acquisition was performed.

## Release and live gates

Use the repository's required feature-closeout script with `-KeepWorktree` and without `-GoLive`. Prepare the exact integrated revision through incremental IIS `-PlanOnly`, current health/index/transport evidence and retained recovery payload. Production apply requires explicit user authority and independent operational review.

Before an application downgrade, quiesce workers, pause/disable every Git root and drain/fence its claims. Older binaries ignore the discovery mode; they must never run against an enabled Git root. Keep these roots paused until compatible code returns. The disposable pause/renewal/resume check proves the control sequence; it is not a production rollback.

After the corrective release, resume the existing registered main-repository root; do not create another root or filename list. Observe discovered/retained/published/structured/withheld/failed counts, then verify scope, citations, symbols and genuine tracked documentation refresh through MCP, REST and CLI. Expected-answer briefs, complete live publication counts and final health remain pending. Deferred OCR, Outlook desktop and wider lifecycle operational acceptance remain separate.
