# Local models for semantic retrieval and reranking

Current proposal (25 September 2026): use the already acquired BGE-M3 ONNX
embedding model with BAAI/bge-reranker-v2-m3 in the
[hybrid passage design](hybrid-passage-retrieval.md). Include GPU scheduling
from the outset; CPU placement is optional after same-model validation. Neither
model is activated for production by this proposal. Subsequent approved work
acquired and exported the reranker; native CPU adapter parity is now verified as
recorded below. Qwen evaluation and the alternative
production Python route remain paused; a demonstrated limitation can justify a
specific alternative without changing the passage/candidate/citation contracts.
The earlier shortlist below is historical research, not current acquisition scope.

Metadata and official documentation checked 24 September 2026. This is a shortlist for local evaluation, not a production model selection or permission to acquire model files. No model payload, tokenizer, config or runtime package was downloaded for this research. All byte counts assume an empty cache; subtract only artifacts verified in `J:\Models` or other existing provider caches before requesting acquisition. `J:\Models` is the sole canonical model store.

The original pre-acquisition inventory found no matching embedding artifacts in
its checked caches. That finding is superseded for BGE-M3: a nine-file ONNX bundle
now exists under `J:\Models\bundles\bge-m3-onnx\5617a9f61b028005a4858fdac845db406aefb181`,
with a 25 September integrity receipt. The native tokenizer runtime also has a
25 September receipt under `J:\Models\runtimes\bge-onnx-net-tokenizer-1.4.0-win-x64`.
This planning task read metadata; it did not repeat hashing or prove tokenizer
parity. Never treat a stale inventory statement as permission to download again.

## Shortlist

| Candidate | Immutable Hugging Face revision | Dense dimensions | Model context | Query and passage handling | Pooling | Licence | Official source |
|---|---|---:|---:|---|---|---|---|
| BAAI/bge-m3 | `5617a9f61b028005a4858fdac845db406aefb181` | 1024 | 8192 tokens | Same unprefixed text path for queries and passages; no query instruction required. | CLS token for the published Sentence Transformers config; normalise for cosine ranking. | MIT | [Model card](https://huggingface.co/BAAI/bge-m3/tree/5617a9f61b028005a4858fdac845db406aefb181) |
| Qwen/Qwen3-Embedding-0.6B | `97b0c614be4d77ee51c0cef4e5f07c00f9eb65b3` | up to 1024, Matryoshka down to 32 | 32768 model maximum; card example uses 8192 | Query prompt `Instruct: {task}\nQuery:{query}`; passages plain. The Sentence Transformers `query` prompt supplies a default instruction. | Last non-padding token, then L2 normalise. | Apache-2.0 | [Model card](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B/tree/97b0c614be4d77ee51c0cef4e5f07c00f9eb65b3) |
| Qwen/Qwen3-Embedding-4B | `5cf2132abc99cad020ac570b19d031efec650f2b` | up to 2560, Matryoshka down to 32 | 32768 model maximum; card example uses 8192 | Same Qwen query instruction/passages. | Last non-padding token, then L2 normalise. | Apache-2.0 | [Model card](https://huggingface.co/Qwen/Qwen3-Embedding-4B/tree/5cf2132abc99cad020ac570b19d031efec650f2b) |

The model cards and published pooling configs support these specifications: [BGE-M3](https://huggingface.co/BAAI/bge-m3), [BGE pooling config](https://huggingface.co/BAAI/bge-m3/blob/5617a9f61b028005a4858fdac845db406aefb181/1_Pooling/config.json), [Qwen 0.6B](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B), [Qwen 4B](https://huggingface.co/Qwen/Qwen3-Embedding-4B). BGE's owner confirms no prefix is needed in the [model card FAQ](https://huggingface.co/BAAI/bge-m3). The Qwen cards specify Transformers >=4.51.0 and Sentence Transformers >=2.7.0; their examples use left padding, last-token pooling and normalisation. Their 32K context is a model maximum, not an assurance that 32K batching fits the available GPU.

The Hugging Face API metadata response for each pinned revision supplies byte lengths and either an LFS SHA-256 or a Git blob ID. A Git blob ID is **not** a raw-file SHA-256. It can identify an ordinary Git file in the pinned commit, but SHA-256 for those small files must be computed and recorded only if acquisition is separately approved. [Hugging Face cache documentation](https://huggingface.co/docs/huggingface_hub/package_reference/file_download) distinguishes Git SHA from SHA-256 for LFS files.

## Exact metadata manifests and empty-cache transfer ceilings

**BGE-M3 original PyTorch dense embedding path**: 2,293,315,801 bytes (2.136 GiB). This keeps one source format for a reproducible evaluation. The conservative tokenizer set includes both `tokenizer.json` and `sentencepiece.bpe.model`; one may prove unnecessary for a specific offline loader. The separate `sparse_linear.pt` and `colbert_linear.pt` heads are outside this dense-only scope. The BAAI repository also contains a contributor-added ONNX export, but that is an alternative format and is not included in this acquisition set. Its graph, external data and DirectML behaviour need separate inspection if chosen.

| File under pinned BGE revision | Bytes | Metadata identity |
|---|---:|---|
| `1_Pooling/config.json` | 191 | Git blob `9bd85925f325e25246d94c4918dc02ab98f2a1b7` |
| `config.json` | 687 | Git blob `e6eda1c72da8f9dc30fdd9b69c73d35af3b7a7ad` |
| `config_sentence_transformers.json` | 123 | Git blob `1fba91c78a6c8e17227058ab6d4d3acb5d8630a9` |
| `modules.json` | 349 | Git blob `952a9b81c0bfd99800fabf352f69c7ccd46c5e43` |
| `pytorch_model.bin` | 2,271,145,830 | SHA-256 `b5e0ce3470abf5ef3831aa1bd5553b486803e83251590ab7ff35a117cf6aad38` |
| `sentence_bert_config.json` | 54 | Git blob `0140ba1eac83a3c9b857d64baba91969d988624b` |
| `sentencepiece.bpe.model` | 5,069,051 | SHA-256 `cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865` |
| `special_tokens_map.json` | 964 | Git blob `b1879d702821e753ffe4245048eee415d54a9385` |
| `tokenizer.json` | 17,098,108 | SHA-256 `21106b6d7dab2952c1d496fb21d5dc9db75c28ed361a05f5020bbba27810dd08` |
| `tokenizer_config.json` | 444 | Git blob `dc69ac559dcba2694012009aaa108c614541789a` |

**Qwen 0.6B safetensors plus local Sentence Transformers metadata**: 1,207,470,234 bytes (1.125 GiB). The runtime may need a subset of the small metadata files, but this complete operational set is a conservative transfer ceiling. It excludes `README.md` and `.gitattributes`.

| File under pinned Qwen 0.6B revision | Bytes | Metadata identity |
|---|---:|---|
| `1_Pooling/config.json` | 313 | Git blob `b6291baabbc39f9792d6759883a47d7d5bd0fbcf` |
| `config.json` | 727 | Git blob `cef2749ee93607b8f9a58ec72f4f6bfaf874e71d` |
| `config_sentence_transformers.json` | 215 | Git blob `76aef3ade63553ebb698fe3c2a3264040ed093f8` |
| `generation_config.json` | 117 | Git blob `d46f1983345269c582611bbedb3ca0a13f8e5f7b` |
| `merges.txt` | 1,671,853 | Git blob `31349551d90c7606f325fe0f11bbb8bd5fa0d7c7` |
| `model.safetensors` | 1,191,586,416 | SHA-256 `0437e45c94563b09e13cb7a64478fc406947a93cb34a7e05870fc8dcd48e23fd` |
| `modules.json` | 349 | Git blob `952a9b81c0bfd99800fabf352f69c7ccd46c5e43` |
| `tokenizer.json` | 11,423,705 | SHA-256 `def76fb086971c7867b829c23a26261e38d9d74e02139253b38aeb9df8b4b50a` |
| `tokenizer_config.json` | 9,706 | Git blob `7345216a0785dc7086e8c245b2a9d3896ce2b756` |
| `vocab.json` | 2,776,833 | Git blob `4783fe10ac3adce15ac8f358ef5462739852c569` |

**Qwen 4B safetensors plus local Sentence Transformers metadata**: 8,059,503,129 bytes (7.506 GiB), likewise excluding `README.md` and `.gitattributes`.

| File under pinned Qwen 4B revision | Bytes | Metadata identity |
|---|---:|---|
| `1_Pooling/config.json` | 313 | Git blob `81de5602eacbce382009c5af7a23085871801d8f` |
| `config.json` | 727 | Git blob `8b4b87fc69023e7a224eb6563753aaf3223d8b98` |
| `config_sentence_transformers.json` | 215 | Git blob `76aef3ade63553ebb698fe3c2a3264040ed093f8` |
| `generation_config.json` | 117 | Git blob `d46f1983345269c582611bbedb3ca0a13f8e5f7b` |
| `merges.txt` | 1,671,853 | Git blob `31349551d90c7606f325fe0f11bbb8bd5fa0d7c7` |
| `model-00001-of-00002.safetensors` | 4,965,826,464 | SHA-256 `e70bfe3c970523fb7ef4eddffed2254ce3f1e7150c3de2af4342de129dd756f8` |
| `model-00002-of-00002.safetensors` | 3,077,765,624 | SHA-256 `ed1b87c8e9eb7e535a1a155e4fd00d9f4dba80e58a6db48a4c9f82cede7079c1` |
| `model.safetensors.index.json` | 30,431 | Git blob `3d736ef26714eee0abde3e05104ee1b3ec26c974` |
| `modules.json` | 349 | Git blob `952a9b81c0bfd99800fabf352f69c7ccd46c5e43` |
| `tokenizer.json` | 11,422,947 | SHA-256 `83cdf8c3a34f68862319cb1810ee7b1e2c0a44e0864ae930194ddb76bb7feb8d` |
| `tokenizer_config.json` | 7,256 | Git blob `df3a9d96759529ca1006eb6db024bbb099a97578` |
| `vocab.json` | 2,776,833 | Git blob `4783fe10ac3adce15ac8f358ef5462739852c569` |

The byte counts and identities above were read from Hugging Face's metadata-only `api/models/{owner}/{repo}?blobs=true` responses, which returned the exact `sha` values listed above. The pinned repository trees provide inspectable source links: [BGE-M3](https://huggingface.co/BAAI/bge-m3/tree/5617a9f61b028005a4858fdac845db406aefb181), [Qwen 0.6B](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B/tree/97b0c614be4d77ee51c0cef4e5f07c00f9eb65b3), [Qwen 4B](https://huggingface.co/Qwen/Qwen3-Embedding-4B/tree/5cf2132abc99cad020ac570b19d031efec650f2b).

## Runtime and selection implications

* Original PyTorch/safetensors files cannot load directly in the installed .NET ONNX Runtime 1.24.4 DirectML stack. The separately acquired BGE-M3 ONNX export passed the [offline execution pilot](../operations/2026-09-24-bge-m3-onnx-evaluation.md), including close CPU/GPU output agreement. The subsequent [native CPU adapter checks](bge-native-cpu-adapters.md) establish .NET tokenizer and numeric parity for both selected models. GPU ownership integration and integrated relevance remain unaccepted. The original tokenizer acquisition receipt alone was availability evidence.
* The currently inventoried OCR Python runtime has ONNX Runtime 1.30 CPU, tokenizers 0.23.2 and PaddleOCR/Paddle GPU, but no PyTorch, Transformers or Sentence Transformers. Use a separate dedicated offline runtime for PyTorch/safetensors evaluation; do not modify the OCR runtime. Qwen's official repositories provide safetensors, not ONNX. The 4B bfloat16 weights occupy 8.04 billion bytes before activations and runtime overhead; the shared 12 GiB GPU has a 4 GiB OCR admission **estimate**, not measured free VRAM. GPU coexistence cannot be assumed at useful sequence lengths or concurrency. CPU operation with roughly 80 GiB RAM may be possible but latency is unmeasured.
* BGE is MIT licensed, requiring preservation of its copyright and permission notice on redistribution ([MIT terms](https://opensource.org/license/mit)). Qwen cards mark Apache-2.0; redistribution requires licence and notice preservation, prominent changes and carries the stated patent terms ([Apache-2.0 terms](https://www.apache.org/licenses/LICENSE-2.0)). Each repository card is the licence declaration for the weights; dependency licences remain separate.
* Alibaba's [gte-multilingual-base](https://huggingface.co/Alibaba-NLP/gte-multilingual-base) was screened as a smaller 768-dimensional alternative, but its official loading example requires `trust_remote_code=True` and its config refers to code in a different repository. That adds an additional pinned-code and offline integrity surface for this deployment. Qwen3 8B's official files exceed 15 GB and the shared 12 GiB GPU. These were therefore not added to the three-candidate evaluation shortlist.

Choose a production model only after local relevance evaluation against real corpus queries, runtime performance and OCR contention tests, complete cache inventory, and a separately approved exact acquisition for any missing artifacts. Public benchmark scores are not local selection evidence.

## First reranker configuration

Propose [BAAI/bge-reranker-v2-m3](https://huggingface.co/BAAI/bge-reranker-v2-m3),
a trained XLM-R based cross-encoder of approximately 0.6B parameters, with
Apache-2.0 licensing. It accepts a query/passage pair and emits a relevance
logit; it does not supply stored dense embeddings. The model owner includes
English among its recommended uses. Local superiority over alternatives is
unmeasured; this is a deliberate first quality configuration, not a leaderboard
claim. A smaller MiniLM reranker was considered for CPU convenience and is not
the default recommendation.

Metadata-only API inspection on 25 September returned immutable revision
`953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e`. Its
[pinned repository](https://huggingface.co/BAAI/bge-reranker-v2-m3/tree/953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e)
contains these source artifacts and no ONNX export:

| File | Bytes | Metadata identity |
| --- | ---: | --- |
| `model.safetensors` | 2,271,071,852 | SHA-256 `d9e3e081faff1eefb84019509b2f5558fd74c1a05a2c7db22f74174fcedb5286` |
| `config.json` | 795 | Git blob `9f62673cb00ec41dcec8947b9ed16f6f2eb23ba2` |
| `sentencepiece.bpe.model` | 5,069,051 | SHA-256 `cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865` |
| `special_tokens_map.json` | 964 | Git blob `b1879d702821e753ffe4245048eee415d54a9385` |
| `tokenizer.json` | 17,098,273 | SHA-256 `69564b696052886ed0ac63fa393e928384e0f8caada38c1f4864a9bfbf379c15` |
| `tokenizer_config.json` | 1,173 | Git blob `328a00a9a560aadcf2a3064f917517359eb3cc26` |

The source set totals 2,293,242,108 bytes before verified cache reuse. The
sentencepiece file and special-token metadata match earlier BGE source
identities; inspect exact local files rather than acquiring duplicates. Do not
substitute BGE's embedding tokenizer JSON: the reranker file has a different
hash. Git blob IDs are not raw-file SHA-256 values.

This is not a complete acquisition request: exact cache checks, licence files,
conversion runtime dependencies and transfer bytes still need resolution before
approval. Retain all weights/configs, conversion inputs/tools/receipts and ONNX
outputs under `J:\Models`; no ordinary restore/import/load may acquire them.
Use pinned conversion tooling in an isolated offline environment, validate
query/passage tokenisation and logits against the pinned reference, then prove
.NET CPU/DirectML output parity and the actual maximum supported pair length.
The published example uses 512 tokens; it must not become silent truncation of
Flux queries or evidence. Reject oversized complete pairs with an explicit
reason, or validate a larger supported input profile before use.

Measure the real 50-passage shortlist, each model's load/run/unload times and
peak/released GPU memory, and complete search including per-request loads.
The baseline unloads between embedding/reranking phases and after each request.
Combined or across-request residency is not assumed; any retention proposal
needs measured full-search benefit, memory headroom and OCR-wait evidence.
The embedding pilot's CPU p95 of about 109 ms
versus DirectML 18 ms is not a reranker or full-search latency measurement.
Models stay behind stable embedding/reranking ports, so provider placement or
a later scorer update does not require redesigning SQL, USearch or citations.

On 27 September the [separately approved source acquisition](bge-reranker-acquisition-request.md)
completed with exactly 2,288,188,149 bytes transferred and three dependencies
reused. The [offline conversion record](bge-reranker-offline-conversion.md)
documents verified cache adoption with no additional downloads, the float32
ONNX export and CPU/reference logit agreement. The [native CPU adapter record](bge-native-cpu-adapters.md)
now establishes tokenizer and .NET CPU output parity, the 512-token boundary and
library/session cleanup. Scheduler-owned GPU execution, scoped relevance and
full-search latency are still unaccepted; the application has not enabled this provider.
