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
            "metadata": kwargs["metadata"],
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
            "strict_indexing": True,
        },
    )

    assert response.status_code == 200
    payload = response.json()
    assert payload["root"]["name"] == "docs"
    assert payload["sync"]["synced"]["root_name"] == "docs"
    assert captured["watch_enabled"] is True
    assert captured["include_globs"] == ["**/*.md"]
    assert captured["exclude_globs"] == ["private/**"]
    assert captured["metadata"]["strict_indexing"] is True


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


def test_acceleration_reliability_endpoints_forward_to_service(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []

    class FakeService:
        def indexer_reliability_status(self, **kwargs):
            calls.append(("status", kwargs))
            return {"readiness": "partial", "scope": kwargs}

        def run_indexer_reliability(self, **kwargs):
            calls.append(("run", kwargs))
            return {"readiness": "ready", "settings_mutated": False, "run": kwargs}

        def operator_evidence(self, **kwargs):
            calls.append(("evidence", kwargs))
            return {"settings_mutated": False, "gates": {"vss_snapshot": {"state": "hold"}}}

        def indexer_root_reliability(self, root_name):
            calls.append(("root", root_name))
            return {"root_name": root_name, "readiness": "partial"}

        def indexer_reliability_roots(self, **kwargs):
            calls.append(("roots", kwargs))
            return {"roots": [{"root_name": "docs", "readiness": "ready"}], "settings_mutated": False}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())

    client = fastapi_testclient.TestClient(create_app())
    status_response = client.get(
        "/api/acceleration/reliability",
        params={"root_name": "docs", "label": "nightly", "freshness_hours": 12},
    )
    run_response = client.post(
        "/api/acceleration/reliability/run",
        json={
            "scope": "synthetic",
            "deployment_label": "desktop",
            "include_cache_readiness": True,
            "evidence_level": "full",
            "compare_label": "baseline",
        },
    )
    evidence_response = client.get("/api/acceleration/evidence", params={"label": "nightly", "compare_label": "baseline"})
    root_response = client.get("/api/acceleration/reliability/root/docs")
    roots_response = client.get("/api/acceleration/reliability/roots", params={"freshness_hours": 24})

    assert status_response.status_code == 200
    assert status_response.json()["readiness"] == "partial"
    assert run_response.status_code == 200
    assert run_response.json()["settings_mutated"] is False
    assert evidence_response.status_code == 200
    assert evidence_response.json()["gates"]["vss_snapshot"]["state"] == "hold"
    assert root_response.status_code == 200
    assert root_response.json() == {"root_name": "docs", "readiness": "partial"}
    assert roots_response.status_code == 200
    assert roots_response.json()["roots"][0]["root_name"] == "docs"
    assert calls[0] == (
        "status",
        {
            "root_name": "docs",
            "path": None,
                "label": "nightly",
                "deployment_label": None,
                "compare_label": None,
                "freshness_hours": 12,
                "limit": 100,
            },
    )
    assert calls[1][0] == "run"
    assert calls[1][1]["scope"] == "synthetic"
    assert calls[1][1]["include_cache_readiness"] is True
    assert calls[1][1]["evidence_level"] == "full"
    assert calls[1][1]["compare_label"] == "baseline"
    assert calls[2] == ("evidence", {"label": "nightly", "deployment_label": None, "compare_label": "baseline", "freshness_hours": 336, "limit": 100})
    assert calls[3] == ("root", "docs")
    assert calls[4] == (
        "roots",
        {
            "include_disabled": False,
            "freshness_hours": 24,
            "limit": 100,
        },
    )


def test_code_feedback_and_diagnostics_filter_routes_forward_to_service(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []

    class FakeService:
        def record_code_feedback(self, **kwargs):
            calls.append(("feedback", kwargs))
            return {"id": "feedback-1", "settings_mutated": False}

        def code_feedback_summary(self, **kwargs):
            calls.append(("summary", kwargs))
            return {"settings_mutated": False, "rows": [{"miss_category": "missing_symbol"}]}

        def operational_diagnostics(self, **kwargs):
            calls.append(("diagnostics", kwargs))
            return {"settings_mutated": False, "items": [{"section": "jobs"}]}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    feedback_response = client.post(
        "/api/code/feedback",
        json={
            "query": "build invoice",
            "root_name": "docs",
            "result_count": 0,
            "surface": "dashboard",
            "miss_category": "missing_symbol",
            "expected_symbol": "OrderService.build_invoice",
            "path": "E:/private/docs/orders.py",
            "metadata": {"note": "safe"},
        },
    )
    summary_response = client.get("/api/code/feedback/summary", params={"root_name": "docs"})
    diagnostics_response = client.get(
        "/api/diagnostics/jobs",
        params={"root_name": "docs", "status": "blocked_missing_dependency", "family": "office", "since_hours": 24, "include_details": True},
    )

    assert feedback_response.status_code == 200
    assert summary_response.json()["rows"][0]["miss_category"] == "missing_symbol"
    assert diagnostics_response.json()["items"][0]["section"] == "jobs"
    assert calls[0][0] == "feedback"
    assert calls[0][1]["miss_category"] == "missing_symbol"
    assert calls[1] == ("summary", {"root_name": "docs", "limit": 20})
    assert calls[2] == (
        "diagnostics",
        {
            "section": "jobs",
            "limit": 25,
            "root_name": "docs",
            "status": "blocked_missing_dependency",
            "family": "office",
            "since_hours": 24,
            "include_details": True,
        },
    )


def test_diagnostics_action_route_forwards_to_service(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []

    class FakeService:
        def remediate_diagnostic(self, **kwargs):
            calls.append(kwargs)
            return {"settings_mutated": False, "action": kwargs["action"], "result": {"status": "pending"}}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    response = client.post(
        "/api/diagnostics/actions",
        json={
            "action": "retry_corpus_job",
            "target_type": "job",
            "target_id": "job-1",
            "root_name": "docs",
            "family": "office",
            "reason": "operator retry",
        },
    )

    assert response.status_code == 200
    assert response.json()["settings_mutated"] is False
    assert calls == [
        {
            "action": "retry_corpus_job",
            "target_type": "job",
            "target_id": "job-1",
            "root_name": "docs",
            "family": "office",
            "reason": "operator retry",
            "actor": "api",
        }
    ]


def test_code_and_operational_diagnostic_routes_forward_to_service(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []

    class FakeService:
        def code_status(self, **kwargs):
            calls.append(("code_status", kwargs))
            return {"totals": {"symbol_count": 1}, "roots": []}

        def code_search(self, **kwargs):
            calls.append(("code_search", kwargs))
            return {"query": kwargs["query"], "results": [{"symbol": "OrderService"}]}

        def code_symbol_lookup(self, **kwargs):
            calls.append(("code_symbol", kwargs))
            return {"query": kwargs["symbol"], "matches": [{"symbol": kwargs["symbol"]}]}

        def operational_diagnostics(self, **kwargs):
            calls.append(("diagnostics", kwargs))
            return {"section": kwargs["section"], "settings_mutated": False, "sections": {}}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())

    client = fastapi_testclient.TestClient(create_app())

    assert client.get("/api/code/status", params={"root_name": "app"}).json()["totals"]["symbol_count"] == 1
    assert (
        client.get(
            "/api/code/search",
            params={"query": "OrderService", "language": "python", "relationship": "call", "path_glob": "src/*.py", "include_generated": "true"},
        ).json()["results"][0]["symbol"]
        == "OrderService"
    )
    assert client.get("/api/code/symbols", params={"symbol": "OrderService"}).json()["matches"][0]["symbol"] == "OrderService"
    assert client.get("/api/diagnostics/workers", params={"limit": 5}).json()["settings_mutated"] is False

    assert calls == [
        ("code_status", {"root_name": "app"}),
        (
            "code_search",
            {
                "query": "OrderService",
                "root_name": None,
                "language": "python",
                "symbol_kind": None,
                "relationship": "call",
                "path_glob": "src/*.py",
                "include_generated": True,
                "limit": 20,
            },
        ),
        ("code_symbol", {"symbol": "OrderService", "root_name": None, "language": None, "include_references": True, "limit": 20}),
        (
            "diagnostics",
            {
                "section": "workers",
                "limit": 5,
                "root_name": None,
                "status": None,
                "family": None,
                "since_hours": None,
                "include_details": False,
            },
        ),
    ]


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
                "scope_hash": None,
                "deployment_label": "desktop-after",
                "scenario": None,
                "freshness_hours": None,
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


def test_reliability_run_proxies_host_agent_root_benchmark_slices(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    service_calls = []
    host_calls = []

    class FakeService:
        def run_benchmark(self, **kwargs):
            service_calls.append(kwargs)
            return {"scenario": kwargs["scenario"], "runs": []}

        def indexer_reliability_status(self, **kwargs):
            service_calls.append({"status": kwargs})
            return {"readiness": "partial", "settings_mutated": False, "scope": kwargs}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    monkeypatch.setattr(
        "flux_llm_kb.rest_api.database.get_monitored_root",
        lambda root_name: {"name": root_name, "root_path": "E:\\Docs", "metadata": {"host_access": "host_agent"}},
    )
    monkeypatch.setattr("flux_llm_kb.rest_api.host_agent_benchmark", lambda **kwargs: host_calls.append(kwargs) or {"status": "host_agent", "runs": []})
    client = fastapi_testclient.TestClient(create_app())

    response = client.post(
        "/api/acceleration/reliability/run",
        json={
            "scope": "root",
            "root_name": "docs",
            "label": "nightly",
            "deployment_label": "desktop",
            "max_files": 77,
            "passes": 3,
            "include_cache_readiness": True,
            "include_tuning": True,
        },
    )

    assert response.status_code == 200
    assert response.json()["settings_mutated"] is False
    assert [call["scenario"] for call in service_calls if "scenario" in call] == ["reliability", "cache_readiness"]
    assert [call["scenario"] for call in host_calls] == ["host_cloud", "tuning"]
    assert all(call["scope"] == "root" and call["root_name"] == "docs" for call in host_calls)
    assert all(call["max_files"] == 77 and call["passes"] == 3 for call in host_calls)
    assert service_calls[-1] == {
        "status": {
            "root_name": "docs",
            "path": None,
            "label": "nightly",
            "deployment_label": "desktop",
            "compare_label": None,
        }
    }


def test_retrieval_benchmark_routes_are_exposed(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []

    class FakeService:
        def run_retrieval_benchmark(self, **kwargs):
            calls.append(("retrieval_run", kwargs))
            return {
                "suite": kwargs["suite"],
                "metrics": {"top1_accuracy": 1.0},
                "metric_deltas": {"top1_accuracy": 0.2},
                "calibration_summary": {"confidence_bands": {"high": 2}},
                "recommendations": {
                    "settings_mutated": False,
                    "candidates": [{"kind": "semantic_duplicate_threshold", "threshold": 0.86}],
                },
            }

        def retrieval_benchmark_history(self, **kwargs):
            calls.append(("retrieval_history", kwargs))
            return {
                "suite": kwargs["suite"],
                "runs": [
                    {
                        "id": "retrieval-run-1",
                        "metric_deltas": {"top1_accuracy": 0.2},
                        "calibration_summary": {"confidence_bands": {"high": 2}},
                    }
                ],
            }

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    response = client.post(
        "/api/retrieval/benchmarks/run",
        json={
            "suite": "standard",
            "label": "nightly",
            "compare_label": "baseline",
            "limit_per_query": 7,
            "token_budget": 900,
        },
    )
    history = client.get("/api/retrieval/benchmarks?suite=standard&label=nightly&limit=3")

    assert response.status_code == 200
    assert response.json()["metrics"]["top1_accuracy"] == 1.0
    assert response.json()["metric_deltas"] == {"top1_accuracy": 0.2}
    assert response.json()["recommendations"]["candidates"][0]["kind"] == "semantic_duplicate_threshold"
    assert history.status_code == 200
    assert history.json()["runs"][0]["metric_deltas"] == {"top1_accuracy": 0.2}
    assert history.json()["runs"][0]["calibration_summary"]["confidence_bands"] == {"high": 2}
    assert calls == [
        (
            "retrieval_run",
            {
                "suite": "standard",
                "label": "nightly",
                "compare_label": "baseline",
                "limit_per_query": 7,
                "token_budget": 900,
                "persist": True,
            },
        ),
        ("retrieval_history", {"suite": "standard", "label": "nightly", "limit": 3}),
    ]


def test_governance_routes_are_exposed(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []

    class FakeService:
        def governance_runs(self, **kwargs):
            calls.append(("runs", kwargs))
            return {"runs": [{"id": "run-1", "settings_mutated": False}]}

        def run_governance(self, **kwargs):
            calls.append(("run", kwargs))
            return {"run": {"id": "run-2"}, "actions": [], "settings_mutated": False, "memory_mutated": False}

        def governance_actions(self, **kwargs):
            calls.append(("actions", kwargs))
            return {"actions": [{"id": "action-1", "action": "stale_tag", "status": "proposed"}], "telemetry": {"by_action": {"stale_tag": 1}}}

        def governance_apply(self, action_id, **kwargs):
            calls.append(("apply", {"action_id": action_id, **kwargs}))
            return {"action": {"id": action_id, "status": "applied"}, "memory_mutated": True, "settings_mutated": False}

        def governance_recover(self, action_id, **kwargs):
            calls.append(("recover", {"action_id": action_id, **kwargs}))
            return {"action": {"id": action_id, "status": "recovered"}, "memory_mutated": True, "settings_mutated": False}

        def governance_digest(self):
            calls.append(("digest", {}))
            return {"digest": {"summary": {"new_proposals": 1}}, "settings_mutated": False}

        def governance_policy(self):
            calls.append(("policy", {}))
            return {"policy": {"min_shadow_precision": 0.8}, "settings_mutated": False}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    assert client.get("/api/governance/runs", params={"limit": 3}).json()["runs"][0]["id"] == "run-1"
    assert client.post("/api/governance/run", json={"mode": "shadow", "limit": 4}).json()["run"]["id"] == "run-2"
    assert client.get("/api/governance/actions", params={"status": "proposed", "limit": 5}).json()["telemetry"]["by_action"]["stale_tag"] == 1
    assert client.post("/api/governance/actions/action-1/apply", json={"rationale": "reviewed", "confirm": True}).json()["action"]["status"] == "applied"
    assert client.post("/api/governance/actions/action-1/recover", json={"rationale": "rollback", "confirm": True}).json()["action"]["status"] == "recovered"
    assert client.get("/api/governance/digest").json()["digest"]["summary"]["new_proposals"] == 1
    assert client.get("/api/governance/policy").json()["policy"]["min_shadow_precision"] == 0.8
    assert calls == [
        ("runs", {"limit": 3}),
        ("run", {"mode": "shadow", "actor": "api", "limit": 4}),
        ("actions", {"status": "proposed", "limit": 5}),
        ("apply", {"action_id": "action-1", "rationale": "reviewed", "confirm": True, "actor": "api"}),
        ("recover", {"action_id": "action-1", "rationale": "rollback", "confirm": True, "actor": "api"}),
        ("digest", {}),
        ("policy", {}),
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
    cleanup_calls = []

    class FakeService:
        def reconcile_unseen_assets_for_root(self, **kwargs):
            cleanup_calls.append(kwargs)
            return {"assets_marked": 2, "jobs_cancelled": 2}

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
    monkeypatch.setattr(
        database,
        "get_monitored_root_by_identifier",
        lambda _root_id: {"metadata": {"owner": "ops", "strict_indexing": True}},
    )
    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())

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
            "strict_indexing": False,
        },
    )

    assert response.status_code == 200
    assert response.json()["name"] == "docs-edited"
    assert captured["root_id"] == "root-1"
    assert captured["watch_enabled"] is False
    assert captured["metadata"]["source"] == "dashboard"
    assert captured["metadata"]["owner"] == "ops"
    assert captured["metadata"]["strict_indexing"] is False
    assert cleanup_calls == [{"root_name": "docs-edited", "reason": "root_policy_update"}]


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

    data_response = client.post("/api/crawl/backfill", json={"kind": "data", "limit": 5, "workers": 1})

    assert data_response.status_code == 200
    assert data_response.json()["backfill"] == {"kind": "data", "limit": 5, "workers": 1}


def test_crawl_backfill_endpoint_omits_default_parallelism_knobs(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    class FakeService:
        def run_corpus_backfill(self, **kwargs):
            return {"backfill": kwargs}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())
    client = fastapi_testclient.TestClient(create_app())

    response = client.post("/api/crawl/backfill", json={"kind": "text"})

    assert response.status_code == 200
    assert response.json()["backfill"] == {"kind": "text", "limit": None, "workers": None}


def test_dashboard_job_cancel_endpoint_reports_running_job_conflict(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []
    monkeypatch.setattr(
        database,
        "cancel_corpus_job",
        lambda **kwargs: calls.append(kwargs)
        or {
            "job_id": kwargs["job_id"],
            "status": "running",
            "cancelled": False,
            "error": "Corpus job is running and cannot be cancelled mid-execution.",
        },
    )
    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())

    client = fastapi_testclient.TestClient(create_app())
    response = client.post("/api/dashboard/jobs/job-running/cancel")

    assert response.status_code == 409
    assert "cannot be cancelled mid-execution" in response.json()["detail"]
    assert calls == [{"job_id": "job-running", "actor": "dashboard"}]


def test_dashboard_jobs_endpoint_passes_filters_paging_and_updated_range(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []

    def fake_collect_jobs_payload(**kwargs):
        calls.append(kwargs)
        return {
            "jobs": [],
            "count": 0,
            "limit": kwargs["limit"],
            "offset": kwargs["offset"],
            "has_next": False,
            "filter_options": {"statuses": [], "roots": [], "job_types": []},
        }

    monkeypatch.setattr("flux_llm_kb.rest_api.collect_jobs_payload", fake_collect_jobs_payload)
    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())

    client = fastapi_testclient.TestClient(create_app())
    response = client.get(
        "/api/dashboard/jobs",
        params=[
            ("status", "failed"),
            ("status", "retrying_locked"),
            ("root_name", "docs"),
            ("root_name", "mail"),
            ("job_type", "corpus_extract_pdf"),
            ("updated_from", "2026-06-25T00:00:00+00:00"),
            ("updated_to", "2026-06-26T00:00:00+00:00"),
            ("limit", "25"),
            ("offset", "50"),
        ],
    )

    assert response.status_code == 200
    assert response.json()["limit"] == 25
    assert calls == [
        {
            "limit": 25,
            "offset": 50,
            "status": ["failed", "retrying_locked"],
            "root_name": ["docs", "mail"],
            "job_type": ["corpus_extract_pdf"],
            "updated_from": "2026-06-25T00:00:00+00:00",
            "updated_to": "2026-06-26T00:00:00+00:00",
        }
    ]


def test_dashboard_job_tool_invocations_endpoint_returns_bounded_history(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []

    def fake_list_capture_job_tool_invocations(**kwargs):
        calls.append(kwargs)
        return [
            {
                "id": "inv-1",
                "job_id": kwargs["job_id"],
                "command": ["python", "-c", "print('hello')"],
                "cwd": "E:/LLM KB",
                "status": "running",
                "return_code": None,
                "stdout": "hello\n",
                "stderr": "",
            }
        ]

    monkeypatch.setattr(database, "list_capture_job_tool_invocations", fake_list_capture_job_tool_invocations)
    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())

    client = fastapi_testclient.TestClient(create_app())
    response = client.get("/api/dashboard/jobs/job-1/tool-invocations", params={"limit": "25"})

    assert response.status_code == 200
    assert response.json()["job_id"] == "job-1"
    assert response.json()["invocations"][0]["stdout"] == "hello\n"
    assert calls == [{"job_id": "job-1", "limit": 25}]


def test_dashboard_job_retry_endpoint_uses_diagnostic_remediation(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls = []

    class FakeService:
        def remediate_diagnostic(self, **kwargs):
            calls.append(kwargs)
            return {"settings_mutated": False, "action": "retry_corpus_job", "result": {"job_id": kwargs["target_id"], "status": "pending"}}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())

    client = fastapi_testclient.TestClient(create_app())
    response = client.post("/api/dashboard/jobs/job-1/retry")

    assert response.status_code == 200
    assert response.json()["result"] == {"job_id": "job-1", "status": "pending"}
    assert calls == [
        {
            "action": "retry_corpus_job",
            "target_type": "job",
            "target_id": "job-1",
            "reason": "operator forced retry from dashboard",
            "actor": "dashboard",
        }
    ]


def test_dashboard_job_retry_endpoint_reports_ineligible_job_conflict(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    class FakeService:
        def remediate_diagnostic(self, **_kwargs):
            raise LookupError("retryable corpus job not found: job-completed")

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: FakeService())

    client = fastapi_testclient.TestClient(create_app())
    response = client.post("/api/dashboard/jobs/job-completed/retry")

    assert response.status_code == 409
    assert "retryable corpus job not found" in response.json()["detail"]


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
                "include_generated": False,
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
                "include_generated": False,
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
                "include_generated": False,
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

        def ingest_capture_review_jobs(self, **kwargs):
            calls["ingest_capture_review_jobs"] = kwargs
            return {
                "dry_run": kwargs["dry_run"],
                "processed": 1,
                "ingested": 1,
                "settings_mutated": False,
                "jobs": [{"id": kwargs["job_id"], "status": "completed"}],
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
    capture = client.get("/api/capture/review", params={"status": "approved", "limit": 500})
    ingest = client.post(
        "/api/capture/review/ingest",
        json={"job_id": "job-1", "limit": 10, "dry_run": True},
    )
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
    assert calls["list_capture_review_jobs"] == {"status": "approved", "limit": 500}
    assert ingest.status_code == 200
    assert ingest.json()["settings_mutated"] is False
    assert calls["ingest_capture_review_jobs"] == {
        "job_id": "job-1",
        "limit": 10,
        "dry_run": True,
        "actor": "api",
    }
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
