import json
from io import BytesIO
from types import SimpleNamespace
from urllib.error import HTTPError

from pathlib import Path

from flux_llm_kb import database, worker
from flux_llm_kb.embeddings import (
    EmbeddingInput,
    EmbeddingResult,
    SnowflakeEmbeddingProvider,
)
from flux_llm_kb.reranking import QwenReranker
from flux_llm_kb.search_index import (
    SNOWFLAKE_EMBEDDING_DIMENSIONS,
    SNOWFLAKE_EMBEDDING_MODEL,
    SearchIndexError,
    VespaHttpClient,
    VespaSearchAdapter,
    build_vespa_document,
)


class FakeModelRunner:
    def __init__(self):
        self.embedding_requests = []

    def embed(self, texts, *, model, dimensions):
        self.embedding_requests.append({"texts": list(texts), "model": model, "dimensions": dimensions})
        return [[1.0] + [0.0] * (dimensions - 1) for _ in texts]


def test_snowflake_embedding_provider_uses_model_runner_contract():
    runner = FakeModelRunner()
    provider = SnowflakeEmbeddingProvider(model_runner=runner)

    result = provider.embed_batch(
        [
            EmbeddingInput(
                owner_table="asset_chunks",
                owner_id="chunk-1",
                text="local retrieval evidence",
            )
        ]
    )[0]

    assert SNOWFLAKE_EMBEDDING_MODEL == "Snowflake/snowflake-arctic-embed-l-v2.0"
    assert SNOWFLAKE_EMBEDDING_DIMENSIONS == 1024
    assert runner.embedding_requests == [
        {
            "texts": ["local retrieval evidence"],
            "model": SNOWFLAKE_EMBEDDING_MODEL,
            "dimensions": SNOWFLAKE_EMBEDDING_DIMENSIONS,
        }
    ]
    assert result.model == SNOWFLAKE_EMBEDDING_MODEL
    assert result.dimensions == SNOWFLAKE_EMBEDDING_DIMENSIONS
    assert len(result.vector) == SNOWFLAKE_EMBEDDING_DIMENSIONS
    assert result.metadata["provider"] == "model_runner"
    assert result.metadata["source_hash"]
    assert "local retrieval evidence" not in json.dumps(result.metadata)


def test_vespa_document_builder_preserves_search_metadata_and_vector_shape():
    vector = [0.001] * 1024

    document = build_vespa_document(
        {
            "vespa_document_id": "id:flux:evidence::chunk-1",
            "owner_table": "asset_chunks",
            "owner_id": "chunk-1",
            "root_id": "root-1",
            "root_name": "docs",
            "title": "architecture.md",
            "body": "Vespa owns BM25 and dense candidate retrieval.",
            "source_path": "docs/architecture.md",
            "file_kind": "text",
            "language": "markdown",
            "lifecycle_state": "active",
            "deleted": False,
            "canonical": True,
            "source_hash": "hash-1",
            "embedding_model": SNOWFLAKE_EMBEDDING_MODEL,
            "embedding_dimensions": 1024,
            "embedding": vector,
            "symbols": ["KnowledgeService.search"],
        }
    )

    assert document["id"] == "id:flux:evidence::chunk-1"
    fields = document["fields"]
    assert fields["owner_table"] == "asset_chunks"
    assert fields["root_name"] == "docs"
    assert fields["file_kind"] == "text"
    assert fields["embedding_model"] == SNOWFLAKE_EMBEDDING_MODEL
    assert fields["embedding_dimensions"] == 1024
    assert fields["embedding"] == {"values": vector}
    assert fields["symbols"] == ["KnowledgeService.search"]


def test_vespa_document_builder_removes_control_characters_from_text_fields():
    document = build_vespa_document(
        {
            "vespa_document_id": "id:flux:evidence::chunk-1",
            "owner_table": "asset_chunks",
            "owner_id": "chunk-1",
            "root_id": "root-1",
            "root_name": "docs",
            "title": "bad\u000btitle",
            "body": "line one\u000bline two\nline three\tkept",
            "source_path": "docs/bad\u000cpath.md",
            "file_kind": "text",
            "language": "markdown",
            "lifecycle_state": "active",
            "deleted": False,
            "canonical": True,
            "source_hash": "hash-1",
            "embedding_model": SNOWFLAKE_EMBEDDING_MODEL,
            "embedding_dimensions": SNOWFLAKE_EMBEDDING_DIMENSIONS,
            "embedding": [1.0] + [0.0] * (SNOWFLAKE_EMBEDDING_DIMENSIONS - 1),
            "symbols": ["bad\u000bsymbol", "good.symbol"],
        }
    )

    fields = document["fields"]
    assert "\u000b" not in json.dumps(fields)
    assert "\u000c" not in json.dumps(fields)
    assert fields["title"] == "bad title"
    assert fields["body"] == "line one line two\nline three\tkept"
    assert fields["source_path"] == "docs/bad path.md"
    assert fields["symbols"] == ["bad symbol", "good.symbol"]


def test_vespa_adapter_allows_rrf_rank_profile():
    requests = []

    class FakeHttp:
        def post_json(self, path, payload):
            requests.append((path, payload))
            return {"root": {"children": []}}

    adapter = VespaSearchAdapter(base_url="http://vespa:8080", http=FakeHttp())

    adapter.query(
        "hybrid retrieval",
        embedding=[1.0] + [0.0] * (SNOWFLAKE_EMBEDDING_DIMENSIONS - 1),
        rank_profile="hybrid_rrf",
        limit=20,
    )

    assert requests[0][0] == "/search/"
    assert requests[0][1]["ranking.profile"] == "hybrid_rrf"
    assert "nearestNeighbor(embedding, query_embedding) or userQuery()" in requests[0][1]["yql"]


def test_vespa_schema_defines_native_rrf_profile_and_weighted_comparison():
    schema = Path("vespa/schemas/flux_evidence.sd").read_text(encoding="utf-8")

    assert "rank-profile hybrid_rrf" in schema
    assert "function lexical_score()" in schema
    assert "bm25(title) + bm25(body) + bm25(source_path) + bm25(symbols)" in schema
    assert "function dense_score()" in schema
    assert "closeness(field, embedding)" in schema
    assert "global-phase" in schema
    assert "reciprocal_rank(lexical_score, 60) + reciprocal_rank(dense_score, 60)" in schema
    assert "rerank-count: 200" in schema
    assert "rank-profile hybrid_weighted" in schema


def test_vespa_rrf_candidate_merge_orders_by_fused_score():
    candidates = [
        {"owner_table": "asset_chunks", "owner_id": "chunk-low", "score": 0.010, "match_features": {"lexical_score": 5.0}},
        {"owner_table": "episodes", "owner_id": "episode-high", "score": 0.030, "match_features": {"dense_score": 0.9}},
        {"owner_table": "claims", "owner_id": "claim-mid", "score": 0.020, "match_features": {"lexical_score": 2.0, "dense_score": 0.5}},
    ]

    merged = database._merge_vespa_rrf_candidates(candidates)

    assert [(item["owner_table"], item["owner_id"]) for item in merged] == [
        ("episodes", "episode-high"),
        ("claims", "claim-mid"),
        ("asset_chunks", "chunk-low"),
    ]


def test_vespa_adapter_feeds_full_document_with_post_put_payload():
    class FakeFeedHttp:
        def __init__(self):
            self.posts = []

        def post_json(self, path, payload):
            self.posts.append({"path": path, "payload": payload})
            return {"id": payload["put"]}

    http = FakeFeedHttp()
    adapter = VespaSearchAdapter(base_url="http://vespa:8080", http=http)
    document = build_vespa_document(
        {
            "vespa_document_id": "id:flux:flux_evidence::asset_chunks--chunk-1",
            "owner_table": "asset_chunks",
            "owner_id": "chunk-1",
            "root_id": "root-1",
            "root_name": "docs",
            "title": "architecture.md",
            "body": "Vespa owns BM25 and dense candidate retrieval.",
            "source_path": "docs/architecture.md",
            "file_kind": "text",
            "language": "markdown",
            "lifecycle_state": "active",
            "deleted": False,
            "canonical": True,
            "source_hash": "hash-1",
            "embedding_model": SNOWFLAKE_EMBEDDING_MODEL,
            "embedding_dimensions": SNOWFLAKE_EMBEDDING_DIMENSIONS,
            "embedding": [1.0] + [0.0] * (SNOWFLAKE_EMBEDDING_DIMENSIONS - 1),
            "symbols": ["KnowledgeService.search"],
        }
    )

    response = adapter.feed(document)

    assert response == {"id": "id:flux:flux_evidence::asset_chunks--chunk-1"}
    assert http.posts == [
        {
            "path": "/document/v1/flux/flux_evidence/docid/asset_chunks--chunk-1",
            "payload": {
                "put": "id:flux:flux_evidence::asset_chunks--chunk-1",
                "fields": document["fields"],
            },
        }
    ]


def test_vespa_http_client_includes_error_response_body(monkeypatch):
    def fake_urlopen(_request, timeout):
        raise HTTPError(
            url="http://vespa:8080/document/v1/flux/flux_evidence/docid/chunk-1",
            code=400,
            msg="Bad Request",
            hdrs={},
            fp=BytesIO(b'{"message":"Field body contains unsupported code point"}'),
        )

    monkeypatch.setattr("flux_llm_kb.search_index.urlopen", fake_urlopen)
    client = VespaHttpClient("http://vespa:8080")

    try:
        client.post_json("/document/v1/flux/flux_evidence/docid/chunk-1", {"fields": {}})
    except SearchIndexError as exc:
        error = str(exc)
    else:  # pragma: no cover - defensive assertion
        raise AssertionError("expected SearchIndexError")

    assert "HTTP Error 400: Bad Request" in error
    assert "Field body contains unsupported code point" in error


class FakeVespaHttp:
    def __init__(self):
        self.posts = []

    def post_json(self, path, payload):
        self.posts.append({"path": path, "payload": payload})
        return {
            "root": {
                "children": [
                    {
                        "id": "id:flux:evidence::chunk-1",
                        "relevance": 1.7,
                        "fields": {
                            "owner_table": "asset_chunks",
                            "owner_id": "chunk-1",
                            "root_name": "docs",
                            "title": "architecture.md",
                            "source_path": "docs/architecture.md",
                        },
                        "matchfeatures": {
                            "bm25(body)": 3.5,
                            "closeness(field,embedding)": 0.82,
                        },
                    }
                ]
            }
        }


def test_vespa_adapter_builds_hybrid_query_with_filters_and_features():
    http = FakeVespaHttp()
    adapter = VespaSearchAdapter(base_url="http://vespa:8080", http=http)

    results = adapter.query(
        "hybrid retrieval",
        embedding=[0.0] * 1024,
        root_name="docs",
        file_kinds=["text"],
        languages=["markdown"],
        limit=10,
    )

    assert len(results) == 1
    payload = http.posts[0]["payload"]
    assert http.posts[0]["path"] == "/search/"
    assert 'nearestNeighbor(embedding, query_embedding)' in payload["yql"]
    assert 'userQuery()' in payload["yql"]
    assert 'body %%' not in payload["yql"]
    assert payload["ranking.profile"] == "hybrid"
    assert payload["hits"] == 10
    assert payload["input.query(query_embedding)"] == [0.0] * 1024
    assert payload["root_name"] == "docs"
    assert "file_kind in @file_kinds" not in payload["yql"]
    assert "language in @languages" not in payload["yql"]
    assert "file_kind contains @file_kind_0" in payload["yql"]
    assert "language contains @language_0" in payload["yql"]
    assert payload["file_kind_0"] == "text"
    assert payload["language_0"] == "markdown"
    assert results[0]["match_features"]["bm25(body)"] == 3.5


def test_vespa_adapter_reads_matchfeatures_from_fields():
    class FakeFieldsMatchFeaturesHttp:
        def post_json(self, _path, _payload):
            return {
                "root": {
                    "children": [
                        {
                            "relevance": 2.5,
                            "fields": {
                                "owner_table": "asset_chunks",
                                "owner_id": "chunk-1",
                                "matchfeatures": {
                                    "bm25(body)": 1.2,
                                    "closeness(field,embedding)": 0.8,
                                },
                            },
                        }
                    ]
                }
            }

    results = VespaSearchAdapter(base_url="http://vespa:8080", http=FakeFieldsMatchFeaturesHttp()).query(
        "hybrid retrieval",
        embedding=[0.0] * 1024,
        limit=1,
    )

    assert results[0]["match_features"] == {
        "bm25(body)": 1.2,
        "closeness(field,embedding)": 0.8,
    }


class FakeQwenScorer:
    def __init__(self):
        self.calls = []

    def score(self, query, passages, *, model, quantization):
        self.calls.append(
            {
                "query": query,
                "passages": list(passages),
                "model": model,
                "quantization": quantization,
            }
        )
        return [float(len(passage.split())) for passage in passages]


def test_qwen_reranker_uses_quantized_microbatches_and_token_bounds():
    scorer = FakeQwenScorer()
    reranker = QwenReranker(scorer=scorer, top_n=3, microbatch_size=2, max_passage_tokens=4)
    candidates = [
        {"id": "a", "title": "A", "summary": "one two three four five"},
        {"id": "b", "title": "B", "summary": "one two"},
        {"id": "c", "title": "C", "summary": "one two three"},
        {"id": "d", "title": "D", "summary": "one"},
    ]

    reranked = reranker.rerank("rank these", candidates)

    assert [len(call["passages"]) for call in scorer.calls] == [2, 1]
    assert all(call["model"] == "Qwen/Qwen3-Reranker-4B" for call in scorer.calls)
    assert all(call["quantization"] == "int4_awq" for call in scorer.calls)
    assert scorer.calls[0]["passages"][0] == "A\none two three four"
    assert [item["id"] for item in reranked] == ["a", "c", "b"]
    assert [item["reranker"]["model"] for item in reranked] == ["Qwen/Qwen3-Reranker-4B"] * 3
    assert all(item["reranker"]["quantization"] == "int4_awq" for item in reranked)


class FakeSearchIndexProvider:
    def __init__(self):
        self.inputs = []

    def embed_batch(self, inputs):
        self.inputs.append(list(inputs))
        return [
            EmbeddingResult(
                owner_table=item.owner_table,
                owner_id=item.owner_id,
                model=SNOWFLAKE_EMBEDDING_MODEL,
                dimensions=SNOWFLAKE_EMBEDDING_DIMENSIONS,
                vector=[1.0] + [0.0] * (SNOWFLAKE_EMBEDDING_DIMENSIONS - 1),
                metadata={"source_hash": "source-hash", "provider": "fake"},
            )
            for item in inputs
        ]


class FakeSearchIndexAdapter:
    def __init__(self):
        self.fed = []
        self.deleted = []

    def feed(self, document):
        self.fed.append(document)
        return {"ok": True}

    def delete(self, document_id):
        self.deleted.append(document_id)
        return {"ok": True}


def test_sync_search_index_feeds_vespa_and_records_status(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            sql = executed[-1][0]
            if "FROM search_index_records rec" in sql and "LEFT JOIN asset_chunks" in sql:
                return []
            if "FROM asset_chunks c" in sql:
                return [
                    (
                        "11111111-1111-1111-1111-111111111111",
                        "22222222-2222-2222-2222-222222222222",
                        "33333333-3333-3333-3333-333333333333",
                        "docs",
                        "architecture.md",
                        "Vespa owns hybrid search.",
                        "docs/architecture.md",
                        "text",
                        {"code": {"language": "markdown", "qualified_name": "KnowledgeService.search"}},
                        None,
                        None,
                        None,
                    )
                ]
            return []

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    provider = FakeSearchIndexProvider()
    adapter = FakeSearchIndexAdapter()

    result = database.sync_search_index(
        owner_class="corpus",
        root_name="docs",
        limit=10,
        embedding_provider=provider,
        adapter=adapter,
    )

    assert result["indexed"] == 1
    assert result["failed"] == 0
    assert result["skipped_unchanged"] == 0
    assert provider.inputs[0][0].model == SNOWFLAKE_EMBEDDING_MODEL
    assert provider.inputs[0][0].dimensions == SNOWFLAKE_EMBEDDING_DIMENSIONS
    assert "Vespa owns hybrid search." in provider.inputs[0][0].text
    assert adapter.fed[0]["id"].endswith("asset_chunks--11111111-1111-1111-1111-111111111111")
    fields = adapter.fed[0]["fields"]
    assert fields["owner_table"] == "asset_chunks"
    assert fields["root_name"] == "docs"
    assert fields["language"] == "markdown"
    assert fields["symbols"] == ["KnowledgeService.search"]
    assert fields["embedding"]["values"] == [1.0] + [0.0] * (SNOWFLAKE_EMBEDDING_DIMENSIONS - 1)
    assert any(params and "syncing" in params for _sql, params in executed)
    assert any(params and "indexed" in params for _sql, params in executed)


def test_sync_search_index_uses_configured_vespa_base_url(monkeypatch):
    captured: dict[str, str] = {}

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, *_args, **_kwargs):
            return None

        def fetchall(self):
            return []

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    class FakeAdapter:
        def __init__(self, base_url):
            captured["base_url"] = base_url

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setenv("FLUX_KB_RETRIEVAL_VESPA_BASE_URL", "http://vespa:8080")
    monkeypatch.setattr("flux_llm_kb.search_index.VespaSearchAdapter", FakeAdapter)

    result = database.sync_search_index(owner_class="corpus", root_name="docs", limit=1)

    assert result["failed"] == 0
    assert captured["base_url"] == "http://vespa:8080"


def test_search_index_fetch_all_owner_class_does_not_starve_episodes_or_claims():
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            sql, params = executed[-1]
            requested = int(params[-1])
            if "FROM asset_chunks c" in sql:
                return [
                    (
                        f"11111111-1111-1111-1111-{idx:012d}",
                        "22222222-2222-2222-2222-222222222222",
                        "33333333-3333-3333-3333-333333333333",
                        "docs",
                        f"chunk-{idx}.md",
                        "Vespa owns hybrid search.",
                        f"docs/chunk-{idx}.md",
                        "text",
                        {"code": {"language": "markdown"}},
                        None,
                        None,
                        None,
                    )
                    for idx in range(requested)
                ]
            if "FROM episodes e" in sql:
                return [
                    (
                        f"44444444-4444-4444-4444-{idx:012d}",
                        f"Episode {idx}",
                        "Durable memory episode.",
                        {"root_name": "docs"},
                        None,
                        None,
                        None,
                    )
                    for idx in range(requested)
                ]
            if "FROM claims c" in sql:
                return [
                    (
                        f"55555555-5555-5555-5555-{idx:012d}",
                        f"Claim {idx}",
                        "Durable claim text.",
                        "active",
                        {"root_name": "docs"},
                        None,
                        None,
                        None,
                    )
                    for idx in range(requested)
                ]
            return []

    rows = database._fetch_search_index_rows(
        FakeCursor(),
        owner_class="all",
        root_name=None,
        limit=6,
        embedding_model=SNOWFLAKE_EMBEDDING_MODEL,
    )

    assert [row["owner_table"] for row in rows].count("asset_chunks") == 2
    assert [row["owner_table"] for row in rows].count("episodes") == 2
    assert [row["owner_table"] for row in rows].count("claims") == 2
    assert len(executed) == 3
    assert all("WHEN rec.index_status = 'failed' THEN 0" in sql for sql, _params in executed)
    assert all("WHEN rec.index_status IS NULL THEN 1" in sql for sql, _params in executed)
    assert all("WHEN rec.index_status IS DISTINCT FROM 'indexed' THEN 2" in sql for sql, _params in executed)
    assert all("ORDER BY" in sql for sql, _params in executed)


def test_vespa_search_records_explain_diagnostics(monkeypatch):
    vespa_queries = []

    class FakeProvider:
        def __init__(self, **_kwargs):
            pass

        def embed_batch(self, _inputs):
            return [
                EmbeddingResult(
                    owner_table="query",
                    owner_id="query",
                    model=SNOWFLAKE_EMBEDDING_MODEL,
                    dimensions=SNOWFLAKE_EMBEDDING_DIMENSIONS,
                    vector=[1.0] + [0.0] * (SNOWFLAKE_EMBEDDING_DIMENSIONS - 1),
                    metadata={"source_hash": "query-hash"},
                )
            ]

    class FakeAdapter:
        def __init__(self, base_url):
            self.base_url = base_url

        def query(self, *_args, **kwargs):
            vespa_queries.append(kwargs)
            return [
                {
                    "owner_table": "asset_chunks",
                    "owner_id": "chunk-1",
                    "score": 0.016,
                    "match_features": {
                        "lexical_score": 1.2,
                        "dense_score": 0.8,
                    },
                }
            ]

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return self

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    class FakeReranker:
        model = "Qwen/Qwen3-Reranker-4B"
        quantization = "int4_awq"
        top_n = 80
        max_passage_tokens = 1536

        def __init__(self, **kwargs):
            self.top_n = kwargs["top_n"]

        def rerank(self, _query, candidates):
            return [{**candidate, "reranker": {"model": self.model, "quantization": self.quantization, "score": 9.0}} for candidate in candidates]

    monkeypatch.setattr("flux_llm_kb.embeddings.SnowflakeEmbeddingProvider", FakeProvider)
    monkeypatch.setattr("flux_llm_kb.search_index.VespaSearchAdapter", FakeAdapter)
    monkeypatch.setattr("flux_llm_kb.reranking.QwenReranker", FakeReranker)
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(
        database,
        "_hydrate_corpus_candidate_details",
        lambda *_args, **_kwargs: {
            "chunk-1": {
                "title": "Doc",
                "summary": "Private text is not copied into diagnostics.",
                "raw_scores": {"vespa_rrf": 0.016, "vespa_lexical": 1.2, "vespa_dense": 0.8},
            }
        },
    )
    monkeypatch.setattr(database, "_add_semantic_duplicate_metadata", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(database, "_rank_corpus_candidates", lambda *_args, **_kwargs: [SimpleNamespace(item_id="chunk-1", score=0.016)])
    monkeypatch.setattr(
        database,
        "_corpus_results_from_fused",
        lambda _fused, _details: [
            {
                "id": "chunk-1",
                "title": "Doc",
                "summary": "Private text",
                "score": 0.016,
                "raw_scores": {"vespa_rrf": 0.016, "vespa_lexical": 1.2, "vespa_dense": 0.8},
            }
        ],
    )
    diagnostics: dict[str, object] = {}

    results = database.search_corpus_chunks_vespa("quality", limit=3, root_name="docs", vespa_base_url="http://vespa:8080", diagnostics=diagnostics)

    assert len(results) == 1
    assert vespa_queries[0]["rank_profile"] == "hybrid_rrf"
    assert diagnostics["vespa"]["rank_profile"] == "hybrid_rrf"
    assert diagnostics["vespa"]["query_mode"] == "vespa_hybrid_rrf"
    assert diagnostics["vespa"]["rrf_k"] == 60
    assert diagnostics["vespa"]["candidate_count"] == 1
    assert diagnostics["vespa"]["fused_candidate_count"] == 1
    assert diagnostics["vespa"]["stream_counts"] == {"lexical": 1, "dense": 1, "overlap": 1}
    assert diagnostics["vespa"]["hydrated_count"] == 1
    assert diagnostics["vespa"]["match_feature_keys"] == ["dense_score", "lexical_score"]
    assert diagnostics["reranker"]["model"] == "Qwen/Qwen3-Reranker-4B"
    assert diagnostics["reranker"]["quantization"] == "int4_awq"
    assert diagnostics["reranker"]["input_count"] == 1
    assert results[0]["streams"] == ["vespa_rrf", "vespa_lexical", "vespa_dense"]


def test_vespa_evidence_search_hydrates_asset_episode_and_claim_results(monkeypatch):
    class FakeProvider:
        def __init__(self, **_kwargs):
            pass

        def embed_batch(self, _inputs):
            return [
                EmbeddingResult(
                    owner_table="query",
                    owner_id="query",
                    model=SNOWFLAKE_EMBEDDING_MODEL,
                    dimensions=SNOWFLAKE_EMBEDDING_DIMENSIONS,
                    vector=[1.0] + [0.0] * (SNOWFLAKE_EMBEDDING_DIMENSIONS - 1),
                    metadata={"source_hash": "query-hash"},
                )
            ]

    class FakeAdapter:
        def __init__(self, base_url):
            self.base_url = base_url

        def query(self, *_args, **_kwargs):
            return [
                {"owner_table": "asset_chunks", "owner_id": "chunk-1", "score": 0.018, "match_features": {"lexical_score": 1.0, "dense_score": 0.7}},
                {"owner_table": "episodes", "owner_id": "episode-1", "score": 0.015, "match_features": {"lexical_score": 0.8}},
                {"owner_table": "claims", "owner_id": "claim-1", "score": 0.014, "match_features": {"dense_score": 0.6}},
            ]

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return self

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    class FakeReranker:
        model = "Qwen/Qwen3-Reranker-4B"
        quantization = "int4_awq"
        top_n = 80
        max_passage_tokens = 1536

        def __init__(self, **kwargs):
            self.top_n = kwargs["top_n"]

        def rerank(self, _query, candidates):
            return [{**candidate, "reranker": {"score": float(10 - idx)}} for idx, candidate in enumerate(candidates)]

    monkeypatch.setattr("flux_llm_kb.embeddings.SnowflakeEmbeddingProvider", FakeProvider)
    monkeypatch.setattr("flux_llm_kb.search_index.VespaSearchAdapter", FakeAdapter)
    monkeypatch.setattr("flux_llm_kb.reranking.QwenReranker", FakeReranker)
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_add_semantic_duplicate_metadata", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(
        database,
        "_hydrate_corpus_candidate_details",
        lambda *_args, **_kwargs: {
            "chunk-1": {
                "title": "Doc",
                "summary": "Corpus chunk text",
                "raw_scores": {"vespa_rrf": 0.018, "vespa_lexical": 1.0, "vespa_dense": 0.7},
                "asset_id": "asset-1",
                "source_path": "docs/doc.md",
                "root_name": "docs",
                "duplicate_count": 0,
                "trust_rank": 500,
            }
        },
    )
    monkeypatch.setattr(
        database,
        "_hydrate_episode_candidate_details",
        lambda *_args, **_kwargs: {
            "episode-1": {
                "id": "episode-1",
                "kind": "episode",
                "title": "Episode",
                "summary": "Episode text",
                "raw_scores": {"vespa_rrf": 0.015, "vespa_lexical": 0.8},
            }
        },
        raising=False,
    )
    monkeypatch.setattr(
        database,
        "_hydrate_claim_candidate_details",
        lambda *_args, **_kwargs: {
            "claim-1": {
                "id": "claim-1",
                "kind": "claim",
                "title": "Claim",
                "summary": "Claim text",
                "raw_scores": {"vespa_rrf": 0.014, "vespa_dense": 0.6},
            }
        },
        raising=False,
    )
    diagnostics: dict[str, object] = {}

    results = database.search_evidence_vespa(
        "quality",
        limit=5,
        root_name="docs",
        filters={},
        vespa_base_url="http://vespa:8080",
        diagnostics=diagnostics,
    )

    assert [result["kind"] for result in results] == ["corpus_chunk", "episode", "claim"]
    assert diagnostics["vespa"]["owner_counts"] == {"asset_chunks": 1, "episodes": 1, "claims": 1}
    assert diagnostics["vespa"]["stream_counts"] == {"lexical": 2, "dense": 2, "overlap": 1}
    assert diagnostics["vespa"]["hydrated_count"] == 3
    assert diagnostics["reranker"]["input_count"] == 3
    assert results[0]["streams"] == ["vespa_rrf", "vespa_lexical", "vespa_dense"]
    assert results[1]["streams"] == ["vespa_rrf", "vespa_lexical"]
    assert results[2]["streams"] == ["vespa_rrf", "vespa_dense"]


def test_vespa_evidence_search_merges_duplicate_rrf_stream_signals(monkeypatch):
    class FakeProvider:
        def __init__(self, **_kwargs):
            pass

        def embed_batch(self, _inputs):
            return [
                EmbeddingResult(
                    owner_table="query",
                    owner_id="query",
                    model=SNOWFLAKE_EMBEDDING_MODEL,
                    dimensions=SNOWFLAKE_EMBEDDING_DIMENSIONS,
                    vector=[1.0] + [0.0] * (SNOWFLAKE_EMBEDDING_DIMENSIONS - 1),
                    metadata={"source_hash": "query-hash"},
                )
            ]

    class FakeAdapter:
        def __init__(self, base_url):
            self.base_url = base_url

        def query(self, *_args, **_kwargs):
            return [
                {"owner_table": "asset_chunks", "owner_id": "chunk-1", "score": 0.012, "match_features": {"lexical_score": 4.0}},
                {"owner_table": "asset_chunks", "owner_id": "chunk-1", "score": 0.016, "match_features": {"dense_score": 0.82}},
            ]

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return self

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    class FakeReranker:
        model = "Qwen/Qwen3-Reranker-4B"
        quantization = "int4_awq"
        top_n = 80
        max_passage_tokens = 1536

        def __init__(self, **kwargs):
            self.top_n = kwargs["top_n"]

        def rerank(self, _query, candidates):
            return [{**candidate, "reranker": {"score": 9.0}} for candidate in candidates]

    monkeypatch.setattr("flux_llm_kb.embeddings.SnowflakeEmbeddingProvider", FakeProvider)
    monkeypatch.setattr("flux_llm_kb.search_index.VespaSearchAdapter", FakeAdapter)
    monkeypatch.setattr("flux_llm_kb.reranking.QwenReranker", FakeReranker)
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_add_semantic_duplicate_metadata", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(
        database,
        "_hydrate_corpus_candidate_details",
        lambda *_args, **_kwargs: {
            "chunk-1": {
                "title": "Doc",
                "summary": "Corpus chunk text",
                "raw_scores": {"vespa_rrf": 0.016, "vespa_lexical": 4.0, "vespa_dense": 0.82},
                "asset_id": "asset-1",
                "source_path": "docs/doc.md",
                "root_name": "docs",
                "duplicate_count": 0,
                "trust_rank": 500,
            }
        },
    )
    monkeypatch.setattr(database, "_hydrate_episode_candidate_details", lambda *_args, **_kwargs: {}, raising=False)
    monkeypatch.setattr(database, "_hydrate_claim_candidate_details", lambda *_args, **_kwargs: {}, raising=False)
    diagnostics: dict[str, object] = {}

    results = database.search_evidence_vespa(
        "quality",
        limit=5,
        root_name="docs",
        filters={},
        vespa_base_url="http://vespa:8080",
        diagnostics=diagnostics,
    )

    assert len(results) == 1
    assert results[0]["id"] == "chunk-1"
    assert results[0]["streams"] == ["vespa_rrf", "vespa_lexical", "vespa_dense"]
    assert results[0]["raw_scores"] == {"vespa_rrf": 0.016, "vespa_lexical": 4.0, "vespa_dense": 0.82}
    assert diagnostics["vespa"]["candidate_count"] == 2
    assert diagnostics["vespa"]["fused_candidate_count"] == 1
    assert diagnostics["vespa"]["stream_counts"] == {"lexical": 1, "dense": 1, "overlap": 1}
    assert diagnostics["reranker"]["input_count"] == 1


def test_vespa_evidence_search_promotes_exact_code_symbol_matches(monkeypatch):
    class FakeProvider:
        def __init__(self, **_kwargs):
            pass

        def embed_batch(self, _inputs):
            return [
                EmbeddingResult(
                    owner_table="query",
                    owner_id="query",
                    model=SNOWFLAKE_EMBEDDING_MODEL,
                    dimensions=SNOWFLAKE_EMBEDDING_DIMENSIONS,
                    vector=[1.0] + [0.0] * (SNOWFLAKE_EMBEDDING_DIMENSIONS - 1),
                    metadata={"source_hash": "query-hash"},
                )
            ]

    class FakeAdapter:
        def __init__(self, base_url):
            self.base_url = base_url

        def query(self, *_args, **_kwargs):
            return [
                {"owner_table": "asset_chunks", "owner_id": "chunk-module", "score": 9.0},
                {"owner_table": "asset_chunks", "owner_id": "chunk-helper", "score": 0.2},
            ]

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return self

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    class FakeReranker:
        model = "Qwen/Qwen3-Reranker-4B"
        quantization = "int4_awq"
        top_n = 80
        max_passage_tokens = 1536

        def __init__(self, **kwargs):
            self.top_n = kwargs["top_n"]

        def rerank(self, _query, candidates):
            return [{**candidate, "reranker": {"score": float(10 - idx)}} for idx, candidate in enumerate(candidates)]

    monkeypatch.setattr("flux_llm_kb.embeddings.SnowflakeEmbeddingProvider", FakeProvider)
    monkeypatch.setattr("flux_llm_kb.search_index.VespaSearchAdapter", FakeAdapter)
    monkeypatch.setattr("flux_llm_kb.reranking.QwenReranker", FakeReranker)
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_add_semantic_duplicate_metadata", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(
        database,
        "_hydrate_corpus_candidate_details",
        lambda *_args, **_kwargs: {
            "chunk-module": {
                "title": "service_impl.py::module",
                "summary": "module marker and helper definition",
                "raw_scores": {"vespa_hybrid": 9.0},
                "asset_id": "asset-1",
                "source_path": "service_impl.py",
                "root_name": "repo",
                "duplicate_count": 0,
                "trust_rank": 500,
                "file_kind": "code",
                "code": {"primary_symbol": "module", "relationship": "definition"},
            },
            "chunk-helper": {
                "title": "service_impl.py::_benchmark_private_helper",
                "summary": "def _benchmark_private_helper(request): return request",
                "raw_scores": {"vespa_hybrid": 0.2},
                "asset_id": "asset-1",
                "source_path": "service_impl.py",
                "root_name": "repo",
                "duplicate_count": 0,
                "trust_rank": 500,
                "file_kind": "code",
                "code": {"primary_symbol": "_benchmark_private_helper", "relationship": "definition"},
            },
        },
    )
    monkeypatch.setattr(database, "_hydrate_episode_candidate_details", lambda *_args, **_kwargs: {}, raising=False)
    monkeypatch.setattr(database, "_hydrate_claim_candidate_details", lambda *_args, **_kwargs: {}, raising=False)

    results = database.search_evidence_vespa(
        "code marker _benchmark_private_helper",
        limit=5,
        root_name="repo",
        filters={"logical_kinds": ["file"], "file_kinds": ["code"]},
        vespa_base_url="http://vespa:8080",
    )

    assert results[0]["id"] == "chunk-helper"
    assert "code_rank_adjustment" in results[0]["streams"]
    assert results[0]["raw_scores"]["code_rank_adjustment"] > 0


def test_legacy_pgvector_and_hash_embedding_code_paths_are_removed():
    database_source = Path(database.__file__).read_text(encoding="utf-8")
    legacy_search_body = database_source.split("def search_corpus_chunks", 1)[1].split("\ndef ", 1)[0]
    semantic_duplicate_body = database_source.split("def refresh_semantic_duplicate_clusters", 1)[1].split("\ndef ", 1)[0]

    assert '_SEMANTIC_DUPLICATE_ALGORITHM = "snowflake-vespa-cosine-v1"' in database_source
    assert "emb.embedding <=>" not in legacy_search_body
    assert "to_pgvector_literal" not in legacy_search_body
    assert "DEFAULT_EMBEDDING_MODEL" not in semantic_duplicate_body
    assert "JOIN embeddings emb" not in semantic_duplicate_body


def test_claim_corpus_jobs_includes_search_index_sync_jobs(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return []

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    database.claim_corpus_jobs(limit=1, job_families=["embedding"], family_caps={"embedding": 1})

    sql = executed[0][0]
    assert "job_type = 'search_index_sync'" in sql
    assert "capture_jobs.job_type = 'search_index_sync'" in sql


def test_worker_processes_search_index_sync_jobs(monkeypatch):
    monkeypatch.setattr(
        database,
        "sync_search_index",
        lambda **kwargs: {
            "search_engine": "vespa",
            "requested": 2,
            "indexed": 1,
            "deleted": 1,
            "skipped_unchanged": 0,
            "failed": 0,
            "embedding_model": SNOWFLAKE_EMBEDDING_MODEL,
            "embedding_dimensions": SNOWFLAKE_EMBEDDING_DIMENSIONS,
            "model_generation": "snowflake-qwen-paddleocr-v1",
            **kwargs,
        },
    )

    result = worker.process_search_index_sync_job(
        {"payload": {"owner_class": "corpus", "root_name": "docs", "limit": 2}}
    )

    assert result.status == "indexed"
    assert result.telemetry["search_index_engine"] == "vespa"
    assert result.telemetry["search_index_indexed"] == 1
    assert result.telemetry["search_index_deleted"] == 1
    assert result.telemetry["search_index_embedding_dimensions"] == 1024
