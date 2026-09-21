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
| Offline model gate and English document OCR | P1 | in progress | 98% | Address repeated-text fidelity, mixed native/scanned regions on one page, page fields in search responses and supported retry of a terminal failed OCR revision. Arabic OCR and language routing are outside the current delivery. |
| Native MCP, REST, CLI and Codex integration | P0 | in progress | 97% | Maintain parity across the nine tools, bounded retained projections, cursor/confirmation/idempotency contracts and plugin authority. User hook trust and operational registration remain explicit actions. |
| Source pause/resume and complete deletion | P0 | in progress | 85% | The implementation includes admission/publication fences, drain, shared-blob preservation and survivor index rebuilds. Retain the delivery estimate until the remaining source-lifecycle deployment and disposable-source live acceptance are recorded. |
| Native repository and maintained documentation | P0 | complete | 100% | Keep repository/link checks in CI and feature closeout. The native Release build has zero warnings; 2,300 tests pass, with 17 opt-in browser cases skipped in the default suite and the affected browser case separately passing. Independent review found no remaining blocking issues. This is repository verification, not a deployment claim. |

English OCR and interactive Visio have scoped native delivery evidence:
[OCR](operations/2026-09-20-english-ocr-live-delivery.md),
[practical quality assessment](operations/2026-09-20-english-ocr-practical-assessment.md)
and [Visio](operations/2026-09-20-interactive-visio-delivery.md).
These records establish their stated scenarios, not universal format or OCR
accuracy guarantees. The [coverage matrix](file-type-coverage.md) states current
behaviour and limitations.

## Planned retrieval work

| Item | Priority | Status | Progress % | Remaining Work |
| --- | --- | --- | ---: | --- |
| Scoped corpus search and cited passage reading | P1 | planned | 0% | Implement scoped chunk retrieval and bounded evidence reads against current document publication/provenance contracts; verify native and OCR text across MCP, REST and CLI. |
| Evaluated semantic corpus retrieval | P1 | planned | 0% | Deliver scoped retrieval first; select an exact cache-verified local model/runtime, review schema and transaction invariants, test model/index transition and rollback, demonstrate retrieval benefit and obtain operational authority. |

The [retrieval design](design/corpus-retrieval.md) and its
[scoped retrieval plan](design/scoped-corpus-retrieval-plan.md) and
[semantic retrieval plan](design/semantic-corpus-retrieval-plan.md) are future
intent. They do not activate providers, acquire models or imply that proposed
`corpus.search` and `corpus.read` operations already exist.

## Update rules

Update affected `Progress %` and `Remaining Work` entries when capability or
acceptance evidence changes. Keep implemented behaviour in
[architecture](architecture.md), operational commands in [setup](setup.md) and
surface contracts in [integrations](integrations.md). Store only public,
sanitised evidence in Git. Production changes, source-original access and model
acquisition remain subject to their explicit operational boundaries.
