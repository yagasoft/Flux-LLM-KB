# Interrupted hybrid rebuild and controlled forward recovery

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

## Approved replacement capability

The user approved the controlled replacement of this interrupted 35-input rebuild.
The recovery extension was merged and its controlled replacement committed. The
successor operation and epoch are held while native memory recovery is completed.
Independent review concluded that controlled replacement is clearer than repairing
individual chunk identities, failed requests, manifests and checkpoint counts.
The final focused lifecycle run passed 63 tests, including replacement, source
deletion and native-operation recovery. The preceding combined recovery run passed
70 tests and the additional transaction-fence run passed 19. Independent review
approved the complete recovery change and the final archived-task ownership check.
The first closeout's feature suite passed 2,646 tests, but its merged-main suite
reported one source-deletion failure after a pinned query session was closed. The
failure did not reproduce in isolation. Both affected lock tests now require a
successful SQL lock-release acknowledgement before asserting immediate deletion;
their blocked-deletion, control-root and terminal-receipt assertions remain intact.
Independent review approved this fixture correction and 41 focused tests passed.
This corrects an underdefined release premise; it does not prove the cause of the
unreproduced failure. The complete closeout must pass before deployment.

The incremental updater and rebuild operator implement one explicit replacement
of the interrupted release with schema 54:

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
complete replacement passed independent review, required feature closeout, and a
fresh `-PlanOnly` operational packet. The user approval authorised this
replacement action, including its new operation/epoch and disposal of the partially
rebuilt projection. It does not authorise model acquisition or dead-letter processing.

Use `-ApplyHybridPassageRebuild -ReplaceHybridRebuildRelease <exact old release>`
with the incremental updater. The read-only plan binds the predecessor journal,
manifest, payload/configuration/schema hashes and SQL receipt. The successor
journal retains that immutable binding and the predecessor's original scheduled
intake preference. SQL supersession and unstarted-task cancellation commit together
under the existing fences. Historical jobs retain their actual state and attempt
counts; a separate supersession receipt permanently removes their execution
authority. Embed request and native receipt history survives projection deletion.
The successor hold takes ownership only after an authoritative SQL receipt matches
the new manifest. A failed post-activation attempt restores deny-all admission for
that successor; resume uses its exact new release name. Downgrade refuses while
rebuild or supersession receipts exist.

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

## Successor state and native memory investigation

The controlled replacement committed on 27 September 2026 as release
`20260927T212805Z-38c90f816f21-hybrid`, operation
`ce09966e-c2f7-4377-816a-838bbeb8c307`, epoch
`044b6dab-b58c-4844-8d80-9329432ef29a`. Schema migration 55 is applied.
The successor retained all 35 canonical inputs and historical failures. SQL proves
55 old jobs superseded, 84 old embedding requests retained, and 13 never-native-
owned queued requests cancelled. Its 3,596 new passages contain no ASCII
whitespace-only entry. A bounded, reviewed same-release resume recycled a drained
IIS worker without resetting the operation or checkpoints. At the subsequent
deny-all hold, 28 inputs and 908 vectors had completed; seven inputs remained.
No active native batch was present and no new job had failed.

The first successor worker grew from 21.3 GB to 42.3 GB private memory while
embedding batches ran. Managed GC committed memory was about 415 MB, so the bulk
was outside the managed heap. The recycled worker also grew to approximately
20.0 GB before a five-minute bounded resume timed out and restored deny-all
admission. A separate, offline tokenizer-only test reproduced 3.48 GB private
memory after 20 ordinary tokenizer create/encode/dispose/DLL-unload cycles,
including after full GC. Keeping one additional verified native DLL handle held
for that test reduced 20-cycle memory to 1.19 GB; 80 cycles plateaued near
1.06 GB. This implicates repeated unload of the mimalloc-backed tokenizer DLL;
it does not prove DirectML never retains memory. No acquisition, weight
residency, scheduler change or dead-letter action followed from the observation.

The local correction pins only the verified tokenizer native DLL and protected
runtime file lease for the process lifetime. Each tokenizer and ONNX model session
continues per-request disposal. The incremental updater's existing resume checks
the old commit and payload hash; it cannot accept a corrected binary within this
release. The forward-patch recovery operation preserves
the current operation, epoch, checkpoints, source fingerprint and deny-all hold
while draining and replacing only the compatible application payload. No updater
bypass or second reset of the successor projection is authorised.

The incremental updater now has that forward-patch route. Its initial invocation
requires the exact held predecessor release and independently reviewed SHA-256
fingerprints for the new web and operator payloads. Run `-PlanOnly` first, then
`-ApplyHybridPassageRebuild -PatchHybridRebuildRelease <release>
-ExpectedPatchCandidateHash <hash> -ExpectedPatchOperatorHash <hash> -Apply`
only with production approval. It verifies the unchanged schema, SQL operation,
epoch, manifest, canonical inputs, hold owner, configuration and offline model
inventory before replacing the web payload. It disables interactive intake,
drains the GPU and IIS worker without interrupting an active OCR page, records
activation intent, and retains the old payload for inspection. It neither runs a
migration nor resets or prepares the projection. It releases the hold only after
the same operation finishes and the required loopback and input checks pass.

If activation or validation stops, the hold remains deny-all. Resume the exact
patch release through `-ApplyHybridPassageRebuild -ResumeHybridPatchRelease
<patch-release> -Apply` after reviewing its `-PlanOnly` output. Recovery checks
the immutable predecessor and patch binding, recopies any partial payload from
the verified candidate, and validates the full payload fingerprint before
starting IIS. It never rolls back the schema, projection or old application
binary automatically. Neither this route nor an approved dry run authorises a
new model download or dead-letter processing.
