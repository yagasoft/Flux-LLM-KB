from math import isclose, sqrt

from flux_llm_kb.embeddings import embed_text, to_pgvector_literal


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
