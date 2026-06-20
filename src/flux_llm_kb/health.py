from __future__ import annotations

import html
import platform
import shutil
import subprocess
import sys
from typing import Any

from . import database
from .extractors import extractor_availability
from .watcher import summarize_watcher_staleness


def doctor_payload() -> dict[str, Any]:
    checks = {
        "python": {
            "ok": sys.version_info >= (3, 11),
            "message": platform.python_version(),
            "required": True,
        },
        "docker": _docker_check(required=False),
        "git": _command_check("git", "Git source control", required=True),
        "gh": _command_check("gh", "GitHub CLI", required=False),
    }
    db_status = database.check_database()
    checks["postgresql"] = {"ok": db_status.ok, "message": db_status.message, "required": True}
    return {
        "summary": {"ok": all(check["ok"] for check in checks.values() if check.get("required", True))},
        "checks": checks,
    }


def collect_dashboard_payload() -> dict[str, Any]:
    db_status = database.check_database()
    roots = _safe(database.list_monitored_roots, [])
    crawl = _safe(
        database.crawl_status,
        {
            "active_watch_roots": 0,
            "disabled_watch_roots": 0,
            "pending_jobs": 0,
            "failed_jobs": 0,
            "recent_errors": [],
            "watchers": [],
        },
    )
    retrieval = _safe(
        database.retrieval_stats,
        {"episodes": 0, "sources": 0, "source_assets": 0, "asset_chunks": 0, "embeddings": 0},
    )
    checks = doctor_payload()["checks"]
    watcher_summary = summarize_watcher_staleness(crawl.get("watchers", []))
    return {
        "database": {"ok": db_status.ok, "message": db_status.message},
        "runtime": {
            "python": checks["python"],
            "docker": checks["docker"],
            "git": checks["git"],
            "postgresql": checks["postgresql"],
        },
        "watcher": {
            "active_roots": crawl["active_watch_roots"],
            "disabled_roots": crawl["disabled_watch_roots"],
            "roots": roots,
            "states": watcher_summary["states"],
            "stale_count": watcher_summary["stale_count"],
        },
        "jobs": {
            "pending": crawl["pending_jobs"],
            "failed": crawl["failed_jobs"],
            "blocked": crawl.get("blocked_jobs", 0),
        },
        "retrieval": retrieval,
        "extractors": extractor_availability(),
        "duplicates": {"assets": crawl.get("duplicate_assets", retrieval.get("duplicate_assets", 0))},
        "recent_errors": crawl["recent_errors"],
        "settings": _safe(lambda: __import__("flux_llm_kb.settings", fromlist=["SettingsService"]).SettingsService().public_list(), []),
        "mail": _safe(lambda: __import__("flux_llm_kb.mail_ingestion", fromlist=["mail_status"]).mail_status(), {"enabled_profiles": 0, "profiles": []}),
    }


def collect_crawl_payload() -> dict[str, Any]:
    return {"roots": _safe(database.list_monitored_roots, []), "status": _safe(database.crawl_status, {})}


def collect_jobs_payload(limit: int = 50) -> dict[str, Any]:
    return {"jobs": _safe(lambda: database.list_capture_jobs(limit=limit), [])}


def collect_retrieval_payload() -> dict[str, Any]:
    return database.retrieval_stats()


def build_dashboard_html() -> str:
    title = "Flux-LLM-KB Dashboard"
    return f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{html.escape(title)}</title>
  <style>
    body {{ margin: 0; font-family: Arial, sans-serif; background: #f7f8fa; color: #17181c; }}
    header {{ padding: 20px 28px; border-bottom: 1px solid #d9dde5; background: #ffffff; }}
    nav {{ display: flex; gap: 8px; padding: 12px 24px; border-bottom: 1px solid #d9dde5; background: #ffffff; }}
    button {{ border: 1px solid #c9ced8; background: #ffffff; border-radius: 6px; padding: 8px 10px; cursor: pointer; }}
    main {{ display: grid; gap: 16px; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); padding: 24px; }}
    section {{ border: 1px solid #d9dde5; border-radius: 8px; background: #ffffff; padding: 16px; min-height: 132px; }}
    h1 {{ font-size: 24px; margin: 0; }}
    h2 {{ font-size: 16px; margin: 0 0 12px; }}
    h3 {{ font-size: 14px; margin: 18px 0 8px; }}
    label {{ display: grid; gap: 4px; font-size: 12px; color: #4a4f5c; }}
    input, select {{ border: 1px solid #c9ced8; border-radius: 6px; padding: 8px; font: inherit; min-width: 0; }}
    form {{ display: grid; gap: 10px; margin-bottom: 12px; }}
    .row {{ display: grid; gap: 10px; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); }}
    .hint {{ color: #5b6270; font-size: 12px; line-height: 1.4; }}
    table {{ width: 100%; border-collapse: collapse; font-size: 12px; }}
    th, td {{ text-align: left; border-bottom: 1px solid #e2e5ec; padding: 6px; vertical-align: top; }}
    td input {{ width: 100%; box-sizing: border-box; }}
    pre {{ white-space: pre-wrap; word-break: break-word; font-size: 12px; line-height: 1.45; }}
  </style>
</head>
<body>
  <header><h1>{html.escape(title)}</h1></header>
  <nav>
    <button data-tab="health">Health</button>
    <button data-tab="crawl">Crawl</button>
    <button data-tab="jobs">Jobs</button>
    <button data-tab="retrieval">Retrieval</button>
    <button data-tab="settings">Settings</button>
    <button data-tab="mail">Outlook/Mail Capture</button>
  </nav>
  <main>
    <section data-panel="watcher"><h2>Watcher</h2><pre id="watcher">Loading...</pre></section>
    <section data-panel="crawl"><h2>Crawl</h2><pre id="crawl">Loading...</pre></section>
    <section data-panel="jobs"><h2>Jobs</h2><pre id="jobs">Loading...</pre></section>
    <section data-panel="retrieval"><h2>Retrieval</h2><pre id="retrieval">Loading...</pre></section>
    <section data-panel="settings">
      <h2>Settings</h2>
      <p class="hint">settings catalog-backed runtime configuration. Values are stored by Flux, not the Windows Registry.</p>
      <form id="settings-form" onsubmit="saveSetting(event)">
        <div class="row">
          <label>Key<input id="setting-key" name="key" placeholder="retrieval.token_budget" required></label>
          <label>Value<input id="setting-value" name="value" required></label>
        </div>
        <button type="submit">Save Setting</button>
      </form>
      <div id="settings-table">Loading...</div>
    </section>
    <section data-panel="mail">
      <h2>Mail Capture</h2>
      <form id="mail-profile-form" onsubmit="createMailProfile(event)">
        <div class="row">
          <label>Name<input name="name" placeholder="gmail-capture" required></label>
          <label>Source<select name="source_type"><option value="imap">IMAP</option><option value="outlook_com">Outlook COM</option></select></label>
          <label>Account<input name="account" placeholder="me@gmail.com"></label>
          <label>Server<input name="server" value="imap.gmail.com"></label>
        </div>
        <label>Folders<input name="folder_paths" placeholder="FluxCapture,Archive/Flux" required></label>
        <label>Private Spool Path<input name="spool_path" placeholder="private/mail-spool/gmail-capture" required></label>
        <label>Post Process<select name="post_process_policy"><option value="move_to_processed">move_to_processed</option><option value="remove_label">remove_label</option><option value="none">none</option><option value="trash">trash</option></select></label>
        <button type="submit">Save Mail Profile</button>
      </form>
      <form id="oauth-start-form" onsubmit="startGmailOAuth(event)">
        <h3>Gmail OAuth</h3>
        <div class="row">
          <label>Profile<input name="profile_name" placeholder="gmail-capture" required></label>
          <label>Private Client JSON<input name="client_config_path" placeholder="private/google-oauth-client.json" required></label>
        </div>
        <button type="submit">Start Gmail OAuth</button>
      </form>
      <div id="oauth-output"></div>
      <pre id="mail">Loading...</pre>
    </section>
  </main>
  <script>
    const confirmationModes = new Set(["reload", "restart_component", "reindex_required", "manual_process_restart"]);
    async function load(id, url) {{
      const response = await fetch(url);
      document.getElementById(id).textContent = JSON.stringify(await response.json(), null, 2);
    }}
    async function json(url, options) {{
      const response = await fetch(url, options);
      const payload = await response.json();
      if (!response.ok) throw new Error(JSON.stringify(payload));
      return payload;
    }}
    function confirmSettingChange(key, applyMode) {{
      if (!confirmationModes.has(applyMode)) return true;
      return window.confirm(key + " uses apply mode " + applyMode + ". Continue?");
    }}
    async function renderSettings() {{
      const settings = await json("/api/settings");
      const rows = settings.map(item => `
        <tr>
          <td>${{item.key}}</td>
          <td>${{item.source}}</td>
          <td>${{item.sensitive ? "secret" : item.category}}</td>
          <td>${{item.apply_mode}}</td>
          <td><input data-key="${{item.key}}" data-apply="${{item.apply_mode}}" value="${{item.value ?? ""}}" ${{item.read_only ? "disabled" : ""}}></td>
          <td><button type="button" ${{item.read_only ? "disabled" : ""}} onclick="saveInlineSetting('${{item.key}}')">Save</button></td>
        </tr>`).join("");
      document.getElementById("settings-table").innerHTML = `<table><thead><tr><th>Key</th><th>Source</th><th>Type</th><th>Apply</th><th>Value</th><th></th></tr></thead><tbody>${{rows}}</tbody></table>`;
    }}
    async function saveInlineSetting(key) {{
      const input = document.querySelector(`input[data-key="${{key}}"]`);
      if (!confirmSettingChange(key, input.dataset.apply)) return;
      await json("/api/settings/" + encodeURIComponent(key), {{
        method: "PUT",
        headers: {{"Content-Type": "application/json"}},
        body: JSON.stringify({{value: input.value, confirmed: true}})
      }});
      await renderSettings();
    }}
    async function saveSetting(event) {{
      event.preventDefault();
      const key = event.target.key.value;
      const value = event.target.value.value;
      const current = (await json("/api/settings/" + encodeURIComponent(key)));
      if (!confirmSettingChange(key, current.apply_mode)) return;
      await json("/api/settings/" + encodeURIComponent(key), {{
        method: "PUT",
        headers: {{"Content-Type": "application/json"}},
        body: JSON.stringify({{value, confirmed: true}})
      }});
      event.target.reset();
      await renderSettings();
    }}
    async function createMailProfile(event) {{
      event.preventDefault();
      const data = Object.fromEntries(new FormData(event.target).entries());
      data.folder_paths = data.folder_paths.split(",").map(item => item.trim()).filter(Boolean);
      await json("/api/mail/profiles", {{
        method: "POST",
        headers: {{"Content-Type": "application/json"}},
        body: JSON.stringify(data)
      }});
      event.target.reset();
      await load("mail", "/api/mail/status");
    }}
    async function startGmailOAuth(event) {{
      event.preventDefault();
      const data = Object.fromEntries(new FormData(event.target).entries());
      const payload = await json("/api/mail/oauth/gmail/start", {{
        method: "POST",
        headers: {{"Content-Type": "application/json"}},
        body: JSON.stringify(data)
      }});
      document.getElementById("oauth-output").innerHTML = `<a href="${{payload.authorization_url}}" target="_blank" rel="noreferrer">Open Google OAuth</a><pre>${{JSON.stringify(payload, null, 2)}}</pre>`;
    }}
    load("watcher", "/api/dashboard/health");
    load("crawl", "/api/dashboard/crawl");
    load("jobs", "/api/dashboard/jobs");
    load("retrieval", "/api/dashboard/retrieval-stats");
    renderSettings();
    load("mail", "/api/mail/status");
  </script>
</body>
</html>"""


def _safe(callable_obj, fallback):
    try:
        return callable_obj()
    except Exception:
        return fallback


def _command_check(command: str, description: str, *, required: bool = True) -> dict[str, Any]:
    path = shutil.which(command)
    return {
        "ok": path is not None,
        "message": path or f"{description} command not found",
        "required": required,
    }


def _docker_check(*, required: bool = True) -> dict[str, Any]:
    path = shutil.which("docker")
    if path is None:
        return {"ok": False, "message": "Docker command not found", "required": required}
    try:
        result = subprocess.run(
            ["docker", "compose", "version"],
            text=True,
            capture_output=True,
            timeout=8,
            check=False,
        )
    except Exception as exc:  # pragma: no cover - environment-specific
        return {"ok": False, "message": str(exc), "required": required}
    if result.returncode != 0:
        message = result.stderr.strip() or result.stdout.strip() or "Docker Compose unavailable"
        return {"ok": False, "message": message, "required": required}
    return {"ok": True, "message": result.stdout.strip() or path, "required": required}
