# File-type coverage

Coverage describes the current native pipeline. A recognised format can be
retained or deferred without having searchable extracted content. SQL source
and pipeline status expose the distinction; a filename extension alone does not
establish successful extraction.

| Input | Current route | Bounds and limitations |
| --- | --- | --- |
| UTF-8 text, Markdown, logs, CSV/TSV, JSON, XML and YAML | Bounded retained text, normalisation, indexing and publication. | Strict UTF-8 and binary-signature checks; ordinary text is capped at 16 MiB. |
| C# | Retained syntax, symbols, relationships, diagnostics and searchable projections. | No builds, dependency restore, execution or project-supplied generators/analyzers. |
| Other code languages | Recognised and explicitly deferred when no supported capability exists. | Do not infer semantic code analysis from extension recognition. |
| ZIP and TAR | Bounded archive expansion using retained members and durable processor branches. | Path traversal, links, excessive expansion and unsupported members are refused or deferred. |
| DOCX, XLSX and PPTX | Office Open XML structural extraction under the original document identity. | One logical document; package members are not corpus entries. Extracted text is capped at 200 MiB within package security bounds. |
| PDF with native text | Page extraction, normalisation and one original-document publication. | Retained page provenance; encrypted or invalid inputs refuse processing. |
| PDF pages with visible content but no native text | Provisioned English OCR through the fenced GPU scheduler. | Requires verified local models and explicit runtime activation. |
| JPEG and PNG | Provisioned single-frame English OCR with encoded-orientation handling. | 64 MiB, 25 megapixels and a 6,000-pixel maximum edge. |
| VSDX | Interactive Visio extraction through the logged-in companion. | Installed Visio required; refuses an existing user session; one logical document with page/shape provenance. |
| Binary Office DOC, XLS and PPT | Retained with an explicit parser-unavailable state. | No supported binary Office text parser is currently registered. |
| Other recognised image, audio and video formats | Bounded metadata extraction where a capability is registered; content extraction may remain deferred. | No general speech transcription, video understanding or image-captioning capability is promised. |
| Unknown, malformed, protected or policy-denied content | Named refusal or deferred state. | No silent coercion into text or unrestricted external parser fallback. |

English OCR has known repeated-text fidelity limits. A page with both native
text and scanned regions is not currently region-OCRed. Arabic OCR is outside
the supported scope. Page/block fields are retained internally but not exposed
in current public search responses. Reprocessing a failed terminal revision is
idempotent and is not a same-revision retry facility.

See [architecture](architecture.md), the
[practical OCR assessment](operations/2026-09-20-english-ocr-practical-assessment.md)
and [roadmap](roadmap.md) for the evidence and remaining work.
