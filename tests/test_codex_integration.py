import json
from pathlib import Path

import flux_llm_kb.codex_integration as codex_integration
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
    assert "[mcp_servers.flux_llm_kb]" in config
    assert 'args = ["-m", "flux_llm_kb.mcp_server"]' in config
    assert "enabled = true" in config
    assert "startup_timeout_sec = 15" in config
    assert "tool_timeout_sec = 60" in config


def test_codex_install_plugin_preserves_unrelated_config_and_replaces_stale_mcp_block(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    codex_home.mkdir()
    repo_root = tmp_path / "repo"
    plugin = repo_root / "plugins" / "flux-llm-kb"
    (plugin / ".codex-plugin").mkdir(parents=True)
    (plugin / ".codex-plugin" / "plugin.json").write_text(
        '{"name":"flux-llm-kb","version":"0.1.0","interface":{"displayName":"Flux LLM-KB"}}',
        encoding="utf-8",
    )
    (plugin / "hooks").mkdir()
    (plugin / "hooks" / "hooks.json").write_text('{"hooks": {}}', encoding="utf-8")
    (codex_home / "config.toml").write_text(
        "\n".join(
            [
                '[mcp_servers.other]',
                'command = "other"',
                "",
                "[mcp_servers.flux_llm_kb]",
                'command = "stale-python"',
                'args = ["old"]',
                "enabled = false",
                "",
                '[profiles.personal]',
                'model = "gpt-5"',
                "",
            ]
        ),
        encoding="utf-8",
    )
    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    venv_python = repo_root / ".venv" / "Scripts" / "python.exe"
    venv_python.parent.mkdir(parents=True)
    venv_python.write_text("", encoding="utf-8")
    monkeypatch.setenv("FLUX_KB_PYTHON", "C:\\CodexBundled\\python.exe")
    monkeypatch.setattr(
        codex_integration,
        "_mcp_python_usable",
        lambda command, cwd: str(command) == str(venv_python),
        raising=False,
    )

    install_plugin(repo_root=repo_root)

    config = (codex_home / "config.toml").read_text(encoding="utf-8")
    assert config.count("[mcp_servers.flux_llm_kb]") == 1
    assert '[mcp_servers.other]' in config
    assert '[profiles.personal]' in config
    assert 'command = "stale-python"' not in config
    assert f"command = {json.dumps(str(venv_python))}" in config
    assert f"cwd = {json.dumps(str(repo_root))}" in config


def test_resolve_mcp_python_ignores_stale_env_when_app_venv_is_usable(tmp_path, monkeypatch):
    app_root = tmp_path / "app"
    venv_python = app_root / ".venv" / "Scripts" / "python.exe"
    venv_python.parent.mkdir(parents=True)
    venv_python.write_text("", encoding="utf-8")
    stale_python = tmp_path / "codex-python.exe"
    stale_python.write_text("", encoding="utf-8")
    monkeypatch.setenv("FLUX_KB_PYTHON", str(stale_python))
    monkeypatch.setattr(
        codex_integration,
        "_mcp_python_usable",
        lambda command, cwd: str(command) == str(venv_python),
        raising=False,
    )

    assert codex_integration._resolve_mcp_python(app_root) == str(venv_python)


def test_resolve_mcp_python_keeps_valid_env_override(tmp_path, monkeypatch):
    app_root = tmp_path / "app"
    requested = tmp_path / "custom-python.exe"
    requested.write_text("", encoding="utf-8")
    monkeypatch.setenv("FLUX_KB_PYTHON", str(requested))
    monkeypatch.setattr(
        codex_integration,
        "_mcp_python_usable",
        lambda command, cwd: str(command) == str(requested),
        raising=False,
    )

    assert codex_integration._resolve_mcp_python(app_root) == str(requested)


def test_codex_status_reports_mcp_configuration_health(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    codex_home.mkdir()
    app_root = tmp_path / "FluxLLMKB" / "app"
    _write_test_marketplace(app_root)
    plugin = app_root / "plugins" / "flux-llm-kb"
    (plugin / ".codex-plugin").mkdir(parents=True)
    (plugin / ".codex-plugin" / "plugin.json").write_text(
        '{"name":"flux-llm-kb","version":"0.1.0","interface":{"displayName":"Flux LLM-KB"}}',
        encoding="utf-8",
    )
    (plugin / "hooks").mkdir()
    (plugin / "hooks" / "hooks.json").write_text('{"hooks": {}}', encoding="utf-8")
    (codex_home / "config.toml").write_text(
        "\n".join(
            [
                f'[marketplaces.flux-llm-kb-local]',
                'source_type = "local"',
                f"source = {json.dumps(str(app_root))}",
                '[plugins."flux-llm-kb@flux-llm-kb-local"]',
                "enabled = true",
                "[mcp_servers.flux_llm_kb]",
                'command = "python"',
                'args = ["-m", "flux_llm_kb.mcp_server"]',
                f"cwd = {json.dumps(str(app_root))}",
                "enabled = true",
                "startup_timeout_sec = 15",
                "tool_timeout_sec = 60",
                "",
            ]
        ),
        encoding="utf-8",
    )
    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    monkeypatch.setattr(codex_integration, "_mcp_dependency_available", lambda command, cwd: True, raising=False)

    result = codex_status(repo_root=app_root)

    assert result["mcp"] == {
        "configured": True,
        "command": "python",
        "cwd": str(app_root),
        "enabled": True,
        "dependency_available": True,
        "message": "ready",
    }


def test_codex_status_reports_missing_and_dependency_missing_mcp_states(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    codex_home.mkdir()
    repo_root = tmp_path / "repo"
    _write_test_marketplace(repo_root)
    (codex_home / "config.toml").write_text(
        f'[marketplaces.flux-llm-kb-local]\nsource_type = "local"\nsource = {json.dumps(str(repo_root))}\n',
        encoding="utf-8",
    )
    monkeypatch.setattr(Path, "home", lambda: tmp_path)

    missing = codex_status(repo_root=repo_root)
    assert missing["mcp"]["configured"] is False
    assert missing["mcp"]["dependency_available"] is False
    assert "not configured" in missing["mcp"]["message"]

    (codex_home / "config.toml").write_text(
        "\n".join(
            [
                "[mcp_servers.flux_llm_kb]",
                'command = "python"',
                'args = ["-m", "flux_llm_kb.mcp_server"]',
                f"cwd = {json.dumps(str(repo_root))}",
                "enabled = true",
            ]
        ),
        encoding="utf-8",
    )
    monkeypatch.setattr(codex_integration, "_mcp_dependency_available", lambda command, cwd: False, raising=False)

    dependency_missing = codex_status(repo_root=repo_root)
    assert dependency_missing["mcp"]["configured"] is True
    assert dependency_missing["mcp"]["dependency_available"] is False
    assert "MCP optional dependency is not available" in dependency_missing["mcp"]["message"]


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


def test_codex_plugin_prompts_ask_for_indexable_final_responses():
    root = Path(__file__).resolve().parents[1]
    manifest = json.loads((root / "plugins" / "flux-llm-kb" / ".codex-plugin" / "plugin.json").read_text(encoding="utf-8"))
    skill = (root / "plugins" / "flux-llm-kb" / "skills" / "flux-memory" / "SKILL.md").read_text(encoding="utf-8")

    prompts = "\n".join(manifest["interface"]["defaultPrompt"])
    assert "Make final responses indexable" in prompts
    assert "files changed or referenced" in prompts
    assert "Make final responses indexable" in skill
    assert "commands/tests run" in skill


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
