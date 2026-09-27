# Native BGE CPU adapter verification

Date: 27 September 2026. The implementation branch has independently reviewed
offline native tokenizer, embedding and reranking adapters. This is CPU numeric
and lifecycle acceptance only. Neither provider nor the shared admission gate is
registered in production. Full search, GPU execution, request-owner recovery,
retrieval quality and response-time acceptance remain outstanding.

## Fixed models and loading boundary

`BgeOfflineModels` uses the existing BGE-M3 ONNX revision
`5617a9f61b028005a4858fdac845db406aefb181` and the float32
BGE-reranker-v2-m3 export of revision
`953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e`, export identity
`f083b9dbba6d1b56869a6572e5d7f9f5fd17f961d3c521ea2424d80c5f800580`.
See the [conversion record](bge-reranker-offline-conversion.md).

Recursive inspection of every ONNX message, including nested tensor attributes,
established the external dependency closure for each exact graph SHA-256:
`model.onnx_data` only. The fixed model specifications bind both graph and data;
a graph change requires fresh dependency inspection and reference parity. Model
loading uses their explicitly verified co-located paths. It never resolves a
provider name, checks another drive or starts acquisition.

The existing model verifier now supports an explicitly selected literal bundle
directory chain under `J:\Models`. It freshly checks lengths and hashes, persists
a receipt and retains no-follow ancestor handles plus share-read payload handles.
Ancestors cannot be renamed/replaced, and held files cannot be overwritten or
deleted. Directory handles allow unrelated directory writes. The native session
owns its complete lease until session disposal; a path alone is not a lease.

The tokenizer reads the separately pinned JSON for each model and verifies the
cached Tokenizers.DotNet 1.4.0 managed/native runtime. It uses an isolated assembly
context and an explicit native DLL path. A held rebuild exposed native memory
growth when the mimalloc-backed DLL was unloaded after every tokenizer. The local
correction retains one extra verified DLL handle and protected runtime lease until
process exit. Each request still balances its own DLL reference and disposes its
tokenizer and model session; another verified runtime path or identity is refused.
An isolated tokenizer-only run reached 3.48 GB after 20 ordinary unload cycles,
compared with 1.06 GB after 80 cycles with an extra DLL handle held. That second
run was a diagnostic; the corrected payload still awaits production deployment.
Tokenizer/model payloads remain in the central store and are not copied into
application releases.

## Inference contracts

Both tokenizers count untruncated inputs. Configured JSON truncation/padding is
refused; malformed UTF-16 is refused before the native call. Reranker pairs use
the verified XLM-R template with two separator tokens between complete query and
passage inputs. Batch padding uses token 1 with attention mask zero.

The first profile accepts at most 512 complete model tokens and native batches
of four. The reranker preflights the complete shortlist, at most 50 distinct
positive passage IDs, before executing any batch. It returns exactly one finite
raw logit for each original ID, or throws an explicit refusal. Logits are ranking
scores, not probabilities that evidence is true. Embeddings must have exactly
1,024 finite components and a non-zero norm, then receive L2 normalisation.
Model-space fingerprints include the pinned files, tokenizer and input/output
policy; CPU placement is not a different embedding space.

CPU inference cancellation terminates a running ONNX call and observes its return
before disposal. Session disposal is synchronised with execution and precedes
file-lease release. There is no GPU session creation path in this slice.

## Observed evidence and remaining gates

The opt-in native probe uses public synthetic fixtures and verified local models.
Ordinary tests skip the real-model probe and never acquire weights. The Python
reference ran with socket connections blocked and recorded zero network attempts.
Its recursive dependency inspection, complete token IDs and vectors/logits are
stored under `J:\Models\manifests\bge-reranker-source-20260927`.

| Check | Observed result |
| --- | --- |
| Native tokenizer IDs | Exact reference equality, including Unicode, CRLF, special-token literals and complete pair template |
| Embedding components | Maximum absolute reference difference `0.0000004116445779800415` |
| Reranker logits | Maximum absolute source-reference difference `0.000015676021575927734` |
| Pair boundary | Exactly 512 tokens accepted; 513 refused without truncation |
| Native library lifetime | Request references balanced; verified runtime-only process pin passed focused regression |
| Native models, contracts and model-store checks | 51 passed; zero failures/skips in the explicitly enabled run |

These are numerical and lifecycle checks. Probe aggregate timings include cache
verification, loading and several fixtures, including a 512-token pair. They are
not per-request model-load measurements, a 50-candidate benchmark, full-search
latency or evidence that CPU placement meets the service target. GPU execution
must first run within reviewed scheduler ownership and release/recovery boundaries.
Keeping models loaded between requests remains a separate conditional enhancement.

The unreleased `SharedGpuAdmissionGate` selects the existing OCR physical slot
and owner key for allowlisted interactive retrieval and background embedding
batches. It enforces exact runtime/settings profiles and batch/byte bounds.
No production retrieval memory estimate has been selected. The accompanying SQL
execution read requires the exact request instance, task, batch, dispatch and
reserved slot in one statement. Cancellation/expiry is execution metadata, never
proof of release; uncertain capacity refuses new execution reads. Native request
payload ownership and process-incarnation recovery now pass the independent
executor gate. The guarded GPU factories require an active context issued after
the exact SQL acknowledgement and fenced execution read. It is invalidated
before settlement; expired/cancelled contexts, wrong profiles and cache misses
are refused before native session creation. DirectML settings match the existing
OCR factory: sequential execution, device 0 and memory patterns disabled.

The guard/context suite passed 27 cases; a fresh explicitly enabled cached CPU
reference/lifetime regression passed both real-model probes. This supplies no GPU
parity or memory evidence. Trusted pipeline wrappers must await every native task
and successful disposal before reporting capacity released. Actual GPU execution
must use the canonical shared OCR slot; disposable SQL ownership alone cannot
exclude production OCR. Runtime wiring and measured GPU behaviour remain, with no
production registration implied.
