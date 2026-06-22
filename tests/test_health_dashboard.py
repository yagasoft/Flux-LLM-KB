import tomllib
from pathlib import Path

from flux_llm_kb import database, health
from flux_llm_kb.health import (
    build_dashboard_html,
    collect_crawl_payload,
    collect_dashboard_payload,
    doctor_payload,
)


def test_collect_dashboard_payload_uses_shared_health_sources(monkeypatch):
    monkeypatch.setenv("FLUX_KB_INSTALL_ROOT", "D:\\FluxLLMKB")
    monkeypatch.setenv("FLUX_KB_IMAGE_TAG", "abc123")
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
    monkeypatch.setattr(
        database,
        "list_runtime_components",
        lambda: [
            {
                "name": "corpus-worker:docker",
                "status": "running",
                "heartbeat_age_seconds": 2,
                "metadata": {"last_result": {"completed": 1}},
            }
        ],
        raising=False,
    )
    monkeypatch.setattr(
        health,
        "remote_status",
        lambda: {
            "status": "running",
            "codex": {"status": "ready", "installed": True},
            "runtime": {"git": {"ok": True, "message": "host git", "required": True}},
        },
    )
    monkeypatch.setattr(health, "codex_status", lambda: {"status": "missing"})

    payload = collect_dashboard_payload()

    assert payload["database"]["ok"] is True
    assert payload["watcher"]["active_roots"] == 1
    assert payload["jobs"]["pending"] == 2
    assert payload["retrieval"]["asset_chunks"] == 5
    assert payload["recent_errors"] == ["bad file"]
    assert "extractors" in payload
    assert payload["codex"]["status"] == "ready"
    assert payload["runtime"]["git"]["message"] == "host git"
    assert payload["workers"]["active"] == 1
    assert payload["workers"]["components"][0]["name"] == "corpus-worker:docker"
    assert payload["deployment"]["install_root"] == "D:\\FluxLLMKB"
    assert payload["deployment"]["image_tag"] == "abc123"
    assert "repo_coupled" in payload["deployment"]


def test_collect_crawl_payload_includes_enriched_root_summaries(monkeypatch):
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [{"name": "docs", "root_path": "E:/Docs", "watch_enabled": True}],
    )
    monkeypatch.setattr(
        database,
        "crawl_status",
        lambda: {"active_watch_roots": 1, "disabled_watch_roots": 0, "recent_errors": ["bad file"]},
    )
    monkeypatch.setattr(
        database,
        "crawl_root_summaries",
        lambda: [
            {
                "name": "docs",
                "root_path": "E:/Docs",
                "state": "watching",
                "asset_counts": {"indexed": 3, "queued": 1},
                "job_counts": {"pending": 1, "blocked": 0},
                "latest_crawl": {"status": "completed", "files_seen": 4},
                "recent_assets": [{"path": "README.md", "status": "indexed"}],
                "recent_jobs": [],
            }
        ],
    )

    payload = collect_crawl_payload()

    assert payload["roots"][0]["name"] == "docs"
    assert payload["root_summaries"][0]["state"] == "watching"
    assert payload["root_summaries"][0]["asset_counts"]["indexed"] == 3
    assert payload["recent_errors"] == ["bad file"]


def test_collect_crawl_payload_marks_host_agent_roots_offline_when_bridge_is_down(monkeypatch):
    host_root = {
        "name": "watch-test",
        "root_path": "E:/Temp/watch-test",
        "watch_enabled": True,
        "metadata": {"host_access": "host_agent"},
    }
    monkeypatch.setattr(database, "list_monitored_roots", lambda: [host_root])
    monkeypatch.setattr(
        database,
        "crawl_status",
        lambda: {
            "active_watch_roots": 1,
            "disabled_watch_roots": 0,
            "recent_errors": [],
            "watchers": [
                {
                    "root_name": "watch-test",
                    "status": "running",
                    "heartbeat_age_seconds": 999,
                    "last_error": None,
                }
            ],
        },
    )
    monkeypatch.setattr(
        database,
        "crawl_root_summaries",
        lambda: [
            {
                **host_root,
                "state": "stale",
                "watcher": {"status": "running", "heartbeat_age_seconds": 999, "last_error": None},
                "asset_counts": {"indexed": 10},
                "job_counts": {"pending": 0},
            }
        ],
    )
    monkeypatch.setattr(
        health,
        "remote_status",
        lambda: {"status": "host_agent_offline", "message": "connection refused"},
    )

    payload = collect_crawl_payload()

    assert payload["watchers"][0]["status"] == "host_offline"
    assert payload["watchers"][0]["last_error"] == "connection refused"
    assert payload["root_summaries"][0]["state"] == "host_offline"
    assert payload["root_summaries"][0]["watcher"]["status"] == "host_offline"


def test_dashboard_html_contains_health_mount_points():
    html = build_dashboard_html()

    assert "Flux-LLM-KB Dashboard" in html
    assert "id=\"root\"" in html
    assert "/dashboard/assets/" in html


def test_dashboard_html_contains_settings_and_mail_forms_without_registry_wording():
    html = build_dashboard_html()

    assert "registry-backed" not in html.lower()
    assert "<pre id=\"watcher\"" not in html


def test_dashboard_html_can_load_built_spa(tmp_path, monkeypatch):
    from flux_llm_kb import health

    index = tmp_path / "index.html"
    index.write_text("<!doctype html><div id=\"root\">Built SPA</div>", encoding="utf-8")
    monkeypatch.setattr(health, "DASHBOARD_INDEX", index)

    html = build_dashboard_html()

    assert "Built SPA" in html
    assert "This dashboard build is missing" not in html


def test_doctor_summary_treats_gh_as_optional(monkeypatch):
    monkeypatch.delenv("FLUX_KB_INSTALL_ROOT", raising=False)
    monkeypatch.setattr(database, "check_database", lambda: database.DatabaseStatus(True, "ok"))
    monkeypatch.setattr(health, "_docker_check", lambda **_kwargs: {"ok": True, "message": "ok", "required": True})
    monkeypatch.setattr(
        health,
        "_command_check",
        lambda command, description, **kwargs: {
            "ok": command != "gh",
            "message": f"{command} check",
            "required": kwargs.get("required", True),
        },
    )

    payload = doctor_payload()

    assert payload["checks"]["gh"]["ok"] is False
    assert payload["checks"]["gh"]["required"] is False
    assert payload["summary"]["ok"] is True


def test_doctor_summary_treats_host_owned_tools_as_ok_in_production(monkeypatch):
    monkeypatch.setenv("FLUX_KB_INSTALL_ROOT", "D:\\FluxLLMKB")
    monkeypatch.setattr(database, "check_database", lambda: database.DatabaseStatus(True, "ok"))
    monkeypatch.setattr(health.shutil, "which", lambda _command: None)

    payload = doctor_payload()

    assert payload["checks"]["docker"]["ok"] is True
    assert payload["checks"]["docker"]["required"] is False
    assert payload["checks"]["git"]["ok"] is True
    assert payload["checks"]["git"]["required"] is False
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
