from math import isclose, sqrt

from flux_llm_kb.embeddings import (
    DEFAULT_EMBEDDING_MODEL,
    EmbeddingInput,
    HashEmbeddingProvider,
    embed_text,
    embedding_cache_key,
    embedding_source_hash,
    to_pgvector_literal,
)


def test_embed_text_is_deterministic_and_normalized():
    first = embed_text("PostgreSQL pgvector memory kernel", dimensions=64)
    second = embed_text("PostgreSQL pgvector memory kernel", dimensions=64)

    assert first == second
    assert len(first) == 64
    norm = sqrt(sum(value * value for value in first))
    assert isclose(norm, 1.0, rel_tol=1e-6)


def test_pgvector_literal_uses_vector_array_syntax():
    literal = to_pgvector_literal([0.1, -0.25, 0.0])

    assert literal == "[0.100000,-0.250000,0.000000]"


def test_hash_embedding_provider_batches_vectors_with_redacted_metadata():
    source_hash = embedding_source_hash("Private project alpha notes")
    provider = HashEmbeddingProvider(dimensions=64)

    results = provider.embed_batch(
        [
            EmbeddingInput(
                owner_table="asset_chunks",
                owner_id="chunk-1",
                text="Private project alpha notes",
                model=DEFAULT_EMBEDDING_MODEL,
                dimensions=64,
            )
        ]
    )

    assert len(results) == 1
    assert results[0].vector == embed_text("Private project alpha notes", dimensions=64)
    assert results[0].metadata == {
        "provider": "hash",
        "model": DEFAULT_EMBEDDING_MODEL,
        "dimensions": 64,
        "source_hash": source_hash,
        "cache_key": embedding_cache_key(
            model=DEFAULT_EMBEDDING_MODEL,
            dimensions=64,
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
