from __future__ import annotations

import hashlib
import math
from typing import Any


def evaluate_retrieval_cases(
    cases: list[dict[str, Any]],
    observations: dict[str, dict[str, Any]],
    *,
    limit_per_query: int = 5,
) -> dict[str, Any]:
    bounded_limit = max(1, min(int(limit_per_query or 5), 50))
    case_results = [_evaluate_case(case, observations.get(str(case.get("id") or ""), {}), limit_per_query=bounded_limit) for case in cases]
    query_count = len(case_results)
    passed_count = sum(1 for case in case_results if case["status"] == "passed")
    metrics = {
        "top1_accuracy": _average(case["top1"] for case in case_results),
        "precision_at_3": _average(case["precision_at_3"] for case in case_results),
        "recall_at_5": _average(case["recall_at_5"] for case in case_results),
        "mrr": _average(case["mrr"] for case in case_results),
        "ndcg_at_5": _average(case["ndcg_at_5"] for case in case_results),
        "brief_recall": _average(case["brief_recall"] for case in case_results),
        "brief_dilution": _average(case["brief_dilution"] for case in case_results),
        "scope_pass_count": sum(1 for case in case_results if case["scope_pass"]),
        "suppression_pass_count": sum(1 for case in case_results if case["suppression_pass"]),
        "elapsed_ms": sum(int(case.get("elapsed_ms") or 0) for case in case_results),
    }
    return {
        "query_count": query_count,
        "passed_count": passed_count,
        "failed_count": query_count - passed_count,
        "metrics": metrics,
        "case_results": [
            {
                "case_id": case["case_id"],
                "query_hash": case["query_hash"],
                "status": case["status"],
                "expected_ids": case["expected_ids"],
                "observed_ids": case["observed_ids"],
                "expected_scope": case.get("expected_scope"),
                "observed_scope": case.get("observed_scope"),
                "expected_suppression": case["expected_suppression"],
                "observed_suppression": case["observed_suppression"],
                "rank": case.get("rank"),
                "elapsed_ms": case.get("elapsed_ms", 0),
                "reasons": case["reasons"],
                "result_summaries": case["result_summaries"],
            }
            for case in case_results
        ],
    }


def metric_deltas(current: dict[str, Any], previous: dict[str, Any] | None) -> dict[str, float]:
    if not previous:
        return {}
    deltas: dict[str, float] = {}
    for key, value in current.items():
        if isinstance(value, (int, float)) and isinstance(previous.get(key), (int, float)):
            deltas[key] = round(float(value) - float(previous[key]), 6)
    return deltas


def _evaluate_case(case: dict[str, Any], observation: dict[str, Any], *, limit_per_query: int) -> dict[str, Any]:
    expected_ids = [str(value) for value in case.get("expected_ids") or [] if value]
    expected_brief_ids = [str(value) for value in (case.get("expected_brief_ids") or expected_ids) if value]
    results = [item for item in observation.get("results") or [] if isinstance(item, dict)]
    observed_ids = [str(item.get("id") or "") for item in results if item.get("id")]
    top_ids = observed_ids[:limit_per_query]
    top3 = observed_ids[:3]
    top5 = observed_ids[:5]
    expected_set = set(expected_ids)
    first_rank = _first_rank(top_ids, expected_set)
    top1 = 1.0 if top_ids and top_ids[0] in expected_set else 0.0
    precision_at_3 = 1.0 if expected_set.intersection(top3) else 0.0
    recall_at_5 = (len(expected_set.intersection(top5)) / len(expected_set)) if expected_set else 1.0
    mrr = 0.0 if first_rank is None else 1.0 / float(first_rank)
    ndcg_at_5 = 0.0 if first_rank is None or first_rank > 5 else 1.0 / math.log2(first_rank + 1)
    brief = observation.get("brief") if isinstance(observation.get("brief"), dict) else {}
    packed_ids = [str(item.get("id") or "") for item in brief.get("packed") or [] if isinstance(item, dict)]
    expected_brief_set = set(expected_brief_ids)
    brief_recall = (len(expected_brief_set.intersection(packed_ids)) / len(expected_brief_set)) if expected_brief_set else 1.0
    brief_dilution = 0.0 if not packed_ids else len([item_id for item_id in packed_ids if item_id not in expected_brief_set]) / len(packed_ids)
    observed_scope = _observed_scope(results)
    expected_scope = str(case.get("expected_scope") or "").strip() or None
    scope_pass = True if not expected_scope else observed_scope == expected_scope
    expected_suppression = bool(case.get("expect_suppression"))
    observed_suppression = _observed_suppression(results)
    suppression_pass = observed_suppression if expected_suppression else not observed_suppression
    reasons: list[str] = []
    if top1 < 1.0:
        reasons.append("top1_miss")
    if recall_at_5 < 1.0:
        reasons.append("recall_miss")
    if brief_recall < 1.0:
        reasons.append("brief_miss")
    if not scope_pass:
        reasons.append("scope_miss")
    if not suppression_pass:
        reasons.append("suppression_miss")
    return {
        "case_id": str(case.get("id") or ""),
        "query_hash": _hash_text(str(case.get("query") or "")),
        "expected_ids": expected_ids,
        "observed_ids": top_ids,
        "expected_scope": expected_scope,
        "observed_scope": observed_scope,
        "expected_suppression": expected_suppression,
        "observed_suppression": observed_suppression,
        "rank": first_rank,
        "top1": top1,
        "precision_at_3": precision_at_3,
        "recall_at_5": recall_at_5,
        "mrr": mrr,
        "ndcg_at_5": ndcg_at_5,
        "brief_recall": brief_recall,
        "brief_dilution": brief_dilution,
        "scope_pass": scope_pass,
        "suppression_pass": suppression_pass,
        "elapsed_ms": int(observation.get("elapsed_ms") or 0),
        "reasons": reasons,
        "result_summaries": [_result_summary(index, item) for index, item in enumerate(results[:limit_per_query], start=1)],
        "status": "passed" if not reasons else "failed",
    }


def _first_rank(observed_ids: list[str], expected_ids: set[str]) -> int | None:
    for index, item_id in enumerate(observed_ids, start=1):
        if item_id in expected_ids:
            return index
    return None


def _observed_scope(results: list[dict[str, Any]]) -> str | None:
    if not results:
        return None
    first = results[0]
    explanation = first.get("retrieval_explanation") if isinstance(first.get("retrieval_explanation"), dict) else {}
    scope = explanation.get("scope") if isinstance(explanation.get("scope"), dict) else {}
    return str(first.get("retrieval_scope") or scope.get("label") or "").strip() or None


def _observed_suppression(results: list[dict[str, Any]]) -> bool:
    for item in results:
        if int(item.get("duplicate_count") or 0) > 0:
            return True
        explanation = item.get("retrieval_explanation") if isinstance(item.get("retrieval_explanation"), dict) else {}
        suppression = explanation.get("suppression") if isinstance(explanation.get("suppression"), dict) else {}
        for value in suppression.values():
            if isinstance(value, dict) and int(value.get("suppressed_count") or 0) > 0:
                return True
            if isinstance(value, list) and value:
                return True
    return False


def _result_summary(rank: int, item: dict[str, Any]) -> dict[str, Any]:
    return {
        "rank": rank,
        "id": str(item.get("id") or ""),
        "kind": str(item.get("kind") or ""),
        "logical_kind": str(item.get("logical_kind") or ""),
        "streams": [str(value) for value in item.get("streams") or []],
        "score": float(item.get("score") or 0.0),
    }


def _hash_text(value: str) -> str:
    return f"sha256:{hashlib.sha256(value.encode('utf-8')).hexdigest()}"


def _average(values: Any) -> float:
    rows = [float(value) for value in values]
    if not rows:
        return 0.0
    return round(sum(rows) / len(rows), 6)
