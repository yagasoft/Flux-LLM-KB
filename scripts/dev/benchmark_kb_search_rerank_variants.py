from __future__ import annotations

import argparse
import contextlib
import hashlib
import json
import os
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator

from flux_llm_kb import database, reranking
from flux_llm_kb.retrieval_benchmark import evaluate_retrieval_cases
from flux_llm_kb.service import (
    KnowledgeService,
    RetrievalScope,
    _apply_retrieval_filters,
    _brief_selection_trace,
    _dedupe_search_results,
    _effective_retrieval_filters,
    _enrich_search_results,
    _has_lexical_or_fuzzy_evidence,
    _normalize_retrieval_benchmark_suite,
    _resolve_retrieval_scope,
    normalize_retrieval_filters,
)


@dataclass(frozen=True)
class Variant:
    name: str
    rerank: bool = True
    pool: int = 12
    microbatch_size: int = 1
    max_passage_tokens: int = 1536
    mode: str = "production"


VARIANTS: tuple[Variant, ...] = (
    Variant("vespa_no_rerank", rerank=False, pool=80, microbatch_size=1, max_passage_tokens=1536),
    Variant("pool12_mb1_tok1536", pool=12, microbatch_size=1, max_passage_tokens=1536),
    Variant("pool12_mb1_tok768", pool=12, microbatch_size=1, max_passage_tokens=768),
    Variant("pool12_mb1_tok384", pool=12, microbatch_size=1, max_passage_tokens=384),
    Variant("pool12_mb2_tok1536", pool=12, microbatch_size=2, max_passage_tokens=1536),
    Variant("pool20_mb1_tok1536", pool=20, microbatch_size=1, max_passage_tokens=1536),
    Variant("pool40_mb1_tok1536", pool=40, microbatch_size=1, max_passage_tokens=1536),
    Variant("pool80_mb1_tok1536", pool=80, microbatch_size=1, max_passage_tokens=1536),
    Variant("pool20_mb1_tok768", pool=20, microbatch_size=1, max_passage_tokens=768),
    Variant("pool20_mb1_tok384", pool=20, microbatch_size=1, max_passage_tokens=384),
    Variant("pool20_mb2_tok1536", pool=20, microbatch_size=2, max_passage_tokens=1536),
    Variant("pool20_mb4_tok1536", pool=20, microbatch_size=4, max_passage_tokens=1536),
    Variant("single_final_pool20_mb1_tok1536", pool=20, microbatch_size=1, max_passage_tokens=1536, mode="single_final"),
)


class IdentityReranker:
    model = "vespa_no_rerank"
    quantization = "none"
    requested_quantization = "none"
    quantization_backend = "none"
    load_model = "none"
    awq_model = ""
    max_passage_tokens = 0
    microbatch_size = 1

    def __init__(self, *, top_n: int = 80, **_kwargs: Any) -> None:
        self.top_n = max(1, int(top_n or 80))

    def rerank(self, _query: str, candidates: list[dict[str, Any]] | tuple[dict[str, Any], ...]) -> list[dict[str, Any]]:
        return [
            {
                **dict(candidate),
                "reranker": {
                    "model": self.model,
                    "quantization": self.quantization,
                    "requested_quantization": self.requested_quantization,
                    "quantization_backend": self.quantization_backend,
                    "load_model": self.load_model,
                    "awq_model": self.awq_model,
                    "score": float(candidate.get("score") or 0.0),
                },
            }
            for candidate in list(candidates)[: self.top_n]
        ]


@contextlib.contextmanager
def variant_context(variant: Variant) -> Iterator[None]:
    env_keys = (
        "FLUX_KB_RETRIEVAL_RERANK_TOP_N",
        "FLUX_KB_RETRIEVAL_RERANK_MICROBATCH_SIZE",
        "FLUX_KB_RETRIEVAL_MAX_RERANK_PASSAGE_TOKENS",
    )
    old_env = {key: os.environ.get(key) for key in env_keys}
    old_min_pool = database._MIN_RERANK_POOL_SIZE
    old_multiplier = database._RERANK_POOL_LIMIT_MULTIPLIER
    old_default_top_n = database._DEFAULT_RERANK_POOL_TOP_N
    old_reranker = reranking.QwenReranker
    try:
        os.environ["FLUX_KB_RETRIEVAL_RERANK_TOP_N"] = str(variant.pool)
        os.environ["FLUX_KB_RETRIEVAL_RERANK_MICROBATCH_SIZE"] = str(variant.microbatch_size)
        os.environ["FLUX_KB_RETRIEVAL_MAX_RERANK_PASSAGE_TOKENS"] = str(variant.max_passage_tokens)
        database._MIN_RERANK_POOL_SIZE = int(variant.pool)
        database._RERANK_POOL_LIMIT_MULTIPLIER = 0
        database._DEFAULT_RERANK_POOL_TOP_N = int(variant.pool)
        if not variant.rerank:
            reranking.QwenReranker = IdentityReranker
        yield
    finally:
        for key, value in old_env.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value
        database._MIN_RERANK_POOL_SIZE = old_min_pool
        database._RERANK_POOL_LIMIT_MULTIPLIER = old_multiplier
        database._DEFAULT_RERANK_POOL_TOP_N = old_default_top_n
        reranking.QwenReranker = old_reranker


@contextlib.contextmanager
def identity_reranker_context() -> Iterator[None]:
    old_reranker = reranking.QwenReranker
    try:
        reranking.QwenReranker = IdentityReranker
        yield
    finally:
        reranking.QwenReranker = old_reranker


def run_standard_suite(
    service: KnowledgeService,
    *,
    limits: list[int],
    token_budget: int,
    variants: list[Variant],
    max_cases: int | None,
) -> dict[str, Any]:
    suite = _normalize_retrieval_benchmark_suite("standard")
    cases, cleanup = service._prepare_retrieval_benchmark_cases(suite)
    if max_cases is not None:
        cases = cases[: max(1, int(max_cases))]
    try:
        runs: list[dict[str, Any]] = []
        for limit in limits:
            for variant in variants:
                started = time.perf_counter()
                observations: dict[str, dict[str, Any]] = {}
                diagnostics_rows: list[dict[str, Any]] = []
                with variant_context(variant):
                    for case in cases:
                        case_started = time.perf_counter()
                        explain_payload = _run_explain_for_variant(
                            service,
                            variant,
                            str(case["query"]),
                            limit=limit,
                            token_budget=token_budget,
                            root_name=case.get("root_name"),
                            scope_mode=str(case.get("scope_mode") or "local_first"),
                            filters=case.get("filters"),
                        )
                        diagnostics_rows.append(_diagnostic_summary(explain_payload.get("retrieval_timing")))
                        observations[str(case["id"])] = {
                            "results": explain_payload.get("results") or [],
                            "brief": explain_payload.get("brief") or {},
                            "elapsed_ms": max(0, int((time.perf_counter() - case_started) * 1000)),
                        }
                report = evaluate_retrieval_cases(cases, observations, limit_per_query=limit)
                runs.append(
                    {
                        "variant": variant.__dict__,
                        "limit_per_query": limit,
                        "elapsed_ms": max(0, int((time.perf_counter() - started) * 1000)),
                        "query_count": report["query_count"],
                        "passed_count": report["passed_count"],
                        "failed_count": report["failed_count"],
                        "metrics": report["metrics"],
                        "diagnostics": _aggregate_diagnostics(diagnostics_rows),
                        "case_results": report["case_results"],
                    }
                )
        return {"suite": suite, "runs": runs}
    finally:
        cleanup()


def run_live_queries(
    service: KnowledgeService,
    *,
    queries: list[str],
    variants: list[Variant],
    root_name: str | None,
    scope_mode: str,
    limit: int,
    token_budget: int,
) -> dict[str, Any]:
    runs: list[dict[str, Any]] = []
    for query in queries:
        query_hash = _hash_text(query)
        variant_rows: list[dict[str, Any]] = []
        for variant in variants:
            started = time.perf_counter()
            with variant_context(variant):
                payload = _run_explain_for_variant(
                    service,
                    variant,
                    query,
                    limit=limit,
                    token_budget=token_budget,
                    root_name=root_name,
                    scope_mode=scope_mode,
                    filters=None,
                )
            results = payload.get("results") if isinstance(payload.get("results"), list) else []
            variant_rows.append(
                {
                    "variant": variant.__dict__,
                    "elapsed_ms": max(0, int((time.perf_counter() - started) * 1000)),
                    "result_count": len(results),
                    "top_result_hash": _hash_text(str(results[0].get("id") or "")) if results else None,
                    "top_scope": str(results[0].get("retrieval_scope") or "") if results else None,
                    "result_hashes": [_hash_text(str(item.get("id") or "")) for item in results[:5] if isinstance(item, dict)],
                    "diagnostics": _diagnostic_summary(payload.get("retrieval_timing")),
                }
            )
        oracle = next((row for row in variant_rows if row["variant"]["name"] == "pool80_mb1_tok1536"), None)
        oracle_top = oracle.get("top_result_hash") if isinstance(oracle, dict) else None
        oracle_results = set(oracle.get("result_hashes") or []) if isinstance(oracle, dict) else set()
        for row in variant_rows:
            row["top1_matches_pool80_oracle"] = bool(oracle_top and row.get("top_result_hash") == oracle_top)
            row["overlap_at_5_with_pool80_oracle"] = len(set(row.get("result_hashes") or []).intersection(oracle_results))
        runs.append({"query_hash": query_hash, "root_name": root_name, "scope_mode": scope_mode, "limit": limit, "variants": variant_rows})
    return {"runs": runs}


def _run_explain_for_variant(
    service: KnowledgeService,
    variant: Variant,
    query: str,
    *,
    limit: int,
    token_budget: int,
    root_name: str | None,
    scope_mode: str,
    filters: dict[str, Any] | None,
) -> dict[str, Any]:
    if variant.mode == "single_final":
        return _single_final_explain(
            service,
            query,
            limit=limit,
            token_budget=token_budget,
            root_name=root_name,
            scope_mode=scope_mode,
            filters=filters,
            rerank_pool=variant.pool,
        )
    return service.explain(
        query,
        limit=limit,
        token_budget=token_budget,
        root_name=root_name,
        scope_mode=scope_mode,
        filters=filters,
    )


def _single_final_explain(
    service: KnowledgeService,
    query: str,
    *,
    limit: int,
    token_budget: int,
    root_name: str | None,
    scope_mode: str,
    filters: dict[str, Any] | None,
    rerank_pool: int,
) -> dict[str, Any]:
    normalized_filters = normalize_retrieval_filters(filters) if filters is not None else None
    effective_filters = _effective_retrieval_filters(normalized_filters)
    scope = _resolve_retrieval_scope(cwd=None, root_name=root_name, scope_mode=scope_mode)
    result_limit = max(1, min(int(limit or 5), 50))
    retrieval_limit = max(result_limit, int(rerank_pool))
    diagnostics: dict[str, Any] = {"single_final": {"input_scopes": []}}
    with identity_reranker_context():
        if scope.mode == "global" or not scope.is_scoped:
            raw_candidates = service._search_once(
                query,
                limit=retrieval_limit,
                rerank_limit=rerank_pool,
                scope=RetrievalScope(mode="global"),
                label="global",
                filters=effective_filters,
                diagnostics=diagnostics,
            )
            diagnostics["single_final"]["input_scopes"].append("global")
        else:
            local_candidates = service._search_once(
                query,
                limit=retrieval_limit,
                rerank_limit=rerank_pool,
                scope=scope,
                label="local",
                filters=effective_filters,
                diagnostics=diagnostics,
            )
            raw_candidates = list(local_candidates)
            diagnostics["single_final"]["input_scopes"].append("local")
            if scope.mode != "local_only" and not _has_lexical_or_fuzzy_evidence(local_candidates):
                raw_candidates.extend(
                    service._search_once(
                        query,
                        limit=retrieval_limit,
                        rerank_limit=rerank_pool,
                        scope=RetrievalScope(mode="global"),
                        label="global_fallback",
                        filters=effective_filters,
                        diagnostics=diagnostics,
                    )
                )
                diagnostics["single_final"]["input_scopes"].append("global_fallback")
    deduped = _dedupe_search_results(raw_candidates)
    rerank_started = time.perf_counter()
    reranker = reranking.QwenReranker(top_n=rerank_pool)
    reranked = reranker.rerank(query, deduped[:rerank_pool])
    diagnostics["single_final"]["reranker"] = {
        "input_count": len(deduped[:rerank_pool]),
        "returned_count": min(result_limit, len(reranked)),
        "latency_ms": max(0, int((time.perf_counter() - rerank_started) * 1000)),
        "top_n": reranker.top_n,
        "microbatch_size": reranker.microbatch_size,
        "microbatch_count": (len(deduped[:rerank_pool]) + reranker.microbatch_size - 1) // reranker.microbatch_size,
        "max_passage_tokens": reranker.max_passage_tokens,
    }
    filtered_results, excluded = _apply_retrieval_filters(reranked, effective_filters)
    search_results = _enrich_search_results(query, filtered_results, retrieval_filters=normalized_filters)
    payload = {
        "results": search_results[:result_limit],
        "brief": _brief_selection_trace(search_results, token_budget=token_budget),
        "retrieval_timing": diagnostics,
    }
    if normalized_filters is not None:
        payload["filters"] = normalized_filters
        payload["filter_trace"] = {"excluded": excluded}
    return payload


def _diagnostic_summary(value: Any) -> dict[str, Any]:
    rows = list(_iter_reranker_diagnostics(value if isinstance(value, dict) else {}))
    return _aggregate_diagnostics(rows)


def _iter_reranker_diagnostics(value: dict[str, Any]) -> Iterator[dict[str, Any]]:
    scopes = value.get("scopes") if isinstance(value.get("scopes"), dict) else {}
    for scope_label, scope_payload in scopes.items():
        if not isinstance(scope_payload, dict):
            continue
        corpus = scope_payload.get("corpus") if isinstance(scope_payload.get("corpus"), dict) else {}
        reranker = corpus.get("reranker") if isinstance(corpus.get("reranker"), dict) else {}
        if reranker:
            yield {"scope": str(scope_label), **reranker}
    single_final = value.get("single_final") if isinstance(value.get("single_final"), dict) else {}
    reranker = single_final.get("reranker") if isinstance(single_final.get("reranker"), dict) else {}
    if reranker:
        yield {"scope": "single_final", **reranker}


def _aggregate_diagnostics(rows: list[dict[str, Any]]) -> dict[str, Any]:
    if not rows:
        return {
            "rerank_calls": 0,
            "rerank_input_count": 0,
            "rerank_microbatch_count": 0,
            "rerank_latency_ms": 0,
            "max_rerank_input_count": 0,
            "max_passage_chars": 0,
            "max_passage_words": 0,
        }
    rerank_calls = 0
    rerank_input_count = 0
    rerank_microbatch_count = 0
    rerank_latency_ms = 0
    max_rerank_input_count = 0
    max_passage_chars = 0
    max_passage_words = 0
    for row in rows:
        calls = _diagnostic_int(row, "rerank_calls", default=1)
        rerank_calls += calls
        input_count = _diagnostic_int(row, "input_count", fallback="rerank_input_count")
        microbatch_count = _diagnostic_int(row, "microbatch_count", fallback="rerank_microbatch_count")
        latency_ms = _diagnostic_int(row, "latency_ms", fallback="rerank_latency_ms")
        max_input = _diagnostic_int(row, "max_rerank_input_count", default=input_count)
        rerank_input_count += input_count
        rerank_microbatch_count += microbatch_count
        rerank_latency_ms += latency_ms
        max_rerank_input_count = max(max_rerank_input_count, max_input)
        max_passage_chars = max(max_passage_chars, _diagnostic_int(row, "max_passage_chars", default=_summary_max(row.get("passage_chars"))))
        max_passage_words = max(max_passage_words, _diagnostic_int(row, "max_passage_words", default=_summary_max(row.get("passage_words"))))
    return {
        "rerank_calls": rerank_calls,
        "rerank_input_count": rerank_input_count,
        "rerank_microbatch_count": rerank_microbatch_count,
        "rerank_latency_ms": rerank_latency_ms,
        "max_rerank_input_count": max_rerank_input_count,
        "max_passage_chars": max_passage_chars,
        "max_passage_words": max_passage_words,
    }


def _diagnostic_int(row: dict[str, Any], key: str, *, fallback: str | None = None, default: int = 0) -> int:
    value = row.get(key)
    if value is None and fallback:
        value = row.get(fallback)
    if value is None:
        value = default
    try:
        return int(value or 0)
    except (TypeError, ValueError):
        return int(default)


def _summary_max(value: Any) -> int:
    if isinstance(value, dict):
        try:
            return int(value.get("max") or 0)
        except (TypeError, ValueError):
            return 0
    return 0


def _hash_text(value: str) -> str:
    return f"sha256:{hashlib.sha256(value.encode('utf-8')).hexdigest()}"


def _parse_limits(value: str) -> list[int]:
    limits = []
    for part in value.split(","):
        text = part.strip()
        if text:
            limits.append(max(1, min(int(text), 50)))
    return limits or [1]


def _select_variants(value: str | None) -> list[Variant]:
    if not value:
        return list(VARIANTS)
    requested = {part.strip() for part in value.split(",") if part.strip()}
    selected = [variant for variant in VARIANTS if variant.name in requested]
    missing = sorted(requested - {variant.name for variant in selected})
    if missing:
        raise ValueError(f"unknown variants: {', '.join(missing)}")
    return selected


def main() -> int:
    parser = argparse.ArgumentParser(description="Benchmark KB Search rerank parameter variants.")
    parser.add_argument("--limits", default="1,5", help="Comma-separated synthetic benchmark result limits.")
    parser.add_argument("--variants", default="", help="Comma-separated variant names. Defaults to all variants.")
    parser.add_argument("--max-cases", type=int, default=0, help="Limit synthetic cases for smoke tests. Defaults to all cases.")
    parser.add_argument("--token-budget", type=int, default=1200)
    parser.add_argument("--live-query", action="append", default=[])
    parser.add_argument("--live-root-name", default=None)
    parser.add_argument("--live-scope-mode", default="local_first")
    parser.add_argument("--live-limit", type=int, default=1)
    parser.add_argument("--output", default="")
    args = parser.parse_args()

    service = KnowledgeService()
    selected_variants = _select_variants(args.variants)
    started_at = datetime.now(timezone.utc).isoformat()
    result: dict[str, Any] = {
        "started_at": started_at,
        "settings_mutated": False,
        "variants": [variant.__dict__ for variant in selected_variants],
        "standard": run_standard_suite(
            service,
            limits=_parse_limits(args.limits),
            token_budget=max(1, args.token_budget),
            variants=selected_variants,
            max_cases=args.max_cases if int(args.max_cases or 0) > 0 else None,
        ),
    }
    if args.live_query:
        result["live"] = run_live_queries(
            service,
            queries=[str(query) for query in args.live_query],
            variants=selected_variants,
            root_name=args.live_root_name or None,
            scope_mode=args.live_scope_mode,
            limit=max(1, min(int(args.live_limit or 1), 50)),
            token_budget=max(1, args.token_budget),
        )
    result["completed_at"] = datetime.now(timezone.utc).isoformat()

    payload = json.dumps(result, indent=2, sort_keys=True)
    if args.output:
        Path(args.output).write_text(payload + "\n", encoding="utf-8")
    print(payload)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
