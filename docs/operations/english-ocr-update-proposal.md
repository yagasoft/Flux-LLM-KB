# English OCR updates before benchmarking

Date: 2026-09-19
Status: approved Tesseract payloads acquired and verified; not a production release

The [active design](../superpowers/specs/2026-09-19-visio-ocr-delivery-design.md)
and [plan](../superpowers/plans/2026-09-19-visio-ocr-delivery.md) are English-only.
Arabic OCR, language routing and Arabic model updates are excluded. Preserve those
cached assets. This request does not select the production OCR implementation or
claim a quality result.

## Already current: no replacement downloads

All twelve files in the six English-relevant central Paddle ONNX component
manifests passed fresh SHA-256/length checks. Their pinned revisions equal the
current published revisions of the same upstream repositories:

| Repository under `PaddlePaddle` | Immutable revision |
| --- | --- |
| `PP-OCRv6_medium_det_onnx` | `61323801669c338b7891481ec7bac61ce31b576a` |
| `PP-OCRv6_medium_rec_onnx` | `50c7eacafc52fa7bcf4194e8cd08e46f8558504b` |
| `PP-LCNet_x1_0_doc_ori_onnx` | `7330ab7039123e46af2dc03154b9969aa412c61d` |
| `PP-LCNet_x1_0_textline_ori_onnx` | `7fdcf3cf7061163eda7183b224aa334bd33068f7` |
| `PP-DocLayout_plus-L_onnx` | `feb74619326f634e0e883218598096a3733ad9f7` |
| `SLANet_plus_onnx` | `7dbe640e127602bf506815e822c09758de73c482` |

These are pipeline components, not six alternative OCR systems or a complete
PP-StructureV3 installation. New successor models and variants are separate
candidate changes, not permission to replace these files.

The twenty runtime files in the five inventoried legacy Paddle bundles were also
rehashed locally and compared with current upstream identities: both PP-OCRv5
server models, both orientation models and UVDoc need no payload replacement.
The text-line orientation repository's newer HEAD changes its README, not these
runtime files. Do not redownload unchanged weights because that repository SHA
changed, and do not adopt legacy bundles before they are needed by the selected
supported candidate.

Installed Tesseract English and orientation data also match current
`tesseract-ocr/tessdata_fast` revision
`87416418657359cb625c412a48b6e1d6d41c29bd`, including upstream Git blob identities.
The engine is older (`5.4.0.20240606`). The current stable engine is
[5.5.3](https://github.com/tesseract-ocr/tesseract/releases/tag/5.5.3).
The separately proposed `tessdata_best` English file is an accuracy variant,
not a newer revision of the existing fast model. The upstream
[data-file documentation](https://tesseract-ocr.github.io/tessdoc/Data-Files.html)
distinguishes these variants; local benchmark results must decide between them.

## Approved payloads, now cached

| Artifact | Immutable identity | Transfer bytes |
| --- | --- | ---: |
| `tesseract-ocr-w64-setup-5.5.3.20260724.exe` | Release `5.5.3`; SHA-256 `bee9e3434bd94fd65387d9be28cd467a41f61b1275383b55b0f59a1331270ae4` | 26,573,224 |
| `tesseract-ocr/tessdata_best/eng.traineddata` | Revision `e12c65a915945e4c28e237a9b52bc4a8f39a0cec`; Git blob `176dc3220de7db34d3b3aecbfa42043a6038348b`; verified SHA-256 `8280aed0782fe27257a68ea10fe7ef324ca0f8d85bd2fd145d1c2b560bcb66ba` | 15,400,601 |
| **Actual payload transferred** | Excludes protocol overhead | **41,973,825** |

The upstream Git tree supplied the English file's byte length and immutable Git
blob identity, not a published SHA-256. The approved transfer matched both; the
computed SHA-256 above is now recorded in the immutable local manifest/receipt.
Both payloads were published to the canonical content-addressed store. The native
`models verify --manifest` command then accepted the English file and persisted a
verification receipt. These existing files must be reused, never downloaded again
because of a context or checkout change. No mutable branch URL is acceptable.

Sources are the release asset linked above and the exact model revision at
`https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/e12c65a915945e4c28e237a9b52bc4a8f39a0cec/eng.traineddata`.

Checks covered `J:\Models` and its inventory, relevant profile/provider caches,
`E:\Temp\pip-cache`, `E:\FluxPackageCache`, `E:\Codex Workspaces`, and the user
Downloads directory. Neither missing artifact was found. Existing Tesseract data
and the legacy Paddle inventory were inspected separately. Keep detailed private
inventory outside Git.

## Zero-download reuse and destinations

The proposed non-destructive adoption is complete: these already-installed files
were verified and copied into the central store, with zero download bytes. Their
originals and other applications' settings remain unchanged:

| File | Byte length | SHA-256 |
| --- | ---: | --- |
| Fast `eng.traineddata` | 4,113,088 | `7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2` |
| `osd.traineddata` | 10,562,727 | `9cf5d576fcc47564f11265841e5ca839001e7e6f38ff7f7aacf46d15a96b00ff` |

- All acquisition staging: `J:\Models\staging\english-ocr-update-20260919\`.
- Verified models: existing `J:\Models\artifacts\sha256\<sha256>\<filename>` layout.
- Manifests: `J:\Models\manifests\english-ocr-update-20260919\`; immutable receipts:
  `J:\Models\inventory\`. Recheck under per-artifact locks before every transfer
  or adoption; unchanged cached bytes are never downloaded again.
- The verified Tesseract executable/runtime libraries are extracted to the new,
  side-by-side `J:\Models\runtimes\tesseract\5.5.3.20260724\` directory:
  56 files, 97,632,889 bytes, all rehashed against the extraction receipt.
  `--version` reports `tesseract v5.5.3.20260724`. No traineddata/configuration
  assets were extracted from the installer. The machine's existing Tesseract and
  global cache settings are unchanged.
- **Do not execute the installer.** Its
  [pinned NSIS source](https://raw.githubusercontent.com/tesseract-ocr/tesseract/5.5.3/nsis/tesseract.nsi)
  contains install-time traineddata downloads from a mutable branch. Use the
  installed 7-Zip to inspect/extract approved runtime files without running that
  logic. Stop if the archive cannot provide a self-contained runtime this way.
- No force download, retry loop, cache purge, provider loader, other-language
  download, fallback drive or implicit package/model acquisition. Keep partial
  work under J: and record actual transferred bytes. Refuse if J: is unsafe,
  unavailable, unwritable or lacks space.

## Complete-candidate prerequisites remain explicit

This small request updates the Tesseract benchmark baseline and permits comparing
the fast/best English variants. It does **not** provide a table reconstruction
engine or make Tesseract the production winner.

Official PP-StructureV3 needs its supported runtime and complete structural model
set; six cached ONNX roles do not establish completeness. Syncfusion Smart Data
Extractor/Smart Table Extractor are not installed; current NuGet metadata reports
`34.2.8`. Their documented default copies bundled ONNX models into build/publish
output, which conflicts with this application's J-only rule. Establish a supported
J-only loading arrangement and exact missing payload inventory before acquiring
those packages; do not silently restore model-containing packages into a profile
cache. Missing installation alone is not an accuracy rejection. PaddleOCR-VL
remains a justified challenger, not an automatic multi-gigabyte download.

The next bounded request is now the [exact CPU/ONNX acquisition proposal](english-ocr-onnx-acquisition-proposal.md),
which reuses compatible cached models. The provisional native-Paddle/GPU route
is not requested. Merely having optional models in an upstream catalogue does
not authorise acquiring them.

Both updated Tesseract variants have completed the same 30-page English synthetic
screening; [measured results and commands](2026-09-19-english-ocr-screening.md)
show no complete quality pass. No production winner, integration, deployment or
live OCR verification is claimed. Real, independently labelled English scans and
all retained release thresholds still apply.

## Public material for the release corpus

The user authorised searching for more challenging test material. These are
candidate sources, not already-validated release fixtures:

- [PubTabNet](https://github.com/ibm-aur-nlp/PubTabNet) provides scientific table
  images, cell text/boxes and HTML structure. Its repository has twenty examples,
  avoiding a bulk corpus download. The authors identify the source as the PMC
  Open Access commercial-use collection; retain the annotation licence and
  underlying image attribution. Digital-born table images do not prove scan or
  handwriting accuracy.
- [FUNSD](https://github.com/crcresearch/FUNSD) provides noisy real English form
  scans, transcribed words, boxes and entity relations. Its
  [data terms](https://guillaumejaume.github.io/FUNSD/work/) restrict use to
  non-commercial research/education and require agreement. Do not treat code
  access as unrestricted data permission. It does not supply table-cell truth.
- [OmniDocBench](https://github.com/opendatalab/OmniDocBench) supplies richer
  text, reading-order and table annotations across varied document types,
  including notes. Use only English-labelled pages. The dataset's research-only,
  non-commercial terms are separate from the code's Apache licence.

Select a small reusable subset, check rendered pages and critical numbers against
annotations independently, and freeze source revision/hash plus adjudicated truth
before candidate comparison. Keep downloaded documents and detailed results
outside Git. Repository examples are smoke tests, not evidence of an unseen test
set; prefer held-out splits and record any unknown upstream training exposure.
No source above has yet contributed a measured score, and no dataset terms have
been accepted on the user's behalf.
