from pathlib import Path
import re


def test_dashboard_user_manual_source_docx_scripts_and_directive_exist():
    repo_root = Path(__file__).resolve().parents[1]
    manual_md = repo_root / "docs" / "user-guide" / "dashboard-user-manual.md"
    manual_docx = repo_root / "docs" / "user-guide" / "Flux-LLM-KB-Dashboard-User-Manual.docx"
    screenshot_script = repo_root / "scripts" / "docs" / "capture-dashboard-user-guide-screens.ps1"
    build_script = repo_root / "scripts" / "docs" / "build-dashboard-user-guide.ps1"
    screenshot_driver = repo_root / "scripts" / "docs" / "capture_dashboard_user_guide_screens.mjs"
    screenshot_fixtures = repo_root / "scripts" / "docs" / "dashboard_user_guide_fixtures.mjs"
    agents = (repo_root / "AGENTS.md").read_text(encoding="utf-8")

    assert manual_md.exists()
    assert manual_docx.exists()
    assert manual_docx.stat().st_size > 200_000
    assert screenshot_script.exists()
    assert screenshot_driver.exists()
    assert screenshot_fixtures.exists()
    assert build_script.exists()

    manual_text = manual_md.read_text(encoding="utf-8")
    for heading in (
        "# Flux LLM KB Dashboard User Manual",
        "## Global Dashboard Controls",
        "## Overview Tab",
        "## Automation Tab",
        "## Diagnostics Tab",
        "## Performance Tab",
        "## Corpus Tab",
        "## Mail Tab",
        "## Retrieval Tab",
        "## Review Tab",
        "## Settings Tab",
        "## Jobs Tab",
        "## Result Details",
        "## Common Workflows",
        "## Status And Value Glossary",
        "## Action Safety Matrix",
    ):
        assert heading in manual_text

    assert "actual dashboard UI with public-safe mocked data" in manual_text
    assert "Guarded Auto" in manual_text
    assert "What you see" in manual_text
    assert "What the actions do" in manual_text
    assert "Why it matters" in manual_text
    assert "values can appear" in manual_text
    assert len(manual_text) > 45_000

    assert "Do not update `docs/user-guide/dashboard-user-manual.md`" in agents
    assert "unless the user explicitly asks for manual updates in the current turn" in agents
    assert "may ship without manual regeneration when no explicit manual request is present" in agents


def test_dashboard_user_manual_references_real_gui_screenshot_set():
    repo_root = Path(__file__).resolve().parents[1]
    manual_text = (repo_root / "docs" / "user-guide" / "dashboard-user-manual.md").read_text(encoding="utf-8")
    screen_refs = set(re.findall(r"!\[[^\]]+\]\(screens/([^)]+\.png)\)", manual_text))

    expected_screens = {
        "overview.png",
        "automation.png",
        "automation-after-run.png",
        "diagnostics.png",
        "diagnostics-detail.png",
        "performance.png",
        "performance-benchmark.png",
        "corpus.png",
        "corpus-root-form.png",
        "mail.png",
        "mail-profile-form.png",
        "retrieval.png",
        "retrieval-result-detail.png",
        "retrieval-code-diagnostics.png",
        "review.png",
        "review-capture-decision.png",
        "settings.png",
        "settings-editor.png",
        "jobs.png",
        "global-controls.png",
        "result-detail.png",
    }

    assert expected_screens <= screen_refs
    for screen in expected_screens:
        assert (repo_root / "docs" / "user-guide" / "screens" / screen).exists()


def test_dashboard_user_manual_capture_pipeline_uses_playwright_not_drawn_mocks():
    repo_root = Path(__file__).resolve().parents[1]
    capture_wrapper = (repo_root / "scripts" / "docs" / "capture-dashboard-user-guide-screens.ps1").read_text(encoding="utf-8")
    capture_driver = repo_root / "scripts" / "docs" / "capture_dashboard_user_guide_screens.mjs"
    fixture_module = repo_root / "scripts" / "docs" / "dashboard_user_guide_fixtures.mjs"
    builder = (repo_root / "scripts" / "docs" / "build_dashboard_user_guide.py").read_text(encoding="utf-8")
    package_json = (repo_root / "dashboard" / "package.json").read_text(encoding="utf-8")

    assert "capture_dashboard_user_guide_screens.mjs" in capture_wrapper
    assert capture_driver.exists()
    assert fixture_module.exists()

    driver_text = capture_driver.read_text(encoding="utf-8")
    fixture_text = fixture_module.read_text(encoding="utf-8")
    assert "@playwright/test" in driver_text
    assert "page.route" in driver_text
    assert "**/api/" in driver_text
    assert "global-controls" in driver_text
    assert "automation-after-run" in driver_text
    assert "/api/dashboard/health" in fixture_text
    assert "/api/automation/status" in fixture_text
    assert "/api/retrieval/benchmarks" in fixture_text

    assert "@playwright/test" in package_json
    assert "SCREEN_SPECS" not in builder
    assert "from PIL" not in builder


def test_dashboard_user_manual_builder_supports_rich_markdown():
    repo_root = Path(__file__).resolve().parents[1]
    manual_text = (repo_root / "docs" / "user-guide" / "dashboard-user-manual.md").read_text(encoding="utf-8")
    builder = (repo_root / "scripts" / "docs" / "build_dashboard_user_guide.py").read_text(encoding="utf-8")

    assert "| Area | Safe to automate? | Why |" in manual_text
    assert "> **Safety note:**" in manual_text
    assert "1. Start on Overview" in manual_text
    assert "_Figure:" in manual_text

    assert "parse_markdown_table" in builder
    assert "add_callout" in builder
    assert "add_caption" in builder
    assert "document.add_table" in builder
