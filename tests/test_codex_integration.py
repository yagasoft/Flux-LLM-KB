import json
import os
from pathlib import Path
import shutil
import subprocess

import pytest
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


def test_codex_install_plugin_uses_host_python_for_production_app_root(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    codex_home.mkdir()
    app_root = tmp_path / "FluxLLMKB" / "app"
    plugin = app_root / "plugins" / "flux-llm-kb"
    (plugin / ".codex-plugin").mkdir(parents=True)
    (plugin / ".codex-plugin" / "plugin.json").write_text(
        '{"name":"flux-llm-kb","version":"0.1.0","interface":{"displayName":"Flux LLM-KB"}}',
        encoding="utf-8",
    )
    (plugin / "hooks").mkdir()
    (plugin / "hooks" / "hooks.json").write_text('{"hooks": {}}', encoding="utf-8")
    (app_root / "docker-compose.yml").write_text("services: {}\n", encoding="utf-8")
    (app_root / "VERSION").write_text("abc123\n", encoding="utf-8")
    venv_python = app_root / ".venv" / "Scripts" / "python.exe"
    venv_python.parent.mkdir(parents=True)
    venv_python.write_text("", encoding="utf-8")

    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    monkeypatch.setenv("FLUX_KB_APP_ROOT", str(app_root))
    monkeypatch.setattr(
        codex_integration,
        "_mcp_python_usable",
        lambda command, cwd: str(command) == str(venv_python),
        raising=False,
    )

    install_plugin(repo_root=app_root)

    config = (codex_home / "config.toml").read_text(encoding="utf-8")
    assert f"command = {json.dumps(str(venv_python))}" in config
    assert 'args = ["-m", "flux_llm_kb.mcp_server"]' in config
    assert 'command = "docker"' not in config
    assert '"exec", "-i", "flux-llm-kb-api"' not in config
    assert f"cwd = {json.dumps(str(app_root))}" in config


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


def test_codex_mcp_readiness_rejects_container_backed_config(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    codex_home.mkdir()
    (codex_home / "config.toml").write_text(
        "\n".join(
            [
                "[mcp_servers.flux_llm_kb]",
                'command = "docker"',
                'args = ["exec", "-i", "flux-llm-kb-api", "python", "-m", "flux_llm_kb.mcp_server"]',
                f"cwd = {json.dumps(str(tmp_path))}",
                "enabled = true",
            ]
        ),
        encoding="utf-8",
    )
    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    monkeypatch.setattr(
        codex_integration,
        "_run_mcp_readiness_tools",
        lambda *args, **kwargs: (_ for _ in ()).throw(AssertionError("container config must not be spawned")),
        raising=False,
    )

    result = codex_integration.codex_mcp_readiness()

    assert result["ok"] is False
    assert result["status"] == "container_backed"
    assert result["configured"] is True
    assert result["transport_alive"] is False
    assert "docker exec" in result["message"]


def test_codex_mcp_readiness_reports_host_python_success(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    codex_home.mkdir()
    python_path = tmp_path / ".venv" / "Scripts" / "python.exe"
    python_path.parent.mkdir(parents=True)
    python_path.write_text("", encoding="utf-8")
    (codex_home / "config.toml").write_text(
        "\n".join(
            [
                "[mcp_servers.flux_llm_kb]",
                f"command = {json.dumps(str(python_path))}",
                'args = ["-m", "flux_llm_kb.mcp_server"]',
                f"cwd = {json.dumps(str(tmp_path))}",
                "enabled = true",
                "tool_timeout_sec = 60",
            ]
        ),
        encoding="utf-8",
    )
    captured = {}

    def fake_probe(command, args, cwd, timeout_seconds):
        captured.update({"command": command, "args": args, "cwd": cwd, "timeout_seconds": timeout_seconds})
        return {
            "ok": True,
            "transport_alive": True,
            "tools": [
                {"name": "kb.status", "ok": True},
                {"name": "kb.search", "ok": True},
                {"name": "kb.brief", "ok": True},
            ],
        }

    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    monkeypatch.setattr(codex_integration, "_run_mcp_readiness_tools", fake_probe, raising=False)

    result = codex_integration.codex_mcp_readiness()

    assert result["ok"] is True
    assert result["status"] == "ready"
    assert result["transport_alive"] is True
    assert captured == {
        "command": str(python_path),
        "args": ["-m", "flux_llm_kb.mcp_server"],
        "cwd": str(tmp_path),
        "timeout_seconds": 60,
    }


def test_codex_mcp_readiness_uses_fast_status_probe_only():
    assert codex_integration._MCP_READINESS_CHECKS == (("kb.status", {}),)


def test_mcp_readiness_tool_result_fails_typed_tool_errors():
    class TextContent:
        text = json.dumps(
            {
                "ok": False,
                "status": "tool_error",
                "error": {"message": "diagnostic failed"},
            }
        )

    class Result:
        isError = False
        content = [TextContent()]

    result = codex_integration._mcp_readiness_tool_result("kb.status", Result())

    assert result["ok"] is False
    assert result["status"] == "tool_error"
    assert result["message"] == "diagnostic failed"


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


def test_codex_install_plugin_replaces_dangling_existing_link(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    target = codex_home / "plugins" / "flux-llm-kb"
    target.parent.mkdir(parents=True)
    try:
        target.symlink_to(tmp_path / "missing-production-plugin", target_is_directory=True)
    except OSError:
        pytest.skip("test environment cannot create a directory symlink")

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

    assert (target / ".codex-plugin" / "plugin.json").exists()
    assert not target.is_symlink() or target.resolve() == plugin.resolve()


def test_codex_plugin_hook_manifest_uses_current_command_schema():
    root = Path(__file__).resolve().parents[1]
    manifest = json.loads(
        (root / "plugins" / "flux-llm-kb" / "hooks" / "hooks.json").read_text(encoding="utf-8")
    )
    handlers = [
        handler
        for matcher_groups in manifest["hooks"].values()
        for matcher_group in matcher_groups
        for handler in matcher_group["hooks"]
        if handler["type"] == "command"
    ]

    assert handlers
    assert all(handler.get("command") for handler in handlers)
    assert all(handler.get("commandWindows") for handler in handlers)
    assert all("command_windows" not in handler for handler in handlers)
    assert all("%PLUGIN_ROOT%" not in handler["commandWindows"] for handler in handlers)
    assert all("Join-Path $env:PLUGIN_ROOT" in handler["commandWindows"] for handler in handlers)


@pytest.mark.skipif(
    os.name != "nt" or shutil.which("powershell") is None,
    reason="Flux Windows hook wrappers require Windows PowerShell",
)
def test_windows_pre_compact_hook_falls_back_from_invalid_python_override(tmp_path):
    root = Path(__file__).resolve().parents[1]
    plugin_root = root / "plugins" / "flux-llm-kb"
    environment = os.environ.copy()
    environment["PLUGIN_ROOT"] = str(plugin_root)
    environment["FLUX_KB_PYTHON"] = str(tmp_path / "missing-python.exe")

    result = subprocess.run(
        [
            "powershell",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            "& (Join-Path $env:PLUGIN_ROOT 'scripts\\pre_compact.ps1')",
        ],
        cwd=root,
        env=environment,
        input='{"trigger":"manual"}',
        text=True,
        capture_output=True,
        check=False,
    )

    assert result.returncode == 0, result.stderr
    assert json.loads(result.stdout) == {"continue": True}


@pytest.mark.skipif(
    os.name != "nt" or shutil.which("powershell") is None,
    reason="Flux Windows hook wrappers require Windows PowerShell",
)
def test_windows_pre_compact_hook_reports_python_failure(tmp_path):
    root = Path(__file__).resolve().parents[1]
    plugin_root = root / "plugins" / "flux-llm-kb"
    fake_python = tmp_path / "fake-python.cmd"
    fake_python.write_text(
        "@echo off\r\n"
        'if "%~1"=="-c" exit /b 0\r\n'
        "exit /b 23\r\n",
        encoding="utf-8",
    )
    environment = os.environ.copy()
    environment["PLUGIN_ROOT"] = str(plugin_root)
    environment["FLUX_KB_PYTHON"] = str(fake_python)

    result = subprocess.run(
        [
            "powershell",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            "& (Join-Path $env:PLUGIN_ROOT 'scripts\\pre_compact.ps1')",
        ],
        cwd=root,
        env=environment,
        input='{"trigger":"manual"}',
        text=True,
        capture_output=True,
        check=False,
    )

    assert result.returncode != 0


def test_codex_plugin_prompts_ask_for_indexable_final_responses():
    root = Path(__file__).resolve().parents[1]
    manifest = json.loads((root / "plugins" / "flux-llm-kb" / ".codex-plugin" / "plugin.json").read_text(encoding="utf-8"))
    skill = (root / "plugins" / "flux-llm-kb" / "skills" / "flux-memory" / "SKILL.md").read_text(encoding="utf-8")

    prompts = "\n".join(manifest["interface"]["defaultPrompt"])
    manifest_text = json.dumps(manifest, sort_keys=True)
    assert "Make final responses indexable" in prompts
    assert "files changed or referenced" in prompts
    assert "concise redacted durable saves" in prompts
    assert "reusable mid-turn outcomes" in prompts
    assert "Make final responses indexable" in skill
    assert "commands/tests run" in skill
    assert "mcp__flux_llm_kb.kb_brief" in skill
    assert "mid-turn" in skill
    assert "prior decisions" in skill
    assert "workspace_boosted" in skill
    assert "local files, the prompt, or current tool output already answer" in skill
    assert "normal kb.brief/search for broad context" in skill
    assert "kb.code_search" in skill
    assert "kb.code_symbol_lookup" in skill
    assert "Do not infer `root_name` from folder names" in skill
    assert "kb.code_status(cwd=" in skill
    assert 'mode="literal_symbol"' in skill
    assert 'mode="full_text"' in skill
    assert 'filters={"file_kinds":["code"]}' in skill
    assert "as the only file kind" in skill
    assert "mixed code plus non-code file kinds are rejected" in skill
    assert "Broad `kb.search`, `kb.brief`, and `kb.explain` exclude code results by default" in skill
    assert "mcp__flux_llm_kb.kb_code_status" in skill
    assert "mcp__flux_llm_kb.kb_code_symbol_lookup" in skill
    assert "kb.code_search" in prompts
    assert "kb.code_status" in prompts
    assert "kb.code_symbol_lookup" in prompts
    assert "Do not infer root_name from folder names" in prompts
    assert "mode=\"full_text\"" in prompts
    assert 'filters={"file_kinds":["code"]}' in prompts
    assert "as the only file kind" in prompts
    assert "separate broad non-code and code-specific calls" in prompts
    assert "broad search/brief excludes code" in prompts
    assert "code-heavy results" not in prompts
    assert "mcp__flux_llm_kb.kb_remember" in skill
    assert "durable atomic saves" in skill
    assert "concise, redacted, and scoped" in skill
    assert "mcp__flux_llm_kb.kb_finalize_turn" in skill
    assert "avoid duplicating every prior" in skill
    assert "active workspace `cwd`" in skill
    assert "code-search" in manifest["keywords"]
    assert "code-status" in manifest["keywords"]
    assert "symbol-lookup" in manifest["keywords"]
    assert "kb.code_status" in manifest_text
    assert "kb.code_search" in manifest_text
    assert "kb.code_symbol_lookup" in manifest_text


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


def test_codex_status_reports_stale_discovery_cache(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    installed = codex_home / "plugins" / "flux-llm-kb"
    repo_root = tmp_path / "repo"
    _write_test_marketplace(repo_root)
    _write_test_plugin(repo_root, skill_text="fresh guidance with kb_search kb_remember kb_finalize_turn")
    installed.mkdir(parents=True)
    _write_cached_plugin(
        codex_home,
        skill_text="old guidance with kb.brief only",
        manifest_description="old cached manifest",
    )
    (codex_home / "config.toml").write_text(
        f'[marketplaces.flux-llm-kb-local]\nsource_type = "local"\nsource = {json.dumps(str(repo_root))}\n'
        '[plugins."flux-llm-kb@flux-llm-kb-local"]\nenabled = true\n',
        encoding="utf-8",
    )

    monkeypatch.setattr(Path, "home", lambda: tmp_path)

    result = codex_status(repo_root=repo_root)

    assert result["discoverable"] is False
    assert result["restart_required"] is True
    assert result["status"] == "ready_restart_required"
    assert result["discovery_cache"]["state"] == "stale"
    assert result["discovery_cache"]["fresh"] is False
    assert any(path.endswith("SKILL.md") for path in result["discovery_cache"]["stale_files"])
    assert "stale" in result["message"]


def test_codex_status_reports_fresh_discovery_cache(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    installed = codex_home / "plugins" / "flux-llm-kb"
    repo_root = tmp_path / "repo"
    skill_text = "fresh guidance with kb_search kb_remember kb_finalize_turn workspace_boosted"
    _write_test_marketplace(repo_root)
    _write_test_plugin(repo_root, skill_text=skill_text)
    installed.mkdir(parents=True)
    _write_cached_plugin(codex_home, skill_text=skill_text)
    (codex_home / "config.toml").write_text(
        f'[marketplaces.flux-llm-kb-local]\nsource_type = "local"\nsource = {json.dumps(str(repo_root))}\n'
        '[plugins."flux-llm-kb@flux-llm-kb-local"]\nenabled = true\n',
        encoding="utf-8",
    )

    monkeypatch.setattr(Path, "home", lambda: tmp_path)

    result = codex_status(repo_root=repo_root)

    assert result["discoverable"] is True
    assert result["restart_required"] is False
    assert result["status"] == "ready"
    assert result["discovery_cache"]["state"] == "fresh"
    assert result["discovery_cache"]["fresh"] is True
    assert result["discovery_cache"]["stale_paths"] == []


def test_codex_status_checks_cache_against_configured_marketplace_source(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    dev_root = tmp_path / "dev"
    app_root = tmp_path / "FluxLLMKB" / "app"
    skill_text = "deployed guidance with kb_search kb_remember kb_finalize_turn"
    _write_test_marketplace(app_root)
    _write_test_plugin(app_root, skill_text=skill_text)
    _write_test_plugin(dev_root, skill_text="different dev checkout guidance")
    (codex_home / "plugins" / "flux-llm-kb").mkdir(parents=True)
    _write_cached_plugin(codex_home, skill_text=skill_text)
    (codex_home / "config.toml").write_text(
        f'[marketplaces.flux-llm-kb-local]\nsource_type = "local"\nsource = {json.dumps(str(app_root))}\n'
        '[plugins."flux-llm-kb@flux-llm-kb-local"]\nenabled = true\n',
        encoding="utf-8",
    )

    monkeypatch.setattr(Path, "home", lambda: tmp_path)

    result = codex_status(repo_root=dev_root)

    assert result["discoverable"] is True
    assert result["discovery_cache"]["state"] == "fresh"
    assert result["plugin_source_path"] == str(app_root / "plugins" / "flux-llm-kb")
    assert result["repo_plugin_path"] == str(dev_root / "plugins" / "flux-llm-kb")


def test_codex_install_plugin_without_repo_root_preserves_configured_marketplace_source(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    codex_home.mkdir()
    dev_root = tmp_path / "dev"
    app_root = tmp_path / "FluxLLMKB" / "app"
    _write_test_marketplace(app_root)
    _write_test_plugin(app_root, skill_text="deployed guidance")
    _write_test_plugin(dev_root, skill_text="dev checkout guidance")
    (codex_home / "config.toml").write_text(
        f'[marketplaces.flux-llm-kb-local]\nsource_type = "local"\nsource = {json.dumps(str(app_root))}\n'
        '[plugins."flux-llm-kb@flux-llm-kb-local"]\nenabled = true\n',
        encoding="utf-8",
    )
    fake_module = dev_root / "src" / "flux_llm_kb" / "codex_integration.py"
    fake_module.parent.mkdir(parents=True)
    fake_module.write_text("", encoding="utf-8")

    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    monkeypatch.delenv("FLUX_KB_APP_ROOT", raising=False)
    monkeypatch.setattr(codex_integration, "__file__", str(fake_module))

    result = install_plugin()

    config = (codex_home / "config.toml").read_text(encoding="utf-8")
    assert f"source = {json.dumps(str(app_root))}" in config
    assert f"source = {json.dumps(str(dev_root))}" not in config
    assert result["plugin_source_path"] == str(app_root / "plugins" / "flux-llm-kb")
    assert result["repo_plugin_path"] == str(app_root / "plugins" / "flux-llm-kb")


def test_codex_install_plugin_invalidates_stale_discovery_cache_only(tmp_path, monkeypatch):
    codex_home = tmp_path / ".codex"
    repo_root = tmp_path / "repo"
    stale_cache = _write_cached_plugin(
        codex_home,
        skill_text="old guidance with kb.brief only",
        manifest_description="old cached manifest",
    )
    unrelated_cache = codex_home / "plugins" / "cache" / "other-marketplace" / "other-plugin" / "1.0.0"
    (unrelated_cache / ".codex-plugin").mkdir(parents=True)
    (unrelated_cache / ".codex-plugin" / "plugin.json").write_text(
        '{"name":"other-plugin","version":"1.0.0","interface":{"displayName":"Other"}}',
        encoding="utf-8",
    )
    _write_test_plugin(repo_root, skill_text="fresh guidance with kb_search kb_remember kb_finalize_turn")

    monkeypatch.setattr(Path, "home", lambda: tmp_path)

    result = install_plugin(repo_root=repo_root)

    assert not stale_cache.exists()
    assert unrelated_cache.exists()
    assert result["discoverable"] is False
    assert result["restart_required"] is True
    assert result["discovery_cache"]["state"] == "missing"


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


def _write_test_plugin(root: Path, *, skill_text: str) -> Path:
    plugin = root / "plugins" / "flux-llm-kb"
    (plugin / ".codex-plugin").mkdir(parents=True)
    (plugin / ".codex-plugin" / "plugin.json").write_text(
        json.dumps(
            {
                "name": "flux-llm-kb",
                "version": "0.1.0",
                "description": "test plugin",
                "skills": "./skills/",
                "hooks": "./hooks/hooks.json",
                "interface": {"displayName": "Flux LLM-KB"},
            }
        ),
        encoding="utf-8",
    )
    (plugin / "skills" / "flux-memory").mkdir(parents=True)
    (plugin / "skills" / "flux-memory" / "SKILL.md").write_text(skill_text, encoding="utf-8")
    (plugin / "hooks").mkdir()
    (plugin / "hooks" / "hooks.json").write_text('{"hooks": {}}', encoding="utf-8")
    return plugin


def _write_cached_plugin(codex_home: Path, *, skill_text: str, manifest_description: str = "test plugin") -> Path:
    cache = codex_home / "plugins" / "cache" / "flux-llm-kb-local" / "flux-llm-kb" / "0.1.0"
    (cache / ".codex-plugin").mkdir(parents=True)
    (cache / ".codex-plugin" / "plugin.json").write_text(
        json.dumps(
            {
                "name": "flux-llm-kb",
                "version": "0.1.0",
                "description": manifest_description,
                "skills": "./skills/",
                "hooks": "./hooks/hooks.json",
                "interface": {"displayName": "Flux LLM-KB"},
            }
        ),
        encoding="utf-8",
    )
    (cache / "skills" / "flux-memory").mkdir(parents=True)
    (cache / "skills" / "flux-memory" / "SKILL.md").write_text(skill_text, encoding="utf-8")
    (cache / "hooks").mkdir()
    (cache / "hooks" / "hooks.json").write_text('{"hooks": {}}', encoding="utf-8")
    return cache
