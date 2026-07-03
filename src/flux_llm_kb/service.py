from __future__ import annotations

from collections import Counter
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
import base64
import fnmatch
import hashlib
import json
from pathlib import Path
from pathlib import PurePosixPath, PureWindowsPath
import re
import shutil
import tempfile
import threading
import time
from typing import Any, Callable
import uuid
from uuid import UUID, uuid4

from .acceleration import (
    BENCHMARK_FIXTURES,
    FAMILY_DEFAULT_CAPS,
    JOB_FAMILIES,
    collect_acceleration_status,
    job_family_for_type,
    kind_to_job_families,
)
from .crawler import CorpusPolicy, _is_included, scan_path, strict_indexing_enabled
from . import __version__, acceleration, database, governance, operator_automation
from .glob_policy import effective_glob_policy
from .extractors import extractor_availability
from .indexer_diagnostics import (
    build_benchmark_recommendations,
    build_indexer_diagnostics,
    normalize_benchmark_scenario,
    scenario_recommendation_metadata,
)
from .code_diagnostics import build_code_status_report, sanitize_code_lookup, sanitize_code_result
from .indexer_reliability import build_indexer_reliability_report, build_root_reliability_card, build_roots_reliability_report
from .operator_evidence import build_operator_evidence_report
from .operational_diagnostics import summarize_operational_diagnostics
from .processes import run_no_window
from .redaction import RedactionFinding, redact_text
from .retrieval_benchmark import build_retrieval_recommendations, evaluate_retrieval_cases, metric_deltas
from .retrieval_explain import enrich_search_result
from .result_details import collapse_mail_spool_search_results, decorate_corpus_search_item
from .runtime_heartbeat import WatcherHeartbeatRunner
from .scoring import ContextCandidate, pack_context, pack_context_with_trace
from .settings import SettingsService
from .versioning import collapse_version_families
from .watcher import WatchEvent, WatchRoot, create_corpus_watcher, probe_watcher_backend


LOCAL_SCOPE_SCORE_BOOST = 1.15
STRONG_SEMANTIC_MIN_SCORE = 0.35
ALLOWED_RETRIEVAL_LOGICAL_KINDS = {"episode", "file", "mail"}
CODE_FILE_KIND = "code"
INTERNAL_EXCLUDE_FILE_KINDS_KEY = "_exclude_file_kinds"
CODE_SEARCH_LITERAL_SYMBOL_MODE = "literal_symbol"
CODE_SEARCH_FULL_TEXT_MODE = "full_text"
WATCHER_HEARTBEAT_INTERVAL_SECONDS = 10.0
_DEFAULT_SEARCH_CORPUS_CHUNKS = database.search_corpus_chunks


@dataclass(frozen=True)
class RememberResult:
    id: str
    redaction_count: int


@dataclass(frozen=True)
class RetrievalScope:
    mode: str
    cwd: str | None = None
    root_name: str | None = None
    root_path: str | None = None
    workspace_root: str | None = None
    workspace_key: str | None = None

    @property
    def is_scoped(self) -> bool:
        return bool(self.cwd or self.root_name or self.root_path or self.workspace_key)


class KnowledgeService:
    def remember(
        self,
        title: str,
        body: str,
        metadata: dict[str, Any] | None = None,
        cwd: str | None = None,
        root_name: str | None = None,
    ) -> RememberResult:
        redacted_title, title_findings = redact_text(title)
        redacted, findings = redact_text(body)
        all_findings = title_findings + findings
        enriched_metadata = _enrich_workspace_metadata(metadata or {}, cwd=cwd, root_name=root_name)
        episode_id = database.insert_episode(
            title=redacted_title,
            summary=redacted,
            metadata={**enriched_metadata, "redactions": [finding.kind for finding in all_findings]},
        )
        return RememberResult(id=episode_id, redaction_count=len(all_findings))

    def search(
        self,
        query: str,
        limit: int = 5,
        cwd: str | None = None,
        root_name: str | None = None,
        scope_mode: str = "local_first",
        filters: dict[str, Any] | None = None,
    ) -> list[dict[str, Any]]:
        normalized_filters = normalize_retrieval_filters(filters) if filters is not None else None
        effective_filters = _effective_retrieval_filters(normalized_filters)
        raw_results = self._search_raw(
            query,
            limit=limit,
            rerank_limit=limit,
            cwd=cwd,
            root_name=root_name,
            scope_mode=scope_mode,
            filters=effective_filters,
        )
        filtered_results, _excluded = _apply_retrieval_filters(raw_results, effective_filters)
        return _enrich_search_results(query, filtered_results, retrieval_filters=normalized_filters)

    def _search_raw(
        self,
        query: str,
        *,
        limit: int,
        cwd: str | None,
        root_name: str | None,
        scope_mode: str,
        rerank_limit: int | None = None,
        filters: dict[str, Any] | None = None,
        diagnostics: dict[str, Any] | None = None,
    ) -> list[dict[str, Any]]:
        scope = _resolve_retrieval_scope(cwd=cwd, root_name=root_name, scope_mode=scope_mode)
        if scope.mode == "global" or not scope.is_scoped:
            return self._search_once(
                query,
                limit=limit,
                scope=RetrievalScope(mode="global"),
                label="global",
                rerank_limit=rerank_limit,
                filters=filters,
                diagnostics=diagnostics,
            )
        if scope.mode == "workspace_boosted":
            return self._search_workspace_boosted(
                query,
                limit=limit,
                rerank_limit=rerank_limit,
                scope=scope,
                filters=filters,
                diagnostics=diagnostics,
            )

        scoped_results = self._search_once(
            query,
            limit=limit,
            rerank_limit=rerank_limit,
            scope=scope,
            label="local",
            filters=filters,
            diagnostics=diagnostics,
        )
        if scope.mode == "local_only" or _has_lexical_or_fuzzy_evidence(scoped_results):
            return scoped_results

        return self._search_once(
            query,
            limit=limit,
            rerank_limit=rerank_limit,
            scope=RetrievalScope(mode="global"),
            label="global_fallback",
            filters=filters,
            diagnostics=diagnostics,
        )

    def _search_workspace_boosted(
        self,
        query: str,
        *,
        limit: int,
        rerank_limit: int | None = None,
        scope: RetrievalScope,
        filters: dict[str, Any] | None = None,
        diagnostics: dict[str, Any] | None = None,
    ) -> list[dict[str, Any]]:
        limit = max(1, min(int(limit or 5), 50))
        local_results = self._search_once(
            query,
            limit=limit,
            rerank_limit=rerank_limit,
            scope=scope,
            label="local",
            filters=filters,
            diagnostics=diagnostics,
        )
        local_keys = {_result_identity(item) for item in local_results}

        cross_candidate_limit = min(max(limit * 2, 8), 50)
        cross_results = self._search_once(
            query,
            limit=cross_candidate_limit,
            rerank_limit=rerank_limit,
            scope=RetrievalScope(mode="global"),
            label_scope=scope,
            label="cross_workspace",
            filters=filters,
            diagnostics=diagnostics,
        )
        cross_results = [
            item
            for item in cross_results
            if _result_identity(item) not in local_keys and _is_strong_cross_workspace_evidence(item)
        ]
        if _has_lexical_or_fuzzy_evidence(local_results):
            cross_results = cross_results[: max(1, limit // 2)]

        combined = _dedupe_search_results(
            [_with_scope_score_boost(item, LOCAL_SCOPE_SCORE_BOOST) for item in local_results] + cross_results
        )
        return collapse_version_families(
            sorted(combined, key=lambda item: float(item.get("score") or 0.0), reverse=True),
            limit=limit,
        )

    def _search_once(
        self,
        query: str,
        *,
        limit: int,
        rerank_limit: int | None = None,
        scope: RetrievalScope,
        label: str,
        label_scope: RetrievalScope | None = None,
        filters: dict[str, Any] | None = None,
        diagnostics: dict[str, Any] | None = None,
    ) -> list[dict[str, Any]]:
        corpus_limit = max(limit * 4, 20)
        is_local = label == "local"
        logical_kinds = set(filters.get("logical_kinds") or []) if isinstance(filters, dict) else set()
        include_episodes = not logical_kinds or "episode" in logical_kinds
        include_corpus = not logical_kinds or bool(logical_kinds.intersection({"file", "mail"}))
        episode_workspace_key = scope.workspace_key or (f"root:{scope.root_name}" if scope.root_name else None)
        corpus_kwargs: dict[str, Any] = {"limit": corpus_limit, "root_name": scope.root_name}
        if filters is not None:
            corpus_kwargs["filters"] = filters
        corpus_diagnostics: dict[str, Any] | None = {} if diagnostics is not None else None
        if corpus_diagnostics is not None:
            corpus_kwargs["diagnostics"] = corpus_diagnostics
        evidence_kwargs = {**corpus_kwargs, "rerank_limit": rerank_limit if rerank_limit is not None else limit}
        evidence_items = (
            _search_evidence_with_configured_engine(query, **evidence_kwargs)
            if (include_corpus or include_episodes) and (not is_local or scope.root_name or episode_workspace_key)
            else None
        )
        if evidence_items is None:
            episode_items = (
                database.search_episodes(
                    query,
                    limit=limit,
                    cwd=scope.cwd,
                    root_path=scope.root_path,
                    workspace_key=episode_workspace_key,
                )
                if include_episodes and (not is_local or scope.cwd or scope.root_path or episode_workspace_key)
                else []
            )
            corpus_items = (
                _search_corpus_with_configured_engine(query, **corpus_kwargs)
                if include_corpus and (not is_local or scope.root_name)
                else []
            )
        else:
            episode_items = [item for item in evidence_items if item.get("kind") in {"episode", "claim"}] if include_episodes else []
            corpus_items = [item for item in evidence_items if item.get("kind") == "corpus_chunk"] if include_corpus else []
        if diagnostics is not None and corpus_diagnostics is not None:
            diagnostics.setdefault("scopes", {}).setdefault(label, {})["corpus"] = corpus_diagnostics
        episodes = [_format_memory_evidence_search_item(item) for item in episode_items]
        corpus = collapse_mail_spool_search_results(
            [
                decorate_corpus_search_item({"kind": "corpus_chunk", **_format_corpus_search_item(item)})
                for item in corpus_items
            ]
        )
        results = collapse_version_families(
            sorted(corpus + episodes, key=lambda item: item["score"], reverse=True),
            limit=limit,
        )
        return [_tag_retrieval_scope(item, label, scope=label_scope or scope) for item in results]

    def brief(
        self,
        query: str,
        token_budget: int | None = None,
        cwd: str | None = None,
        root_name: str | None = None,
        scope_mode: str = "local_first",
        filters: dict[str, Any] | None = None,
    ) -> str:
        if filters is not None:
            normalize_retrieval_filters(filters)
        if token_budget is None:
            token_budget = _configured_token_budget()
        search_kwargs: dict[str, Any] = {
            "limit": 10,
            "cwd": cwd,
            "root_name": root_name,
            "scope_mode": scope_mode,
        }
        if filters is not None:
            search_kwargs["filters"] = filters
        search_results = self.search(query, **search_kwargs)
        current_results = [item for item in search_results if _is_current_evidence(item)]
        if current_results:
            search_results = current_results
        candidates = [
            ContextCandidate(
                id=item["id"],
                title=item["title"],
                body=item["summary"],
                score=item["score"],
            )
            for item in search_results
        ]
        return pack_context(candidates, token_budget=token_budget)

    def explain(
        self,
        query: str,
        limit: int = 5,
        token_budget: int | None = None,
        cwd: str | None = None,
        root_name: str | None = None,
        scope_mode: str = "local_first",
        filters: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        normalized_filters = normalize_retrieval_filters(filters) if filters is not None else None
        if token_budget is None:
            token_budget = _configured_token_budget()
        result_limit = max(1, min(int(limit or 5), 50))
        effective_filters = _effective_retrieval_filters(normalized_filters)
        diagnostics = {}
        raw_results = self._search_raw(
            query,
            limit=max(result_limit, 10),
            rerank_limit=result_limit,
            cwd=cwd,
            root_name=root_name,
            scope_mode=scope_mode,
            filters=effective_filters,
            diagnostics=diagnostics,
        )
        filtered_results, excluded = _apply_retrieval_filters(raw_results, effective_filters)
        search_results = _enrich_search_results(query, filtered_results, retrieval_filters=normalized_filters)
        payload = {
            "query": query,
            "results": search_results[:result_limit],
            "brief": _brief_selection_trace(search_results, token_budget=token_budget),
            "retrieval_timing": diagnostics,
        }
        if normalized_filters is not None:
            payload["filters"] = normalized_filters
            payload["filter_trace"] = {"excluded": excluded}
            if normalized_filters.get("include_suppressed"):
                payload["suppression"] = _suppression_trace(raw_results)
        return payload

    def run_retrieval_benchmark(
        self,
        *,
        suite: str = "standard",
        label: str | None = None,
        compare_label: str | None = None,
        limit_per_query: int = 5,
        token_budget: int | None = None,
        persist: bool = True,
    ) -> dict[str, Any]:
        normalized_suite = _normalize_retrieval_benchmark_suite(suite)
        bounded_limit = max(1, min(int(limit_per_query or 5), 50))
        if token_budget is None:
            token_budget = _configured_token_budget()
        cases, cleanup = self._prepare_retrieval_benchmark_cases(normalized_suite)
        try:
            observations: dict[str, dict[str, Any]] = {}
            for case in cases:
                started = time.perf_counter()
                explain_payload = self.explain(
                    str(case["query"]),
                    limit=bounded_limit,
                    token_budget=token_budget,
                    root_name=case.get("root_name"),
                    scope_mode=str(case.get("scope_mode") or "local_first"),
                    filters=case.get("filters"),
                )
                observations[str(case["id"])] = {
                    "results": explain_payload.get("results") or [],
                    "brief": explain_payload.get("brief") or {},
                    "elapsed_ms": max(0, int((time.perf_counter() - started) * 1000)),
                }
            report = evaluate_retrieval_cases(cases, observations, limit_per_query=bounded_limit)
            recommendations = build_retrieval_recommendations(report)
            recorded: dict[str, Any] | None = None
            if persist:
                metadata = {
                    "provider": "synthetic",
                    "suite_version": "v2",
                    "limit_per_query": bounded_limit,
                    "calibration_summary": report["calibration_summary"],
                }
                if isinstance(recommendations.get("governance_shadow"), dict):
                    metadata["governance_shadow"] = recommendations["governance_shadow"]
                recorded = database.record_retrieval_benchmark_run(
                    suite=normalized_suite,
                    label=label,
                    compare_label=compare_label,
                    status="completed",
                    query_count=report["query_count"],
                    passed_count=report["passed_count"],
                    failed_count=report["failed_count"],
                    metrics=report["metrics"],
                    case_results=report["case_results"],
                    metadata=metadata,
                    recommendation_metadata=recommendations,
                )
                history_rows = database.list_retrieval_benchmark_runs(
                    suite=normalized_suite,
                    label=label or None,
                    limit=1,
                )
                if history_rows:
                    recorded = {**recorded, **history_rows[0]}
            return {
                "id": recorded.get("id") if recorded else None,
                "suite": normalized_suite,
                "label": label,
                "compare_label": compare_label,
                "status": "completed",
                "query_count": report["query_count"],
                "passed_count": report["passed_count"],
                "failed_count": report["failed_count"],
                "metrics": report["metrics"],
                "metric_deltas": recorded.get("metric_deltas") if recorded else metric_deltas(report["metrics"], None),
                "calibration_summary": report["calibration_summary"],
                "case_results": report["case_results"],
                "recommendations": recommendations,
                "created_at": recorded.get("created_at") if recorded else None,
            }
        finally:
            cleanup()

    def retrieval_benchmark_history(
        self,
        *,
        suite: str | None = None,
        label: str | None = None,
        limit: int = 20,
    ) -> dict[str, Any]:
        normalized_suite = None if suite in {None, "", "all"} else _normalize_retrieval_benchmark_suite(suite)
        return {
            "suite": normalized_suite or "all",
            "runs": database.list_retrieval_benchmark_runs(
                suite=normalized_suite,
                label=label or None,
                limit=limit,
            ),
        }

    def run_governance(
        self,
        *,
        mode: str = "shadow",
        actor: str = "system",
        limit: int = 25,
    ) -> dict[str, Any]:
        bounded_limit = max(1, min(int(limit or 25), 200))
        policy = governance.normalized_policy({**_governance_policy_from_settings(), "mode": mode, "max_actions_per_run": bounded_limit})
        quality_report = database.retention_quality_report(limit=bounded_limit)
        benchmark_runs = database.list_retrieval_benchmark_runs(suite="governance-shadow", limit=1)
        capture_jobs = database.list_capture_review_jobs(status="all", limit=bounded_limit)
        code_feedback = database.code_feedback_summary(limit=bounded_limit)
        semantic_clusters = database.list_semantic_duplicate_clusters(limit=bounded_limit)
        existing_actions = database.list_memory_governance_actions(status="all", limit=200)
        proposal_payload = governance.build_governance_proposals(
            quality_report=quality_report,
            benchmark_runs=benchmark_runs,
            capture_jobs=capture_jobs,
            code_feedback=code_feedback,
            semantic_clusters=semantic_clusters,
            existing_actions=existing_actions,
            policy=policy,
        )
        run_status = "completed" if proposal_payload["gate"].get("status") == "ready" else "blocked"
        run = database.record_memory_governance_run(
            mode=proposal_payload["policy"].get("mode") or "shadow",
            trigger="manual",
            status=run_status,
            policy_snapshot=proposal_payload["policy"],
            gate=proposal_payload["gate"],
            summary=proposal_payload["summary"],
            actor=actor,
            memory_mutated=False,
        )
        actions = [
            database.record_memory_governance_action(
                run_id=run["id"],
                action=action["action"],
                target_type=action["target_type"],
                target_id=action["target_id"],
                memory_class=action.get("memory_class"),
                risk=action["risk"],
                status=action["status"],
                source=action["source"],
                rationale=action.get("rationale") or {},
                evidence=action.get("evidence") or {},
                before_state=action.get("before_state") or {},
                after_state=action.get("after_state") or {},
                actor=actor,
                memory_mutated=False,
            )
            for action in proposal_payload["actions"]
        ]
        applied_actions: list[dict[str, Any]] = []
        if _governance_should_auto_apply(proposal_payload["policy"], proposal_payload["gate"]):
            updated_actions: list[dict[str, Any]] = []
            for action in actions:
                if not _governance_auto_apply_allowed(action):
                    updated_actions.append(action)
                    continue
                try:
                    applied = self.governance_apply(
                        str(action["id"]),
                        rationale="guarded low-risk governance auto-apply after passing governance-shadow gate",
                        confirm=True,
                        actor=actor,
                    )
                    applied_action = applied["action"]
                    applied_actions.append(applied_action)
                    updated_actions.append(applied_action)
                except Exception as exc:
                    updated_actions.append(
                        database.update_memory_governance_action(
                            action_id=str(action["id"]),
                            status="failed",
                            after_state={"auto_apply_error": str(exc), "settings_mutated": False},
                            memory_mutated=False,
                            error=str(exc),
                        )
                    )
            actions = updated_actions
            if applied_actions:
                proposal_payload["summary"] = {
                    **proposal_payload["summary"],
                    "auto_applied": len(applied_actions),
                }
                run = database.update_memory_governance_run(
                    run_id=str(run["id"]),
                    status=run.get("status") or run_status,
                    summary=proposal_payload["summary"],
                    memory_mutated=True,
                )
        digest_payload = governance.build_governance_digest(run=run, actions=actions, gate=proposal_payload["gate"])
        digest = database.record_memory_governance_digest(
            run_id=run["id"],
            summary=digest_payload["summary"],
            recommendations=digest_payload["recommendations"],
            actor=actor,
            memory_mutated=bool(applied_actions),
        )
        return {
            "settings_mutated": False,
            "memory_mutated": bool(applied_actions),
            "run": run,
            "actions": actions,
            "digest": digest,
            "gate": proposal_payload["gate"],
            "summary": proposal_payload["summary"],
            "policy": proposal_payload["policy"],
        }

    def governance_runs(self, *, limit: int = 20) -> dict[str, Any]:
        return {
            "settings_mutated": False,
            "runs": database.list_memory_governance_runs(limit=limit),
        }

    def governance_actions(self, *, status: str = "proposed", limit: int = 50) -> dict[str, Any]:
        actions = database.list_memory_governance_actions(status=status, limit=limit)
        return {
            "settings_mutated": False,
            "status": status,
            "actions": actions,
            "telemetry": _governance_action_telemetry(actions),
        }

    def governance_digest(self) -> dict[str, Any]:
        digest = database.latest_memory_governance_digest()
        return {
            "settings_mutated": False,
            "digest": digest or {
                "summary": {
                    "new_proposals": 0,
                    "blocked_proposals": 0,
                    "recoverable_actions": 0,
                    "high_risk": 0,
                    "gate_status": "unknown",
                },
                "recommendations": [{"action": "run_governance", "reason": "no_digest_recorded"}],
            },
        }

    def governance_policy(self) -> dict[str, Any]:
        return {
            "settings_mutated": False,
            "policy": governance.normalized_policy(_governance_policy_from_settings()),
        }

    def operator_automation_status(self) -> dict[str, Any]:
        policy = operator_automation.normalized_policy(_operator_automation_policy_from_settings())
        try:
            runs = database.list_operator_automation_runs(limit=5)
        except Exception:
            runs = []
        try:
            actions = database.list_operator_automation_actions(status="all", limit=25)
        except Exception:
            actions = []
        eligible = self._operator_automation_plan(policy, limit=int(policy.get("max_actions_per_run") or 25))
        last_run = runs[0] if runs else None
        recurring = _operator_automation_recurring_state(policy, last_run)
        return {
            "settings_mutated": False,
            "policy": {
                **policy,
                "next_run_after_seconds": int(policy.get("interval_seconds") or 1800),
            },
            "recurring": recurring,
            "last_run": last_run,
            "eligible_actions": eligible,
            "manual_required": operator_automation.manual_required_items(),
            "recent_actions": actions,
            "runs": runs,
        }

    def operator_automation_actions(
        self,
        *,
        status: str = "all",
        run_id: str | None = None,
        action: str | None = None,
        limit: int = 50,
    ) -> dict[str, Any]:
        actions = database.list_operator_automation_actions(status=status, run_id=run_id, action=action, limit=limit)
        return {
            "settings_mutated": False,
            "status": status,
            "run_id": run_id,
            "action": action,
            "actions": actions,
            "telemetry": _automation_action_telemetry(actions),
        }

    def run_operator_automation(
        self,
        *,
        mode: str | None = None,
        trigger: str = "manual",
        actor: str = "system",
        limit: int | None = None,
        dry_run: bool = False,
    ) -> dict[str, Any]:
        settings_policy = _operator_automation_policy_from_settings()
        requested = {**settings_policy}
        if mode:
            requested["mode"] = mode
        if limit is not None:
            requested["max_actions_per_run"] = limit
        policy = operator_automation.normalized_policy(requested)
        bounded_limit = max(1, min(int(limit or policy.get("max_actions_per_run") or 25), 200))
        plan = self._operator_automation_plan(policy, limit=bounded_limit)
        run = database.record_operator_automation_run(
            mode=str(policy.get("mode") or "guarded"),
            trigger=trigger,
            status="running",
            policy_snapshot=policy,
            summary={"eligible": len(plan), "dry_run": bool(dry_run), "settings_mutated": False},
            actor=actor,
            memory_mutated=False,
        )
        recorded_actions: list[dict[str, Any]] = []
        applied = skipped = blocked = failed = 0
        memory_mutated = False
        execute = str(policy.get("mode") or "guarded") == "guarded" and not dry_run
        for item in plan[:bounded_limit]:
            result: dict[str, Any] = {}
            status = "proposed"
            error: str | None = None
            item_memory_mutated = False
            if not execute:
                status = "skipped"
                skipped += 1
            else:
                try:
                    result = self._execute_operator_automation_action(item, actor=actor, limit=bounded_limit)
                    if bool(result.get("settings_mutated")):
                        status = "blocked"
                        blocked += 1
                        result = {**result, "blocked_reason": "underlying action reported settings_mutated"}
                    else:
                        status = "applied"
                        applied += 1
                    item_memory_mutated = bool(result.get("memory_mutated"))
                    memory_mutated = memory_mutated or item_memory_mutated
                except Exception as exc:
                    status = "failed"
                    failed += 1
                    error = str(exc)
                    result = {"settings_mutated": False, "error": error}
            recorded = database.record_operator_automation_action(
                run_id=run["id"],
                action=str(item["action"]),
                target_type=item.get("target_type"),
                target_id=item.get("target_id"),
                risk=str(item.get("risk") or "low"),
                status=status,
                source=str(item.get("source") or "automation"),
                rationale=item.get("rationale") if isinstance(item.get("rationale"), dict) else {},
                evidence=item.get("evidence") if isinstance(item.get("evidence"), dict) else {},
                result={**result, "settings_mutated": bool(result.get("settings_mutated", False))},
                actor=actor,
                memory_mutated=item_memory_mutated,
                error=error,
            )
            recorded_actions.append({**recorded, "settings_mutated": bool(recorded.get("settings_mutated", False))})
        if failed:
            run_status = "failed" if applied == 0 and skipped == 0 and blocked == 0 else "blocked"
        elif blocked:
            run_status = "blocked"
        else:
            run_status = "completed"
        summary = {
            "eligible": len(plan),
            "applied": applied,
            "skipped": skipped,
            "blocked": blocked,
            "failed": failed,
            "dry_run": bool(dry_run),
            "settings_mutated": False,
        }
        run = database.update_operator_automation_run(
            run_id=str(run["id"]),
            status=run_status,
            summary=summary,
            memory_mutated=memory_mutated,
        )
        return {
            "settings_mutated": False,
            "memory_mutated": memory_mutated,
            "policy": policy,
            "run": run,
            "summary": summary,
            "actions": recorded_actions,
            "manual_required": operator_automation.manual_required_items(),
        }

    def _operator_automation_plan(self, policy: dict[str, Any], *, limit: int = 25) -> list[dict[str, Any]]:
        labels = operator_automation.guarded_action_labels()
        plan: list[dict[str, Any]] = []
        if bool(policy.get("auto_refresh_evidence")):
            plan.append(
                _automation_plan_action(
                    action="refresh_retrieval_evidence",
                    label=labels["refresh_retrieval_evidence"],
                    source="evidence",
                    target_type="benchmark",
                    target_id="standard",
                    reason="Refresh retrieval and reliability evidence without changing settings.",
                    evidence={"suite": "standard", "scenario": "reliability"},
                )
            )
        if bool(policy.get("auto_ingest_approved_capture")):
            try:
                capture_jobs = database.list_capture_ingestion_jobs(limit=limit)
            except Exception:
                capture_jobs = []
            if capture_jobs:
                plan.append(
                    _automation_plan_action(
                        action="ingest_approved_capture",
                        label=labels["ingest_approved_capture"],
                        source="capture",
                        target_type="capture_review_job",
                        target_id=str(capture_jobs[0].get("id") or "approved_capture"),
                        reason=f"{len(capture_jobs)} approved capture job(s) are eligible for ingestion.",
                        evidence={"approved_count": len(capture_jobs), "job_ids": [str(job.get("id") or "") for job in capture_jobs[:10]]},
                    )
                )
        if bool(policy.get("auto_remediate_diagnostics")):
            diagnostic_action = self._first_safe_diagnostic_action(limit=limit)
            if diagnostic_action:
                plan.append(diagnostic_action)
        if bool(policy.get("auto_sync_search_index")):
            try:
                search_index_status = self.search_index_status()
            except Exception:
                search_index_status = {}
            summary = search_index_status.get("summary") if isinstance(search_index_status.get("summary"), dict) else {}
            by_status = summary.get("by_status") if isinstance(summary.get("by_status"), dict) else {}
            missing_by_class = search_index_status.get("missing") if isinstance(search_index_status.get("missing"), dict) else {}
            missing = int(summary.get("missing") or 0)
            pending = int(summary.get("pending_work") or 0)
            if not pending:
                pending = (
                    int(by_status.get("pending") or 0)
                    + int(by_status.get("failed") or 0)
                    + int(by_status.get("syncing") or 0)
                    + int(summary.get("pending") or 0)
                    + int(summary.get("failed") or 0)
                    + missing
                )
            if pending:
                plan.append(
                    _automation_plan_action(
                        action="sync_search_index",
                        label=labels["sync_search_index"],
                        source="search_index",
                        target_type="search_index_queue",
                        target_id="pending_or_missing" if missing else "pending_or_failed",
                        reason=f"{pending} search-index record(s) need sync.",
                        evidence={
                            "pending_or_failed": pending - missing,
                            "missing": missing,
                            "missing_by_class": missing_by_class,
                            "by_status": by_status,
                        },
                    )
                )
        if bool(policy.get("auto_run_governance_shadow")):
            plan.append(
                _automation_plan_action(
                    action="run_governance_shadow",
                    label=labels["run_governance_shadow"],
                    source="governance",
                    target_type="governance",
                    target_id="shadow",
                    reason="Generate governance proposals in shadow mode only.",
                    evidence={"mode": "shadow"},
                )
            )
        return plan[: max(1, min(int(limit or 25), 200))]

    def _first_safe_diagnostic_action(self, *, limit: int = 25) -> dict[str, Any] | None:
        try:
            diagnostics = self.operational_diagnostics(section="all", limit=limit, include_details=True)
        except Exception:
            return None
        labels = operator_automation.guarded_action_labels()
        for item in diagnostics.get("items") or []:
            if not isinstance(item, dict):
                continue
            for action in item.get("remediation_actions") or []:
                if not isinstance(action, dict):
                    continue
                payload = action.get("payload") if isinstance(action.get("payload"), dict) else {}
                action_id = str(payload.get("action") or action.get("id") or "")
                if action_id not in {"retry_corpus_job", "run_backfill", "repair_asset_statuses", "clear_completed_errors"}:
                    continue
                if bool(action.get("destructive")) or bool(action.get("settings_mutated")):
                    continue
                target = action.get("target") if isinstance(action.get("target"), dict) else {}
                return _automation_plan_action(
                    action="safe_diagnostic_recovery",
                    label=labels["safe_diagnostic_recovery"],
                    source="diagnostics",
                    target_type=str(payload.get("target_type") or target.get("type") or "diagnostic"),
                    target_id=str(payload.get("target_id") or target.get("id") or action_id),
                    reason=str(action.get("label") or item.get("summary") or "Run safe diagnostic remediation."),
                    evidence={"diagnostic": item, "payload": payload},
                    risk="low",
                )
        return None

    def _execute_operator_automation_action(self, item: dict[str, Any], *, actor: str, limit: int) -> dict[str, Any]:
        action = str(item.get("action") or "")
        if action == "refresh_retrieval_evidence":
            retrieval = self.run_retrieval_benchmark(suite="standard", label="operator-automation", limit_per_query=5, persist=True)
            reliability = self.run_indexer_reliability(scope="all_roots", label="operator-automation", evidence_level="standard")
            return {
                "settings_mutated": bool(retrieval.get("settings_mutated") or reliability.get("settings_mutated")),
                "retrieval": retrieval,
                "reliability": reliability,
            }
        if action == "ingest_approved_capture":
            return self.ingest_capture_review_jobs(limit=limit, dry_run=False, actor=actor)
        if action == "safe_diagnostic_recovery":
            evidence = item.get("evidence") if isinstance(item.get("evidence"), dict) else {}
            payload = evidence.get("payload") if isinstance(evidence.get("payload"), dict) else {}
            return self.remediate_diagnostic(
                action=str(payload.get("action") or ""),
                target_type=str(payload.get("target_type") or ""),
                target_id=payload.get("target_id"),
                root_name=payload.get("root_name"),
                family=payload.get("family"),
                reason=str(payload.get("reason") or "operator automation diagnostic remediation"),
                actor=actor,
            )
        if action == "sync_search_index":
            return self.search_index_sync(owner_class="all", limit=limit)
        if action == "run_governance_shadow":
            return self.run_governance(mode="shadow", actor=actor, limit=limit)
        raise ValueError(f"unsupported operator automation action: {action}")

    def governance_apply(
        self,
        action_id: str,
        *,
        rationale: str,
        confirm: bool = False,
        actor: str = "system",
    ) -> dict[str, Any]:
        clean_rationale = str(rationale or "").strip()
        if not confirm:
            raise ValueError("governance apply requires confirmation")
        if not clean_rationale:
            raise ValueError("governance apply rationale is required")
        action = database.get_memory_governance_action(action_id)
        if action is None:
            raise LookupError(f"governance action not found: {action_id}")
        if action.get("status") == "applied":
            return {"settings_mutated": False, "memory_mutated": bool(action.get("memory_mutated")), "action": action}
        if action.get("status") == "blocked" or (action.get("rationale") or {}).get("guardrails", {}).get("protected"):
            raise ValueError("governance action is blocked by guardrails")
        if action.get("status") != "proposed":
            raise ValueError(f"governance action is not proposed: {action.get('status')}")
        gate = governance.evaluate_governance_gate(
            database.list_retrieval_benchmark_runs(suite="governance-shadow", limit=1),
            policy=self.governance_policy()["policy"],
        )
        if gate.get("status") != "ready":
            raise ValueError(f"governance apply blocked by benchmark gate: {','.join(gate.get('reasons') or [])}")

        conflict = _governance_conflict(action)
        if conflict:
            updated = database.update_memory_governance_action(
                action_id=action_id,
                status="skipped_conflict",
                after_state={"conflict": conflict, "rationale": clean_rationale},
                memory_mutated=False,
            )
            return {"settings_mutated": False, "memory_mutated": False, "action": updated, "gate": gate}

        after_state: dict[str, Any] = {"rationale": clean_rationale}
        memory_mutated = False
        transition = _governance_claim_transition(action)
        if transition:
            claim = database.transition_claim(
                claim_id=str(action.get("target_id")),
                transition=transition,
                reason=f"governance:{clean_rationale}",
                actor=actor,
            )
            after_state["claim"] = _claim_lifecycle_snapshot(claim)
            memory_mutated = True
        else:
            audit_event = database.record_audit_event(
                event_type="governance.action_applied",
                target_table=str(action.get("target_type") or "memory_governance_actions"),
                target_id=str(action.get("target_id") or action_id),
                details={"action_id": action_id, "action": action.get("action"), "actor": actor, "rationale": clean_rationale},
            )
            if isinstance(audit_event, dict) and audit_event.get("id"):
                after_state["audit_event_id"] = audit_event["id"]
            after_state["applied"] = True
        updated = database.update_memory_governance_action(
            action_id=action_id,
            status="applied",
            after_state=after_state,
            memory_mutated=memory_mutated,
            audit_event_id=after_state.get("audit_event_id"),
        )
        return {"settings_mutated": False, "memory_mutated": memory_mutated, "action": updated, "gate": gate}

    def governance_recover(
        self,
        action_id: str,
        *,
        rationale: str,
        confirm: bool = False,
        actor: str = "system",
    ) -> dict[str, Any]:
        clean_rationale = str(rationale or "").strip()
        if not confirm:
            raise ValueError("governance recovery requires confirmation")
        if not clean_rationale:
            raise ValueError("governance recovery rationale is required")
        action = database.get_memory_governance_action(action_id)
        if action is None:
            raise LookupError(f"governance action not found: {action_id}")
        if action.get("status") == "recovered":
            return {"settings_mutated": False, "memory_mutated": bool(action.get("memory_mutated")), "action": action}
        if action.get("status") != "applied":
            raise ValueError(f"governance action is not applied: {action.get('status')}")

        before = action.get("before_state") if isinstance(action.get("before_state"), dict) else {}
        after_state: dict[str, Any] = {"recovery_rationale": clean_rationale}
        memory_mutated = False
        if _governance_claim_transition(action) and before.get("lifecycle_state"):
            restored = database.restore_claim_lifecycle_state(
                claim_id=str(action.get("target_id")),
                lifecycle_state=str(before.get("lifecycle_state")),
                retention_action=str(before.get("retention_action") or "keep"),
                actor=actor,
                reason=clean_rationale,
            )
            after_state["restored_claim"] = _claim_lifecycle_snapshot(restored)
            memory_mutated = True
        else:
            audit_event = database.record_audit_event(
                event_type="governance.action_recovered",
                target_table=str(action.get("target_type") or "memory_governance_actions"),
                target_id=str(action.get("target_id") or action_id),
                details={"action_id": action_id, "action": action.get("action"), "actor": actor, "rationale": clean_rationale},
            )
            if isinstance(audit_event, dict) and audit_event.get("id"):
                after_state["audit_event_id"] = audit_event["id"]
            after_state["recovered"] = True
        updated = database.update_memory_governance_action(
            action_id=action_id,
            status="recovered",
            after_state=after_state,
            memory_mutated=memory_mutated,
            audit_event_id=after_state.get("audit_event_id"),
        )
        return {"settings_mutated": False, "memory_mutated": memory_mutated, "action": updated}

    def _prepare_retrieval_benchmark_cases(self, suite: str) -> tuple[list[dict[str, Any]], Any]:
        normalized_suite = _normalize_retrieval_benchmark_suite(suite)
        if normalized_suite == "governance-shadow":
            return self._prepare_governance_shadow_benchmark_cases()
        if normalized_suite != "standard":
            raise ValueError("retrieval benchmark suite must be standard or governance-shadow")
        temp_dir = tempfile.TemporaryDirectory(prefix="flux-kb-retrieval-benchmark-")
        root = Path(temp_dir.name)
        root_name = f"__retrieval_benchmark_{uuid4().hex[:12]}"
        episode_ids: list[str] = []
        root_created = False

        def cleanup() -> None:
            try:
                for episode_id in episode_ids:
                    database.forget_episode(episode_id)
                if root_created:
                    database.delete_monitored_root(root_id=root_name, purge_index=True, actor="retrieval_benchmark")
                    cleanup_index_result = database.sync_search_index(owner_class="all", root_name=root_name, limit=1000)
                    if int(cleanup_index_result.get("failed") or 0):
                        errors = "; ".join(str(item) for item in (cleanup_index_result.get("errors") or [])[:3])
                        raise RuntimeError(
                            f"retrieval benchmark search-index cleanup failed: {errors or cleanup_index_result['failed']}"
                    )
                    database._delete_search_index_records_for_root(root_name=root_name, statuses=["deleted"])
                    database._delete_semantic_duplicate_clusters_for_root(root_name=root_name)
            finally:
                temp_dir.cleanup()

        try:
            marker = uuid4().hex
            alpha_token = f"alphabench{marker}"
            duplicate_token = f"duplicatebench{marker}"
            current_token = f"currentbench{marker}"
            guardrail_token = f"guardrailbench{marker}"
            fallback_token = f"fallbackbench{marker}"
            contradiction_token = f"contradictionbench{marker}"
            mail_token = f"mailbench{marker}"
            episode_token = f"episodebench{marker}"
            files = {
                "alpha-decision.md": (
                    f"{alpha_token} retrieval benchmark decision. "
                    "Flux should find scoped corpus evidence before broad fallback results."
                ),
                "duplicate-canonical.md": (
                    f"{duplicate_token} duplicate benchmark note. "
                    "Exact duplicate suppression should keep one canonical searchable result."
                ),
                "duplicate-copy.md": (
                    f"{duplicate_token} duplicate benchmark note. "
                    "Exact duplicate suppression should keep one canonical searchable result."
                ),
                "service_impl.py": (
                    f"# code-{marker} retrieval benchmark fixture\n\n"
                    "def _benchmark_private_helper(request):\n"
                    "    return {'status': 'ok', 'source': request}\n\n"
                    "def benchmark_handler(request):\n"
                    "    return _benchmark_private_helper(request)\n"
                ),
                "tests/test_service_impl.py": (
                    f"# caller-{marker} test benchmark fixture\n\n"
                    "import pytest\n"
                    "from service_impl import benchmark_handler\n\n"
                    "@pytest.fixture\n"
                    "def request_payload():\n"
                    "    return {'id': 'case-1'}\n\n"
                    "def test_benchmark_handler_returns_status(request_payload):\n"
                    "    result = benchmark_handler(request_payload)\n"
                    "    assert result['status'] == 'ok'\n"
                ),
                "web/routes.ts": (
                    f"// route-{marker} route benchmark fixture\n\n"
                    "function renderBenchmarkOrder(id) { return { id }; }\n"
                    "export const benchmarkRoute = (req, res) => res.json(renderBenchmarkOrder(req.params.orderId));\n"
                    "router.get('/api/benchmark/orders/:orderId', benchmarkRoute);\n"
                ),
                "generated/client.py": (
                    f"# generated-{marker} generated benchmark fixture\n"
                    "# Code generated by Flux benchmark. DO NOT EDIT.\n\n"
                    "class GeneratedBenchmarkClient:\n"
                    "    def fetch_order(self, order_id):\n"
                    "        return {'order_id': order_id}\n"
                ),
                "config/app.yaml": (
                    f"# config-{marker} benchmark configuration\n"
                    "retrieval:\n"
                    "  ranking_mode: code-aware\n"
                ),
                "db/migrations/0001_create_benchmark_orders.sql": (
                    f"-- migration-{marker} benchmark migration fixture\n"
                    "CREATE TABLE benchmark_orders (id text primary key, status text not null);\n"
                    "CREATE INDEX idx_benchmark_orders_status ON benchmark_orders (status);\n"
                ),
                "app/service_impl.py": (
                    f"# disambiguate-{marker} app scoped code fixture\n\n"
                    "def benchmark_handler(request):\n"
                    "    return {'status': 'app', 'source': request}\n"
                ),
                "other/service_impl.py": (
                    f"# disambiguate-{marker} non-app duplicate fixture\n\n"
                    "def benchmark_handler(request):\n"
                    "    return {'status': 'other', 'source': request}\n"
                ),
                "contradiction-review.md": (
                    f"{contradiction_token} benchmark evidence says the older statement was superseded "
                    "by a newer local review note."
                ),
                "current-note.md": (
                    f"{current_token} current-only benchmark evidence. "
                    "This current file should remain after stale memory filtering."
                ),
                "semantic-guardrail.md": (
                    f"{guardrail_token} benchmark note. "
                    "This similar-looking note should not be treated as a semantic duplicate."
                ),
                "code_fallback.py": (
                    f"# {fallback_token} code-symbol miss benchmark fixture\n\n"
                    "def unrelated_handler(request):\n"
                    "    return {'status': 'fallback'}\n"
                ),
                "mail-alpha/manifest.json": json.dumps(
                    {
                        "subject": f"{mail_token} benchmark message",
                        "sender": "synthetic@example.invalid",
                        "recipients": ["operator@example.invalid"],
                        "source_type": "synthetic",
                        "source_folder": "FluxBenchmark",
                        "attachment_count": 0,
                    },
                    sort_keys=True,
                ),
            }
            for relative_path, content in files.items():
                target = root / relative_path
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(content, encoding="utf-8")
            database.add_monitored_root(
                name=root_name,
                root_path=root,
                include_globs=["*", "**/*"],
                exclude_globs=[],
                trust_rank=850,
                metadata={"benchmark_tag": root_name, "provider": "synthetic"},
            )
            root_created = True
            self.sync_corpus(root_name=root_name)
            database.refresh_semantic_duplicate_clusters(memory_class="corpus", root_name=root_name, limit=1000)
            episode_id = database.insert_episode(
                title=f"{episode_token} retrieval benchmark memory",
                summary=f"{episode_token} synthetic benchmark episode for brief packing and workspace-scoped memory retrieval.",
                metadata={"root_name": root_name, "workspace_key": f"root:{root_name}", "benchmark_tag": root_name},
            )
            episode_ids.append(episode_id)
            stale_episode_id = database.insert_episode(
                title=f"stale-{marker} retrieval benchmark memory",
                summary=f"{current_token} stale memory should be excluded by current_only filtering.",
                metadata={"root_name": root_name, "workspace_key": f"root:{root_name}", "benchmark_tag": root_name},
            )
            episode_ids.append(stale_episode_id)
            stale_claim = database.upsert_claim(
                subject_type="benchmark",
                subject_name=current_token,
                predicate="mentions",
                object_text=f"{current_token} stale memory should be deprioritized.",
                confidence=0.7,
                episode_id=stale_episode_id,
                metadata={"benchmark_tag": root_name},
            )
            database.transition_claim(
                claim_id=stale_claim["id"],
                transition="deprioritize",
                reason="synthetic retrieval benchmark stale evidence",
            )
            search_index_result = database.sync_search_index(owner_class="all", root_name=root_name, limit=1000)
            if int(search_index_result.get("failed") or 0):
                errors = "; ".join(str(item) for item in (search_index_result.get("errors") or [])[:3])
                raise RuntimeError(f"retrieval benchmark search-index sync failed: {errors or search_index_result['failed']}")
            cases = [
                _retrieval_benchmark_case(
                    self,
                    case_id="scoped-corpus",
                    category="scoped_corpus",
                    query=f"{alpha_token} scoped corpus evidence",
                    root_name=root_name,
                    source_path="alpha-decision.md",
                ),
                _retrieval_benchmark_case(
                    self,
                    case_id="duplicate-suppression",
                    category="semantic_duplicate",
                    query=f"{duplicate_token} exact duplicate suppression",
                    root_name=root_name,
                    source_path="duplicate-canonical.md",
                    expect_suppression=True,
                    semantic_similarity=0.92,
                    expected_semantic_duplicate=True,
                ),
                _retrieval_benchmark_case(
                    self,
                    case_id="code-symbol",
                    category="code_symbol",
                    query=f"code-{marker} benchmark_handler",
                    root_name=root_name,
                    source_path="service_impl.py",
                    filters={
                        "logical_kinds": ["file"],
                        "current_only": True,
                        "file_kinds": ["code"],
                        "path_globs": ["service_impl.py"],
                    },
                ),
                _retrieval_benchmark_case(
                    self,
                    case_id="code-exact-definition",
                    category="code_exact_definition",
                    query=f"code-{marker} _benchmark_private_helper",
                    root_name=root_name,
                    source_path="service_impl.py",
                    expected_symbol="_benchmark_private_helper",
                    filters={"logical_kinds": ["file"], "current_only": True, "file_kinds": ["code"]},
                ),
                _retrieval_benchmark_case(
                    self,
                    case_id="code-caller",
                    category="code_caller",
                    query=f"caller-{marker} benchmark_handler caller test",
                    root_name=root_name,
                    source_path="tests/test_service_impl.py",
                    filters={
                        "logical_kinds": ["file"],
                        "current_only": True,
                        "file_kinds": ["code"],
                        "relationships": ["test"],
                        "path_globs": ["tests/*"],
                        "include_generated": False,
                    },
                ),
                _retrieval_benchmark_case(
                    self,
                    case_id="code-test",
                    category="code_test",
                    query=f"test-{marker} request_payload fixture benchmark_handler test",
                    root_name=root_name,
                    source_path="tests/test_service_impl.py",
                    filters={
                        "logical_kinds": ["file"],
                        "current_only": True,
                        "file_kinds": ["code"],
                        "relationships": ["test"],
                        "path_globs": ["tests/*"],
                        "include_generated": False,
                    },
                ),
                _retrieval_benchmark_case(
                    self,
                    case_id="code-route",
                    category="code_route",
                    query=f"route-{marker} benchmarkRoute /api/benchmark/orders route",
                    root_name=root_name,
                    source_path="web/routes.ts",
                    filters={
                        "logical_kinds": ["file"],
                        "current_only": True,
                        "file_kinds": ["code"],
                        "relationships": ["route"],
                        "path_globs": ["web/*"],
                        "include_generated": False,
                    },
                ),
                _retrieval_benchmark_case(
                    self,
                    case_id="code-generated-suppression",
                    category="code_generated_suppression",
                    query=f"generated-{marker} GeneratedBenchmarkClient generated client",
                    root_name=root_name,
                    source_path="generated/client.py",
                    filters={
                        "logical_kinds": ["file"],
                        "current_only": True,
                        "file_kinds": ["code"],
                        "path_globs": ["generated/*"],
                        "include_generated": True,
                    },
                ),
                _retrieval_benchmark_case(
                    self,
                    case_id="code-config",
                    category="code_config",
                    query=f"config-{marker} retrieval ranking_mode code-aware",
                    root_name=root_name,
                    source_path="config/app.yaml",
                    filters={
                        "logical_kinds": ["file"],
                        "current_only": True,
                        "file_kinds": ["code"],
                        "relationships": ["config"],
                        "path_globs": ["config/*"],
                        "include_generated": False,
                    },
                ),
                _retrieval_benchmark_case(
                    self,
                    case_id="code-migration",
                    category="code_migration",
                    query=f"migration-{marker} benchmark_orders status index",
                    root_name=root_name,
                    source_path="db/migrations/0001_create_benchmark_orders.sql",
                    filters={
                        "logical_kinds": ["file"],
                        "current_only": True,
                        "file_kinds": ["code"],
                        "relationships": ["migration"],
                        "path_globs": ["db/migrations/*"],
                        "include_generated": False,
                    },
                ),
                _retrieval_benchmark_case(
                    self,
                    case_id="code-cross-root",
                    category="code_cross_root",
                    query=f"disambiguate-{marker} app benchmark_handler",
                    root_name=root_name,
                    source_path="app/service_impl.py",
                    filters={
                        "logical_kinds": ["file"],
                        "current_only": True,
                        "file_kinds": ["code"],
                        "path_globs": ["app/*"],
                        "include_generated": False,
                    },
                ),
                _retrieval_benchmark_case(
                    self,
                    case_id="mail-filter",
                    category="mail_filter",
                    query=f"{mail_token} benchmark message",
                    root_name=root_name,
                    source_path="mail-alpha/manifest.json",
                    filters={"logical_kinds": ["mail"], "current_only": True},
                ),
                _retrieval_benchmark_case(
                    self,
                    case_id="current-only",
                    category="current_only",
                    query=f"{current_token} current-only benchmark evidence",
                    root_name=root_name,
                    source_path="current-note.md",
                    filters={"current_only": True},
                ),
                _retrieval_benchmark_case(
                    self,
                    case_id="semantic-guardrail",
                    category="semantic_guardrail",
                    query=f"{guardrail_token} benchmark note",
                    root_name=root_name,
                    source_path="semantic-guardrail.md",
                    semantic_similarity=0.81,
                    expected_semantic_duplicate=False,
                ),
                _retrieval_benchmark_case(
                    self,
                    case_id="code-symbol-miss",
                    category="code_symbol_miss",
                    query=f"{fallback_token} missing symbol fallback note",
                    root_name=root_name,
                    source_path="code_fallback.py",
                    filters={
                        "logical_kinds": ["file"],
                        "current_only": True,
                        "file_kinds": ["code"],
                        "path_globs": ["code_fallback.py"],
                    },
                ),
                {
                    "id": "episode-brief",
                    "category": "brief_packing",
                    "query": f"{episode_token} benchmark memory",
                    "root_name": root_name,
                    "scope_mode": "local_only",
                    "filters": {"logical_kinds": ["episode"], "current_only": True},
                    "expected_ids": [episode_id],
                    "expected_brief_ids": [episode_id],
                    "expected_scope": "local",
                    "expect_suppression": False,
                },
                _retrieval_benchmark_case(
                    self,
                    case_id="contradiction-review",
                    category="lifecycle_review",
                    query=f"{contradiction_token} superseded older statement",
                    root_name=root_name,
                    source_path="contradiction-review.md",
                ),
            ]
            return cases, cleanup
        except Exception:
            cleanup()
            raise

    def _prepare_governance_shadow_benchmark_cases(self) -> tuple[list[dict[str, Any]], Any]:
        marker = uuid4().hex
        episode_ids: list[str] = []

        def cleanup() -> None:
            for episode_id in episode_ids:
                database.forget_episode(episode_id)

        try:
            stale_episode = database.insert_episode(
                title=f"governance stale {marker}",
                summary=f"governance-stale-{marker} stale claim should be proposed for review in shadow mode.",
                metadata={"benchmark_tag": f"governance-shadow:{marker}", "protected": False},
            )
            episode_ids.append(stale_episode)
            stale_claim = database.upsert_claim(
                subject_type="benchmark",
                subject_name=f"governance-stale-{marker}",
                predicate="needs",
                object_text="shadow review",
                confidence=0.42,
                episode_id=stale_episode,
                metadata={"benchmark_tag": f"governance-shadow:{marker}"},
            )
            database.transition_claim(
                claim_id=stale_claim["id"],
                transition="stale",
                reason="synthetic governance shadow stale evidence",
            )

            low_conf_episode = database.insert_episode(
                title=f"governance low confidence {marker}",
                summary=f"governance-low-confidence-{marker} low confidence memory needs human review.",
                metadata={"benchmark_tag": f"governance-shadow:{marker}", "protected": False},
            )
            episode_ids.append(low_conf_episode)
            low_conf_claim = database.upsert_claim(
                subject_type="benchmark",
                subject_name=f"governance-low-confidence-{marker}",
                predicate="needs",
                object_text="confidence review",
                confidence=0.18,
                episode_id=low_conf_episode,
                metadata={"benchmark_tag": f"governance-shadow:{marker}"},
            )

            duplicate_episode = database.insert_episode(
                title=f"governance duplicate canonical {marker}",
                summary=f"governance-duplicate-{marker} canonical duplicate evidence should be preserved.",
                metadata={"benchmark_tag": f"governance-shadow:{marker}", "protected": False},
            )
            duplicate_copy = database.insert_episode(
                title=f"governance duplicate copy {marker}",
                summary=f"governance-duplicate-{marker} canonical duplicate evidence should be preserved.",
                metadata={"benchmark_tag": f"governance-shadow:{marker}", "protected": False},
            )
            episode_ids.extend([duplicate_episode, duplicate_copy])

            current_episode = database.insert_episode(
                title=f"governance current protected {marker}",
                summary=f"governance-current-{marker} current protected memory must not be proposed for cleanup.",
                metadata={"benchmark_tag": f"governance-shadow:{marker}", "protected": True},
            )
            episode_ids.append(current_episode)
            current_claim = database.upsert_claim(
                subject_type="benchmark",
                subject_name=f"governance-current-{marker}",
                predicate="remains",
                object_text="current",
                confidence=0.95,
                episode_id=current_episode,
                metadata={"benchmark_tag": f"governance-shadow:{marker}", "protected": True},
            )

            contradiction_episode = database.insert_episode(
                title=f"governance contradiction {marker}",
                summary=f"governance-contradiction-{marker} contradiction should require review before automation.",
                metadata={"benchmark_tag": f"governance-shadow:{marker}", "protected": False},
            )
            episode_ids.append(contradiction_episode)
            contradiction_claim = database.upsert_claim(
                subject_type="benchmark",
                subject_name=f"governance-contradiction-{marker}",
                predicate="conflicts",
                object_text="old statement",
                confidence=0.55,
                episode_id=contradiction_episode,
                metadata={"benchmark_tag": f"governance-shadow:{marker}"},
            )
            database.transition_claim(
                claim_id=contradiction_claim["id"],
                transition="contradict",
                related_claim_id=current_claim["id"],
                reason="synthetic governance shadow contradiction",
            )
            capture_episode = database.insert_episode(
                title=f"governance capture ingestion {marker}",
                summary=f"governance-capture-{marker} approved capture ingestion failure needs sanitized recheck.",
                metadata={"benchmark_tag": f"governance-shadow:{marker}", "protected": False},
            )
            feedback_episode = database.insert_episode(
                title=f"governance feedback gap {marker}",
                summary=f"governance-feedback-{marker} repeated code retrieval miss needs escalation.",
                metadata={"benchmark_tag": f"governance-shadow:{marker}", "protected": False},
            )
            episode_ids.extend([capture_episode, feedback_episode])

            return [
                {
                    "id": "governance-stale",
                    "category": "governance_stale",
                    "query": f"governance-stale-{marker} shadow review",
                    "expected_ids": [stale_claim["id"], stale_episode],
                    "expected_brief_ids": [stale_claim["id"], stale_episode],
                    "expect_suppression": False,
                },
                {
                    "id": "governance-apply-recover",
                    "category": "governance_apply_recover",
                    "query": f"governance-stale-{marker} apply recover",
                    "expected_ids": [stale_claim["id"], stale_episode],
                    "expected_brief_ids": [stale_claim["id"], stale_episode],
                    "expect_suppression": False,
                },
                {
                    "id": "governance-stale-proposal-conflict",
                    "category": "governance_stale_proposal_conflict",
                    "query": f"governance-low-confidence-{marker} stale proposal conflict",
                    "expected_ids": [low_conf_claim["id"], low_conf_episode],
                    "expected_brief_ids": [low_conf_claim["id"], low_conf_episode],
                    "expect_suppression": False,
                },
                {
                    "id": "governance-low-confidence",
                    "category": "governance_low_confidence",
                    "query": f"governance-low-confidence-{marker} confidence review",
                    "expected_ids": [low_conf_claim["id"], low_conf_episode],
                    "expected_brief_ids": [low_conf_claim["id"], low_conf_episode],
                    "expect_suppression": False,
                },
                {
                    "id": "governance-duplicate",
                    "category": "governance_duplicate",
                    "query": f"governance-duplicate-{marker} canonical duplicate",
                    "expected_ids": [duplicate_episode, duplicate_copy],
                    "expected_brief_ids": [duplicate_episode, duplicate_copy],
                    "expect_suppression": False,
                },
                {
                    "id": "governance-duplicate-cluster",
                    "category": "governance_duplicate_cluster",
                    "query": f"governance-duplicate-{marker} cluster canonical",
                    "expected_ids": [duplicate_episode, duplicate_copy],
                    "expected_brief_ids": [duplicate_episode, duplicate_copy],
                    "expect_suppression": False,
                },
                {
                    "id": "governance-contradiction",
                    "category": "governance_contradiction",
                    "query": f"governance-contradiction-{marker} contradiction review",
                    "expected_ids": [contradiction_claim["id"], contradiction_episode],
                    "expected_brief_ids": [contradiction_claim["id"], contradiction_episode],
                    "expect_suppression": False,
                },
                {
                    "id": "governance-current-guardrail",
                    "category": "governance_guardrail_current",
                    "query": f"governance-current-{marker} current protected",
                    "expected_ids": [current_claim["id"], current_episode],
                    "expected_brief_ids": [current_claim["id"], current_episode],
                    "expect_suppression": False,
                },
                {
                    "id": "governance-capture-ingestion",
                    "category": "governance_capture_ingestion",
                    "query": f"governance-capture-{marker} ingestion recheck",
                    "expected_ids": [capture_episode],
                    "expected_brief_ids": [capture_episode],
                    "expect_suppression": False,
                },
                {
                    "id": "governance-feedback-gap",
                    "category": "governance_feedback_gap",
                    "query": f"governance-feedback-{marker} retrieval escalation",
                    "expected_ids": [feedback_episode],
                    "expected_brief_ids": [feedback_episode],
                    "expect_suppression": False,
                },
            ], cleanup
        except Exception:
            cleanup()
            raise

    def audit(self, limit: int = 50) -> list[dict[str, Any]]:
        return database.list_audit_events(limit=limit)

    def forget(self, memory_id: str, reason: str = "user_request") -> dict[str, Any]:
        deleted = database.forget_episode(memory_id, reason=reason)
        return {"id": memory_id, "deleted": deleted}

    def upsert_claim(
        self,
        *,
        subject_type: str,
        subject_name: str,
        predicate: str,
        object_text: str,
        confidence: float = 0.5,
        episode_id: str | None = None,
        metadata: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        redacted_subject, subject_findings = redact_text(subject_name)
        redacted_predicate, predicate_findings = redact_text(predicate)
        redacted_object, object_findings = redact_text(object_text)
        redacted_metadata, metadata_findings = _redact_metadata(metadata or {})
        all_findings = subject_findings + predicate_findings + object_findings + metadata_findings
        if all_findings:
            existing_redactions = redacted_metadata.get("redactions")
            redactions = existing_redactions if isinstance(existing_redactions, list) else []
            redacted_metadata = {
                **redacted_metadata,
                "redactions": [*redactions, *(finding.kind for finding in all_findings)],
            }
        return database.upsert_claim(
            subject_type=subject_type,
            subject_name=redacted_subject,
            predicate=redacted_predicate,
            object_text=redacted_object,
            confidence=confidence,
            episode_id=episode_id,
            metadata=redacted_metadata,
        )

    def get_claim(self, claim_id: str) -> dict[str, Any]:
        claim = database.get_claim(claim_id)
        if claim is None:
            raise LookupError(f"claim not found: {claim_id}")
        return claim

    def list_claims(
        self,
        *,
        review: str = "all",
        state: str | None = None,
        q: str | None = None,
        limit: int = 50,
    ) -> dict[str, Any]:
        return {
            "claims": database.list_claims(review=review, state=state, q=q, limit=limit),
            "counts": database.claim_review_counts(),
        }

    def list_retention_policies(self) -> dict[str, Any]:
        return {"policies": database.list_retention_policies()}

    def set_retention_policy(
        self,
        *,
        memory_class: str,
        half_life_days: int,
        min_confidence: float,
        action: str,
        actor: str = "system",
        reason: str,
    ) -> dict[str, Any]:
        return database.set_retention_policy(
            memory_class=memory_class,
            half_life_days=half_life_days,
            min_confidence=min_confidence,
            action=action,
            actor=actor,
            reason=reason,
        )

    def retention_quality_report(self, *, limit: int = 25) -> dict[str, Any]:
        return database.retention_quality_report(limit=limit)

    def refresh_semantic_duplicate_clusters(
        self,
        *,
        memory_class: str = "all",
        root_name: str | None = None,
        threshold: float | None = None,
        limit: int = 1000,
    ) -> dict[str, Any]:
        return database.refresh_semantic_duplicate_clusters(
            memory_class=memory_class,
            root_name=root_name,
            threshold=threshold,
            limit=limit,
        )

    def list_semantic_duplicate_clusters(
        self,
        *,
        memory_class: str | None = None,
        root_name: str | None = None,
        limit: int = 50,
    ) -> dict[str, Any]:
        return database.list_semantic_duplicate_clusters(
            memory_class=memory_class,
            root_name=root_name,
            limit=limit,
        )

    def search_index_status(self, *, root_name: str | None = None) -> dict[str, Any]:
        return database.search_index_status(root_name=root_name)

    def search_index_sync(
        self,
        *,
        owner_class: str = "all",
        root_name: str | None = None,
        limit: int = database.DEFAULT_SEARCH_INDEX_JOB_LIMIT,
    ) -> dict[str, Any]:
        return database.enqueue_search_index_sync(owner_class=owner_class, root_name=root_name, limit=limit)

    def search_index_rebuild(self, *, root_name: str | None = None, confirmed: bool = False) -> dict[str, Any]:
        return database.mark_search_index_rebuild(root_name=root_name, confirmed=confirmed)

    def purge_deleted_corpora(self, *, confirmed: bool = False) -> dict[str, Any]:
        return database.purge_deleted_corpus_residue(confirmed=confirmed)

    def reprocess_derived_state(
        self,
        *,
        all_roots: bool = False,
        root_name: str | None = None,
        confirm: bool = False,
        force: bool = False,
        clear_caches: str = "all",
        process: bool = False,
        limit: int = 1000,
        workers: int | None = None,
        max_passes: int = 1,
    ) -> dict[str, Any]:
        if all_roots and root_name:
            raise ValueError("use either --all-roots or --root, not both")
        if not all_roots and not root_name:
            raise ValueError("maintenance reprocess requires --all-roots or --root")
        row_limit = max(1, min(int(limit or 1000), 10000))
        pass_count = max(1, min(int(max_passes or 1), 20))
        inventory_before = database.inventory_reprocess_derived_state(
            all_roots=all_roots,
            root_name=root_name,
            force=force,
            limit=row_limit,
        )
        selected_caches = _parse_reprocess_cache_selection(clear_caches)
        dry_run = not bool(confirm)
        blocked_reasons: list[str] = []
        running_jobs = inventory_before.get("running_jobs") if isinstance(inventory_before.get("running_jobs"), list) else []
        if confirm and running_jobs:
            blocked_reasons.append(f"{len(running_jobs)} scoped corpus/search-index job(s) are already running")
            cache_actions = _reprocess_cache_actions(selected_caches, dry_run=True)
            return {
                "settings_mutated": False,
                "dry_run": False,
                "scope": inventory_before.get("scope", {}),
                "counts_before": inventory_before.get("counts", {}),
                "counts_after": inventory_before.get("counts", {}),
                "cache_actions": cache_actions,
                "jobs_obsoleted": 0,
                "assets_requeued": 0,
                "search_records_marked": 0,
                "backfill": [],
                "verification": {"running_jobs": len(running_jobs), "status": "blocked"},
                "blocked_reasons": blocked_reasons,
            }
        if dry_run:
            cache_actions = _reprocess_cache_actions(selected_caches, dry_run=True)
            return {
                "settings_mutated": False,
                "dry_run": True,
                "scope": inventory_before.get("scope", {}),
                "counts_before": inventory_before.get("counts", {}),
                "counts_after": inventory_before.get("counts", {}),
                "cache_actions": cache_actions,
                "jobs_obsoleted": 0,
                "assets_requeued": 0,
                "search_records_marked": 0,
                "backfill": [],
                "verification": {"running_jobs": len(running_jobs), "status": "dry_run"},
                "blocked_reasons": [],
            }

        cache_actions = _reprocess_cache_actions(selected_caches, dry_run=False)
        invalidation = database.invalidate_reprocess_derived_state(
            all_roots=all_roots,
            root_name=root_name,
            force=force,
            limit=row_limit,
            actor="maintenance",
        )
        backfill_runs: list[dict[str, Any]] = []
        if process:
            for pass_index in range(1, pass_count + 1):
                corpus_result = self.run_corpus_backfill(kind="all", limit=row_limit, workers=workers)
                backfill_runs.append(
                    {
                        "pass": pass_index,
                        "corpus": corpus_result,
                    }
                )
        search_sync = database.enqueue_search_index_sync(
            owner_class="all",
            root_name=None if all_roots else root_name,
            limit=row_limit,
        )
        if process:
            for run in backfill_runs:
                run["search_index"] = self.run_corpus_backfill(kind="search-index", limit=row_limit, workers=workers)
        inventory_after = database.inventory_reprocess_derived_state(
            all_roots=all_roots,
            root_name=root_name,
            force=force,
            limit=row_limit,
        )
        return {
            "settings_mutated": False,
            "dry_run": False,
            "scope": inventory_before.get("scope", {}),
            "counts_before": inventory_before.get("counts", {}),
            "counts_after": inventory_after.get("counts", {}),
            "cache_actions": cache_actions,
            "jobs_obsoleted": int(invalidation.get("jobs_obsoleted") or 0),
            "assets_requeued": int(invalidation.get("assets_requeued") or 0),
            "search_records_marked": int(invalidation.get("search_records_marked") or 0),
            "backfill": backfill_runs,
            "verification": {
                "status": "processed" if process else "queued",
                "search_index_sync": search_sync,
                "running_jobs_after": int((inventory_after.get("counts") or {}).get("running_jobs") or 0),
            },
            "blocked_reasons": blocked_reasons,
        }

    def list_capture_review_jobs(self, *, status: str = "pending_review", limit: int = 50) -> dict[str, Any]:
        return {"status": status, "jobs": database.list_capture_review_jobs(status=status, limit=limit)}

    def review_capture_job(
        self,
        *,
        job_id: str,
        decision: str,
        rationale: str,
        actor: str = "system",
    ) -> dict[str, Any]:
        return database.review_capture_job(
            job_id=job_id,
            decision=decision,
            rationale=rationale,
            actor=actor,
        )

    def decide_capture_review_job(
        self,
        *,
        job_id: str,
        decision: str,
        reason: str | None = None,
        rationale: str | None = None,
        actor: str = "system",
    ) -> dict[str, Any]:
        return self.review_capture_job(
            job_id=job_id,
            decision=decision,
            rationale=rationale if rationale is not None else reason or "",
            actor=actor,
        )

    def ingest_capture_review_jobs(
        self,
        *,
        job_id: str | None = None,
        limit: int = 25,
        dry_run: bool = False,
        actor: str = "system",
    ) -> dict[str, Any]:
        jobs = database.list_capture_ingestion_jobs(job_id=job_id, limit=limit)
        if job_id and not jobs:
            raise LookupError(f"approved capture review job not found: {job_id}")
        totals = {
            "dry_run": bool(dry_run),
            "requested": len(jobs),
            "processed": 0,
            "ingested": 0,
            "skipped": 0,
            "failed": 0,
            "blocked": 0,
            "settings_mutated": False,
            "jobs": [],
        }
        for job in jobs:
            outcome = self._ingest_capture_review_job(job, dry_run=dry_run, actor=actor)
            totals["processed"] += 1
            status = str(outcome.get("ingestion", {}).get("status") or "")
            if status == "ingested":
                totals["ingested"] += 1
            elif status in {"skipped", "would_skip"}:
                totals["skipped"] += 1
            elif status == "blocked_missing_dependency":
                totals["blocked"] += 1
            elif status == "failed":
                totals["failed"] += 1
            totals["jobs"].append(outcome)
        return totals

    def _ingest_capture_review_job(self, job: dict[str, Any], *, dry_run: bool, actor: str) -> dict[str, Any]:
        payload = job.get("payload") if isinstance(job.get("payload"), dict) else {}
        source_path = _capture_backfill_source_path(payload)
        job_id = str(job.get("id") or "")
        base = {
            "job_id": job_id,
            "job_type": str(job.get("job_type") or ""),
            "source_leaf": _source_leaf(source_path) if source_path else None,
            "dry_run": bool(dry_run),
        }
        if str(job.get("job_type") or "") != "codex_backfill":
            ingestion = {**base, "status": "skipped", "skip_reasons": ["unsupported_job_type"], "episode_ids": []}
            return _capture_ingestion_response(job, ingestion)
        if source_path is None:
            ingestion = {**base, "status": "blocked_missing_dependency", "error": "source_missing", "episode_ids": []}
            return self._finalize_capture_ingestion(job_id, "blocked_missing_dependency", ingestion, actor, "capture.ingestion_failed", dry_run)
        if not source_path.exists() or not source_path.is_file():
            ingestion = {**base, "status": "blocked_missing_dependency", "error": "source_missing", "episode_ids": []}
            return self._finalize_capture_ingestion(job_id, "blocked_missing_dependency", ingestion, actor, "capture.ingestion_failed", dry_run)
        source_hash = _file_source_hash(source_path)
        ingestion_base = {**base, "source_hash": source_hash, "source_leaf": _source_leaf(source_path)}
        if database.codex_backfill_source_hash_exists(source_hash=source_hash):
            status = "would_skip" if dry_run else "skipped"
            ingestion = {**ingestion_base, "status": status, "skip_reasons": ["duplicate_source_hash"], "episode_ids": []}
            return self._finalize_capture_ingestion(job_id, "completed", ingestion, actor, "capture.ingestion_skipped", dry_run)
        try:
            records = _normalize_codex_backfill_records(source_path)
        except Exception as exc:
            ingestion = {**ingestion_base, "status": "failed", "error": "parse_failed", "error_type": type(exc).__name__, "episode_ids": []}
            return self._finalize_capture_ingestion(job_id, "failed", ingestion, actor, "capture.ingestion_failed", dry_run)
        valid_records = [record for record in records if len(str(record.get("body") or "").strip()) >= 32]
        if not valid_records:
            status = "would_skip" if dry_run else "skipped"
            ingestion = {**ingestion_base, "status": status, "skip_reasons": ["empty_or_too_short"], "record_count": len(records), "episode_ids": []}
            return self._finalize_capture_ingestion(job_id, "completed", ingestion, actor, "capture.ingestion_skipped", dry_run)
        if dry_run:
            ingestion = {
                **ingestion_base,
                "status": "would_ingest",
                "record_count": len(valid_records),
                "episode_ids": [],
            }
            return _capture_ingestion_response(job, ingestion)
        episode_ids: list[str] = []
        for index, record in enumerate(valid_records):
            metadata = _codex_backfill_metadata(
                record,
                job=job,
                source_path=source_path,
                source_hash=source_hash,
                record_index=index,
            )
            result = self.remember(
                str(record.get("title") or source_path.stem),
                str(record.get("body") or ""),
                metadata=metadata,
                cwd=record.get("cwd"),
                root_name=record.get("root_name"),
            )
            episode_ids.append(result.id)
        ingestion = {
            **ingestion_base,
            "status": "ingested",
            "record_count": len(valid_records),
            "episode_ids": episode_ids,
        }
        return self._finalize_capture_ingestion(job_id, "completed", ingestion, actor, "capture.ingested", dry_run)

    def _finalize_capture_ingestion(
        self,
        job_id: str,
        status: str,
        ingestion: dict[str, Any],
        actor: str,
        event_type: str,
        dry_run: bool,
    ) -> dict[str, Any]:
        if dry_run:
            return {"id": job_id, "status": status, "ingestion": ingestion}
        job = database.update_capture_job_ingestion(job_id=job_id, status=status, ingestion=ingestion)
        database.record_audit_event(
            event_type=event_type,
            target_table="capture_jobs",
            target_id=job_id,
            details={
                "status": ingestion.get("status"),
                "source_leaf": ingestion.get("source_leaf"),
                "source_hash": ingestion.get("source_hash"),
                "episode_count": len(ingestion.get("episode_ids") or []),
                "actor": actor,
            },
        )
        return _capture_ingestion_response(job, ingestion)

    def transition_claim(
        self,
        *,
        claim_id: str,
        transition: str,
        related_claim_id: str | None = None,
        reason: str | None = None,
        actor: str = "system",
        confidence_delta: float = 0.0,
    ) -> dict[str, Any]:
        return database.transition_claim(
            claim_id=claim_id,
            transition=transition,
            related_claim_id=related_claim_id,
            reason=reason,
            actor=actor,
            confidence_delta=confidence_delta,
        )

    def traverse_graph(
        self,
        *,
        entity_id: str,
        relation_types: list[str] | None = None,
        max_depth: int = 2,
        direction: str = "out",
        limit: int = 100,
    ) -> dict[str, Any]:
        return database.traverse_entity_graph(
            entity_id=entity_id,
            relation_types=relation_types,
            max_depth=max_depth,
            direction=direction,
            limit=limit,
        )

    def export_wiki(self, output_dir: str | Path, limit: int = 500) -> dict[str, Any]:
        path = Path(output_dir)
        path.mkdir(parents=True, exist_ok=True)
        episodes = database.list_episodes(limit=limit)

        index_lines = [
            "# Flux-LLM-KB Export",
            "",
            "This export is generated from the local private database. Review before sharing.",
            "",
        ]
        for episode in episodes:
            filename = f"{_slugify(episode['title'])}-{episode['id'][:8]}.md"
            target = path / filename
            target.write_text(_episode_markdown(episode), encoding="utf-8")
            index_lines.append(f"- [{episode['title']}]({filename})")

        (path / "index.md").write_text("\n".join(index_lines) + "\n", encoding="utf-8")
        database.record_audit_event(
            event_type="wiki.exported",
            details={"output_dir": str(path), "episode_count": len(episodes)},
        )
        return {"output_dir": str(path), "episode_count": len(episodes)}

    def queue_codex_backfill(self, source_dir: str | Path, *, dry_run: bool = False) -> dict[str, Any]:
        root = Path(source_dir).expanduser()
        candidates = [
            path
            for path in root.rglob("*")
            if path.is_file() and path.suffix.lower() in {".json", ".jsonl", ".md", ".txt"}
        ]
        if dry_run:
            return {"source_dir": str(root), "candidate_count": len(candidates), "queued": 0}

        queued = 0
        for path in candidates:
            database.enqueue_capture_job(
                job_type="codex_backfill",
                payload={"path": str(path), "status": "pending_review"},
            )
            queued += 1
        return {"source_dir": str(root), "candidate_count": len(candidates), "queued": queued}

    def sync_corpus(
        self,
        *,
        root_name: str | None = None,
        path: str | Path | None = None,
        dry_run: bool = False,
        reason: str = "manual_sync",
        progress_callback: Callable[[dict[str, Any]], None] | None = None,
    ) -> dict[str, Any]:
        root = _select_root(root_name=root_name, path=path)
        return self._sync_corpus_selected_root(
            root=root,
            path=path,
            dry_run=dry_run,
            reason=reason,
            progress_callback=progress_callback,
            manifest=_manifest_store(root["name"]),
        )

    def _sync_corpus_selected_root(
        self,
        *,
        root: dict[str, Any],
        path: str | Path | None = None,
        dry_run: bool = False,
        reason: str = "manual_sync",
        progress_callback: Callable[[dict[str, Any]], None] | None = None,
        manifest: dict[str, dict[str, Any]] | None = None,
        glob_policy: dict[str, Any] | None = None,
        container_limits: dict[str, int] | None = None,
        hash_parallelism: int | None = None,
    ) -> dict[str, Any]:
        if glob_policy is None:
            glob_policy = _configured_glob_policy(root)
        if manifest is None:
            manifest = _manifest_store(root["name"])
        if container_limits is None:
            container_limits = _configured_container_limits()
        if hash_parallelism is None:
            hash_parallelism = _configured_hash_parallelism()
        policy = CorpusPolicy(
            root_path=Path(root["root_path"]),
            recursive=root["recursive"],
            include_globs=tuple(glob_policy["include_globs"]),
            exclude_globs=tuple(glob_policy["exclude_globs"]),
            strict_indexing=strict_indexing_enabled(root.get("metadata") if isinstance(root.get("metadata"), dict) else {}),
            max_inline_bytes=root["max_inline_bytes"],
            heavy_threshold_bytes=root["heavy_threshold_bytes"],
            **container_limits,
            hash_parallelism=hash_parallelism,
            manifest_lookup=lambda relative_path, store=manifest: store.get(relative_path),
            stability_quiet_seconds=_configured_stability_quiet_seconds() if reason == "watch_event" else 0.0,
            large_file_stability_quiet_seconds=_configured_large_file_stability_quiet_seconds() if reason == "watch_event" else 0.0,
            mail_spool=bool((root.get("metadata") or {}).get("mail_profile")) if isinstance(root.get("metadata"), dict) else False,
        )
        plan = scan_path(root["root_path"], policy, target_path=path, progress_callback=progress_callback)
        return database.persist_crawl_plan(
            root_name=root["name"],
            plan=plan,
            dry_run=dry_run,
            reason=reason,
            unseen_purge_grace_seconds=_configured_unseen_asset_purge_grace_seconds(),
            progress_callback=progress_callback,
        )

    def reconcile_unseen_assets_for_root(self, *, root_name: str, reason: str = "root_policy_update") -> dict[str, Any]:
        root = database.get_monitored_root(root_name)
        if root is None:
            raise ValueError(f"monitored root not found: {root_name}")
        glob_policy = _configured_glob_policy(root)
        policy = CorpusPolicy(
            root_path=Path(root["root_path"]),
            recursive=root["recursive"],
            include_globs=tuple(glob_policy["include_globs"]),
            exclude_globs=tuple(glob_policy["exclude_globs"]),
            strict_indexing=strict_indexing_enabled(root.get("metadata") if isinstance(root.get("metadata"), dict) else {}),
            max_inline_bytes=root["max_inline_bytes"],
            heavy_threshold_bytes=root["heavy_threshold_bytes"],
        )
        active_paths = database.list_active_source_asset_paths(root_name=root["name"])
        unseen_paths = [path for path in active_paths if not _is_included(path, policy, [])]
        if not unseen_paths:
            return {"root_name": root["name"], "reason": reason, "assets_marked": 0, "jobs_cancelled": 0}
        result = database.mark_unseen_source_assets(
            root_name=root["name"],
            paths=unseen_paths,
            reason=reason,
            grace_seconds=_configured_unseen_asset_purge_grace_seconds(),
        )
        return {
            "root_name": root["name"],
            "reason": reason,
            "assets_marked": int(result.get("assets_marked") or 0),
            "jobs_cancelled": int(result.get("jobs_cancelled") or 0),
        }

    def watch_probe(self, *, timeout_seconds: float = 2.0) -> dict[str, Any]:
        return probe_watcher_backend(
            backend_policy=_configured_watcher_backend(),
            timeout_seconds=max(0.1, float(timeout_seconds or 2.0)),
        )

    def worker_status(self, *, family: str = "all") -> dict[str, Any]:
        rows = collect_acceleration_status()["worker_families"]
        normalized = str(family or "all").lower()
        if normalized != "all":
            rows = [row for row in rows if row.get("family") == normalized]
        return {"family": normalized, "families": rows}

    def watch_events(self, *, limit: int = 50) -> dict[str, Any]:
        return {"events": database.list_watch_events(limit=limit)}

    def benchmark_history(
        self,
        *,
        fixture: str | None = None,
        mode: str | None = None,
        label: str | None = None,
        warm_state: str | None = None,
        scope_type: str | None = None,
        scope_hash: str | None = None,
        deployment_label: str | None = None,
        scenario: str | None = None,
        freshness_hours: int | None = None,
        limit: int = 20,
    ) -> dict[str, Any]:
        normalized = None if fixture in {None, "", "all"} else str(fixture)
        normalized_mode = None if mode in {None, "", "all"} else str(mode)
        return {
            "fixture": normalized or "all",
            "mode": normalized_mode or "all",
            "runs": database.list_benchmark_runs(
                fixture=normalized,
                mode=normalized_mode,
                label=label or None,
                warm_state=warm_state or None,
                scope_type=scope_type or None,
                scope_hash=scope_hash or None,
                deployment_label=deployment_label or None,
                scenario=scenario or None,
                freshness_hours=freshness_hours,
                limit=limit,
            ),
        }

    def indexer_reliability_status(
        self,
        *,
        root_name: str | None = None,
        path: str | None = None,
        label: str | None = None,
        deployment_label: str | None = None,
        compare_label: str | None = None,
        freshness_hours: int = 336,
        limit: int = 100,
    ) -> dict[str, Any]:
        scope = "root" if root_name or path else "synthetic"
        scope_descriptor = _benchmark_scope_descriptor(scope=scope, root_name=root_name, path=path)
        runs = database.list_benchmark_runs(
            label=label or None,
            compare_label=compare_label or None,
            deployment_label=deployment_label or None,
            freshness_hours=max(1, int(freshness_hours or 336)),
            limit=max(1, min(int(limit or 100), 500)),
        )
        try:
            worker_families = collect_acceleration_status().get("worker_families", [])
        except Exception:
            worker_families = []
        try:
            watcher_events = database.list_watch_events(limit=25)
        except Exception:
            watcher_events = []
        return build_indexer_reliability_report(
            runs=runs,
            scope_type=scope_descriptor["scope_type"],
            scope_hash=scope_descriptor.get("scope_hash"),
            label=label,
            deployment_label=deployment_label,
            worker_families=worker_families,
            watcher_events=watcher_events,
            freshness_hours=freshness_hours,
        )

    def run_indexer_reliability(
        self,
        *,
        scope: str = "synthetic",
        root_name: str | None = None,
        path: str | None = None,
        label: str | None = None,
        deployment_label: str | None = None,
        compare_label: str | None = None,
        max_files: int = 1000,
        passes: int = 2,
        include_cache_readiness: bool = False,
        include_tuning: bool = True,
        evidence_level: str = "standard",
    ) -> dict[str, Any]:
        normalized_scope = str(scope or "synthetic").strip().lower()
        if normalized_scope in {"all-roots", "all_roots"}:
            normalized_scope = "all_roots"
        normalized_evidence = str(evidence_level or "standard").strip().lower()
        if normalized_evidence not in {"standard", "full"}:
            raise ValueError("evidence_level must be standard or full")
        if normalized_evidence == "full":
            include_cache_readiness = True
            include_tuning = True
        self.run_benchmark(
            fixture="all",
            files=10,
            mode="all",
            passes=max(1, int(passes or 2)),
            label=label,
            compare_label=compare_label,
            deployment_label=deployment_label,
            scenario="reliability",
        )
        if normalized_scope == "all_roots":
            roots = [
                root
                for root in database.crawl_root_summaries(limit_assets=0, limit_jobs=0)
                if root.get("enabled")
            ]
            for root in roots:
                self.run_benchmark(
                    fixture="all",
                    files=10,
                    mode="scan",
                    passes=max(1, int(passes or 2)),
                    label=label,
                    compare_label=compare_label,
                    deployment_label=deployment_label,
                    scenario="host_cloud",
                    scope="root",
                    root_name=str(root.get("name") or ""),
                    max_files=max(1, int(max_files or 1000)),
                )
            if include_cache_readiness:
                self.run_benchmark(
                    fixture="image-heavy",
                    files=10,
                    mode="model",
                    passes=1,
                    label=label,
                    compare_label=compare_label,
                    deployment_label=deployment_label,
                    scenario="cache_readiness",
                )
            if include_tuning:
                for root in roots:
                    self.run_benchmark(
                        fixture="all",
                        files=10,
                        mode="scan",
                        passes=max(1, int(passes or 2)),
                        label=label,
                        compare_label=compare_label,
                        deployment_label=deployment_label,
                        scenario="tuning",
                        scope="root",
                        root_name=str(root.get("name") or ""),
                        max_files=max(1, int(max_files or 1000)),
                    )
            return self.indexer_reliability_roots(freshness_hours=336)
        if normalized_scope != "synthetic":
            self.run_benchmark(
                fixture="all",
                files=10,
                mode="scan",
                passes=max(1, int(passes or 2)),
            label=label,
            compare_label=compare_label,
            deployment_label=deployment_label,
            scenario="host_cloud",
                scope=normalized_scope,
                root_name=root_name,
                path=path,
                max_files=max(1, int(max_files or 1000)),
            )
        if include_cache_readiness:
            self.run_benchmark(
                fixture="image-heavy",
                files=10,
                mode="model",
                passes=1,
            label=label,
            compare_label=compare_label,
            deployment_label=deployment_label,
            scenario="cache_readiness",
            )
        if include_tuning:
            tuning_kwargs: dict[str, Any] = {}
            if normalized_scope != "synthetic":
                tuning_kwargs.update(
                    {
                        "scope": normalized_scope,
                        "root_name": root_name,
                        "path": path,
                        "max_files": max(1, int(max_files or 1000)),
                    }
                )
            self.run_benchmark(
                fixture="all",
                files=10,
                mode="scan",
                passes=max(1, int(passes or 2)),
                label=label,
                compare_label=compare_label,
                deployment_label=deployment_label,
                scenario="tuning",
                **tuning_kwargs,
            )
        return self.indexer_reliability_status(
            root_name=root_name if normalized_scope != "synthetic" else None,
            path=path if normalized_scope == "path" else None,
            label=label,
            deployment_label=deployment_label,
            compare_label=compare_label,
        )

    def indexer_root_reliability(self, root_name: str) -> dict[str, Any]:
        summaries = database.crawl_root_summaries(limit_assets=0, limit_jobs=0)
        root = next((item for item in summaries if item.get("name") == root_name), None)
        if not root:
            raise ValueError(f"unknown monitored root: {root_name}")
        scope_hash = _benchmark_scope_hash("monitored_root", str(root.get("name") or ""), str(root.get("root_path") or ""))
        runs = database.list_benchmark_runs(
            scenario="host_cloud",
            scope_type="monitored_root",
            scope_hash=scope_hash,
            limit=1,
        )
        latest_benchmark = runs[0] if runs else None
        return build_root_reliability_card(
            root=root,
            asset_counts=root.get("asset_counts") or {},
            job_counts=root.get("job_counts") or {},
            latest_crawl=root.get("latest_crawl"),
            latest_benchmark=latest_benchmark,
            scope_hash=scope_hash,
        )

    def indexer_reliability_roots(
        self,
        *,
        include_disabled: bool = False,
        freshness_hours: int = 336,
        limit: int = 100,
    ) -> dict[str, Any]:
        summaries = database.crawl_root_summaries(limit_assets=0, limit_jobs=0)
        cards: list[dict[str, Any]] = []
        capped = max(1, min(int(limit or 100), 500))
        for root in summaries[:capped]:
            if not include_disabled and not root.get("enabled"):
                continue
            scope_hash = _benchmark_scope_hash("monitored_root", str(root.get("name") or ""), str(root.get("root_path") or ""))
            runs = database.list_benchmark_runs(
                scenario="host_cloud",
                scope_type="monitored_root",
                scope_hash=scope_hash,
                freshness_hours=max(1, int(freshness_hours or 336)),
                limit=1,
            )
            cards.append(
                build_root_reliability_card(
                    root=root,
                    asset_counts=root.get("asset_counts") or {},
                    job_counts=root.get("job_counts") or {},
                    latest_crawl=root.get("latest_crawl"),
                    latest_benchmark=runs[0] if runs else None,
                    scope_hash=scope_hash,
                )
            )
        return build_roots_reliability_report(
            roots=cards,
            include_disabled=include_disabled,
            freshness_hours=freshness_hours,
        )

    def operator_evidence(
        self,
        *,
        label: str | None = None,
        deployment_label: str | None = None,
        compare_label: str | None = None,
        freshness_hours: int = 336,
        limit: int = 100,
    ) -> dict[str, Any]:
        reliability = self.indexer_reliability_status(
            label=label,
            deployment_label=deployment_label,
            compare_label=compare_label,
            freshness_hours=freshness_hours,
            limit=limit,
        )
        roots = self.indexer_reliability_roots(freshness_hours=freshness_hours, limit=limit)
        code_status = self.code_status()
        diagnostics = self.operational_diagnostics(section="all", limit=25, include_details=True)
        return build_operator_evidence_report(
            reliability=reliability,
            roots=roots,
            code_status=code_status,
            diagnostics=diagnostics,
        )

    def code_status(self, *, root_name: str | None = None, cwd: str | None = None) -> dict[str, Any]:
        effective_root_name = _resolve_code_root_name(root_name=root_name, cwd=cwd)
        payload = database.code_index_status(root_name=effective_root_name)
        roots = payload.get("roots") if isinstance(payload, dict) else []
        totals = payload.get("totals") if isinstance(payload, dict) else {}
        report = build_code_status_report(roots=roots or [], totals=totals or {})
        feedback = self.code_feedback_summary(root_name=effective_root_name)
        report["feedback_summary"] = feedback
        try:
            latest_retrieval = database.list_retrieval_benchmark_runs(suite="standard", limit=1)
        except Exception:
            latest_retrieval = []
        report["retrieval_benchmark_summary"] = latest_retrieval[0] if latest_retrieval else {}
        report["gaps"] = _code_gaps(report, feedback, report["retrieval_benchmark_summary"])
        return report

    def code_search(
        self,
        query: str,
        *,
        root_name: str | None = None,
        cwd: str | None = None,
        mode: str = CODE_SEARCH_LITERAL_SYMBOL_MODE,
        language: str | None = None,
        symbol_kind: str | None = None,
        relationship: str | None = None,
        path_glob: str | None = None,
        include_generated: bool = False,
        limit: int = 20,
    ) -> dict[str, Any]:
        normalized_mode = _normalize_code_search_mode(mode)
        effective_root_name = _resolve_code_root_name(root_name=root_name, cwd=cwd)
        capped_limit = max(1, min(int(limit or 20), 100))
        if normalized_mode == CODE_SEARCH_FULL_TEXT_MODE:
            filters = _code_full_text_filters(
                language=language,
                symbol_kind=symbol_kind,
                relationship=relationship,
                path_glob=path_glob,
                include_generated=include_generated,
            )
            results = self.search(
                query,
                limit=capped_limit,
                root_name=effective_root_name,
                scope_mode="local_only" if effective_root_name else "global",
                filters=filters,
            )
            return {
                "query": query,
                "mode": normalized_mode,
                "settings_mutated": False,
                "results": [_sanitize_full_text_code_result(row) for row in results],
            }
        rows = database.search_code_symbols(
            query=query,
            root_name=effective_root_name,
            language=language,
            symbol_kind=symbol_kind,
            relationship=relationship,
            path_glob=path_glob,
            include_generated=include_generated,
            limit=capped_limit,
        )
        return {
            "query": query,
            "mode": normalized_mode,
            "settings_mutated": False,
            "results": [sanitize_code_result(row) for row in rows],
        }

    def code_symbol_lookup(
        self,
        symbol: str,
        *,
        root_name: str | None = None,
        language: str | None = None,
        include_references: bool = True,
        limit: int = 20,
    ) -> dict[str, Any]:
        payload = database.lookup_code_symbol(
            symbol=symbol,
            root_name=root_name,
            language=language,
            include_references=include_references,
            limit=max(1, min(int(limit or 20), 100)),
        )
        return sanitize_code_lookup(payload)

    def record_code_feedback(
        self,
        *,
        query: str,
        root_name: str | None = None,
        result_count: int = 0,
        surface: str = "unknown",
        miss_category: str = "other",
        expected_symbol: str | None = None,
        path: str | None = None,
        metadata: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        return database.record_code_feedback_event(
            query=query,
            root_name=root_name,
            result_count=result_count,
            surface=surface,
            miss_category=miss_category,
            expected_symbol=expected_symbol,
            path=path,
            metadata=metadata or {},
        )

    def code_feedback_summary(
        self,
        *,
        root_name: str | None = None,
        limit: int = 20,
    ) -> dict[str, Any]:
        try:
            return database.code_feedback_summary(root_name=root_name, limit=limit)
        except Exception:
            return {"settings_mutated": False, "root_name": root_name, "totals": {"event_count": 0, "category_count": 0}, "rows": []}

    def operational_diagnostics(
        self,
        *,
        section: str = "all",
        limit: int = 25,
        root_name: str | None = None,
        status: str | None = None,
        family: str | None = None,
        since_hours: int | None = None,
        include_details: bool = False,
    ) -> dict[str, Any]:
        normalized = str(section or "all").strip().lower().replace("-", "_")
        if normalized not in {"all", "retrieval", "watcher", "workers", "jobs", "mail"}:
            raise ValueError("diagnostics section must be all, retrieval, watcher, workers, jobs, or mail")
        capped = max(1, min(int(limit or 25), 100))
        retrieval = {"recent_explains": database.recent_retrieval_explain_diagnostics(limit=capped)} if normalized in {"all", "retrieval"} else {}
        watcher = {"events": database.list_watch_events(limit=capped)} if normalized in {"all", "watcher"} else {}
        workers = {"families": database.worker_family_stats()} if normalized in {"all", "workers"} else {}
        jobs = {"jobs": database.list_capture_jobs(limit=capped)} if normalized in {"all", "jobs"} else {}
        mail = (
            {
                "sync_runs": database.list_mail_sync_runs(limit=capped),
                "post_process_events": database.list_mail_post_process_events(limit=capped),
            }
            if normalized in {"all", "mail"}
            else {}
        )
        return summarize_operational_diagnostics(
            retrieval=retrieval,
            watcher=watcher,
            workers=workers,
            jobs=jobs,
            mail=mail,
            section=normalized,
            root_name=root_name,
            status=status,
            family=family,
            since_hours=since_hours,
            include_details=include_details,
        )

    def run_benchmark(
        self,
        *,
        fixture: str = "all",
        files: int = 10,
        mode: str = "scan",
        passes: int = 1,
        label: str | None = None,
        compare_label: str | None = None,
        workers: int = 1,
        family: str = "all",
        scope: str = "synthetic",
        root_name: str | None = None,
        path: str | None = None,
        max_files: int | None = None,
        deployment_label: str | None = None,
        scenario: str = "standard",
        include_model_probe: bool = False,
    ) -> dict[str, Any]:
        normalized_scenario = normalize_benchmark_scenario(scenario)
        scope_descriptor = _benchmark_scope_descriptor(scope=scope, root_name=root_name, path=path)
        if normalized_scenario == "host_cloud" and scope_descriptor["scope_type"] == "synthetic":
            raise ValueError("host_cloud benchmark scenario requires root or path benchmark scope")
        fixture_names = [item["name"] for item in BENCHMARK_FIXTURES]
        requested = str(fixture or "all")
        real_scope = scope_descriptor["scope_type"] != "synthetic"
        names = [scope_descriptor["fixture"]] if real_scope and requested == "all" else (fixture_names if requested == "all" else [requested])
        unknown = [name for name in names if name not in fixture_names]
        if unknown and not real_scope:
            raise ValueError(f"unknown benchmark fixture: {unknown[0]}")
        normalized_mode = _normalize_benchmark_mode(mode, allow_all=True)
        modes = ["scan", "soak", "watcher"] if normalized_mode == "all" else [normalized_mode]
        if normalized_mode == "all" and include_model_probe:
            modes.append("model")
        normalized_family = _normalize_benchmark_family(family)
        file_count = max(1, min(int(files or 10), 500))
        real_file_count = max(1, min(int(max_files or file_count), 10_000))
        pass_count = max(1, min(int(passes or 1), 10))
        worker_count = max(1, min(int(workers or 1), 32))
        runs: list[dict[str, Any]] = []
        for name in names:
            for selected_mode in modes:
                if selected_mode == "scan":
                    if real_scope:
                        runs.extend(
                            self._run_real_scope_scan_benchmark(
                                scope_descriptor,
                                real_file_count,
                                passes=pass_count,
                                label=label,
                                compare_label=compare_label,
                                worker_count=worker_count,
                                deployment_label=deployment_label,
                                scenario=normalized_scenario,
                            )
                        )
                    else:
                        runs.extend(
                            self._run_scan_benchmark(
                                name,
                                file_count,
                                passes=pass_count,
                                label=label,
                                compare_label=compare_label,
                                worker_count=worker_count,
                                scope_descriptor=scope_descriptor,
                                deployment_label=deployment_label,
                                scenario=normalized_scenario,
                            )
                        )
                elif selected_mode == "soak":
                    runs.append(
                        self._run_soak_benchmark(
                            name,
                            file_count,
                            label=label,
                            compare_label=compare_label,
                            worker_count=worker_count,
                            family=normalized_family,
                            scope_descriptor=scope_descriptor,
                            deployment_label=deployment_label,
                            scenario=normalized_scenario,
                        )
                    )
                elif selected_mode == "watcher":
                    runs.append(
                        self._run_watcher_benchmark(
                            name,
                            file_count,
                            label=label,
                            compare_label=compare_label,
                            worker_count=worker_count,
                            scope_descriptor=scope_descriptor,
                            deployment_label=deployment_label,
                            scenario=normalized_scenario,
                        )
                    )
                elif selected_mode == "model":
                    runs.extend(
                        self._run_model_benchmark(
                            name,
                            passes=pass_count,
                            label=label,
                            compare_label=compare_label,
                            worker_count=worker_count,
                            scope_descriptor=scope_descriptor,
                            deployment_label=deployment_label,
                            scenario=normalized_scenario,
                        )
                    )
        settings_snapshot = _benchmark_settings_snapshot()
        model_telemetry = _benchmark_model_telemetry() if normalized_scenario == "cache_readiness" else _latest_model_telemetry(runs)
        acceleration_status = collect_acceleration_status() if normalized_scenario == "cache_readiness" else {}
        scenario_evidence = _benchmark_scenario_evidence(normalized_scenario)
        diagnostics = build_indexer_diagnostics(
            scenario=normalized_scenario,
            runs=runs,
            scope_descriptor=scope_descriptor,
            settings_snapshot=settings_snapshot,
            acceleration_status=acceleration_status,
            model_telemetry=model_telemetry,
            lock_retry_cooldown_seconds=_configured_lock_retry_cooldown_seconds(),
            lock_max_attempts=_configured_lock_max_attempts(),
            scenario_evidence=scenario_evidence,
        )
        recommendations = build_benchmark_recommendations(
            scenario=normalized_scenario,
            runs=runs,
            settings_snapshot=settings_snapshot,
        )
        return {
            "fixture": requested if requested != "all" else "all",
            "mode": normalized_mode,
            "scenario": normalized_scenario,
            "scope_type": scope_descriptor["scope_type"],
            "files": file_count,
            "runs": runs,
            "diagnostics": diagnostics,
            "recommendations": recommendations,
        }

    def _run_scan_benchmark(
        self,
        fixture: str,
        files: int,
        *,
        passes: int,
        label: str | None,
        compare_label: str | None,
        worker_count: int,
        scope_descriptor: dict[str, Any],
        deployment_label: str | None,
        scenario: str,
    ) -> list[dict[str, Any]]:
        runs: list[dict[str, Any]] = []
        with tempfile.TemporaryDirectory(prefix="flux-kb-benchmark-") as temp_dir:
            root = Path(temp_dir)
            _write_benchmark_fixture(root, fixture, files)
            manifest: dict[str, dict[str, Any]] = {}
            hash_parallelism = _configured_hash_parallelism()
            for pass_index in range(1, passes + 1):
                started = time.perf_counter()
                plan = scan_path(
                    root,
                    CorpusPolicy(
                        root_path=root,
                        hash_parallelism=hash_parallelism,
                        manifest_lookup=lambda relative_path, store=manifest: store.get(relative_path),
                    ),
                )
                elapsed_ms = max(0, int((time.perf_counter() - started) * 1000))
                for asset in plan.assets:
                    manifest[asset.relative_path] = {
                        "path": asset.relative_path,
                        "size_bytes": asset.size_bytes,
                        "mtime_ns": asset.mtime_ns,
                        "quick_hash": asset.quick_hash,
                        "content_hash": asset.content_hash,
                    }
                manifest_skipped = sum(1 for asset in plan.assets if asset.metadata.get("manifest_skipped_unchanged"))
                per_file_ms = max(0, int(elapsed_ms / max(1, len(plan.assets))))
                family_breakdown = _benchmark_family_breakdown(plan)
                warm_state = "cold" if pass_index == 1 else "warm"
                metadata = {
                    "provider": "synthetic",
                    "path_scope": "temporary",
                    "watcher_backend": _configured_watcher_backend(),
                    "hash_parallelism": hash_parallelism,
                    "scenario": scenario,
                }
                record_fields = _benchmark_record_fields(
                    scope_descriptor=scope_descriptor,
                    deployment_label=deployment_label,
                    recommendation_metadata=scenario_recommendation_metadata(scenario),
                )
                recorded = database.record_benchmark_run(
                    fixture=fixture,
                    mode="scan",
                    label=label,
                    compare_label=compare_label,
                    file_count=len(plan.assets),
                    elapsed_ms=elapsed_ms,
                    timings_ms=[per_file_ms for _asset in plan.assets],
                    warm_state=warm_state,
                    pass_index=pass_index,
                    hash_parallelism=hash_parallelism,
                    worker_count=worker_count,
                    manifest_skipped_unchanged=manifest_skipped,
                    cache_hits=manifest_skipped,
                    cache_misses=max(0, len(plan.assets) - manifest_skipped),
                    jobs_queued=len(plan.deferred_jobs),
                    jobs_completed=len(plan.assets) - len(plan.deferred_jobs),
                    jobs_blocked=len(plan.errors),
                    worker_family_breakdown=family_breakdown,
                    metadata=metadata,
                    **record_fields,
                )
                runs.append(
                    _benchmark_run_payload(
                        recorded=recorded,
                        fixture=fixture,
                        mode="scan",
                        file_count=len(plan.assets),
                        elapsed_ms=elapsed_ms,
                        jobs_queued=len(plan.deferred_jobs),
                        jobs_completed=len(plan.assets) - len(plan.deferred_jobs),
                        jobs_blocked=len(plan.errors),
                        worker_family_breakdown=family_breakdown,
                        warm_state=warm_state,
                        pass_index=pass_index,
                        hash_parallelism=hash_parallelism,
                        worker_count=worker_count,
                        manifest_skipped_unchanged=manifest_skipped,
                        cache_hits=manifest_skipped,
                        cache_misses=max(0, len(plan.assets) - manifest_skipped),
                        metadata=metadata,
                        **record_fields,
                    )
                )
        return runs

    def _run_real_scope_scan_benchmark(
        self,
        scope_descriptor: dict[str, Any],
        max_files: int,
        *,
        passes: int,
        label: str | None,
        compare_label: str | None,
        worker_count: int,
        deployment_label: str | None,
        scenario: str,
    ) -> list[dict[str, Any]]:
        root = scope_descriptor["root"]
        target_path = scope_descriptor.get("path")
        runs: list[dict[str, Any]] = []
        hash_parallelism = _configured_hash_parallelism()
        manifest: dict[str, dict[str, Any]] = {}
        for pass_index in range(1, passes + 1):
            started = time.perf_counter()
            plan = scan_path(
                root["root_path"],
                CorpusPolicy(
                    root_path=Path(root["root_path"]),
                    recursive=root["recursive"],
                    include_globs=tuple(root.get("include_globs") or ()),
                    exclude_globs=tuple(root.get("exclude_globs") or ()),
                    strict_indexing=strict_indexing_enabled(root.get("metadata") if isinstance(root.get("metadata"), dict) else {}),
                    max_inline_bytes=root["max_inline_bytes"],
                    heavy_threshold_bytes=root["heavy_threshold_bytes"],
                    **_configured_container_limits(),
                    hash_parallelism=hash_parallelism,
                    manifest_lookup=lambda relative_path, store=manifest: store.get(relative_path),
                    mail_spool=bool((root.get("metadata") or {}).get("mail_profile")) if isinstance(root.get("metadata"), dict) else False,
                ),
                target_path=target_path,
            )
            elapsed_ms = max(0, int((time.perf_counter() - started) * 1000))
            selected_assets = plan.assets[:max_files]
            for asset in selected_assets:
                manifest[asset.relative_path] = {
                    "path": asset.relative_path,
                    "size_bytes": asset.size_bytes,
                    "mtime_ns": asset.mtime_ns,
                    "quick_hash": asset.quick_hash,
                    "content_hash": asset.content_hash,
                }
            manifest_skipped = sum(1 for asset in selected_assets if asset.metadata.get("manifest_skipped_unchanged"))
            file_count = len(selected_assets)
            per_file_ms = max(0, int(elapsed_ms / max(1, file_count)))
            family_breakdown = _benchmark_family_breakdown_for_assets(selected_assets)
            warm_state = "cold" if pass_index == 1 else "warm"
            metadata = {
                "provider": "real_root",
                "path_scope": scope_descriptor["scope_type"],
                "scope_label": scope_descriptor.get("scope_label"),
                "host_access": scope_descriptor.get("host_access"),
                "watcher_backend": _configured_watcher_backend(),
                "hash_parallelism": hash_parallelism,
                "max_files": max_files,
                "observed_files": len(plan.assets),
                "errors": len(plan.errors),
                "scenario": scenario,
            }
            record_fields = _benchmark_record_fields(
                scope_descriptor=scope_descriptor,
                deployment_label=deployment_label,
                recommendation_metadata=scenario_recommendation_metadata(scenario),
            )
            recorded = database.record_benchmark_run(
                fixture=scope_descriptor["fixture"],
                mode="scan",
                label=label,
                compare_label=compare_label,
                file_count=file_count,
                elapsed_ms=elapsed_ms,
                timings_ms=[per_file_ms for _asset in selected_assets],
                warm_state=warm_state,
                pass_index=pass_index,
                hash_parallelism=hash_parallelism,
                worker_count=worker_count,
                manifest_skipped_unchanged=manifest_skipped,
                cache_hits=manifest_skipped,
                cache_misses=max(0, file_count - manifest_skipped),
                jobs_queued=len(plan.deferred_jobs),
                jobs_completed=max(0, file_count - len(plan.deferred_jobs)),
                jobs_blocked=len(plan.errors),
                worker_family_breakdown=family_breakdown,
                metadata=metadata,
                **record_fields,
            )
            runs.append(
                _benchmark_run_payload(
                    recorded=recorded,
                    fixture=scope_descriptor["fixture"],
                    mode="scan",
                    file_count=file_count,
                    elapsed_ms=elapsed_ms,
                    jobs_queued=len(plan.deferred_jobs),
                    jobs_completed=max(0, file_count - len(plan.deferred_jobs)),
                    jobs_blocked=len(plan.errors),
                    worker_family_breakdown=family_breakdown,
                    warm_state=warm_state,
                    pass_index=pass_index,
                    hash_parallelism=hash_parallelism,
                    worker_count=worker_count,
                    manifest_skipped_unchanged=manifest_skipped,
                    cache_hits=manifest_skipped,
                    cache_misses=max(0, file_count - manifest_skipped),
                    metadata=metadata,
                    **record_fields,
                )
            )
        return runs

    def _run_soak_benchmark(
        self,
        fixture: str,
        files: int,
        *,
        label: str | None,
        compare_label: str | None,
        worker_count: int,
        family: str,
        scope_descriptor: dict[str, Any],
        deployment_label: str | None,
        scenario: str,
    ) -> dict[str, Any]:
        tag = hashlib.sha256(f"{fixture}:{label or ''}:{time.time_ns()}".encode("utf-8")).hexdigest()[:16]
        family_caps = _configured_worker_caps()
        normalized_family = str(family or "all").lower()
        family_filter = None if normalized_family == "all" else [normalized_family]
        created = database.create_benchmark_soak_jobs(
            tag=tag,
            fixture=fixture,
            file_count=files,
            family=normalized_family,
            label=label,
        )
        completed = 0
        blocked = 0
        timings: list[int] = []
        family_breakdown: dict[str, dict[str, int]] = {}
        started = time.perf_counter()
        try:
            claimed = database.claim_corpus_jobs(
                limit=files,
                worker_id=f"flux-kb-benchmark-{tag}",
                job_families=family_filter,
                family_caps=family_caps,
            )
            for index, job in enumerate(claimed):
                duration_ms = max(1, index + 1)
                timings.append(duration_ms)
                job_family = str(job.get("job_family") or "general")
                row = family_breakdown.setdefault(job_family, {"claimed": 0, "completed": 0, "blocked": 0})
                row["claimed"] += 1
                telemetry = {
                    "benchmark_mode": "soak",
                    "benchmark_tag": tag,
                    "benchmark_fixture": fixture,
                    "benchmark_file_count": files,
                    "job_family": job_family,
                    "resource_class": job.get("resource_class"),
                }
                payload = job.get("payload") if isinstance(job.get("payload"), dict) else {}
                if payload.get("benchmark_outcome") == "blocked":
                    database.block_corpus_job(
                        job_id=job["id"],
                        error="benchmark synthetic blocked",
                        status="blocked_benchmark",
                        duration_ms=duration_ms,
                        telemetry=telemetry,
                    )
                    row["blocked"] += 1
                    blocked += 1
                else:
                    database.complete_corpus_job(job_id=job["id"], duration_ms=duration_ms, telemetry=telemetry)
                    row["completed"] += 1
                    completed += 1
        finally:
            purged = database.purge_benchmark_soak_jobs(tag=tag)
        elapsed_ms = max(0, int((time.perf_counter() - started) * 1000))
        metadata = {
            "provider": "synthetic",
            "path_scope": "temporary",
            "benchmark_tag": tag,
            "family": normalized_family,
            "worker_caps": family_caps,
            "purged": purged.get("purged"),
            "scenario": scenario,
        }
        record_fields = _benchmark_record_fields(
            scope_descriptor=scope_descriptor,
            deployment_label=deployment_label,
            recommendation_metadata=scenario_recommendation_metadata(scenario),
        )
        recorded = database.record_benchmark_run(
            fixture=fixture,
            mode="soak",
            label=label,
            compare_label=compare_label,
            file_count=int(created.get("created") or files),
            elapsed_ms=elapsed_ms,
            timings_ms=timings,
            warm_state="warm",
            pass_index=1,
            hash_parallelism=_configured_hash_parallelism(),
            worker_count=worker_count,
            cache_hits=0,
            cache_misses=int(created.get("created") or files),
            jobs_queued=int(created.get("created") or files),
            jobs_completed=completed,
            jobs_blocked=blocked,
            worker_family_breakdown=family_breakdown,
            metadata=metadata,
            **record_fields,
        )
        return _benchmark_run_payload(
            recorded=recorded,
            fixture=fixture,
            mode="soak",
            file_count=int(created.get("created") or files),
            elapsed_ms=elapsed_ms,
            jobs_queued=int(created.get("created") or files),
            jobs_completed=completed,
            jobs_blocked=blocked,
            worker_family_breakdown=family_breakdown,
            warm_state="warm",
            pass_index=1,
            hash_parallelism=_configured_hash_parallelism(),
            worker_count=worker_count,
            cache_hits=0,
            cache_misses=int(created.get("created") or files),
            metadata=metadata,
            **record_fields,
        )

    def _run_watcher_benchmark(
        self,
        fixture: str,
        files: int,
        *,
        label: str | None,
        compare_label: str | None,
        worker_count: int,
        scope_descriptor: dict[str, Any],
        deployment_label: str | None,
        scenario: str,
    ) -> dict[str, Any]:
        started = time.perf_counter()
        probe = probe_watcher_backend(backend_policy=_configured_watcher_backend(), timeout_seconds=2.0)
        elapsed_ms = int(probe.get("latency_ms") or max(0, int((time.perf_counter() - started) * 1000)))
        metadata = {
            "provider": "synthetic",
            "path_scope": "temporary",
            "watcher_backend": probe.get("backend") or {},
            "watcher_events": probe.get("events") or {},
            "latency_ms": elapsed_ms,
            "scenario": scenario,
        }
        record_fields = _benchmark_record_fields(
            scope_descriptor=scope_descriptor,
            deployment_label=deployment_label,
            recommendation_metadata=scenario_recommendation_metadata(scenario),
        )
        recorded = database.record_benchmark_run(
            fixture=fixture,
            mode="watcher",
            label=label,
            compare_label=compare_label,
            file_count=files,
            elapsed_ms=elapsed_ms,
            timings_ms=[elapsed_ms],
            warm_state="warm",
            pass_index=1,
            hash_parallelism=_configured_hash_parallelism(),
            worker_count=worker_count,
            cache_hits=0,
            cache_misses=0,
            jobs_queued=0,
            jobs_completed=0,
            jobs_blocked=0,
            worker_family_breakdown={},
            metadata=metadata,
            **record_fields,
        )
        return _benchmark_run_payload(
            recorded=recorded,
            fixture=fixture,
            mode="watcher",
            file_count=files,
            elapsed_ms=elapsed_ms,
            jobs_queued=0,
            jobs_completed=0,
            jobs_blocked=0,
            worker_family_breakdown={},
            warm_state="warm",
            pass_index=1,
            hash_parallelism=_configured_hash_parallelism(),
            worker_count=worker_count,
            cache_hits=0,
            cache_misses=0,
            metadata=metadata,
            **record_fields,
        )

    def _run_model_benchmark(
        self,
        fixture: str,
        *,
        passes: int,
        label: str | None,
        compare_label: str | None,
        worker_count: int,
        scope_descriptor: dict[str, Any],
        deployment_label: str | None,
        scenario: str,
    ) -> list[dict[str, Any]]:
        runs: list[dict[str, Any]] = []
        for pass_index in range(1, passes + 1):
            started = time.perf_counter()
            model_telemetry = _benchmark_model_telemetry()
            elapsed_ms = max(0, int((time.perf_counter() - started) * 1000))
            warm_state = "cold" if pass_index == 1 else "warm"
            record_fields = _benchmark_record_fields(
                scope_descriptor=scope_descriptor,
                deployment_label=deployment_label,
                model_telemetry=model_telemetry,
                recommendation_metadata=scenario_recommendation_metadata(scenario),
            )
            metadata = {
                "provider": "local_only",
                "path_scope": "temporary",
                "blocked_dependency_count": model_telemetry.get("blocked_dependency_count", 0),
                "scenario": scenario,
            }
            recorded = database.record_benchmark_run(
                fixture=fixture,
                mode="model",
                label=label,
                compare_label=compare_label,
                file_count=0,
                elapsed_ms=elapsed_ms,
                timings_ms=[elapsed_ms],
                warm_state=warm_state,
                pass_index=pass_index,
                hash_parallelism=_configured_hash_parallelism(),
                worker_count=worker_count,
                cache_hits=0,
                cache_misses=0,
                jobs_queued=0,
                jobs_completed=0,
                jobs_blocked=int(model_telemetry.get("blocked_dependency_count") or 0),
                worker_family_breakdown={},
                metadata=metadata,
                **record_fields,
            )
            runs.append(
                _benchmark_run_payload(
                    recorded=recorded,
                    fixture=fixture,
                    mode="model",
                    file_count=0,
                    elapsed_ms=elapsed_ms,
                    jobs_queued=0,
                    jobs_completed=0,
                    jobs_blocked=int(model_telemetry.get("blocked_dependency_count") or 0),
                    worker_family_breakdown={},
                    warm_state=warm_state,
                    pass_index=pass_index,
                    hash_parallelism=_configured_hash_parallelism(),
                    worker_count=worker_count,
                    cache_hits=0,
                    cache_misses=0,
                    metadata=metadata,
                    **record_fields,
                )
            )
        return runs

    def backfill_episode_workspace_scope(
        self,
        *,
        episode_ids: list[str],
        cwd: str | None,
        root_name: str | None = None,
        dry_run: bool = False,
    ) -> dict[str, Any]:
        metadata_patch = _workspace_metadata(cwd=cwd, root_name=root_name)
        if not metadata_patch.get("workspace_key"):
            raise ValueError("scope-backfill requires a cwd or root_name that can resolve to a workspace")
        return database.backfill_episode_workspace_scope(
            episode_ids=episode_ids,
            metadata_patch=metadata_patch,
            dry_run=dry_run,
        )

    def reconcile_watch_roots(
        self,
        *,
        root_name: str | None = None,
        reason: str = "periodic_reconcile",
        host_agent_roots: bool | None = None,
        component_name: str = "watch-reconciler:service",
    ) -> dict[str, Any]:
        roots = [
            root
            for root in database.list_monitored_roots(watch_enabled=True)
            if root.get("enabled")
            and root.get("watch_enabled")
            and (root_name is None or root.get("name") == root_name)
            and _root_matches_host_agent_filter(root, host_agent_roots)
        ]
        if not roots:
            payload = {"status": "no_enabled_watch_roots", "reason": reason, "roots": 0, "results": []}
            database.record_runtime_component_heartbeat(
                name=component_name,
                status="idle",
                metadata=payload,
            )
            return payload

        results: list[dict[str, Any]] = []
        totals = {"files_seen": 0, "files_changed": 0, "files_deleted": 0, "jobs_queued": 0}
        for root in roots:
            try:
                result = self.sync_corpus(root_name=root["name"], reason=reason)
                results.append(result)
                for key in totals:
                    totals[key] += int(result.get(key) or 0)
            except Exception as exc:
                error = {"root_name": root.get("name"), "status": "error", "error": str(exc)}
                results.append(error)
                database.record_watch_error(root_name=root["name"], error=str(exc))
        status = "completed" if all(item.get("status") != "error" for item in results) else "partial"
        payload = {"status": status, "reason": reason, "roots": len(roots), **totals, "results": results}
        database.record_runtime_component_heartbeat(
            name=component_name,
            status="running" if status == "completed" else "error",
            metadata=payload,
        )
        return payload

    def run_watch(self, *, root_name: str | None = None, interval_seconds: float = 2.0) -> dict[str, Any]:
        if not _load_watch_roots(root_name):
            return {"status": "no_enabled_roots", "root_name": root_name}

        watcher = create_corpus_watcher(
            lambda: _load_watch_roots(root_name),
            on_change=None,
            interval_seconds=interval_seconds,
            debounce_seconds=_configured_watcher_debounce_seconds(),
            stability_quiet_seconds=_configured_stability_quiet_seconds(),
            max_queue_size=_configured_watcher_max_queue_size(),
            backend_policy=_configured_watcher_backend(),
        )
        last_reconcile_at = 0.0
        heartbeat = WatcherHeartbeatRunner(
            load_roots=lambda: _load_watch_roots(root_name),
            record=lambda name, metadata: database.record_watcher_heartbeat(
                root_name=name,
                metadata={
                    "watcher_backend": getattr(watcher, "backend_status", None),
                    **metadata,
                },
            ),
            interval_seconds=WATCHER_HEARTBEAT_INTERVAL_SECONDS,
        )
        heartbeat.start()
        try:
            if _configured_reconcile_on_start():
                heartbeat.update(stage="startup_reconcile", busy=True)
                self.reconcile_watch_roots(root_name=root_name, reason="startup_reconcile")
                last_reconcile_at = time.monotonic()
            heartbeat.update(stage="seed", busy=True)
            watcher.poll_once(seed=True)
            while True:
                started = time.perf_counter()
                heartbeat.update(stage="poll", busy=True)
                polled_events = watcher.poll_once()
                events = _drained_watch_events(watcher, polled_events)
                for event in events:
                    self._handle_watch_event(event)
                loop_duration_ms = max(0, int((time.perf_counter() - started) * 1000))
                heartbeat.update(
                    stage="idle",
                    busy=False,
                    last_loop_duration_ms=loop_duration_ms,
                    last_event_count=len(events),
                    queue_depth=_watcher_queue_depth(watcher),
                )
                reconcile_interval = _configured_reconcile_interval_seconds()
                if reconcile_interval > 0 and time.monotonic() - last_reconcile_at >= reconcile_interval:
                    heartbeat.update(stage="periodic_reconcile", busy=True)
                    self.reconcile_watch_roots(root_name=root_name, reason="periodic_reconcile")
                    last_reconcile_at = time.monotonic()
                    heartbeat.update(stage="idle", busy=False)
                time.sleep(interval_seconds)
        finally:
            heartbeat.stop()

    def run_corpus_backfill(
        self,
        *,
        kind: str = "all",
        limit: int | None = None,
        workers: int | None = None,
        root_name: str | None = None,
        host_agent_roots: bool | None = None,
        family: str | None = None,
        worker_id: str | None = None,
    ) -> dict[str, Any]:
        effective_limit = _resolved_worker_batch_size(limit)
        effective_workers = _resolved_worker_count(workers)
        effective_worker_id = worker_id or _new_worker_instance_id(f"flux-kb-backfill-{effective_workers}")
        _record_worker_instance_heartbeat(
            worker_id=effective_worker_id,
            parent_component=None if worker_id else f"flux-kb-backfill-{effective_workers}",
            metadata={
                "kind": family or kind,
                "limit": effective_limit,
                "workers": effective_workers,
                "root_name": root_name,
                "host_agent_roots": host_agent_roots,
            },
        )
        stale_recovery = {"root_name": root_name, "recovered": 0}
        try:
            stale_recovery = database.recover_stale_running_corpus_jobs(root_name=root_name)
        except Exception as exc:
            stale_recovery = {"root_name": root_name, "recovered": 0, "error": str(exc)}
        try:
            purged_unseen = database.purge_unseen_corpus_assets(
                root_name=root_name,
                grace_seconds=_configured_unseen_asset_purge_grace_seconds(),
                batch_size=_configured_unseen_asset_purge_batch_size(),
            )
        except Exception as exc:
            purged_unseen = {"root_name": root_name, "assets_purged": 0, "error": str(exc)}
        cancelled = database.cancel_duplicate_corpus_jobs(root_name=root_name)
        effective_kind = family or kind
        job_families = kind_to_job_families(effective_kind)
        claim_kwargs: dict[str, Any] = {
            "limit": effective_limit,
            "worker_id": effective_worker_id,
            "root_name": root_name,
        }
        if job_families is not None:
            claim_kwargs["job_families"] = list(job_families)
        if host_agent_roots is not None:
            claim_kwargs["host_agent_roots"] = host_agent_roots
        claim_kwargs["family_caps"] = _configured_worker_caps()
        claimed = database.claim_corpus_jobs(**claim_kwargs)
        completed = 0
        blocked = 0
        retried = 0
        failed = 0
        cancelled_orphaned = 0
        cancelled_missing_source = 0
        cancelled_unseen_asset = 0
        for job, duration_ms, process_result in self._process_claimed_corpus_jobs(claimed, workers=effective_workers):
            telemetry = {
                "job_family": job.get("job_family"),
                "resource_class": job.get("resource_class"),
                "result_status": process_result.status,
            }
            telemetry.update(process_result.telemetry or {})
            if process_result.status in {"indexed", "metadata_only", "staged"}:
                database.complete_corpus_job(job_id=job["id"], duration_ms=duration_ms, telemetry=telemetry)
                completed += 1
            elif process_result.status == "cancelled_orphaned_root":
                database.cancel_orphaned_corpus_job(
                    job_id=job["id"],
                    error=process_result.message or "monitored root not found",
                    duration_ms=duration_ms,
                    telemetry=telemetry,
                )
                cancelled_orphaned += 1
            elif process_result.status == "cancelled_missing_source":
                payload = job.get("payload") or {}
                database.cancel_missing_source_corpus_job(
                    job_id=job["id"],
                    root_name=str(payload.get("root_name") or ""),
                    relative_path=str(payload.get("path") or ""),
                    error=process_result.message or "source file not found",
                    duration_ms=duration_ms,
                    telemetry=telemetry,
                )
                cancelled_missing_source += 1
            elif process_result.status == "cancelled_unseen_asset":
                database.cancel_unseen_corpus_job(
                    job_id=job["id"],
                    error=process_result.message or "cancelled_unseen_asset",
                    duration_ms=duration_ms,
                    telemetry=telemetry,
                )
                cancelled_unseen_asset += 1
            elif process_result.status in {"blocked_missing_dependency", "blocked_by_policy", "blocked_invalid_source"}:
                kwargs: dict[str, Any] = {}
                if process_result.status != "blocked_missing_dependency":
                    kwargs["status"] = process_result.status
                database.block_corpus_job(
                    job_id=job["id"],
                    error=process_result.message or process_result.status,
                    duration_ms=duration_ms,
                    telemetry=telemetry,
                    **kwargs,
                )
                blocked += 1
            elif process_result.status == "retrying_locked":
                if int(job.get("attempts") or 0) >= _configured_lock_max_attempts():
                    database.block_corpus_job(
                        job_id=job["id"],
                        error=process_result.message or "blocked_locked",
                        status="blocked_locked",
                        duration_ms=duration_ms,
                        telemetry=telemetry,
                    )
                    blocked += 1
                else:
                    database.retry_corpus_job(
                        job_id=job["id"],
                        error=process_result.message or "retrying_locked",
                        cooldown_seconds=_configured_lock_retry_cooldown_seconds(),
                        status="retrying_locked",
                        duration_ms=duration_ms,
                        telemetry=telemetry,
                    )
                    retried += 1
            elif process_result.status == "retrying_vss_failed":
                if int(job.get("attempts") or 0) >= _configured_lock_max_attempts():
                    database.block_corpus_job(
                        job_id=job["id"],
                        error=process_result.message or "blocked_vss_failed",
                        status="blocked_vss_failed",
                        duration_ms=duration_ms,
                        telemetry=telemetry,
                    )
                    blocked += 1
                else:
                    database.retry_corpus_job(
                        job_id=job["id"],
                        error=process_result.message or "retrying_vss_failed",
                        cooldown_seconds=_configured_lock_retry_cooldown_seconds(),
                        status="retrying_vss_failed",
                        duration_ms=duration_ms,
                        telemetry=telemetry,
                    )
                    retried += 1
            elif process_result.status == "retrying_gpu_busy":
                retry_after = int(float((process_result.telemetry or {}).get("retry_after_seconds") or 1))
                database.retry_corpus_job(
                    job_id=job["id"],
                    error=process_result.message or "retrying_gpu_busy",
                    cooldown_seconds=max(1, retry_after),
                    status="retrying_gpu_busy",
                    duration_ms=duration_ms,
                    telemetry=telemetry,
                )
                retried += 1
            elif process_result.status == "failed":
                if int(job.get("attempts") or 0) >= _configured_failure_max_attempts():
                    database.block_corpus_job(
                        job_id=job["id"],
                        error=process_result.message or process_result.status,
                        status="failed",
                        duration_ms=duration_ms,
                        telemetry=telemetry,
                    )
                    failed += 1
                else:
                    database.retry_corpus_job(
                        job_id=job["id"],
                        error=process_result.message or process_result.status,
                        cooldown_seconds=_configured_retry_cooldown_seconds(),
                        duration_ms=duration_ms,
                        telemetry=telemetry,
                    )
                    retried += 1
            else:
                database.retry_corpus_job(
                    job_id=job["id"],
                    error=process_result.message or process_result.status,
                    cooldown_seconds=_configured_retry_cooldown_seconds(),
                    duration_ms=duration_ms,
                    telemetry=telemetry,
                )
                retried += 1
        repaired = database.repair_extracted_corpus_asset_statuses(root_name=root_name)
        cleared_errors = database.clear_completed_corpus_job_errors(root_name=root_name)
        capture_job_purge = self._purge_expired_capture_jobs()
        database.record_audit_event(
            event_type="corpus.backfill",
            details={
                "kind": effective_kind,
                "job_families": list(job_families) if job_families else None,
                "root_name": root_name,
                "host_agent_roots": host_agent_roots,
                "worker_id": effective_worker_id,
                "claimed": len(claimed),
                "completed": completed,
                "blocked": blocked,
                "retried": retried,
                "failed": failed,
                "cancelled_orphaned": cancelled_orphaned,
                "cancelled_missing_source": cancelled_missing_source,
                "cancelled_unseen_asset": cancelled_unseen_asset,
                "recovered_stale_running": stale_recovery.get("recovered", 0),
                "purged_unseen_assets": purged_unseen.get("assets_purged", 0),
                "cancelled_duplicate": cancelled["cancelled"],
                "repaired_assets": repaired["repaired"],
                "cleared_completed_errors": cleared_errors["cleared"],
                "purged_capture_jobs": capture_job_purge.get("purged", 0),
                "capture_job_retention_days": capture_job_purge.get("retention_days", 7),
                "capture_job_purge_error": capture_job_purge.get("error_type"),
                "workers": effective_workers,
            },
        )
        return {
            "kind": effective_kind,
            "job_families": list(job_families) if job_families else None,
            "root_name": root_name,
            "host_agent_roots": host_agent_roots,
            "worker_id": effective_worker_id,
            "claimed": len(claimed),
            "completed": completed,
            "blocked": blocked,
            "retried": retried,
            "failed": failed,
            "cancelled_orphaned": cancelled_orphaned,
            "cancelled_missing_source": cancelled_missing_source,
            "cancelled_unseen_asset": cancelled_unseen_asset,
            "recovered_stale_running": stale_recovery.get("recovered", 0),
            "purged_unseen_assets": purged_unseen.get("assets_purged", 0),
            "cancelled_duplicate": cancelled["cancelled"],
            "repaired_assets": repaired["repaired"],
            "cleared_completed_errors": cleared_errors["cleared"],
            "purged_capture_jobs": capture_job_purge.get("purged", 0),
            "capture_job_retention_days": capture_job_purge.get("retention_days", 7),
            "capture_job_purge_error": capture_job_purge.get("error_type"),
            "jobs": claimed,
        }

    def _process_claimed_corpus_jobs(self, claimed: list[dict[str, Any]], *, workers: int) -> list[tuple[dict[str, Any], int, Any]]:
        bounded_workers = max(1, min(int(workers or 1), len(claimed) or 1))
        if bounded_workers <= 1 or len(claimed) <= 1:
            return [self._process_claimed_corpus_job(job) for job in claimed]

        indexed_jobs = list(enumerate(claimed))
        results: list[tuple[int, tuple[dict[str, Any], int, Any]]] = []
        with ThreadPoolExecutor(max_workers=bounded_workers, thread_name_prefix="flux-corpus-worker") as executor:
            futures = {executor.submit(self._process_claimed_corpus_job, job): index for index, job in indexed_jobs}
            for future in as_completed(futures):
                results.append((futures[future], future.result()))
        return [result for _, result in sorted(results, key=lambda item: item[0])]

    def _process_claimed_corpus_job(self, job: dict[str, Any]) -> tuple[dict[str, Any], int, Any]:
        from . import processes
        from . import worker

        started = time.perf_counter()
        try:
            with processes.capture_job_tool_invocations(str(job.get("id") or "")):
                if job.get("job_type") == "corpus_sync_root":
                    process_result = self._process_corpus_sync_job(job)
                elif job.get("job_type") == "search_index_sync":
                    process_result = worker.process_search_index_sync_job(job)
                else:
                    process_result = worker.process_corpus_job(job)
        except Exception as exc:
            process_result = worker.JobProcessResult(
                status="failed",
                message=str(exc),
                telemetry={"error_type": exc.__class__.__name__},
            )
        duration_ms = max(0, int((time.perf_counter() - started) * 1000))
        return job, duration_ms, process_result

    def _purge_expired_capture_jobs(self) -> dict[str, Any]:
        try:
            return database.purge_expired_capture_jobs(retention_days=7)
        except Exception as exc:
            return {"purged": 0, "retention_days": 7, "error_type": exc.__class__.__name__}

    def _process_corpus_sync_job(self, job: dict[str, Any]):
        from .worker import JobProcessResult

        payload = job.get("payload") or {}
        root_name = str(payload.get("root_name") or "").strip()
        if not root_name:
            return JobProcessResult(status="failed", message="corpus_sync_root payload requires root_name")
        reason = str(payload.get("reason") or "background_sync")
        path = payload.get("path") or None
        raw_paths = payload.get("paths")
        paths = []
        if isinstance(raw_paths, list):
            paths.extend(str(item).strip() for item in raw_paths if str(item).strip())
        if path:
            paths.append(str(path))
        paths = list(dict.fromkeys(paths))
        job_id = str(job.get("id") or "")

        def progress_int(value: Any, default: int = 0) -> int:
            try:
                if value is None:
                    return default
                return int(value)
            except (TypeError, ValueError):
                return default

        def progress_label(telemetry: dict[str, Any]) -> str:
            parts: list[str] = []
            paths_total = progress_int(telemetry.get("paths_total"))
            if paths_total:
                parts.append(f"Paths {progress_int(telemetry.get('paths_done'))}/{paths_total}")
            stage = str(telemetry.get("stage") or "running")
            stage_index = progress_int(telemetry.get("stage_index"))
            stage_total = progress_int(telemetry.get("stage_total"))
            if stage_index and stage_total:
                parts.append(f"stage {stage_index}/{stage_total} {stage}")
            else:
                parts.append(stage)
            files_total = progress_int(telemetry.get("files_total"))
            if files_total:
                parts.append(f"files {progress_int(telemetry.get('files_done'), progress_int(telemetry.get('files_seen')))}/{files_total}")
            return ", ".join(parts)

        def normalize_progress(progress: dict[str, Any]) -> dict[str, Any]:
            telemetry = {"root_name": root_name, "reason": reason}
            telemetry.update(progress)
            telemetry["stage"] = str(telemetry.get("stage") or "running")
            if "path" in telemetry and "current_path" not in telemetry:
                telemetry["current_path"] = telemetry["path"]
            if len(paths) and "paths_total" not in telemetry:
                telemetry["paths_total"] = len(paths)
            if "path_index" in telemetry and "paths_done" not in telemetry:
                telemetry["paths_done"] = progress_int(telemetry.get("path_index"))
            if "files_done" not in telemetry and "files_seen" in telemetry:
                telemetry["files_done"] = progress_int(telemetry.get("files_seen"))
            if len(paths):
                if "batch_paths_total" not in telemetry:
                    telemetry["batch_paths_total"] = len(paths)
                if "batch_paths_done" not in telemetry and "paths_done" in telemetry:
                    telemetry["batch_paths_done"] = progress_int(telemetry.get("paths_done"))
            paths_total = progress_int(telemetry.get("paths_total"))
            if paths_total and "path_index" in telemetry:
                completed_before = max(0, min(progress_int(telemetry.get("path_index")) - 1, paths_total))
                path_fraction = max(0.0, min(float(progress_int(telemetry.get("progress_percent"))) / 100.0, 1.0))
                if telemetry["stage"] == "path_completed":
                    path_fraction = 1.0
                telemetry["progress_percent"] = min(100, int(((completed_before + path_fraction) / paths_total) * 100))
            elif "progress_percent" not in telemetry:
                telemetry["progress_percent"] = 0 if telemetry["stage"] == "starting" else None
            if paths_total:
                telemetry["progress_label"] = progress_label(telemetry)
            elif not telemetry.get("progress_label"):
                telemetry["progress_label"] = progress_label(telemetry)
            return {key: value for key, value in telemetry.items() if value is not None}

        database.update_corpus_job_progress(
            job_id=job_id,
            telemetry=normalize_progress(
                {
                    "stage": "starting",
                    "stage_index": 0,
                    "stage_total": 6,
                    "paths_done": 0,
                    "paths_total": len(paths),
                    "progress_percent": 0,
                }
            ),
        )

        stop_heartbeat = threading.Event()
        progress_lock = threading.Lock()
        latest_progress: dict[str, Any] = normalize_progress({"stage": "running", "paths_total": len(paths)})
        last_progress_at = 0.0

        def report_progress(progress: dict[str, Any]) -> None:
            nonlocal last_progress_at
            telemetry = normalize_progress(progress)
            with progress_lock:
                latest_progress.clear()
                latest_progress.update(telemetry)
            now = time.monotonic()
            if now - last_progress_at < 5.0 and telemetry["stage"] not in {"enumerated", "discovered"}:
                return
            last_progress_at = now
            database.update_corpus_job_progress(job_id=job_id, telemetry=telemetry)

        def heartbeat() -> None:
            while not stop_heartbeat.wait(15.0):
                try:
                    with progress_lock:
                        telemetry = dict(latest_progress)
                    telemetry.setdefault("stage", "running")
                    telemetry["root_name"] = root_name
                    telemetry["reason"] = reason
                    database.heartbeat_corpus_job(job_id=job_id, telemetry=telemetry)
                except Exception:
                    pass

        thread = threading.Thread(target=heartbeat, name=f"corpus-sync-heartbeat:{root_name}", daemon=True)
        thread.start()
        try:
            if paths:
                result = {
                    "root_name": root_name,
                    "files_seen": 0,
                    "files_changed": 0,
                    "files_deleted": 0,
                    "jobs_queued": 0,
                    "chunks_indexed": 0,
                    "manifest_skipped_unchanged": 0,
                    "paths_total": len(paths),
                    "batch_paths_total": len(paths),
                    "batch_paths_done": 0,
                    "batch_manifest_loaded_once": True,
                }
                root = _select_root(root_name=root_name, path=None)
                glob_policy = _configured_glob_policy(root)
                manifest = _manifest_store(root["name"])
                container_limits = _configured_container_limits()
                hash_parallelism = _configured_hash_parallelism()
                for index, sync_path in enumerate(paths, start=1):
                    report_progress(
                        {
                            "stage": "path_sync",
                            "current_path": sync_path,
                            "path_index": index,
                            "paths_done": index - 1,
                            "paths_total": len(paths),
                            "batch_paths_done": index - 1,
                            "batch_paths_total": len(paths),
                            "batch_manifest_loaded_once": True,
                            "manifest_skipped_unchanged": int(result.get("manifest_skipped_unchanged") or 0),
                        }
                    )

                    def path_progress(progress: dict[str, Any], *, current_path: str = sync_path, current_index: int = index) -> None:
                        progress_payload = dict(progress)
                        progress_payload["current_path"] = current_path
                        progress_payload["path"] = current_path
                        progress_payload["path_index"] = current_index
                        progress_payload["paths_done"] = current_index
                        progress_payload["paths_total"] = len(paths)
                        progress_payload["batch_paths_done"] = current_index - 1
                        progress_payload["batch_paths_total"] = len(paths)
                        progress_payload["batch_manifest_loaded_once"] = True
                        progress_payload["manifest_skipped_unchanged"] = int(result.get("manifest_skipped_unchanged") or 0)
                        report_progress(progress_payload)

                    path_result = self._sync_corpus_selected_root(
                        root=root,
                        path=sync_path,
                        dry_run=False,
                        reason=reason,
                        progress_callback=path_progress,
                        manifest=manifest,
                        glob_policy=glob_policy,
                        container_limits=container_limits,
                        hash_parallelism=hash_parallelism,
                    )
                    for key in (
                        "files_seen",
                        "files_changed",
                        "files_deleted",
                        "jobs_queued",
                        "chunks_indexed",
                        "manifest_skipped_unchanged",
                    ):
                        result[key] += int(path_result.get(key) or 0)
                    result["batch_paths_done"] = index
                    report_progress(
                        {
                            "stage": "path_completed",
                            "current_path": sync_path,
                            "path_index": index,
                            "paths_done": index,
                            "paths_total": len(paths),
                            "batch_paths_done": index,
                            "batch_paths_total": len(paths),
                            "batch_manifest_loaded_once": True,
                            "files_done": int(path_result.get("files_seen") or 0),
                            "files_total": int(path_result.get("files_seen") or 0),
                            "manifest_skipped_unchanged": int(result.get("manifest_skipped_unchanged") or 0),
                        }
                    )
            else:
                result = self.sync_corpus(
                    root_name=root_name,
                    path=path,
                    dry_run=False,
                    reason=reason,
                    progress_callback=report_progress,
                )
        except ValueError as exc:
            message = str(exc)
            status = "cancelled_orphaned_root" if "monitored root not found" in message else "failed"
            return JobProcessResult(
                status=status,
                message=message,
                telemetry={"stage": "failed", "root_name": root_name, "reason": reason, "error_type": exc.__class__.__name__},
            )
        except Exception as exc:
            return JobProcessResult(
                status="failed",
                message=str(exc),
                telemetry={"stage": "failed", "root_name": root_name, "reason": reason, "error_type": exc.__class__.__name__},
            )
        finally:
            stop_heartbeat.set()
            thread.join(timeout=1.0)

        telemetry = {
            "stage": "completed",
            "stage_index": 6,
            "stage_total": 6,
            "root_name": root_name,
            "reason": reason,
            "files_seen": int(result.get("files_seen") or 0),
            "files_done": int(result.get("files_seen") or 0),
            "files_changed": int(result.get("files_changed") or 0),
            "files_deleted": int(result.get("files_deleted") or 0),
            "jobs_queued": int(result.get("jobs_queued") or 0),
            "chunks_indexed": int(result.get("chunks_indexed") or 0),
            "manifest_skipped_unchanged": int(result.get("manifest_skipped_unchanged") or 0),
            "progress_percent": 100,
        }
        if paths:
            telemetry["paths_total"] = len(paths)
            telemetry["paths_done"] = len(paths)
            telemetry["batch_paths_total"] = len(paths)
            telemetry["batch_paths_done"] = len(paths)
            telemetry["batch_manifest_loaded_once"] = bool(result.get("batch_manifest_loaded_once"))
        telemetry["progress_label"] = progress_label(telemetry)
        return JobProcessResult(status="indexed", telemetry=telemetry)

    def remediate_diagnostic(
        self,
        *,
        action: str,
        target_type: str,
        target_id: str | None = None,
        root_name: str | None = None,
        family: str | None = None,
        reason: str = "operator diagnostic remediation",
        actor: str = "system",
    ) -> dict[str, Any]:
        normalized_action = str(action or "").strip().lower()
        normalized_target_type = str(target_type or "").strip().lower()
        clean_reason = str(reason or "operator diagnostic remediation").strip() or "operator diagnostic remediation"
        if normalized_action == "retry_corpus_job":
            if normalized_target_type != "job" or not target_id:
                raise ValueError("retry_corpus_job requires target_type=job and target_id")
            result = database.requeue_corpus_job(job_id=target_id, reason=clean_reason)
        elif normalized_action == "run_backfill":
            effective_family = family or (target_id if normalized_target_type == "family" else None)
            if not root_name or not effective_family:
                raise ValueError("run_backfill requires root_name and exact worker family")
            result = self.run_corpus_backfill(kind=effective_family, limit=10, workers=1, root_name=root_name)
        elif normalized_action == "repair_asset_statuses":
            if not root_name:
                raise ValueError("repair_asset_statuses requires root_name")
            result = database.repair_extracted_corpus_asset_statuses(root_name=root_name)
        elif normalized_action == "clear_completed_errors":
            if not root_name:
                raise ValueError("clear_completed_errors requires root_name")
            result = database.clear_completed_corpus_job_errors(root_name=root_name)
        elif normalized_action == "recover_stale_running_jobs":
            result = database.recover_stale_running_corpus_jobs(root_name=root_name)
        else:
            raise ValueError(
                "diagnostic remediation action must be retry_corpus_job, run_backfill, repair_asset_statuses, "
                "clear_completed_errors, or recover_stale_running_jobs"
            )
        audit_target_id = target_id if _is_uuid_like(target_id) else None
        audit_event = database.record_audit_event(
            event_type="diagnostics.remediation",
            target_table=normalized_target_type or None,
            target_id=audit_target_id,
            details={
                "action": normalized_action,
                "actor": actor,
                "target_type": normalized_target_type,
                "target_id": target_id,
                "root_name": root_name,
                "family": family,
                "reason": clean_reason,
                "settings_mutated": False,
            },
        )
        return {
            "settings_mutated": False,
            "action": normalized_action,
            "target": {"type": normalized_target_type, "id": target_id},
            "root_name": root_name,
            "family": family,
            "result": result,
            "audit_event": audit_event,
        }

    def run_corpus_worker(
        self,
        *,
        kind: str = "all",
        limit: int | None = None,
        workers: int | None = None,
        interval_seconds: float = 5.0,
        once: bool = False,
        root_name: str | None = None,
        host_agent_roots: bool | None = None,
        component_name: str = "corpus-worker:docker",
    ) -> dict[str, Any]:
        runs = 0
        last_result: dict[str, Any] | None = None
        last_governance_at = 0.0
        last_automation_at = 0.0
        mail_orphan_recovery: dict[str, Any] | None = None
        worker_id = _new_worker_instance_id(component_name)
        if host_agent_roots is not True:
            try:
                mail_orphan_recovery = database.recover_interrupted_imap_sync_runs(
                    worker_id=component_name,
                    worker_started_at=datetime.now(timezone.utc),
                )
            except Exception as exc:
                mail_orphan_recovery = {"status": "failed", "error": str(exc), "worker_id": component_name}
        while True:
            runs += 1
            effective_limit = _resolved_worker_batch_size(limit)
            effective_workers = _resolved_worker_count(workers)
            _record_worker_instance_heartbeat(
                worker_id=worker_id,
                parent_component=component_name,
                metadata={
                    "kind": kind,
                    "limit": effective_limit,
                    "workers": effective_workers,
                    "root_name": root_name,
                    "host_agent_roots": host_agent_roots,
                    "runs": runs,
                    "worker_id": worker_id,
                },
            )
            database.record_runtime_component_heartbeat(
                name=component_name,
                status="running",
                metadata={
                    "kind": kind,
                    "limit": effective_limit,
                    "workers": effective_workers,
                    "root_name": root_name,
                    "host_agent_roots": host_agent_roots,
                    "runs": runs,
                },
            )
            last_result = self.run_corpus_backfill(
                kind=kind,
                limit=effective_limit,
                workers=effective_workers,
                root_name=root_name,
                host_agent_roots=host_agent_roots,
                worker_id=worker_id,
            )
            if mail_orphan_recovery is not None:
                last_result["mail_orphan_recovery"] = mail_orphan_recovery
                mail_orphan_recovery = None
            if host_agent_roots is not True:
                try:
                    from . import mail_ingestion

                    last_result["mail_sync"] = mail_ingestion.sync_due_mail_profiles(limit=effective_limit, worker_id=component_name)
                except Exception as exc:
                    last_result["mail_sync"] = {"status": "failed", "error": str(exc)}
            automation_policy = operator_automation.normalized_policy(_operator_automation_policy_from_settings())
            if bool(automation_policy.get("enabled")) and (time.monotonic() - last_automation_at) >= float(automation_policy.get("interval_seconds") or 1800):
                try:
                    last_result["automation"] = self.run_operator_automation(
                        mode=str(automation_policy.get("mode") or "guarded"),
                        trigger="worker",
                        actor=component_name,
                        limit=int(automation_policy.get("max_actions_per_run") or 25),
                    )
                    last_automation_at = time.monotonic()
                except Exception as exc:
                    last_result["automation"] = {"status": "failed", "error": str(exc), "settings_mutated": False}
            policy = governance.normalized_policy(_governance_policy_from_settings())
            if (
                not bool(automation_policy.get("enabled"))
                and bool(policy.get("librarian_enabled"))
                and (time.monotonic() - last_governance_at) >= float(policy.get("interval_seconds") or 3600)
            ):
                governance_mode = str(policy.get("mode") or "shadow")
                if not bool(policy.get("auto_apply_enabled")):
                    governance_mode = "shadow"
                try:
                    last_result["governance"] = self.run_governance(
                        mode=governance_mode,
                        actor=component_name,
                        limit=int(policy.get("max_actions_per_run") or 25),
                    )
                    last_governance_at = time.monotonic()
                except Exception as exc:
                    last_result["governance"] = {"status": "failed", "error": str(exc), "settings_mutated": False}
            database.record_runtime_component_heartbeat(
                name=component_name,
                status="running",
                metadata={"last_result": last_result, "runs": runs, "worker_id": worker_id},
            )
            if once:
                return {
                    "status": "completed_once",
                    "once": True,
                    "worker_id": worker_id,
                    "kind": kind,
                    "limit": effective_limit,
                    "workers": effective_workers,
                    "interval_seconds": interval_seconds,
                    "root_name": root_name,
                    "host_agent_roots": host_agent_roots,
                    "runs": runs,
                    "last_result": last_result,
                }
            time.sleep(interval_seconds)

    def _handle_watch_event(self, event: WatchEvent) -> None:
        try:
            database.record_watch_event(
                root_name=event.root_name,
                action=event.action,
                path_hash=_watch_event_path_hash(event),
                metadata={"action": event.action},
            )
            database.enqueue_corpus_sync_job(root_name=event.root_name, path=str(event.path), reason="watch_event")
        except Exception as exc:  # pragma: no cover - environment-specific watcher loop
            database.record_watch_error(root_name=event.root_name, error=str(exc))


def _episode_markdown(episode: dict[str, Any]) -> str:
    return "\n".join(
        [
            f"# {episode['title']}",
            "",
            f"- ID: `{episode['id']}`",
            f"- Source kind: `{episode['source_kind']}`",
            f"- Updated: `{episode['updated_at']}`",
            "",
            episode["summary"].strip(),
            "",
        ]
    )


def _slugify(value: str) -> str:
    slug = re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")
    return slug[:72] or "memory"


def _format_corpus_search_item(item: dict[str, Any]) -> dict[str, Any]:
    result = dict(item)
    result.setdefault("excerpt", str(result.get("summary") or ""))
    source_path = str(result.get("source_path") or "").replace("\\", "/")
    if source_path.endswith("/manifest.json") or source_path == "manifest.json":
        manifest = _parse_json_object(result.get("summary"))
        if manifest:
            subject = str(manifest.get("subject") or "").strip()
            if subject:
                result["title"] = f"Mail: {subject}"
            summary = _mail_manifest_summary(manifest)
            if summary:
                result["summary"] = summary
                result["excerpt"] = summary
    return result


def _format_memory_evidence_search_item(item: dict[str, Any]) -> dict[str, Any]:
    kind = str(item.get("kind") or "episode")
    detail_kind = "episode" if kind == "episode" else "claim"
    result = {
        "kind": kind,
        "logical_kind": "episode",
        **item,
        "excerpt": item.get("summary", ""),
        "detail_ref": {"kind": detail_kind, "id": item.get("id")},
        "related_evidence_count": 0,
    }
    return result


def normalize_retrieval_filters(filters: dict[str, Any] | None = None) -> dict[str, Any]:
    filters = filters or {}
    logical_kinds = _normalize_filter_values(filters.get("logical_kinds") or filters.get("kind"))
    invalid_kinds = [kind for kind in logical_kinds if kind not in ALLOWED_RETRIEVAL_LOGICAL_KINDS]
    if invalid_kinds:
        raise ValueError("logical_kinds must contain only: episode, file, mail")
    normalized = {
        "logical_kinds": logical_kinds,
        "current_only": bool(filters.get("current_only", False)),
        "lifecycle_states": _normalize_filter_values(filters.get("lifecycle_states") or filters.get("lifecycle_state")),
        "include_suppressed": bool(filters.get("include_suppressed", False)),
    }
    code_filters = {
        "file_kinds": _normalize_filter_values(filters.get("file_kinds") or filters.get("file_kind")),
        "languages": _normalize_filter_values(filters.get("languages") or filters.get("language")),
        "symbol_kinds": _normalize_filter_values(filters.get("symbol_kinds") or filters.get("symbol_kind")),
        "relationships": _normalize_filter_values(filters.get("relationships") or filters.get("relationship")),
        "path_globs": _normalize_filter_values(filters.get("path_globs") or filters.get("path_glob")),
    }
    _validate_code_file_kind_filter(code_filters["file_kinds"])
    normalized.update({key: value for key, value in code_filters.items() if value})
    if "include_generated" in filters:
        normalized["include_generated"] = bool(filters.get("include_generated"))
    return normalized


def _normalize_code_search_mode(mode: str | None) -> str:
    normalized = str(mode or CODE_SEARCH_LITERAL_SYMBOL_MODE).strip().lower().replace("-", "_")
    if normalized not in {CODE_SEARCH_LITERAL_SYMBOL_MODE, CODE_SEARCH_FULL_TEXT_MODE}:
        raise ValueError("mode must be one of: literal_symbol, full_text")
    return normalized


def _resolve_code_root_name(*, root_name: str | None, cwd: str | None) -> str | None:
    cleaned_root_name = _clean_optional_text(root_name)
    if cleaned_root_name:
        return cleaned_root_name
    root = _retrieval_root(None, _clean_optional_text(cwd))
    if root is None:
        return None
    return _clean_optional_text(str(root.get("name") or ""))


def _code_full_text_filters(
    *,
    language: str | None,
    symbol_kind: str | None,
    relationship: str | None,
    path_glob: str | None,
    include_generated: bool,
) -> dict[str, Any]:
    filters: dict[str, Any] = {
        "logical_kinds": ["file"],
        "file_kinds": [CODE_FILE_KIND],
        "include_generated": include_generated,
    }
    if language:
        filters["language"] = language
    if symbol_kind:
        filters["symbol_kind"] = symbol_kind
    if relationship:
        filters["relationship"] = relationship
    if path_glob:
        filters["path_glob"] = path_glob
    return normalize_retrieval_filters(filters)


def _sanitize_full_text_code_result(item: dict[str, Any]) -> dict[str, Any]:
    code = item.get("code") if isinstance(item.get("code"), dict) else {}
    code_range = code.get("range") if isinstance(code.get("range"), dict) else {}
    payload: dict[str, Any] = {
        "symbol": code.get("primary_symbol") or item.get("title"),
        "symbol_kind": code.get("symbol_kind"),
        "relationship": code.get("relationship"),
        "language": code.get("language"),
        "path": item.get("source_path") or code.get("source_path") or item.get("path"),
        "line_start": code_range.get("line_start") or code.get("line_start"),
        "line_end": code_range.get("line_end") or code.get("line_end"),
        "parser_status": code.get("parser_status"),
        "root_name": item.get("root_name"),
        "excerpt": item.get("excerpt"),
        "snippet": item.get("snippet"),
        "score": item.get("score"),
        "streams": item.get("streams"),
    }
    if code.get("generated") is not None:
        payload["is_generated"] = bool(code.get("generated"))
    return sanitize_code_result(payload)


def _validate_code_file_kind_filter(file_kinds: list[str]) -> None:
    if CODE_FILE_KIND in file_kinds and len(file_kinds) > 1:
        raise ValueError(
            'file_kinds must request code alone; use filters={"file_kinds":["code"]} '
            "or run separate broad non-code and dedicated code lookups."
        )


def _effective_retrieval_filters(filters: dict[str, Any] | None) -> dict[str, Any]:
    effective = dict(filters) if isinstance(filters, dict) else normalize_retrieval_filters(None)
    logical_kinds = {str(value).strip().lower().replace("-", "_") for value in effective.get("logical_kinds") or []}
    mail_only = logical_kinds == {"mail"}
    if not mail_only and not _retrieval_filters_request_code_results(effective):
        effective[INTERNAL_EXCLUDE_FILE_KINDS_KEY] = [CODE_FILE_KIND]
    return effective


def _retrieval_filters_request_code_results(filters: dict[str, Any] | None) -> bool:
    if not isinstance(filters, dict):
        return False
    file_kinds = {str(value).strip().lower().replace("-", "_") for value in filters.get("file_kinds") or []}
    return CODE_FILE_KIND in file_kinds


def _normalize_retrieval_benchmark_suite(value: str | None) -> str:
    normalized = str(value or "standard").strip().lower().replace("_", "-")
    if normalized not in {"standard", "governance-shadow"}:
        raise ValueError("retrieval benchmark suite must be standard or governance-shadow")
    return normalized


def _governance_action_telemetry(actions: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "total": len(actions),
        "by_source": dict(Counter(str(item.get("source") or "governance") for item in actions)),
        "by_action": dict(Counter(str(item.get("action") or "unknown") for item in actions)),
        "by_risk": dict(Counter(str(item.get("risk") or "medium") for item in actions)),
        "by_status": dict(Counter(str(item.get("status") or "unknown") for item in actions)),
        "by_mutation": dict(Counter("mutated" if item.get("memory_mutated") else "not_mutated" for item in actions)),
    }


def _governance_should_auto_apply(policy: dict[str, Any], gate: dict[str, Any]) -> bool:
    return (
        str(policy.get("mode") or "shadow") == "auto"
        and bool(policy.get("auto_apply_enabled"))
        and str(gate.get("status") or "") == "ready"
    )


def _governance_auto_apply_allowed(action: dict[str, Any]) -> bool:
    rationale = action.get("rationale") if isinstance(action.get("rationale"), dict) else {}
    guardrails = rationale.get("guardrails") if isinstance(rationale.get("guardrails"), dict) else {}
    return (
        action.get("status") == "proposed"
        and str(action.get("action") or "") in governance.LOW_RISK_AUTO_ACTIONS
        and str(action.get("risk") or "") == "low"
        and str(action.get("target_type") or "") == "claim"
        and bool(guardrails.get("apply_allowed"))
        and not bool(guardrails.get("protected"))
    )


def _governance_policy_from_settings() -> dict[str, Any]:
    settings = SettingsService()

    def resolve(key: str, default: Any) -> Any:
        try:
            return settings.resolve(key).raw_value
        except Exception:
            return default

    protected_rules = governance.DEFAULT_POLICY["protected_memory_rules"]
    configured_rules = resolve("governance.librarian.protected_memory_rules", "")
    if isinstance(configured_rules, str) and configured_rules.strip():
        try:
            parsed_rules = json.loads(configured_rules)
            if isinstance(parsed_rules, dict):
                protected_rules = {**protected_rules, **parsed_rules}
        except JSONDecodeError:
            pass
    return {
        "librarian_enabled": resolve("governance.librarian.enabled", False),
        "interval_seconds": resolve("governance.librarian.interval_seconds", 3600),
        "mode": resolve("governance.librarian.mode", governance.DEFAULT_POLICY["mode"]),
        "max_actions_per_run": resolve("governance.librarian.max_actions_per_run", governance.DEFAULT_POLICY["max_actions_per_run"]),
        "min_shadow_precision": resolve("governance.librarian.min_shadow_precision", governance.DEFAULT_POLICY["min_shadow_precision"]),
        "auto_apply_enabled": resolve("governance.librarian.auto_apply_enabled", governance.DEFAULT_POLICY["auto_apply_enabled"]),
        "auto_apply_risk_ceiling": resolve("governance.librarian.auto_apply_risk_ceiling", governance.DEFAULT_POLICY["auto_apply_risk_ceiling"]),
        "digest_retention_days": resolve("governance.librarian.digest_retention_days", governance.DEFAULT_POLICY["digest_retention_days"]),
        "local_model_rationale_enabled": resolve(
            "governance.local_model_rationale.enabled",
            governance.DEFAULT_POLICY["local_model_rationale_enabled"],
        ),
        "local_model_rationale_model": resolve(
            "governance.local_model_rationale.model",
            governance.DEFAULT_POLICY["local_model_rationale_model"],
        ),
        "protected_memory_rules": {
            "protect_metadata_flag": bool(protected_rules.get("protect_metadata_flag", True)),
            "protect_confirmed_confidence": float(protected_rules.get("protect_confirmed_confidence", 0.85)),
            "protect_reinforced_confidence": float(protected_rules.get("protect_reinforced_confidence", 0.75)),
            "protect_active_capture_review": bool(protected_rules.get("protect_active_capture_review", True)),
        },
    }


def _operator_automation_policy_from_settings() -> dict[str, Any]:
    settings = SettingsService()

    def resolve(key: str, default: Any) -> Any:
        try:
            return settings.resolve(key).raw_value
        except Exception:
            return default

    return {
        "enabled": resolve("operator.automation.enabled", operator_automation.DEFAULT_POLICY["enabled"]),
        "mode": resolve("operator.automation.mode", operator_automation.DEFAULT_POLICY["mode"]),
        "interval_seconds": resolve("operator.automation.interval_seconds", operator_automation.DEFAULT_POLICY["interval_seconds"]),
        "evidence_freshness_hours": resolve(
            "operator.automation.evidence_freshness_hours",
            operator_automation.DEFAULT_POLICY["evidence_freshness_hours"],
        ),
        "max_actions_per_run": resolve("operator.automation.max_actions_per_run", operator_automation.DEFAULT_POLICY["max_actions_per_run"]),
        "auto_refresh_evidence": resolve("operator.automation.auto_refresh_evidence", operator_automation.DEFAULT_POLICY["auto_refresh_evidence"]),
        "auto_ingest_approved_capture": resolve(
            "operator.automation.auto_ingest_approved_capture",
            operator_automation.DEFAULT_POLICY["auto_ingest_approved_capture"],
        ),
        "auto_remediate_diagnostics": resolve(
            "operator.automation.auto_remediate_diagnostics",
            operator_automation.DEFAULT_POLICY["auto_remediate_diagnostics"],
        ),
        "auto_sync_search_index": resolve(
            "operator.automation.auto_sync_search_index",
            operator_automation.DEFAULT_POLICY["auto_sync_search_index"],
        ),
        "auto_run_governance_shadow": resolve(
            "operator.automation.auto_run_governance_shadow",
            operator_automation.DEFAULT_POLICY["auto_run_governance_shadow"],
        ),
    }


def _operator_automation_recurring_state(policy: dict[str, Any], last_run: dict[str, Any] | None) -> dict[str, Any]:
    interval_seconds = int(policy.get("interval_seconds") or operator_automation.DEFAULT_POLICY["interval_seconds"])
    state: dict[str, Any] = {
        "enabled": bool(policy.get("enabled")),
        "interval_seconds": interval_seconds,
        "last_run_at": None,
        "next_run_at": None,
        "remaining_seconds": 0,
        "due": True,
        "settings_mutated": False,
    }
    if not last_run:
        return state
    last_run_at = _parse_operator_automation_time(
        last_run.get("completed_at") or last_run.get("updated_at") or last_run.get("created_at")
    )
    if last_run_at is None:
        return state
    next_run_at = last_run_at + timedelta(seconds=interval_seconds)
    now = datetime.now(timezone.utc)
    remaining = max(0, int((next_run_at - now).total_seconds()))
    return {
        **state,
        "last_run_at": last_run_at.isoformat(),
        "next_run_at": next_run_at.isoformat(),
        "remaining_seconds": remaining,
        "due": remaining == 0,
    }


def _parse_operator_automation_time(value: Any) -> datetime | None:
    if isinstance(value, datetime):
        parsed = value
    elif isinstance(value, str) and value.strip():
        try:
            parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        except ValueError:
            return None
    else:
        return None
    if parsed.tzinfo is None:
        return parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def _automation_plan_action(
    *,
    action: str,
    label: str,
    source: str,
    target_type: str,
    target_id: str,
    reason: str,
    evidence: dict[str, Any] | None = None,
    risk: str = "low",
) -> dict[str, Any]:
    return {
        "action": action,
        "label": label,
        "status": "eligible",
        "risk": risk,
        "source": source,
        "target_type": target_type,
        "target_id": target_id,
        "reason": reason,
        "rationale": {"summary": reason, "guardrails": {"settings_mutated": False, "allowlisted": True}},
        "evidence": evidence or {},
        "settings_mutated": False,
    }


def _automation_action_telemetry(actions: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "total": len(actions),
        "by_action": dict(Counter(str(item.get("action") or "unknown") for item in actions)),
        "by_status": dict(Counter(str(item.get("status") or "unknown") for item in actions)),
        "by_risk": dict(Counter(str(item.get("risk") or "unknown") for item in actions)),
        "by_source": dict(Counter(str(item.get("source") or "automation") for item in actions)),
        "settings_mutated": any(bool(item.get("settings_mutated")) for item in actions),
    }


def _governance_claim_transition(action: dict[str, Any]) -> str | None:
    if action.get("memory_class") != "claim" and action.get("target_type") != "claim":
        return None
    return {
        "mark_review": "stale",
        "stale_tag": "stale",
        "deprioritize": "deprioritize",
        "retire": "retire",
    }.get(str(action.get("action") or ""))


def _governance_conflict(action: dict[str, Any]) -> dict[str, Any] | None:
    if not _governance_claim_transition(action):
        return None
    before = action.get("before_state") if isinstance(action.get("before_state"), dict) else {}
    expected_state = before.get("lifecycle_state")
    if not expected_state:
        return None
    current = database.get_claim(str(action.get("target_id")))
    if current is None:
        return {"reason": "target_missing", "expected_lifecycle_state": expected_state}
    current_state = current.get("lifecycle_state")
    if current_state != expected_state:
        return {
            "reason": "target_state_changed",
            "expected_lifecycle_state": expected_state,
            "current_lifecycle_state": current_state,
        }
    return None


def _claim_lifecycle_snapshot(claim: dict[str, Any]) -> dict[str, Any]:
    return {
        "id": claim.get("id"),
        "lifecycle_state": claim.get("lifecycle_state"),
        "retention_action": claim.get("retention_action"),
    }


_REPROCESS_DERIVED_CACHES = ("asr", "embeddings", "ocr", "parser", "thumbnails", "vision")
_REPROCESS_PROTECTED_CACHES = {"mail_content", "models", "temp"}


def _parse_reprocess_cache_selection(value: str | None) -> list[str]:
    raw = str(value or "all").strip().lower()
    if raw in {"", "all"}:
        return list(_REPROCESS_DERIVED_CACHES)
    if raw == "none":
        return []
    selected = sorted({item.strip().lower() for item in raw.split(",") if item.strip()})
    blocked = [item for item in selected if item in _REPROCESS_PROTECTED_CACHES]
    unknown = [item for item in selected if item not in _REPROCESS_DERIVED_CACHES and item not in _REPROCESS_PROTECTED_CACHES]
    if blocked:
        raise ValueError(f"protected cache directories cannot be cleared by maintenance reprocess: {', '.join(blocked)}")
    if unknown:
        raise ValueError(f"unsupported cache selection: {', '.join(unknown)}")
    return selected


def _reprocess_cache_actions(selected: list[str], *, dry_run: bool) -> dict[str, Any]:
    layout = acceleration.resolve_cache_layout()
    root = Path(str(layout.get("root") or "")).expanduser()
    directories = layout.get("directories") if isinstance(layout.get("directories"), dict) else {}
    root_resolved = root.resolve()
    planned: list[str] = []
    cleared: list[str] = []
    missing: list[str] = []
    entries_removed: dict[str, int] = {}
    bytes_removed: dict[str, int] = {}
    for name in sorted(selected):
        if name in _REPROCESS_PROTECTED_CACHES:
            raise ValueError(f"protected cache directory cannot be cleared: {name}")
        if name not in _REPROCESS_DERIVED_CACHES:
            raise ValueError(f"unsupported cache selection: {name}")
        path = Path(str(directories.get(name) or root / name)).expanduser()
        resolved = path.resolve()
        expected = root_resolved / name
        if resolved != expected and (resolved == root_resolved or root_resolved not in resolved.parents):
            raise ValueError(f"refusing to clear cache path outside cache root: {path}")
        planned.append(name)
        if not path.exists():
            missing.append(name)
            entries_removed[name] = 0
            bytes_removed[name] = 0
            continue
        entry_count, byte_count = _cache_tree_stats(path)
        entries_removed[name] = entry_count
        bytes_removed[name] = byte_count
        if dry_run:
            continue
        for child in path.iterdir():
            if child.is_dir():
                shutil.rmtree(child)
            else:
                child.unlink()
        cleared.append(name)
    return {
        "source": layout.get("source"),
        "root": str(root),
        "requested": sorted(selected),
        "planned": planned,
        "cleared": cleared,
        "missing": missing,
        "entries": entries_removed,
        "bytes": bytes_removed,
        "dry_run": bool(dry_run),
        "protected": sorted(_REPROCESS_PROTECTED_CACHES),
    }


def _cache_tree_stats(path: Path) -> tuple[int, int]:
    count = 0
    size = 0
    for child in path.rglob("*"):
        count += 1
        try:
            if child.is_file():
                size += int(child.stat().st_size)
        except OSError:
            continue
    return count, size


_CAPTURE_BACKFILL_MAX_BYTES = 1024 * 1024
_CAPTURE_BACKFILL_MAX_BODY_CHARS = 8000
_CAPTURE_BACKFILL_TEXT_KEYS = ("body", "summary", "text", "content", "message", "last_assistant_message")
_CAPTURE_BACKFILL_TITLE_KEYS = ("title", "name", "subject")
_CAPTURE_BACKFILL_METADATA_KEYS = ("session_id", "turn_id", "cwd", "root_name", "workspace_key", "model")
_CAPTURE_BACKFILL_RAW_KEYS = {"body", "content", "html", "message", "raw", "raw_text", "summary", "text", "transcript"}
_CAPTURE_BACKFILL_PATH_KEYS = {"file", "path", "source", "source_dir"}


def _capture_backfill_source_path(payload: dict[str, Any]) -> Path | None:
    for key in ("path", "source", "file"):
        value = payload.get(key)
        if isinstance(value, str) and value.strip():
            return Path(value).expanduser()
    return None


def _file_source_hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return f"sha256:{digest.hexdigest()}"


def _normalize_codex_backfill_records(path: Path) -> list[dict[str, Any]]:
    text, truncated = _read_bounded_backfill_text(path)
    suffix = path.suffix.lower()
    if suffix == ".json":
        value = json.loads(text)
        values = value if isinstance(value, list) else [value]
        return [_normalize_codex_backfill_record(item, path=path, truncated=truncated) for item in values]
    if suffix == ".jsonl":
        records: list[dict[str, Any]] = []
        for index, line in enumerate(text.splitlines()):
            stripped = line.strip()
            if not stripped:
                continue
            records.append(_normalize_codex_backfill_record(json.loads(stripped), path=path, truncated=truncated, index=index))
        return records
    title = _markdown_title(text) if suffix in {".md", ".markdown"} else path.stem
    return [{"title": title or path.stem, "body": _truncate_body(text), "truncated": truncated, "source_format": suffix.lstrip(".") or "text"}]


def _read_bounded_backfill_text(path: Path) -> tuple[str, bool]:
    data = path.read_bytes()
    truncated = len(data) > _CAPTURE_BACKFILL_MAX_BYTES
    return data[:_CAPTURE_BACKFILL_MAX_BYTES].decode("utf-8", errors="replace"), truncated


def _normalize_codex_backfill_record(
    value: Any,
    *,
    path: Path,
    truncated: bool,
    index: int = 0,
) -> dict[str, Any]:
    if isinstance(value, str):
        return {
            "title": path.stem,
            "body": _truncate_body(value),
            "truncated": truncated or len(value) > _CAPTURE_BACKFILL_MAX_BODY_CHARS,
            "source_format": path.suffix.lower().lstrip(".") or "text",
            "record_index": index,
        }
    if not isinstance(value, dict):
        return {"title": path.stem, "body": "", "truncated": truncated, "source_format": path.suffix.lower().lstrip(".") or "json", "record_index": index}
    title = next((str(value.get(key)).strip() for key in _CAPTURE_BACKFILL_TITLE_KEYS if str(value.get(key) or "").strip()), "")
    body = next((str(value.get(key)).strip() for key in _CAPTURE_BACKFILL_TEXT_KEYS if str(value.get(key) or "").strip()), "")
    record = {
        "title": title or _codex_backfill_fallback_title(value, path),
        "body": _truncate_body(body),
        "truncated": truncated or len(body) > _CAPTURE_BACKFILL_MAX_BODY_CHARS,
        "source_format": path.suffix.lower().lstrip(".") or "json",
        "record_index": index,
    }
    for key in _CAPTURE_BACKFILL_METADATA_KEYS:
        if str(value.get(key) or "").strip():
            record[key] = str(value[key]).strip()
    return record


def _truncate_body(value: str) -> str:
    if len(value) <= _CAPTURE_BACKFILL_MAX_BODY_CHARS:
        return value
    return f"{value[:_CAPTURE_BACKFILL_MAX_BODY_CHARS].rstrip()}\n\n[truncated]"


def _codex_backfill_fallback_title(value: dict[str, Any], path: Path) -> str:
    turn = str(value.get("turn_id") or "").strip()
    if turn:
        return f"Codex turn {turn}"
    session = str(value.get("session_id") or "").strip()
    if session:
        return f"Codex session {session}"
    return path.stem


def _markdown_title(text: str) -> str | None:
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("#"):
            return stripped.lstrip("#").strip() or None
    return None


def _codex_backfill_metadata(
    record: dict[str, Any],
    *,
    job: dict[str, Any],
    source_path: Path,
    source_hash: str,
    record_index: int,
) -> dict[str, Any]:
    payload = job.get("payload") if isinstance(job.get("payload"), dict) else {}
    review = payload.get("review") if isinstance(payload.get("review"), dict) else {}
    metadata = {
        "source": "codex_backfill",
        "capture_review_job_id": str(job.get("id") or ""),
        "review_audit_event_id": review.get("audit_event_id"),
        "source_hash": source_hash,
        "source_leaf": _source_leaf(source_path),
        "record_index": record.get("record_index", record_index),
        "source_format": record.get("source_format"),
        "truncated": bool(record.get("truncated")),
    }
    for key in _CAPTURE_BACKFILL_METADATA_KEYS:
        if str(record.get(key) or "").strip():
            metadata[key] = str(record[key]).strip()
    return {key: value for key, value in metadata.items() if value not in {None, ""}}


def _capture_ingestion_response(job: dict[str, Any], ingestion: dict[str, Any]) -> dict[str, Any]:
    payload = job.get("payload") if isinstance(job.get("payload"), dict) else {}
    return {
        "id": job.get("id"),
        "job_type": job.get("job_type"),
        "status": job.get("status"),
        "payload": {**_sanitize_capture_payload(payload), "ingestion": ingestion, "status": job.get("status")},
        "ingestion": ingestion,
    }


def _sanitize_capture_payload(payload: dict[str, Any]) -> dict[str, Any]:
    sanitized: dict[str, Any] = {}
    for key, value in payload.items():
        lowered = key.lower()
        if lowered in _CAPTURE_BACKFILL_RAW_KEYS:
            continue
        if lowered in _CAPTURE_BACKFILL_PATH_KEYS and isinstance(value, str):
            sanitized[key] = _source_leaf(Path(value))
        elif isinstance(value, dict):
            sanitized[key] = _sanitize_capture_payload(value)
        else:
            sanitized[key] = value
    return sanitized


def _source_leaf(path: Path) -> str:
    value = str(path)
    normalized = value.replace("\\", "/").rstrip("/")
    return normalized.rsplit("/", 1)[-1] or normalized


def _retrieval_benchmark_case(
    service: KnowledgeService,
    *,
    case_id: str,
    category: str,
    query: str,
    root_name: str,
    source_path: str,
    expect_suppression: bool = False,
    filters: dict[str, Any] | None = None,
    semantic_similarity: float | None = None,
    expected_semantic_duplicate: bool | None = None,
    expected_symbol: str | None = None,
) -> dict[str, Any]:
    expected_id = _retrieval_benchmark_expected_id(
        service,
        query=query,
        root_name=root_name,
        source_path=source_path,
        filters=filters,
        expected_symbol=expected_symbol,
    )
    case = {
        "id": case_id,
        "category": category,
        "query": query,
        "root_name": root_name,
        "scope_mode": "local_only",
        "filters": filters,
        "expected_ids": [expected_id] if expected_id else [],
        "expected_brief_ids": [expected_id] if expected_id else [],
        "expected_scope": "local",
        "expect_suppression": expect_suppression,
        "semantic_similarity": semantic_similarity,
        "expected_semantic_duplicate": expected_semantic_duplicate,
    }
    if expected_symbol:
        case["expected_symbol"] = expected_symbol
    return case


def _retrieval_benchmark_expected_id(
    service: KnowledgeService,
    *,
    query: str,
    root_name: str,
    source_path: str,
    filters: dict[str, Any] | None,
    expected_symbol: str | None = None,
) -> str | None:
    results = service.search(query, limit=10, root_name=root_name, scope_mode="local_only", filters=filters)
    normalized_source = source_path.replace("\\", "/")
    if expected_symbol:
        for item in results:
            item_path = str(item.get("source_path") or "").replace("\\", "/")
            if item_path == normalized_source and _retrieval_benchmark_item_matches_symbol(item, expected_symbol):
                return str(item.get("id") or "") or None
        return _retrieval_benchmark_source_chunk_id(
            root_name=root_name,
            source_path=source_path,
            expected_symbol=expected_symbol,
        )
    for item in results:
        item_path = str(item.get("source_path") or "").replace("\\", "/")
        if item_path == normalized_source:
            return str(item.get("id") or "") or None
    resolved_id = _retrieval_benchmark_source_chunk_id(root_name=root_name, source_path=source_path)
    if resolved_id:
        return resolved_id
    return None


def _retrieval_benchmark_source_chunk_id(
    *,
    root_name: str,
    source_path: str,
    expected_symbol: str | None = None,
) -> str | None:
    normalized_source = source_path.replace("\\", "/").strip("/")
    assets = database.list_source_assets(root_name=root_name, path=normalized_source, limit=20)
    for asset in assets:
        item_path = str(asset.get("path") or "").replace("\\", "/").strip("/")
        if item_path != normalized_source:
            continue
        asset_id = str(asset.get("canonical_asset_id") or asset.get("id") or "")
        if not asset_id:
            continue
        detail = database.get_source_asset(asset_id)
        chunks = detail.get("chunks") if isinstance(detail, dict) else []
        if not isinstance(chunks, list):
            continue
        for chunk in sorted(
            [item for item in chunks if isinstance(item, dict)],
            key=lambda item: int(item.get("chunk_index") or 0),
        ):
            if expected_symbol and not _retrieval_benchmark_item_matches_symbol(chunk, expected_symbol):
                continue
            chunk_id = str(chunk.get("id") or "")
            if chunk_id:
                return chunk_id
    return None


def _retrieval_benchmark_item_matches_symbol(item: dict[str, Any], expected_symbol: str) -> bool:
    expected_aliases = _retrieval_benchmark_symbol_aliases(expected_symbol)
    if not expected_aliases:
        return False
    code = item.get("code") if isinstance(item.get("code"), dict) else {}
    primary_symbol = code.get("primary_symbol")
    if primary_symbol and _retrieval_benchmark_symbol_aliases(str(primary_symbol)).intersection(expected_aliases):
        return True
    title = str(item.get("title") or "")
    if "::" in title and _retrieval_benchmark_symbol_aliases(title.rsplit("::", 1)[-1]).intersection(expected_aliases):
        return True
    return False


def _retrieval_benchmark_symbol_aliases(value: str) -> set[str]:
    normalized = _normalize_retrieval_benchmark_symbol(value)
    if not normalized:
        return set()
    aliases = {normalized}
    if "." in normalized:
        aliases.add(normalized.rsplit(".", 1)[-1])
    return aliases


def _normalize_retrieval_benchmark_symbol(value: str) -> str:
    return str(value or "").strip().strip("`'\"()[]{}:,;").replace("::", ".").lower()


def _normalize_filter_values(value: Any) -> list[str]:
    if value is None:
        return []
    raw_values = value if isinstance(value, list) else [value]
    normalized: list[str] = []
    seen: set[str] = set()
    for item in raw_values:
        text = str(item or "").strip().lower().replace("-", "_")
        if not text or text in seen:
            continue
        seen.add(text)
        normalized.append(text)
    return sorted(normalized)


def _apply_retrieval_filters(
    results: list[dict[str, Any]],
    filters: dict[str, Any] | None,
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    if filters is None:
        return results, []
    included: list[dict[str, Any]] = []
    excluded: list[dict[str, Any]] = []
    for item in results:
        reason = _retrieval_filter_exclusion_reason(item, filters)
        if reason:
            excluded.append(_filter_excluded_item(item, reason=reason))
        else:
            included.append(item)
    return included, excluded


def _retrieval_filter_exclusion_reason(item: dict[str, Any], filters: dict[str, Any]) -> str | None:
    logical_kinds = set(filters.get("logical_kinds") or [])
    item_kind = str(item.get("logical_kind") or item.get("kind") or "").lower()
    if logical_kinds and item_kind not in logical_kinds:
        return "logical_kind"

    lifecycle_state = _item_lifecycle_state(item)
    lifecycle_states = set(filters.get("lifecycle_states") or [])
    if lifecycle_states and lifecycle_state not in lifecycle_states:
        return "lifecycle_state"

    if filters.get("current_only") and not _is_current_evidence(item):
        return "current_only"

    code = item.get("code") if isinstance(item.get("code"), dict) else {}
    item_file_kind = str(item.get("file_kind") or "").lower().replace("-", "_")
    excluded_file_kinds = set(filters.get(INTERNAL_EXCLUDE_FILE_KINDS_KEY) or filters.get("exclude_file_kinds") or [])
    if excluded_file_kinds and (item_file_kind in excluded_file_kinds or (CODE_FILE_KIND in excluded_file_kinds and code)):
        return "excluded_file_kind"

    file_kinds = set(filters.get("file_kinds") or [])
    if file_kinds and item_file_kind not in file_kinds:
        return "file_kind"

    languages = set(filters.get("languages") or [])
    item_language = str(code.get("language") or item.get("language") or "").lower().replace("-", "_")
    if languages and item_language not in languages:
        return "language"

    symbol_kinds = set(filters.get("symbol_kinds") or [])
    item_symbol_kind = str(code.get("symbol_kind") or item.get("symbol_kind") or "").lower().replace("-", "_")
    if symbol_kinds and item_symbol_kind not in symbol_kinds:
        return "symbol_kind"

    relationships = set(filters.get("relationships") or [])
    item_relationship = str(code.get("relationship") or item.get("relationship") or "").lower().replace("-", "_")
    if relationships and item_relationship not in relationships:
        return "relationship"

    if filters.get("include_generated") is False and _item_is_generated(item, code):
        return "generated"

    path_globs = filters.get("path_globs") or []
    source_path = str(item.get("source_path") or code.get("source_path") or "").replace("\\", "/")
    if path_globs and not any(fnmatch.fnmatch(source_path, pattern) for pattern in path_globs):
        return "path_glob"
    return None


def _item_is_generated(item: dict[str, Any], code: dict[str, Any]) -> bool:
    metadata = item.get("metadata") if isinstance(item.get("metadata"), dict) else {}
    return bool(item.get("is_generated") or item.get("generated") or code.get("generated") or metadata.get("generated"))


def _item_lifecycle_state(item: dict[str, Any]) -> str:
    lifecycle = item.get("lifecycle") if isinstance(item.get("lifecycle"), dict) else {}
    return str(lifecycle.get("state") or item.get("lifecycle_state") or "active").lower().replace("-", "_")


def _filter_excluded_item(item: dict[str, Any], *, reason: str) -> dict[str, Any]:
    lifecycle = item.get("lifecycle") if isinstance(item.get("lifecycle"), dict) else {}
    excluded = {
        "id": str(item.get("id") or ""),
        "title": str(item.get("title") or item.get("id") or "Untitled"),
        "kind": str(item.get("logical_kind") or item.get("kind") or ""),
        "score": float(item.get("score") or 0.0),
        "reason": reason,
        "lifecycle_state": str(lifecycle.get("state") or item.get("lifecycle_state") or ""),
    }
    if item.get("source_path"):
        excluded["source_path"] = str(item.get("source_path"))
    return excluded


def _enrich_search_results(
    query: str,
    results: list[dict[str, Any]],
    *,
    retrieval_filters: dict[str, Any] | None = None,
) -> list[dict[str, Any]]:
    ranked_results = _with_rank_evidence(results)
    return [enrich_search_result(query, _with_retrieval_filters(item, retrieval_filters)) for item in ranked_results]


def _with_rank_evidence(results: list[dict[str, Any]]) -> list[dict[str, Any]]:
    ranked: list[dict[str, Any]] = []
    for index, item in enumerate(results):
        current_score = float(item.get("score") or 0.0)
        next_score = float(results[index + 1].get("score") or 0.0) if index + 1 < len(results) else None
        enriched = dict(item)
        enriched["rank"] = index + 1
        enriched["rank_margin"] = round(current_score - next_score, 6) if next_score is not None else None
        ranked.append(enriched)
    return ranked


def _with_retrieval_filters(item: dict[str, Any], filters: dict[str, Any] | None) -> dict[str, Any]:
    if filters is None:
        return item
    result = dict(item)
    result["retrieval_filters"] = filters
    return result


def _suppression_trace(results: list[dict[str, Any]]) -> dict[str, Any]:
    exact_duplicates: list[dict[str, Any]] = []
    version_families: list[dict[str, Any]] = []
    semantic_duplicates: list[dict[str, Any]] = []
    for item in results:
        duplicate_count = _positive_int(item.get("duplicate_count"))
        if duplicate_count:
            exact = {
                "id": str(item.get("id") or ""),
                "title": str(item.get("title") or item.get("id") or "Untitled"),
                "suppressed_count": duplicate_count,
                "reason": "exact_content_duplicate",
            }
            if item.get("source_path"):
                exact["canonical_source_path"] = str(item.get("source_path"))
            if item.get("asset_id"):
                exact["canonical_asset_id"] = str(item.get("asset_id"))
            exact_duplicates.append(exact)

        version_family = item.get("version_family")
        if isinstance(version_family, dict) and _positive_int(version_family.get("suppressed_count")):
            family = {
                "id": str(item.get("id") or ""),
                "title": str(item.get("title") or item.get("id") or "Untitled"),
                "suppressed_count": _positive_int(version_family.get("suppressed_count")),
                "reason": "same_document_version_family",
            }
            for key in ("key", "canonical_source_path", "suppressed_source_paths"):
                if version_family.get(key) is not None:
                    family[key] = version_family.get(key)
            version_families.append(family)

        semantic_cluster = item.get("semantic_duplicate_cluster")
        if isinstance(semantic_cluster, dict) and _positive_int(semantic_cluster.get("suppressed_count")):
            semantic = {
                "id": str(item.get("id") or ""),
                "title": str(item.get("title") or item.get("id") or "Untitled"),
                "cluster_id": str(semantic_cluster.get("cluster_id") or ""),
                "suppressed_count": _positive_int(semantic_cluster.get("suppressed_count")),
                "reason": "semantic_near_duplicate",
            }
            for key in ("threshold", "max_similarity", "suppressed"):
                if semantic_cluster.get(key) is not None:
                    semantic[key] = semantic_cluster.get(key)
            semantic_duplicates.append(semantic)

    trace: dict[str, Any] = {}
    if exact_duplicates:
        trace["exact_duplicates"] = exact_duplicates
    if version_families:
        trace["version_families"] = version_families
    if semantic_duplicates:
        trace["semantic_duplicates"] = semantic_duplicates
    return trace


def _positive_int(value: Any) -> int:
    try:
        number = int(value or 0)
    except (TypeError, ValueError):
        return 0
    return max(0, number)


def _brief_selection_trace(search_results: list[dict[str, Any]], *, token_budget: int) -> dict[str, Any]:
    current_results = [item for item in search_results if _is_current_evidence(item)]
    excluded: list[dict[str, Any]] = []
    packing_results = search_results
    if current_results:
        packing_results = current_results
        excluded.extend(_brief_excluded_item(item, reason="non_current") for item in search_results if item not in current_results)
    candidates = [
        ContextCandidate(
            id=str(item.get("id") or ""),
            title=str(item.get("title") or item.get("id") or "Untitled"),
            body=str(item.get("summary") or ""),
            score=float(item.get("score") or 0.0),
        )
        for item in packing_results
    ]
    packed = pack_context_with_trace(candidates, token_budget=token_budget)
    return {
        "text": packed.text,
        "token_budget": token_budget,
        "packed": list(packed.packed),
        "excluded": excluded + list(packed.excluded),
    }


def _brief_excluded_item(item: dict[str, Any], *, reason: str) -> dict[str, Any]:
    lifecycle = item.get("lifecycle") if isinstance(item.get("lifecycle"), dict) else {}
    return {
        "id": str(item.get("id") or ""),
        "title": str(item.get("title") or item.get("id") or "Untitled"),
        "score": float(item.get("score") or 0.0),
        "reason": reason,
        "lifecycle_state": str(lifecycle.get("state") or item.get("lifecycle_state") or ""),
    }


def _parse_json_object(value: Any) -> dict[str, Any]:
    if isinstance(value, dict):
        return value
    if not isinstance(value, str):
        return {}
    try:
        payload = json.loads(value)
    except json.JSONDecodeError:
        return {}
    return payload if isinstance(payload, dict) else {}


def _mail_manifest_summary(manifest: dict[str, Any]) -> str:
    parts: list[str] = []
    sender = _display_address(manifest.get("sender"))
    if sender:
        parts.append(f"From {sender}")
    recipients = manifest.get("recipients")
    if isinstance(recipients, list) and recipients:
        display_recipients = ", ".join(_display_address(item) for item in recipients[:3] if _display_address(item))
        if display_recipients:
            parts.append(f"to {display_recipients}")
    received_at = str(manifest.get("received_at") or "").strip()
    if received_at:
        parts.append(f"received {received_at}")
    source_folder = str(manifest.get("source_folder") or "").strip()
    if source_folder:
        parts.append(f"folder {source_folder}")
    if manifest.get("attachment_count") is not None:
        count = int(manifest.get("attachment_count") or 0)
        parts.append(f"{count} attachment{'s' if count != 1 else ''}")
    return "; ".join(parts) + "." if parts else ""


def _resolve_retrieval_scope(*, cwd: str | None, root_name: str | None, scope_mode: str) -> RetrievalScope:
    mode = _normalize_scope_mode(scope_mode)
    if mode == "global":
        return RetrievalScope(mode=mode)

    cleaned_cwd = _clean_optional_text(cwd)
    cleaned_root_name = _clean_optional_text(root_name)
    if not cleaned_cwd and not cleaned_root_name:
        return RetrievalScope(mode=mode)
    root = _retrieval_root(cleaned_root_name, cleaned_cwd)
    workspace = _workspace_identity(cwd=cleaned_cwd, root_name=cleaned_root_name, root=root)
    if root is None:
        return RetrievalScope(
            mode=mode,
            cwd=cleaned_cwd,
            root_name=cleaned_root_name,
            workspace_root=workspace.get("workspace_root"),
            workspace_key=workspace.get("workspace_key"),
        )
    return RetrievalScope(
        mode=mode,
        cwd=cleaned_cwd,
        root_name=str(root.get("name") or cleaned_root_name or ""),
        root_path=str(root.get("root_path") or "") or None,
        workspace_root=workspace.get("workspace_root"),
        workspace_key=workspace.get("workspace_key"),
    )


def _normalize_scope_mode(scope_mode: str) -> str:
    mode = str(scope_mode or "local_first").strip().lower()
    if mode == "expanded":
        mode = "workspace_boosted"
    if mode not in {"local_first", "local_only", "global", "workspace_boosted"}:
        raise ValueError("scope_mode must be one of: local_first, local_only, global, workspace_boosted")
    return mode


def _clean_optional_text(value: str | None) -> str | None:
    text = str(value or "").strip()
    return text or None


def _is_uuid_like(value: Any) -> bool:
    try:
        UUID(str(value or ""))
    except (TypeError, ValueError):
        return False
    return True


def _retrieval_root(root_name: str | None, cwd: str | None) -> dict[str, Any] | None:
    if root_name and database.search_corpus_chunks is not _DEFAULT_SEARCH_CORPUS_CHUNKS:
        return {"name": root_name, "root_path": ""}
    try:
        roots = database.list_monitored_roots()
    except Exception:
        roots = []
    enabled_roots = [root for root in roots if root.get("enabled", True)]
    if root_name:
        for root in enabled_roots:
            if root.get("name") == root_name:
                return root
        return None
    if cwd:
        matching = [
            root
            for root in enabled_roots
            if _path_is_under_root(cwd, str(root.get("root_path") or ""))
        ]
        if matching:
            return sorted(matching, key=lambda item: len(str(item.get("root_path") or "")), reverse=True)[0]
    return None


def _enrich_workspace_metadata(
    metadata: dict[str, Any],
    *,
    cwd: str | None = None,
    root_name: str | None = None,
) -> dict[str, Any]:
    result = dict(metadata)
    metadata_cwd = _clean_optional_text(cwd) or _clean_optional_text(result.get("cwd"))
    metadata_root_name = _clean_optional_text(root_name) or _clean_optional_text(result.get("root_name"))
    result.update(_workspace_metadata(cwd=metadata_cwd, root_name=metadata_root_name))
    return result


def _workspace_metadata(*, cwd: str | None = None, root_name: str | None = None) -> dict[str, str]:
    cleaned_cwd = _clean_optional_text(cwd)
    cleaned_root_name = _clean_optional_text(root_name)
    root = _retrieval_root(cleaned_root_name, cleaned_cwd)
    return _workspace_identity(cwd=cleaned_cwd, root_name=cleaned_root_name, root=root)


def _workspace_identity(
    *,
    cwd: str | None,
    root_name: str | None,
    root: dict[str, Any] | None,
) -> dict[str, str]:
    metadata: dict[str, str] = {}
    if cwd:
        metadata["cwd"] = cwd
    if root is not None:
        resolved_root_name = str(root.get("name") or root_name or "").strip()
        root_path = str(root.get("root_path") or "").strip()
        if resolved_root_name:
            metadata["root_name"] = resolved_root_name
            metadata["workspace_key"] = f"root:{resolved_root_name}"
        if root_path:
            metadata["workspace_root"] = root_path
        return metadata
    if root_name:
        metadata["root_name"] = root_name

    workspace_root = _git_repo_root(cwd) if cwd else None
    if not workspace_root and cwd:
        workspace_root = cwd
    if workspace_root:
        metadata["workspace_root"] = workspace_root
        metadata["workspace_key"] = f"path:{_normalised_workspace_path(workspace_root)}"
    return metadata


def _git_repo_root(cwd: str | None) -> str | None:
    if not cwd:
        return None
    try:
        completed = run_no_window(
            ["git", "rev-parse", "--show-toplevel"],
            cwd=str(cwd),
            text=True,
            capture_output=True,
            timeout=5,
            check=False,
        )
    except Exception:
        return None
    if completed.returncode != 0:
        return None
    root = completed.stdout.strip()
    return root or None


def _normalised_workspace_path(path: str) -> str:
    cleaned = str(path).strip().rstrip("\\/")
    return cleaned.replace("\\", "/").lower()


def _has_lexical_or_fuzzy_evidence(results: list[dict[str, Any]]) -> bool:
    for result in results:
        streams = {str(stream) for stream in result.get("streams", [])}
        if any("lexical" in stream or "fuzzy" in stream for stream in streams):
            return True
    return False


def _is_strong_cross_workspace_evidence(item: dict[str, Any]) -> bool:
    streams = {str(stream) for stream in item.get("streams", [])}
    if any("lexical" in stream or "fuzzy" in stream for stream in streams):
        return True
    if {"vespa_hybrid", "vespa_rrf", "vespa_dense"}.intersection(streams):
        return float(item.get("score") or 0.0) >= STRONG_SEMANTIC_MIN_SCORE
    return False


def _result_identity(item: dict[str, Any]) -> tuple[str, str]:
    kind = str(item.get("logical_kind") or item.get("kind") or "")
    identifier = str(item.get("id") or item.get("asset_id") or item.get("source_path") or item.get("title") or "")
    return kind, identifier


def _dedupe_search_results(items: list[dict[str, Any]]) -> list[dict[str, Any]]:
    deduped: list[dict[str, Any]] = []
    seen: set[tuple[str, str]] = set()
    for item in items:
        identity = _result_identity(item)
        if identity in seen:
            continue
        seen.add(identity)
        deduped.append(item)
    return deduped


def _with_scope_score_boost(item: dict[str, Any], boost: float) -> dict[str, Any]:
    result = dict(item)
    base_score = float(result.get("score") or 0.0)
    result["base_score"] = base_score
    result["scope_score_boost"] = boost
    result["score"] = base_score * boost
    return result


def _tag_retrieval_scope(item: dict[str, Any], label: str, *, scope: RetrievalScope | None = None) -> dict[str, Any]:
    result = dict(item)
    result["retrieval_scope"] = _truthful_retrieval_label(result, label, scope=scope)
    if scope:
        if scope.cwd:
            result["retrieval_cwd"] = scope.cwd
        if scope.root_name:
            result["retrieval_root_name"] = scope.root_name
        if scope.root_path:
            result["retrieval_root_path"] = scope.root_path
        if scope.workspace_root:
            result["retrieval_workspace_root"] = scope.workspace_root
        if scope.workspace_key:
            result["retrieval_workspace_key"] = scope.workspace_key
    return result


def _truthful_retrieval_label(item: dict[str, Any], label: str, *, scope: RetrievalScope | None = None) -> str:
    if label != "cross_workspace":
        return label
    if _matches_scope_provenance(item, scope):
        return "local"
    if _has_known_workspace_provenance(item):
        return "cross_workspace"
    return "unscoped_global"


def _matches_scope_provenance(item: dict[str, Any], scope: RetrievalScope | None) -> bool:
    if scope is None:
        return False
    metadata = item.get("metadata") if isinstance(item.get("metadata"), dict) else {}
    item_workspace_key = _clean_optional_text(metadata.get("workspace_key"))
    if item_workspace_key and scope.workspace_key:
        return item_workspace_key == scope.workspace_key
    item_root_name = _clean_optional_text(item.get("root_name")) or _clean_optional_text(metadata.get("root_name"))
    if item_root_name and scope.root_name:
        return item_root_name == scope.root_name
    item_cwd = _clean_optional_text(metadata.get("cwd"))
    if item_cwd and (scope.root_path or scope.cwd):
        return _path_is_under_root(item_cwd, scope.root_path or scope.cwd or "")
    return False


def _has_known_workspace_provenance(item: dict[str, Any]) -> bool:
    metadata = item.get("metadata") if isinstance(item.get("metadata"), dict) else {}
    return bool(
        _clean_optional_text(item.get("root_name"))
        or _clean_optional_text(metadata.get("root_name"))
        or _clean_optional_text(metadata.get("workspace_key"))
        or _clean_optional_text(metadata.get("workspace_root"))
        or _clean_optional_text(metadata.get("cwd"))
    )


def _redact_metadata(value: Any) -> tuple[Any, list[RedactionFinding]]:
    if isinstance(value, str):
        return redact_text(value)
    if isinstance(value, list):
        redacted_items: list[Any] = []
        findings: list[RedactionFinding] = []
        for item in value:
            redacted_item, item_findings = _redact_metadata(item)
            redacted_items.append(redacted_item)
            findings.extend(item_findings)
        return redacted_items, findings
    if isinstance(value, dict):
        redacted: dict[str, Any] = {}
        findings: list[RedactionFinding] = []
        for key, item in value.items():
            redacted_key, key_findings = redact_text(str(key))
            redacted_item, item_findings = _redact_metadata(item)
            redacted[redacted_key] = redacted_item
            findings.extend(key_findings)
            findings.extend(item_findings)
        return redacted, findings
    return value, []


def _is_current_evidence(item: dict[str, Any]) -> bool:
    lifecycle = item.get("lifecycle")
    if not isinstance(lifecycle, dict):
        return True
    if lifecycle.get("current") is False:
        return False
    return str(lifecycle.get("state") or "active") not in {"superseded", "contradicted", "retired"}


def _display_address(value: Any) -> str:
    text = str(value or "").strip()
    if "<" in text:
        text = text.split("<", 1)[0].strip()
    return text


def _select_root(*, root_name: str | None, path: str | Path | None) -> dict[str, Any]:
    roots = database.list_monitored_roots()
    if root_name:
        for root in roots:
            if root["name"] == root_name:
                return root
        raise ValueError(f"monitored root not found: {root_name}")
    if path:
        target = str(path)
        for root in roots:
            if _path_is_under_root(target, str(root["root_path"])):
                return root
        raise ValueError(f"path is not under a monitored root: {path}")
    if len(roots) == 1:
        return roots[0]
    raise ValueError("specify --root or --path")


def _job_matches_kind(job_type: str, kind: str) -> bool:
    if kind == "images":
        return job_type == "corpus_extract_image"
    if kind == "diagrams":
        return job_type == "corpus_extract_diagram"
    if kind in {"archives", "containers"}:
        return job_type in {"corpus_extract_archive", "corpus_extract_container"}
    if kind == "media":
        return job_type in {"corpus_extract_audio", "corpus_extract_video"}
    if kind == "text":
        return job_type in {"corpus_extract_text", "corpus_extract_code", "corpus_extract_document"}
    if kind == "search-index":
        return job_type == "search_index_sync"
    return True


def _configured_token_budget() -> int:
    try:
        return int(SettingsService().resolve("retrieval.token_budget").raw_value)
    except Exception:
        return 1200


def _configured_retrieval_search_engine() -> str:
    try:
        return str(SettingsService().resolve("retrieval.search_engine").raw_value or "vespa").strip().lower()
    except Exception:
        return "vespa"


def _configured_vespa_base_url() -> str:
    try:
        return str(SettingsService().resolve("retrieval.vespa_base_url").raw_value or "http://127.0.0.1:8080").strip()
    except Exception:
        return "http://127.0.0.1:8080"


def _search_corpus_with_configured_engine(query: str, **kwargs: Any) -> list[dict[str, Any]]:
    diagnostics = kwargs.get("diagnostics") if isinstance(kwargs.get("diagnostics"), dict) else None
    if database.search_corpus_chunks is not _DEFAULT_SEARCH_CORPUS_CHUNKS:
        return database.search_corpus_chunks(query, **kwargs)
    if _configured_retrieval_search_engine() != "vespa":
        return database.search_corpus_chunks_postgres_diagnostic(query, **kwargs)
    try:
        return database.search_corpus_chunks_vespa(
            query,
            vespa_base_url=_configured_vespa_base_url(),
            **kwargs,
        )
    except Exception as exc:
        if diagnostics is not None:
            diagnostics.setdefault("degraded_mode", {})["reason"] = str(exc)[:300]
            diagnostics["degraded_mode"]["search_engine"] = "vespa"
            diagnostics["degraded_mode"]["fallback"] = "postgres_lexical_diagnostic"
        return database.search_corpus_chunks_postgres_diagnostic(query, **kwargs)


def _search_evidence_with_configured_engine(query: str, **kwargs: Any) -> list[dict[str, Any]] | None:
    diagnostics = kwargs.get("diagnostics") if isinstance(kwargs.get("diagnostics"), dict) else None
    if database.search_corpus_chunks is not _DEFAULT_SEARCH_CORPUS_CHUNKS:
        return None
    if _configured_retrieval_search_engine() != "vespa":
        return None
    try:
        return database.search_evidence_vespa(
            query,
            vespa_base_url=_configured_vespa_base_url(),
            **kwargs,
        )
    except Exception as exc:
        if diagnostics is not None:
            diagnostics.setdefault("degraded_mode", {})["reason"] = str(exc)[:300]
            diagnostics["degraded_mode"]["search_engine"] = "vespa"
            diagnostics["degraded_mode"]["fallback"] = "postgres_lexical_diagnostic"
        return None


def _configured_reconcile_on_start() -> bool:
    try:
        return bool(SettingsService().resolve("watcher.reconcile_on_start").raw_value)
    except Exception:
        return True


def _configured_reconcile_interval_seconds() -> int:
    try:
        return int(SettingsService().resolve("watcher.reconcile_interval_seconds").raw_value)
    except Exception:
        return 3600


def _configured_watcher_debounce_seconds() -> float:
    try:
        return float(SettingsService().resolve("watcher.debounce_seconds").raw_value)
    except Exception:
        return 2.0


def _configured_watcher_max_queue_size() -> int:
    try:
        return int(SettingsService().resolve("watcher.max_queue_size").raw_value)
    except Exception:
        return 1000


def _configured_stability_quiet_seconds() -> float:
    try:
        return float(SettingsService().resolve("watcher.stability_quiet_seconds").raw_value)
    except Exception:
        return 2.0


def _configured_large_file_stability_quiet_seconds() -> float:
    try:
        return float(SettingsService().resolve("watcher.large_file_stability_quiet_seconds").raw_value)
    except Exception:
        return 30.0


def _configured_watcher_backend() -> str:
    try:
        return str(SettingsService().resolve("watcher.backend").raw_value)
    except Exception:
        return "auto"


def _configured_hash_parallelism() -> int:
    try:
        return int(SettingsService().resolve("crawler.hash_parallelism").raw_value)
    except Exception:
        return 1


def _configured_unseen_asset_purge_grace_seconds() -> int:
    try:
        return max(0, int(SettingsService().resolve("crawler.unseen_asset_purge_grace_seconds").raw_value))
    except Exception:
        return database.DEFAULT_UNSEEN_ASSET_PURGE_GRACE_SECONDS


def _configured_unseen_asset_purge_batch_size() -> int:
    try:
        return max(1, min(int(SettingsService().resolve("crawler.unseen_asset_purge_batch_size").raw_value), 5000))
    except Exception:
        return database.DEFAULT_UNSEEN_ASSET_PURGE_BATCH_SIZE


def _configured_worker_caps() -> dict[str, int]:
    settings = SettingsService()
    caps: dict[str, int] = {}
    for family, default in FAMILY_DEFAULT_CAPS.items():
        try:
            caps[family] = int(settings.resolve(f"acceleration.worker_cap.{family}").raw_value)
        except Exception:
            caps[family] = int(default)
    return caps


def _configured_worker_batch_size() -> int:
    try:
        return int(SettingsService().resolve("worker.batch_size").raw_value)
    except Exception:
        return 24


def _configured_default_workers() -> int:
    try:
        return int(SettingsService().resolve("worker.default_workers").raw_value)
    except Exception:
        return 8


def _resolved_worker_batch_size(limit: int | None) -> int:
    value = _configured_worker_batch_size() if limit is None else limit
    return max(1, int(value))


def _resolved_worker_count(workers: int | None) -> int:
    value = _configured_default_workers() if workers is None else workers
    return max(1, int(value))


def _new_worker_instance_id(component_name: str) -> str:
    return f"{component_name}:{uuid.uuid4().hex}"


def _record_worker_instance_heartbeat(
    *,
    worker_id: str,
    parent_component: str | None,
    metadata: dict[str, Any] | None = None,
) -> None:
    heartbeat_metadata = {"worker_instance": True}
    if parent_component:
        heartbeat_metadata["parent_component"] = parent_component
    heartbeat_metadata.update({key: value for key, value in (metadata or {}).items() if value is not None})
    try:
        database.record_runtime_component_heartbeat(
            name=worker_id,
            status="running",
            metadata=heartbeat_metadata,
        )
    except Exception:
        pass


def _configured_retry_cooldown_seconds() -> int:
    try:
        return int(SettingsService().resolve("worker.retry_cooldown_seconds").raw_value)
    except Exception:
        return 300


def _configured_lock_retry_cooldown_seconds() -> int:
    try:
        return int(SettingsService().resolve("worker.lock_retry_cooldown_seconds").raw_value)
    except Exception:
        return 300


def _configured_lock_max_attempts() -> int:
    try:
        return int(SettingsService().resolve("worker.lock_max_attempts").raw_value)
    except Exception:
        return 3


def _configured_failure_max_attempts() -> int:
    try:
        return int(SettingsService().resolve("worker.failure_max_attempts").raw_value)
    except Exception:
        return 3


def _configured_container_limits() -> dict[str, int]:
    settings = SettingsService()
    defaults = CorpusPolicy(root_path=Path("."))
    keys = {
        "container_max_depth": "crawler.container_max_depth",
        "container_max_members": "crawler.container_max_members",
        "container_max_total_bytes": "crawler.container_max_total_bytes",
        "container_max_member_bytes": "crawler.container_max_member_bytes",
    }
    resolved: dict[str, int] = {}
    for field_name, setting_key in keys.items():
        try:
            resolved[field_name] = int(settings.resolve(setting_key).raw_value)
        except Exception:
            resolved[field_name] = int(getattr(defaults, field_name))
    return resolved


def _manifest_lookup(root_name: str):
    def lookup(relative_path: str) -> dict[str, Any] | None:
        try:
            return database.lookup_scan_manifest(root_name=root_name, path=relative_path)
        except Exception:
            return None

    return lookup


def _manifest_store(root_name: str) -> dict[str, dict[str, Any]]:
    try:
        return database.load_scan_manifest(root_name=root_name)
    except Exception:
        return {}


def _watch_event_path_hash(event: WatchEvent) -> str:
    digest = hashlib.sha256(f"{event.root_name}:{event.relative_path}".encode("utf-8", errors="ignore")).hexdigest()
    return f"sha256:{digest}"


def _drained_watch_events(watcher: Any, polled_events: list[WatchEvent]) -> list[WatchEvent]:
    if hasattr(watcher, "drain_events"):
        return list(watcher.drain_events())
    return list(polled_events or [])


def _watcher_queue_depth(watcher: Any) -> int:
    queue = getattr(watcher, "_queue", None)
    try:
        return len(queue) if queue is not None else 0
    except TypeError:
        return 0


def _benchmark_family_breakdown(plan: Any) -> dict[str, dict[str, int]]:
    return _benchmark_family_breakdown_for_assets(plan.assets)


def _code_gaps(report: dict[str, Any], feedback: dict[str, Any], benchmark: dict[str, Any] | None = None) -> list[dict[str, Any]]:
    gaps: list[dict[str, Any]] = []
    totals = report.get("totals") if isinstance(report.get("totals"), dict) else {}
    fallback_count = int(totals.get("fallback_count") or 0)
    if fallback_count:
        gaps.append(
            {
                "category": "parser_fallback",
                "priority": "high",
                "count": fallback_count,
                "summary": "Parser fallback rows need review before code retrieval tuning.",
            }
        )
    for row in feedback.get("rows", []) if isinstance(feedback.get("rows"), list) else []:
        if not isinstance(row, dict):
            continue
        count = int(row.get("event_count") or 0)
        if count <= 0:
            continue
        gaps.append(
            {
                "category": row.get("miss_category") or "other",
                "root_name": row.get("root_name"),
                "priority": "high" if count >= 3 else "medium",
                "count": count,
                "summary": f"Code feedback reported {row.get('miss_category') or 'other'} misses.",
            }
        )
    gaps.extend(_code_benchmark_gaps(benchmark or {}))
    return gaps[:8]


def _code_benchmark_gaps(benchmark: dict[str, Any]) -> list[dict[str, Any]]:
    case_results = benchmark.get("case_results") if isinstance(benchmark.get("case_results"), list) else []
    grouped: dict[str, dict[str, Any]] = {}
    for case in case_results:
        if not isinstance(case, dict) or str(case.get("status") or "").lower() != "failed":
            continue
        category = str(case.get("category") or "").strip().lower().replace("-", "_")
        if not category.startswith("code_"):
            continue
        row = grouped.setdefault(category, {"count": 0, "reasons": set()})
        row["count"] += 1
        for reason in case.get("reasons") or []:
            safe_reason = _safe_gap_reason(reason)
            if safe_reason:
                row["reasons"].add(safe_reason)
    gaps: list[dict[str, Any]] = []
    for category in sorted(grouped):
        row = grouped[category]
        count = int(row.get("count") or 0)
        reasons = sorted(row.get("reasons") or [])
        human_category = category.replace("code_", "").replace("_", " ")
        reason_text = f" Reasons: {', '.join(reasons)}." if reasons else ""
        gaps.append(
            {
                "category": f"benchmark_{category}",
                "priority": "high",
                "count": count,
                "source": "retrieval_benchmark",
                "case_category": category,
                "reasons": reasons,
                "summary": f"Retrieval benchmark reported {count} failed {human_category} case{'s' if count != 1 else ''}.{reason_text}",
            }
        )
    return gaps


def _safe_gap_reason(value: Any) -> str:
    reason = str(value or "").strip().lower().replace("-", "_")
    allowed = {"top1_miss", "recall_miss", "brief_miss", "scope_miss", "suppression_miss"}
    return reason if reason in allowed else ""


def _benchmark_family_breakdown_for_assets(assets: list[Any]) -> dict[str, dict[str, int]]:
    breakdown: dict[str, dict[str, int]] = {}
    for asset in assets:
        family = job_family_for_type(f"corpus_extract_{asset.file_kind}")
        row = breakdown.setdefault(family, {"files": 0, "deferred": 0, "inline": 0, "metadata_only": 0})
        row["files"] += 1
        if asset.extraction_tier == "deferred":
            row["deferred"] += 1
        elif asset.extraction_tier == "inline":
            row["inline"] += 1
        else:
            row["metadata_only"] += 1
    return breakdown


def _benchmark_run_payload(
    *,
    recorded: dict[str, Any],
    fixture: str,
    mode: str,
    file_count: int,
    elapsed_ms: int,
    jobs_queued: int,
    jobs_completed: int,
    jobs_blocked: int,
    worker_family_breakdown: dict[str, Any],
    warm_state: str,
    pass_index: int,
    hash_parallelism: int,
    worker_count: int,
    manifest_skipped_unchanged: int = 0,
    cache_hits: int,
    cache_misses: int,
    metadata: dict[str, Any],
    scope_type: str = "synthetic",
    scope_hash: str | None = None,
    deployment_label: str | None = None,
    build_metadata: dict[str, Any] | None = None,
    settings_snapshot: dict[str, Any] | None = None,
    model_telemetry: dict[str, Any] | None = None,
    recommendation_metadata: dict[str, Any] | None = None,
) -> dict[str, Any]:
    return {
        "id": recorded.get("id"),
        "fixture": fixture,
        "mode": mode,
        "file_count": file_count,
        "elapsed_ms": elapsed_ms,
        "throughput_files_per_second": (file_count / (elapsed_ms / 1000.0)) if elapsed_ms else 0.0,
        "jobs_queued": jobs_queued,
        "jobs_completed": jobs_completed,
        "jobs_blocked": jobs_blocked,
        "worker_family_breakdown": worker_family_breakdown,
        "warm_state": warm_state,
        "pass_index": pass_index,
        "hash_parallelism": hash_parallelism,
        "worker_count": worker_count,
        "manifest_skipped_unchanged": manifest_skipped_unchanged,
        "cache_hits": cache_hits,
        "cache_misses": cache_misses,
        "metadata": metadata,
        "scope_type": scope_type,
        "scope_hash": scope_hash,
        "deployment_label": deployment_label,
        "build_metadata": build_metadata or {},
        "settings_snapshot": settings_snapshot or {},
        "model_telemetry": model_telemetry or {},
        "recommendation_metadata": recommendation_metadata or {},
    }


def _latest_model_telemetry(runs: list[dict[str, Any]]) -> dict[str, Any]:
    for run in reversed(runs):
        telemetry = run.get("model_telemetry")
        if isinstance(telemetry, dict) and telemetry:
            return telemetry
    return {}


def _benchmark_scenario_evidence(scenario: str) -> dict[str, Any]:
    if scenario != "reliability":
        return {}
    try:
        return {"file_churn": _benchmark_file_churn_probe()}
    except Exception as exc:
        return {"file_churn": {"probe_error": type(exc).__name__}}


def _benchmark_file_churn_probe() -> dict[str, Any]:
    with tempfile.TemporaryDirectory(prefix="flux-kb-file-churn-") as temp_dir:
        root = Path(temp_dir)
        large_bytes = 1024 * 1024
        (root / "large-write.bin").write_bytes(b"x" * large_bytes)
        rename_temp = root / "rename-save.tmp"
        rename_temp.write_text("rename-save draft", encoding="utf-8")
        rename_temp.rename(root / "rename-save.md")
        (root / "~$budget.xlsx").write_bytes(b"office temp")
        (root / "transient.tmp").write_text("partial", encoding="utf-8")
        pending = root / "pending-stable.md"
        pending.write_text("still changing", encoding="utf-8")

        pending_plan = scan_path(
            root,
            CorpusPolicy(
                root_path=root,
                stability_quiet_seconds=5.0,
                clock=lambda target=pending: target.stat().st_mtime + 1.0,
            ),
        )
        stable_plan = scan_path(root, CorpusPolicy(root_path=root, stability_quiet_seconds=0.0))
        manifest = {
            asset.relative_path: {
                "path": asset.relative_path,
                "size_bytes": asset.size_bytes,
                "mtime_ns": asset.mtime_ns,
                "quick_hash": asset.quick_hash,
                "content_hash": asset.content_hash,
            }
            for asset in stable_plan.assets
        }
        warm_plan = scan_path(
            root,
            CorpusPolicy(
                root_path=root,
                stability_quiet_seconds=0.0,
                manifest_lookup=lambda relative_path, store=manifest: store.get(relative_path),
            ),
        )
        stable_paths = {asset.relative_path for asset in stable_plan.assets}
        return {
            "large_write_bytes": large_bytes,
            "rename_save_detected": "rename-save.md" in stable_paths,
            "transient_skipped": int("~$budget.xlsx" not in stable_paths) + int("transient.tmp" not in stable_paths),
            "pending_stable_count": sum(1 for asset in pending_plan.assets if asset.extraction_status == "pending_stable"),
            "probe_warm_manifest_skips": sum(1 for asset in warm_plan.assets if asset.metadata.get("manifest_skipped_unchanged")),
        }


def _benchmark_recommendations(runs: list[dict[str, Any]]) -> dict[str, Any]:
    return build_benchmark_recommendations(
        scenario="standard",
        runs=runs,
        settings_snapshot=_benchmark_settings_snapshot(),
    )


def _normalize_benchmark_mode(value: str | None, *, allow_all: bool = False) -> str:
    normalized = str(value or "scan").strip().lower()
    allowed = {"scan", "soak", "watcher", "model"} | ({"all"} if allow_all else set())
    if normalized not in allowed:
        raise ValueError("benchmark mode must be scan, soak, watcher, model, or all")
    return normalized


def _normalize_benchmark_family(value: str | None) -> str:
    normalized = str(value or "all").strip().lower()
    if normalized != "all" and normalized not in JOB_FAMILIES:
        raise ValueError(f"benchmark family must be all or one of: {', '.join(JOB_FAMILIES)}")
    return normalized


def _write_benchmark_fixture(root: Path, fixture: str, files: int) -> None:
    root.mkdir(parents=True, exist_ok=True)
    writers = {
        "text-heavy": _write_text_fixture,
        "code-heavy": _write_code_fixture,
        "office-pdf-heavy": _write_office_pdf_fixture,
        "archive-container-heavy": _write_archive_fixture,
        "image-heavy": _write_image_fixture,
        "audio-video-heavy": _write_media_fixture,
    }
    writers[fixture](root, files)


def _write_text_fixture(root: Path, files: int) -> None:
    for index in range(files):
        (root / f"note-{index:04d}.md").write_text(
            f"# Synthetic note {index}\n\nThis benchmark fixture contains deterministic public-safe text.\n",
            encoding="utf-8",
        )


def _write_code_fixture(root: Path, files: int) -> None:
    service_source = "\n".join(
        [
            "from fastapi import APIRouter",
            "",
            "router = APIRouter()",
            "",
            "class OrderService:",
            "    def build_invoice(self, order_id: str) -> dict[str, str]:",
            "        return {'order_id': order_id, 'status': 'ready'}",
            "",
            "@router.get('/orders/{order_id}')",
            "def get_order(order_id: str):",
            "    service = OrderService()",
            "    return service.build_invoice(order_id)",
            "",
        ]
    )
    templates: list[tuple[str, str]] = [
        ("src/orders.py", service_source),
        (
            "tests/test_orders.py",
            "\n".join(
                [
                    "import pytest",
                    "from src.orders import OrderService",
                    "",
                    "@pytest.fixture",
                    "def order_service():",
                    "    return OrderService()",
                    "",
                    "def test_build_invoice_returns_ready_status(order_service):",
                    "    invoice = order_service.build_invoice('order-1')",
                    "    assert invoice['status'] == 'ready'",
                    "",
                ]
            ),
        ),
        (
            "web/routes.ts",
            "\n".join(
                [
                    "import express from 'express';",
                    "",
                    "const router = express.Router();",
                    "",
                    "function renderOrder(id) { return { id }; }",
                    "export const buildOrder = (req, res) => res.json(renderOrder(req.params.orderId));",
                    "router.post('/api/orders/:orderId', buildOrder);",
                    "",
                    "export function configureRoutes(app) {",
                    "  app.use(router);",
                    "}",
                    "",
                ]
            ),
        ),
        (
            "db/migrations/0001_create_orders.sql",
            "\n".join(
                [
                    "CREATE TABLE orders (id text primary key, status text not null);",
                    "CREATE INDEX idx_orders_status ON orders (status);",
                    "",
                ]
            ),
        ),
        ("pyproject.toml", "[project]\nname = 'synthetic-code-heavy'\nversion = '0.1.0'\n"),
        (
            "generated/client.py",
            "\n".join(
                [
                    "# Code generated by Flux benchmark. DO NOT EDIT.",
                    "",
                    "class GeneratedOrdersClient:",
                    "    def fetch_order(self, order_id: str):",
                    "        return {'order_id': order_id}",
                    "",
                ]
            ),
        ),
        ("src/broken.py", "def broken(:\n    return 'ops@example.com'\n"),
        ("src/unsupported.go", "package orders\n\nvar Broken =\n"),
        ("src/main.go", "package orders\n\nfunc BuildInvoice(id string) string {\n  return id\n}\n"),
        ("src/lib.rs", "pub fn build_invoice(id: &str) -> String {\n    id.to_string()\n}\n"),
        (
            "src/OrderService.cs",
            "\n".join(
                [
                    "namespace Synthetic.Orders;",
                    "public class OrderService {",
                    "  public string BuildInvoice(string id) { return id; }",
                    "}",
                    "",
                ]
            ),
        ),
        (
            "src/Controllers/OrdersController.cs",
            "\n".join(
                [
                    "using Microsoft.AspNetCore.Mvc;",
                    "",
                    "namespace Synthetic.Orders.Api;",
                    "",
                    "[ApiController]",
                    "[Route(\"api/orders\")]",
                    "public class OrdersController : ControllerBase",
                    "{",
                    "  private readonly OrderService _service;",
                    "  public OrdersController(OrderService service) { _service = service; }",
                    "",
                    "  [HttpGet(\"{orderId}\")]",
                    "  public ActionResult<string> GetOrder(string orderId)",
                    "  {",
                    "    return Ok(_service.BuildInvoice(orderId));",
                    "  }",
                    "}",
                    "",
                ]
            ),
        ),
        (
            "tests/OrderServiceTests.cs",
            "\n".join(
                [
                    "using Xunit;",
                    "using Synthetic.Orders;",
                    "",
                    "public class OrderServiceTests",
                    "{",
                    "  [Fact]",
                    "  public void BuildInvoice_returns_ready_status()",
                    "  {",
                    "    Assert.Equal(\"order-1\", new OrderService().BuildInvoice(\"order-1\"));",
                    "  }",
                    "}",
                    "",
                ]
            ),
        ),
        (
            "web/components/OrderCard.tsx",
            "\n".join(
                [
                    "import OrderStatus from './OrderStatus';",
                    "",
                    "export function OrderCard({ order }) {",
                    "  return <article className=\"order-card\"><OrderStatus status={order.status} /></article>;",
                    "}",
                    "",
                ]
            ),
        ),
        (
            "web/components/OrderCard.vue",
            "\n".join(
                [
                    "<template>",
                    "  <article class=\"order-card\" @click=\"selectOrder\">",
                    "    <OrderStatus :status=\"order.status\" />",
                    "    <form action=\"/orders/search\"><input id=\"order-search\" /></form>",
                    "  </article>",
                    "</template>",
                    "<script setup lang=\"ts\">",
                    "import OrderStatus from './OrderStatus.vue';",
                    "defineProps<{ order: Order }>();",
                    "function selectOrder() {}",
                    "</script>",
                    "",
                ]
            ),
        ),
        (
            "web/components/OrderPanel.svelte",
            "\n".join(
                [
                    "<script>",
                    "  import OrderCard from './OrderCard.svelte';",
                    "  export let orders = [];",
                    "</script>",
                    "<section class=\"order-panel\"><OrderCard order={orders[0]} /></section>",
                    "",
                ]
            ),
        ),
        (
            "web/pages/order-details.astro",
            "\n".join(
                [
                    "---",
                    "import OrderCard from '../components/OrderCard.vue';",
                    "const { order } = Astro.props;",
                    "---",
                    "<OrderCard order={order} />",
                    "",
                ]
            ),
        ),
        (
            "web/Pages/Orders.cshtml",
            "\n".join(
                [
                    "@page \"/orders\"",
                    "@model OrdersModel",
                    "<section class=\"orders-page\"><form action=\"/orders/search\"></form></section>",
                    "",
                ]
            ),
        ),
        (
            "web/index.html",
            "<main id=\"orders-app\" class=\"order-shell\"><form action=\"/orders/search\"></form></main>\n",
        ),
        (
            "web/styles/orders.module.scss",
            "\n".join(
                [
                    ":root { --status-color: #127a5b; }",
                    ".order-card { color: var(--status-color); }",
                    "#order-search { border: 1px solid currentColor; }",
                    "@keyframes orderPulse { from { opacity: 0; } to { opacity: 1; } }",
                    "@media (min-width: 640px) { .order-card { display: grid; } }",
                    "",
                ]
            ),
        ),
        (
            "web/styles/orders.css",
            ".orders-page { display: grid; }\n#orders-app { min-height: 100vh; }\n",
        ),
        (
            "tools/orders.ps1",
            "\n".join(
                [
                    "function Invoke-BuildInvoice {",
                    "  param($Id)",
                    "  return $Id",
                    "}",
                    "",
                ]
            ),
        ),
        ("duplicates/orders_copy.py", service_source),
        (
            "openapi.yaml",
            "\n".join(
                [
                    "openapi: 3.1.0",
                    "info:",
                    "  title: Synthetic Orders API",
                    "  version: 1.0.0",
                    "paths:",
                    "  /orders/{order_id}:",
                    "    get:",
                    "      operationId: getOrder",
                    "",
                ]
            ),
        ),
        (
            "notebooks/orders.ipynb",
            json.dumps(
                {
                    "cells": [
                        {"cell_type": "markdown", "source": ["# Synthetic notebook\n"]},
                        {"cell_type": "code", "source": ["from src.orders import OrderService\n", "OrderService().build_invoice('order-1')\n"]},
                    ],
                    "metadata": {},
                    "nbformat": 4,
                    "nbformat_minor": 5,
                },
                sort_keys=True,
            ),
        ),
    ]
    for index in range(files):
        relative_path, body = templates[index % len(templates)]
        if index >= len(templates):
            relative_path = f"extra/{index:04d}-{Path(relative_path).name}"
        target = root.joinpath(*relative_path.split("/"))
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(body, encoding="utf-8")


def _write_office_pdf_fixture(root: Path, files: int) -> None:
    extensions = [".pdf", ".docx", ".xlsx", ".pptx"]
    for index in range(files):
        (root / f"document-{index:04d}{extensions[index % len(extensions)]}").write_bytes(
            f"synthetic office/pdf fixture {index}".encode("utf-8")
        )


def _write_archive_fixture(root: Path, files: int) -> None:
    extensions = [".zip", ".tar", ".whl", ".jar"]
    for index in range(files):
        (root / f"package-{index:04d}{extensions[index % len(extensions)]}").write_bytes(b"PK synthetic container")


def _write_image_fixture(root: Path, files: int) -> None:
    png = base64.b64decode(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAIAAADZrBkAAAAAD0lEQVR4nGP8z8AARLJAgAEACPwDAaz3RyoAAAAASUVORK5CYII="
    )
    for index in range(files):
        (root / f"image-{index:04d}.png").write_bytes(png)


def _write_media_fixture(root: Path, files: int) -> None:
    extensions = [".mp3", ".wav", ".mp4", ".webm"]
    for index in range(files):
        (root / f"media-{index:04d}{extensions[index % len(extensions)]}").write_bytes(b"synthetic media")


def _benchmark_scope_descriptor(*, scope: str | None, root_name: str | None, path: str | None) -> dict[str, Any]:
    normalized = str(scope or "synthetic").strip().lower().replace("-", "_")
    if normalized in {"synthetic", "fixture", "fixtures"}:
        return {
            "scope_type": "synthetic",
            "scope_hash": None,
            "fixture": "synthetic",
            "scope_label": "Synthetic fixtures",
            "host_access": "temporary",
        }
    if normalized in {"root", "monitored_root"}:
        root = _select_root(root_name=root_name, path=path)
        return {
            "scope_type": "monitored_root",
            "scope_hash": _benchmark_scope_hash("monitored_root", str(root.get("name") or ""), str(root.get("root_path") or "")),
            "fixture": "monitored-root",
            "scope_label": str(root.get("name") or "monitored root"),
            "host_access": (root.get("metadata") or {}).get("host_access", "direct"),
            "root": root,
            "path": path,
        }
    if normalized == "path":
        if not path:
            raise ValueError("path benchmark scope requires path")
        root = _select_root(root_name=root_name, path=path)
        return {
            "scope_type": "path",
            "scope_hash": _benchmark_scope_hash("path", str(root.get("name") or ""), str(path)),
            "fixture": "monitored-path",
            "scope_label": str(root.get("name") or "monitored path"),
            "host_access": (root.get("metadata") or {}).get("host_access", "direct"),
            "root": root,
            "path": path,
        }
    raise ValueError("benchmark scope must be synthetic, root, or path")


def _benchmark_scope_hash(*parts: str) -> str:
    digest = hashlib.sha256("\n".join(parts).encode("utf-8", errors="ignore")).hexdigest()
    return f"sha256:{digest}"


def _benchmark_record_fields(
    *,
    scope_descriptor: dict[str, Any],
    deployment_label: str | None,
    model_telemetry: dict[str, Any] | None = None,
    recommendation_metadata: dict[str, Any] | None = None,
) -> dict[str, Any]:
    return {
        "scope_type": scope_descriptor["scope_type"],
        "scope_hash": scope_descriptor.get("scope_hash"),
        "deployment_label": deployment_label,
        "build_metadata": {"version": __version__},
        "settings_snapshot": _benchmark_settings_snapshot(),
        "model_telemetry": model_telemetry or {},
        "recommendation_metadata": recommendation_metadata or {"settings_mutated": False},
    }


def _benchmark_settings_snapshot() -> dict[str, Any]:
    return {
        "hash_parallelism": _configured_hash_parallelism(),
        "worker_caps": _configured_worker_caps(),
        "watcher_backend": _configured_watcher_backend(),
    }


def _benchmark_model_telemetry() -> dict[str, Any]:
    status = collect_acceleration_status()
    availability = extractor_availability()
    tool_names = ("paddleocr", "pdftoppm", "ffprobe", "ffmpeg", "faster_whisper")
    tools: dict[str, dict[str, Any]] = {}
    blocked = 0
    for name in tool_names:
        item = availability.get(name)
        if not isinstance(item, dict):
            continue
        ok = bool(item.get("ok"))
        tools[name] = {"ok": ok, "message": str(item.get("message") or "")[:200]}
        if not ok:
            blocked += 1
    capabilities = status.get("capabilities") if isinstance(status, dict) else {}
    local_model = capabilities.get("local_model") if isinstance(capabilities, dict) else {}
    local_model_payload = {
        key: value
        for key, value in (local_model or {}).items()
        if key in {"ok", "state", "provider", "models", "message"}
    }
    return {
        "local_model": local_model_payload,
        "tools": tools,
        "blocked_dependency_count": blocked,
    }


def _configured_glob_policy(root: dict[str, Any]) -> dict[str, Any]:
    settings = SettingsService()
    try:
        global_include = settings.resolve("crawler.global_include_globs").raw_value
    except Exception:
        global_include = []
    try:
        global_exclude = settings.resolve("crawler.global_exclude_globs").raw_value
    except Exception:
        global_exclude = []
    return effective_glob_policy(root, global_include=global_include, global_exclude=global_exclude)


def _path_is_under_root(path: str, root_path: str) -> bool:
    if _looks_windows(path) or _looks_windows(root_path):
        target = PureWindowsPath(path)
        root = PureWindowsPath(root_path)
        try:
            target.relative_to(root)
            return True
        except ValueError:
            return False
    target_posix = PurePosixPath(path)
    root_posix = PurePosixPath(root_path)
    try:
        target_posix.relative_to(root_posix)
        return True
    except ValueError:
        pass
    try:
        target_local = Path(path).expanduser().resolve()
        root_local = Path(root_path).expanduser().resolve()
        return target_local == root_local or target_local.is_relative_to(root_local)
    except Exception:
        return False


def _looks_windows(path: str) -> bool:
    return bool(PureWindowsPath(path).drive) or str(path).startswith("\\\\")


def _root_matches_host_agent_filter(root: dict[str, Any], host_agent_roots: bool | None) -> bool:
    if host_agent_roots is None:
        return True
    metadata = root.get("metadata") or {}
    is_host_root = metadata.get("host_access") == "host_agent" or _looks_windows(str(root.get("root_path") or ""))
    return is_host_root is host_agent_roots


def _load_watch_roots(root_name: str | None = None) -> list[WatchRoot]:
    roots = [
        root
        for root in database.list_monitored_roots(watch_enabled=True)
        if root["enabled"] and (root_name is None or root["name"] == root_name)
    ]
    watch_roots: list[WatchRoot] = []
    for root in roots:
        glob_policy = _configured_glob_policy(root)
        watch_roots.append(
            WatchRoot(
                name=root["name"],
                root_path=Path(root["root_path"]),
                watch_enabled=root["watch_enabled"],
                recursive=root["recursive"],
                include_globs=tuple(glob_policy["include_globs"]),
                exclude_globs=tuple(glob_policy["exclude_globs"]),
            )
        )
    return watch_roots
