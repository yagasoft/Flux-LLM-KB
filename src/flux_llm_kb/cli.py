from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from . import __version__, database
from .health import doctor_payload
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

    crawl_parser = subparsers.add_parser("crawl", help="Manage recursive corpus crawling")
    crawl_subparsers = crawl_parser.add_subparsers(dest="crawl_command", required=True)

    crawl_add = crawl_subparsers.add_parser("add", help="Add or update a monitored root")
    crawl_add.add_argument("path")
    crawl_add.add_argument("--name", required=True)
    crawl_add.add_argument("--watch", action="store_true", help="Enable watch mode for this root")
    crawl_add.add_argument("--no-recursive", action="store_false", dest="recursive")
    crawl_add.set_defaults(recursive=True)

    crawl_subparsers.add_parser("list", help="List monitored roots")

    crawl_sync = crawl_subparsers.add_parser("sync", help="Run a one-shot crawl")
    target = crawl_sync.add_mutually_exclusive_group()
    target.add_argument("--root")
    target.add_argument("--path")
    crawl_sync.add_argument("--dry-run", action="store_true")

    crawl_jobs = crawl_subparsers.add_parser("jobs", help="List corpus extraction jobs")
    crawl_jobs.add_argument("--limit", type=int, default=50)

    crawl_backfill = crawl_subparsers.add_parser("backfill", help="Claim deferred corpus extraction jobs")
    crawl_backfill.add_argument("--kind", choices=["text", "images", "media", "embeddings", "all"], default="all")
    crawl_backfill.add_argument("--limit", type=int, default=10)
    crawl_backfill.add_argument("--workers", type=int, default=1)

    crawl_subparsers.add_parser("doctor", help="Show crawler and watcher health")

    watch_parser = crawl_subparsers.add_parser("watch", help="Manage watch mode")
    watch_subparsers = watch_parser.add_subparsers(dest="watch_command", required=True)
    watch_run = watch_subparsers.add_parser("run", help="Run the foreground polling watcher")
    watch_run.add_argument("--root")
    watch_run.add_argument("--interval", type=float, default=2.0)
    watch_enable = watch_subparsers.add_parser("enable", help="Enable watch mode")
    watch_enable.add_argument("--root")
    watch_enable.add_argument("--all", action="store_true")
    watch_disable = watch_subparsers.add_parser("disable", help="Disable watch mode")
    watch_disable.add_argument("--root")
    watch_disable.add_argument("--all", action="store_true")
    watch_subparsers.add_parser("status", help="Show watcher status")

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
        "crawl": _crawl,
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
    from .service import KnowledgeService

    print(json.dumps(KnowledgeService().search(args.query, limit=args.limit), indent=2))
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


def _crawl(args: argparse.Namespace) -> int:
    if args.crawl_command == "add":
        payload = database.add_monitored_root(
            name=args.name,
            root_path=str(Path(args.path).expanduser().resolve()),
            recursive=args.recursive,
            watch_enabled=args.watch,
        )
    elif args.crawl_command == "list":
        payload = database.list_monitored_roots()
    elif args.crawl_command == "sync":
        from .service import KnowledgeService

        payload = KnowledgeService().sync_corpus(root_name=args.root, path=args.path, dry_run=args.dry_run)
    elif args.crawl_command == "jobs":
        payload = {"jobs": database.list_capture_jobs(limit=args.limit)}
    elif args.crawl_command == "backfill":
        from .service import KnowledgeService

        payload = KnowledgeService().run_corpus_backfill(kind=args.kind, limit=args.limit, workers=args.workers)
    elif args.crawl_command == "doctor":
        from .health import collect_dashboard_payload

        payload = collect_dashboard_payload()
    elif args.crawl_command == "watch":
        payload = _crawl_watch(args)
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.crawl_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _crawl_watch(args: argparse.Namespace):
    if args.watch_command == "enable":
        if not args.all and not args.root:
            raise SystemExit("watch enable requires --root or --all")
        return database.set_watch_enabled(root_name=None if args.all else args.root, enabled=True)
    if args.watch_command == "disable":
        if not args.all and not args.root:
            raise SystemExit("watch disable requires --root or --all")
        return database.set_watch_enabled(root_name=None if args.all else args.root, enabled=False)
    if args.watch_command == "status":
        return database.crawl_status()
    if args.watch_command == "run":
        from .service import KnowledgeService

        return KnowledgeService().run_watch(root_name=args.root, interval_seconds=args.interval)
    raise ValueError(args.watch_command)  # pragma: no cover - argparse prevents this


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
