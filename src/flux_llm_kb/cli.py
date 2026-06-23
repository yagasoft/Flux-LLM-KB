from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from . import __version__, database
from .health import doctor_payload
from .migrations import load_migrations
from .settings import SettingsService


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

    claim_parser = subparsers.add_parser("claim", help="Manage durable claims")
    claim_subparsers = claim_parser.add_subparsers(dest="claim_command", required=True)
    claim_upsert = claim_subparsers.add_parser("upsert", help="Create or update a claim")
    claim_upsert.add_argument("--subject-type", required=True)
    claim_upsert.add_argument("--subject", required=True)
    claim_upsert.add_argument("--predicate", required=True)
    claim_upsert.add_argument("--object", required=True, dest="object_text")
    claim_upsert.add_argument("--confidence", type=float, default=0.5)
    claim_upsert.add_argument("--episode-id")
    claim_transition = claim_subparsers.add_parser("transition", help="Apply a lifecycle transition to a claim")
    claim_transition.add_argument("claim_id")
    claim_transition.add_argument(
        "transition",
        choices=["reinforce", "confirm", "supersede", "contradict", "stale", "deprioritize", "retire", "delete"],
    )
    claim_transition.add_argument("--related-claim-id")
    claim_transition.add_argument("--reason")
    claim_transition.add_argument("--confidence-delta", type=float, default=0.0)

    graph_parser = subparsers.add_parser("graph", help="Traverse entity graph relations")
    graph_subparsers = graph_parser.add_subparsers(dest="graph_command", required=True)
    graph_traverse = graph_subparsers.add_parser("traverse", help="Traverse relations from an entity")
    graph_traverse.add_argument("entity_id")
    graph_traverse.add_argument("--relation-type", action="append", dest="relation_types")
    graph_traverse.add_argument("--max-depth", type=int, default=2)
    graph_traverse.add_argument("--direction", choices=["out", "in", "both"], default="out")
    graph_traverse.add_argument("--limit", type=int, default=100)

    capture_parser = subparsers.add_parser("capture", help="Manage capture review workflows")
    capture_subparsers = capture_parser.add_subparsers(dest="capture_command", required=True)
    capture_review_parser = capture_subparsers.add_parser("review", help="Review pending capture jobs")
    capture_review_subparsers = capture_review_parser.add_subparsers(
        dest="capture_review_command",
        required=True,
    )
    capture_review_list = capture_review_subparsers.add_parser("list", help="List pending capture review jobs")
    capture_review_list.add_argument("--limit", type=int, default=50)
    capture_review_decide = capture_review_subparsers.add_parser("decide", help="Approve or reject a capture review job")
    capture_review_decide.add_argument("job_id")
    capture_review_decide.add_argument("--decision", required=True, choices=["approve", "reject"])
    capture_review_decide.add_argument("--rationale", required=True)

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
    crawl_add.add_argument("--glob-mode", choices=["inherit", "extend", "override"], default="extend")
    crawl_add.set_defaults(recursive=True)

    crawl_edit = crawl_subparsers.add_parser("edit", help="Edit a monitored root")
    crawl_edit.add_argument("root")
    crawl_edit.add_argument("--name")
    crawl_edit.add_argument("--path")
    crawl_edit.add_argument("--enable", action="store_true")
    crawl_edit.add_argument("--disable", action="store_true")
    crawl_edit_watch = crawl_edit.add_mutually_exclusive_group()
    crawl_edit_watch.add_argument("--enable-watch", action="store_true")
    crawl_edit_watch.add_argument("--disable-watch", action="store_true")
    crawl_edit.add_argument("--recursive", action="store_true")
    crawl_edit.add_argument("--no-recursive", action="store_true")
    crawl_edit.add_argument("--trust-rank", type=int)
    crawl_edit.add_argument("--include-glob", action="append")
    crawl_edit.add_argument("--exclude-glob", action="append")
    crawl_edit.add_argument("--glob-mode", choices=["inherit", "extend", "override"])
    crawl_edit.add_argument("--max-inline-bytes", type=int)
    crawl_edit.add_argument("--heavy-threshold-bytes", type=int)

    crawl_delete = crawl_subparsers.add_parser("delete", help="Delete a monitored root and purge indexed files")
    crawl_delete.add_argument("root")
    crawl_delete.add_argument("--purge-index", action="store_true", required=True)

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

    crawl_worker = crawl_subparsers.add_parser("worker", help="Run the corpus extraction worker")
    crawl_worker_subparsers = crawl_worker.add_subparsers(dest="worker_command", required=True)
    crawl_worker_run = crawl_worker_subparsers.add_parser("run", help="Run the corpus worker loop")
    crawl_worker_run.add_argument("--kind", choices=["text", "images", "media", "embeddings", "all"], default="all")
    crawl_worker_run.add_argument("--limit", type=int, default=10)
    crawl_worker_run.add_argument("--workers", type=int, default=1)
    crawl_worker_run.add_argument("--interval", type=float, default=5.0)
    crawl_worker_run.add_argument("--once", action="store_true")
    crawl_worker_run.add_argument("--host-agent-roots", action="store_true", help="Process only host-agent owned roots")
    crawl_worker_run.add_argument(
        "--exclude-host-agent-roots",
        action="store_true",
        help="Skip host-agent owned roots so Docker workers do not open host paths",
    )

    crawl_subparsers.add_parser("doctor", help="Show crawler and watcher health")

    watch_parser = crawl_subparsers.add_parser("watch", help="Manage watch mode")
    watch_subparsers = watch_parser.add_subparsers(dest="watch_command", required=True)
    watch_run = watch_subparsers.add_parser("run", help="Run the foreground polling watcher")
    watch_run.add_argument("--root")
    watch_run.add_argument("--interval", type=float)
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

    codex_parser = subparsers.add_parser("codex", help="Install and inspect Codex integration")
    codex_subparsers = codex_parser.add_subparsers(dest="codex_command", required=True)
    codex_subparsers.add_parser("install-plugin", help="Install/link the local Flux Codex plugin")
    codex_status_parser = codex_subparsers.add_parser("status", help="Show Codex plugin status")
    codex_status_parser.add_argument("--json", action="store_true", dest="json_output")

    settings_parser = subparsers.add_parser("settings", help="Manage runtime settings")
    settings_subparsers = settings_parser.add_subparsers(dest="settings_command", required=True)
    settings_subparsers.add_parser("list", help="List runtime settings")
    settings_get = settings_subparsers.add_parser("get", help="Get one runtime setting")
    settings_get.add_argument("key")
    settings_set = settings_subparsers.add_parser("set", help="Set one runtime setting")
    settings_set.add_argument("key")
    settings_set.add_argument("value")
    settings_set.add_argument("--confirm", action="store_true")
    settings_reset = settings_subparsers.add_parser("reset", help="Reset one runtime setting to default/env")
    settings_reset.add_argument("key")
    settings_apply = settings_subparsers.add_parser("apply", help="Acknowledge pending runtime control requests")
    settings_apply.add_argument("--component")

    mail_parser = subparsers.add_parser("mail", help="Manage mail ingestion")
    mail_subparsers = mail_parser.add_subparsers(dest="mail_command", required=True)
    mail_profile = mail_subparsers.add_parser("profile", help="Manage mail profiles")
    mail_profile_subparsers = mail_profile.add_subparsers(dest="mail_profile_command", required=True)
    mail_add_imap = mail_profile_subparsers.add_parser("add-imap", help="Add an IMAP mail profile")
    mail_add_imap.add_argument("--name", required=True)
    mail_add_imap.add_argument("--account", required=True)
    mail_add_imap.add_argument("--server", default="imap.gmail.com")
    mail_add_imap.add_argument("--folder", action="append", required=True)
    mail_add_imap.add_argument("--spool", required=True)
    mail_add_imap.add_argument("--post-process", default="move_to_processed")
    _add_mail_schedule_args(mail_add_imap)
    mail_add_outlook = mail_profile_subparsers.add_parser("add-outlook", help="Add an Outlook COM catch-up profile")
    mail_add_outlook.add_argument("--name", required=True)
    mail_add_outlook.add_argument("--folder", action="append", required=True)
    mail_add_outlook.add_argument("--spool", required=True)
    mail_add_outlook.add_argument("--post-process", default="move_to_processed")
    _add_mail_schedule_args(mail_add_outlook)
    mail_profile_subparsers.add_parser("list", help="List mail profiles")
    mail_subparsers.add_parser("status", help="Show mail ingestion status")
    mail_sync = mail_subparsers.add_parser("sync", help="Sync exported mail spool into the corpus")
    mail_sync.add_argument("--profile")
    mail_watch = mail_subparsers.add_parser("watch", help="Run a foreground mail watcher")
    mail_watch_subparsers = mail_watch.add_subparsers(dest="mail_watch_command", required=True)
    mail_watch_run = mail_watch_subparsers.add_parser("run", help="Run mail reconciliation loop")
    mail_watch_run.add_argument("--profile")
    mail_oauth = mail_subparsers.add_parser("oauth", help="Manage mail OAuth")
    mail_oauth_subparsers = mail_oauth.add_subparsers(dest="mail_oauth_command", required=True)
    gmail_oauth = mail_oauth_subparsers.add_parser("gmail", help="Gmail IMAP OAuth")
    gmail_oauth_subparsers = gmail_oauth.add_subparsers(dest="gmail_oauth_command", required=True)
    gmail_oauth_start = gmail_oauth_subparsers.add_parser("start", help="Start Gmail installed-app OAuth")
    gmail_oauth_start.add_argument("--profile", required=True)
    gmail_oauth_start.add_argument("--client-config", required=True)
    gmail_oauth_start.add_argument("--redirect-uri")
    gmail_oauth_complete = gmail_oauth_subparsers.add_parser("complete", help="Complete Gmail OAuth from callback code")
    gmail_oauth_complete.add_argument("--state", required=True)
    gmail_oauth_complete.add_argument("--code", required=True)
    mail_oauth_status = mail_oauth_subparsers.add_parser("status", help="Show mail OAuth status")
    mail_oauth_status.add_argument("--profile")
    mail_render = mail_subparsers.add_parser("render-outlook-config", help="Render an Outlook COM catch-up config")
    mail_render.add_argument("--profile", required=True)
    mail_render.add_argument("--spool", required=True)
    mail_render.add_argument("--folder", action="append", required=True)

    outlook_host_parser = subparsers.add_parser("outlook-host", help="Run the Windows Outlook COM bridge")
    outlook_host_subparsers = outlook_host_parser.add_subparsers(dest="outlook_host_command", required=True)
    outlook_host_run = outlook_host_subparsers.add_parser("run", help="Run the Outlook COM host loop")
    outlook_host_run.add_argument("--host-id", default="default")
    outlook_host_run.add_argument("--interval-seconds", type=int, default=15)
    outlook_host_subparsers.add_parser("status", help="Show Outlook COM host status")
    outlook_host_sync = outlook_host_subparsers.add_parser("sync", help="Request an Outlook COM profile sync")
    outlook_host_sync.add_argument("--profile", required=True)

    host_agent_parser = subparsers.add_parser("host-agent", help="Run the local filesystem host agent")
    host_agent_subparsers = host_agent_parser.add_subparsers(dest="host_agent_command", required=True)
    host_agent_run = host_agent_subparsers.add_parser("run", help="Run the local host-agent REST bridge")
    host_agent_run.add_argument("--host", default="127.0.0.1")
    host_agent_run.add_argument("--port", type=int, default=8799)
    host_agent_subparsers.add_parser("status", help="Show local host-agent status")
    host_agent_subparsers.add_parser("browse", help="Open a native folder picker")

    args = parser.parse_args(argv)
    handlers = {
        "doctor": _doctor,
        "init": _init,
        "migrate": _migrate,
        "status": _status,
        "search": _search,
        "remember": _remember,
        "claim": _claim,
        "graph": _graph,
        "capture": _capture,
        "forget": _forget,
        "audit": _audit,
        "backfill-codex": _backfill_codex,
        "crawl": _crawl,
        "export-wiki": _export_wiki,
        "lint": _lint,
        "hook": _hook,
        "codex": _codex,
        "settings": _settings,
        "mail": _mail,
        "outlook-host": _outlook_host,
        "host-agent": _host_agent,
    }
    return handlers[args.command](args)


def _add_mail_schedule_args(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--sync-enabled", action="store_true", help="Enable scheduled sync for this profile")
    parser.add_argument("--sync-interval-seconds", type=int, default=900)
    parser.add_argument("--sync-window-days", type=int, default=30)
    parser.add_argument("--max-messages-per-run", type=int, default=200)


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


def _claim(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    service = KnowledgeService()
    if args.claim_command == "upsert":
        payload = service.upsert_claim(
            subject_type=args.subject_type,
            subject_name=args.subject,
            predicate=args.predicate,
            object_text=args.object_text,
            confidence=args.confidence,
            episode_id=args.episode_id,
        )
    elif args.claim_command == "transition":
        payload = service.transition_claim(
            claim_id=args.claim_id,
            transition=args.transition,
            related_claim_id=args.related_claim_id,
            reason=args.reason,
            confidence_delta=args.confidence_delta,
            actor="cli",
        )
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.claim_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _graph(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    if args.graph_command != "traverse":  # pragma: no cover - argparse prevents this
        raise ValueError(args.graph_command)
    payload = KnowledgeService().traverse_graph(
        entity_id=args.entity_id,
        relation_types=args.relation_types,
        max_depth=args.max_depth,
        direction=args.direction,
        limit=args.limit,
    )
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _capture(args: argparse.Namespace) -> int:
    if args.capture_command == "review":
        return _capture_review(args)
    raise ValueError(args.capture_command)  # pragma: no cover - argparse prevents this


def _capture_review(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    service = KnowledgeService()
    if args.capture_review_command == "list":
        payload = service.list_capture_review_jobs(limit=args.limit)
    elif args.capture_review_command == "decide":
        payload = service.review_capture_job(
            job_id=args.job_id,
            decision=args.decision,
            rationale=args.rationale,
            actor="cli",
        )
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.capture_review_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
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
        settings = SettingsService()
        payload = database.add_monitored_root(
            name=args.name,
            root_path=args.path,
            recursive=args.recursive,
            watch_enabled=args.watch,
            glob_mode=args.glob_mode,
            max_inline_bytes=int(settings.resolve("crawler.max_inline_bytes").raw_value),
            heavy_threshold_bytes=int(settings.resolve("crawler.heavy_threshold_bytes").raw_value),
        )
    elif args.crawl_command == "list":
        payload = database.list_monitored_roots()
    elif args.crawl_command == "edit":
        payload = _crawl_edit(args)
    elif args.crawl_command == "delete":
        payload = database.delete_monitored_root(root_id=args.root, purge_index=args.purge_index, actor="cli")
    elif args.crawl_command == "sync":
        from .service import KnowledgeService

        payload = KnowledgeService().sync_corpus(root_name=args.root, path=args.path, dry_run=args.dry_run)
    elif args.crawl_command == "jobs":
        payload = {"jobs": database.list_capture_jobs(limit=args.limit)}
    elif args.crawl_command == "backfill":
        from .service import KnowledgeService

        payload = KnowledgeService().run_corpus_backfill(kind=args.kind, limit=args.limit, workers=args.workers)
    elif args.crawl_command == "worker":
        from .service import KnowledgeService

        if args.worker_command != "run":  # pragma: no cover - argparse prevents this
            raise ValueError(args.worker_command)
        host_agent_roots = None
        component_name = "corpus-worker:manual"
        if args.host_agent_roots:
            host_agent_roots = True
            component_name = "corpus-worker:host-agent"
        elif args.exclude_host_agent_roots:
            host_agent_roots = False
            component_name = "corpus-worker:docker"
        payload = KnowledgeService().run_corpus_worker(
            kind=args.kind,
            limit=args.limit,
            workers=args.workers,
            interval_seconds=args.interval,
            once=args.once,
            host_agent_roots=host_agent_roots,
            component_name=component_name,
        )
    elif args.crawl_command == "doctor":
        from .health import collect_dashboard_payload

        payload = collect_dashboard_payload()
    elif args.crawl_command == "watch":
        payload = _crawl_watch(args)
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.crawl_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _crawl_edit(args: argparse.Namespace) -> dict:
    existing = database.get_monitored_root_by_identifier(args.root)
    if existing is None:
        raise SystemExit(f"monitored root not found: {args.root}")
    if args.enable and args.disable:
        raise SystemExit("choose either --enable or --disable")
    if args.recursive and args.no_recursive:
        raise SystemExit("choose either --recursive or --no-recursive")
    watch_enabled = existing["watch_enabled"]
    if args.enable_watch:
        watch_enabled = True
    if args.disable_watch:
        watch_enabled = False
    enabled = existing["enabled"]
    if args.enable:
        enabled = True
    if args.disable:
        enabled = False
    recursive = existing["recursive"]
    if args.recursive:
        recursive = True
    if args.no_recursive:
        recursive = False
    return database.update_monitored_root(
        root_id=existing["id"],
        name=args.name or existing["name"],
        root_path=args.path or existing["root_path"],
        enabled=enabled,
        recursive=recursive,
        watch_enabled=watch_enabled,
        trust_rank=args.trust_rank if args.trust_rank is not None else existing["trust_rank"],
        include_globs=args.include_glob if args.include_glob is not None else existing["include_globs"],
        exclude_globs=args.exclude_glob if args.exclude_glob is not None else existing["exclude_globs"],
        glob_mode=args.glob_mode or existing["glob_mode"],
        max_inline_bytes=args.max_inline_bytes if args.max_inline_bytes is not None else existing["max_inline_bytes"],
        heavy_threshold_bytes=(
            args.heavy_threshold_bytes if args.heavy_threshold_bytes is not None else existing["heavy_threshold_bytes"]
        ),
        metadata={"source": "cli"},
    )


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

        interval = args.interval
        if interval is None:
            interval = float(SettingsService().resolve("watcher.interval_seconds").raw_value)
        return KnowledgeService().run_watch(root_name=args.root, interval_seconds=interval)
    raise ValueError(args.watch_command)  # pragma: no cover - argparse prevents this


def _settings(args: argparse.Namespace) -> int:
    service = SettingsService()
    if args.settings_command == "list":
        payload = [setting.to_public_dict() for setting in service.list()]
    elif args.settings_command == "get":
        payload = service.resolve(args.key).to_public_dict()
    elif args.settings_command == "set":
        payload = service.set(args.key, args.value, actor="cli", confirmed=args.confirm)
    elif args.settings_command == "reset":
        payload = service.reset(args.key, actor="cli")
    elif args.settings_command == "apply":
        payload = service.apply(component=args.component, actor="cli")
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.settings_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _codex(args: argparse.Namespace) -> int:
    from .codex_integration import codex_status, install_plugin

    if args.codex_command == "install-plugin":
        payload = install_plugin()
    elif args.codex_command == "status":
        payload = codex_status()
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.codex_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _mail(args: argparse.Namespace) -> int:
    from . import mail_ingestion

    if args.mail_command == "profile":
        if args.mail_profile_command == "add-imap":
            payload = mail_ingestion.add_mail_profile(
                name=args.name,
                source_type="imap",
                account=args.account,
                server=args.server,
                folder_paths=args.folder,
                spool_path=args.spool,
                post_process_policy=args.post_process,
                sync_enabled=args.sync_enabled,
                sync_interval_seconds=args.sync_interval_seconds,
                sync_window_days=args.sync_window_days,
                max_messages_per_run=args.max_messages_per_run,
            )
        elif args.mail_profile_command == "add-outlook":
            payload = mail_ingestion.add_mail_profile(
                name=args.name,
                source_type="outlook_com",
                account=None,
                server=None,
                folder_paths=args.folder,
                spool_path=args.spool,
                post_process_policy=args.post_process,
                sync_enabled=args.sync_enabled,
                sync_interval_seconds=args.sync_interval_seconds,
                sync_window_days=args.sync_window_days,
                max_messages_per_run=args.max_messages_per_run,
            )
        elif args.mail_profile_command == "list":
            payload = database.list_mail_profiles()
        else:  # pragma: no cover - argparse prevents this
            raise ValueError(args.mail_profile_command)
    elif args.mail_command == "status":
        payload = mail_ingestion.mail_status()
    elif args.mail_command == "sync":
        payload = mail_ingestion.sync_mail_profile(profile_name=args.profile)
    elif args.mail_command == "watch":
        if args.mail_watch_command != "run":  # pragma: no cover - argparse prevents this
            raise ValueError(args.mail_watch_command)
        payload = _mail_watch_run(args.profile)
    elif args.mail_command == "oauth":
        payload = _mail_oauth(args)
    elif args.mail_command == "render-outlook-config":
        print(mail_ingestion.render_outlook_config(args.profile, spool_path=args.spool, folder_paths=args.folder), end="")
        return 0
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.mail_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _outlook_host(args: argparse.Namespace) -> int:
    from . import outlook_host

    if args.outlook_host_command == "status":
        payload = outlook_host.status()
    elif args.outlook_host_command == "sync":
        payload = outlook_host.request_sync(args.profile, actor="cli")
    elif args.outlook_host_command == "run":
        payload = outlook_host.run_forever(host_id=args.host_id, interval_seconds=args.interval_seconds)
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.outlook_host_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _host_agent(args: argparse.Namespace) -> int:
    from . import host_agent

    if args.host_agent_command == "run":
        payload = host_agent.run_server(host=args.host, port=args.port)
    elif args.host_agent_command == "status":
        payload = host_agent.status_payload()
    elif args.host_agent_command == "browse":
        payload = host_agent.browse_folder()
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.host_agent_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _mail_oauth(args: argparse.Namespace) -> dict:
    from . import mail_oauth

    if args.mail_oauth_command == "gmail":
        if args.gmail_oauth_command == "start":
            return mail_oauth.start_gmail_oauth(
                profile_name=args.profile,
                client_config_path=args.client_config,
                redirect_uri=args.redirect_uri,
            )
        if args.gmail_oauth_command == "complete":
            return mail_oauth.complete_gmail_oauth(state=args.state, code=args.code)
        raise ValueError(args.gmail_oauth_command)
    if args.mail_oauth_command == "status":
        return mail_oauth.oauth_status(profile_name=args.profile)
    raise ValueError(args.mail_oauth_command)


def _mail_watch_run(profile_name: str | None) -> dict:
    import time

    from .mail_ingestion import sync_mail_profile
    from .settings import SettingsService

    interval = int(SettingsService().resolve("mail.imap.poll_interval_seconds").raw_value)
    while True:
        sync_mail_profile(profile_name=profile_name)
        time.sleep(interval)


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
