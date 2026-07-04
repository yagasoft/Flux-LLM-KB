from __future__ import annotations

from dataclasses import dataclass
import hashlib
from typing import Any

from .search_index import SNOWFLAKE_EMBEDDING_DIMENSIONS, SNOWFLAKE_EMBEDDING_MODEL

DEFAULT_EMBEDDING_DIMENSIONS = SNOWFLAKE_EMBEDDING_DIMENSIONS
DEFAULT_EMBEDDING_MODEL = SNOWFLAKE_EMBEDDING_MODEL


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


class SnowflakeEmbeddingProvider:
    name = "model_runner"

    def __init__(
        self,
        *,
        model: str = DEFAULT_EMBEDDING_MODEL,
        dimensions: int = DEFAULT_EMBEDDING_DIMENSIONS,
        model_runner: Any | None = None,
        timeout_seconds: float | None = None,
    ) -> None:
        if model_runner is None:
            from .model_runner import ModelRunnerClient

            model_runner = ModelRunnerClient()
        self.model = model
        self.dimensions = int(dimensions or DEFAULT_EMBEDDING_DIMENSIONS)
        self.model_runner = model_runner
        self.timeout_seconds = float(timeout_seconds) if timeout_seconds is not None else None

    def embed_batch(self, inputs: list[EmbeddingInput] | tuple[EmbeddingInput, ...]) -> list[EmbeddingResult]:
        items = list(inputs)
        if not items:
            return []
        texts = [item.text for item in items]
        model = items[0].model or self.model
        dimensions = int(items[0].dimensions or self.dimensions)
        embed_kwargs: dict[str, Any] = {"model": model, "dimensions": dimensions}
        if self.timeout_seconds is not None:
            embed_kwargs["timeout_seconds"] = self.timeout_seconds
        vectors = self.model_runner.embed(texts, **embed_kwargs)
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
