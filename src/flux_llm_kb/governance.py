from __future__ import annotations

from collections import Counter
from pathlib import Path
from typing import Any


LOW_RISK_AUTO_ACTIONS = {"mark_review", "stale_tag", "deprioritize"}

DEFAULT_POLICY: dict[str, Any] = {
    "mode": "shadow",
    "min_shadow_precision": 0.8,
    "auto_apply_enabled": False,
    "auto_apply_risk_ceiling": "low",
    "max_actions_per_run": 25,
    "digest_retention_days": 30,
    "protected_memory_rules": {
        "protect_metadata_flag": True,
        "protect_confirmed_confidence": 0.85,
        "protect_reinforced_confidence": 0.75,
        "protect_active_capture_review": True,
    },
    "local_model_rationale_enabled": False,
    "local_model_rationale_model": "",
}


def normalized_policy(policy: dict[str, Any] | None = None) -> dict[str, Any]:
    merged = {**DEFAULT_POLICY, **(policy or {})}
    merged["min_shadow_precision"] = _bounded_float(merged.get("min_shadow_precision"), 0.8, minimum=0.0, maximum=1.0)
    merged["max_actions_per_run"] = max(1, min(int(merged.get("max_actions_per_run") or 25), 200))
    merged["auto_apply_enabled"] = bool(merged.get("auto_apply_enabled"))
    merged["settings_mutated"] = False
    return _sanitize(merged)


def evaluate_governance_gate(benchmark_runs: list[dict[str, Any]] | None, *, policy: dict[str, Any] | None = None) -> dict[str, Any]:
    resolved = normalized_policy(policy)
    summary = _latest_governance_shadow_summary(benchmark_runs or [])
    reasons: list[str] = []
    if not summary:
        reasons.append("governance_shadow_not_run")
        precision = 0.0
        guardrail_failures = 0
    else:
        precision = _bounded_float(summary.get("proposal_precision"), 0.0, minimum=0.0, maximum=1.0)
        guardrail_failures = int(summary.get("guardrail_fail_count") or 0)
        if precision < float(resolved["min_shadow_precision"]):
            reasons.append("proposal_precision_below_threshold")
        if guardrail_failures > 0:
            reasons.append("guardrail_failures")
    return {
        "settings_mutated": False,
        "status": "ready" if not reasons else "blocked",
        "apply_allowed": not reasons,
        "reasons": reasons,
        "min_shadow_precision": resolved["min_shadow_precision"],
        "proposal_precision": precision,
        "guardrail_fail_count": guardrail_failures,
        "shadow_summary": summary or {},
    }


def build_governance_proposals(
    *,
    quality_report: dict[str, Any],
    benchmark_runs: list[dict[str, Any]] | None = None,
    capture_jobs: list[dict[str, Any]] | None = None,
    code_feedback: dict[str, Any] | None = None,
    semantic_clusters: dict[str, Any] | None = None,
    existing_actions: list[dict[str, Any]] | None = None,
    policy: dict[str, Any] | None = None,
) -> dict[str, Any]:
    resolved = normalized_policy(policy)
    gate = evaluate_governance_gate(benchmark_runs or [], policy=resolved)
    existing_keys = {
        _proposal_key(item)
        for item in (existing_actions or [])
        if str(item.get("status") or "") in {"proposed", "pending", "blocked", "applied"}
    }
    seen: set[tuple[str, str, str]] = set()
    actions: list[dict[str, Any]] = []
    deduplicated = 0

    for candidate in quality_report.get("candidates") or []:
        action = _action_from_quality_candidate(candidate, gate=gate, policy=resolved)
        if not action:
            continue
        key = _proposal_key(action)
        if key in existing_keys or key in seen:
            deduplicated += 1
            continue
        seen.add(key)
        actions.append(action)

    for job in capture_jobs or []:
        action = _action_from_capture_job(job, gate=gate, policy=resolved)
        if not action:
            continue
        key = _proposal_key(action)
        if key in existing_keys or key in seen:
            deduplicated += 1
            continue
        seen.add(key)
        actions.append(action)

    for row in _feedback_rows(code_feedback or {}):
        action = _action_from_feedback_row(row, gate=gate, policy=resolved)
        if not action:
            continue
        key = _proposal_key(action)
        if key in existing_keys or key in seen:
            deduplicated += 1
            continue
        seen.add(key)
        actions.append(action)

    for cluster in _semantic_cluster_rows(semantic_clusters or {}):
        for action in _actions_from_semantic_cluster(cluster, gate=gate, policy=resolved):
            key = _proposal_key(action)
            if key in existing_keys or key in seen:
                deduplicated += 1
                continue
            seen.add(key)
            actions.append(action)

    actions = actions[: int(resolved["max_actions_per_run"])]
    summary = _summary(actions)
    summary["deduplicated"] = deduplicated
    summary["blocked"] = sum(1 for item in actions if item["status"] == "blocked")
    return {
        "settings_mutated": False,
        "memory_mutated": False,
        "policy": resolved,
        "gate": gate,
        "summary": summary,
        "actions": actions,
    }


def build_governance_digest(
    *,
    run: dict[str, Any],
    actions: list[dict[str, Any]],
    gate: dict[str, Any],
) -> dict[str, Any]:
    recoverable = [item for item in actions if item.get("status") == "applied"]
    high_risk = [item for item in actions if item.get("risk") == "high"]
    blocked = [item for item in actions if item.get("status") == "blocked"]
    recommendations = []
    if gate.get("status") != "ready":
        recommendations.append({"action": "run_governance_shadow_benchmark", "reason": ",".join(gate.get("reasons") or [])})
    if high_risk:
        recommendations.append({"action": "review_high_risk_governance", "count": len(high_risk)})
    if blocked:
        recommendations.append({"action": "inspect_blocked_governance", "count": len(blocked)})
    return {
        "settings_mutated": False,
        "memory_mutated": False,
        "run_id": run.get("id"),
        "summary": {
            "new_proposals": sum(1 for item in actions if item.get("status") == "proposed"),
            "blocked_proposals": len(blocked),
            "recoverable_actions": len(recoverable),
            "high_risk": len(high_risk),
            "gate_status": gate.get("status"),
        },
        "recommendations": recommendations[:10],
    }


def _action_from_quality_candidate(candidate: dict[str, Any], *, gate: dict[str, Any], policy: dict[str, Any]) -> dict[str, Any] | None:
    memory_class = str(candidate.get("memory_class") or "").strip().lower()
    target_id = str(candidate.get("id") or "").strip()
    if not memory_class or not target_id:
        return None
    bucket = str(candidate.get("quality_bucket") or "").strip().lower()
    reason = str(candidate.get("reason") or "").strip().lower()
    lifecycle_state = str(candidate.get("lifecycle_state") or "").strip().lower()
    confidence = _maybe_float(candidate.get("confidence"))
    protected = _is_protected_candidate(candidate, policy=policy)
    if protected and bucket in {"", "healthy", "keep"}:
        return None
    if bucket in {"", "healthy", "keep"} and lifecycle_state in {"", "active", "confirmed", "reinforced"}:
        return None
    if reason in {"semantic_near_duplicate", "semantic_duplicate"}:
        action = "semantic_cluster_apply"
        risk = "medium"
    elif bucket == "retire" or reason == "retired" or lifecycle_state == "retired":
        action = "retire"
        risk = "high"
    elif reason in {"contradiction", "contradicted"} or lifecycle_state == "contradicted":
        action = "mark_review"
        risk = "high"
    elif bucket == "deprioritize" or "deprioritize" in reason:
        action = "deprioritize"
        risk = "low" if memory_class == "claim" else "medium"
    elif lifecycle_state == "stale" or reason == "stale":
        action = "stale_tag"
        risk = "low"
    else:
        action = "mark_review"
        risk = "medium"
    status = "blocked" if protected else "proposed"
    evidence: dict[str, Any] = {
        "quality_bucket": bucket or None,
        "reason": reason or None,
        "lifecycle_state": lifecycle_state or None,
        "confidence": confidence,
    }
    metadata = candidate.get("metadata") if isinstance(candidate.get("metadata"), dict) else {}
    if metadata.get("root_name"):
        evidence["root_name"] = str(metadata.get("root_name"))[:120]
    if isinstance(metadata.get("suppressed_count"), int):
        evidence["suppressed_count"] = int(metadata["suppressed_count"])
    return _proposal(
        action=action,
        target_type="semantic_cluster" if action == "semantic_cluster_apply" else memory_class,
        target_id=target_id,
        memory_class=memory_class,
        risk=risk,
        source="retention_quality",
        status=status,
        evidence=evidence,
        rationale_reason=reason or bucket or lifecycle_state or action,
        gate=gate,
        protected=protected,
        before_state={
            "lifecycle_state": lifecycle_state or None,
            "retention_action": candidate.get("retention_action") or "keep",
            "confidence": confidence,
        },
    )


def _action_from_capture_job(job: dict[str, Any], *, gate: dict[str, Any], policy: dict[str, Any]) -> dict[str, Any] | None:
    payload = job.get("payload") if isinstance(job.get("payload"), dict) else {}
    ingestion = payload.get("ingestion") if isinstance(payload.get("ingestion"), dict) else {}
    status = str(payload.get("status") or job.get("status") or "").lower()
    ingestion_status = str(ingestion.get("status") or "").lower()
    if status not in {"failed", "blocked_missing_dependency"} and ingestion_status not in {"failed", "blocked_missing_dependency", "skipped"}:
        return None
    path_leaf = _path_leaf(str(payload.get("path") or ""))
    return _proposal(
        action="capture_ingestion_recheck",
        target_type="capture_job",
        target_id=str(job.get("id") or ""),
        memory_class=None,
        risk="medium",
        source="capture_ingestion",
        status="proposed",
        evidence={"job_type": job.get("job_type"), "job_status": status, "ingestion_status": ingestion_status, "source_leaf": path_leaf},
        rationale_reason="approved capture ingestion needs recheck",
        gate=gate,
        protected=False,
    )


def _action_from_feedback_row(row: dict[str, Any], *, gate: dict[str, Any], policy: dict[str, Any]) -> dict[str, Any] | None:
    count = int(row.get("count") or row.get("event_count") or 0)
    if count <= 0:
        return None
    category = str(row.get("miss_category") or "other")
    root_name = str(row.get("root_name") or "global")
    target_id = f"{root_name}:{category}"
    return _proposal(
        action="feedback_gap_escalate",
        target_type="code_feedback",
        target_id=target_id,
        memory_class=None,
        risk="medium",
        source="code_feedback",
        status="proposed",
        evidence={"miss_category": category, "count": count, "root_name": root_name},
        rationale_reason="repeated code retrieval feedback gap",
        gate=gate,
        protected=False,
    )


def _actions_from_semantic_cluster(cluster: dict[str, Any], *, gate: dict[str, Any], policy: dict[str, Any]) -> list[dict[str, Any]]:
    cluster_id = str(cluster.get("id") or "").strip()
    if not cluster_id or str(cluster.get("status") or "active").lower() != "active":
        return []
    suppressed_count = int(cluster.get("suppressed_count") or 0)
    members = cluster.get("members") if isinstance(cluster.get("members"), list) else []
    if suppressed_count <= 0 and len(members) < 2:
        return []
    memory_class = str(cluster.get("memory_class") or "").strip().lower() or None
    evidence = {
        "suppressed_count": suppressed_count,
        "member_count": len(members),
        "root_name": cluster.get("root_name"),
        "threshold": cluster.get("threshold"),
        "canonical_owner_table": cluster.get("canonical_owner_table"),
        "canonical_owner_id": cluster.get("canonical_owner_id"),
    }
    apply_action = _proposal(
        action="semantic_cluster_apply",
        target_type="semantic_cluster",
        target_id=cluster_id,
        memory_class=memory_class,
        risk="medium",
        source="semantic_duplicate_cluster",
        status="proposed",
        evidence=evidence,
        rationale_reason="active semantic duplicate cluster can be applied to presentation",
        gate=gate,
        protected=False,
        before_state={
            "status": cluster.get("status"),
            "canonical_owner_id": cluster.get("canonical_owner_id"),
            "suppressed_count": suppressed_count,
        },
    )
    promote_action = _proposal(
        action="canonical_cluster_promote",
        target_type="semantic_cluster",
        target_id=cluster_id,
        memory_class=memory_class,
        risk="high" if memory_class == "claim" else "medium",
        source="semantic_duplicate_cluster",
        status="proposed",
        evidence=evidence,
        rationale_reason="canonical cluster promotion requires manual review",
        gate=gate,
        protected=False,
        before_state={
            "status": cluster.get("status"),
            "canonical_owner_id": cluster.get("canonical_owner_id"),
            "suppressed_count": suppressed_count,
        },
    )
    return [apply_action, promote_action]


def _proposal(
    *,
    action: str,
    target_type: str,
    target_id: str,
    memory_class: str | None,
    risk: str,
    source: str,
    status: str,
    evidence: dict[str, Any],
    rationale_reason: str,
    gate: dict[str, Any],
    protected: bool,
    before_state: dict[str, Any] | None = None,
) -> dict[str, Any]:
    apply_allowed = bool(gate.get("apply_allowed")) and not protected
    if action not in LOW_RISK_AUTO_ACTIONS:
        apply_allowed = False
    rationale = {
        "summary": rationale_reason,
        "guardrails": {
            "gate_status": gate.get("status"),
            "apply_allowed": apply_allowed,
            "protected": protected,
            "requires_manual_apply": action not in LOW_RISK_AUTO_ACTIONS or risk != "low",
        },
        "settings_mutated": False,
    }
    return {
        "action": action,
        "target_type": target_type,
        "target_id": target_id,
        "memory_class": memory_class,
        "risk": risk,
        "status": status,
        "source": source,
        "rationale": _sanitize(rationale),
        "evidence": _sanitize(evidence),
        "before_state": _sanitize(before_state or {}),
        "after_state": {},
        "settings_mutated": False,
        "memory_mutated": False,
    }


def _summary(actions: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "total": len(actions),
        "by_action": dict(Counter(str(item.get("action") or "unknown") for item in actions)),
        "by_risk": dict(Counter(str(item.get("risk") or "medium") for item in actions)),
        "by_status": dict(Counter(str(item.get("status") or "proposed") for item in actions)),
        "by_source": dict(Counter(str(item.get("source") or "governance") for item in actions)),
    }


def _latest_governance_shadow_summary(runs: list[dict[str, Any]]) -> dict[str, Any] | None:
    for run in runs:
        recommendation = run.get("recommendation_metadata") if isinstance(run.get("recommendation_metadata"), dict) else {}
        direct = run.get("recommendations") if isinstance(run.get("recommendations"), dict) else {}
        metadata = run.get("metadata") if isinstance(run.get("metadata"), dict) else {}
        for source in (recommendation, direct, metadata):
            summary = source.get("governance_shadow") if isinstance(source.get("governance_shadow"), dict) else None
            if summary:
                return _sanitize(summary)
    return None


def _feedback_rows(payload: dict[str, Any]) -> list[dict[str, Any]]:
    rows = payload.get("rows") if isinstance(payload.get("rows"), list) else None
    if rows is None:
        rows = payload.get("gaps") if isinstance(payload.get("gaps"), list) else []
    return [item for item in rows if isinstance(item, dict)]


def _semantic_cluster_rows(payload: dict[str, Any]) -> list[dict[str, Any]]:
    clusters = payload.get("clusters") if isinstance(payload.get("clusters"), list) else []
    return [item for item in clusters if isinstance(item, dict)]


def _proposal_key(item: dict[str, Any]) -> tuple[str, str, str]:
    return (
        str(item.get("target_type") or item.get("memory_class") or ""),
        str(item.get("target_id") or ""),
        str(item.get("action") or ""),
    )


def _is_protected_candidate(candidate: dict[str, Any], *, policy: dict[str, Any]) -> bool:
    metadata = candidate.get("metadata") if isinstance(candidate.get("metadata"), dict) else {}
    if bool(metadata.get("protected")):
        return True
    benchmark_tag = str(metadata.get("benchmark_tag") or "").lower()
    if "governance-current" in benchmark_tag or "guardrail" in benchmark_tag:
        return True
    confidence = _maybe_float(candidate.get("confidence"))
    lifecycle_state = str(candidate.get("lifecycle_state") or "").lower()
    rules = policy.get("protected_memory_rules") if isinstance(policy.get("protected_memory_rules"), dict) else {}
    if lifecycle_state == "confirmed" and confidence is not None and confidence >= float(rules.get("protect_confirmed_confidence") or 0.85):
        return True
    if lifecycle_state == "reinforced" and confidence is not None and confidence >= float(rules.get("protect_reinforced_confidence") or 0.75):
        return True
    return False


def _sanitize(value: Any) -> Any:
    if value is None or isinstance(value, (bool, int, float)):
        return value
    if isinstance(value, str):
        return value[:200]
    if isinstance(value, list):
        rows = [_sanitize(item) for item in value[:50]]
        return [item for item in rows if item is not None]
    if isinstance(value, dict):
        sanitized: dict[str, Any] = {}
        for key, item in value.items():
            key_text = str(key)
            lowered = key_text.lower()
            if lowered in {"raw", "body", "content", "text", "prompt", "query", "snippet", "error"}:
                continue
            if "private_path" in lowered or "embedding" in lowered or "secret" in lowered or "token" in lowered:
                continue
            if lowered in {"path", "source_path", "file_path"}:
                leaf = _path_leaf(str(item or ""))
                if leaf:
                    sanitized["source_leaf"] = leaf
                continue
            sanitized_item = _sanitize(item)
            if sanitized_item is not None:
                sanitized[key_text] = sanitized_item
        return sanitized
    return str(value)[:200]


def _path_leaf(value: str) -> str:
    if not value:
        return ""
    return Path(value.replace("\\", "/")).name


def _maybe_float(value: Any) -> float | None:
    if isinstance(value, (int, float)):
        return round(float(value), 6)
    return None


def _bounded_float(value: Any, default: float, *, minimum: float, maximum: float) -> float:
    if not isinstance(value, (int, float)):
        return default
    return max(minimum, min(float(value), maximum))
