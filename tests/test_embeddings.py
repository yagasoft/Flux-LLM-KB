from flux_llm_kb.embeddings import (
    DEFAULT_EMBEDDING_DIMENSIONS,
    DEFAULT_EMBEDDING_MODEL,
    EmbeddingInput,
    SnowflakeEmbeddingProvider,
    embedding_cache_key,
    embedding_source_hash,
)
from flux_llm_kb.search_index import SNOWFLAKE_EMBEDDING_DIMENSIONS, SNOWFLAKE_EMBEDDING_MODEL


class FakeModelRunner:
    def __init__(self):
        self.calls = []

    def embed(self, texts, *, model, dimensions):
        self.calls.append({"texts": list(texts), "model": model, "dimensions": dimensions})
        return [[1.0] + [0.0] * (dimensions - 1) for _ in texts]


def test_snowflake_embedding_provider_batches_vectors_with_redacted_metadata():
    source_hash = embedding_source_hash("Private project alpha notes")
    runner = FakeModelRunner()
    provider = SnowflakeEmbeddingProvider(model_runner=runner)

    results = provider.embed_batch(
        [
            EmbeddingInput(
                owner_table="asset_chunks",
                owner_id="chunk-1",
                text="Private project alpha notes",
                model=DEFAULT_EMBEDDING_MODEL,
                dimensions=DEFAULT_EMBEDDING_DIMENSIONS,
            )
        ]
    )

    assert len(results) == 1
    assert DEFAULT_EMBEDDING_MODEL == SNOWFLAKE_EMBEDDING_MODEL
    assert DEFAULT_EMBEDDING_DIMENSIONS == SNOWFLAKE_EMBEDDING_DIMENSIONS
    assert results[0].vector == [1.0] + [0.0] * (SNOWFLAKE_EMBEDDING_DIMENSIONS - 1)
    assert runner.calls == [
        {
            "texts": ["Private project alpha notes"],
            "model": SNOWFLAKE_EMBEDDING_MODEL,
            "dimensions": SNOWFLAKE_EMBEDDING_DIMENSIONS,
        }
    ]
    assert results[0].metadata == {
        "provider": "model_runner",
        "model": DEFAULT_EMBEDDING_MODEL,
        "dimensions": DEFAULT_EMBEDDING_DIMENSIONS,
        "source_hash": source_hash,
        "cache_key": embedding_cache_key(
            model=DEFAULT_EMBEDDING_MODEL,
            dimensions=DEFAULT_EMBEDDING_DIMENSIONS,
            source_hash=source_hash,
        ),
    }
    assert "Private" not in str(results[0].metadata)


def test_embedding_source_hash_is_stable_and_text_sensitive():
    first = embedding_source_hash("same semantic source")
    second = embedding_source_hash("same semantic source")
    different = embedding_source_hash("different semantic source")

    assert first == second
    assert first != different
    assert len(first) == 64
