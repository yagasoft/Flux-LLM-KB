from pathlib import Path


def _read_docs() -> str:
    return "\n".join(
        Path(path).read_text(encoding="utf-8")
        for path in (
            "docs/roadmap.md",
            "docs/architecture.md",
            "docs/integrations.md",
            "docs/setup.md",
        )
    )


def test_v26_docs_use_settings_catalog_public_wording():
    docs = _read_docs().lower()

    assert "settings catalog-backed" in docs
    assert "windows registry" in docs
    assert "registry-backed" not in docs


def test_v26_roadmap_names_oauth_refresh_dashboard_forms_and_token_health():
    roadmap = Path("docs/roadmap.md").read_text(encoding="utf-8").lower()

    assert "v2.6: mail capture and runtime configuration" in roadmap
    assert "gmail oauth setup" in roadmap
    assert "token refresh" in roadmap
    assert "token health" in roadmap
    assert "dashboard forms" in roadmap
    assert "settings catalog-backed" in roadmap
