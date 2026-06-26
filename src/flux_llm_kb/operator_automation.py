from __future__ import annotations

from typing import Any


ALLOWED_ACTIONS = [
    "refresh_retrieval_evidence",
    "ingest_approved_capture",
    "safe_diagnostic_recovery",
    "enqueue_embedding_refresh",
    "run_governance_shadow",
]

MANUAL_ACTIONS = [
    "delete",
    "destructive_mail_policy",
    "oauth",
    "host_startup",
    "restart",
    "reindex_settings",
    "capture_approve_reject",
    "high_risk_governance",
    "file_open_reveal",
    "ambiguous_action",
]

DEFAULT_POLICY: dict[str, Any] = {
    "enabled": False,
    "mode": "guarded",
    "interval_seconds": 1800,
    "evidence_freshness_hours": 336,
    "max_actions_per_run": 25,
    "auto_refresh_evidence": True,
    "auto_ingest_approved_capture": True,
    "auto_remediate_diagnostics": True,
    "auto_refresh_embeddings": True,
    "auto_run_governance_shadow": True,
}


def normalized_policy(settings: dict[str, Any] | None) -> dict[str, Any]:
    raw = {**DEFAULT_POLICY, **(settings or {})}
    mode = str(raw.get("mode") or "guarded").strip().lower().replace("-", "_")
    if mode not in {"guarded", "suggest_only"}:
        mode = "guarded"
    return {
        "enabled": bool(raw.get("enabled")),
        "mode": mode,
        "interval_seconds": _bounded_int(raw.get("interval_seconds"), 60, 86_400, DEFAULT_POLICY["interval_seconds"]),
        "evidence_freshness_hours": _bounded_int(raw.get("evidence_freshness_hours"), 1, 8_760, DEFAULT_POLICY["evidence_freshness_hours"]),
        "max_actions_per_run": _bounded_int(raw.get("max_actions_per_run"), 1, 200, DEFAULT_POLICY["max_actions_per_run"]),
        "auto_refresh_evidence": bool(raw.get("auto_refresh_evidence")),
        "auto_ingest_approved_capture": bool(raw.get("auto_ingest_approved_capture")),
        "auto_remediate_diagnostics": bool(raw.get("auto_remediate_diagnostics")),
        "auto_refresh_embeddings": bool(raw.get("auto_refresh_embeddings")),
        "auto_run_governance_shadow": bool(raw.get("auto_run_governance_shadow")),
        "allowed_actions": list(ALLOWED_ACTIONS),
        "manual_actions": list(MANUAL_ACTIONS),
        "settings_mutated": False,
    }


def guarded_action_labels() -> dict[str, str]:
    return {
        "refresh_retrieval_evidence": "Refresh retrieval evidence",
        "ingest_approved_capture": "Ingest approved captures",
        "safe_diagnostic_recovery": "Run safe diagnostic recovery",
        "enqueue_embedding_refresh": "Queue embedding refresh",
        "run_governance_shadow": "Run governance shadow proposals",
    }


def manual_required_items() -> list[dict[str, str]]:
    return [
        {"action": "delete", "label": "Delete or purge data", "reason": "Destructive actions require explicit operator confirmation."},
        {"action": "destructive_mail_policy", "label": "Destructive mail policies", "reason": "Trash, delete, and expunge policies must stay opt-in and confirmation-gated."},
        {"action": "oauth", "label": "OAuth setup", "reason": "OAuth consent must happen in a browser under the operator account."},
        {"action": "host_startup", "label": "Host startup", "reason": "Starting host-only processes depends on the logged-in desktop session."},
        {"action": "restart", "label": "Restart services", "reason": "Restarting API, worker, or host services can interrupt active work."},
        {"action": "reindex_settings", "label": "Restart or reindex settings", "reason": "Settings that change indexing behavior require manual review before apply."},
        {"action": "capture_approve_reject", "label": "Capture approve/reject", "reason": "Capture review is the human safety gate before memory ingestion."},
        {"action": "high_risk_governance", "label": "High-risk governance", "reason": "Contradiction, retirement, and ambiguous memory changes must remain manual."},
        {"action": "file_open_reveal", "label": "Open or reveal files", "reason": "Opening local files can disclose private context outside the dashboard."},
        {"action": "ambiguous_action", "label": "Ambiguous actions", "reason": "Flux pauses when intent, target, or safety impact is unclear."},
    ]


def _bounded_int(value: Any, minimum: int, maximum: int, default: int) -> int:
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        return int(default)
    return max(minimum, min(parsed, maximum))
