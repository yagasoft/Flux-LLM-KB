import tomllib
from pathlib import Path

from flux_llm_kb import database, health
from flux_llm_kb.health import build_dashboard_html, collect_dashboard_payload, doctor_payload


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
    assert "extractors" in payload


def test_dashboard_html_contains_health_mount_points():
    html = build_dashboard_html()

    assert "Flux-LLM-KB Dashboard" in html
    assert "data-panel=\"watcher\"" in html
    assert "data-tab=\"settings\"" in html
    assert "data-tab=\"mail\"" in html
    assert "/api/dashboard/health" in html
    assert "/api/settings" in html
    assert "/api/mail/status" in html


def test_doctor_summary_treats_gh_as_optional(monkeypatch):
    monkeypatch.setattr(database, "check_database", lambda: database.DatabaseStatus(True, "ok"))
    monkeypatch.setattr(health, "_docker_check", lambda **_kwargs: {"ok": True, "message": "ok", "required": True})
    monkeypatch.setattr(
        health,
        "_command_check",
        lambda command, description, required=True: {
            "ok": command != "gh",
            "message": f"{command} check",
            "required": required,
        },
    )

    payload = doctor_payload()

    assert payload["checks"]["gh"]["ok"] is False
    assert payload["checks"]["gh"]["required"] is False
    assert payload["summary"]["ok"] is True


def test_pyproject_defines_corpus_optional_extra():
    pyproject = tomllib.loads(Path("pyproject.toml").read_text(encoding="utf-8"))

    corpus = pyproject["project"]["optional-dependencies"]["corpus"]

    assert "watchdog>=4.0" in corpus
    assert "pypdf>=4.0" in corpus
    assert "python-docx>=1.1" in corpus
    assert "python-pptx>=0.6" in corpus
    assert "openpyxl>=3.1" in corpus
    assert "pillow>=10.0" in corpus
