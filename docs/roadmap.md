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
| Hybrid passage and semantic corpus retrieval | P1 | merged implementation; held live rebuild requires forward recovery | 20% | Schema and disposable reset committed; background GPU execution and confirmed cleanup are observed. The rebuild completed 20 of 35 inputs before separator-only passages exhausted one embedding batch. The merged coherent-passages-v2 fix omits blank spans without rewriting canonical text. The user-approved controlled replacement is now implemented locally, with 70 focused checks passing; full feature closeout, independent final/operational review and execution remain pending. Permanent job supersession, proven unstarted GPU cancellation, retained failure/native history and authoritative SQL receipt recovery protect the new operation. See the [recovery record](operations/2026-09-27-hybrid-rebuild-recovery.md). The 24-source, 96-question English acceptance set and lexical baseline remain frozen. Background embedding loading measured median 3.57 seconds and p95 3.69 seconds; full query/reranker latency, healthy one/two-caller behaviour, sustained-search OCR waiting, numeric GPU parity and held-out quality remain pending. Retaining models remains conditional on complete loading/memory/full-search evidence. Current production is held; no healthy semantic-search delivery is claimed. |

The [original retrieval design](design/corpus-retrieval.md) and
[scoped retrieval plan](design/scoped-corpus-retrieval-plan.md) retain the
deployed contract history. Future delivery follows the
[hybrid passage design](design/hybrid-passage-retrieval.md) and
[implementation plan](design/hybrid-passage-retrieval-plan.md), superseding the
earlier [semantic transition plan](design/semantic-corpus-retrieval-plan.md).
The scoped operations and one-time SQL Full-Text index are deployed and
live-validated. Production semantic retrieval is enabled only within the held incomplete rebuild;
the [BGE-M3 ONNX evaluation](operations/2026-09-24-bge-m3-onnx-evaluation.md)
records a failed relevance pilot and does not activate an embedding provider.

The hybrid item's 20% counts one completed, independently reviewed delivery gate
out of the plan's five equally counted milestone gates. It is not an effort,
elapsed-time or accuracy estimate. The partial scheduler-core work does not yet
count as completed milestone 2 without hardware acceptance. The locally implemented
query/runtime slices likewise do not count as whole-release acceptance. The implementation plan records the local
focused checks; real-model quality, full-search latency and deployment remain
unverified.

The separate [lexical passage ranking investigation](operations/2026-09-24-scoped-lexical-ranking-investigation.md)
used the old pilot for diagnosis and fresh development data for two bounded
prototypes. Neither passed the no-regression gate; no held-out ranking run
occurred and the deployed ranking remains unchanged.

## Update rules

Update affected `Progress %` and `Remaining Work` entries when capability or
acceptance evidence changes. Keep implemented behaviour in
[architecture](architecture.md), operational commands in [setup](setup.md) and
surface contracts in [integrations](integrations.md). Store only public,
sanitised evidence in Git. Production changes, source-original access and model
acquisition remain subject to their explicit operational boundaries.
