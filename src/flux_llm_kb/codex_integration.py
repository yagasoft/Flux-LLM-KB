from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any

from .processes import run_no_window


PLUGIN_NAME = "flux-llm-kb"
MARKETPLACE_NAME = "flux-llm-kb-local"
PLUGIN_CONFIG_NAME = f"{PLUGIN_NAME}@{MARKETPLACE_NAME}"
MCP_SERVER_NAME = "flux_llm_kb"
_MCP_READINESS_CHECKS = (
    ("kb.status", {}),
)
DISCOVERY_CACHE_DIRS = (".codex-plugin", "skills", "hooks", "scripts")


def codex_status(*, repo_root: str | Path | None = None) -> dict[str, Any]:
    root = Path(repo_root) if repo_root else _default_root()
    codex_home = Path.home() / ".codex"
    config_path = codex_home / "config.toml"
    installed_path = codex_home / "plugins" / PLUGIN_NAME
    repo_plugin = root / "plugins" / PLUGIN_NAME

    config = ""
    configured = False
    marketplace_source: Path | None = None
    marketplace_path: Path | None = None
    marketplace_valid = False
    if config_path.exists():
        config = config_path.read_text(encoding="utf-8", errors="ignore")
        configured = PLUGIN_NAME in config
        marketplace_source = _read_local_marketplace_source(config)
        if marketplace_source:
            marketplace_path = _marketplace_file(marketplace_source)
            marketplace_valid = _marketplace_contains_plugin(marketplace_path)
    plugin_source = repo_plugin
    if marketplace_source and marketplace_valid:
        plugin_source = marketplace_source / "plugins" / PLUGIN_NAME
    hooks_json = plugin_source / "hooks" / "hooks.json"
    manifest_path = plugin_source / ".codex-plugin" / "plugin.json"
    mcp = _flux_mcp_status(config)
    installed = installed_path.exists()
    hooks_available = hooks_json.exists()
    manifest = _read_manifest(manifest_path)
    manifest_valid = manifest.get("name") == PLUGIN_NAME and bool(manifest.get("interface", {}).get("displayName"))
    discovery_cache = _discovery_cache_status(codex_home, plugin_source)
    discoverable = bool(discovery_cache["fresh"])
    restart_required = installed and configured and marketplace_valid and hooks_available and not discoverable
    if installed and configured and not marketplace_valid:
        status = "marketplace_misconfigured"
    elif installed and configured and hooks_available and discoverable:
        status = "ready"
    elif installed and configured and hooks_available:
        status = "ready_restart_required"
    elif configured and not installed:
        status = "configured_not_installed"
    elif installed and not configured:
        status = "installed_not_configured"
    elif hooks_available:
        status = "scaffold_available"
    else:
        status = "missing"
    return {
        "status": status,
        "configured": configured,
        "installed": installed,
        "hooks_available": hooks_available,
        "manifest_valid": manifest_valid,
        "marketplace_source": str(marketplace_source) if marketplace_source else None,
        "marketplace_path": str(marketplace_path) if marketplace_path else None,
        "marketplace_valid": marketplace_valid,
        "discoverable": discoverable,
        "discovery_cache": discovery_cache,
        "restart_required": restart_required,
        "codex_home": str(codex_home),
        "config_path": str(config_path),
        "installed_path": str(installed_path),
        "repo_plugin_path": str(repo_plugin),
        "plugin_source_path": str(plugin_source),
        "manifest_path": str(manifest_path),
        "mcp": mcp,
        "message": _status_message(status, restart_required=restart_required, discovery_cache=discovery_cache),
    }


def install_plugin(*, repo_root: str | Path | None = None) -> dict[str, Any]:
    root = Path(repo_root) if repo_root else _default_root()
    codex_home = Path.home() / ".codex"
    plugin_source = root / "plugins" / PLUGIN_NAME
    if not plugin_source.exists():
        raise FileNotFoundError(f"plugin source not found: {plugin_source}")
    manifest_path = plugin_source / ".codex-plugin" / "plugin.json"
    manifest = _read_manifest(manifest_path)
    if manifest.get("name") != PLUGIN_NAME:
        raise ValueError(f"invalid Flux plugin manifest at {manifest_path}")

    codex_home.mkdir(parents=True, exist_ok=True)
    plugins_dir = codex_home / "plugins"
    plugins_dir.mkdir(parents=True, exist_ok=True)
    installed_path = plugins_dir / PLUGIN_NAME
    _install_plugin_path(plugin_source, installed_path)
    _write_local_marketplace(root)
    _write_local_marketplace_config(codex_home / "config.toml", root)
    _write_flux_mcp_server_config(codex_home / "config.toml", root)
    invalidated_cache_paths = _invalidate_stale_discovery_cache(codex_home, plugin_source)

    status = codex_status(repo_root=root)
    return {
        **status,
        "installed": installed_path.exists(),
        "configured": True,
        "restart_required": True if not status.get("discoverable") else False,
        "invalidated_discovery_cache_paths": invalidated_cache_paths,
        "action": "installed",
    }


def _default_root() -> Path:
    candidates: list[Path] = []
    app_root = os.environ.get("FLUX_KB_APP_ROOT")
    if app_root:
        candidates.append(Path(app_root))
    configured_source = _configured_marketplace_source_root()
    if configured_source:
        candidates.append(configured_source)
    candidates.extend(Path(__file__).resolve().parents)
    for root in candidates:
        if (root / "plugins" / PLUGIN_NAME).exists():
            return root
    return candidates[0]


def _configured_marketplace_source_root() -> Path | None:
    config_path = Path.home() / ".codex" / "config.toml"
    if not config_path.exists():
        return None
    try:
        config = config_path.read_text(encoding="utf-8", errors="ignore")
    except OSError:
        return None
    source = _read_local_marketplace_source(config)
    if not source:
        return None
    if not (source / "plugins" / PLUGIN_NAME).exists():
        return None
    if not _marketplace_contains_plugin(_marketplace_file(source)):
        return None
    return source


def _install_plugin_path(source: Path, target: Path) -> None:
    if target.exists():
        try:
            if target.resolve() == source.resolve():
                return
        except OSError:
            pass
        _remove_existing_plugin_path(target)
    try:
        target.symlink_to(source, target_is_directory=True)
    except OSError:
        shutil.copytree(source, target)


def _remove_existing_plugin_path(target: Path) -> None:
    if target.is_symlink() or getattr(target, "is_junction", lambda: False)():
        target.rmdir()
    elif target.is_dir():
        shutil.rmtree(target)
    else:
        target.unlink()


def _marketplace_file(root: Path) -> Path:
    return root / ".agents" / "plugins" / "marketplace.json"


def _write_local_marketplace(root: Path) -> Path:
    marketplace_path = _marketplace_file(root)
    marketplace_path.parent.mkdir(parents=True, exist_ok=True)
    marketplace = {
        "name": MARKETPLACE_NAME,
        "interface": {"displayName": "Flux LLM-KB Local"},
        "plugins": [
            {
                "name": PLUGIN_NAME,
                "source": {"source": "local", "path": f"./plugins/{PLUGIN_NAME}"},
                "policy": {"installation": "AVAILABLE", "authentication": "ON_INSTALL"},
                "category": "Developer Tools",
            }
        ],
    }
    marketplace_path.write_text(json.dumps(marketplace, indent=2) + "\n", encoding="utf-8")
    return marketplace_path


def _write_local_marketplace_config(config_path: Path, source_dir: Path) -> None:
    existing = config_path.read_text(encoding="utf-8", errors="ignore") if config_path.exists() else ""
    existing = _remove_toml_table(existing, f"marketplaces.{MARKETPLACE_NAME}")
    existing = _remove_toml_table(existing, f'plugins."{PLUGIN_CONFIG_NAME}"')
    addition = "\n".join(
        [
            f"[marketplaces.{MARKETPLACE_NAME}]",
            'last_updated = "2026-06-21T00:00:00Z"',
            'source_type = "local"',
            f"source = {_toml_string(str(source_dir))}",
            "",
            f'[plugins."{PLUGIN_CONFIG_NAME}"]',
            "enabled = true",
            "",
        ]
    )
    config_path.write_text(existing.rstrip() + "\n\n" + addition, encoding="utf-8")


def _write_flux_mcp_server_config(config_path: Path, app_root: Path) -> None:
    existing = config_path.read_text(encoding="utf-8", errors="ignore") if config_path.exists() else ""
    existing = _remove_toml_table(existing, f"mcp_servers.{MCP_SERVER_NAME}")
    command, args = _resolve_mcp_command(app_root)
    addition = "\n".join(
        [
            f"[mcp_servers.{MCP_SERVER_NAME}]",
            f"command = {_toml_string(command)}",
            f"args = {json.dumps(args)}",
            f"cwd = {_toml_string(str(app_root))}",
            "enabled = true",
            "startup_timeout_sec = 15",
            "tool_timeout_sec = 60",
            "",
        ]
    )
    config_path.write_text(existing.rstrip() + "\n\n" + addition, encoding="utf-8")


def _resolve_mcp_command(app_root: Path) -> tuple[str, list[str]]:
    return _resolve_mcp_python(app_root), ["-m", "flux_llm_kb.mcp_server"]


def _is_production_app_root(app_root: Path) -> bool:
    configured_root = os.environ.get("FLUX_KB_APP_ROOT")
    if configured_root:
        try:
            if Path(configured_root).resolve() == app_root.resolve():
                return (app_root / "docker-compose.yml").exists()
        except OSError:
            if Path(configured_root) == app_root:
                return (app_root / "docker-compose.yml").exists()
    return (app_root / "docker-compose.yml").exists() and (app_root / "VERSION").exists()


def _docker_mcp_available() -> bool:
    try:
        result = run_no_window(
            ["docker", "exec", "flux-llm-kb-api", "python", "-c", "import flux_llm_kb; import mcp.server.fastmcp"],
            text=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=8,
            check=False,
        )
    except Exception:
        return False
    return result.returncode == 0


def _resolve_mcp_python(app_root: Path) -> str:
    requested = os.environ.get("FLUX_KB_PYTHON")
    venv_candidates = (
        app_root / ".venv" / "Scripts" / "python.exe",
        app_root / ".venv" / "bin" / "python",
    )
    candidates: list[str] = []
    if requested:
        candidates.append(requested)
    candidates.extend(str(candidate) for candidate in venv_candidates)
    candidates.append(sys.executable)
    for candidate in candidates:
        if _mcp_python_usable(candidate, app_root):
            return candidate
    for candidate in venv_candidates:
        if candidate.exists():
            return str(candidate)
    if requested:
        return requested
    return sys.executable


def _remove_toml_table(text: str, table_name: str) -> str:
    escaped = re.escape(table_name)
    pattern = re.compile(rf"^\[{escaped}\]\n(?:^(?!\[).*\n?)*", re.MULTILINE)
    return pattern.sub("", text)


def _toml_string(value: str) -> str:
    return json.dumps(value)


def _read_local_marketplace_source(config: str) -> Path | None:
    match = re.search(
        rf"^\[marketplaces\.{re.escape(MARKETPLACE_NAME)}\]\n(?:^[^\[].*\n?)*?^source\s*=\s*(.+)$",
        config,
        re.MULTILINE,
    )
    if not match:
        return None
    raw = match.group(1).strip()
    try:
        value = json.loads(raw)
    except json.JSONDecodeError:
        value = raw.strip("'\"")
    return Path(value) if value else None


def _flux_mcp_status(config: str) -> dict[str, Any]:
    block = _read_toml_table_block(config, f"mcp_servers.{MCP_SERVER_NAME}")
    if block is None:
        return {
            "configured": False,
            "command": None,
            "cwd": None,
            "enabled": False,
            "dependency_available": False,
            "message": "Flux MCP server is not configured; run flux-kb codex install-plugin.",
        }
    values = _parse_simple_toml_table(block)
    command = _optional_str(values.get("command"))
    cwd = _optional_str(values.get("cwd"))
    args = values.get("args")
    enabled = bool(values.get("enabled", True))
    dependency_available = bool(command and cwd and enabled and _configured_mcp_dependency_available(command, cwd, args))
    if not enabled:
        message = "Flux MCP server is configured but disabled."
    elif not command or not cwd:
        message = "Flux MCP server config is incomplete."
    elif not dependency_available:
        if command == "docker":
            message = "Flux MCP server is configured for the API container, but the container is not available."
        else:
            message = "MCP optional dependency is not available for the configured Flux Python."
    else:
        message = "ready"
    return {
        "configured": True,
        "command": command,
        "cwd": cwd,
        "enabled": enabled,
        "dependency_available": dependency_available,
        "message": message,
    }


def _configured_mcp_dependency_available(command: str, cwd: str, args: Any) -> bool:
    if _is_container_backed_mcp_config(command, args):
        return False
    return _mcp_dependency_available(command, cwd)


def codex_mcp_readiness() -> dict[str, Any]:
    codex_home = Path.home() / ".codex"
    config_path = codex_home / "config.toml"
    base: dict[str, Any] = {
        "configured": False,
        "config_path": str(config_path),
        "command": None,
        "args": None,
        "cwd": None,
        "transport_alive": False,
        "tools": [],
    }
    if not config_path.exists():
        return {
            **base,
            "ok": False,
            "status": "not_configured",
            "message": "Codex config.toml was not found; run flux-kb codex install-plugin.",
        }
    config = config_path.read_text(encoding="utf-8", errors="ignore")
    block = _read_toml_table_block(config, f"mcp_servers.{MCP_SERVER_NAME}")
    if block is None:
        return {
            **base,
            "ok": False,
            "status": "not_configured",
            "message": "Flux MCP server is not configured; run flux-kb codex install-plugin.",
        }
    values = _parse_simple_toml_table(block)
    command = _optional_str(values.get("command"))
    cwd = _optional_str(values.get("cwd"))
    args = values.get("args")
    enabled = bool(values.get("enabled", True))
    timeout_seconds = int(values.get("tool_timeout_sec") or 60)
    base.update({"configured": True, "command": command, "args": args, "cwd": cwd, "enabled": enabled})
    if not enabled:
        return {
            **base,
            "ok": False,
            "status": "disabled",
            "message": "Flux MCP server is configured but disabled.",
        }
    if not command or not cwd or not isinstance(args, list):
        return {
            **base,
            "ok": False,
            "status": "misconfigured",
            "message": "Flux MCP server config is incomplete.",
        }
    if _is_container_backed_mcp_config(command, args):
        return {
            **base,
            "ok": False,
            "status": "container_backed",
            "transport_alive": False,
            "message": "Flux MCP server uses docker exec; reinstall the Codex plugin so the MCP process runs from the host production venv.",
        }

    try:
        probe = _run_mcp_readiness_tools(command, args, cwd, timeout_seconds)
    except Exception as exc:
        return {
            **base,
            "ok": False,
            "status": "transport_closed",
            "transport_alive": False,
            "tools": [],
            "message": f"Flux MCP readiness probe failed before tool checks completed: {exc}",
        }

    tools = list(probe.get("tools") or [])
    transport_alive = bool(probe.get("transport_alive"))
    temporary = [tool for tool in tools if tool.get("status") == "temporary_unavailable" or tool.get("temporary_unavailable")]
    failed = [tool for tool in tools if not tool.get("ok")]
    if not transport_alive:
        status = "transport_closed"
        ok = False
        message = probe.get("message") or "Flux MCP readiness probe transport closed."
    elif temporary:
        status = "temporary_unavailable"
        ok = False
        message = "Flux MCP server is alive but the backend is temporarily unavailable."
    elif failed:
        status = "tool_error"
        ok = False
        message = "Flux MCP server is alive but one or more readiness tools failed."
    else:
        status = "ready"
        ok = True
        message = "ready"
    return {
        **base,
        **probe,
        "ok": ok,
        "status": status,
        "transport_alive": transport_alive,
        "tools": tools,
        "message": message,
    }


def _is_container_backed_mcp_config(command: str | None, args: Any) -> bool:
    return command == "docker" and isinstance(args, list) and args[:3] == ["exec", "-i", "flux-llm-kb-api"]


def _run_mcp_readiness_tools(command: str, args: list[Any], cwd: str, timeout_seconds: int) -> dict[str, Any]:
    import anyio

    return anyio.run(_run_mcp_readiness_tools_async, command, [str(arg) for arg in args], cwd, timeout_seconds)


async def _run_mcp_readiness_tools_async(
    command: str,
    args: list[str],
    cwd: str,
    timeout_seconds: int,
) -> dict[str, Any]:
    import anyio
    from mcp import ClientSession, StdioServerParameters
    from mcp.client.stdio import stdio_client

    params = StdioServerParameters(command=command, args=args, cwd=cwd, env=os.environ.copy())
    tools: list[dict[str, Any]] = []
    try:
        with anyio.fail_after(timeout_seconds):
            async with stdio_client(params) as (read_stream, write_stream):
                async with ClientSession(read_stream, write_stream) as session:
                    await session.initialize()
                    for name, arguments in _MCP_READINESS_CHECKS:
                        result = await session.call_tool(name, arguments)
                        tools.append(_mcp_readiness_tool_result(name, result))
    except Exception as exc:
        return {
            "ok": False,
            "transport_alive": False,
            "tools": tools,
            "message": f"Flux MCP transport closed or failed during readiness probe: {exc}",
        }
    return {
        "ok": all(tool.get("ok") for tool in tools),
        "transport_alive": True,
        "tools": tools,
    }


def _mcp_readiness_tool_result(name: str, result: Any) -> dict[str, Any]:
    is_error = bool(getattr(result, "isError", False))
    payload = _mcp_result_payload(result)
    status = None
    temporary = False
    payload_failed = False
    message = None
    if isinstance(payload, dict):
        status = _optional_str(payload.get("status"))
        temporary = status == "temporary_unavailable"
        payload_failed = payload.get("ok") is False or status in {"temporary_unavailable", "tool_error"}
        error = payload.get("error") if isinstance(payload.get("error"), dict) else {}
        message = _optional_str(error.get("message")) or _optional_str(payload.get("message"))
    ok = not is_error and not payload_failed
    return {
        "name": name,
        "ok": ok,
        "is_error": is_error,
        "status": status or ("tool_error" if is_error else "ok"),
        "temporary_unavailable": temporary,
        "message": message,
    }


def _mcp_result_payload(result: Any) -> Any:
    content = getattr(result, "content", None)
    if not content:
        return None
    text = getattr(content[0], "text", None)
    if text is None:
        return None
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return text


def _read_toml_table_block(config: str, table_name: str) -> str | None:
    escaped = re.escape(table_name)
    match = re.search(rf"^\[{escaped}\]\n(?P<body>(?:^(?!\[).*\n?)*)", config, re.MULTILINE)
    return match.group("body") if match else None


def _parse_simple_toml_table(block: str) -> dict[str, Any]:
    values: dict[str, Any] = {}
    for raw_line in block.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, raw_value = line.split("=", 1)
        values[key.strip()] = _parse_simple_toml_value(raw_value.strip())
    return values


def _parse_simple_toml_value(raw_value: str) -> Any:
    if raw_value in {"true", "false"}:
        return raw_value == "true"
    if raw_value.startswith('"') and raw_value.endswith('"'):
        try:
            return json.loads(raw_value)
        except json.JSONDecodeError:
            return raw_value.strip('"')
    if raw_value.startswith("["):
        try:
            return json.loads(raw_value)
        except json.JSONDecodeError:
            return raw_value
    try:
        return int(raw_value)
    except ValueError:
        return raw_value


def _mcp_dependency_available(command: str, cwd: str) -> bool:
    return _mcp_python_usable(command, Path(cwd))


def _mcp_python_usable(command: str | Path, cwd: str | Path) -> bool:
    try:
        result = run_no_window(
            [str(command), "-c", "import flux_llm_kb; import mcp.server.fastmcp"],
            cwd=str(cwd),
            text=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=5,
            check=False,
        )
    except Exception:
        return False
    return result.returncode == 0


def _optional_str(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if text else None


def _read_manifest(path: Path) -> dict[str, Any]:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return {}


def _discovery_cache_status(codex_home: Path, plugin_source: Path) -> dict[str, Any]:
    cache_roots = _flux_discovery_cache_roots(codex_home)
    if not cache_roots:
        return {
            "state": "missing",
            "fresh": False,
            "paths": [],
            "fresh_paths": [],
            "stale_paths": [],
            "stale_files": [],
        }

    fresh_paths: list[str] = []
    stale_paths: list[str] = []
    stale_files: list[str] = []
    for cache_root in cache_roots:
        mismatches = _discovery_cache_mismatches(plugin_source, cache_root)
        if mismatches:
            stale_paths.append(str(cache_root))
            stale_files.extend(mismatches)
        else:
            fresh_paths.append(str(cache_root))

    fresh = bool(fresh_paths) and not stale_paths
    return {
        "state": "fresh" if fresh else "stale",
        "fresh": fresh,
        "paths": [str(path) for path in cache_roots],
        "fresh_paths": fresh_paths,
        "stale_paths": stale_paths,
        "stale_files": stale_files,
    }


def _invalidate_stale_discovery_cache(codex_home: Path, plugin_source: Path) -> list[str]:
    cache_roots = _flux_discovery_cache_roots(codex_home)
    if not cache_roots:
        return []
    invalidated: list[str] = []
    for cache_root in cache_roots:
        if not _discovery_cache_mismatches(plugin_source, cache_root):
            continue
        _remove_discovery_cache_root(codex_home, cache_root)
        invalidated.append(str(cache_root))
    return invalidated


def _flux_discovery_cache_roots(codex_home: Path) -> list[Path]:
    cache = codex_home / "plugins" / "cache"
    if not cache.exists():
        return []
    roots: list[Path] = []
    for manifest_path in cache.rglob("plugin.json"):
        if manifest_path.parent.name != ".codex-plugin":
            continue
        if _read_manifest(manifest_path).get("name") == PLUGIN_NAME:
            roots.append(manifest_path.parent.parent)
    return sorted(roots, key=lambda path: str(path))


def _discovery_cache_mismatches(plugin_source: Path, cache_root: Path) -> list[str]:
    mismatches: list[str] = []
    for relative_path in _plugin_discovery_files(plugin_source):
        source_path = plugin_source / relative_path
        cache_path = cache_root / relative_path
        if not cache_path.is_file():
            mismatches.append(str(cache_path))
            continue
        try:
            if source_path.read_bytes() != cache_path.read_bytes():
                mismatches.append(str(cache_path))
        except OSError:
            mismatches.append(str(cache_path))
    return mismatches


def _plugin_discovery_files(plugin_source: Path) -> list[Path]:
    files: list[Path] = []
    for directory_name in DISCOVERY_CACHE_DIRS:
        directory = plugin_source / directory_name
        if not directory.exists():
            continue
        for path in directory.rglob("*"):
            if path.is_file():
                files.append(path.relative_to(plugin_source))
    return sorted(files, key=lambda path: path.as_posix())


def _remove_discovery_cache_root(codex_home: Path, cache_root: Path) -> None:
    cache_base = (codex_home / "plugins" / "cache").resolve()
    resolved_cache_root = cache_root.resolve()
    if resolved_cache_root == cache_base or cache_base not in resolved_cache_root.parents:
        raise ValueError(f"refusing to remove cache path outside Codex plugin cache: {cache_root}")
    shutil.rmtree(resolved_cache_root)


def _marketplace_contains_plugin(path: Path | None) -> bool:
    if not path or not path.exists():
        return False
    try:
        marketplace = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return False
    return any(plugin.get("name") == PLUGIN_NAME for plugin in marketplace.get("plugins", []))


def _status_message(status: str, *, restart_required: bool, discovery_cache: dict[str, Any] | None = None) -> str:
    if status == "marketplace_misconfigured":
        return "Codex marketplace config is missing or points to the wrong root; run flux-kb codex install-plugin."
    if discovery_cache and discovery_cache.get("state") == "stale":
        return "Codex plugin discovery cache is stale; run flux-kb codex install-plugin to invalidate it, then restart Codex Desktop."
    if restart_required:
        return "Codex plugin is installed/configured; restart Codex Desktop if the Plugins UI has not indexed it yet."
    return status.replace("_", " ")
