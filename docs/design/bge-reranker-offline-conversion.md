# BGE reranker offline conversion and cache adoption

Prepared: 27 September 2026. This is local implementation work within the
approved hybrid retrieval plan. No further acquisition is needed for the
inspected conversion configuration. No production or GPU activation is implied.

## Non-destructive adoption proposal

All 31 dependencies for a Python 3.12 Windows x64 CPU conversion environment
were found in existing caches. Their exact filenames, lengths and SHA-256
values match [PyPI metadata](https://pypi.org). Twenty-four wheels already reside
in the central model store. The seven external-cache wheels below are proposed
for non-destructive adoption into `J:\Models\artifacts\sha256`, then an isolated
wheelhouse/runtime under `J:\Models`. Preserve the originals and their consumers.
Do not change the OCR runtime or machine-wide cache settings.

| Cached wheel | Bytes | SHA-256 |
| --- | ---: | --- |
| `huggingface_hub-0.36.2-py3-none-any.whl` | 566,395 | `48f0c8eac16145dfce371e9d2d7772854a4f591bcb56c9cf548accf531d54270` |
| `mpmath-1.3.0-py3-none-any.whl` | 536,198 | `a0b2b9fe80bbcd81a6647ff13108738cfb482d481d826cc0e02f5b35e5c88d2c` |
| `setuptools-80.9.0-py3-none-any.whl` | 1,201,486 | `062d34222ad13e0cc312a4c02d73f059e86a4acbfbdea8f8f76b28c99f306922` |
| `sympy-1.14.0-py3-none-any.whl` | 6,299,353 | `e091cc3e99d2141a0ba2847328f5479b05d94a6635cb96148ccb3f34671bd8f5` |
| `tokenizers-0.22.2-cp39-abi3-win_amd64.whl` | 2,747,786 | `c9ea31edff2968b44a88f97d784c2f16dc0729b8b143ed004699ebca91f05c48` |
| `torch-2.12.1-cp312-cp312-win_amd64.whl` | 122,988,095 | `e86550597877fb272ddc52db2f85b82cb601ea7bd932576a0340152cae2200b3` |
| `transformers-4.57.6-py3-none-any.whl` | 11,993,498 | `4c9e9de11333ddfe5114bc872c9f370509198acf0b87a832a0ab9458e2bd0550` |

The Transformers wheel is in `E:\FluxPackageCache`; the other six are verified
opaque bodies in the configured pip cache `E:\Temp\pip-cache`. Additional
network transfer: **zero bytes**. This is not permission for new downloads.
Any missing or changed cache artifact stops setup; do not substitute a version
or fetch a replacement. Detailed inventory, source paths, dependency closure and
upstream identities remain under
`J:\Models\manifests\bge-reranker-source-20260927`.

Transformers 4.57.6 requires tokenizers at most 0.23.0 and Hugging Face Hub below
1.0. Use the compatible cached versions above, preserving the newer OCR versions.
The cached Linux GPU PyTorch wheel is not used. The Windows PyTorch wheel supports
the CPU-only conversion placement. Source precision remains float32.

## Local setup and export contract

1. Recheck each source under a per-artifact lock. Copy external hits to unique
   staging under J:, verify the complete hash/length, then publish atomically
   without overwriting. Retain adoption receipts and preserve all originals.
2. Create a dedicated runtime at
   `J:\Models\runtimes\bge-reranker-conversion-2.12.1-cp312`. Install the entire
   frozen 31-wheel closure with no index, no dependency resolution and mandatory
   hashes. Run dependency checks; no model import can remedy a setup failure by
   acquiring content.
3. Set per-process offline modes and explicit J: cache roots before any model
   import. Load the verified source bundle with local-files-only mode, disabled
   remote code and the explicit CPU device. Do not create a GPU session.
4. Validate untruncated query/passage pair tokenisation. Export full-precision
   classification logits to ONNX under J:, with supported dynamic batch/sequence
   dimensions and a bounded 512-token runtime input profile. Reject an oversized
   complete pair; never silently truncate it. Keep external tensor data under J:.
5. Record source, script, Python/package versions, graph and external-data hashes.
   Compare CPU reference and ONNX logits across public synthetic English pairs,
   Unicode, padding and input lengths. Preserve reference fixtures outside Git
   when they contain reusable model/tokenizer outputs.
6. Native .NET tokenisation/provider parity and scheduler-owned GPU execution
   remain separate milestone-2 checks. This export does not activate the search
   provider, establish local relevance superiority or justify model retention.

Conversion uses CPU so it does not acquire the OCR GPU slot. Search remains on
the approved scheduler-owned GPU design; conversion placement does not change
that decision. The model manifest/profile remains fixed across device checks.

## Observed local results

The seven external wheels were copied non-destructively and all 31 pinned wheels
installed offline into the isolated runtime. Network transfer was zero. Four
synthetic adoption tests passed, including changed-source refusal, no overwrite
and preserved originals. The complete dependency check passed. Runtime imports,
source model loading, reference inference and export ran with socket connections
blocked; no network attempts occurred. PyTorch reports a CPU-only build.

Eight public synthetic English pairs produced finite scalar logits, and the
explicit XLM-R pair assembly matched the official tokenizer IDs. An oversized
pair remained untruncated during length detection. The float32 ONNX export uses
opset 17 and one external-data file. CPU ONNX logits matched the source model for
batch sizes 1, 2, 4 and 8, padding and a 289-token pair. Maximum absolute logit
error was **0.0000152587890625**. This is numeric/reference evidence, not corpus
retrieval accuracy or full-search latency acceptance.

| Export file | Bytes | SHA-256 |
| --- | ---: | --- |
| `model.onnx` | 550,237 | `a4d1c276f48200935f10d134b4c5c21233727cafc88a4ea7fb9c40ce10b63478` |
| `model.onnx_data` | 2,271,023,108 | `fe17b9d4a11cafc0680c183ac3b704b14f2f935514a8527a4423013f0f27325e` |

The export identity is
`f083b9dbba6d1b56869a6572e5d7f9f5fd17f961d3c521ea2424d80c5f800580`,
under the pinned upstream revision in
`J:\Models\bundles\bge-reranker-v2-m3-onnx`. Scripts, complete model/runtime
manifests, fixtures, logs, adoption receipts and export receipts remain under J:.

The pinned TorchScript export path emitted a PyTorch deprecation notice. It was
used explicitly for the opset-17 graph; the checker and reference parity passed.
The notice is retained in `offline-export.log`, not hidden. It concerns future
export tooling and does not change the exported graph or application build.
Subsequent [native CPU adapter verification](bge-native-cpu-adapters.md) passed
.NET tokenizer/runtime parity and the exact 512-token boundary. GPU memory/release,
50-candidate latency and scheduler integration remain unaccepted. Keeping models
loaded between requests remains conditional on the later full-search evidence.
