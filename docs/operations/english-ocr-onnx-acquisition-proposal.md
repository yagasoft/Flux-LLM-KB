# Exact English ONNX benchmark acquisition

Date: 2026-09-19
Status: the user approved this exact file list, byte count and isolated offline
setup on 2026-09-20. Recheck caches and acquire only still-missing approved files.
This approval does not select or activate a production processor.

## Purpose and boundary

Run the supported PP-StructureV3 pipeline as the next complete English accuracy
candidate, using PaddleOCR 3.7.0, PaddleX 3.7.2 and ONNX Runtime 1.30.0 on Windows
CPython 3.12, CPU. Reuse the verified English v6 detector/recogniser, document/line
orientation, layout and wireless-table models already cached centrally. This is
not a new production processor selection or activation.

The direct ONNX engine is documented independently of HPI. This comparison needs
no CUDA, Paddle GPU, Paddle CPU, Torch, Docker or WSL installation. The earlier
provisional native-Paddle/GPU inventory is **not requested**. GPU placement,
numerical consistency and performance remain later checks if quality passes.

Use explicit verified local models and batch size one. Enable page/line
orientation and table/region recognition. Explicitly disable formula, chart,
seal and document-unwarping models; none is silently acquired. Preserve headers,
footnotes and numbers in scored output rather than using the default Markdown
ignore list. No Arabic OCR or additional recognition variant is included.

## Exact payload request

| Missing structural bundle under `PaddlePaddle` | Immutable revision | Bytes for `inference.onnx` + `inference.yml` |
| --- | --- | ---: |
| `PP-DocBlockLayout_onnx` | `bcbfaadf1a9b7197def8c01337cd5bbbef261fbd` | 129,355,080 |
| `PP-LCNet_x1_0_table_cls_onnx` | `605f623b09f67a562bae77e781d5d5266f14905a` | 6,778,579 |
| `SLANeXt_wired_onnx` | `04356de883011f433f83e5098793f3a501a9af6e` | 367,745,439 |
| `RT-DETR-L_wired_table_cell_det_onnx` | `b2c0720b5fe6f1c0dd40f8a7993a3f28e04252f8` | 129,355,091 |
| `RT-DETR-L_wireless_table_cell_det_onnx` | `94c021be206064f0136ef1383fbd4b68b168fa61` | 129,355,094 |
| **10 model/configuration files** | [Individual hashes and lengths](english-ocr-ppstructure-onnx-artifacts.draft.json) | **762,589,283** |
| **97 missing Windows/Python wheels** | [Exact filenames, versions, URLs, SHA-256 and lengths](english-ocr-paddle-runtime.draft.json) | **183,804,964** |
| **Total missing payload** | Excludes protocol overhead | **946,394,247** |

That is 0.946 GB (0.881 GiB), not the provisional 3.31 GB native/GPU route. Each
model URL is `https://huggingface.co/<repository>/resolve/<revision>/<filename>`
using only the immutable values in the linked model list. Model payloads have
upstream SHA-256 identities; small YAML files have upstream Git blob identities.
Validate those and lengths, then record computed SHA-256 for every file before
publishing an immutable native manifest. Never use a mutable branch download.

Pinned PaddleX 3.7.2 source revision
`ffb64904d23708863ff5b8da312a5cbd52a7f462` uses cached `SLANet_plus` for wireless
table structure. The optional `SLANeXt_wireless` is **not required or requested**.
The [pinned pipeline configuration](https://raw.githubusercontent.com/PaddlePaddle/PaddleX/ffb64904d23708863ff5b8da312a5cbd52a7f462/paddlex/configs/pipelines/PP-StructureV3.yaml)
is the basis for this distinction; advertising a model is not evidence that the
pipeline must acquire it.

## Cache reuse, destinations and permitted setup

Checks covered J and its inventory, relevant legacy Paddle/Hugging Face caches,
`E:\Temp\pip-cache`, `E:\FluxPackageCache` and `E:\Codex Workspaces`. Six existing
English-relevant ONNX bundles (289,829,008 bytes) need no replacement. Preserve
cached Arabic assets without loading or updating them. Reuse the three verified
wheel-cache hits, `h11` 0.16.0, `httpcore` 1.0.9 and `httpx` 0.28.1: 189,816 local
bytes, zero download bytes. Their originals remain untouched.

After exact approval, recheck all canonical files, relevant caches and partials
under per-artifact locks. Transfer only still-missing bytes; verify and atomically
publish new files. No force download, automatic dependency acquisition, cache
clearing, overwrite or fallback drive is permitted. Stop on an unavailable,
unsafe, unwritable or space-constrained J: drive. Retain actual transfer receipts.

- Staging: `J:\Models\staging\english-ocr-paddle-onnx-cpu-20260919\`.
- Verified files: `J:\Models\artifacts\sha256\<sha256>\<filename>`.
- Manifests: `J:\Models\manifests\english-ocr-paddle-onnx-cpu-20260919\`.
- Immutable receipts: `J:\Models\inventory\`.
- Isolated runtime: `J:\Models\runtimes\ppstructurev3-3.7.0-ort-1.30.0-cp312\`.

The requested approval includes non-destructive local reuse/assembly beneath J:
and isolated offline runtime setup for this benchmark only, using the listed
verified wheels with no-index/no-dependency acquisition. It does not change the
machine's Python, another application's cache, production configuration, SQL,
IIS or source data. Any consumer layout must retain no-follow verification and
held read leases for the actual files consumed; an alias is not verification.

## Remaining checks, not assumed successes

Metadata resolves the required Python packages, not executable compatibility or
accuracy. The exact complete ONNX pipeline and the cached v6 configuration have
not yet run. Inspect package code and exercise explicit offline/cache-refusal
tests before loading models. Network client libraries pulled by the documented
extras do not authorise provider access, remote processing or model acquisition.

If imports or inference require an unlisted artifact, runtime or workaround,
stop and report it; do not install or download it automatically. Retain failures
as failures. The [measured Tesseract baseline](2026-09-19-english-ocr-screening.md)
does not select a winner. Complete independently checked English accuracy,
ownership, release and scoped live verification gates still apply.
