# BGE reranker source acquisition request

Prepared: 26 September 2026. Status: approved and completed on 27 September 2026.
This request covers source artifacts only, not runtime packages, model variants,
production activation or permission to bypass the central model gate.

## Completion evidence

The four approved files transferred exactly **2,288,188,149 bytes**. Three
tokenizer dependencies were reused with zero transfer. All seven files passed
fresh complete SHA-256/length checks after acquisition; the two small upstream
Git blob identities also matched. The raw SHA-256 values are now recorded:
`config.json` is `13dcd6c31d9fec9d1d8e158702072f62d7fa7d312a64b9fe057bec9a08cfe41a`;
`README.md` is `c887aa6dd2598f908bf0582ca7068cc816585c7a1b6a07df305b631ede0cb174`.

The explicit acquisition helper and sealed manifest are retained under
`J:\Models\manifests\bge-reranker-source-20260927`. Twenty synthetic safeguard
tests passed before acquisition, including separate-process locking, no-download
cache hits, closed misses, unavailable/reparsed/unwritable paths and cumulative
transfer accounting across interruption. Independent review approved the bounded
helper after the transfer-ceiling corrections. No additional artifacts were
downloaded. The completed receipt is
`J:\Models\inventory\bge-reranker-source-20260927-64ab3f20f8de4856a8c5c3663d8623ac.json`.

Subsequent inspection found every required conversion package already cached.
Their [non-destructive adoption and offline conversion](bge-reranker-offline-conversion.md)
therefore required no further acquisition. Source availability does not activate
the application provider or complete the hybrid-search acceptance gate.

## Exact source and destination

Repository: `BAAI/bge-reranker-v2-m3`.
Immutable revision: `953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e`.
Format: upstream safetensors and its original tokenizer/configuration.
Destination: `J:\Models\bundles\bge-reranker-v2-m3\953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e`.
Content-addressed copies, staging, locks and receipts also remain under
`J:\Models`. The model card is retained for the upstream licensing declaration.

Fresh [metadata-only inspection](https://huggingface.co/api/models/BAAI/bge-reranker-v2-m3/revision/953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e?blobs=true)
confirmed the pinned revision, lengths and identities below. No artifact was
transferred during this inspection.

| Missing file | Expected bytes | Upstream identity |
| --- | ---: | --- |
| `model.safetensors` | 2,271,071,852 | SHA-256 `d9e3e081faff1eefb84019509b2f5558fd74c1a05a2c7db22f74174fcedb5286` |
| `tokenizer.json` | 17,098,273 | SHA-256 `69564b696052886ed0ac63fa393e928384e0f8caada38c1f4864a9bfbf379c15` |
| `config.json` | 795 | Git blob `9f62673cb00ec41dcec8947b9ed16f6f2eb23ba2` |
| `README.md` | 17,229 | Git blob `553540879ec61aea21df00434c984c0f760a3fcc` |

Expected transfer ceiling: **2,288,188,149 bytes** (about 2.13 GiB).
Git blob identifiers for the small files are not raw-file SHA-256 hashes.
Acquisition must validate their pinned Git blob identities and lengths, calculate
raw SHA-256 values and retain them in the verified bundle receipt before any
loader can use the bundle.

## Verified reuse

These files already exist in the verified BGE-M3 ONNX bundle under `J:\Models`.
Fresh hashes and lengths match the reranker's pinned upstream identities; do
not download them again. Reuse them non-destructively and preserve the embedding
bundle and all existing consumers.

| Reusable file | Bytes | Raw SHA-256 |
| --- | ---: | --- |
| `sentencepiece.bpe.model` | 5,069,051 | `cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865` |
| `special_tokens_map.json` | 964 | `8c785abebea9ae3257b61681b4e6fd8365ceafde980c21970d001e834cf10835` |
| `tokenizer_config.json` | 1,173 | `7e4c1cc848840aeccdd763458c18dd525eb0f795c992e00ebe9c28554e7db2d4` |

The last two files' Git blob hashes also match the upstream metadata. The
embedding `tokenizer.json` has a different hash and cannot substitute for the
reranker tokenizer.

## Cache checks and acquisition controls

Inspected central inventory/manifests and accessible files under `J:\Models`,
including bundles, content-addressed artifacts, staging and provider caches.
Checked the default user Hugging Face cache and relevant package caches
`E:\Temp\pip-cache`, `E:\FluxPackageCache` and `E:\Codex Workspaces`.
No exact weight/tokenizer match was found. There are no process-level Hugging
Face, Transformers, Torch or XDG cache overrides. Default user Torch and local
Hugging Face cache roots are absent. Reparse directories were not followed;
these checks do not claim whole-machine absence.

`J:\Models` is present and is not a reparse point. J: had approximately 239 GB
free during inspection. Recheck availability, ancestry, space and exact caches
under the per-artifact lock immediately before acquisition. A new cache hit
reduces transfer; it never authorises replacement of an existing artifact.
An identity mismatch or ambiguous external hit stops the affected acquisition.

Retain partial transfers, resume only after validating pinned identity/length,
verify the complete file, publish atomically and record actual transferred
bytes. Never use force-download, clear a cache or retry by discarding a partial.
Normal loading remains offline and local-files-only. Required no-download,
concurrent-transfer and unavailable-drive tests precede enabling a new path.

## Separate conversion gate

The source repository has no ONNX export. These source files alone do not
complete the native adapter or authorise additional downloads. The current
native OCR runtime and bundled Python environment lack PyTorch/Transformers.
The inspected package cache contains a Linux GPU PyTorch wheel, which is not a
Windows conversion runtime. Preserve it and the OCR environment.

Prepare pinned conversion tooling, exact missing dependencies and transfer
bytes separately; adopt any verified external cache non-destructively only
with the required authority. Export offline to `J:\Models`, record input/tool/
output hashes, and pass tokenizer/reference-logit/.NET provider parity before
using the output. Source acquisition is neither semantic-quality acceptance nor
a production-ready release.
