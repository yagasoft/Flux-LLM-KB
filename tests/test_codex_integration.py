import json
from pathlib import Path

from flux_llm_kb.codex_integration import codex_status, install_plugin


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


def test_codex_install_plugin_links_plugin_and_writes_local_marketplace(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    codex_home.mkdir()
    repo_root = tmp_path / "repo"
    plugin = repo_root / "plugins" / "flux-llm-kb"
    (plugin / ".codex-plugin").mkdir(parents=True)
    (plugin / ".codex-plugin" / "plugin.json").write_text(
        '{"name":"flux-llm-kb","version":"0.1.0","skills":"./skills/","hooks":"./hooks/hooks.json","interface":{"displayName":"Flux LLM-KB"}}',
        encoding="utf-8",
    )
    (plugin / "hooks").mkdir()
    (plugin / "hooks" / "hooks.json").write_text('{"hooks": {}}', encoding="utf-8")
    (plugin / "skills").mkdir()

    monkeypatch.setattr(Path, "home", lambda: tmp_path)

    result = install_plugin(repo_root=repo_root)

    assert result["installed"] is True
    assert result["configured"] is True
    assert result["restart_required"] is True
    assert (codex_home / "plugins" / "flux-llm-kb").exists()
    marketplace_path = repo_root / ".agents" / "plugins" / "marketplace.json"
    assert marketplace_path.exists()
    marketplace = json.loads(marketplace_path.read_text(encoding="utf-8"))
    assert marketplace["name"] == "flux-llm-kb-local"
    assert marketplace["plugins"] == [
        {
            "name": "flux-llm-kb",
            "source": {"source": "local", "path": "./plugins/flux-llm-kb"},
            "policy": {"installation": "AVAILABLE", "authentication": "ON_INSTALL"},
            "category": "Developer Tools",
        }
    ]
    config = (codex_home / "config.toml").read_text(encoding="utf-8")
    assert "[marketplaces.flux-llm-kb-local]" in config
    assert f"source = {json.dumps(str(repo_root))}" in config
    assert f"source = {json.dumps(str(repo_root / 'plugins'))}" not in config
    assert '[plugins."flux-llm-kb@flux-llm-kb-local"]' in config


def test_codex_install_plugin_replaces_stale_existing_install(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    stale = codex_home / "plugins" / "flux-llm-kb"
    stale.mkdir(parents=True)
    (stale / "old.txt").write_text("old", encoding="utf-8")
    repo_root = tmp_path / "repo"
    plugin = repo_root / "plugins" / "flux-llm-kb"
    (plugin / ".codex-plugin").mkdir(parents=True)
    (plugin / ".codex-plugin" / "plugin.json").write_text(
        '{"name":"flux-llm-kb","version":"0.1.0","interface":{"displayName":"Flux LLM-KB"}}',
        encoding="utf-8",
    )
    (plugin / "hooks").mkdir()
    (plugin / "hooks" / "hooks.json").write_text('{"hooks": {}}', encoding="utf-8")

    monkeypatch.setattr(Path, "home", lambda: tmp_path)

    install_plugin(repo_root=repo_root)

    installed = codex_home / "plugins" / "flux-llm-kb"
    assert not (installed / "old.txt").exists()
    assert (installed / ".codex-plugin" / "plugin.json").exists()


def test_codex_status_reports_discovery_cache_and_restart_need(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    plugin_dir = codex_home / "plugins" / "flux-llm-kb"
    cache_dir = codex_home / "plugins" / "cache"
    repo_root = tmp_path / "repo"
    plugin_dir.mkdir(parents=True)
    cache_dir.mkdir(parents=True)
    _write_test_marketplace(repo_root)
    (codex_home / "config.toml").write_text(
        f'[marketplaces.flux-llm-kb-local]\nsource_type = "local"\nsource = {json.dumps(str(repo_root))}\n'
        '[plugins."flux-llm-kb@flux-llm-kb-local"]\nenabled = true\n',
        encoding="utf-8",
    )
    repo_plugin = repo_root / "plugins" / "flux-llm-kb"
    (repo_plugin / "hooks").mkdir(parents=True)
    (repo_plugin / "hooks" / "hooks.json").write_text('{"hooks": {}}', encoding="utf-8")

    monkeypatch.setattr(Path, "home", lambda: tmp_path)

    result = codex_status(repo_root=repo_root)

    assert result["installed"] is True
    assert result["configured"] is True
    assert result["discoverable"] is False
    assert result["restart_required"] is True


def test_codex_status_reports_misconfigured_marketplace_root(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    plugin_dir = codex_home / "plugins" / "flux-llm-kb"
    plugin_dir.mkdir(parents=True)
    repo_root = tmp_path / "repo"
    wrong_source = repo_root / "plugins"
    (codex_home / "config.toml").write_text(
        f'[marketplaces.flux-llm-kb-local]\nsource_type = "local"\nsource = "{wrong_source.as_posix()}"\n'
        '[plugins."flux-llm-kb@flux-llm-kb-local"]\nenabled = true\n',
        encoding="utf-8",
    )
    repo_plugin = repo_root / "plugins" / "flux-llm-kb"
    (repo_plugin / "hooks").mkdir(parents=True)
    (repo_plugin / "hooks" / "hooks.json").write_text('{"hooks": {}}', encoding="utf-8")

    monkeypatch.setattr(Path, "home", lambda: tmp_path)

    result = codex_status(repo_root=repo_root)

    assert result["configured"] is True
    assert result["marketplace_valid"] is False
    assert result["status"] == "marketplace_misconfigured"
    assert result["restart_required"] is False


def test_codex_status_uses_deployed_app_root_when_available(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    plugin_dir = codex_home / "plugins" / "flux-llm-kb"
    plugin_dir.mkdir(parents=True)
    app_root = tmp_path / "FluxLLMKB" / "app"
    _write_test_marketplace(app_root)
    (codex_home / "config.toml").write_text(
        f'[marketplaces.flux-llm-kb-local]\nsource_type = "local"\nsource = {json.dumps(str(app_root))}\n'
        '[plugins."flux-llm-kb@flux-llm-kb-local"]\nenabled = true\n',
        encoding="utf-8",
    )
    plugin = app_root / "plugins" / "flux-llm-kb"
    (plugin / ".codex-plugin").mkdir(parents=True)
    (plugin / ".codex-plugin" / "plugin.json").write_text(
        '{"name":"flux-llm-kb","version":"0.1.0","interface":{"displayName":"Flux LLM-KB"}}',
        encoding="utf-8",
    )
    (plugin / "hooks").mkdir()
    (plugin / "hooks" / "hooks.json").write_text('{"hooks": {}}', encoding="utf-8")

    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    monkeypatch.setenv("FLUX_KB_APP_ROOT", str(app_root))

    result = codex_status()

    assert result["repo_plugin_path"] == str(plugin)
    assert result["hooks_available"] is True
    assert result["manifest_valid"] is True
    assert result["status"] == "ready_restart_required"


def _write_test_marketplace(root: Path) -> None:
    path = root / ".agents" / "plugins" / "marketplace.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps({"name": "flux-llm-kb-local", "plugins": [{"name": "flux-llm-kb"}]}),
        encoding="utf-8",
    )
