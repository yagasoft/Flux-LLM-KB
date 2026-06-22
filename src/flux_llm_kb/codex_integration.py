from __future__ import annotations

import json
import os
import re
import shutil
from pathlib import Path
from typing import Any


PLUGIN_NAME = "flux-llm-kb"
MARKETPLACE_NAME = "flux-llm-kb-local"
PLUGIN_CONFIG_NAME = f"{PLUGIN_NAME}@{MARKETPLACE_NAME}"


def codex_status(*, repo_root: str | Path | None = None) -> dict[str, Any]:
    root = Path(repo_root) if repo_root else _default_root()
    codex_home = Path.home() / ".codex"
    config_path = codex_home / "config.toml"
    installed_path = codex_home / "plugins" / PLUGIN_NAME
    repo_plugin = root / "plugins" / PLUGIN_NAME
    hooks_json = repo_plugin / "hooks" / "hooks.json"
    manifest_path = repo_plugin / ".codex-plugin" / "plugin.json"

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
    installed = installed_path.exists()
    hooks_available = hooks_json.exists()
    manifest = _read_manifest(manifest_path)
    manifest_valid = manifest.get("name") == PLUGIN_NAME and bool(manifest.get("interface", {}).get("displayName"))
    discoverable = _discovery_cache_contains(codex_home)
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
        "restart_required": restart_required,
        "codex_home": str(codex_home),
        "config_path": str(config_path),
        "installed_path": str(installed_path),
        "repo_plugin_path": str(repo_plugin),
        "manifest_path": str(manifest_path),
        "message": _status_message(status, restart_required=restart_required),
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

    status = codex_status(repo_root=root)
    return {
        **status,
        "installed": installed_path.exists(),
        "configured": True,
        "restart_required": True if not status.get("discoverable") else False,
        "action": "installed",
    }


def _default_root() -> Path:
    candidates: list[Path] = []
    app_root = os.environ.get("FLUX_KB_APP_ROOT")
    if app_root:
        candidates.append(Path(app_root))
    candidates.extend(Path(__file__).resolve().parents)
    for root in candidates:
        if (root / "plugins" / PLUGIN_NAME).exists():
            return root
    return candidates[0]


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


def _remove_toml_table(text: str, table_name: str) -> str:
    escaped = re.escape(table_name)
    pattern = re.compile(rf"^\[{escaped}\]\n(?:^[^\[].*\n?)*", re.MULTILINE)
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


def _read_manifest(path: Path) -> dict[str, Any]:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return {}


def _discovery_cache_contains(codex_home: Path) -> bool:
    cache = codex_home / "plugins" / "cache"
    if not cache.exists():
        return False
    for manifest_path in cache.rglob("plugin.json"):
        if manifest_path.parent.name != ".codex-plugin":
            continue
        if _read_manifest(manifest_path).get("name") == PLUGIN_NAME:
            return True
    return False


def _marketplace_contains_plugin(path: Path | None) -> bool:
    if not path or not path.exists():
        return False
    try:
        marketplace = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return False
    return any(plugin.get("name") == PLUGIN_NAME for plugin in marketplace.get("plugins", []))


def _status_message(status: str, *, restart_required: bool) -> str:
    if status == "marketplace_misconfigured":
        return "Codex marketplace config is missing or points to the wrong root; run flux-kb codex install-plugin."
    if restart_required:
        return "Codex plugin is installed/configured; restart Codex Desktop if the Plugins UI has not indexed it yet."
    return status.replace("_", " ")
