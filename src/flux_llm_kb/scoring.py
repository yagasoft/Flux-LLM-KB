from __future__ import annotations

from dataclasses import dataclass
from math import exp, log1p
from typing import Iterable, Mapping


@dataclass(frozen=True)
class RankedItem:
    item_id: str
    score: float
    streams: tuple[str, ...]


@dataclass(frozen=True)
class ContextCandidate:
    id: str
    title: str
    body: str
    score: float


def reciprocal_rank_fusion(
    ranked_streams: Mapping[str, Iterable[str]], k: int = 60
) -> list[RankedItem]:
    scores: dict[str, float] = {}
    streams: dict[str, set[str]] = {}

    for stream_name, stream in ranked_streams.items():
        for rank, item_id in enumerate(stream, start=1):
            scores[item_id] = scores.get(item_id, 0.0) + 1.0 / (k + rank)
            streams.setdefault(item_id, set()).add(stream_name)

    return [
        RankedItem(item_id=item_id, score=score, streams=tuple(sorted(streams[item_id])))
        for item_id, score in sorted(scores.items(), key=lambda item: (-item[1], item[0]))
    ]


def lifecycle_score(
    *, confidence: float, age_days: float, usage_count: int, superseded: bool
) -> float:
    confidence = _clamp(confidence)
    recency = exp(-max(age_days, 0.0) / 90.0)
    reinforcement = min(log1p(max(usage_count, 0)) / log1p(20), 1.0)
    score = (confidence * 0.55) + (recency * 0.30) + (reinforcement * 0.15)
    if superseded:
        score *= 0.15
    return _clamp(score)


def pack_context(candidates: Iterable[ContextCandidate], token_budget: int) -> str:
    if token_budget <= 0:
        return ""

    packed: list[str] = []
    used = 0
    for candidate in sorted(candidates, key=lambda item: (-item.score, item.title, item.id)):
        block = f"### {candidate.title}\n{candidate.body.strip()}"
        cost = _estimate_tokens(block)
        if used + cost > token_budget:
            continue
        packed.append(block)
        used += cost

    return "\n\n".join(packed)


def _estimate_tokens(text: str) -> int:
    return max(1, len(text.split()))


def _clamp(value: float) -> float:
    return max(0.0, min(1.0, value))
