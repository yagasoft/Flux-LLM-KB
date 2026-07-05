from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

from . import __version__, database
from .acceleration import JOB_FAMILIES
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
    search_parser.add_argument("--root", dest="root_name")
    _add_retrieval_filter_args(search_parser)

    explain_parser = subparsers.add_parser("explain", help="Search with snippets, ranking signals, and brief packing rationale")
    explain_parser.add_argument("query")
    explain_parser.add_argument("--limit", type=int, default=5)
    explain_parser.add_argument("--token-budget", type=int)
    explain_parser.add_argument("--cwd")
    explain_parser.add_argument("--root-name")
    explain_parser.add_argument("--scope-mode", default="local_first")
    _add_retrieval_filter_args(explain_parser)

    retrieval_parser = subparsers.add_parser("retrieval", help="Evaluate retrieval quality")
    retrieval_subparsers = retrieval_parser.add_subparsers(dest="retrieval_command", required=True)
    retrieval_benchmark = retrieval_subparsers.add_parser("benchmark", help="Run or inspect retrieval benchmark history")
    retrieval_benchmark_subparsers = retrieval_benchmark.add_subparsers(dest="retrieval_benchmark_command", required=True)
    retrieval_benchmark_run = retrieval_benchmark_subparsers.add_parser("run", help="Run the synthetic retrieval benchmark suite")
    retrieval_benchmark_run.add_argument("--suite", default="standard")
    retrieval_benchmark_run.add_argument("--label")
    retrieval_benchmark_run.add_argument("--compare-label")
    retrieval_benchmark_run.add_argument("--limit-per-query", type=int, default=5)
    retrieval_benchmark_run.add_argument("--token-budget", type=int)
    retrieval_benchmark_run.add_argument("--no-persist", action="store_false", dest="persist")
    retrieval_benchmark_run.set_defaults(persist=True)
    retrieval_benchmark_history = retrieval_benchmark_subparsers.add_parser("history", help="List retrieval benchmark history")
    retrieval_benchmark_history.add_argument("--suite", default="standard")
    retrieval_benchmark_history.add_argument("--label")
    retrieval_benchmark_history.add_argument("--limit", type=int, default=20)

    search_index_parser = subparsers.add_parser("search-index", help="Manage the active Vespa search index")
    search_index_subparsers = search_index_parser.add_subparsers(dest="search_index_command", required=True)
    search_index_sync = search_index_subparsers.add_parser("sync", help="Queue Vespa search-index sync work")
    search_index_sync.add_argument("--owner-class", choices=["all", "corpus", "episodes", "claims"], default="all")
    search_index_sync.add_argument("--root", dest="root_name")
    search_index_sync.add_argument("--limit", type=int, default=250)
    search_index_status = search_index_subparsers.add_parser("status", help="Show Vespa search-index sync state")
    search_index_status.add_argument("--root", dest="root_name")
    search_index_rebuild = search_index_subparsers.add_parser("rebuild", help="Mark Vespa search-index records pending for rebuild")
    search_index_rebuild.add_argument("--root", dest="root_name")
    search_index_rebuild.add_argument("--confirm", action="store_true")
    search_index_purge_deleted = search_index_subparsers.add_parser(
        "purge-deleted-corpora",
        help="Purge Vespa and derived-cache residue for deleted corpus roots",
    )
    search_index_purge_deleted.add_argument("--confirm", action="store_true")

    maintenance_parser = subparsers.add_parser("maintenance", help="Run confirmation-gated maintenance operations")
    maintenance_subparsers = maintenance_parser.add_subparsers(dest="maintenance_command", required=True)
    maintenance_reprocess = maintenance_subparsers.add_parser("reprocess", help="Refresh derived corpus, OCR/ASR, and search-index state")
    maintenance_scope = maintenance_reprocess.add_mutually_exclusive_group(required=True)
    maintenance_scope.add_argument("--all-roots", action="store_true")
    maintenance_scope.add_argument("--root", dest="root_name")
    maintenance_reprocess.add_argument("--confirm", action="store_true")
    maintenance_reprocess.add_argument("--force", action="store_true")
    maintenance_reprocess.add_argument("--clear-caches", default="all")
    maintenance_reprocess.add_argument("--process", action="store_true")
    maintenance_reprocess.add_argument("--limit", type=int, default=1000)
    maintenance_reprocess.add_argument("--workers", type=int)
    maintenance_reprocess.add_argument("--max-passes", type=int, default=1)

    automation_parser = subparsers.add_parser("automation", help="Inspect or run guarded operator automation")
    automation_subparsers = automation_parser.add_subparsers(dest="automation_command", required=True)
    automation_subparsers.add_parser("status", help="Show guarded automation status and eligible actions")
    automation_run = automation_subparsers.add_parser("run", help="Run one guarded automation pass now")
    automation_run.add_argument("--mode", default="guarded", choices=["guarded", "suggest_only"])
    automation_run.add_argument("--limit", type=int, default=25)
    automation_run.add_argument("--dry-run", action="store_true")
    automation_actions = automation_subparsers.add_parser("actions", help="List recent guarded automation actions")
    automation_actions.add_argument("--status", default="all")
    automation_actions.add_argument("--run-id")
    automation_actions.add_argument("--action")
    automation_actions.add_argument("--limit", type=int, default=50)

    governance_parser = subparsers.add_parser("governance", help="Run and review evaluated memory governance automation")
    governance_subparsers = governance_parser.add_subparsers(dest="governance_command", required=True)
    governance_run = governance_subparsers.add_parser("run", help="Generate a governance proposal run")
    governance_run.add_argument("--mode", default="shadow")
    governance_run.add_argument("--limit", type=int, default=25)
    governance_actions = governance_subparsers.add_parser("actions", help="List, apply, or recover governance actions")
    governance_actions_subparsers = governance_actions.add_subparsers(dest="governance_actions_command", required=True)
    governance_actions_list = governance_actions_subparsers.add_parser("list", help="List governance actions")
    governance_actions_list.add_argument("--status", default="proposed")
    governance_actions_list.add_argument("--limit", type=int, default=50)
    governance_actions_apply = governance_actions_subparsers.add_parser("apply", help="Apply a confirmed governance action")
    governance_actions_apply.add_argument("action_id")
    governance_actions_apply.add_argument("--rationale", required=True)
    governance_actions_apply.add_argument("--confirm", action="store_true")
    governance_actions_recover = governance_actions_subparsers.add_parser("recover", help="Recover a previously applied governance action")
    governance_actions_recover.add_argument("action_id")
    governance_actions_recover.add_argument("--rationale", required=True)
    governance_actions_recover.add_argument("--confirm", action="store_true")
    governance_subparsers.add_parser("digest", help="Show the latest governance digest")
    governance_subparsers.add_parser("policy", help="Show effective governance policy")

    remember_parser = subparsers.add_parser("remember", help="Store a manual memory")
    remember_parser.add_argument("title")
    remember_parser.add_argument("body")
    remember_parser.add_argument("--cwd", default=None)
    remember_parser.add_argument("--root-name")

    episodes_parser = subparsers.add_parser("episodes", help="Manage stored episode metadata")
    episodes_subparsers = episodes_parser.add_subparsers(dest="episodes_command", required=True)
    episodes_scope = episodes_subparsers.add_parser("scope-backfill", help="Backfill workspace metadata for explicit episode IDs")
    episodes_scope.add_argument("--cwd", required=True)
    episodes_scope.add_argument("--root-name")
    episodes_scope.add_argument("--id", action="append", required=True, dest="episode_ids")
    episodes_scope.add_argument("--dry-run", action="store_true")

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
    capture_review_list.add_argument(
        "--status",
        choices=["pending_review", "approved", "rejected", "completed", "failed", "blocked_missing_dependency", "all"],
        default="pending_review",
    )
    capture_review_list.add_argument("--limit", type=int, default=50)
    capture_review_decide = capture_review_subparsers.add_parser("decide", help="Approve or reject a capture review job")
    capture_review_decide.add_argument("job_id")
    capture_review_decide.add_argument("--decision", required=True, choices=["approve", "reject"])
    capture_review_decide.add_argument("--rationale", required=True)
    capture_review_ingest = capture_review_subparsers.add_parser("ingest", help="Ingest approved capture review jobs")
    capture_review_ingest.add_argument("--job-id")
    capture_review_ingest.add_argument("--limit", type=int, default=25)
    capture_review_ingest.add_argument("--dry-run", action="store_true")

    retention_parser = subparsers.add_parser("retention", help="Inspect and tune retention quality")
    retention_subparsers = retention_parser.add_subparsers(dest="retention_command", required=True)
    retention_policy = retention_subparsers.add_parser("policy", help="Manage retention policies")
    retention_policy_subparsers = retention_policy.add_subparsers(dest="retention_policy_command", required=True)
    retention_policy_subparsers.add_parser("list", help="List retention policies")
    retention_policy_set = retention_policy_subparsers.add_parser("set", help="Update a retention policy")
    retention_policy_set.add_argument("memory_class", choices=["claim", "episode", "corpus"])
    retention_policy_set.add_argument("--half-life-days", type=int, required=True)
    retention_policy_set.add_argument("--min-confidence", type=float, required=True)
    retention_policy_set.add_argument("--action", required=True, choices=["review", "deprioritize", "retire"])
    retention_policy_set.add_argument("--reason", required=True)
    retention_quality = retention_subparsers.add_parser("quality", help="Show memory quality report")
    retention_quality.add_argument("--limit", type=int, default=25)

    semantic_parser = subparsers.add_parser("semantic-duplicates", help="Refresh or inspect semantic duplicate clusters")
    semantic_subparsers = semantic_parser.add_subparsers(dest="semantic_duplicates_command", required=True)
    semantic_refresh = semantic_subparsers.add_parser("refresh", help="Refresh advisory semantic duplicate clusters")
    semantic_refresh.add_argument("--memory-class", choices=["all", "corpus", "episode", "claim"], default="all")
    semantic_refresh.add_argument("--root-name")
    semantic_refresh.add_argument("--threshold", type=float)
    semantic_refresh.add_argument("--limit", type=int, default=1000)
    semantic_list = semantic_subparsers.add_parser("list", help="List active semantic duplicate clusters")
    semantic_list.add_argument("--memory-class", choices=["corpus", "episode", "claim"])
    semantic_list.add_argument("--root-name")
    semantic_list.add_argument("--limit", type=int, default=50)

    code_parser = subparsers.add_parser("code", help="Inspect code-aware retrieval diagnostics")
    code_subparsers = code_parser.add_subparsers(dest="code_command", required=True)
    code_status = code_subparsers.add_parser("status", help="Show code index coverage and parser status")
    code_status.add_argument("--root", dest="root_name")
    code_status.add_argument("--cwd", help="Workspace directory used to resolve the monitored root when --root is omitted")
    code_search = code_subparsers.add_parser("search", help="Search code symbols or indexed code text")
    code_search.add_argument("query")
    code_search.add_argument("--root", dest="root_name")
    code_search.add_argument("--cwd", help="Workspace directory used to resolve the monitored root when --root is omitted")
    code_search.add_argument(
        "--mode",
        choices=["literal-symbol", "literal_symbol", "full-text", "full_text"],
        default="literal_symbol",
        help="Search literal symbol/path metadata or indexed code chunk text",
    )
    code_search.add_argument("--language")
    code_search.add_argument("--symbol-kind")
    code_search.add_argument("--relationship")
    code_search.add_argument("--path-glob")
    code_search.add_argument("--include-generated", action="store_true")
    code_search.add_argument("--limit", type=int, default=20)
    code_symbol = code_subparsers.add_parser("symbol", help="Look up a code symbol and references")
    code_symbol.add_argument("symbol")
    code_symbol.add_argument("--root", dest="root_name")
    code_symbol.add_argument("--language")
    code_symbol.add_argument("--no-references", action="store_false", dest="include_references")
    code_symbol.add_argument("--limit", type=int, default=20)
    code_symbol.set_defaults(include_references=True)
    code_feedback = code_subparsers.add_parser("feedback", help="Record or summarize code retrieval feedback")
    code_feedback_subparsers = code_feedback.add_subparsers(dest="code_feedback_command", required=True)
    code_feedback_add = code_feedback_subparsers.add_parser("add", help="Record privacy-safe code retrieval miss feedback")
    code_feedback_add.add_argument("--query", required=True)
    code_feedback_add.add_argument("--root", dest="root_name")
    code_feedback_add.add_argument("--result-count", type=int, default=0)
    code_feedback_add.add_argument("--surface", default="cli")
    code_feedback_add.add_argument("--miss-category", default="other")
    code_feedback_add.add_argument("--expected-symbol")
    code_feedback_add.add_argument("--path")
    code_feedback_summary = code_feedback_subparsers.add_parser("summary", help="Summarize code retrieval feedback")
    code_feedback_summary.add_argument("--root", dest="root_name")
    code_feedback_summary.add_argument("--limit", type=int, default=20)

    diagnostics_parser = subparsers.add_parser("diagnostics", help="Inspect operational diagnostic read models")
    diagnostics_subparsers = diagnostics_parser.add_subparsers(dest="diagnostics_command", required=True)
    for diagnostics_name in ("all", "retrieval", "watcher", "workers", "jobs", "mail"):
        diagnostics_command = diagnostics_subparsers.add_parser(diagnostics_name, help=f"Show {diagnostics_name} diagnostics")
        diagnostics_command.add_argument("--limit", type=int, default=25)
        diagnostics_command.add_argument("--root", dest="root_name")
        diagnostics_command.add_argument("--status")
        diagnostics_command.add_argument("--family")
        diagnostics_command.add_argument("--since-hours", type=int)
        diagnostics_command.add_argument("--include-details", action="store_true")
    diagnostics_remediate = diagnostics_subparsers.add_parser("remediate", help="Run a confirmation-gated diagnostic remediation action")
    diagnostics_remediate.add_argument("action", choices=["retry_corpus_job", "run_backfill", "repair_asset_statuses", "clear_completed_errors"])
    diagnostics_remediate.add_argument("--target-type", required=True)
    diagnostics_remediate.add_argument("--target-id")
    diagnostics_remediate.add_argument("--root", dest="root_name")
    diagnostics_remediate.add_argument("--family", choices=JOB_FAMILIES)
    diagnostics_remediate.add_argument("--reason", default="operator diagnostic remediation")

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
    crawl_add.add_argument(
        "--strict-indexing",
        action="store_true",
        help="Block metadata-only assets for this root instead of treating them as indexed knowledge",
    )
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
    crawl_edit_strict = crawl_edit.add_mutually_exclusive_group()
    crawl_edit_strict.add_argument(
        "--strict-indexing",
        action="store_true",
        default=None,
        help="Block metadata-only assets for this root instead of treating them as indexed knowledge",
    )
    crawl_edit_strict.add_argument(
        "--allow-metadata-only",
        action="store_false",
        dest="strict_indexing",
        help="Allow metadata-only asset rows for this pilot root",
    )

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

    crawl_requeue_metadata = crawl_subparsers.add_parser("requeue-metadata-only", help="Requeue active metadata-only source assets")
    crawl_requeue_metadata.add_argument("--root", dest="root_name")
    crawl_requeue_metadata.add_argument("--limit", type=int, default=1000)

    crawl_requeue_svg = crawl_subparsers.add_parser("requeue-svg", help="Requeue active SVG source assets for renderer-backed extraction")
    crawl_requeue_svg.add_argument("--root", dest="root_name")
    crawl_requeue_svg.add_argument("--limit", type=int, default=1000)

    crawl_backfill = crawl_subparsers.add_parser("backfill", help="Claim deferred corpus extraction jobs")
    crawl_kind_choices = ["text", "images", "diagrams", "archives", "containers", "media", "search-index", "data", "mail", "reports", "metadata", "all"]
    crawl_backfill.add_argument(
        "--kind",
        choices=crawl_kind_choices,
        default="all",
    )
    crawl_backfill.add_argument("--limit", type=int)
    crawl_backfill.add_argument("--workers", type=int)
    crawl_backfill.add_argument("--root", dest="root_name")
    crawl_backfill.add_argument("--family", choices=JOB_FAMILIES)
    crawl_backfill.add_argument("--callback-url")

    crawl_worker = crawl_subparsers.add_parser("worker", help="Run the corpus extraction worker")
    crawl_worker_subparsers = crawl_worker.add_subparsers(dest="worker_command", required=True)
    crawl_worker_run = crawl_worker_subparsers.add_parser("run", help="Run the corpus worker loop")
    crawl_worker_run.add_argument(
        "--kind",
        choices=crawl_kind_choices,
        default="all",
    )
    crawl_worker_run.add_argument("--limit", type=int)
    crawl_worker_run.add_argument("--workers", type=int)
    crawl_worker_run.add_argument("--interval", type=float, default=5.0)
    crawl_worker_run.add_argument("--once", action="store_true")
    crawl_worker_run.add_argument("--host-agent-roots", action="store_true", help="Process only host-agent owned roots")
    crawl_worker_run.add_argument(
        "--exclude-host-agent-roots",
        action="store_true",
        help="Skip host-agent owned roots so Docker workers do not open host paths",
    )
    crawl_worker_status = crawl_worker_subparsers.add_parser("status", help="Show worker family status")
    crawl_worker_status.add_argument("--family", default="all")

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
    watch_probe = watch_subparsers.add_parser("probe", help="Probe watcher backend behavior in a temporary directory")
    watch_probe.add_argument("--timeout", type=float, default=2.0)
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
    codex_readiness_parser = codex_subparsers.add_parser("mcp-readiness", help="Probe configured Flux MCP stdio readiness")
    codex_readiness_parser.add_argument("--json", action="store_true", dest="json_output")

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

    event_parser = subparsers.add_parser("event", help="Run RabbitMQ event-driven workers")
    event_subparsers = event_parser.add_subparsers(dest="event_command", required=True)
    event_worker = event_subparsers.add_parser("worker", help="Consume RabbitMQ work queues")
    event_worker_subparsers = event_worker.add_subparsers(dest="event_worker_command", required=True)
    event_worker_run = event_worker_subparsers.add_parser("run", help="Run a RabbitMQ command consumer")
    event_worker_run.add_argument("--queue", default="flux.commands.corpus")
    event_worker_run.add_argument("--worker-id")
    event_outbox = event_subparsers.add_parser("outbox", help="Publish PostgreSQL outbox rows to RabbitMQ")
    event_outbox_subparsers = event_outbox.add_subparsers(dest="event_outbox_command", required=True)
    event_outbox_relay = event_outbox_subparsers.add_parser("relay", help="Run the transactional outbox relay")
    event_outbox_relay.add_argument("--once", action="store_true")
    event_outbox_relay.add_argument("--interval", type=float, default=1.0)
    event_outbox_relay.add_argument("--limit", type=int, default=100)
    event_callbacks = event_subparsers.add_parser("callbacks", help="Dispatch signed webhook callbacks")
    event_callbacks_subparsers = event_callbacks.add_subparsers(dest="event_callbacks_command", required=True)
    event_callbacks_dispatch = event_callbacks_subparsers.add_parser("dispatch", help="Run the callback dispatcher")
    event_callbacks_dispatch.add_argument("--queue", default="flux.callbacks.dispatch")
    event_subscriber = event_subparsers.add_parser("subscriber", help="Consume durable RabbitMQ event subscriber queues")
    event_subscriber_subparsers = event_subscriber.add_subparsers(dest="event_subscriber_command", required=True)
    event_subscriber_run = event_subscriber_subparsers.add_parser("run", help="Run a RabbitMQ event subscriber")
    event_subscriber_run.add_argument("--queue", default="flux.events.audit")
    event_subscriber_run.add_argument("--subscriber", default="audit")
    event_scheduler = event_subparsers.add_parser("scheduler", help="Enqueue due scheduled work into RabbitMQ")
    event_scheduler_subparsers = event_scheduler.add_subparsers(dest="event_scheduler_command", required=True)
    event_scheduler_run = event_scheduler_subparsers.add_parser("run", help="Run the event scheduler")
    event_scheduler_run.add_argument("--once", action="store_true")
    event_scheduler_run.add_argument("--interval", type=float, default=30.0)
    event_scheduler_run.add_argument("--limit", type=int, default=25)
    event_repair_storm = event_subparsers.add_parser(
        "repair-capture-command-storm",
        help="Repair duplicate capture command rows from a broker claim storm",
    )
    event_repair_storm.add_argument("--apply", action="store_true")
    event_repair_storm.add_argument("--confirm")
    event_repair_storm.add_argument("--purge-rabbitmq", action="store_true")

    acceleration_parser = subparsers.add_parser("acceleration", help="Inspect V2.8 acceleration capability and queue status")
    acceleration_subparsers = acceleration_parser.add_subparsers(dest="acceleration_command", required=True)
    acceleration_subparsers.add_parser("status", help="Show local capability, cache, and worker-family status")
    acceleration_evidence = acceleration_subparsers.add_parser("evidence", help="Show combined operator evidence gates")
    acceleration_evidence.add_argument("--label")
    acceleration_evidence.add_argument("--deployment-label")
    acceleration_evidence.add_argument("--compare-label")
    acceleration_evidence.add_argument("--freshness-hours", type=int, default=336)
    acceleration_evidence.add_argument("--limit", type=int, default=100)
    acceleration_benchmark = acceleration_subparsers.add_parser("benchmark", help="Run or inspect synthetic benchmark history")
    benchmark_subparsers = acceleration_benchmark.add_subparsers(dest="benchmark_command", required=True)
    benchmark_run = benchmark_subparsers.add_parser("run", help="Run synthetic indexing benchmarks")
    benchmark_run.add_argument("--fixture", default="all")
    benchmark_run.add_argument("--files", type=int, default=10)
    benchmark_run.add_argument("--mode", choices=["scan", "soak", "watcher", "model", "all"], default="scan")
    benchmark_run.add_argument("--passes", type=int, default=1)
    benchmark_run.add_argument("--label")
    benchmark_run.add_argument("--compare-label")
    benchmark_run.add_argument("--workers", type=int, default=1)
    benchmark_run.add_argument("--family", default="all")
    benchmark_run.add_argument("--scope", choices=["synthetic", "root", "path"], default="synthetic")
    benchmark_run.add_argument("--root", dest="root_name")
    benchmark_run.add_argument("--path")
    benchmark_run.add_argument("--max-files", type=int)
    benchmark_run.add_argument("--deployment-label")
    benchmark_run.add_argument("--scenario", choices=["standard", "reliability", "host_cloud", "cache_readiness", "tuning"], default="standard")
    benchmark_run.add_argument("--include-model-probe", action="store_true")
    benchmark_history = benchmark_subparsers.add_parser("history", help="List benchmark run history")
    benchmark_history.add_argument("--fixture", required=True)
    benchmark_history.add_argument("--mode")
    benchmark_history.add_argument("--label")
    benchmark_history.add_argument("--warm-state")
    benchmark_history.add_argument("--scope-type")
    benchmark_history.add_argument("--scope-hash")
    benchmark_history.add_argument("--deployment-label")
    benchmark_history.add_argument("--scenario")
    benchmark_history.add_argument("--freshness-hours", type=int)
    benchmark_history.add_argument("--limit", type=int, default=20)
    reliability_parser = acceleration_subparsers.add_parser("reliability", help="Run or inspect indexer reliability evidence gate")
    reliability_subparsers = reliability_parser.add_subparsers(dest="reliability_command", required=True)
    reliability_status = reliability_subparsers.add_parser("status", help="Show metadata-only indexer reliability readiness")
    reliability_status.add_argument("--root", dest="root_name")
    reliability_status.add_argument("--path")
    reliability_status.add_argument("--label")
    reliability_status.add_argument("--deployment-label")
    reliability_status.add_argument("--compare-label")
    reliability_status.add_argument("--freshness-hours", type=int, default=336)
    reliability_status.add_argument("--limit", type=int, default=100)
    reliability_run = reliability_subparsers.add_parser("run", help="Run the reliability validation benchmark suite")
    reliability_run.add_argument("--scope", choices=["synthetic", "root", "path", "all-roots", "all_roots"], default="synthetic")
    reliability_run.add_argument("--root", dest="root_name")
    reliability_run.add_argument("--path")
    reliability_run.add_argument("--label")
    reliability_run.add_argument("--deployment-label")
    reliability_run.add_argument("--compare-label")
    reliability_run.add_argument("--max-files", type=int, default=1000)
    reliability_run.add_argument("--passes", type=int, default=2)
    reliability_run.add_argument("--include-cache-readiness", action="store_true")
    reliability_run.add_argument("--skip-tuning", action="store_false", dest="include_tuning")
    reliability_run.add_argument("--full", action="store_const", const="full", default="standard", dest="evidence_level")
    reliability_run.set_defaults(include_tuning=True)
    reliability_root = reliability_subparsers.add_parser("root-status", help="Show monitored-root reliability card")
    reliability_root.add_argument("--root", dest="root_name", required=True)
    reliability_roots = reliability_subparsers.add_parser("roots", help="Show all monitored-root reliability cards")
    reliability_roots.add_argument("--include-disabled", action="store_true")
    reliability_roots.add_argument("--freshness-hours", type=int, default=336)
    reliability_roots.add_argument("--limit", type=int, default=100)

    gpu_parser = subparsers.add_parser("gpu", help="Inspect local GPU scheduler state")
    gpu_subparsers = gpu_parser.add_subparsers(dest="gpu_command", required=True)
    gpu_subparsers.add_parser("status", help="Show running, waiting, and recent GPU leases")

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
    mail_add_imap.add_argument("--processed-folder")
    mail_add_imap.add_argument("--trash-folder")
    mail_add_imap.add_argument("--confirm-destructive-post-process", action="store_true")
    _add_mail_schedule_args(mail_add_imap)
    mail_add_outlook = mail_profile_subparsers.add_parser("add-outlook", help="Add an Outlook COM catch-up profile")
    mail_add_outlook.add_argument("--name", required=True)
    mail_add_outlook.add_argument("--folder", action="append", required=True)
    mail_add_outlook.add_argument("--spool", required=True)
    mail_add_outlook.add_argument("--post-process", default="none")
    mail_add_outlook.add_argument("--processed-folder")
    mail_add_outlook.add_argument("--trash-folder")
    mail_add_outlook.add_argument("--confirm-destructive-post-process", action="store_true")
    mail_add_outlook.add_argument(
        "--include-subfolders",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="Include child folders under each selected Outlook COM folder",
    )
    mail_add_outlook.add_argument(
        "--incremental-basis",
        choices=["received-time", "last-modification-time"],
        default="received-time",
        help="Outlook COM timestamp used for incremental sync filtering",
    )
    _add_mail_schedule_args(mail_add_outlook)
    mail_profile_subparsers.add_parser("list", help="List mail profiles")
    mail_subparsers.add_parser("status", help="Show mail ingestion status")
    mail_sync = mail_subparsers.add_parser("sync", help="Sync exported mail spool into the corpus")
    mail_sync.add_argument("--profile")
    mail_spool_dedupe = mail_subparsers.add_parser("spool-dedupe", help="Report or purge safe duplicate Outlook spool exports")
    mail_spool_dedupe_source = mail_spool_dedupe.add_mutually_exclusive_group(required=True)
    mail_spool_dedupe_source.add_argument("--profile")
    mail_spool_dedupe_source.add_argument("--spool")
    mail_spool_dedupe.add_argument("--apply", action="store_true", help="Apply the dedupe action")
    mail_spool_dedupe.add_argument("--purge", action="store_true", help="Permanently delete safe duplicate export folders")
    mail_spool_dedupe.add_argument("--json", action="store_true", dest="json_output", help="Emit JSON output")
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
    mail_post_process = mail_subparsers.add_parser("post-process", help="Preview and inspect post-process actions")
    mail_post_process_subparsers = mail_post_process.add_subparsers(dest="mail_post_process_command", required=True)
    mail_post_process_dry_run = mail_post_process_subparsers.add_parser("dry-run", help="Preview selected profile post-process actions")
    mail_post_process_dry_run.add_argument("--profile", required=True)
    mail_post_process_dry_run.add_argument("--limit", type=int, default=5)
    mail_post_process_events = mail_post_process_subparsers.add_parser("events", help="List recent post-process events")
    mail_post_process_events.add_argument("--profile")
    mail_post_process_events.add_argument("--limit", type=int, default=20)
    mail_render = mail_subparsers.add_parser("render-outlook-config", help="Render an Outlook COM catch-up config")
    mail_render.add_argument("--profile", required=True)
    mail_render.add_argument("--spool", required=True)
    mail_render.add_argument("--folder", action="append", required=True)

    outlook_host_parser = subparsers.add_parser("outlook-host", help="Run the Windows Outlook COM bridge")
    outlook_host_subparsers = outlook_host_parser.add_subparsers(dest="outlook_host_command", required=True)
    outlook_host_run = outlook_host_subparsers.add_parser("run", help="Run the Outlook COM host loop")
    outlook_host_run.add_argument("--host-id", default="default")
    outlook_host_run.add_argument("--interval-seconds", type=int, default=15)
    outlook_host_run.add_argument(
        "--legacy-db-loop",
        action="store_true",
        help="Run the old DB-claim loop; development only and requires FLUX_KB_ALLOW_INLINE_WORKERS=1",
    )
    outlook_host_subparsers.add_parser("status", help="Show Outlook COM host status")
    outlook_host_sync = outlook_host_subparsers.add_parser("sync", help="Request an Outlook COM profile sync")
    outlook_host_sync.add_argument("--profile", required=True)

    host_agent_parser = subparsers.add_parser("host-agent", help="Run the local filesystem host agent")
    host_agent_subparsers = host_agent_parser.add_subparsers(dest="host_agent_command", required=True)
    host_agent_run = host_agent_subparsers.add_parser("run", help="Run the local host-agent REST bridge")
    host_agent_run.add_argument("--host", default="127.0.0.1")
    host_agent_run.add_argument("--port", type=int, default=8799)
    host_agent_run.add_argument(
        "--no-broker-worker",
        action="store_true",
        help="Disable the host-side RabbitMQ consumer for host-agent corpus jobs",
    )
    host_agent_subparsers.add_parser("status", help="Show local host-agent status")
    host_agent_subparsers.add_parser("browse", help="Open a native folder picker")

    args = parser.parse_args(argv)
    handlers = {
        "doctor": _doctor,
        "init": _init,
        "migrate": _migrate,
        "status": _status,
        "search": _search,
        "explain": _explain,
        "retrieval": _retrieval,
        "search-index": _search_index,
        "maintenance": _maintenance,
        "automation": _automation,
        "governance": _governance,
        "remember": _remember,
        "episodes": _episodes,
        "claim": _claim,
        "graph": _graph,
        "capture": _capture,
        "retention": _retention,
        "semantic-duplicates": _semantic_duplicates,
        "forget": _forget,
        "audit": _audit,
        "backfill-codex": _backfill_codex,
        "crawl": _crawl,
        "export-wiki": _export_wiki,
        "lint": _lint,
        "hook": _hook,
        "codex": _codex,
        "settings": _settings,
        "event": _event_command,
        "acceleration": _acceleration,
        "gpu": _gpu,
        "code": _code,
        "diagnostics": _diagnostics,
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
    print("Copy .env.example to .env, start PostgreSQL, then run `flux-kb migrate`.")
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

    filters = _retrieval_filters_from_args(args)
    root_name = getattr(args, "root_name", None)
    kwargs = {"limit": args.limit}
    if root_name:
        kwargs["root_name"] = root_name
    if filters is not None:
        kwargs["filters"] = filters
    payload = KnowledgeService().search(args.query, **kwargs)
    print(json.dumps(payload, indent=2))
    return 0


def _explain(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    filters = _retrieval_filters_from_args(args)
    kwargs = {
        "limit": args.limit,
        "token_budget": args.token_budget,
        "cwd": args.cwd,
        "root_name": args.root_name,
        "scope_mode": args.scope_mode,
    }
    if filters is not None:
        kwargs["filters"] = filters
    print(
        json.dumps(
            KnowledgeService().explain(args.query, **kwargs),
            indent=2,
        )
    )
    return 0


def _add_retrieval_filter_args(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--kind", action="append", choices=["episode", "file", "mail"], dest="logical_kinds")
    parser.add_argument("--current-only", action="store_true")
    parser.add_argument("--lifecycle-state", action="append", dest="lifecycle_states")
    parser.add_argument("--include-suppressed", action="store_true")
    parser.add_argument("--file-kind", action="append", dest="file_kinds")
    parser.add_argument("--language", action="append", dest="languages")
    parser.add_argument("--symbol-kind", action="append", dest="symbol_kinds")
    parser.add_argument("--relationship", action="append", dest="relationships")
    parser.add_argument("--path-glob", action="append", dest="path_globs")
    parser.add_argument("--include-generated", action="store_true")


def _retrieval_filters_from_args(args: argparse.Namespace) -> dict | None:
    filters = {
        "logical_kinds": getattr(args, "logical_kinds", None) or [],
        "current_only": bool(getattr(args, "current_only", False)),
        "lifecycle_states": getattr(args, "lifecycle_states", None) or [],
        "include_suppressed": bool(getattr(args, "include_suppressed", False)),
        "file_kinds": getattr(args, "file_kinds", None) or [],
        "languages": getattr(args, "languages", None) or [],
        "symbol_kinds": getattr(args, "symbol_kinds", None) or [],
        "relationships": getattr(args, "relationships", None) or [],
        "path_globs": getattr(args, "path_globs", None) or [],
        "include_generated": bool(getattr(args, "include_generated", False)),
    }
    if not any(filters.values()):
        return None
    from .service import normalize_retrieval_filters

    return normalize_retrieval_filters(filters)


def _remember(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    cwd = args.cwd if args.cwd is not None else str(Path.cwd())
    result = KnowledgeService().remember(args.title, args.body, cwd=cwd, root_name=args.root_name)
    print(json.dumps({"id": result.id, "redaction_count": result.redaction_count}, indent=2))
    return 0


def _episodes(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    if args.episodes_command == "scope-backfill":
        payload = KnowledgeService().backfill_episode_workspace_scope(
            episode_ids=args.episode_ids,
            cwd=args.cwd,
            root_name=args.root_name,
            dry_run=args.dry_run,
        )
        print(json.dumps(payload, indent=2))
        return 0
    raise ValueError(f"unknown episodes command: {args.episodes_command}")


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
        payload = service.list_capture_review_jobs(status=args.status, limit=args.limit)
    elif args.capture_review_command == "decide":
        payload = service.review_capture_job(
            job_id=args.job_id,
            decision=args.decision,
            rationale=args.rationale,
            actor="cli",
        )
    elif args.capture_review_command == "ingest":
        payload = service.ingest_capture_review_jobs(
            job_id=args.job_id,
            limit=args.limit,
            dry_run=args.dry_run,
            actor="cli",
        )
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.capture_review_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _retention(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    service = KnowledgeService()
    if args.retention_command == "policy":
        if args.retention_policy_command == "list":
            payload = service.list_retention_policies()
        elif args.retention_policy_command == "set":
            payload = service.set_retention_policy(
                memory_class=args.memory_class,
                half_life_days=args.half_life_days,
                min_confidence=args.min_confidence,
                action=args.action,
                actor="cli",
                reason=args.reason,
            )
        else:  # pragma: no cover - argparse prevents this
            raise ValueError(args.retention_policy_command)
    elif args.retention_command == "quality":
        payload = service.retention_quality_report(limit=args.limit)
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.retention_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _semantic_duplicates(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    service = KnowledgeService()
    if args.semantic_duplicates_command == "refresh":
        payload = service.refresh_semantic_duplicate_clusters(
            memory_class=args.memory_class,
            root_name=args.root_name,
            threshold=args.threshold,
            limit=args.limit,
        )
    elif args.semantic_duplicates_command == "list":
        payload = service.list_semantic_duplicate_clusters(
            memory_class=args.memory_class,
            root_name=args.root_name,
            limit=args.limit,
        )
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.semantic_duplicates_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _code(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    service = KnowledgeService()
    if args.code_command == "status":
        payload = service.code_status(root_name=args.root_name, cwd=args.cwd)
    elif args.code_command == "search":
        payload = service.code_search(
            query=args.query,
            root_name=args.root_name,
            cwd=args.cwd,
            mode=args.mode.replace("-", "_"),
            language=args.language,
            symbol_kind=args.symbol_kind,
            relationship=args.relationship,
            path_glob=args.path_glob,
            include_generated=args.include_generated,
            limit=args.limit,
        )
    elif args.code_command == "symbol":
        payload = service.code_symbol_lookup(
            symbol=args.symbol,
            root_name=args.root_name,
            language=args.language,
            include_references=args.include_references,
            limit=args.limit,
        )
    elif args.code_command == "feedback":
        if args.code_feedback_command == "add":
            payload = service.record_code_feedback(
                query=args.query,
                root_name=args.root_name,
                result_count=args.result_count,
                surface=args.surface,
                miss_category=args.miss_category,
                expected_symbol=args.expected_symbol,
                path=args.path,
                metadata={},
            )
        elif args.code_feedback_command == "summary":
            payload = service.code_feedback_summary(root_name=args.root_name, limit=args.limit)
        else:  # pragma: no cover - argparse prevents this
            raise ValueError(args.code_feedback_command)
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.code_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _diagnostics(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    service = KnowledgeService()
    if args.diagnostics_command == "remediate":
        payload = service.remediate_diagnostic(
            action=args.action,
            target_type=args.target_type,
            target_id=args.target_id,
            root_name=args.root_name,
            family=args.family,
            reason=args.reason,
            actor="cli",
        )
    else:
        payload = service.operational_diagnostics(
            section=args.diagnostics_command,
            limit=args.limit,
            root_name=args.root_name,
            status=args.status,
            family=args.family,
            since_hours=args.since_hours,
            include_details=args.include_details,
        )
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _automation(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    service = KnowledgeService()
    if args.automation_command == "status":
        payload = service.operator_automation_status()
    elif args.automation_command == "run":
        payload = service.enqueue_operator_automation(
            mode=args.mode,
            trigger="manual",
            actor="cli",
            limit=args.limit,
            dry_run=args.dry_run,
        )
    elif args.automation_command == "actions":
        payload = service.operator_automation_actions(status=args.status, run_id=args.run_id, action=args.action, limit=args.limit)
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.automation_command)
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
            metadata={"source": "cli", "strict_indexing": True} if args.strict_indexing else {"source": "cli"},
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
    elif args.crawl_command == "requeue-metadata-only":
        payload = database.requeue_metadata_only_source_assets(root_name=args.root_name, limit=args.limit)
    elif args.crawl_command == "requeue-svg":
        payload = database.requeue_svg_source_assets(root_name=args.root_name, limit=args.limit)
    elif args.crawl_command == "backfill":
        from .service import KnowledgeService

        kwargs = {"kind": args.family or args.kind, "limit": args.limit, "workers": args.workers}
        if args.root_name:
            kwargs["root_name"] = args.root_name
        if args.callback_url:
            kwargs["callback_url"] = args.callback_url
        service = KnowledgeService()
        enqueue = getattr(service, "enqueue_corpus_backfill", None)
        if enqueue is None:
            raise RuntimeError("crawl backfill requires enqueue_corpus_backfill; direct inline backfill is not a production path")
        payload = enqueue(**kwargs)
    elif args.crawl_command == "worker":
        from .service import KnowledgeService

        if args.worker_command == "status":
            payload = KnowledgeService().worker_status(family=args.family)
        elif args.worker_command != "run":  # pragma: no cover - argparse prevents this
            raise ValueError(args.worker_command)
        else:
            if os.environ.get("FLUX_KB_ALLOW_INLINE_WORKERS") != "1":
                raise SystemExit(
                    "crawl worker run is a development-only inline runner. "
                    "Use `flux-kb event worker run --queue flux.commands.corpus`, "
                    "or set FLUX_KB_ALLOW_INLINE_WORKERS=1 for local debugging."
                )
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


def _event_command(args: argparse.Namespace) -> int:
    if args.event_command == "worker":
        if args.event_worker_command != "run":  # pragma: no cover - argparse prevents this
            raise ValueError(args.event_worker_command)
        from .event_worker import run_worker

        payload = run_worker(queue_name=args.queue, worker_id=args.worker_id)
    elif args.event_command == "outbox":
        if args.event_outbox_command != "relay":  # pragma: no cover - argparse prevents this
            raise ValueError(args.event_outbox_command)
        import asyncio

        from .outbox_relay import run_relay_loop

        payload = asyncio.run(run_relay_loop(once=args.once, interval_seconds=args.interval, limit=args.limit))
    elif args.event_command == "callbacks":
        if args.event_callbacks_command != "dispatch":  # pragma: no cover - argparse prevents this
            raise ValueError(args.event_callbacks_command)
        from .callback_dispatcher import run_dispatcher

        payload = run_dispatcher(queue_name=args.queue)
    elif args.event_command == "subscriber":
        if args.event_subscriber_command != "run":  # pragma: no cover - argparse prevents this
            raise ValueError(args.event_subscriber_command)
        from .event_subscriber import run_subscriber

        payload = run_subscriber(queue_name=args.queue, subscriber_name=args.subscriber)
    elif args.event_command == "scheduler":
        if args.event_scheduler_command != "run":  # pragma: no cover - argparse prevents this
            raise ValueError(args.event_scheduler_command)
        from .event_scheduler import run_scheduler

        payload = run_scheduler(once=args.once, interval_seconds=args.interval, limit=args.limit)
    elif args.event_command == "repair-capture-command-storm":
        from .event_repair import repair_capture_command_storm

        payload = repair_capture_command_storm(
            apply=args.apply,
            confirm=args.confirm,
            purge_rabbitmq=args.purge_rabbitmq,
        )
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.event_command)
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
    metadata = dict(existing.get("metadata") or {})
    if getattr(args, "strict_indexing", None) is not None:
        metadata["strict_indexing"] = bool(args.strict_indexing)
    metadata["source"] = "cli"
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
        metadata=metadata,
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
    if args.watch_command == "probe":
        from .service import KnowledgeService

        return KnowledgeService().watch_probe(timeout_seconds=args.timeout)
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
        payload = service.enqueue_apply(component=args.component, actor="cli")
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.settings_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _retrieval(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    if args.retrieval_command == "benchmark":
        if args.retrieval_benchmark_command == "run":
            payload = KnowledgeService().run_retrieval_benchmark(
                suite=args.suite,
                label=args.label,
                compare_label=args.compare_label,
                limit_per_query=args.limit_per_query,
                token_budget=args.token_budget,
                persist=args.persist,
            )
        elif args.retrieval_benchmark_command == "history":
            payload = KnowledgeService().retrieval_benchmark_history(
                suite=args.suite,
                label=args.label,
                limit=args.limit,
            )
        else:  # pragma: no cover - argparse prevents this
            raise ValueError(args.retrieval_benchmark_command)
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.retrieval_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _search_index(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    service = KnowledgeService()
    if args.search_index_command == "status":
        payload = service.search_index_status(root_name=args.root_name)
    elif args.search_index_command == "sync":
        payload = service.search_index_sync(owner_class=args.owner_class, root_name=args.root_name, limit=args.limit)
    elif args.search_index_command == "rebuild":
        payload = service.search_index_rebuild(root_name=args.root_name, confirmed=args.confirm)
    elif args.search_index_command == "purge-deleted-corpora":
        payload = service.purge_deleted_corpora(confirmed=args.confirm)
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.search_index_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _maintenance(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    if args.maintenance_command == "reprocess":
        payload = KnowledgeService().reprocess_derived_state(
            all_roots=args.all_roots,
            root_name=args.root_name,
            confirm=args.confirm,
            force=args.force,
            clear_caches=args.clear_caches,
            process=args.process,
            limit=args.limit,
            workers=args.workers,
            max_passes=args.max_passes,
        )
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.maintenance_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _governance(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    service = KnowledgeService()
    if args.governance_command == "run":
        payload = service.enqueue_governance_run(mode=args.mode, actor="cli", limit=args.limit)
    elif args.governance_command == "actions":
        if args.governance_actions_command == "list":
            payload = service.governance_actions(status=args.status, limit=args.limit)
        elif args.governance_actions_command == "apply":
            payload = service.governance_apply(
                args.action_id,
                rationale=args.rationale,
                confirm=args.confirm,
                actor="cli",
            )
        elif args.governance_actions_command == "recover":
            payload = service.governance_recover(
                args.action_id,
                rationale=args.rationale,
                confirm=args.confirm,
                actor="cli",
            )
        else:  # pragma: no cover - argparse prevents this
            raise ValueError(args.governance_actions_command)
    elif args.governance_command == "digest":
        payload = service.governance_digest()
    elif args.governance_command == "policy":
        payload = service.governance_policy()
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.governance_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _acceleration(args: argparse.Namespace) -> int:
    if args.acceleration_command == "status":
        from .acceleration import collect_acceleration_status

        payload = collect_acceleration_status()
    elif args.acceleration_command == "evidence":
        from .service import KnowledgeService

        payload = KnowledgeService().operator_evidence(
            label=args.label,
            deployment_label=args.deployment_label,
            compare_label=args.compare_label,
            freshness_hours=args.freshness_hours,
            limit=args.limit,
        )
    elif args.acceleration_command == "benchmark":
        from .service import KnowledgeService

        if args.benchmark_command == "run":
            payload = KnowledgeService().run_benchmark(
                fixture=args.fixture,
                files=args.files,
                mode=args.mode,
                passes=args.passes,
                label=args.label,
                compare_label=args.compare_label,
                workers=args.workers,
                family=args.family,
                scope=args.scope,
                root_name=args.root_name,
                path=args.path,
                max_files=args.max_files,
                deployment_label=args.deployment_label,
                scenario=args.scenario,
                include_model_probe=args.include_model_probe,
            )
        elif args.benchmark_command == "history":
            payload = KnowledgeService().benchmark_history(
                fixture=args.fixture,
                mode=args.mode,
                label=args.label,
                warm_state=args.warm_state,
                scope_type=args.scope_type,
                scope_hash=args.scope_hash,
                deployment_label=args.deployment_label,
                scenario=args.scenario,
                freshness_hours=args.freshness_hours,
                limit=args.limit,
            )
        else:  # pragma: no cover - argparse prevents this
            raise ValueError(args.benchmark_command)
    elif args.acceleration_command == "reliability":
        from .service import KnowledgeService

        if args.reliability_command == "status":
            payload = KnowledgeService().indexer_reliability_status(
                root_name=args.root_name,
                path=args.path,
                label=args.label,
                deployment_label=args.deployment_label,
                compare_label=args.compare_label,
                freshness_hours=args.freshness_hours,
                limit=args.limit,
            )
        elif args.reliability_command == "run":
            payload = KnowledgeService().run_indexer_reliability(
                scope=args.scope,
                root_name=args.root_name,
                path=args.path,
                label=args.label,
                deployment_label=args.deployment_label,
                compare_label=args.compare_label,
                max_files=args.max_files,
                passes=args.passes,
                include_cache_readiness=args.include_cache_readiness,
                include_tuning=args.include_tuning,
                evidence_level=args.evidence_level,
            )
        elif args.reliability_command == "root-status":
            payload = KnowledgeService().indexer_root_reliability(root_name=args.root_name)
        elif args.reliability_command == "roots":
            payload = KnowledgeService().indexer_reliability_roots(
                include_disabled=args.include_disabled,
                freshness_hours=args.freshness_hours,
                limit=args.limit,
            )
        else:  # pragma: no cover - argparse prevents this
            raise ValueError(args.reliability_command)
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.acceleration_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _gpu(args: argparse.Namespace) -> int:
    if args.gpu_command == "status":
        from .gpu_scheduler import get_gpu_scheduler

        payload = get_gpu_scheduler().status()
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.gpu_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _codex(args: argparse.Namespace) -> int:
    from .codex_integration import codex_mcp_readiness, codex_status, install_plugin

    if args.codex_command == "install-plugin":
        payload = install_plugin()
    elif args.codex_command == "status":
        payload = codex_status()
    elif args.codex_command == "mcp-readiness":
        payload = codex_mcp_readiness()
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.codex_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    if args.codex_command == "mcp-readiness" and not payload.get("ok"):
        return 1
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
                processed_folder=args.processed_folder,
                trash_folder=args.trash_folder,
                destructive_post_process_confirmed=args.confirm_destructive_post_process,
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
                processed_folder=args.processed_folder,
                trash_folder=args.trash_folder,
                destructive_post_process_confirmed=args.confirm_destructive_post_process,
                sync_enabled=args.sync_enabled,
                sync_interval_seconds=args.sync_interval_seconds,
                sync_window_days=args.sync_window_days,
                max_messages_per_run=args.max_messages_per_run,
                include_subfolders=args.include_subfolders,
                outlook_incremental_basis=args.incremental_basis.replace("-", "_"),
            )
        elif args.mail_profile_command == "list":
            payload = database.list_mail_profiles()
        else:  # pragma: no cover - argparse prevents this
            raise ValueError(args.mail_profile_command)
    elif args.mail_command == "status":
        payload = mail_ingestion.mail_status()
    elif args.mail_command == "sync":
        payload = database.enqueue_imap_sync_command(profile_name=args.profile, requested_by="cli")
    elif args.mail_command == "spool-dedupe":
        if args.profile:
            profiles = database.list_mail_profiles(name=args.profile)
            if not profiles:
                raise SystemExit(f"mail profile not found: {args.profile}")
            profile = profiles[0]
            payload = mail_ingestion.dedupe_outlook_spool(
                profile["spool_path"],
                profile_name=profile["name"],
                apply=args.apply,
                purge=args.purge,
            )
        else:
            payload = mail_ingestion.dedupe_outlook_spool(args.spool, apply=args.apply, purge=args.purge)
    elif args.mail_command == "post-process":
        if args.mail_post_process_command == "dry-run":
            payload = mail_ingestion.dry_run_mail_post_process(profile_name=args.profile, limit=args.limit)
        elif args.mail_post_process_command == "events":
            payload = {
                "events": database.list_mail_post_process_events(
                    profile_name=args.profile,
                    limit=args.limit,
                )
            }
        else:  # pragma: no cover - argparse prevents this
            raise ValueError(args.mail_post_process_command)
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
        if args.legacy_db_loop:
            if os.environ.get("FLUX_KB_ALLOW_INLINE_WORKERS") != "1":
                raise SystemExit(
                    "outlook-host run --legacy-db-loop is a development-only DB claim loop. "
                    "Set FLUX_KB_ALLOW_INLINE_WORKERS=1 to use it intentionally."
                )
            payload = outlook_host.run_forever(host_id=args.host_id, interval_seconds=args.interval_seconds)
        else:
            from . import event_worker, messaging

            payload = event_worker.run_worker(queue_name=messaging.COMMAND_OUTLOOK_QUEUE, worker_id=args.host_id)
    else:  # pragma: no cover - argparse prevents this
        raise ValueError(args.outlook_host_command)
    print(json.dumps(payload, indent=2, sort_keys=True))
    return 0


def _host_agent(args: argparse.Namespace) -> int:
    from . import host_agent

    if args.host_agent_command == "run":
        payload = host_agent.run_server(host=args.host, port=args.port, start_broker_consumer=not args.no_broker_worker)
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

    from .settings import SettingsService

    interval = int(SettingsService().resolve("mail.imap.poll_interval_seconds").raw_value)
    while True:
        database.enqueue_imap_sync_command(profile_name=profile_name, requested_by="mail-watch")
        time.sleep(interval)


def _export_wiki(args: argparse.Namespace) -> int:
    from .service import KnowledgeService

    print(json.dumps(KnowledgeService().export_wiki(args.output, limit=args.limit), indent=2))
    return 0


def _lint(_: argparse.Namespace) -> int:
    migrations = load_migrations()
    sql = "\n".join(migration.sql for migration in migrations)
    required = [
        "CREATE EXTENSION IF NOT EXISTS pg_trgm",
        "CREATE EXTENSION IF NOT EXISTS pgcrypto",
        "CREATE TABLE IF NOT EXISTS search_index_records",
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
