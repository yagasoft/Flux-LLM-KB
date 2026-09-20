# Document OCR acquisition proposal

Date: 2026-09-19
Status: awaiting explicit acquisition/adoption approval; not an executable model manifest

This public acquisition specification records the exact candidates already selected
in the [approved design](../superpowers/specs/2026-09-18-document-processors-design.md).
It contains no private cache inventory, licence material or model payloads. Metadata
was checked through the upstream repository tree API at the revisions below, without
fetching model or configuration payloads. Compatibility and OCR quality remain
unmeasured; this document is not deployment approval.

## Proposed operation

- Acquire only the seven ONNX files and five missing YAML files listed below:
  **297,832,619 payload bytes** (297.83 MB; 284.04 MiB), before protocol overhead.
- Reuse verified existing copies of the two orientation YAML files: **1,501 bytes**,
  copied non-destructively after approval; do not download these files.
- Final payload total: **297,834,120 bytes**, plus small local manifests and receipts.
- Destination is exclusively `J:\Models`. Final files use the existing gate layout:
  `J:\Models\artifacts\sha256\<file-sha256>\<filename>`.
- Any incomplete transfer stays in `J:\Models\staging\document-ocr-20260919\<role>\`.
  Manifests go under `J:\Models\manifests\document-ocr-20260919\`; immutable
  acquisition/verification receipts go under `J:\Models\inventory\`.
- No other variants, conversions, provider activation or application deployment
  are included. No full-repository clone, provider download helper or runtime loader
  is needed for this one-time operator action.

The transfer amount is an upper bound for this exact allowlist, not authority to
download duplicates. Immediately before acquisition, recheck the central inventory
and relevant legacy caches under per-artifact locks. Reuse exact verified content,
preserve partial work where safely supported, and transfer only missing content.
If an identity, length, cache state, dependency or destination check changes, stop.
Do not overwrite existing files, clean caches, fall back to another drive, or run
automatic full-download retry loops. Preserve legacy originals and their consumers.

## Exact upstream revisions

All repositories are under the official `PaddlePaddle` organisation on Hugging Face.
Each role has its own manifest/lease because the component filenames repeat.

| Role | Repository | Immutable revision |
| --- | --- | --- |
| Text detection | [PP-OCRv6_medium_det_onnx](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_det_onnx) | `61323801669c338b7891481ec7bac61ce31b576a` |
| English recognition | [PP-OCRv6_medium_rec_onnx](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec_onnx) | `50c7eacafc52fa7bcf4194e8cd08e46f8558504b` |
| Arabic recognition | [arabic_PP-OCRv5_mobile_rec_onnx](https://huggingface.co/PaddlePaddle/arabic_PP-OCRv5_mobile_rec_onnx) | `14aaedcd75825982689ecf5cd64ab33ee083215a` |
| Page orientation | [PP-LCNet_x1_0_doc_ori_onnx](https://huggingface.co/PaddlePaddle/PP-LCNet_x1_0_doc_ori_onnx) | `7330ab7039123e46af2dc03154b9969aa412c61d` |
| Line orientation | [PP-LCNet_x1_0_textline_ori_onnx](https://huggingface.co/PaddlePaddle/PP-LCNet_x1_0_textline_ori_onnx) | `7fdcf3cf7061163eda7183b224aa334bd33068f7` |
| Layout | [PP-DocLayout_plus-L_onnx](https://huggingface.co/PaddlePaddle/PP-DocLayout_plus-L_onnx) | `feb74619326f634e0e883218598096a3733ad9f7` |
| Tables | [SLANet_plus_onnx](https://huggingface.co/PaddlePaddle/SLANet_plus_onnx) | `7dbe640e127602bf506815e822c09758de73c482` |

The machine-readable metadata endpoint for each row is
`https://huggingface.co/api/models/PaddlePaddle/<repository>/tree/<revision>?recursive=true&expand=false`.
Resolve downloads only at the immutable revision, never `main` or `latest`.

## ONNX payload allowlist

Each file is exactly `inference.onnx` in the corresponding repository/revision above.
The SHA-256 values below are the upstream LFS object identities, not Git blob IDs or
Xet hashes. These are the selected upstream exports, not locally converted or
quantised variants. Inspect their actual tensor types and graph dependencies before
admission; the filename alone does not prove precision or runtime compatibility.

| Role | Bytes | SHA-256 |
| --- | ---: | --- |
| Text detection | 62,032,837 | `eb13b44b25bb36f89528b68720af8a61d9cf381176107f465db1757b65d086e1` |
| English recognition | 76,554,979 | `9c09abf0957f7968c7586464b7397b84ad2387a0497a351af40e9acc71b673ba` |
| Arabic recognition | 7,998,947 | `799113ebf267fbe742deb99eb36e8d42c9ddc5291ceacf92add41b4d52a59110` |
| Page orientation | 6,788,069 | `af9a0a4f317ff0709ce752067807f819cb15d883f8ecad89f28df1c6ee2d9c92` |
| Line orientation | 6,777,816 | `38aa97cd4be591e0ad304e659f07ba30d946f27a63315433f6659c69c8778345` |
| Layout | 129,736,329 | `77afb2caa74dd13240d087d2eced91d7fcd2caebd16006a0a66162fc8707ff0e` |
| Tables | 7,782,138 | `7790c0c13ce064782c9d22ebeb16b4da8216f83d3ba576da962c106ef58386da` |
| Total | 297,671,115 | |

## Required companion allowlist

Each file is exactly `inference.yml` in the same repository/revision as its role.
These settings/decoder resources are model dependencies, not optional downloads.

| Role | Bytes | Upstream Git blob SHA-1 | Proposed action |
| --- | ---: | --- | --- |
| Text detection | 886 | `1c5c05809877e4c7385f899019fff0ac9017ca80` | Acquire |
| English recognition | 150,580 | `c53a96fcd315a86cb4748d2746f3d90941e1c6d8` | Acquire |
| Arabic recognition | 6,165 | `bfc972d4c07cf0654e07ed9451a3868d325b95b9` | Acquire |
| Page orientation | 766 | `be5ff4a823ed0dc4fb6fa8e228608ba9ab9fc01d` | Reuse verified existing copy |
| Line orientation | 735 | `61977592412f3529cf153ed9c904f6700cb8b9a6` | Reuse verified existing copy |
| Layout | 1,838 | `9a236587eae068a1e7906fd158f712dc41400563` | Acquire |
| Tables | 2,035 | `9d57e936f02d62335817f936d6e779e811f56d2a` | Acquire |

Verified SHA-256 identities for the two reusable files:

- Page orientation: `9e195eb729a8173588cd0e8a852c8b373aa606e79e77b4ac7d8346f5426caf26`.
- Line orientation: `8d5120d0e1a30a9df7ed46aa9119da3796ed066777089d1c1d705f132d5e90f9`.

### Explicit approval detail: five companion checksums

Upstream tree metadata exposes Git blob SHA-1 and length, but not SHA-256, for
the five missing YAML files. Their SHA-256 values are therefore **not yet known**.
Approval is requested explicitly for this verification sequence: acquire these
five exact immutable-revision files (161,504 bytes total), verify their byte length
and Git blob identity, then compute and record SHA-256 before atomic publication
into the content-addressed store or creation of a successful gate receipt. Compute
the Git identity over `blob <decimal-byte-length>\0` followed by the unchanged file
bytes; it is not plain file SHA-1.

This does not weaken the runtime manifest: it must still contain a verified
SHA-256 and byte length for every file. Do not substitute Git SHA-1 for SHA-256,
invent checksums, issue incomplete manifests, or load any unverified file. Until
the user approves this sequence, do not fetch even these small companions.

## Dependency and activation boundary

The [official ONNX deployment documentation](https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/inference_deployment/cross_platform/browser.en.md)
identifies `inference.onnx` and `inference.yml` as ONNX model resources. This proposal
excludes `.gitattributes`, model cards and the Paddle `inference.json` graph files
present in the two v6 repositories. It does not acquire another runtime or a
conversion toolchain.

A repository listing is not proof of a self-contained ONNX graph. Before any
provider loading, check the acquired graphs for external tensor data and custom
operator requirements, and check YAML for unresolved decoder/config dependencies.
Unexpected dependencies stop admission and require a revised exact-file proposal;
they must never trigger a download. Runtime compatibility and accuracy must still
pass the existing separately gated offline tests before deployment. Acquiring the
files is not proof that OCR works.

No model file has been downloaded, copied, moved, converted or loaded as part of
preparing this proposal. `J:\Models` has not been modified.
