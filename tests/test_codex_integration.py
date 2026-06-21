from pathlib import Path

from flux_llm_kb.codex_integration import codex_status


def test_codex_status_reports_configured_but_unlinked_plugin(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    codex_home.mkdir()
    (codex_home / "config.toml").write_text(
        '[plugins."flux-llm-kb@flux-llm-kb-local"]\nenabled = true\n',
        encoding="utf-8",
    )
    repo_plugin = tmp_path / "repo" / "plugins" / "flux-llm-kb"
    hooks = repo_plugin / "hooks"
    hooks.mkdir(parents=True)
    (hooks / "hooks.json").write_text('{"hooks": {}}', encoding="utf-8")

    monkeypatch.setattr(Path, "home", lambda: tmp_path)

    result = codex_status(repo_root=tmp_path / "repo")

    assert result["configured"] is True
    assert result["installed"] is False
    assert result["hooks_available"] is True
    assert result["status"] == "configured_not_installed"
