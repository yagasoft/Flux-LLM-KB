from flux_llm_kb import database
from flux_llm_kb.health import build_dashboard_html, collect_dashboard_payload


def test_collect_dashboard_payload_uses_shared_health_sources(monkeypatch):
    monkeypatch.setattr(database, "check_database", lambda: database.DatabaseStatus(True, "ok"))
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [{"name": "docs", "watch_enabled": True, "enabled": True}],
    )
    monkeypatch.setattr(
        database,
        "crawl_status",
        lambda: {
            "active_watch_roots": 1,
            "disabled_watch_roots": 0,
            "pending_jobs": 2,
            "failed_jobs": 1,
            "recent_errors": ["bad file"],
        },
    )
    monkeypatch.setattr(
        database,
        "retrieval_stats",
        lambda: {"episodes": 3, "asset_chunks": 5, "embeddings": 8},
    )

    payload = collect_dashboard_payload()

    assert payload["database"]["ok"] is True
    assert payload["watcher"]["active_roots"] == 1
    assert payload["jobs"]["pending"] == 2
    assert payload["retrieval"]["asset_chunks"] == 5
    assert payload["recent_errors"] == ["bad file"]


def test_dashboard_html_contains_health_mount_points():
    html = build_dashboard_html()

    assert "Flux-LLM-KB Dashboard" in html
    assert "data-panel=\"watcher\"" in html
    assert "/api/dashboard/health" in html
