from pathlib import Path
from types import SimpleNamespace

import pytest

from flux_llm_kb import database


fastapi_testclient = pytest.importorskip("fastapi.testclient")


def test_remember_endpoint_passes_workspace_scope(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    captured = {}

    class FakeService:
        def remember(self, title, body, metadata=None, cwd=None, root_name=None):
            captured.update({"title": title, "body": body, "metadata": metadata, "cwd": cwd, "root_name": root_name})
            return SimpleNamespace(id="episode-1", redaction_count=0)

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())

    client = fastapi_testclient.TestClient(create_app())
    response = client.post(
        "/api/remember",
        json={"title": "Scoped", "body": "Memory", "cwd": "E:/Repo", "root_name": "repo"},
    )

    assert response.status_code == 200
    assert response.json() == {"id": "episode-1", "redaction_count": 0}
    assert captured == {
        "title": "Scoped",
        "body": "Memory",
        "metadata": None,
        "cwd": "E:/Repo",
        "root_name": "repo",
    }


def test_crawl_root_create_endpoint_validates_and_adds_root(tmp_path, monkeypatch):
    from flux_llm_kb.rest_api import create_app

    captured = {}

    def fake_add_monitored_root(**kwargs):
        captured.update(kwargs)
        return {
            "name": kwargs["name"],
            "root_path": str(Path(kwargs["root_path"])),
            "recursive": kwargs["recursive"],
            "watch_enabled": kwargs["watch_enabled"],
            "trust_rank": kwargs["trust_rank"],
            "include_globs": kwargs["include_globs"],
            "exclude_globs": kwargs["exclude_globs"],
            "max_inline_bytes": kwargs["max_inline_bytes"],
            "heavy_threshold_bytes": kwargs["heavy_threshold_bytes"],
        }

    class FakeService:
        def sync_corpus(self, **kwargs):
            return {"synced": kwargs}

    monkeypatch.setattr(database, "add_monitored_root", fake_add_monitored_root)
    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())

    client = fastapi_testclient.TestClient(create_app())
    response = client.post(
        "/api/crawl/roots",
        json={
            "name": "docs",
            "root_path": str(tmp_path),
            "recursive": True,
            "watch_enabled": True,
            "initial_crawl": True,
            "trust_rank": 720,
            "include_globs": ["**/*.md"],
            "exclude_globs": ["private/**"],
            "max_inline_bytes": 131072,
            "heavy_threshold_bytes": 5242880,
        },
    )

    assert response.status_code == 200
    payload = response.json()
    assert payload["root"]["name"] == "docs"
    assert payload["sync"]["synced"]["root_name"] == "docs"
    assert captured["watch_enabled"] is True
    assert captured["include_globs"] == ["**/*.md"]
    assert captured["exclude_globs"] == ["private/**"]


def test_crawl_root_create_accepts_windows_host_path_via_host_agent(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    captured = {}

    def fake_add_monitored_root(**kwargs):
        captured.update(kwargs)
        return {
            "name": kwargs["name"],
            "root_path": kwargs["root_path"],
            "glob_mode": kwargs["glob_mode"],
            "metadata": kwargs["metadata"],
        }

    class FakeService:
        def sync_corpus(self, **_kwargs):
            raise AssertionError("Docker API must not directly crawl host-agent paths")

    monkeypatch.setattr(database, "add_monitored_root", fake_add_monitored_root)
    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    monkeypatch.setattr(
        "flux_llm_kb.rest_api.host_agent_validate_path",
        lambda path: {
            "status": "ok",
            "absolute": True,
            "is_dir": True,
            "exists": True,
            "path": path,
            "path_style": "windows_drive",
        },
    )
    monkeypatch.setattr(
        "flux_llm_kb.rest_api.host_agent_sync",
        lambda **kwargs: {"status": "queued", **kwargs},
    )
    monkeypatch.setattr("flux_llm_kb.rest_api.path_requires_host_agent", lambda _path: True)

    client = fastapi_testclient.TestClient(create_app())
    response = client.post(
        "/api/crawl/roots",
        json={
            "name": "watch-test",
            "root_path": "E:\\Temp\\watch-test",
            "initial_crawl": True,
            "glob_mode": "extend",
            "include_globs": ["**/*.md"],
        },
    )

    assert response.status_code == 200
    payload = response.json()
    assert payload["root"]["root_path"] == "E:\\Temp\\watch-test"
    assert payload["sync"]["status"] == "queued"
    assert captured["glob_mode"] == "extend"
    assert captured["metadata"]["host_access"] == "host_agent"


def test_host_routes_are_exposed(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())
    client = fastapi_testclient.TestClient(create_app())

    response = client.get("/api/host/status")

    assert response.status_code == 200
    assert "status" in response.json()


def test_crawl_root_create_endpoint_rejects_missing_directory(monkeypatch, tmp_path):
    from flux_llm_kb.rest_api import create_app

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())
    client = fastapi_testclient.TestClient(create_app())

    response = client.post(
        "/api/crawl/roots",
        json={"name": "missing", "root_path": str(tmp_path / "does-not-exist")},
    )

    assert response.status_code == 400
    payload = response.json()
    assert "directory does not exist" in payload["detail"]
    assert payload["message"] == payload["detail"]
    assert payload["error"]["code"] == "crawl.root_invalid"
    assert payload["error"]["component"] == "crawler"
    assert payload["error"]["severity"] == "error"
    assert payload["error"]["retryable"] is False
    assert "Choose an existing directory" in payload["error"]["user_action"]
    assert payload["error"]["status_code"] == 400


def test_crawl_root_update_endpoint_validates_and_persists(monkeypatch, tmp_path):
    from flux_llm_kb.rest_api import create_app

    captured = {}

    def fake_update_monitored_root(**kwargs):
        captured.update(kwargs)
        return {
            "id": kwargs["root_id"],
            "name": kwargs["name"],
            "root_path": kwargs["root_path"],
            "watch_enabled": kwargs["watch_enabled"],
            "include_globs": kwargs["include_globs"],
            "exclude_globs": kwargs["exclude_globs"],
            "metadata": kwargs["metadata"],
        }

    monkeypatch.setattr(database, "update_monitored_root", fake_update_monitored_root)
    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())

    client = fastapi_testclient.TestClient(create_app())
    response = client.patch(
        "/api/crawl/roots/root-1",
        json={
            "name": "docs-edited",
            "root_path": str(tmp_path),
            "watch_enabled": False,
            "recursive": True,
            "trust_rank": 640,
            "include_globs": ["**/*.md"],
            "exclude_globs": ["tmp/**"],
            "glob_mode": "override",
            "max_inline_bytes": 262144,
            "heavy_threshold_bytes": 10485760,
        },
    )

    assert response.status_code == 200
    assert response.json()["name"] == "docs-edited"
    assert captured["root_id"] == "root-1"
    assert captured["watch_enabled"] is False
    assert captured["metadata"]["source"] == "dashboard"


def test_crawl_root_delete_endpoint_purges_index(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []
    monkeypatch.setattr(
        database,
        "delete_monitored_root",
        lambda **kwargs: calls.append(kwargs)
        or {"id": kwargs["root_id"], "deleted": True, "purged_index": kwargs["purge_index"]},
    )
    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())

    client = fastapi_testclient.TestClient(create_app())
    response = client.delete("/api/crawl/roots/root-1?purge_index=true")

    assert response.status_code == 200
    assert response.json() == {"id": "root-1", "deleted": True, "purged_index": True}
    assert calls == [{"root_id": "root-1", "purge_index": True, "actor": "dashboard"}]


def test_crawl_backfill_endpoint_runs_worker_once(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    class FakeService:
        def run_corpus_backfill(self, **kwargs):
            return {"backfill": kwargs, "completed": 2}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    response = client.post("/api/crawl/backfill", json={"kind": "text", "limit": 3, "workers": 1})

    assert response.status_code == 200
    assert response.json()["backfill"] == {"kind": "text", "limit": 3, "workers": 1}


def test_crawl_backfill_endpoint_proxies_host_agent_root(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    class FakeService:
        def run_corpus_backfill(self, **_kwargs):
            raise AssertionError("Docker API must not process host-agent backfill jobs directly")

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda name: {
            "name": name,
            "root_path": "E:\\Temp\\watch-test",
            "metadata": {"host_access": "host_agent"},
        },
    )
    monkeypatch.setattr(
        "flux_llm_kb.rest_api.host_agent_backfill",
        lambda **kwargs: {"host_backfill": kwargs, "completed": 4},
    )

    client = fastapi_testclient.TestClient(create_app())
    response = client.post(
        "/api/crawl/backfill",
        json={"kind": "all", "limit": 10, "workers": 1, "root_name": "watch-test"},
    )

    assert response.status_code == 200
    assert response.json()["host_backfill"] == {
        "kind": "all",
        "limit": 10,
        "workers": 1,
        "root_name": "watch-test",
    }


def test_post_body_models_are_bound_from_json(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    class FakeService:
        def search(self, query, limit=5, **_kwargs):
            return [{"query": query, "limit": limit}]

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    response = client.post("/api/search", json={"query": "corpus roots", "limit": 7})

    assert response.status_code == 200
    assert response.json() == [{"query": "corpus roots", "limit": 7}]


def test_get_search_and_brief_support_external_consumers(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = {}

    class FakeService:
        def search(self, query, limit=5, cwd=None, root_name=None, scope_mode="local_first"):
            calls["search"] = {
                "query": query,
                "limit": limit,
                "cwd": cwd,
                "root_name": root_name,
                "scope_mode": scope_mode,
            }
            return [{"kind": "corpus_chunk", "query": query, "limit": limit}]

        def brief(self, query, token_budget=None, cwd=None, root_name=None, scope_mode="local_first"):
            calls["brief"] = {
                "query": query,
                "token_budget": token_budget,
                "cwd": cwd,
                "root_name": root_name,
                "scope_mode": scope_mode,
            }
            return f"{query}:{token_budget}"

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    search = client.get(
        "/api/search",
        params={
            "query": "RFP",
            "limit": 3,
            "cwd": "E:/Repo",
            "root_name": "repo",
            "scope_mode": "local_only",
        },
    )
    brief = client.get(
        "/api/brief",
        params={
            "query": "RFP",
            "token_budget": 900,
            "cwd": "E:/Repo",
            "root_name": "repo",
            "scope_mode": "local_only",
        },
    )

    assert search.status_code == 200
    assert search.json() == [{"kind": "corpus_chunk", "query": "RFP", "limit": 3}]
    assert brief.status_code == 200
    assert brief.json() == {"brief": "RFP:900"}
    assert calls["search"] == {
        "query": "RFP",
        "limit": 3,
        "cwd": "E:/Repo",
        "root_name": "repo",
        "scope_mode": "local_only",
    }
    assert calls["brief"] == {
        "query": "RFP",
        "token_budget": 900,
        "cwd": "E:/Repo",
        "root_name": "repo",
        "scope_mode": "local_only",
    }


def test_post_search_and_brief_accept_scope_fields(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = {}

    class FakeService:
        def search(self, query, limit=5, cwd=None, root_name=None, scope_mode="local_first"):
            calls["search"] = {
                "query": query,
                "limit": limit,
                "cwd": cwd,
                "root_name": root_name,
                "scope_mode": scope_mode,
            }
            return [{"query": query, "scope_mode": scope_mode}]

        def brief(self, query, token_budget=None, cwd=None, root_name=None, scope_mode="local_first"):
            calls["brief"] = {
                "query": query,
                "token_budget": token_budget,
                "cwd": cwd,
                "root_name": root_name,
                "scope_mode": scope_mode,
            }
            return f"{query}:{scope_mode}:{token_budget}"

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    search = client.post(
        "/api/search",
        json={"query": "RFP", "limit": 2, "cwd": "E:/Repo", "scope_mode": "workspace_boosted"},
    )
    brief = client.post(
        "/api/brief",
        json={"query": "RFP", "token_budget": 700, "root_name": "repo", "scope_mode": "global"},
    )

    assert search.status_code == 200
    assert search.json() == [{"query": "RFP", "scope_mode": "workspace_boosted"}]
    assert brief.status_code == 200
    assert brief.json() == {"brief": "RFP:global:700"}
    assert calls["search"]["cwd"] == "E:/Repo"
    assert calls["search"]["scope_mode"] == "workspace_boosted"
    assert calls["brief"]["root_name"] == "repo"
    assert calls["brief"]["scope_mode"] == "global"
    assert calls["brief"]["token_budget"] == 700


def test_claim_and_graph_routes_are_exposed(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = {}

    class FakeService:
        def upsert_claim(self, **kwargs):
            calls["upsert"] = kwargs
            return {"id": "claim-1", "lifecycle_state": "active"}

        def get_claim(self, claim_id):
            calls["get_claim"] = claim_id
            return {"id": claim_id, "lifecycle_state": "active"}

        def transition_claim(self, **kwargs):
            calls["transition"] = kwargs
            return {"id": kwargs["claim_id"], "lifecycle_state": "contradicted"}

        def traverse_graph(self, **kwargs):
            calls["traverse"] = kwargs
            return {"start_entity_id": kwargs["entity_id"], "edges": []}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    created = client.post(
        "/api/claims",
        json={
            "subject_type": "project",
            "subject": "Flux",
            "predicate": "uses",
            "object_text": "PostgreSQL",
            "confidence": 0.8,
        },
    )
    fetched = client.get("/api/claims/claim-1")
    transitioned = client.post(
        "/api/claims/claim-1/transitions",
        json={"transition": "contradict", "related_claim_id": "claim-2", "reason": "newer evidence"},
    )
    graph = client.get(
        "/api/graph/traverse",
        params={"entity_id": "entity-1", "relation_type": "depends_on", "max_depth": 2, "direction": "out"},
    )

    assert created.status_code == 200
    assert fetched.status_code == 200
    assert transitioned.status_code == 200
    assert graph.status_code == 200
    assert calls["upsert"]["subject_name"] == "Flux"
    assert calls["transition"]["transition"] == "contradict"
    assert calls["traverse"]["relation_types"] == ["depends_on"]


def test_claim_review_and_capture_review_routes_are_exposed(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = {}

    class FakeService:
        def list_claims(self, **kwargs):
            calls["list_claims"] = kwargs
            return {
                "claims": [
                    {
                        "id": "claim-1",
                        "subject_entity_id": "entity-1",
                        "predicate": "uses",
                        "object_text": "PostgreSQL",
                        "lifecycle_state": "stale",
                        "retention_action": "deprioritize",
                        "review_reasons": ["stale", "retention:deprioritize"],
                    }
                ],
                "counts": {"total": 1, "needs_review": 1, "current": 0},
            }

        def list_capture_review_jobs(self, **kwargs):
            calls["list_capture_review_jobs"] = kwargs
            return {
                "jobs": [
                    {
                        "id": "job-1",
                        "job_type": "codex_backfill",
                        "status": "pending",
                        "payload": {"status": "pending_review", "path": "sessions/session.json"},
                    }
                ]
            }

        def review_capture_job(self, **kwargs):
            calls["review_capture_job"] = kwargs
            return {
                "job": {
                    "id": kwargs["job_id"],
                    "job_type": "codex_backfill",
                    "status": "approved",
                    "payload": {
                        "status": "approved",
                        "path": "sessions/session.json",
                        "review": {
                            "decision": kwargs["decision"],
                            "rationale": kwargs["rationale"],
                            "actor": kwargs["actor"],
                            "reviewed_at": "2026-06-23T10:00:00+00:00",
                            "audit_event_id": "audit-1",
                        },
                    },
                },
                "review": {
                    "decision": kwargs["decision"],
                    "rationale": kwargs["rationale"],
                    "actor": kwargs["actor"],
                    "reviewed_at": "2026-06-23T10:00:00+00:00",
                    "audit_event_id": "audit-1",
                },
                "audit_event_id": "audit-1",
                "audit_event": {"id": "audit-1", "event_type": "capture.review_approved"},
                "links": [{"label": "Audit event", "href": "/api/audit?limit=50", "audit_event_id": "audit-1"}],
            }

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    claims = client.get(
        "/api/claims",
        params={"review": "needs_review", "state": "stale", "q": "postgres", "limit": 500},
    )
    capture = client.get("/api/capture/review", params={"limit": 500})
    decision = client.post(
        "/api/capture/review/job-1/decision",
        json={"decision": "approve", "rationale": "verified enough"},
    )

    assert claims.status_code == 200
    assert claims.json()["claims"][0]["review_reasons"] == ["stale", "retention:deprioritize"]
    assert calls["list_claims"] == {
        "review": "needs_review",
        "state": "stale",
        "q": "postgres",
        "limit": 500,
    }
    assert capture.status_code == 200
    assert capture.json()["jobs"][0]["payload"] == {"status": "pending_review", "path": "sessions/session.json"}
    assert calls["list_capture_review_jobs"] == {"limit": 500}
    assert decision.status_code == 200
    assert decision.json()["audit_event"]["event_type"] == "capture.review_approved"
    assert decision.json()["audit_event_id"] == "audit-1"
    assert calls["review_capture_job"] == {
        "job_id": "job-1",
        "decision": "approve",
        "rationale": "verified enough",
        "actor": "api",
    }


@pytest.mark.parametrize(
    ("exc", "status_code", "code"),
    [
        (ValueError("rationale is required"), 400, "capture_review.decision_invalid"),
        (LookupError("capture review job not found: job-1"), 404, "capture_review.job_not_found"),
        (RuntimeError("capture review job already decided: job-1"), 409, "capture_review.job_conflict"),
    ],
)
def test_capture_review_decision_route_maps_errors(monkeypatch, exc, status_code, code):
    from flux_llm_kb.rest_api import create_app

    class FakeService:
        def review_capture_job(self, **_kwargs):
            raise exc

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    response = client.post(
        "/api/capture/review/job-1/decision",
        json={"decision": "approve", "rationale": "verified enough"},
    )

    assert response.status_code == status_code
    assert response.json()["error"]["code"] == code


def test_corpus_lookup_routes_return_assets_and_chunks(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())
    monkeypatch.setattr(
        database,
        "list_source_assets",
        lambda **kwargs: [{"id": "asset-1", "path": "docs/readme.md", "root_name": kwargs.get("root_name")}],
        raising=False,
    )
    monkeypatch.setattr(
        database,
        "get_source_asset",
        lambda asset_id: {"id": asset_id, "path": "docs/readme.md"},
        raising=False,
    )
    monkeypatch.setattr(
        database,
        "get_asset_chunk",
        lambda chunk_id: {"id": chunk_id, "title": "Readme"},
        raising=False,
    )

    client = fastapi_testclient.TestClient(create_app())

    assets = client.get("/api/corpus/assets", params={"root_name": "docs", "limit": 2})
    asset = client.get("/api/corpus/assets/asset-1")
    chunk = client.get("/api/corpus/chunks/chunk-1")

    assert assets.status_code == 200
    assert assets.json()["assets"][0]["root_name"] == "docs"
    assert asset.status_code == 200
    assert asset.json()["id"] == "asset-1"
    assert chunk.status_code == 200
    assert chunk.json()["title"] == "Readme"


def test_result_detail_route_returns_logical_payload(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())
    monkeypatch.setattr(
        "flux_llm_kb.result_details.result_detail",
        lambda kind, result_id: {
            "logical_kind": "file",
            "detail_ref": {"kind": kind, "id": result_id},
            "asset_id": "asset-1",
            "preview": {"text": "preview"},
        },
        raising=False,
    )

    client = fastapi_testclient.TestClient(create_app())
    response = client.get("/api/results/corpus_chunk/chunk-1")

    assert response.status_code == 200
    assert response.json()["detail_ref"] == {"kind": "corpus_chunk", "id": "chunk-1"}
    assert response.json()["logical_kind"] == "file"


def test_file_action_route_proxies_to_host_agent_without_path(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    captured = {}
    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())
    monkeypatch.setattr(
        "flux_llm_kb.rest_api.host_agent_file_action",
        lambda **kwargs: captured.update(kwargs) or {"state": "opened", "asset_id": kwargs["asset_id"]},
        raising=False,
    )

    client = fastapi_testclient.TestClient(create_app())
    response = client.post("/api/corpus/assets/asset-1/actions", json={"action": "reveal"})

    assert response.status_code == 200
    assert response.json()["state"] == "opened"
    assert captured == {"asset_id": "asset-1", "action": "reveal"}


def test_file_action_route_rejects_browser_supplied_path(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())
    client = fastapi_testclient.TestClient(create_app())

    response = client.post(
        "/api/corpus/assets/asset-1/actions",
        json={"action": "open", "path": "E:\\Unsafe\\from-browser.txt"},
    )

    assert response.status_code == 422
    payload = response.json()
    assert payload["error"]["code"] == "api.request_invalid"
    assert payload["error"]["component"] == "api"
    assert payload["error"]["severity"] == "error"
    assert payload["error"]["retryable"] is False
    assert "path" in payload["message"]


def test_lookup_routes_return_structured_error_envelopes(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())
    monkeypatch.setattr(database, "get_source_asset", lambda _asset_id: None, raising=False)
    monkeypatch.setattr(database, "get_asset_chunk", lambda _chunk_id: None, raising=False)
    monkeypatch.setattr(
        "flux_llm_kb.result_details.result_detail",
        lambda _kind, _result_id: (_ for _ in ()).throw(LookupError("result not found")),
        raising=False,
    )

    client = fastapi_testclient.TestClient(create_app())

    asset = client.get("/api/corpus/assets/asset-missing")
    chunk = client.get("/api/corpus/chunks/chunk-missing")
    result = client.get("/api/results/corpus_chunk/chunk-missing")

    assert asset.status_code == 404
    assert asset.json()["error"]["code"] == "corpus.asset_not_found"
    assert asset.json()["error"]["target"] == {"type": "asset", "id": "asset-missing"}
    assert asset.json()["detail"] == "source asset not found"
    assert chunk.status_code == 404
    assert chunk.json()["error"]["code"] == "corpus.chunk_not_found"
    assert chunk.json()["error"]["target"] == {"type": "chunk", "id": "chunk-missing"}
    assert result.status_code == 404
    assert result.json()["error"]["code"] == "result.not_found"
    assert result.json()["error"]["target"] == {"type": "corpus_chunk", "id": "chunk-missing"}


def test_result_detail_invalid_kind_returns_structured_bad_request(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())
    monkeypatch.setattr(
        "flux_llm_kb.result_details.result_detail",
        lambda _kind, _result_id: (_ for _ in ()).throw(ValueError("unsupported result kind: unknown")),
        raising=False,
    )

    client = fastapi_testclient.TestClient(create_app())
    response = client.get("/api/results/unknown/id-1")

    assert response.status_code == 400
    payload = response.json()
    assert payload["error"]["code"] == "result.kind_invalid"
    assert payload["error"]["component"] == "retrieval"
    assert payload["error"]["retryable"] is False
    assert payload["error"]["target"] == {"type": "unknown", "id": "id-1"}


def test_mail_profile_lookup_failure_returns_structured_error(monkeypatch):
    from flux_llm_kb.rest_api import create_app
    from flux_llm_kb import mail_ingestion

    monkeypatch.setattr(
        mail_ingestion,
        "update_mail_profile_oauth_client_config_path",
        lambda **_kwargs: (_ for _ in ()).throw(ValueError("mail profile not found: gmail-missing")),
    )

    client = fastapi_testclient.TestClient(create_app())
    response = client.put(
        "/api/mail/profiles/gmail-missing/oauth-client-config",
        json={"client_config_path": "private/client.json"},
    )

    assert response.status_code == 404
    payload = response.json()
    assert payload["error"]["code"] == "mail.profile_not_found"
    assert payload["error"]["component"] == "mail"
    assert payload["error"]["target"] == {"type": "mail_profile", "id": "gmail-missing"}
    assert payload["message"] == "mail profile not found: gmail-missing"
