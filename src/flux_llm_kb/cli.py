from __future__ import annotations

import argparse
import json
import platform
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any

from . import __version__, database
from .migrations import load_migrations


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="flux-kb")
    parser.add_argument("--version", action="version", version=f"flux-kb {__version__}")
    subparsers = parser.add_subparsers(dest="command", required=True)

    doctor_parser = subparsers.add_parser("doctor", help="Check local prerequisites")
    doctor_parser.add_argument("--json", action="store_true", dest="json_output")

    subparsers.add_parser("init", help="Show initialization guidance")
    subparsers.add_parser("migrate", help="Apply PostgreSQL migrations")
    subparsers.add_parser("status", help="Check database status")
    subparsers.add_parser("lint", help="Validate migrations and configuration")
    audit_parser = subparsers.add_parser("audit", help="Show recent audit events")
    audit_parser.add_argument("--limit", type=int, default=20)

    search_parser = subparsers.add_parser("search", help="Search stored episodes")
    search_parser.add_argument("query")
    search_parser.add_argument("--limit", type=int, default=5)

    remember_parser = subparsers.add_parser("remember", help="Store a manual memory")
    remember_parser.add_argument("title")
    remember_parser.add_argument("body")

    forget_parser = subparsers.add_parser("forget", help="Delete a stored memory by ID")
    forget_parser.add_argument("memory_id")
    forget_parser.add_argument("--reason", default="user_request")

    backfill_parser = subparsers.add_parser("backfill-codex", help="Queue historical Codex files for review")
    backfill_parser.add_argument("--source", default=str(Path.home() / ".codex"))
    backfill_parser.add_argument("--dry-run", action="store_true")

    export_parser = subparsers.add_parser("export-wiki", help="Export human-readable Markdown")
    export_parser.add_argument("--output", default="private/wiki-export")
    export_parser.add_argument("--limit", type=int, default=500)

    hook_parser = subparsers.add_parser("hook", help="Run a Codex hook handler")
    hook_parser.add_argument("event")

    args = parser.parse_args(argv)
    handlers = {
        "doctor": _doctor,
        "init": _init,
        "migrate": _migrate,
        "status": _status,
        "search": _search,
        "remember": _remember,
        "forget": _forget,
        "audit": _audit,
        "backfill-codex": _backfill_codex,
        "export-wiki": _export_wiki,
        "lint": _lint,
        "hook": _hook,
    }
    return handlers[args.command](args)


def _doctor(args: argparse.Namespace) -> int:
    payload = doctor_payload()
    if args.json_output:
        print(json.dumps(payload, indent=2, sort_keys=True))
    else:
        for name, check in payload["checks"].items():
            status = "ok" if check["ok"] else "missing"
            print(f"{name}: {status} - {check['message']}")
    return 0


def doctor_payload() -> dict[str, Any]:
    checks = {
        "python": {
            "ok": sys.version_info >= (3, 11),
            "message": platform.python_version(),
        },
        "docker": _docker_check(),
        "git": _command_check("git", "Git source control"),
        "gh": _command_check("gh", "GitHub CLI"),
    }
    db_status = database.check_database()
    checks["postgresql"] = {"ok": db_status.ok, "message": db_status.message}
    return {"summary": {"ok": all(check["ok"] for check in checks.values())}, "checks": checks}


def _command_check(command: str, description: str) -> dict[str, Any]:
    path = shutil.which(command)
    return {
        "ok": path is not None,
        "message": path or f"{description} command not found",
    }


def _docker_check() -> dict[str, Any]:
    path = shutil.which("docker")
    if path is None:
        return {"ok": False, "message": "Docker command not found"}
    try:
        result = subprocess.run(
            ["docker", "compose", "version"],
            text=True,
            capture_output=True,
            timeout=8,
            check=False,
        )
    except Exception as exc:  # pragma: no cover - environment-specific
        return {"ok": False, "message": str(exc)}
    if result.returncode != 0:
        message = result.stderr.strip() or result.stdout.strip() or "Docker Compose unavailable"
        return {"ok": False, "message": message}
    return {"ok": True, "message": result.stdout.strip() or path}


def _init(_: argparse.Namespace) -> int:
    print("Copy .env.example to .env, start PostgreSQL/pgvector, then run `flux-kb migrate`.")
    return 0


def _migrate(_: argparse.Namespace) -> int:
    applied = database.run_migrations()
    print(json.dumps({"applied": applied}, indent=2))
    return 0


def _status(_: argparse.Namespace) -> int:
    status = database.check_database()
    print(json.dumps({"ok": status.ok, "message": status.message}, indent=2))
    return 0 if status.ok else 1


def _search(args: argparse.Namespace) -> int:
    print(json.dumps(database.search_episodes(args.query, limit=args.limit), indent=2))
    return 0


def _remember(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    result = KnowledgeService().remember(args.title, args.body)
    print(json.dumps({"id": result.id, "redaction_count": result.redaction_count}, indent=2))
    return 0


def _audit(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    print(json.dumps(KnowledgeService().audit(limit=args.limit), indent=2))
    return 0


def _forget(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    print(json.dumps(KnowledgeService().forget(args.memory_id, reason=args.reason), indent=2))
    return 0


def _backfill_codex(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    print(
        json.dumps(
            KnowledgeService().queue_codex_backfill(args.source, dry_run=args.dry_run),
            indent=2,
        )
    )
    return 0


def _export_wiki(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    print(json.dumps(KnowledgeService().export_wiki(args.output, limit=args.limit), indent=2))
    return 0


def _lint(_: argparse.Namespace) -> int:
    migrations = load_migrations()
    sql = "\n".join(migration.sql for migration in migrations)
    required = [
        "CREATE EXTENSION IF NOT EXISTS vector",
        "CREATE EXTENSION IF NOT EXISTS pg_trgm",
        "CREATE EXTENSION IF NOT EXISTS pgcrypto",
        "USING hnsw",
    ]
    missing = [item for item in required if item not in sql]
    payload = {"migration_count": len(migrations), "ok": bool(migrations) and not missing, "missing": missing}
    print(json.dumps(payload, indent=2))
    return 0 if payload["ok"] else 1


def _hook(args: argparse.Namespace) -> int:
    from .hooks import run_hook

    output = run_hook(args.event, sys.stdin.read())
    print(json.dumps(output))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
