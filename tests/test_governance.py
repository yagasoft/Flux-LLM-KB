from __future__ import annotations

import json
from datetime import datetime, timezone

import pytest

from flux_llm_kb import database, governance
from flux_llm_kb import service as service_module
from flux_llm_kb.service import KnowledgeService


def test_governance_proposals_are_sanitized_deduplicated_and_gate_checked():
    quality = {
        "candidates": [
            {
                "id": "claim-stale",
                "memory_class": "claim",
                "quality_bucket": "review",
                "reason": "stale",
                "label": "Private claim text must not leak",
                "confidence": 0.24,
                "lifecycle_state": "stale",
                "metadata": {"path": "E:/Private/customer-plan.md"},
            },
            {
                "id": "cluster-1",
                "memory_class": "corpus",
                "quality_bucket": "deprioritize",
                "reason": "semantic_near_duplicate",
                "label": "Semantic duplicates: Architecture",
                "metadata": {"root_name": "docs", "suppressed_count": 3},
            },
            {
                "id": "claim-current",
                "memory_class": "claim",
                "quality_bucket": "healthy",
                "reason": "current",
                "confidence": 0.95,
                "lifecycle_state": "confirmed",
                "metadata": {"protected": True},
            },
        ]
    }
    benchmark_runs = [
        {
            "suite": "governance-shadow",
            "recommendation_metadata": {
                "settings_mutated": False,
                "governance_shadow": {
                    "proposal_precision": 0.9,
                    "guardrail_case_count": 2,
                    "guardrail_pass_count": 2,
                    "guardrail_fail_count": 0,
                },
            },
        }
    ]
    existing_actions = [
        {
            "target_type": "claim",
            "target_id": "claim-stale",
            "action": "stale_tag",
            "status": "pending",
        }
    ]

    payload = governance.build_governance_proposals(
        quality_report=quality,
        benchmark_runs=benchmark_runs,
        capture_jobs=[
            {
                "id": "job-failed",
                "job_type": "codex_backfill",
                "status": "failed",
                "payload": {
                    "status": "failed",
                    "path": "E:/Private/session.json",
                    "ingestion": {"status": "failed", "error": "raw private failure text"},
                },
            }
        ],
        code_feedback={"rows": [{"miss_category": "missing_symbol", "count": 4, "root_name": "repo"}]},
        existing_actions=existing_actions,
        policy={"min_shadow_precision": 0.8},
    )

    assert payload["settings_mutated"] is False
    assert payload["gate"]["status"] == "ready"
    assert payload["summary"]["deduplicated"] == 1
    assert payload["summary"]["by_action"]["semantic_cluster_apply"] == 1
    assert payload["summary"]["by_action"]["capture_ingestion_recheck"] == 1
    assert payload["summary"]["by_action"]["feedback_gap_escalate"] == 1
    assert all(action["status"] == "proposed" for action in payload["actions"])
    assert all("rationale" in action and "guardrails" in action["rationale"] for action in payload["actions"])
    serialized = json.dumps(payload)
    assert "Private claim text" not in serialized
    assert "customer-plan" not in serialized
    assert "session.json" in serialized
    assert "raw private failure text" not in serialized


def test_governance_gate_blocks_apply_when_shadow_precision_or_guardrails_fail():
    gate = governance.evaluate_governance_gate(
        [
            {
                "suite": "governance-shadow",
                "recommendation_metadata": {
                    "settings_mutated": False,
                    "governance_shadow": {
                        "proposal_precision": 0.75,
                        "guardrail_case_count": 2,
                        "guardrail_pass_count": 1,
                        "guardrail_fail_count": 1,
                    },
                },
            }
        ],
        policy={"min_shadow_precision": 0.8},
    )

    assert gate["status"] == "blocked"
    assert gate["settings_mutated"] is False
    assert "proposal_precision_below_threshold" in gate["reasons"]
    assert "guardrail_failures" in gate["reasons"]


def test_database_records_governance_run_action_and_digest(monkeypatch):
    executed: list[tuple[str, tuple[object, ...]]] = []
    timestamp = datetime(2026, 6, 26, 10, 0, tzinfo=timezone.utc)

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, tuple(params or ())))

        def fetchone(self):
            sql, params = executed[-1]
            if "INSERT INTO memory_governance_runs" in sql:
                return ("run-1", timestamp)
            if "INSERT INTO memory_governance_actions" in sql:
                return ("action-1", timestamp)
            if "INSERT INTO memory_governance_digests" in sql:
                return ("digest-1", timestamp)
            raise AssertionError(sql)

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    run = database.record_memory_governance_run(
        mode="shadow",
        trigger="manual",
        status="completed",
        policy_snapshot={"min_shadow_precision": 0.8, "private_path": "E:/Private"},
        gate={"status": "ready"},
        summary={"total": 1},
        actor="tester",
    )
    action = database.record_memory_governance_action(
        run_id=run["id"],
        action="stale_tag",
        target_type="claim",
        target_id="claim-1",
        memory_class="claim",
        risk="low",
        status="proposed",
        source="retention_quality",
        rationale={"summary": "stale evidence", "raw": "Private claim text"},
        evidence={"path": "E:/Private/customer.md", "source_leaf": "customer.md"},
        before_state={"lifecycle_state": "active"},
        after_state={},
        actor="tester",
    )
    digest = database.record_memory_governance_digest(
        run_id=run["id"],
        summary={"new_proposals": 1},
        recommendations=[{"action": "review", "raw": "Private claim text"}],
        actor="tester",
    )

    assert run["id"] == "run-1"
    assert action["id"] == "action-1"
    assert digest["id"] == "digest-1"
    assert any("INSERT INTO memory_governance_policy_snapshots" in sql for sql, _params in executed)
    serialized_params = json.dumps([params for _, params in executed], default=str)
    assert "Private claim text" not in serialized_params
    assert "E:/Private/customer.md" not in serialized_params
    assert "customer.md" in serialized_params


def test_governance_proposals_include_semantic_cluster_and_protected_blocks():
    payload = governance.build_governance_proposals(
        quality_report={
            "candidates": [
                {
                    "id": "claim-guardrail",
                    "memory_class": "claim",
                    "quality_bucket": "review",
                    "reason": "stale",
                    "confidence": 0.93,
                    "lifecycle_state": "confirmed",
                    "metadata": {"benchmark_tag": "governance-shadow:governance-current-guardrail"},
                }
            ]
        },
        benchmark_runs=[
            {
                "recommendation_metadata": {
                    "governance_shadow": {"proposal_precision": 0.93, "guardrail_fail_count": 0},
                    "settings_mutated": False,
                }
            }
        ],
        semantic_clusters={
            "clusters": [
                {
                    "id": "cluster-1",
                    "memory_class": "claim",
                    "status": "active",
                    "threshold": 0.86,
                    "root_name": "repo",
                    "canonical_owner_table": "claims",
                    "canonical_owner_id": "claim-canonical",
                    "suppressed_count": 2,
                    "members": [
                        {"owner_id": "claim-canonical", "member_role": "canonical"},
                        {"owner_id": "claim-copy", "member_role": "duplicate"},
                    ],
                }
            ]
        },
        policy={"min_shadow_precision": 0.8},
    )

    assert payload["summary"]["by_action"]["semantic_cluster_apply"] == 1
    assert payload["summary"]["by_action"]["canonical_cluster_promote"] == 1
    assert payload["summary"]["blocked"] == 1
    blocked = next(action for action in payload["actions"] if action["target_id"] == "claim-guardrail")
    assert blocked["status"] == "blocked"
    assert blocked["rationale"]["guardrails"]["protected"] is True
    canonical = next(action for action in payload["actions"] if action["action"] == "canonical_cluster_promote")
    assert canonical["rationale"]["guardrails"]["requires_manual_apply"] is True


def test_service_governance_run_persists_actions_digest_and_honors_shadow_default(monkeypatch):
    recorded_runs = []
    recorded_actions = []
    recorded_digests = []

    monkeypatch.setattr(
        database,
        "retention_quality_report",
        lambda limit=25: {
            "candidates": [
                {
                    "id": "claim-stale",
                    "memory_class": "claim",
                    "quality_bucket": "review",
                    "reason": "stale",
                    "confidence": 0.2,
                    "lifecycle_state": "stale",
                }
            ]
        },
    )
    monkeypatch.setattr(
        database,
        "list_retrieval_benchmark_runs",
        lambda **_kwargs: [
            {
                "suite": "governance-shadow",
                "recommendation_metadata": {
                    "governance_shadow": {
                        "proposal_precision": 0.9,
                        "guardrail_case_count": 1,
                        "guardrail_pass_count": 1,
                        "guardrail_fail_count": 0,
                    },
                    "settings_mutated": False,
                },
            }
        ],
    )
    monkeypatch.setattr(database, "list_capture_review_jobs", lambda status="all", limit=50: [])
    monkeypatch.setattr(database, "code_feedback_summary", lambda **_kwargs: {"rows": []})
    monkeypatch.setattr(database, "list_semantic_duplicate_clusters", lambda **_kwargs: {"clusters": []})
    monkeypatch.setattr(database, "list_memory_governance_actions", lambda **_kwargs: [])
    monkeypatch.setattr(
        database,
        "record_memory_governance_run",
        lambda **kwargs: recorded_runs.append(kwargs) or {"id": "run-1", **kwargs},
    )
    monkeypatch.setattr(
        database,
        "record_memory_governance_action",
        lambda **kwargs: recorded_actions.append(kwargs) or {"id": f"action-{len(recorded_actions)}", **kwargs},
    )
    monkeypatch.setattr(
        database,
        "record_memory_governance_digest",
        lambda **kwargs: recorded_digests.append(kwargs) or {"id": "digest-1", **kwargs},
    )

    result = KnowledgeService().run_governance(mode="shadow", actor="tester", limit=10)

    assert result["settings_mutated"] is False
    assert result["memory_mutated"] is False
    assert result["run"]["id"] == "run-1"
    assert result["digest"]["id"] == "digest-1"
    assert recorded_runs[0]["mode"] == "shadow"
    assert recorded_actions[0]["action"] == "stale_tag"
    assert recorded_actions[0]["status"] == "proposed"
    assert recorded_digests[0]["summary"]["new_proposals"] == 1


def test_service_governance_run_auto_applies_only_allowed_low_risk_actions(monkeypatch):
    recorded_actions: dict[str, dict[str, object]] = {}
    recorded_digests = []
    recorded_run_updates = []
    transitions = []

    monkeypatch.setattr(
        service_module,
        "_governance_policy_from_settings",
        lambda: {
            "mode": "auto",
            "min_shadow_precision": 0.8,
            "auto_apply_enabled": True,
            "auto_apply_risk_ceiling": "low",
            "max_actions_per_run": 10,
            "protected_memory_rules": governance.DEFAULT_POLICY["protected_memory_rules"],
        },
    )
    monkeypatch.setattr(
        database,
        "retention_quality_report",
        lambda limit=25: {
            "candidates": [
                {
                    "id": "claim-stale",
                    "memory_class": "claim",
                    "quality_bucket": "review",
                    "reason": "stale",
                    "confidence": 0.2,
                    "lifecycle_state": "stale",
                }
            ]
        },
    )
    monkeypatch.setattr(
        database,
        "list_retrieval_benchmark_runs",
        lambda **_kwargs: [{"recommendation_metadata": {"governance_shadow": {"proposal_precision": 0.91, "guardrail_fail_count": 0}}}],
    )
    monkeypatch.setattr(database, "list_capture_review_jobs", lambda **_kwargs: [])
    monkeypatch.setattr(database, "code_feedback_summary", lambda **_kwargs: {"rows": []})
    monkeypatch.setattr(
        database,
        "list_semantic_duplicate_clusters",
        lambda **_kwargs: {
            "clusters": [
                {
                    "id": "cluster-1",
                    "memory_class": "claim",
                    "status": "active",
                    "suppressed_count": 2,
                    "members": [{"owner_id": "claim-a"}, {"owner_id": "claim-b"}],
                }
            ]
        },
    )
    monkeypatch.setattr(database, "list_memory_governance_actions", lambda **_kwargs: [])
    monkeypatch.setattr(database, "record_memory_governance_run", lambda **kwargs: {"id": "run-1", **kwargs})
    monkeypatch.setattr(
        database,
        "update_memory_governance_run",
        lambda **kwargs: recorded_run_updates.append(kwargs)
        or {"id": kwargs["run_id"], "status": kwargs["status"], "summary": kwargs["summary"], "memory_mutated": kwargs["memory_mutated"]},
        raising=False,
    )

    def record_action(**kwargs):
        action_id = f"action-{len(recorded_actions) + 1}"
        row = {"id": action_id, **kwargs}
        recorded_actions[action_id] = row
        return row

    def update_action(**kwargs):
        action = {**recorded_actions[str(kwargs["action_id"])]}
        action.update({"status": kwargs["status"], "after_state": kwargs.get("after_state") or {}, "memory_mutated": kwargs.get("memory_mutated", False)})
        recorded_actions[str(kwargs["action_id"])] = action
        return action

    monkeypatch.setattr(database, "record_memory_governance_action", record_action)
    monkeypatch.setattr(database, "get_memory_governance_action", lambda action_id: recorded_actions[action_id])
    monkeypatch.setattr(database, "get_claim", lambda claim_id: {"id": claim_id, "lifecycle_state": "stale", "retention_action": "keep"})
    monkeypatch.setattr(
        database,
        "transition_claim",
        lambda **kwargs: transitions.append(kwargs) or {"id": kwargs["claim_id"], "lifecycle_state": "stale", "retention_action": "keep"},
    )
    monkeypatch.setattr(database, "update_memory_governance_action", update_action)
    monkeypatch.setattr(database, "record_memory_governance_digest", lambda **kwargs: recorded_digests.append(kwargs) or {"id": "digest-1", **kwargs})

    result = KnowledgeService().run_governance(mode="auto", actor="tester", limit=10)

    assert result["memory_mutated"] is True
    assert transitions[0]["claim_id"] == "claim-stale"
    assert recorded_actions["action-1"]["status"] == "applied"
    assert recorded_actions["action-2"]["status"] == "proposed"
    assert recorded_actions["action-3"]["status"] == "proposed"
    assert recorded_run_updates[0]["memory_mutated"] is True
    assert recorded_run_updates[0]["summary"]["auto_applied"] == 1
    assert recorded_digests[0]["memory_mutated"] is True


def test_service_governance_apply_and_recover_are_confirmed_audited_and_idempotent(monkeypatch):
    updates = []
    transitions = []
    restores = []
    action = {
        "id": "action-1",
        "action": "stale_tag",
        "target_type": "claim",
        "target_id": "claim-1",
        "memory_class": "claim",
        "risk": "low",
        "status": "proposed",
        "source": "retention_quality",
        "before_state": {"lifecycle_state": "active", "retention_action": "keep"},
        "after_state": {},
        "rationale": {"guardrails": {"protected": False}},
    }

    monkeypatch.setattr(database, "get_memory_governance_action", lambda action_id: {**action, "status": updates[-1]["status"]} if updates else action)
    monkeypatch.setattr(
        database,
        "list_retrieval_benchmark_runs",
        lambda **_kwargs: [{"recommendation_metadata": {"governance_shadow": {"proposal_precision": 0.91, "guardrail_fail_count": 0}}}],
    )
    monkeypatch.setattr(database, "get_claim", lambda claim_id: {"id": claim_id, "lifecycle_state": "active", "retention_action": "keep"})
    monkeypatch.setattr(
        database,
        "transition_claim",
        lambda **kwargs: transitions.append(kwargs) or {"id": kwargs["claim_id"], "lifecycle_state": "stale", "retention_action": "keep"},
    )
    monkeypatch.setattr(
        database,
        "restore_claim_lifecycle_state",
        lambda **kwargs: restores.append(kwargs) or {"id": kwargs["claim_id"], "lifecycle_state": kwargs["lifecycle_state"], "retention_action": kwargs["retention_action"]},
        raising=False,
    )
    monkeypatch.setattr(
        database,
        "update_memory_governance_action",
        lambda **kwargs: updates.append(kwargs) or {**action, "status": kwargs["status"], "after_state": kwargs["after_state"], "memory_mutated": kwargs["memory_mutated"]},
    )

    with pytest.raises(ValueError, match="confirmation"):
        KnowledgeService().governance_apply("action-1", rationale="reviewed", confirm=False, actor="tester")

    applied = KnowledgeService().governance_apply("action-1", rationale="reviewed stale evidence", confirm=True, actor="tester")
    recovered = KnowledgeService().governance_recover("action-1", rationale="operator rollback", confirm=True, actor="tester")

    assert applied["action"]["status"] == "applied"
    assert applied["memory_mutated"] is True
    assert transitions[0]["transition"] == "stale"
    assert transitions[0]["actor"] == "tester"
    assert recovered["action"]["status"] == "recovered"
    assert recovered["memory_mutated"] is True
    assert restores[0]["lifecycle_state"] == "active"
    assert updates[0]["status"] == "applied"
    assert updates[1]["status"] == "recovered"


def test_service_governance_apply_marks_stale_proposal_conflict(monkeypatch):
    action = {
        "id": "action-1",
        "action": "deprioritize",
        "target_type": "claim",
        "target_id": "claim-1",
        "memory_class": "claim",
        "risk": "low",
        "status": "proposed",
        "source": "retention_quality",
        "before_state": {"lifecycle_state": "active", "retention_action": "keep"},
        "after_state": {},
        "rationale": {"guardrails": {"protected": False}},
    }
    updates = []

    monkeypatch.setattr(database, "get_memory_governance_action", lambda action_id: action)
    monkeypatch.setattr(
        database,
        "list_retrieval_benchmark_runs",
        lambda **_kwargs: [{"recommendation_metadata": {"governance_shadow": {"proposal_precision": 0.91, "guardrail_fail_count": 0}}}],
    )
    monkeypatch.setattr(database, "get_claim", lambda claim_id: {"id": claim_id, "lifecycle_state": "confirmed", "retention_action": "keep"})
    monkeypatch.setattr(database, "update_memory_governance_action", lambda **kwargs: updates.append(kwargs) or {**action, "status": kwargs["status"], "after_state": kwargs["after_state"]})

    result = KnowledgeService().governance_apply("action-1", rationale="reviewed", confirm=True, actor="tester")

    assert result["action"]["status"] == "skipped_conflict"
    assert result["memory_mutated"] is False
    assert updates[0]["status"] == "skipped_conflict"
    assert updates[0]["after_state"]["conflict"]["current_lifecycle_state"] == "confirmed"
