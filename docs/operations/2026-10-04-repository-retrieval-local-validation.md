# Repository retrieval local validation and release preparation

4 October 2026. Local implementation approved; production deployment, migration
and automatic proof backfill await a concrete operational decision. This note
covers the [retrieval design](../design/repository-retrieval-reliability.md) and
[implementation plan](../design/repository-retrieval-reliability-plan.md).

## Delivered behaviour and evidence

Canonical C# indexing produces an optional, immutable disclosure proof. Cited
reads recognise proved structural braces while retaining credential, JSON,
parser, context and envelope checks. Protected literal/comment spans apply to
clipped windows. Invalid, missing, old-policy or incomplete proof stays
conservative. New artifacts/proofs commit atomically; model-free recovery writes
one eligible retained artifact at a time under the publication and deployment
holds. Canonical text, chunk/search-input hashes, vector identities and source
configuration are not changed by this projection.

Root/workspace semantic search uses exact SQL generation membership, in 256-row
keyset pages, with a maximum 100-candidate accumulator. There is no total-vector
cutoff or global ANN opening for scoped requests. Scope/publication bindings,
integrity and lease currency apply to every page and final hydration. Cancellation
or a lost/changed generation discards partial work; callback ownership persists
until its work stops. All-corpus search retains its native ANN path.

The normal Git-discovery fixture passes configuration, publication, search and
exact read of a complete C# method beyond 16 Ki UTF-16 units, with CRLF and Unicode.
It retains the larger-than-32-KiB configuration request, supported code formats,
C# facts/references, automatic edit/rename/untrack reconciliation and private
untracked exclusions. Domain builder, worker and lease tests pass 62 tests;
the combined disposable-SQL run passes 179 tests, including exact large-scope
oracles, corruption, publication/deletion races and scoring/I/O cancellation.
The independent reviewer found a composite-literal boundary defect; five new
clipped-read regressions failed before its correction and now pass. The affected
proof-storage run passes 108 tests; six HTTP end-to-end cases pass. The hosted
service fixture now expects the added recovery worker and its focused rerun passes.
Independent review approved the application capability and updater support,
including migration, rollback faults and the complete final diff. Repository
integration uses the required closeout script; its generated operation log and
observed final report establish the full-suite/Git result.

The first full-suite attempt stopped at the existing external plugin-validator
dependency: its bundled default path is absent. The retained OpenAI validator and
helper match both hashes in the 30 September workspace-brief tooling receipt.
All 28 registrar tests pass with the existing process-local
`FLUXKNOWLEDGE_PLUGIN_VALIDATOR_PATH` override. Closeout uses that pinned copy;
validation assertions, application code and installed plugins are unchanged.

The configured feature suite passed 2,969 tests with 20 existing opt-in skips.
Main verification then reproduced an existing GPU dispatch fixture timeout.
Disposable SQL deadlock metadata showed preflight readers overlapping the injected
uncertainty transaction. That case now waits for all six preflight reads to finish
before acknowledgement, preserving concurrent duplicate delivery, the real clock,
eight-second watchdog, zero native calls and durable capacity/outcome assertions.
Its 19-case run and three fresh executions of the affected case pass; independent
review approved the correction. Production executor code is unchanged. The required
closeout receipt must establish the final full-suite and Git result with this fixture.

The measured exact SQL candidate scans took 4.14 seconds for 10,000 vectors and
4.30 seconds for 10,001, including forced collection for memory sampling. Each
visited 40 pages with at most 256 payloads and 100 retained candidates. Sampled
additional live managed memory was approximately 230 KiB at those sizes, versus
336–340 KiB at 256–513 vectors. The oracle arrays were kept alive across samples;
these are diagnostic measurements, not a process/native-memory bound or live
model-inference latency acceptance. Cumulative allocation was about 46 MB at the
larger sizes and is distinct from retained memory. No deadline or total cap was
increased after this checkpoint.

Ignored receipts are retained under `.agents/test-results/retrieval-reliability`
and `.agents/run-logs`; they contain synthetic/disposable evidence. No real model
was acquired or loaded. SQL fixtures create isolated databases and do not use the
installed application's production connection. The earlier 1 October 17:05 UTC
documentation freshness receipt remains historical evidence, not acceptance of
this new payload.

## Read-only deployment baseline

The baseline observed at 23:03 UTC on 3 October (4 October locally) is main
`687f17073516316e12814779620391f171d84308`, with 1,047 tracked paths, and installed
payload `1b5bd5f670d1c879695f3625f16ff023f4173c4e`. Live, ready and `/api/index-health`
probes succeed; index health is healthy, with no recorded retry/failure, on
generation `ac432361be7c1135d0807012f1bb778a`.

The verified repository root is `8133bdeb-86fc-4c04-a641-52346556753b`, enabled at
configuration revision 11. These observations do not establish today's complete
published-path/hash equality or proof coverage. Those checks require a fresh
post-release inventory. No production write, deployment or restart has occurred.

## Release target, limitation and rollback

The additive migration `20261003220411_AddCanonicalCodeDisclosureProof` creates
only `CanonicalCodeDisclosureProofs` and `CanonicalCodeDisclosureSpans`, with
identity/range constraints and artifact-linked cascade cleanup. It does not
rewrite canonical content, vectors, profiles or source configuration. The
idempotent SQL generated from `20260927202655_AddCorpusRebuildSupersession` has
SHA-256 `59CA9DCB89E5A8B272BA2934D25792034C3C0447FCE534C4874F86086B33E741` and is
retained locally as `.agents/run-logs/retrieval-disclosure-proof-up.sql`; it has
not been applied to production.

The routine incremental updater now supports this migration with the exclusive
`-ApplyCodeDisclosureProofMigration` option. PlanOnly verifies the prior or already
complete additive schema/history and reports both migration and automatic
backfill. The observed production baseline is the expected prior migration; the
new migration is absent and ALTER permission is available. Its complete schema
contract SHA-256 is
`C269D0803C5074D17E19F91E70A35AE6D999240971C01139746612ED2C977CEB`.
The metadata contract includes columns/types/nullability/collation, ordered keys,
FKs/cascades, trusted checks and absence of unexpected triggers. Database-default
collations are represented separately from the explicitly binary fingerprint.

Disposable SQL proves failure after header creation rolls back the open
transaction, the generated SQL is idempotent, and disabled constraints or partial
tables refuse recovery. The actual payload-swap fault tests cover candidate/prior
probe failures, no migration replay/down and no prior-payload start on uncertain
schema. Hold release requires both verified additive compatibility and prior
payload/probes; the existing post-hold-release commit boundary remains.

The updater preserves configuration, SQL/runtime data, recovery payloads and Codex
registration, drains GPU work and validates under the existing deployment hold.
Prepare the immutable integrated payload and immediate independent operational
review, then obtain explicit user authority for incremental Apply, the additive
schema and subsequent automatic backfill. No alternate operational path is needed.

The concrete commands, after repository closeout, are:

```powershell
pwsh -NoProfile -File scripts/deploy/update-native-iis-incremental.ps1 -SourceRoot 'E:\LLM KB' -PlanOnly -ApplyCodeDisclosureProofMigration
# Apply requires approval of the exact committed release and a fresh operational review.
pwsh -NoProfile -File scripts/deploy/update-native-iis-incremental.ps1 -SourceRoot 'E:\LLM KB' -Apply -ApplyCodeDisclosureProofMigration
```

Rollback retains the additive schema/proof rows and restores the compatible
installed payload through the reviewed incremental recovery path. Prior binaries
ignore proofs and resume conservative disclosure and the former scoped capacity
refusal. Do not reverse SQL, downgrade across the Git-root compatibility boundary,
delete caches, change model profiles or replay Publish/dead-letter jobs.

## Remaining live acceptance

After an authorised release, verify proof completeness for accepted methods,
unchanged canonical/vector identities, current Git membership, watcher/rescan
convergence, current documentation and exact method reads through MCP/REST/CLI.
Freeze 20 representative code/docs queries before measuring two callers twice
(40 observations), spanning repository and nested scopes. On a quiescent current
generation require in-scope, semantic-ready, exactly cited results and p95 below
20 seconds; record fallback, update and timeout observations separately. The
existing 25-second outer deadline remains. Verify health/worker continuity and
report new crash/debugger or SQL failures without processing dead letters.

The historical coverage gate remains 80% until this release and its supported
live retrieval cases pass. AGENTS and dashboard manuals are unchanged; deferred
XLSX branch reconciliation, OCR, Outlook and broader lifecycle work remain side
notes.
