from pathlib import Path


def test_dashboard_user_manual_source_docx_scripts_and_directive_exist():
    repo_root = Path(__file__).resolve().parents[1]
    manual_md = repo_root / "docs" / "user-guide" / "dashboard-user-manual.md"
    manual_docx = repo_root / "docs" / "user-guide" / "Flux-LLM-KB-Dashboard-User-Manual.docx"
    screenshot_script = repo_root / "scripts" / "docs" / "capture-dashboard-user-guide-screens.ps1"
    build_script = repo_root / "scripts" / "docs" / "build-dashboard-user-guide.ps1"
    agents = (repo_root / "AGENTS.md").read_text(encoding="utf-8")

    assert manual_md.exists()
    assert manual_docx.exists()
    assert manual_docx.stat().st_size > 10_000
    assert screenshot_script.exists()
    assert build_script.exists()

    manual_text = manual_md.read_text(encoding="utf-8")
    for heading in (
        "# Flux LLM KB Dashboard User Manual",
        "## Overview",
        "## Automation",
        "## Diagnostics",
        "## Performance",
        "## Corpus",
        "## Mail",
        "## Retrieval",
        "## Review",
        "## Settings",
        "## Jobs",
        "## Result Details",
    ):
        assert heading in manual_text

    assert "public-safe mocked screenshots" in manual_text
    assert "Guarded Auto" in manual_text
    assert "dashboard UI, automation behavior, operator APIs, setup docs, or screenshots" in agents
    assert "regenerate DOCX/screenshots" in agents
    assert "visually verify" in agents
