import json
from datetime import datetime, timezone

from flux_llm_kb import database
from flux_llm_kb.service import KnowledgeService


def test_operator_automation_policy_defaults_to_disabled_guarded_mode():
    from flux_llm_kb import operator_automation

    policy = operator_automation.normalized_policy({})

    assert policy["enabled"] is False
    assert policy["mode"] == "guarded"
    assert policy["settings_mutated"] is False
    assert policy["allowed_actions"] == [
        "refresh_retrieval_evidence",
        "ingest_approved_capture",
        "safe_diagnostic_recovery",
        "sync_search_index",
        "run_governance_shadow",
    ]
    assert "delete" in policy["manual_actions"]
    assert "oauth" in policy["manual_actions"]
    assert "restart" in policy["manual_actions"]


def test_operator_automation_run_records_sanitized_guarded_actions(monkeypatch):
    from flux_llm_kb import service as service_module

    recorded_runs = []
    recorded_actions = []
    updated_runs = []
    calls = []
    timestamp = datetime(2026, 6, 26, 10, 0, tzinfo=timezone.utc).isoformat()

    monkeypatch.setattr(
        service_module,
        "_operator_automation_policy_from_settings",
        lambda: {
            "enabled": True,
            "mode": "guarded",
            "interval_seconds": 900,
            "max_actions_per_run": 10,
            "auto_refresh_evidence": True,
            "auto_ingest_approved_capture": True,
            "auto_remediate_diagnostics": True,
            "auto_sync_search_index": True,
            "auto_run_governance_shadow": True,
        },
        raising=False,
    )
    monkeypatch.setattr(database, "list_capture_ingestion_jobs", lambda **_kwargs: [{"id": "capture-1", "payload": {"path": "E:/Private/session.json"}}])
    monkeypatch.setattr(database, "search_index_status", lambda **_kwargs: {"summary": {"pending": 3, "failed": 2}})
    monkeypatch.setattr(
        database,
        "list_operator_automation_runs",
        lambda **_kwargs: recorded_runs[-1:] if recorded_runs else [],
        raising=False,
    )
    monkeypatch.setattr(
        database,
        "list_operator_automation_actions",
        lambda **_kwargs: recorded_actions,
        raising=False,
    )

    def record_run(**kwargs):
        row = {
            "id": f"run-{len(recorded_runs) + 1}",
            "created_at": timestamp,
            "started_at": timestamp,
            "completed_at": None,
            **kwargs,
        }
        recorded_runs.append(row)
        return row

    def update_run(**kwargs):
        row = {**recorded_runs[-1], **kwargs, "completed_at": timestamp}
        updated_runs.append(row)
        return row

    def record_action(**kwargs):
        row = {
            "id": f"action-{len(recorded_actions) + 1}",
            "created_at": timestamp,
            "updated_at": timestamp,
            **kwargs,
        }
        recorded_actions.append(row)
        return row

    monkeypatch.setattr(database, "record_operator_automation_run", record_run, raising=False)
    monkeypatch.setattr(database, "update_operator_automation_run", update_run, raising=False)
    monkeypatch.setattr(database, "record_operator_automation_action", record_action, raising=False)

    service = KnowledgeService()
    monkeypatch.setattr(service, "run_retrieval_benchmark", lambda **kwargs: calls.append(("retrieval", kwargs)) or {"settings_mutated": False})
    monkeypatch.setattr(service, "run_indexer_reliability", lambda **kwargs: calls.append(("reliability", kwargs)) or {"settings_mutated": False})
    monkeypatch.setattr(service, "ingest_capture_review_jobs", lambda **kwargs: calls.append(("capture", kwargs)) or {"settings_mutated": False, "processed": 1})
    monkeypatch.setattr(service, "search_index_sync", lambda **kwargs: calls.append(("search_index", kwargs)) or {"settings_mutated": False, "queued": 5})
    monkeypatch.setattr(service, "run_governance", lambda **kwargs: calls.append(("governance", kwargs)) or {"settings_mutated": False, "summary": {"total": 2}})

    result = service.run_operator_automation(actor="pytest", trigger="manual", mode="guarded", limit=10)

    assert result["settings_mutated"] is False
    assert result["run"]["status"] == "completed"
    assert result["summary"]["applied"] >= 4
    assert all(action["settings_mutated"] is False for action in result["actions"])
    assert all(action["status"] in {"applied", "blocked", "skipped"} for action in result["actions"])
    assert ("governance", {"mode": "shadow", "actor": "pytest", "limit": 10}) in calls
    serialized_actions = json.dumps(recorded_actions, default=str)
    assert "E:/Private/session.json" not in serialized_actions
    assert "capture-1" in serialized_actions


def test_operator_automation_plans_search_index_sync_for_missing_corpus_records(monkeypatch):
    monkeypatch.setattr(
        database,
        "search_index_status",
        lambda **_kwargs: {
            "summary": {
                "total": 12,
                "by_status": {"indexed": 12},
                "missing": 7,
                "pending_work": 7,
            },
            "missing": {"corpus": {"asset_chunks": 7}},
        },
    )

    plan = KnowledgeService()._operator_automation_plan(
        {
            "auto_refresh_evidence": False,
            "auto_ingest_approved_capture": False,
            "auto_remediate_diagnostics": False,
            "auto_sync_search_index": True,
            "auto_run_governance_shadow": False,
        },
        limit=10,
    )

    assert [item["action"] for item in plan] == ["sync_search_index"]
    assert plan[0]["target_id"] == "pending_or_missing"
    assert "7 search-index record(s) need sync" in plan[0]["reason"]
    assert plan[0]["evidence"]["missing"] == 7
    assert plan[0]["evidence"]["missing_by_class"] == {"corpus": {"asset_chunks": 7}}


def test_database_records_operator_automation_history_with_sanitized_evidence(monkeypatch):
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
            sql, _params = executed[-1]
            if "INSERT INTO operator_automation_runs" in sql:
                return ("automation-run-1", timestamp)
            if "INSERT INTO operator_automation_actions" in sql:
                return ("automation-action-1", timestamp)
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

    run = database.record_operator_automation_run(
        mode="guarded",
        trigger="manual",
        status="running",
        policy_snapshot={"private_path": "E:/Private"},
        summary={"eligible": 1},
        actor="tester",
    )
    action = database.record_operator_automation_action(
        run_id=run["id"],
        action="ingest_approved_capture",
        target_type="capture_review_job",
        target_id="capture-1",
        risk="low",
        status="applied",
        source="capture",
        rationale={"summary": "approved capture can be ingested"},
        evidence={"path": "E:/Private/session.json", "source_leaf": "session.json"},
        result={"settings_mutated": False, "created_episode_ids": ["episode-1"]},
        actor="tester",
    )

    assert run["id"] == "automation-run-1"
    assert action["id"] == "automation-action-1"
    serialized_params = json.dumps([params for _, params in executed], default=str)
    assert "E:/Private/session.json" not in serialized_params
    assert "session.json" in serialized_params
    assert "settings_mutated" in serialized_params


def test_operator_automation_rest_cli_and_mcp_contracts(monkeypatch, capsys):
    from flux_llm_kb import cli
    from flux_llm_kb.rest_api import create_app

    fastapi_testclient = __import__("fastapi.testclient", fromlist=["TestClient"])

    calls = []

    class FakeService:
        def operator_automation_status(self):
            calls.append(("status", {}))
            return {"settings_mutated": False, "policy": {"enabled": False}, "eligible_actions": []}

        def run_operator_automation(self, **kwargs):
            calls.append(("run", kwargs))
            return {"settings_mutated": False, "run": {"status": "completed"}, "summary": {"applied": 1}}

        def operator_automation_actions(self, **kwargs):
            calls.append(("actions", kwargs))
            return {"settings_mutated": False, "actions": [{"action": "refresh_retrieval_evidence"}]}

    monkeypatch.setattr("flux_llm_kb.service.KnowledgeService", FakeService)
    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())

    client = fastapi_testclient.TestClient(create_app())
    assert client.get("/api/automation/status").json()["policy"]["enabled"] is False
    assert client.post("/api/automation/run", json={"mode": "guarded", "limit": 5, "dry_run": True}).json()["settings_mutated"] is False
    assert client.get("/api/automation/actions?status=all&limit=3").json()["actions"][0]["action"] == "refresh_retrieval_evidence"

    assert cli.main(["automation", "status"]) == 0
    assert json.loads(capsys.readouterr().out)["policy"]["enabled"] is False
    assert cli.main(["automation", "run", "--mode", "guarded", "--limit", "5", "--dry-run"]) == 0
    assert json.loads(capsys.readouterr().out)["summary"]["applied"] == 1
    assert cli.main(["automation", "actions", "--status", "all", "--limit", "3"]) == 0
    assert json.loads(capsys.readouterr().out)["actions"][0]["action"] == "refresh_retrieval_evidence"

    source = __import__("pathlib").Path(__import__("flux_llm_kb.mcp_server").mcp_server.__file__).read_text(encoding="utf-8")
    assert '@mcp.tool(name="kb.automation_status")' in source
    assert '@mcp.tool(name="kb.automation_run")' in source
    assert '@mcp.tool(name="kb.automation_actions")' in source
