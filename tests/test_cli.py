import json
import subprocess
import sys
from pathlib import Path


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
