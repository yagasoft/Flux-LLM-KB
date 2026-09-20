# Missing ONNX preflight dependency

Status: acquired and installed offline on 2026-09-19. Both approved wheels were transferred once into the canonical J-drive store, then independently rechecked by byte length and SHA-256 before a `--no-index --no-deps` install into the isolated runtime. The immutable J-drive receipts record 8,311,530 network bytes. The schema-preflight implementation and its focused safety tests remain pending; this acquisition does not authorise a real-model run by itself.

The approved CPU/ONNX bundle omitted the schema parser needed for a retained safety invariant. ONNX Runtime 1.30.0 can resolve external tensor data relative to the working directory even when supplied model bytes. The pinned implementation explicitly documents and implements that behaviour: [external-data resolution](https://github.com/microsoft/onnxruntime/blob/v1.30.0/onnxruntime/core/framework/tensorprotoutils.cc#L441). A failed malformed-graph experiment is not evidence that external data is rejected.

The independent review requires a small parse-only preflight using the official ONNX schema, not a hand-written model parser or string scanning. Recursively reject external tensor declarations, including nested attributes and sparse tensors, before ORT construction. Use fresh restricted session options, exact verified paths and CPU-only execution; no custom-library registration, external initialisers, conversion or model export. Prove refusal with valid external-initialiser and external-attribute fixtures plus an inline positive control. The current probe refuses `onnx-schema-preflight-required` before provider imports.

## Exact request

| Missing wheel | Bytes |
| --- | ---: |
| `onnx-1.23.0-cp312-abi3-win_amd64.whl` | 7,872,197 |
| `ml_dtypes-0.6.0-cp312-cp312-win_amd64.whl` | 439,333 |
| **Total** | **8,311,530** |

These are parser/runtime dependencies, not additional OCR models. ONNX requires `ml_dtypes`; the other required packages are already installed at compatible versions: NumPy 2.3.5, protobuf 7.36.2 and typing_extensions 4.16.0. No extras, upgrades, GPU packages or dependency resolver are requested. Official [ONNX loading documentation](https://onnx.ai/onnx/api/serialization.html) supports parsing a `ModelProto` from bytes without acquiring tensor data; inference remains a separate guarded operation.

[The exact acquisition specification](english-ocr-onnx-preflight-acquisition.json) records immutable SHA-256 identities, byte lengths, release commits, PyPI URLs and publisher metadata. SHA-256 values:

- ONNX: `70a2f930b221f9dbdff62704838ce8bf81442787b25048f8b9cdc9118168799e`
- ml_dtypes: `2a3e9d53925597fbffafd2a37048dadeddd0bdaba58058f6ae0869ed709a184d`

Destination: `J:\Models\artifacts\sha256\<hash>\<filename>`, with staging and immutable receipts under J. Install only the verified local wheels into the existing `J:\Models\runtimes\ppstructurev3-3.7.0-ort-1.30.0-cp312` environment, offline and without dependency acquisition. Recheck all relevant caches under each artifact lock immediately before any authorised transfer; never replace or redownload a cache hit.

The bounded inventory checked J and its manifests, legacy PaddleX/PaddleOCR/Hugging Face caches, `E:\Temp\pip-cache`, `E:\FluxPackageCache`, `E:\Codex Workspaces`, Miniconda and Codex Python site-packages. It found the existing protobuf runtime, but no official ONNX generated schema/parser or compatible ONNX/ml_dtypes wheel. No cache adoption, model download or other-drive fallback is requested.

Complete this preflight and the remaining probe safety tests, then resume the same accuracy comparison. This does not approve a production OCR candidate, change the quality thresholds or justify deployment before the quality gate passes. The earlier public smoke is preserved as unqualified exploratory evidence; it is not a compliant release run.
