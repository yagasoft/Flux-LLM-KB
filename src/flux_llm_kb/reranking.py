from __future__ import annotations

import os
from typing import Any

from .model_runner import (
    DEFAULT_RERANKER_AWQ_MODEL,
    DEFAULT_RERANKER_MODEL,
    DEFAULT_RERANKER_QUANTIZATION,
    ModelRunnerRerankScorer,
    normalize_reranker_quantization,
    resolve_reranker_quantization,
)


DEFAULT_RERANK_TOP_N = 80
DEFAULT_MAX_RERANK_PASSAGE_TOKENS = 1536
DEFAULT_RERANK_MICROBATCH_SIZE = 8


class QwenReranker:
    def __init__(
        self,
        *,
        scorer: Any | None = None,
        model: str | None = None,
        quantization: str | None = None,
        awq_model: str | None = None,
        top_n: int = DEFAULT_RERANK_TOP_N,
        microbatch_size: int = DEFAULT_RERANK_MICROBATCH_SIZE,
        max_passage_tokens: int = DEFAULT_MAX_RERANK_PASSAGE_TOKENS,
    ) -> None:
        self.scorer = scorer or ModelRunnerRerankScorer()
        resolved_model = str(
            _runtime_setting("retrieval.reranker_model", DEFAULT_RERANKER_MODEL, "FLUX_KB_RETRIEVAL_RERANKER_MODEL")
            if model is None
            else model
        )
        resolved_quantization = str(
            _runtime_setting(
                "retrieval.reranker_quantization",
                DEFAULT_RERANKER_QUANTIZATION,
                "FLUX_KB_RETRIEVAL_RERANKER_QUANTIZATION",
            )
            if quantization is None
            else quantization
        )
        resolved_awq_model = str(
            _runtime_setting(
                "retrieval.reranker_awq_model",
                DEFAULT_RERANKER_AWQ_MODEL,
                "FLUX_KB_RETRIEVAL_RERANKER_AWQ_MODEL",
            )
            if awq_model is None
            else awq_model
        )
        self.model = resolved_model
        self.quantization_profile = resolve_reranker_quantization(
            resolved_quantization,
            model=resolved_model,
            awq_model=resolved_awq_model,
        )
        self.quantization = self.quantization_profile.quantization
        self.requested_quantization = self.quantization_profile.requested_quantization
        self.quantization_backend = self.quantization_profile.backend
        self.load_model = self.quantization_profile.load_model
        self.awq_model = self.quantization_profile.awq_model
        self.top_n = max(1, min(int(top_n or DEFAULT_RERANK_TOP_N), 200))
        self.microbatch_size = max(1, min(int(microbatch_size or DEFAULT_RERANK_MICROBATCH_SIZE), 32))
        self.max_passage_tokens = max(1, min(int(max_passage_tokens or DEFAULT_MAX_RERANK_PASSAGE_TOKENS), 4096))

    def rerank(self, query: str, candidates: list[dict[str, Any]] | tuple[dict[str, Any], ...]) -> list[dict[str, Any]]:
        bounded = [dict(candidate) for candidate in list(candidates)[: self.top_n]]
        scored: list[tuple[float, int, dict[str, Any]]] = []
        for start in range(0, len(bounded), self.microbatch_size):
            batch = bounded[start : start + self.microbatch_size]
            passages = [_candidate_passage(candidate, max_tokens=self.max_passage_tokens) for candidate in batch]
            scores = self.scorer.score(
                query,
                passages,
                model=self.model,
                quantization=self.quantization,
                awq_model=self.awq_model,
            )
            for offset, (candidate, score) in enumerate(zip(batch, scores)):
                enriched = {
                    **candidate,
                    "reranker": {
                        "model": self.model,
                        "quantization": self.quantization,
                        "requested_quantization": self.requested_quantization,
                        "quantization_backend": self.quantization_backend,
                        "load_model": self.load_model,
                        "awq_model": self.awq_model,
                        "score": float(score),
                    },
                }
                scored.append((float(score), start + offset, enriched))
        return [item for _score, _index, item in sorted(scored, key=lambda row: (-row[0], row[1]))]


def _candidate_passage(candidate: dict[str, Any], *, max_tokens: int) -> str:
    title = str(candidate.get("title") or "").strip()
    body = str(candidate.get("summary") or candidate.get("body") or candidate.get("excerpt") or "").strip()
    body_tokens = body.split()
    bounded_body = " ".join(body_tokens[:max_tokens])
    return "\n".join(part for part in (title, bounded_body) if part)


def _runtime_setting(key: str, default: Any, env_var: str | None = None) -> Any:
    if env_var and env_var in os.environ:
        return os.environ[env_var]
    try:
        from .settings import SettingsService
    except Exception:
        return default
    value = SettingsService().resolve(key).raw_value
    return default if value in {None, ""} else value
