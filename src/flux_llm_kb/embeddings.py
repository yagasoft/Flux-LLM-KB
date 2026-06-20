from __future__ import annotations

import hashlib
from math import sqrt
import re


DEFAULT_EMBEDDING_DIMENSIONS = 1536
DEFAULT_EMBEDDING_MODEL = "flux-hash-v1"

_TOKEN_RE = re.compile(r"[A-Za-z0-9_]{2,}")


def embed_text(text: str, dimensions: int = DEFAULT_EMBEDDING_DIMENSIONS) -> list[float]:
    """Create a deterministic local embedding suitable for pgvector smoke retrieval.

    This is not a neural embedding model. It is a privacy-preserving, dependency-free
    hashed lexical vector that makes the vector pipeline functional from day one. The
    provider boundary lets V1 swap in local sentence-transformers or an API model later.
    """
    vector = [0.0] * dimensions

    for token in _TOKEN_RE.findall(text.lower()):
        digest = hashlib.blake2b(token.encode("utf-8"), digest_size=8).digest()
        index = int.from_bytes(digest[:4], "big") % dimensions
        sign = 1.0 if digest[4] & 1 else -1.0
        vector[index] += sign

    norm = sqrt(sum(value * value for value in vector))
    if norm == 0:
        return vector
    return [round(value / norm, 6) for value in vector]


def to_pgvector_literal(vector: list[float]) -> str:
    return "[" + ",".join(f"{value:.6f}" for value in vector) + "]"
