import pytest

from flux_llm_kb.scoring import (
    ContextCandidate,
    LifecycleScoreInput,
    lifecycle_score,
    pack_context,
    reciprocal_rank_fusion,
)


def test_reciprocal_rank_fusion_combines_ranked_streams():
    fused = reciprocal_rank_fusion(
        {
            "lexical": ["a", "b", "c"],
            "vector": ["b", "d", "a"],
            "graph": ["e", "b"],
        },
        k=60,
    )

    assert fused[0].item_id == "b"
    assert {item.item_id for item in fused} == {"a", "b", "c", "d", "e"}
    assert fused[0].score > fused[-1].score


def test_lifecycle_score_explains_decay_reinforcement_and_confirmation():
    confirmed = lifecycle_score(
        LifecycleScoreInput(
            confidence=0.82,
            age_days=4,
            confirmation_age_days=2,
            reinforcement_count=4,
            reinforcement_age_days=1,
            usage_count=3,
            lifecycle_state="active",
        )
    )
    unconfirmed = lifecycle_score(
        LifecycleScoreInput(
            confidence=0.82,
            age_days=180,
            confirmation_age_days=180,
            reinforcement_count=0,
            reinforcement_age_days=None,
            usage_count=0,
            lifecycle_state="active",
        )
    )

    assert confirmed.score > unconfirmed.score
    assert 0 <= unconfirmed.score <= 1
    assert confirmed.explanation["factors"]["confirmation"] > unconfirmed.explanation["factors"]["confirmation"]
    assert confirmed.explanation["factors"]["reinforcement"] > unconfirmed.explanation["factors"]["reinforcement"]


def test_lifecycle_score_penalizes_supersession_contradictions_retention_and_retirement():
    current = lifecycle_score(
        LifecycleScoreInput(
            confidence=0.9,
            age_days=2,
            confirmation_age_days=1,
            reinforcement_count=2,
            reinforcement_age_days=1,
            usage_count=2,
            lifecycle_state="active",
        )
    )
    contradicted = lifecycle_score(
        LifecycleScoreInput(
            confidence=0.9,
            age_days=2,
            confirmation_age_days=1,
            reinforcement_count=2,
            reinforcement_age_days=1,
            usage_count=2,
            lifecycle_state="contradicted",
            superseded=True,
            contradiction_count=2,
            retention_action="deprioritize",
        )
    )
    retired = lifecycle_score(
        LifecycleScoreInput(
            confidence=0.9,
            age_days=2,
            confirmation_age_days=1,
            reinforcement_count=2,
            reinforcement_age_days=1,
            usage_count=2,
            lifecycle_state="retired",
        )
    )

    assert current.score > contradicted.score > retired.score
    assert contradicted.explanation["penalties"]["supersession"] < 1
    assert contradicted.explanation["penalties"]["contradiction"] < 1
    assert retired.score == 0


def test_lifecycle_score_uses_clean_pre_live_contract_only():
    with pytest.raises(TypeError):
        lifecycle_score(confidence=0.9, age_days=1, usage_count=1, superseded=False)


def test_pack_context_respects_budget_and_orders_by_score():
    candidates = [
        ContextCandidate(id="low", title="Low", body="one two three", score=0.1),
        ContextCandidate(id="high", title="High", body="alpha beta gamma", score=0.9),
        ContextCandidate(id="mid", title="Mid", body="four five six", score=0.5),
    ]

    packed = pack_context(candidates, token_budget=12)

    assert "High" in packed
    assert packed.index("High") < packed.index("Mid")
    assert "Low" not in packed
