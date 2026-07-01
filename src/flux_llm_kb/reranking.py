from __future__ import annotations

from typing import Any

from .model_runner import ModelRunnerRerankScorer


DEFAULT_RERANKER_MODEL = "Qwen/Qwen3-Reranker-4B"
DEFAULT_RERANKER_QUANTIZATION = "int4_awq"
DEFAULT_RERANK_TOP_N = 80
DEFAULT_MAX_RERANK_PASSAGE_TOKENS = 1536
DEFAULT_RERANK_MICROBATCH_SIZE = 8


class QwenReranker:
    def __init__(
        self,
        *,
        scorer: Any | None = None,
        model: str = DEFAULT_RERANKER_MODEL,
        quantization: str = DEFAULT_RERANKER_QUANTIZATION,
        top_n: int = DEFAULT_RERANK_TOP_N,
        microbatch_size: int = DEFAULT_RERANK_MICROBATCH_SIZE,
        max_passage_tokens: int = DEFAULT_MAX_RERANK_PASSAGE_TOKENS,
    ) -> None:
        self.scorer = scorer or ModelRunnerRerankScorer()
        self.model = model
        self.quantization = quantization
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
            )
            for offset, (candidate, score) in enumerate(zip(batch, scores)):
                enriched = {
                    **candidate,
                    "reranker": {
                        "model": self.model,
                        "quantization": self.quantization,
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
