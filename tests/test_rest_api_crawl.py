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
