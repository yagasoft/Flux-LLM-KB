# Roadmap

This roadmap describes the native Windows application. Status separates
implemented capability, recorded operational acceptance and future work.
Percentages below retain the existing item-level delivery estimates; they are
not an overall completion score and are not averaged together. A passing local
test does not by itself establish deployment or live acceptance.

## Current delivery

| Item | Priority | Status | Progress % | Remaining Work |
| --- | --- | --- | ---: | --- |
| Native .NET, SQL Server and USearch foundation | P0 | complete | 100% | Maintain canonical SQL state, immutable index generations, recovery fences and model-free verification. |
| Local sources, retained UTF-8 and watcher reconciliation | P0 | complete | 100% | Maintain root policies, revision identity, searchable publication and source/event projections. |
| Durable scheduler and native worker supervision | P0 | complete | 100% | Preserve priority/FIFO admission, capacity ownership, receipt-first recovery and termination evidence as adapters expand. |
| Native Outlook ingress | P1 | in progress | 90% | Complete the remaining separately authorised desktop operational acceptance; retain logged-in COM and spool ownership boundaries. |
| Retained archive, Office and C# processing boundaries | P1 | complete | 100% | Keep unsupported formats explicit. Additional extraction providers need their own scoped implementation and verification. |
| Offline model gate and English document OCR | P1 | in progress | 98% | Address repeated-text fidelity, mixed native/scanned regions on one page and supported retry of a terminal failed OCR revision. Canonical page locations are available through deployed scoped corpus retrieval. Arabic OCR and language routing are outside the current delivery. |
| Native MCP, REST, CLI and Codex integration | P0 | in progress | 98% | Maintain parity across the eleven tools, bounded retained projections, cursor/confirmation/idempotency contracts and plugin authority. Corpus search/read are live-validated across MCP, REST and CLI; user hook trust and operational registration remain explicit actions. |
| Source pause/resume and complete deletion | P0 | in progress | 85% | The implementation includes admission/publication fences, drain, shared-blob preservation and survivor index rebuilds. Retain the delivery estimate until the remaining source-lifecycle deployment and disposable-source live acceptance are recorded. |
| Native repository and maintained documentation | P0 | complete | 100% | Keep repository/link checks in CI and feature closeout. The native Release build has zero warnings; 2,337 tests pass, with 17 opt-in browser cases skipped in the default suite and the affected browser case separately passing. Independent review found no remaining blocking issues. This is repository verification, not a deployment claim. |

English OCR and interactive Visio have scoped native delivery evidence:
[OCR](operations/2026-09-20-english-ocr-live-delivery.md),
[practical quality assessment](operations/2026-09-20-english-ocr-practical-assessment.md)
and [Visio](operations/2026-09-20-interactive-visio-delivery.md).
These records establish their stated scenarios, not universal format or OCR
accuracy guarantees. The [coverage matrix](file-type-coverage.md) states current
behaviour and limitations.

## Corpus retrieval

| Item | Priority | Status | Progress % | Remaining Work |
| --- | --- | --- | ---: | --- |
| Scoped corpus search and cited passage reading | P1 | complete | 100% | Maintain the deployed one-time chunk Full-Text index and MCP/REST/CLI parity. Live checks cover OCR image, native PDF, DOCX, XLSX and Visio search/read; extraction fidelity, same-page region OCR and failed-revision retry remain separate work. |
| Scoped lexical passage ranking investigation | P1 | complete (no ranking change) | 100% | Historical investigation closed after aggregate gains regressed DOCX. Future passage/ranking work follows the hybrid passage plan, without reopening these excerpt heuristics. The frozen source audit and experiment records remain evidence of their original scope. |
| Hybrid passage and semantic corpus retrieval | P1 | complete; staging accepted | 100% | Maintain the GPU-first/resident-CPU-fallback path, 20-second BGE GPU bound, 25-second outer deadline and OCR-safe scheduler ownership. The final two-caller staging run returned 96/96 ready searches, 80/84 strict top-five answer support (95.24%) across 24 English sources and 480/480 exact citations; full REST p95 was 16.46 seconds under the measured 20-second staging envelope. OCR waited 7.06 seconds for an active GPU batch, then completed without interruption; source deletion and watcher recovery were verified. Preserve these gates during future model, scheduler or passage changes. See the [live acceptance record](operations/2026-09-28-hybrid-search-live-acceptance.md). |

The [original retrieval design](design/corpus-retrieval.md) and
[scoped retrieval plan](design/scoped-corpus-retrieval-plan.md) retain the
deployed contract history. The delivered implementation follows the
[hybrid passage design](design/hybrid-passage-retrieval.md) and
[implementation plan](design/hybrid-passage-retrieval-plan.md), superseding the
earlier [semantic transition plan](design/semantic-corpus-retrieval-plan.md).
The scoped operations and one-time SQL Full-Text index are deployed and
live-validated. The hybrid passage projection is active in the installed app,
which was treated as staging for this acceptance. The frozen English relevance,
exact-citation, availability, two-caller latency and OCR-handover gates passed.
The [BGE-M3 ONNX evaluation](operations/2026-09-24-bge-m3-onnx-evaluation.md)
records the earlier failed relevance pilot, not a result for the new passage pipeline.

The hybrid item's 100% counts five completed delivery gates: complete passages,
coherent publication/rebuild, the shared retrieval engine with transport
parity, reviewed GPU-first/CPU-fallback scheduler ownership, and measured
English staging acceptance. It is not a claim about other languages, answer
abstention or every higher-concurrency workload. The [live acceptance record](operations/2026-09-28-hybrid-search-live-acceptance.md)
preserves the failed intermediate runs and the final evidence.

A current-stamp USearch refresh is implemented and passed focused SQL/USearch
recovery and publication tests. It rebuilds ANN membership from existing SQL
vectors after source suppression or restoration, without resetting passages or
running embedding inference. The first incremental staging release installed
the refresh without a migration, but remained `IndexUpdating`: the strict host
ran recovery only at startup while its deployment hold was active. A bounded
periodic recovery service is now registered for strict hybrid hosts and has
passed a held-startup, hold-release and repeated-publication integration test.
A follow-up incremental staging release recovered healthy index readiness and
exact-citation live search. The later GPU-first/CPU-fallback release, scheduler
mutation lock and watcher outbox fix passed the final two-caller and OCR
acceptance checks described above.

The separate [lexical passage ranking investigation](operations/2026-09-24-scoped-lexical-ranking-investigation.md)
used the old pilot for diagnosis and fresh development data for two bounded
prototypes. Neither passed the no-regression gate; no held-out ranking run
occurred and the deployed ranking remains unchanged.

## Recent increment

| Item | Priority | Status | Progress % | Remaining Work |
| --- | --- | --- | ---: | --- |
| Native instruction alignment and on-demand workspace brief | P1 | installed and locally accepted | 100% | No remaining work for the approved personal workflow. Machine AGENTS received only the approved cell/token edits, with unrelated bytes preserved. The cited brief skill is installed; 12 CLI/MCP fixture cases passed and real native search/read was checked separately. Unregistered workspaces report a gap; registration and plugin-wide distribution remain outside scope. See [acceptance](operations/2026-09-30-native-workspace-brief-acceptance.md). |
| Workspace-aware Codex prompt context | P1 | deployed and live-validated | 100% | No remaining work for the bounded lexical scope. Incremental IIS release `20260929T215634Z-2c6712dcd77a` deployed commit `2c6712dc` without migration; live health probes returned 200, the installed adapter yielded an exact re-readable public citation, the actual Codex client injected one record with `cwd`, and an unindexed workspace stayed empty. Disposable acceptance passed 12/12 useful positives, 0/12 irrelevant negatives and 12/12 boundaries; 40 calls at two callers measured 16 ms p95. The disable setting remains the operational fallback, subject to approval for any future use; broader relevance work remains deferred. |
| Automatic repository code and documentation coverage | P1 | stability release deployed; live acceptance incomplete | 80% | Release `09ce4dac` corrected verified Git file replacement and passed all 272 payload comparisons. The single source is configured at revision 11; authoritative rescans previously converged 1,032 unique current Git-tracked paths, with 881 completed C# branches and one normal parser-limit outcome. Subsequent SQL timeouts caused host stops and 11 unhandled shutdown cancellations; IIS disabled the application pool at 04:26 UTC on 1 October, with HTTP 503 at that observation. All three genuine briefs, scope/citation bindings, representative formats including Python and documentation freshness passed before this incident. The [stability and recovery corrections](design/repository-workspace-coverage.md#live-publication-recovery-addendum) now pass focused disposable shutdown, watcher, publication, lock-order and same-delivery recovery checks, including normal publication and cited readback. Runtime review is approved and feature Release checks passed 2,893 tests with 20 unchanged opt-in skips and zero warnings. Explicit stopped-pool updater recovery completed mandatory integration as `1b5bd5f6` and exact-payload review. Its first authorised Apply was aborted before payload mutation at a retained uncertain GPU reservation; all 272 predecessor files remained unchanged. Separately authorised exact-batch reconciliation and unchanged Apply subsequently succeeded, with all 272 installed files verified, unchanged held fingerprints, released hold and healthy probes. The private exact-batch reconciliation passes 82 combined disposable checks, including packaged command execution and subsequent normal worker continuation without inference. Automatic scans now discover 1,044 unique current paths at unchanged configuration revision 11, including 12 newly tracked files. The same worker remained running and publication advanced from 964 to 982 paths by 14:41 UTC, with no new unhandled crash, recycle or debugger process. Separately authorised terminal Publish recovery subsequently completed the original job/delivery with one added attempt and exact current REST/CLI/MCP citations. At 16:39 UTC, all 1,044 paths were published with no unfinished records or terminal failures, and all retained paths matched Git membership and current file bytes. Initial publication and the 1 October post-merge documentation-freshness check are complete; live acceptance of the retrieval correction remains pending. Safe cited code text matches current bytes; wider method context remains withheld by existing safety checks, and semantic scope capacity/index updates currently cause reported lexical fallback. Preserve these limitations during final acceptance and cleanup. The [repository retrieval reliability design](design/repository-retrieval-reliability.md) and [implementation plan](design/repository-retrieval-reliability-plan.md) now have locally implemented code-disclosure and bounded scoped-semantic corrections, with exact 10,001-vector HTTP readback and independent application review. Initial retrieval release `a248811a` completed mandatory integration and authorised incremental deployment with the additive proof schema/backfill. Fresh live acceptance found 1,023 of 1,066 current eligible paths published and 43 terminal Embed failures predating deployment. Whole-file disclosure and scoped query-plan corrections, plus explicit checkpoint-preserving Embed recovery, completed mandatory integration and authorised incremental deployment as `4f9393ef`, with 3,013 feature/main tests, zero build warnings and all 273 installed payload files verified. Current exact code/docs reads pass through MCP/REST/CLI. Root semantic acceptance still timed out under a parallel page plan; its scoped-only serial correction, separately authorised terminal recovery and complete current live acceptance remain. The item stays at 80% until those gates pass. Five equally weighted gates cover configuration/ingress, disposable automatic publication, local verification/review, release and live acceptance; the first four historical gates passed, while live acceptance remains outstanding. See [acceptance](operations/2026-09-30-repository-workspace-coverage-acceptance.md). |

## Deferred follow-ups

The following items are deferred as of 29 September 2026. Their delivery
estimates and outstanding acceptance requirements above remain unchanged:

- Source pause/resume and complete deletion: remaining deployment and complete
  disposable-source live acceptance.
- English OCR: supported retry of terminal failed revisions, mixed native/scanned
  regions on one page and repeated-text fidelity.
- Native Outlook ingress: remaining separately authorised desktop operational
  acceptance.

## Update rules

Update affected `Progress %` and `Remaining Work` entries when capability or
acceptance evidence changes. Keep implemented behaviour in
[architecture](architecture.md), operational commands in [setup](setup.md) and
surface contracts in [integrations](integrations.md). Store only public,
sanitised evidence in Git. Production changes, source-original access and model
acquisition remain subject to their explicit operational boundaries.
