import json
import subprocess
import sys
from pathlib import Path

from flux_llm_kb import cli


def test_cli_doctor_reports_missing_docker_without_failing():
    repo_root = Path(__file__).resolve().parents[1]

    result = subprocess.run(
        [sys.executable, "-m", "flux_llm_kb.cli", "doctor", "--json"],
        cwd=repo_root,
        text=True,
        capture_output=True,
        check=True,
    )
    payload = json.loads(result.stdout)

    assert payload["checks"]["python"]["ok"] is True
    assert "docker" in payload["checks"]
    assert payload["checks"]["docker"]["ok"] in {True, False}
    assert payload["summary"]["ok"] in {True, False}


def test_cli_lint_requires_vector_and_index_migrations():
    repo_root = Path(__file__).resolve().parents[1]

    result = subprocess.run(
        [sys.executable, "-m", "flux_llm_kb.cli", "lint"],
        cwd=repo_root,
        text=True,
        capture_output=True,
        check=True,
    )
    payload = json.loads(result.stdout)

    assert payload["ok"] is True
    assert payload["missing"] == []


def test_cli_search_uses_service_search(monkeypatch, capsys):
    from flux_llm_kb import service

    class FakeService:
        def search(self, query, limit=5):
            return [{"kind": "corpus_chunk", "title": query, "limit": limit}]

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert cli.main(["search", "dashboard", "--limit", "2"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload == [{"kind": "corpus_chunk", "title": "dashboard", "limit": 2}]


def test_cli_crawl_watch_enable_outputs_json(monkeypatch, capsys):
    monkeypatch.setattr(
        cli.database,
        "set_watch_enabled",
        lambda *, root_name, enabled: {"updated": 1, "root_name": root_name, "watch_enabled": enabled},
    )

    assert cli.main(["crawl", "watch", "enable", "--all"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload == {"updated": 1, "root_name": None, "watch_enabled": True}


def test_cli_crawl_add_persists_disabled_watch_by_default(monkeypatch, tmp_path, capsys):
    root = tmp_path / "docs"
    root.mkdir()

    def fake_add_monitored_root(**kwargs):
        return {"name": kwargs["name"], "root_path": kwargs["root_path"], "watch_enabled": kwargs["watch_enabled"]}

    monkeypatch.setattr(cli.database, "add_monitored_root", fake_add_monitored_root)

    assert cli.main(["crawl", "add", str(root), "--name", "docs"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload["name"] == "docs"
    assert payload["root_path"] == str(root.resolve())
    assert payload["watch_enabled"] is False
