# Automatic repository coverage acceptance

Date: 30 September 2026. Local implementation and verification record; deployment, main-repository registration and live retrieval acceptance remain pending.

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
| Full Release suite before the final two order/replay cases | 2,759 passed, zero failed, 20 opt-in skips: 18 browser cases and two offline model probes. Affected browser checks run separately; skipped real-model probes are not new-feature requirements. |
| Automatic discovery without a count cap | One disposable repository returned all 601 eligible files, including authored `Models` code, without an include list. Mandatory exclusions and untracked sentinels were accounted for. |
| Large input | A valid >32 KiB configuration with 900 optional rules passed native SQL preview/commit/persistence/replay, CLI request preservation, REST admission and real HTTP MCP transport. Escaped multibyte input, malformed suffixes, duplicate command properties, cancellation and competing resource reservations were checked. Fixture sizes are examples, not production maxima. |
| Observable pipeline | Real Git discovery, durable retention/dispatch, synthetic extraction/index publication, cited code/docs reads, structured C# symbols/references and native symbol matching were exercised. Every added source-text format was included. |
| Automatic freshness | Watcher hints picked up tracked additions and working-tree edits; periodic-only scans handled rename, deletion, untracking and an empty inventory. Replaced evidence became stale; removed content disappeared from search. |
| Ownership and recovery | Expired/superseded/configuration-stale scans were refused; concurrent root claims were serialised; healthy scans renewed past their initial expiry; renewal loss cancelled work. Pause prevented claims/renewal/completion, preserved Git mode and allowed compatible resume. |
| C# coexistence | The final combined SQL run passed 51/51 with no skips, including text-first/code-first/replay, concurrent promotion/reconciliation, native symbol matching and mismatched-route refusal. Paused/suppressed promotion preserved its holding reason. |
| Sources browser | All three affected browser checks passed, including the existing add-folder flow and the large repository configuration over direct HTTP with real MCP transport. |
| Repository contract | Link/layout/document contract and diff whitespace checks passed; no unrelated AGENTS or manual changes. |
| Independent review | The complete code/documentation review approved technical closeout with no remaining blockers. Required full-suite closeout remains the integration gate; review does not authorise production actions. |

Fresh local logs are retained outside public Git under the task's ignored run-log directory. Earlier failed runs exposed missing expiry checks, renewal requirements and fixture/UI issues; corrections received focused checks rather than weakening affected invariants.

## Transport and operational limits

Read-only deployment inspection found IIS in-process hosting with effective `maxAllowedContentLength=30000000`. This remains a hosting restriction before Flux handling. Kestrel hosts have their own body settings. The new repository form avoids Blazor's separate default 32 KB incoming-message boundary. The tested HTTP MCP server accepts the 900-rule request; actual Codex/client/proxy maximums remain unmeasured. Runtime memory, cancellation, timeouts, disk capacity and unchanged per-file/parser limits still constrain operation.

No schema or migration file changed. No application release, live source registration, model-store modification, installed-plugin change, AGENTS edit or dashboard-manual regeneration occurred during local implementation. Local coverage does not establish that `E:\LLM KB` is already searchable.

## Release and live gates

Use the repository's required feature-closeout script with `-KeepWorktree` and without `-GoLive`. Prepare the exact integrated revision through incremental IIS `-PlanOnly`, current health/index/transport evidence and retained recovery payload. Production apply requires explicit user authority and independent operational review.

Before an application downgrade, quiesce workers, pause/disable every Git root and drain/fence its claims. Older binaries ignore the discovery mode; they must never run against an enabled Git root. Keep these roots paused until compatible code returns. The disposable pause/renewal/resume check proves the control sequence; it is not a production rollback.

After an authorised release, configure the main repository once with no filename list. Observe discovered/retained/published/structured/withheld/failed counts, then verify scope, citations, symbols and genuine tracked documentation refresh through MCP, REST and CLI. Expected-answer briefs, actual live counts and final health remain pending. Deferred OCR, Outlook desktop and wider lifecycle operational acceptance remain separate.
