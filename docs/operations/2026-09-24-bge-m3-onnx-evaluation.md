# BGE-M3 ONNX scoped-retrieval evaluation

Date: 2026-09-24. **Decision: do not select or activate a production semantic
model yet.** The pinned BGE-M3 export runs offline on the existing .NET ONNX
Runtime/DirectML stack, but the fixed retrieval pilot fails the predeclared
relevance gate. The available corpus also lacks the plain-text and scanned or
mixed-PDF cases required for a production selection. The deployed search remains
lexical.

## Identity and execution

The separately approved export is `BAAI/bge-m3` revision
`5617a9f61b028005a4858fdac845db406aefb181`. Its nine ONNX, external-data,
pooling/configuration and tokenizer files total 2,289,765,981 bytes under
`J:\Models\bundles\bge-m3-onnx\<revision>`. The approval manifest SHA-256 is
`940d3d3cd07f54e2c54bddef688fa91b13e66d038ca3a9b56aec3a8adf2cf01a`.
An offline verification checked every file's approved byte length and SHA-256
or Git-blob identity and the published bundle manifest. This ONNX acquisition
transferred 2,289,765,790 bytes; a 191-byte pooling configuration was already
verified in the central store. No model was loaded from a provider cache or a
worktree, and no inference-runtime or alternative-model download occurred.

The [pinned model card](https://huggingface.co/BAAI/bge-m3/blob/5617a9f61b028005a4858fdac845db406aefb181/README.md)
states MIT licensing, 1,024 dimensions, an 8,192-token context and no required
query instruction. The [pinned ONNX directory](https://huggingface.co/BAAI/bge-m3/tree/5617a9f61b028005a4858fdac845db406aefb181/onnx)
contains the evaluated export. Inspection of its graph and published pooling
configuration found CLS gathering followed by L2 normalisation. Actual .NET
inference returned finite, unit-length 1,024-float vectors. CPU versus DirectML
cosine agreement exceeded 0.99999999999 on short, OCR-noisy and 1,702-token
samples; all 27 fixed query vectors also exceeded 0.99999999999. The DirectML
runtime profile assigned 2,812 of 2,844 recorded node events to DirectML,
including encoder matrix multiplications; the CPU events were shape-related
operations. This is execution evidence, not an inference from provider
configuration alone.

Offline tokenisation used the exact local export `tokenizer.json` through an
already installed tokenizer runtime under `J:\Models`, with beginning/end
tokens and attention masks. The 829 canonical chunks plus 27 queries matched
the local SentencePiece reference. Five of 3,292 smaller overlapping windows
did not match that reference around complex punctuation. No production .NET
tokenizer binding or exact-parity implementation has been accepted; this remains
an adapter gate. Every evaluated window was at most 322 model tokens, below its
frozen 512-token ceiling, and every retained gold span was covered without
truncation.

## Fixed retrieval pilot

The lexical baseline includes the deployed response-safety correction at main
`5fed42f460076daa4bd56c077c732d3505f8b3b4`. Its release build had zero
warnings and the full default native suite passed 2,337 tests, with 17
pre-existing opt-in browser skips. Incremental IIS deployment used the
plan/apply route without a schema migration or clean-slate action. Live
REST/MCP search and cited reading passed for OCR image, native PDF, DOCX,
XLSX and Visio; CLI image and DOCX checks passed, as did refusal of invalid
evidence and forwarded requests. Live, ready and index-health checks remained
HTTP 200 after the local contention assessment. Warm lexical REST search
p50/p95 was 130/200 ms on 81 samples before OCR contention.

The question manifest was frozen before inference (SHA-256
`bbdf81047c880a46690f0fe1d3ccf13117cc0c40f844b695ae8f570be032dac6`).
It contains 27 questions over 11 currently eligible documents in one root.
Twenty-four questions have their independently checked answer span in retained
canonical text; three OCR-image questions fail at extraction and are excluded
from retrieval denominators. The pilot has six native-PDF, six DOCX, four XLSX,
four Visio and four OCR-image answer-present questions. It has no plain `.txt`
or scanned/mixed-PDF source. Private questions, source text, gold labels and
embeddings remain outside Git.

The lexical baseline is the deployed SQL Full-Text route. Dense-only ranking
used cosine over 3,292 frozen 768-UTF-16-unit windows with 192-unit overlap,
half-span overlap suppression and a three-passage-per-document cap. The hybrid
pilot used the repository's reciprocal-rank constant of 60 and a predeclared
lexical-chunk/dense-window pairing rule. These are offline ranking experiments,
not live semantic service results.

| Source class | Eligible | Lexical exact span @5 | Dense @5 | Hybrid @5 |
| --- | ---: | ---: | ---: | ---: |
| Native PDF | 6 | 0 | 4 | 3 |
| DOCX | 6 | 3 | 4 | 3 |
| XLSX | 4 | 3 | 3 | 3 |
| Visio | 4 | 1 | 0 | 1 |
| OCR image | 4 | 4 | 4 | 4 |
| **All answer-present** | **24** | **11 (45.8%)** | **15 (62.5%)** | **14 (58.3%)** |

| Source class | Reciprocal rank @5: lexical / dense / hybrid | Relevant document @5: lexical / dense / hybrid |
| --- | --- | --- |
| Native PDF | 0.000 / 0.228 / 0.194 | 6/6 / 6/6 / 6/6 |
| DOCX | 0.333 / 0.444 / 0.375 | 6/6 / 6/6 / 6/6 |
| XLSX | 0.750 / 0.625 / 0.563 | 4/4 / 4/4 / 4/4 |
| Visio | 0.083 / 0.000 / 0.050 | 1/4 / 0/4 / 1/4 |
| OCR image | 1.000 / 1.000 / 1.000 | 4/4 / 4/4 / 4/4 |
| **All answer-present** | **0.389 / 0.439 / 0.411** | **21/24 / 20/24 / 21/24** |

For 12 exact-term questions, lexical/dense/hybrid span Recall@5 was 7/8/9
and reciprocal rank @5 was 0.486/0.447/0.524. For 12 paraphrases, those
figures were 4/7/5 and 0.292/0.431/0.299. Document Recall@5 was 11/10/11
for exact terms and 10/10/10 for paraphrases, in the same order.

The fixed gate required recovery of at least five of eight predeclared lexical
paraphrase misses at rank five. Dense-only recovered three; hybrid recovered
two. The hybrid also displaced two correct lexical spans, including one term
check. One cause is identifiable without tuning: pairing a lexical chunk with
its highest-ranked dense window can substitute a different passage from that
chunk, while reciprocal-rank fusion can push the correct second window below
the result limit. The Visio failure is more fundamental in this pilot: its
relevant document's best raw dense-window ranks were 35 to 197, despite the
answer being retained. These are retrieval failures, distinct from the three
OCR extraction omissions. Changing the pairing rule on this evaluated set
would require a new held-out set before claiming a pass.

## Latency, memory and OCR priority

The evaluation host has an RTX 4070 SUPER with 12,282 MiB reported GPU memory.
With the existing .NET ONNX Runtime DirectML package, session creation took
about 1.7–1.9 seconds; warm single-query encoder p50/p95 over the fixed 27
queries was 13.0/18.0 ms. CPU inference was 96.7/108.6 ms. The first short
DirectML run took 581 ms; its later runs were about 26 ms. A 1,702-token
DirectML input took about 256–258 ms warm versus about 3.5 seconds on CPU.
The DirectML process peaked at about 2.23 GiB working set, and observed total
GPU use rose to 4,256 MiB on the OCR-noisy probe and 4,988 MiB on the long
probe. Background embedding of all 3,292 windows, batch size four, returned
finite vectors; its batch p50/p95 was 127/147 ms, with about 2.31 GiB peak
process working set. These are offline encoder measurements. No integrated
semantic query endpoint or end-to-end semantic latency has been claimed.

The separately measured 30-page guarded OCR-only run reached 10,160 MiB GPU
use and processed warm pages at 0.489 pages/s, with p95 page interval 2,183 ms.
The BGE and OCR peaks leave inadequate safe headroom for simultaneous DirectML
residency, so no potentially out-of-memory simultaneous GPU run was attempted.
In a matched 30-page guarded OCR run with 61 read-only live lexical searches,
warm OCR throughput was 0.483 pages/s (1.2% lower) and p95 page interval was
2,311 ms (5.9% higher). Lexical query p95 during OCR was 513 ms, below the
two-second fallback deadline. The local guarded OCR driver loaded its existing
J: bundles offline; this run did not submit or process production OCR jobs.
This measures the intended OCR-priority lexical fallback, not concurrent BGE
GPU execution. The production scheduler, admission and degradation behaviour
must still be implemented and tested before semantic activation.

## Required next work

Keep the current lexical service and its rollback path. Build a new,
independently specified cohort with ordinary plain text, at least eight
native-PDF and eight scanned/mixed-PDF cases, and complete extraction labels
before viewing new model ranks. Correct passage fusion so a semantic window
cannot silently replace a correct lexical citation; evaluate that rule on the
new held-out set. Establish exact offline .NET tokeniser parity. The installed
USearch .NET binding has no scoped-filter search overload, so use a bounded
exact scoped scan or another proven scope-aware candidate path rather than
filtering a global top-k. If relevance then passes, independently review the
profile-fenced generation transition and rollback invariants before schema
changes and require separate operational review before any activation. The
alternative Python route remains paused; this pilot does not justify its
operational cost.
