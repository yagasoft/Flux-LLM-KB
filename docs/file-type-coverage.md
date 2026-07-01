# File Type Coverage Roadmap

Flux should assume that every watched file is useful at least as provenance.
The default floor is stable metadata: path, source root, size, timestamps, MIME
or signature, hashes, trust rank, deletion state, duplicate/version-family state,
and extraction status. Content extraction then escalates only when a safe local
parser or local tool exists.

For broad or important go-live roots, enable strict indexing. In that mode,
metadata-only outcomes are not treated as searchable indexed knowledge:
unsupported files must be excluded by glob policy or recorded as visible
`blocked_missing_dependency` work until the required local extractor is
installed.

## Support Tiers

- `metadata`: record file identity, provenance, hashes, MIME/signature, size,
  timestamps, and lifecycle state.
- `inline`: read small UTF/text-like files directly and chunk them immediately.
- `local_parser`: use bundled or optional Python libraries without cloud calls.
- `local_tool`: use installed local tools such as LibreOffice, Tesseract,
  ffprobe/ffmpeg, ExifTool, Pandoc, or domain-specific converters.
- `container`: safely enumerate or expand nested files with depth, size,
  file-count, and bomb-protection limits.
- `enriched`: deferred local OCR, transcription, frame sampling, image/diagram
  structural extraction, or semantic backfill.
- `sensitive_metadata`: record only safe metadata by default because the file is
  likely to contain credentials, private keys, or executable/binary payloads.

## Priority Rules

- Common business formats are first-class: `doc`, `docx`, `xls`, `xlsx`, `ppt`,
  `pptx`, `pdf`, `msg`, `eml`, `drawio`, and `vsdx`.
- Legacy Microsoft formats are not generic binaries. `doc`, `xls`, `ppt`, `vsd`,
  `mdb`, and related formats should use LibreOffice, Windows COM, or other local
  adapters when available, and otherwise report `blocked_missing_dependency`.
- Business document variants are extracted locally where possible: Office
  macro/template packages use existing Python parsers or LibreOffice conversion,
  OpenDocument text/spreadsheet/presentation files use LibreOffice conversion,
  and legacy Excel/PowerPoint binaries can fall back to Windows COM.
- Diagram formats should preserve structure where possible, not just OCR a
  screenshot. Draw.io XML and modern VSDX zipped XML are local structured
  extractors; legacy Visio formats still need local-tool adapters.
- Proprietary or unsafe formats still get metadata, hashes, duplicate/version
  grouping, and optional sidecar extraction.
- Cloud OCR, cloud transcription, and provider LLM calls are off by default.
- OCR is local and cache-backed when available. Deferred image jobs use
  Tesseract; PDFs first keep embedded text when present, then queue staged
  `corpus_extract_pdf_ocr_pages` jobs for scanned pages that need OCR. Those
  page batches render with `pdftoppm` and run Tesseract until all planned pages
  have been attempted. Missing OCR tools report `blocked_missing_dependency`,
  and redacted OCR cache entries stay under the private OCR cache root with
  hit/miss and per-page telemetry exposed through worker-family status.
- ASR is local and cache-backed when available. Deferred audio/video jobs prefer
  transcript sidecars, then use `ffprobe` to plan sequential
  `corpus_extract_media_segment` jobs. Each segment extracts temporary mono 16
  kHz audio with caller-side `ffmpeg` and transcribes through either the local
  OpenAI-compatible ASR service or the local faster-whisper fallback path until
  the full duration is covered. Production GPU deployments serve
  `large-v3-turbo` from the persistent `flux_llm_kb_asr_models` Docker volume;
  model download is an explicit deploy command, not part of extraction. Missing
  ASR tools, service readiness, service URLs, or model paths report
  `blocked_missing_dependency`; redacted ASR cache entries stay under the
  private ASR cache root with hit/miss and segment telemetry exposed through
  worker-family status. Embedded media sidecar transcripts inside archives are
  used before probing or ASR tools.
- Optional local vision is cache-backed and uses configured local loopback or Docker
  host-gateway inference. Image and sampled video-frame descriptions run only when
  `acceleration.vision.enabled` is true, `acceleration.vision.model` is
  configured, and `acceleration.local_inference.*` points at a healthy local
  provider/runtime. Production defaults enable local inference and use
  `qwen3-vl:8b` with a short keepalive. The first implemented runtime path uses
  an Ollama-compatible API; Gemma-class local vision models can be selected
  through `acceleration.vision.model` when installed in the configured runtime.
  Redacted captions stay under the private vision cache, sampled frames stay
  under the private thumbnail cache, and decorative-image spacers are skipped
  before OCR or vision.
- Video frame sampling is enabled by default for video jobs. Direct extraction
  can still use local `ffmpeg` scene detection with
  `acceleration.video.scene_threshold`, sample up to
  `acceleration.video.frame_sample_count` transition frames, and use one midpoint
  fallback frame only when no transition is detected. Staged corpus video jobs
  enqueue `corpus_extract_video_frames` with a bounded timestamp set so frame
  vision cannot race ASR chunk writes for the same asset.

## Coverage Matrix

| Family | Extensions and names | Target tier |
| --- | --- | --- |
| Plain text and notes | `txt`, `text`, `md`, `markdown`, `mdown`, `mkd`, `rst`, `adoc`, `asciidoc`, `org`, `tex`, `ltx`, `log`, `out`, `err`, `trace`, `readme`, `license`, `copying`, `changelog`, `todo`, `notes` | inline |
| Code | `py`, `pyw`, `ipynb`, `js`, `mjs`, `cjs`, `ts`, `tsx`, `jsx`, `java`, `kt`, `kts`, `scala`, `cs`, `fs`, `fsx`, `vb`, `c`, `h`, `cpp`, `cc`, `cxx`, `hpp`, `go`, `rs`, `rb`, `php`, `swift`, `m`, `mm`, `r`, `R`, `jl`, `lua`, `pl`, `pm`, `dart`, `ex`, `exs`, `erl`, `hrl`, `clj`, `cljs`, `hs`, `ml`, `mli`, `nim`, `zig`, `sol` | inline |
| Shell and automation | `sh`, `bash`, `zsh`, `fish`, `ps1`, `psm1`, `psd1`, `bat`, `cmd`, `vbs`, `vba`, `ahk`, `applescript`, `makefile`, `mk`, `justfile`, `taskfile`, `psql`, `sql` | inline |
| Config and manifests | `json`, `jsonc`, `json5`, `yaml`, `yml`, `toml`, `ini`, `cfg`, `conf`, `properties`, `env.example`, `xml`, `plist`, `editorconfig`, `gitignore`, `dockerignore`, `npmrc`, `prettierrc`, `eslintrc`, `babelrc`, `browserslistrc` | inline |
| Developer specs | `openapi.json`, `openapi.yaml`, `swagger.json`, `graphql`, `gql`, `proto`, `thrift`, `avsc`, `raml`, `wsdl`, `xsd`, `dtd`, `patch`, `diff`, `rej` | inline/local_parser |
| Documents | `pdf`, `doc`, `dot`, `docx`, `docm`, `dotx`, `dotm`, `rtf`, `odt`, `ott`, `fodt`, `sxw`, `abw`, `wpd`, `wps`, `pages`, `xps`, `oxps`, `djvu`, `ps`, `eps`, `chm` | local_parser/local_tool |
| Publications and ebooks | `epub`, `mobi`, `azw`, `azw3`, `fb2`, `lit`, `cbz`, `cbr`, `cb7`, `cbt` | local_parser/local_tool/container |
| Scanned documents | image-only `pdf`, scanned `tiff`, scanned `png/jpg`, mixed text/OCR PDFs | local_parser plus enriched OCR |
| Spreadsheets | `csv`, `tsv`, `psv`, `ssv`, `xlsx`, `xlsm`, `xltx`, `xltm`, `xls`, `xlt`, `xlsb`, `ods`, `ots`, `fods`, `numbers`, `dif`, `slk`, `gnumeric` | inline/local_parser/local_tool |
| Presentations | `pptx`, `pptm`, `potx`, `potm`, `ppsx`, `ppsm`, `ppt`, `pot`, `pps`, `odp`, `otp`, `fodp`, `key` | local_parser/local_tool |
| Draw.io and diagrams | `drawio`, `dio`, `drawio.svg`, `drawio.png`, embedded draw.io XML, `mmd`, `mermaid`, `puml`, `plantuml`, `iuml`, `dot`, `gv`, `graphml`, `gml`, `cyjs`, `bpmn`, `dmn`, `excalidraw`, `xmind`, `mm`, `mmap` | local_parser/enriched |
| Visio | `vsdx`, `vsdm`, `vssx`, `vssm`, `vstx`, `vstm`, `vdx`, `vsd`, `vss`, `vst` | local_parser/local_tool |
| Images and raster assets | `png`, `jpg`, `jpeg`, `jpe`, `webp`, `gif`, `tif`, `tiff`, `bmp`, `dib`, `heic`, `heif`, `avif`, `ico`, `icns`, `jp2`, `j2k`, `raw`, `cr2`, `nef`, `arw`, `dng` | metadata/local_parser/enriched |
| Vector and design assets | `svg`, `ai`, `eps`, `pdf` vector pages, `psd`, `psb`, `xcf`, `afdesign`, `sketch`, `fig`, `xd`, `indd`, `idml`, `cdr` | metadata/local_tool/enriched |
| Audio | `mp3`, `wav`, `m4a`, `aac`, `flac`, `ogg`, `oga`, `opus`, `wma`, `aiff`, `aif`, `amr`, `mid`, `midi` | metadata/local_tool/enriched |
| Video | `mp4`, `m4v`, `mov`, `mkv`, `webm`, `avi`, `wmv`, `mpeg`, `mpg`, `ts`, `m2ts`, `mts`, `3gp`, `flv`, `ogv` | metadata/local_tool/enriched |
| Subtitles and transcripts | `srt`, `vtt`, `ass`, `ssa`, `ttml`, `dfxp`, `sbv`, transcript `txt/md/json` sidecars | inline/local_parser |
| Mail | `eml`, `msg`, `mbox`, `maildir`, `pst`, `ost`, RFC822 exports, MIME attachment trees | local_parser/local_tool |
| Calendar and contacts | `ics`, `ical`, `ifb`, `vcf`, `vcard` | local_parser |
| Chat and collaboration exports | Slack JSON/ZIP exports, Teams exports, Discord exports, Mattermost exports, Zoom/Webex/Meet transcripts, meeting chat logs, `har` browser captures | local_parser/container |
| Structured data | `jsonl`, `ndjson`, `parquet`, `avro`, `orc`, `feather`, `arrow`, `xml`, `rdf`, `ttl`, `nt`, `nq`, `jsonld`, `hcl`, `tfvars` | local_parser |
| Databases and data stores | `sqlite`, `sqlite3`, `db`, `duckdb`, `mdb`, `accdb`, `dbf`, `fdb`, `bak`, `dump`, `sql`, `sql.gz` | metadata/local_parser/local_tool |
| BI and analytics | `pbix`, `pbit`, `twb`, `twbx`, `hyper`, `tdsx`, `qvw`, `qvf`, exported CSV/XLSX/PDF reports | metadata/local_tool |
| Geospatial | `geojson`, `topojson`, `kml`, `kmz`, `gpx`, `shp`, `shx`, `dbf`, `prj`, `cpg`, `gpkg`, `mbtiles`, `qgs`, `qgz`, `tif`, `geotiff`, `las`, `laz`, `e57` | metadata/local_parser/local_tool |
| CAD, BIM, and 3D | `dwg`, `dxf`, `dgn`, `ifc`, `ifczip`, `rvt`, `rfa`, `skp`, `step`, `stp`, `iges`, `igs`, `stl`, `obj`, `fbx`, `dae`, `3ds`, `blend`, `gltf`, `glb`, `usd`, `usda`, `usdc`, `usdz`, `ply` | metadata/local_tool |
| Archives | `zip`, `7z`, `rar`, `tar`, `tgz`, `tar.gz`, `gz`, `bz2`, `xz`, `zst`, `lz4`, `cab`, `ar`, `cpio`, `iso`, `dmg` | container |
| Package containers | `jar`, `war`, `ear`, `apk`, `ipa`, `nupkg`, `whl`, `egg`, `gem`, `crate`, `deb`, `rpm`, npm `tgz`, VSIX, browser extension packages | container/metadata |
| Virtual disks and images | `vhd`, `vhdx`, `vmdk`, `qcow2`, `img`, `wim`, `esd` | metadata/local_tool |
| Security and compliance | `sarif`, `spdx`, `cyclonedx`, `cdx.json`, `nessus`, `nmap`, `burp`, `zap`, `pcap`, `pcapng`, `evtx`, audit logs, vulnerability scan exports | local_parser/metadata |
| Test and coverage | `junit.xml`, `trx`, `coverage.xml`, `cobertura.xml`, `lcov`, `gcov`, `profraw`, `profdata`, `tap`, `allure` exports | local_parser |
| Observability and ops | application logs, syslog, journald exports, `har`, OpenTelemetry JSON, trace exports, metrics CSV/JSON, Kubernetes manifests, Helm charts, Terraform plans/state metadata | inline/local_parser/sensitive_metadata |
| Scientific and ML | `rmd`, `qmd`, `mat`, `h5`, `hdf5`, `nc`, `netcdf`, `fits`, `npy`, `npz`, `pkl`, `pickle`, `joblib`, `onnx`, `pb`, `tflite`, `pt`, `pth`, `ckpt`, `safetensors`, `gguf` | metadata/local_parser |
| Fonts | `ttf`, `otf`, `woff`, `woff2`, `eot` | metadata |
| Executables and compiled artifacts | `exe`, `dll`, `msi`, `msp`, `sys`, `scr`, `com`, `elf`, `so`, `dylib`, `class`, `o`, `obj`, `pdb`, `wasm`, firmware images | sensitive_metadata |
| Secrets and keys | `pem`, `key`, `crt`, `cer`, `pfx`, `p12`, `jks`, `keystore`, `kdbx`, `age`, `gpg`, `pgp`, `.env`, private config files | sensitive_metadata |
| Unknown binaries | any unrecognized extension or signature | metadata |

## Extraction Notes

- Code and developer artifacts are indexed through normal corpus chunks plus
  parser metadata. Python files use local `ast` chunking for modules, classes,
  functions, methods, imports, calls, route decorators, class decorators, and
  inheritance. SQL, JavaScript, TypeScript, C#, frontend markup (`html`, `vue`,
  `svelte`, `astro`, `razor`), stylesheets (`css`, `scss`, `sass`, `less`),
  notebooks, generated-code markers, and common config/manifests use
  conservative local pattern parsing. Parser failures and unsupported code-like
  files fall back to redacted text chunks with sanitized `parser_status`
  metadata; they do not block crawl/watch loops.
- Oversized structured files use sample-first indexing where local parsing is
  reliable. CSV, TSV, PSV, SSV, JSON, JSONL, NDJSON, JSON-LD, and
  OpenPyXL-supported workbooks store a bounded schema/profile/sample chunk with
  columns, row-count estimate, sample row count, parse status, truncation state,
  and sheet metadata where relevant; they do not full-index tail rows by
  default. Legacy Excel and OpenDocument spreadsheets converted locally through
  LibreOffice use the same sample-first workbook profiling when the converted
  workbook exceeds the inline extraction limit, preserving source/converted
  extension metadata.
- Office and OpenDocument business files should use local adapters only.
  Cross-platform extraction prefers bundled Python parsers or LibreOffice
  conversion; Windows installs may use Word, Excel, or PowerPoint COM for legacy
  binary formats when available.
- Publication files stay local. EPUB and FB2 are parsed with bounded local
  readers; MOBI, AZW/AZW3, and LIT require Calibre `ebook-convert` when local
  conversion is available and otherwise report `blocked_missing_dependency`.
  Comic archive formats reuse bounded container extraction and preserve
  `comic_archive` metadata.
- Image and scanned-PDF OCR uses local tools only. Image files can be OCRed with
  Tesseract in deferred image jobs after decorative-image skip checks;
  scanned and mixed PDFs use staged `pdftoppm` page-batch rendering plus
  Tesseract until all pages needing OCR are processed. OCR output is redacted
  before chunking and before cache writes. Cache hit/miss counts are stored as
  sanitized job telemetry; raw OCR text is not written to public docs or
  dashboard metadata.
- Audio/video ASR uses local tools only. Sidecar transcripts remain preferred;
  otherwise parent media jobs probe once, queue sequential audio chunks, append
  transcript chunks by stable segment locators, and then queue video frame vision
  work for the same asset when applicable. Each chunk extracts temporary mono 16
  kHz audio with `ffmpeg` and transcribes through `acceleration.asr.provider`.
  The production provider calls the loopback OpenAI-compatible ASR service with
  `acceleration.asr.model` set to `large-v3-turbo`; the fallback provider loads
  faster-whisper from `acceleration.asr.model_path`.
  ASR output is redacted before chunking and before ASR cache writes. Cloud
  transcription stays off by default, and raw transcript text is not written to
  public docs or dashboard metadata.
- Practical exchange/export formats use small local parsers before heavier
  tooling. Subtitle files (`srt`, `vtt`, `ass`, `ssa`, `ttml`, `dfxp`, `sbv`)
  are cleaned into transcript chunks without cue IDs or timestamps. `eml` and
  `mbox` mail exports summarize subjects, plain bodies, message counts, and
  attachment counts without indexing attachment bytes or raw addressing headers.
  Managed IMAP/Outlook spool exports index `manifest.json` as ordinary metadata.
  Canonical `body.txt` and files under `attachments/` are searchable through
  private disk sidecars: PostgreSQL stores blank chunk bodies plus sidecar
  references/hashes and vectors, not plaintext mail body or attachment chunk
  text. Raw `message.eml`, `message.msg`, and duplicate `body.html` artifacts
  stay on disk and are skipped by corpus retrieval.
  `ics` and `vcf` files extract conservative event/contact summaries while
  omitting contact email addresses from chunks.
- Security, test, coverage, and browser capture reports use bounded summaries
  for common local formats. SARIF stores finding counts and rule/message
  summaries, SPDX and CycloneDX store package/component summaries,
  JUnit-style XML/TRX/TAP store test/failure/skipped totals, LCOV and
  Cobertura-style coverage XML store line coverage totals, and HAR stores
  method/URL/status summaries. SQLite databases are read with a read-only stdlib
  connection and index schema/table metadata only; row values are not indexed by
  default.
- `drawio`, `drawio.svg`, and `drawio.png` parse embedded XML when present and
  index page names, shapes, labels, connectors, and links.
- `vsdx` and related modern Visio files are ZIP/XML containers and are parsed
  with bounded in-memory reads before any future rendered-image OCR fallback.
  Legacy `vsd` can use a local converter where available.
- Archives and package containers enumerate members through bounded local
  adapters. ZIP-family packages, TAR-family archives, and gzip/bzip2/xz streams
  use Python stdlib readers; formats such as 7z, RAR, CAB, ISO, DMG, ZST, LZ4,
  AR, CPIO, DEB, RPM, and CRX depend on local tools when available and otherwise
  report `blocked_missing_dependency`. Expansion enforces maximum depth, total
  uncompressed bytes, member count, member size, path traversal protection,
  encrypted-entry rejection, and unsafe link/device rejection.
- Container members are stored as related child assets linked to the parent
  archive. Inline-safe text/code members are chunked. Nested containers are
  recursively expanded up to `crawler.container_max_depth`, and embedded
  documents, diagrams, images, audio, video, subtitles, mail exports,
  calendar/contact files, structured data, reports, SQLite databases, and
  metadata-first domain formats are routed through local extractors from
  temporary private files. Embedded media sidecar transcript files such as
  `clip.mp4.srt` or `clip.mp4.txt` are used before probing or ASR tools while
  remaining visible as their own child assets. Child metadata records sanitized
  depth, parent-member, parser status, skipped, parser count summaries, and
  blocked-dependency details.
- Secret-bearing formats should never have raw content indexed by default.
  They may produce redacted metadata and audit entries only.
- Extractor availability surfaces optional local capability keys for practical
  coverage planning, including `readpst`, `msgconvert`, `duckdb`, `pyarrow`,
  `ogrinfo`, `gdalinfo`, `ifcopenshell`, `assimp`, `blender`, `exiftool`, and
  `pandoc`. These are diagnostics and fallback candidates; they are not required
  dependencies.
