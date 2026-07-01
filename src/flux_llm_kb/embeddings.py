from __future__ import annotations

from dataclasses import dataclass
import hashlib
from math import sqrt
import re
from typing import Any


DEFAULT_EMBEDDING_DIMENSIONS = 1536
DEFAULT_EMBEDDING_MODEL = "flux-hash-v1"

_TOKEN_RE = re.compile(r"[A-Za-z0-9_]{2,}")


@dataclass(frozen=True)
class EmbeddingInput:
    owner_table: str
    owner_id: str
    text: str
    model: str = DEFAULT_EMBEDDING_MODEL
    dimensions: int = DEFAULT_EMBEDDING_DIMENSIONS
    existing_source_hash: str | None = None


@dataclass(frozen=True)
class EmbeddingResult:
    owner_table: str
    owner_id: str
    model: str
    dimensions: int
    vector: list[float]
    metadata: dict[str, object]


def embedding_source_hash(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def embedding_cache_key(*, model: str, dimensions: int, source_hash: str) -> str:
    raw = f"{model}\0{dimensions}\0{source_hash}"
    return hashlib.sha256(raw.encode("utf-8")).hexdigest()


class HashEmbeddingProvider:
    name = "hash"

    def __init__(
        self,
        *,
        model: str = DEFAULT_EMBEDDING_MODEL,
        dimensions: int = DEFAULT_EMBEDDING_DIMENSIONS,
    ) -> None:
        self.model = model
        self.dimensions = dimensions

    def embed_batch(self, inputs: list[EmbeddingInput] | tuple[EmbeddingInput, ...]) -> list[EmbeddingResult]:
        results: list[EmbeddingResult] = []
        for item in inputs:
            model = item.model or self.model
            dimensions = int(item.dimensions or self.dimensions)
            source_hash = embedding_source_hash(item.text)
            results.append(
                EmbeddingResult(
                    owner_table=item.owner_table,
                    owner_id=item.owner_id,
                    model=model,
                    dimensions=dimensions,
                    vector=embed_text(item.text, dimensions=dimensions),
                    metadata={
                        "provider": self.name,
                        "model": model,
                        "dimensions": dimensions,
                        "source_hash": source_hash,
                        "cache_key": embedding_cache_key(
                            model=model,
                            dimensions=dimensions,
                            source_hash=source_hash,
                        ),
                    },
                )
            )
        return results


class SnowflakeEmbeddingProvider:
    name = "model_runner"

    def __init__(
        self,
        *,
        model: str = "Snowflake/snowflake-arctic-embed-l-v2.0",
        dimensions: int = 1024,
        model_runner: Any | None = None,
    ) -> None:
        if model_runner is None:
            from .model_runner import ModelRunnerClient

            model_runner = ModelRunnerClient()
        self.model = model
        self.dimensions = int(dimensions or 1024)
        self.model_runner = model_runner

    def embed_batch(self, inputs: list[EmbeddingInput] | tuple[EmbeddingInput, ...]) -> list[EmbeddingResult]:
        items = list(inputs)
        if not items:
            return []
        texts = [item.text for item in items]
        model = items[0].model if items[0].model and items[0].model != DEFAULT_EMBEDDING_MODEL else self.model
        dimensions = int(items[0].dimensions if items[0].dimensions and items[0].dimensions != DEFAULT_EMBEDDING_DIMENSIONS else self.dimensions)
        vectors = self.model_runner.embed(texts, model=model, dimensions=dimensions)
        if len(vectors) != len(items):
            raise ValueError("model-runner returned a different number of embeddings than requested")
        results: list[EmbeddingResult] = []
        for item, vector in zip(items, vectors):
            if len(vector) != dimensions:
                raise ValueError("model-runner embedding dimension mismatch")
            source_hash = embedding_source_hash(item.text)
            results.append(
                EmbeddingResult(
                    owner_table=item.owner_table,
                    owner_id=item.owner_id,
                    model=model,
                    dimensions=dimensions,
                    vector=[float(value) for value in vector],
                    metadata={
                        "provider": self.name,
                        "model": model,
                        "dimensions": dimensions,
                        "source_hash": source_hash,
                        "cache_key": embedding_cache_key(
                            model=model,
                            dimensions=dimensions,
                            source_hash=source_hash,
                        ),
                    },
                )
            )
        return results


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
