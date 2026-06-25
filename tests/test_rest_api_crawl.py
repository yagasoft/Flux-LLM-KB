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


def test_acceleration_status_route_is_exposed(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())
    monkeypatch.setattr(
        "flux_llm_kb.acceleration.collect_acceleration_status",
        lambda: {
            "capabilities": {"nvidia": {"ok": False, "state": "missing"}},
            "cache": {"root": "D:/FluxLLMKB/private/cache", "source": "install_root", "directories": {}},
            "worker_families": [{"family": "media", "pending": 2, "p95_duration_ms": 95, "ocr_cache_hits": 3, "ocr_cache_misses": 1}],
        },
    )

    client = fastapi_testclient.TestClient(create_app())
    response = client.get("/api/acceleration/status")

    assert response.status_code == 200
    payload = response.json()
    assert payload["cache"]["root"] == "D:/FluxLLMKB/private/cache"
    assert payload["worker_families"][0]["family"] == "media"
    assert payload["worker_families"][0]["ocr_cache_hits"] == 3
    assert payload["worker_families"][0]["ocr_cache_misses"] == 1


def test_watcher_worker_and_benchmark_routes_are_exposed(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []

    class FakeService:
        def watch_probe(self, **kwargs):
            calls.append(("watch_probe", kwargs))
            return {"path_scope": "temporary", "probe": kwargs}

        def worker_status(self, **kwargs):
            calls.append(("worker_status", kwargs))
            return {"families": [{"family": kwargs["family"], "backpressure": "cap_reached"}]}

        def watch_events(self, **kwargs):
            calls.append(("watch_events", kwargs))
            return {"events": [{"action": "changed", "path_hash": "sha256:abc"}]}

        def run_benchmark(self, **kwargs):
            calls.append(("benchmark_run", kwargs))
            return {"fixture": kwargs["fixture"], "files": kwargs["files"], "runs": []}

        def benchmark_history(self, **kwargs):
            calls.append(("benchmark_history", kwargs))
            return {"fixture": kwargs["fixture"], "runs": [{"id": "run-1"}]}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    probe = client.post("/api/crawl/watch/probe", json={"timeout_seconds": 1.5})
    workers = client.get("/api/crawl/workers", params={"family": "media"})
    events = client.get("/api/crawl/watch/events", params={"limit": 5})
    run = client.post(
        "/api/acceleration/benchmarks/run",
        json={
            "fixture": "image-heavy",
            "files": 4,
            "mode": "soak",
            "passes": 2,
            "label": "after-deploy",
            "compare_label": "before-deploy",
            "workers": 3,
            "family": "media",
            "scope": "synthetic",
            "root_name": None,
            "path": None,
            "max_files": 12,
            "deployment_label": "desktop-after",
            "scenario": "cache_readiness",
            "include_model_probe": True,
        },
    )
    history = client.get(
        "/api/acceleration/benchmarks",
        params={
            "fixture": "image-heavy",
            "mode": "soak",
            "label": "after-deploy",
            "warm_state": "warm",
            "scope_type": "monitored_root",
            "deployment_label": "desktop-after",
            "limit": 3,
        },
    )

    assert probe.status_code == 200
    assert probe.json()["path_scope"] == "temporary"
    assert workers.status_code == 200
    assert workers.json()["families"][0]["family"] == "media"
    assert events.status_code == 200
    assert events.json()["events"][0]["path_hash"] == "sha256:abc"
    assert run.status_code == 200
    assert run.json()["fixture"] == "image-heavy"
    assert history.status_code == 200
    assert history.json()["runs"] == [{"id": "run-1"}]
    assert calls == [
        ("watch_probe", {"timeout_seconds": 1.5}),
        ("worker_status", {"family": "media"}),
        ("watch_events", {"limit": 5}),
        (
            "benchmark_run",
            {
                "fixture": "image-heavy",
                "files": 4,
                "mode": "soak",
                "passes": 2,
                "label": "after-deploy",
                "compare_label": "before-deploy",
                "workers": 3,
                "family": "media",
                "scope": "synthetic",
                "root_name": None,
                "path": None,
                "max_files": 12,
                "deployment_label": "desktop-after",
                "scenario": "cache_readiness",
                "include_model_probe": True,
            },
        ),
        (
            "benchmark_history",
            {
                "fixture": "image-heavy",
                "mode": "soak",
                "label": "after-deploy",
                "warm_state": "warm",
                "scope_type": "monitored_root",
                "deployment_label": "desktop-after",
                "limit": 3,
            },
        ),
    ]


def test_benchmark_route_proxies_host_agent_roots(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []
    monkeypatch.setattr(
        "flux_llm_kb.rest_api.database.get_monitored_root",
        lambda root_name: {"name": root_name, "root_path": "E:\\Docs", "metadata": {"host_access": "host_agent"}},
    )
    monkeypatch.setattr("flux_llm_kb.rest_api.host_agent_benchmark", lambda **kwargs: calls.append(kwargs) or {"status": "host_agent", "runs": []})
    client = fastapi_testclient.TestClient(create_app())

    response = client.post(
        "/api/acceleration/benchmarks/run",
        json={"scope": "root", "root_name": "docs", "mode": "scan", "deployment_label": "after", "scenario": "host_cloud"},
    )

    assert response.status_code == 200
    assert response.json()["status"] == "host_agent"
    assert calls == [
        {
            "fixture": "all",
            "files": 10,
            "mode": "scan",
            "passes": 1,
            "label": None,
            "compare_label": None,
            "workers": 1,
            "family": "all",
            "scope": "root",
            "root_name": "docs",
            "path": None,
            "max_files": None,
            "deployment_label": "after",
            "scenario": "host_cloud",
            "include_model_probe": False,
        }
    ]


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

    archive_response = client.post("/api/crawl/backfill", json={"kind": "containers", "limit": 4, "workers": 1})

    assert archive_response.status_code == 200
    assert archive_response.json()["backfill"] == {"kind": "containers", "limit": 4, "workers": 1}


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


def test_embedding_endpoints_status_enqueue_and_backfill(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = {}

    class FakeService:
        def embedding_status(self, **kwargs):
            calls["status"] = kwargs
            return {"status": kwargs}

        def enqueue_embedding_jobs(self, **kwargs):
            calls["enqueue"] = kwargs
            return {"enqueue": kwargs}

        def refresh_embeddings(self, **kwargs):
            calls["backfill"] = kwargs
            return {"backfill": kwargs}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    status_response = client.get("/api/embeddings/status?root_name=docs")
    assert status_response.status_code == 200
    assert status_response.json()["status"] == {"root_name": "docs"}

    enqueue_response = client.post(
        "/api/embeddings/enqueue",
        json={"owner_class": "corpus", "root_name": "docs", "stale_only": True, "limit": 25},
    )
    assert enqueue_response.status_code == 200
    assert enqueue_response.json()["enqueue"] == {
        "owner_class": "corpus",
        "root_name": "docs",
        "stale_only": True,
        "limit": 25,
    }

    backfill_response = client.post(
        "/api/embeddings/backfill",
        json={"owner_class": "all", "root_name": "docs", "stale_only": True, "limit": 20},
    )
    assert backfill_response.status_code == 200
    assert backfill_response.json()["backfill"] == {
        "owner_class": "all",
        "root_name": "docs",
        "stale_only": True,
        "limit": 20,
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
        def search(self, query, limit=5, cwd=None, root_name=None, scope_mode="local_first", filters=None):
            calls["search"] = {
                "query": query,
                "limit": limit,
                "cwd": cwd,
                "root_name": root_name,
                "scope_mode": scope_mode,
                "filters": filters,
            }
            return [{"kind": "corpus_chunk", "query": query, "limit": limit}]

        def brief(self, query, token_budget=None, cwd=None, root_name=None, scope_mode="local_first", filters=None):
            calls["brief"] = {
                "query": query,
                "token_budget": token_budget,
                "cwd": cwd,
                "root_name": root_name,
                "scope_mode": scope_mode,
                "filters": filters,
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
            "kind": ["mail", "file"],
            "current_only": "true",
            "include_suppressed": "true",
            "file_kind": "code",
            "language": "python",
            "symbol_kind": "method",
            "relationship": "definition",
            "path_glob": "src/*.py",
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
            "lifecycle_state": "active",
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
        "filters": {
            "logical_kinds": ["file", "mail"],
            "current_only": True,
            "lifecycle_states": [],
            "include_suppressed": True,
            "file_kinds": ["code"],
            "languages": ["python"],
            "symbol_kinds": ["method"],
            "relationships": ["definition"],
            "path_globs": ["src/*.py"],
        },
    }
    assert calls["brief"] == {
        "query": "RFP",
        "token_budget": 900,
        "cwd": "E:/Repo",
        "root_name": "repo",
        "scope_mode": "local_only",
        "filters": {
            "logical_kinds": [],
            "current_only": False,
            "lifecycle_states": ["active"],
            "include_suppressed": False,
        },
    }


def test_get_and_post_explain_support_external_consumers(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []

    class FakeService:
        def explain(self, query, limit=5, token_budget=None, cwd=None, root_name=None, scope_mode="local_first", filters=None):
            calls.append(
                {
                    "query": query,
                    "limit": limit,
                    "token_budget": token_budget,
                    "cwd": cwd,
                    "root_name": root_name,
                    "scope_mode": scope_mode,
                    "filters": filters,
                }
            )
            return {
                "query": query,
                "results": [{"kind": "corpus_chunk", "query": query, "limit": limit}],
                "brief": {"text": f"{query}:{token_budget}", "token_budget": token_budget, "packed": [], "excluded": []},
            }

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    get_response = client.get(
        "/api/explain",
        params={
            "query": "RFP",
            "limit": 3,
            "token_budget": 900,
            "cwd": "E:/Repo",
            "root_name": "repo",
            "scope_mode": "local_only",
            "kind": "mail",
            "current_only": "true",
        },
    )
    post_response = client.post(
        "/api/explain",
        json={
            "query": "Roadmap",
            "limit": 2,
            "token_budget": 700,
            "scope_mode": "workspace_boosted",
            "filters": {"logical_kinds": ["file"], "include_suppressed": True},
        },
    )

    assert get_response.status_code == 200
    assert get_response.json()["brief"]["text"] == "RFP:900"
    assert post_response.status_code == 200
    assert post_response.json()["results"][0] == {"kind": "corpus_chunk", "query": "Roadmap", "limit": 2}
    assert calls == [
        {
            "query": "RFP",
            "limit": 3,
            "token_budget": 900,
            "cwd": "E:/Repo",
            "root_name": "repo",
            "scope_mode": "local_only",
            "filters": {
                "logical_kinds": ["mail"],
                "current_only": True,
                "lifecycle_states": [],
                "include_suppressed": False,
            },
        },
        {
            "query": "Roadmap",
            "limit": 2,
            "token_budget": 700,
            "cwd": None,
            "root_name": None,
            "scope_mode": "workspace_boosted",
            "filters": {
                "logical_kinds": ["file"],
                "current_only": False,
                "lifecycle_states": [],
                "include_suppressed": True,
            },
        },
    ]


def test_semantic_duplicate_routes_are_exposed(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []

    class FakeService:
        def refresh_semantic_duplicate_clusters(self, memory_class="all", root_name=None, threshold=None, limit=1000):
            calls.append(("refresh", memory_class, root_name, threshold, limit))
            return {"created_clusters": 2, "retired_clusters": 1, "memory_class": memory_class}

        def list_semantic_duplicate_clusters(self, memory_class=None, root_name=None, limit=50):
            calls.append(("list", memory_class, root_name, limit))
            return {"clusters": [{"id": "cluster-1", "memory_class": memory_class, "root_name": root_name}]}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    refresh = client.post(
        "/api/semantic-duplicates/refresh",
        json={"memory_class": "corpus", "root_name": "docs", "threshold": 0.91, "limit": 25},
    )
    listed = client.get(
        "/api/semantic-duplicates",
        params={"memory_class": "claim", "root_name": "docs", "limit": 7},
    )

    assert refresh.status_code == 200
    assert refresh.json()["created_clusters"] == 2
    assert listed.status_code == 200
    assert listed.json()["clusters"][0] == {"id": "cluster-1", "memory_class": "claim", "root_name": "docs"}
    assert calls == [
        ("refresh", "corpus", "docs", 0.91, 25),
        ("list", "claim", "docs", 7),
    ]


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


def test_retention_policy_and_quality_routes_are_exposed(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = {}

    class FakeService:
        def list_retention_policies(self):
            calls["list_retention_policies"] = True
            return {"policies": [{"memory_class": "claim", "half_life_days": 120, "min_confidence": 0.35, "action": "review"}]}

        def set_retention_policy(self, **kwargs):
            calls["set_retention_policy"] = kwargs
            return {
                "policy": {
                    "memory_class": kwargs["memory_class"],
                    "half_life_days": kwargs["half_life_days"],
                    "min_confidence": kwargs["min_confidence"],
                    "action": kwargs["action"],
                },
                "audit_event": {"id": "audit-1", "event_type": "retention.policy_updated"},
            }

        def retention_quality_report(self, **kwargs):
            calls["retention_quality_report"] = kwargs
            return {
                "summary": {"total": 1, "needs_review": 1, "by_class": {"claim": 1}, "by_bucket": {"review": 1}},
                "candidates": [{"id": "claim-1", "memory_class": "claim", "label": "Flux uses PostgreSQL", "reason": "stale"}],
            }

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    policies = client.get("/api/retention/policies")
    updated = client.put(
        "/api/retention/policies/claim",
        json={"half_life_days": 90, "min_confidence": 0.45, "action": "deprioritize", "reason": "live review"},
    )
    quality = client.get("/api/retention/quality", params={"limit": 10})

    assert policies.status_code == 200
    assert policies.json()["policies"][0]["memory_class"] == "claim"
    assert updated.status_code == 200
    assert updated.json()["audit_event"]["event_type"] == "retention.policy_updated"
    assert quality.status_code == 200
    assert quality.json()["candidates"][0]["label"] == "Flux uses PostgreSQL"
    assert calls["list_retention_policies"] is True
    assert calls["set_retention_policy"] == {
        "memory_class": "claim",
        "half_life_days": 90,
        "min_confidence": 0.45,
        "action": "deprioritize",
        "actor": "api",
        "reason": "live review",
    }
    assert calls["retention_quality_report"] == {"limit": 10}


def test_retention_policy_route_maps_validation_errors(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    class FakeService:
        def set_retention_policy(self, **_kwargs):
            raise ValueError("action must be one of: review, deprioritize, retire")

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    response = client.put(
        "/api/retention/policies/claim",
        json={"half_life_days": 90, "min_confidence": 0.45, "action": "delete", "reason": "bad"},
    )

    assert response.status_code == 400
    assert response.json()["error"]["code"] == "retention.policy_invalid"


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
