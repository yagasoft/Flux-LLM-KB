from flux_llm_kb.scoring import (
    ContextCandidate,
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


def test_lifecycle_score_rewards_confidence_recency_and_reinforcement():
    fresh = lifecycle_score(confidence=0.9, age_days=2, usage_count=5, superseded=False)
    stale = lifecycle_score(confidence=0.9, age_days=120, usage_count=0, superseded=False)
    superseded = lifecycle_score(confidence=0.9, age_days=2, usage_count=5, superseded=True)

    assert fresh > stale
    assert superseded < stale
    assert 0 <= superseded <= 1


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

