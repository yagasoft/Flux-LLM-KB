# Interrupted hybrid rebuild and proposed forward recovery

## Observed state

The incremental deployment activated the shared hybrid implementation and applied
the reviewed schema. The disposable projection reset committed. The source and
canonical-input manifest contains 35 documents, including the 24 public English
acceptance documents. The application is held while the rebuild is incomplete.

The rebuild reached 20 completed documents and 246 persisted vectors, then one
embedding job exhausted its three attempts. Its batch included newline-only
passages. Passage construction emitted these separators as passages, while the
native inference contract correctly refused whitespace inputs before loading a
model. A read-only census found 1,978 whitespace-only passages among 5,581 prepared
passages. This is an indexing defect; it is not evidence of weak embedding quality.

An ordinary resume cannot correct prepared chunks or replace a terminal failed
job. A changed passage policy also cannot be substituted beneath the committed
manifest and checkpoints. Restoring previous binaries after the schema/reset
boundary is prohibited. The current payload, journal and deny-all hold remain in
place. No dead-letter entries have been processed.

## Passage correction

`coherent-passages-v2` omits whitespace-only spans while preserving exact canonical
offsets and content hashes for meaningful passages. Empty retained segments still
advance input traversal. Explicit segment ends are recognised as valid passage
boundaries, so a short complete Visio shape followed by whitespace is not split
at an earlier word boundary. Grapheme, sentence-overlap, length and token limits
remain applicable. Canonical source text is not trimmed or rewritten.

The changed passage-output policy requires a fresh derived projection. The local
correction does not change models, ranking, scheduler ownership or residency.

## Proposed replacement capability

This is a proposed recovery extension, not an implemented or executed reset.
Independent review concluded that controlled replacement is clearer than repairing
individual chunk identities, failed requests, manifests and checkpoint counts.

Extend the existing incremental updater and rebuild operator for one explicit
replacement of an interrupted rebuild:

1. Identify the old release, operation, epoch, manifest, activated payload,
   configuration, schema and database exactly. Refuse a foreign hold, ambiguous
   journal or changed input. Preserve the original recovery packet.
2. Use the existing deny-all hold and admission/query/publication fences. Allow
   active native work and any OCR page to finish. Prove capacity release and the
   exact IIS worker/descendant exit before replacement.
3. Capture the same canonical inputs and document winners with the corrected
   passage policy. Reserve a new operation, epoch and job/dispatch identities.
   The plan must identify the interrupted operation it supersedes.
4. Durably supersede every unfinished Embed and Publish job/outbox delivery owned
   by that worklist, so no old delivery can claim or publish after the new hold
   clears. Retire its unstarted queued embedding work only after proving no
   executor acknowledgement or native ownership. Anything admitted requires its
   existing cleanup/recovery proof. Preserve canonical inputs, completed/failed
   history, native receipts and supersession history. Do not fabricate completion,
   clear retry counters, edit chunks beneath checkpoints or process dead-letter
   entries.
5. Commit supersession and the new disposable projection atomically. Validate
   replay, concurrent admission refusal and rollback before exposing this path.
   Resolve an ambiguous commit response through its authoritative SQL receipt.
   Use the existing preparation, GPU execution, native/Full-Text final validation
   and hold-release flow for the replacement worklist.

Before execution, retain focused integration evidence for changed input, wrong
operation/hold, active or uncertain capacity, acknowledged queued work,
publication/query races, failure rollback, replay and preserved histories. The
complete change requires independent review, required feature closeout, and a
fresh `-PlanOnly` operational packet. User authorisation is required for the
replacement action because it changes the committed operation/epoch and discards
the partially rebuilt disposable projection.

After the replacement transaction, recovery stays forward at the new compatible
release and operation. Before that transaction, a failed attempt retains the
original held rebuild. No automatic old-binary restore or schema downgrade is a
rollback strategy.

## Measurements and remaining acceptance

A bounded five-minute trace on the activated canonical IIS worker recorded 28
complete background embedding load/unload pairs and 29 inference phases. All
recorded phases succeeded; separator refusals occurred before these phases.

| Background embedding phase | Samples | Median | p95 |
| --- | ---: | ---: | ---: |
| Load | 28 | 3,569.9 ms | 3,689.0 ms |
| Inference | 29 | 440.0 ms | 474.8 ms |
| Unload | 28 | 192.2 ms | 200.8 ms |

A separate three-minute sample observed IIS private bytes peaking at approximately
11.4 GiB and working memory at approximately 9.8 GiB. These are sampled process
measurements, not per-model allocations or a memory-retention decision. Global
WDDM GPU readings include desktop applications.

Embedding loading alone exceeds the proposed two-second healthy full-search
target in this run. Full query embedding, reranking, end-to-end latency and model
memory still need measurement after recovery. Keeping models loaded remains a
separate conditional enhancement; no residency changes are authorised by these
background timings alone.

The independent held-out search-quality run, healthy one/two-caller latency,
sustained-search OCR waiting and full search/read parity remain pending. Live MCP
initialisation/discovery and CLI corpus query succeeded. An old citation from the
previous epoch correctly returned `evidence-invalid` after reset.

All finished build/test workers, trace collectors, memory samplers and recovery
waiters must exit. Reusable task build servers are disabled to reduce idle
process accumulation. The held application and SQL Server remain service
processes, not orphaned measurement workers.
