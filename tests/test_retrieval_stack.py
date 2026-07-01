import json

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
    assert payload["file_kinds"] == ["text"]
    assert payload["languages"] == ["markdown"]
    assert results[0]["match_features"]["bm25(body)"] == 3.5


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
