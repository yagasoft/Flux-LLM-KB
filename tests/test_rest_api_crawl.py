from pathlib import Path

import pytest

from flux_llm_kb import database


fastapi_testclient = pytest.importorskip("fastapi.testclient")


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
    assert "directory does not exist" in response.json()["detail"]


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
        def search(self, query, limit=5):
            return [{"query": query, "limit": limit}]

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    response = client.post("/api/search", json={"query": "corpus roots", "limit": 7})

    assert response.status_code == 200
    assert response.json() == [{"query": "corpus roots", "limit": 7}]
