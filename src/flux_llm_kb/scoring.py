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


@dataclass(frozen=True)
class LifecycleScoreInput:
    confidence: float
    age_days: float
    confirmation_age_days: float | None = None
    reinforcement_count: int = 0
    reinforcement_age_days: float | None = None
    usage_count: int = 0
    lifecycle_state: str = "active"
    superseded: bool = False
    contradiction_count: int = 0
    retention_action: str = "keep"


@dataclass(frozen=True)
class LifecycleScoreResult:
    score: float
    explanation: dict[str, dict[str, float] | str]


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


def lifecycle_score(signal: LifecycleScoreInput) -> LifecycleScoreResult:
    confidence = _clamp(signal.confidence)
    recency = exp(-max(signal.age_days, 0.0) / 120.0)
    confirmation = _freshness_factor(signal.confirmation_age_days, half_life_days=90.0)
    reinforcement = _reinforcement_factor(
        count=signal.reinforcement_count,
        age_days=signal.reinforcement_age_days,
    )
    usage = min(log1p(max(signal.usage_count, 0)) / log1p(20), 1.0)

    base = (
        (confidence * 0.40)
        + (recency * 0.20)
        + (confirmation * 0.20)
        + (reinforcement * 0.15)
        + (usage * 0.05)
    )
    penalties = _lifecycle_penalties(signal)
    score = base
    for penalty in penalties.values():
        score *= penalty

    score = _clamp(score)
    return LifecycleScoreResult(
        score=score,
        explanation={
            "state": signal.lifecycle_state,
            "retention_action": signal.retention_action,
            "factors": {
                "confidence": confidence,
                "recency": recency,
                "confirmation": confirmation,
                "reinforcement": reinforcement,
                "usage": usage,
            },
            "penalties": penalties,
        },
    )


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


def _freshness_factor(age_days: float | None, *, half_life_days: float) -> float:
    if age_days is None:
        return 0.35
    return exp(-max(age_days, 0.0) / half_life_days)


def _reinforcement_factor(*, count: int, age_days: float | None) -> float:
    volume = min(log1p(max(count, 0)) / log1p(10), 1.0)
    freshness = _freshness_factor(age_days, half_life_days=60.0) if count > 0 else 0.0
    return _clamp((volume * 0.7) + (freshness * 0.3))


def _lifecycle_penalties(signal: LifecycleScoreInput) -> dict[str, float]:
    state = signal.lifecycle_state.lower().replace("-", "_")
    state_penalties = {
        "active": 1.0,
        "confirmed": 1.0,
        "reinforced": 1.0,
        "stale": 0.55,
        "deprioritized": 0.55,
        "superseded": 0.25,
        "contradicted": 0.45,
        "retired": 0.0,
        "deleted": 0.0,
    }
    retention_penalties = {
        "keep": 1.0,
        "review": 0.85,
        "deprioritize": 0.60,
        "retire": 0.20,
        "delete": 0.0,
    }
    contradiction_count = max(signal.contradiction_count, 0)
    return {
        "state": state_penalties.get(state, 0.75),
        "supersession": 0.25 if signal.superseded or state == "superseded" else 1.0,
        "contradiction": 1.0 / (1.0 + (0.45 * contradiction_count)),
        "retention": retention_penalties.get(signal.retention_action.lower(), 0.75),
    }
