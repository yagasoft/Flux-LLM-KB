from __future__ import annotations

from pathlib import Path
from typing import Any


PLUGIN_NAME = "flux-llm-kb"


def codex_status(*, repo_root: str | Path | None = None) -> dict[str, Any]:
    root = Path(repo_root) if repo_root else Path(__file__).resolve().parents[2]
    codex_home = Path.home() / ".codex"
    config_path = codex_home / "config.toml"
    installed_path = codex_home / "plugins" / PLUGIN_NAME
    repo_plugin = root / "plugins" / PLUGIN_NAME
    hooks_json = repo_plugin / "hooks" / "hooks.json"

    configured = False
    if config_path.exists():
        config = config_path.read_text(encoding="utf-8", errors="ignore")
        configured = PLUGIN_NAME in config
    installed = installed_path.exists()
    hooks_available = hooks_json.exists()
    if installed and configured and hooks_available:
        status = "ready"
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
        "codex_home": str(codex_home),
        "config_path": str(config_path),
        "installed_path": str(installed_path),
        "repo_plugin_path": str(repo_plugin),
    }
