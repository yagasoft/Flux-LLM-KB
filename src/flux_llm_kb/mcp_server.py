from __future__ import annotations

from collections.abc import Callable
import errno
import time
from typing import Any
from urllib.error import URLError

from .error_diagnostics import error_envelope
from .service import KnowledgeService
from .model_activity import caller_surface


_READ_RETRY_ATTEMPTS = 3
_READ_RETRY_BACKOFF_SECONDS = (0.2, 0.8)
_TRANSIENT_ERRNOS = {
    errno.ECONNABORTED,
    errno.ECONNREFUSED,
    errno.ECONNRESET,
    errno.EPIPE,
    errno.ETIMEDOUT,
}
_TRANSIENT_MESSAGE_MARKERS = (
    "connection reset",
    "connection refused",
    "connection aborted",
    "broken pipe",
    "timed out",
    "timeout",
    "temporarily unavailable",
    "temporary unavailable",
    "cannot connect",
    "connection failure",
    "failed to establish",
    "server closed the connection",
    "terminating connection",
)


class _ServiceProxy:
    def __init__(self, runtime: "_McpToolRuntime") -> None:
        self._runtime = runtime

    def __getattr__(self, name: str) -> Any:
        return getattr(self._runtime.service, name)


class _McpToolRuntime:
    def __init__(
        self,
        service_factory: Callable[[], KnowledgeService],
        retry_sleep: Callable[[float], None],
    ) -> None:
        self._service_factory = service_factory
        self._retry_sleep = retry_sleep
        self._service: KnowledgeService | None = None

    @property
    def service(self) -> KnowledgeService:
        if self._service is None:
            self._service = self._service_factory()
        return self._service

    def refresh_service(self) -> None:
        self._service = self._service_factory()

    def drop_service(self) -> None:
        self._service = None

    def call(self, tool_name: str, operation: Callable[[], Any], *, readonly: bool) -> Any:
        attempts = _READ_RETRY_ATTEMPTS if readonly else 1
        for attempt in range(1, attempts + 1):
            try:
                return operation()
            except Exception as exc:
                if _is_transient_backend_failure(exc):
                    if attempt < attempts:
                        self.refresh_service()
                        self._retry_sleep(_READ_RETRY_BACKOFF_SECONDS[min(attempt - 1, len(_READ_RETRY_BACKOFF_SECONDS) - 1)])
                        continue
                    self.drop_service()
                    return _temporary_unavailable_payload(tool_name, exc)
                return _tool_error_payload(tool_name, exc)
        raise RuntimeError(f"invalid MCP retry state for {tool_name}")


def _temporary_unavailable_payload(tool_name: str, exc: Exception) -> dict[str, Any]:
    return {
        "ok": False,
        "status": "temporary_unavailable",
        "settings_mutated": False,
        "error": error_envelope(
            code="mcp.temporary_unavailable",
            message=f"Flux memory backend is temporarily unavailable while running {tool_name}.",
            component="mcp",
            stage=tool_name,
            retryable=True,
            user_action="Retry after the Flux API, database, or search service finishes restarting.",
            technical_detail=str(exc),
            status_code=503,
        ),
    }


def _tool_error_payload(tool_name: str, exc: Exception) -> dict[str, Any]:
    return {
        "ok": False,
        "status": "tool_error",
        "settings_mutated": False,
        "error": error_envelope(
            code="mcp.tool_error",
            message=f"Flux memory tool failed while running {tool_name}.",
            component="mcp",
            stage=tool_name,
            retryable=False,
            user_action="Inspect the tool error and retry after fixing the underlying issue.",
            technical_detail=str(exc),
            status_code=500,
        ),
    }


def _is_transient_backend_failure(exc: BaseException) -> bool:
    if isinstance(exc, (ConnectionRefusedError, ConnectionResetError, TimeoutError, BrokenPipeError, URLError)):
        return True
    if isinstance(exc, OSError) and getattr(exc, "errno", None) in _TRANSIENT_ERRNOS:
        return True
    if _is_psycopg_transient(exc):
        return True
    if exc.__class__.__name__ == "SearchIndexError" and _has_transient_message(str(exc)):
        return True
    reason = getattr(exc, "reason", None)
    if isinstance(reason, BaseException) and _is_transient_backend_failure(reason):
        return True
    cause = exc.__cause__ or exc.__context__
    if cause and cause is not exc:
        return _is_transient_backend_failure(cause)
    return _has_transient_message(str(exc)) and exc.__class__.__name__ in {"SearchIndexError", "URLError"}


def _is_psycopg_transient(exc: BaseException) -> bool:
    try:
        import psycopg
    except Exception:  # pragma: no cover - optional dependency
        return False

    classes: list[type[BaseException]] = []
    for name in ("OperationalError", "InterfaceError"):
        candidate = getattr(psycopg, name, None)
        if isinstance(candidate, type):
            classes.append(candidate)
    errors = getattr(psycopg, "errors", None)
    for name in (
        "AdminShutdown",
        "CannotConnectNow",
        "ConnectionDoesNotExist",
        "ConnectionException",
        "ConnectionFailure",
        "SqlclientUnableToEstablishSqlconnection",
    ):
        candidate = getattr(errors, name, None) if errors else None
        if isinstance(candidate, type):
            classes.append(candidate)
    return bool(classes) and isinstance(exc, tuple(classes))


def _has_transient_message(message: str) -> bool:
    lowered = message.lower()
    return any(marker in lowered for marker in _TRANSIENT_MESSAGE_MARKERS)


def create_server(
    service_factory: Callable[[], KnowledgeService] = KnowledgeService,
    retry_sleep: Callable[[float], None] = time.sleep,
):
    try:
        from mcp.server.fastmcp import FastMCP
    except ImportError as exc:  # pragma: no cover - optional dependency
        raise RuntimeError("Install MCP support with `pip install -e .[mcp]`") from exc

    runtime = _McpToolRuntime(service_factory, retry_sleep)
    service = _ServiceProxy(runtime)
    mcp = FastMCP(
        "Flux-LLM-KB",
        instructions=(
            "Use kb.brief before non-trivial work for compact workspace-scoped context. "
            "You may query mid-turn with expanded kb.search using "
            "scope_mode=\"workspace_boosted\" when "
            "you need prior decisions, unresolved project context, patterns from other "
            "workspaces, general indexed documents, previous fixes, or user-referenced "
            "history. Broad kb.search/kb.brief/kb.explain calls exclude code results by default. "
            "When broad lookup should return code, pass filters={\"file_kinds\":[\"code\"]} "
            "as the only file_kinds value, or use kb.code_search / kb.code_symbol_lookup for code lookup. "
            "Do not infer root_name from folder names; pass cwd when available, or call kb.code_status(cwd=...) "
            "and use the exact returned root_name. Use kb.code_search mode=\"literal_symbol\" for known symbols, "
            "definitions, relationships, or paths. Use mode=\"full_text\" for natural-language terms, stderr fragments, "
            "job text, or implementation-body searches over indexed code chunks. "
            "For mixed memory and code context, run separate broad non-code and code-specific calls. "
            "Skip KB queries when local files, the prompt, or current tool "
            "output already answer the question. Use kb.remember for concise durable "
            "atomic saves when a verified decision, fix, reusable procedure, command, "
            "or project fact should be retrievable before turn end; do not wait for turn finalization. "
            "Pass cwd or root_name for workspace provenance. "
            "Finalize with kb.finalize_turn at turn end for meaningful outcomes; avoid "
            "duplicating every prior kb.remember item. Store only redacted, durable "
            "knowledge. Do not persist secrets, raw transcripts, or private exports."
        ),
    )

    @mcp.tool(name="kb.search")
    def search(query: str, limit: int = 5, cwd: str | None = None, root_name: str | None = None, scope_mode: str = "local_first", filters: dict | None = None):
        """Search Flux memory with balanced broad relevance. Broad lookup excludes code results by default; pass filters={"file_kinds":["code"]} alone or use kb.code_search for code."""
        def operation():
            with caller_surface("mcp"):
                return service.search(query, limit=limit, cwd=cwd, root_name=root_name, scope_mode=scope_mode, filters=filters)

        return runtime.call("kb.search", operation, readonly=True)

    @mcp.tool(name="kb.explain")
    def explain(query: str, limit: int = 5, token_budget: int = 1200, cwd: str | None = None, root_name: str | None = None, scope_mode: str = "local_first", filters: dict | None = None):
        """Search Flux memory with explanations. Broad explain excludes code results by default; pass filters={"file_kinds":["code"]} alone for code."""
        def operation():
            with caller_surface("mcp"):
                return service.explain(
                    query,
                    limit=limit,
                    token_budget=token_budget,
                    cwd=cwd,
                    root_name=root_name,
                    scope_mode=scope_mode,
                    filters=filters,
                )

        return runtime.call("kb.explain", operation, readonly=True)

    @mcp.tool(name="kb.brief")
    def brief(query: str, token_budget: int = 1200, cwd: str | None = None, root_name: str | None = None, scope_mode: str = "local_first", filters: dict | None = None):
        """Build a compact brief from balanced search. Broad briefs exclude code results by default; pass filters={"file_kinds":["code"]} alone for code-specific briefs."""
        def operation():
            with caller_surface("mcp"):
                return service.brief(
                    query,
                    token_budget=token_budget,
                    cwd=cwd,
                    root_name=root_name,
                    scope_mode=scope_mode,
                    filters=filters,
                )

        return runtime.call("kb.brief", operation, readonly=True)

    @mcp.tool(name="kb.remember")
    def remember(title: str, body: str, cwd: str | None = None, root_name: str | None = None):
        """Use kb.remember for concise durable atomic saves during work; pass cwd or root_name and store only redacted knowledge."""
        def operation():
            return service.remember(title, body, cwd=cwd, root_name=root_name).__dict__

        return runtime.call("kb.remember", operation, readonly=False)

    @mcp.tool(name="kb.claim_upsert")
    def claim_upsert(
        subject_type: str,
        subject: str,
        predicate: str,
        object_text: str,
        confidence: float = 0.5,
        episode_id: str | None = None,
    ):
        """Create or update an atomic claim linked to an optional source episode."""
        return service.upsert_claim(
            subject_type=subject_type,
            subject_name=subject,
            predicate=predicate,
            object_text=object_text,
            confidence=confidence,
            episode_id=episode_id,
        )

    @mcp.tool(name="kb.claim_transition")
    def claim_transition(
        claim_id: str,
        transition: str,
        related_claim_id: str | None = None,
        reason: str | None = None,
        confidence_delta: float = 0.0,
    ):
        """Transition a claim lifecycle state and append an audit-visible event."""
        return service.transition_claim(
            claim_id=claim_id,
            transition=transition,
            related_claim_id=related_claim_id,
            reason=reason,
            confidence_delta=confidence_delta,
            actor="mcp",
        )

    @mcp.tool(name="kb.graph_traverse")
    def graph_traverse(
        entity_id: str,
        relation_types: list[str] | None = None,
        max_depth: int = 2,
        direction: str = "out",
        limit: int = 100,
    ):
        """Traverse typed knowledge graph relations from an entity."""
        return service.traverse_graph(
            entity_id=entity_id,
            relation_types=relation_types,
            max_depth=max_depth,
            direction=direction,
            limit=limit,
        )

    @mcp.tool(name="kb.capture_review")
    def capture_review(status: str = "pending_review", limit: int = 50):
        """List capture-review jobs by status without raw capture payloads."""
        return service.list_capture_review_jobs(status=status, limit=limit)

    @mcp.tool(name="kb.capture_review_decide")
    def capture_review_decide(job_id: str, decision: str, rationale: str):
        """Approve or reject a capture-review job with a required rationale."""
        return service.review_capture_job(
            job_id=job_id,
            decision=decision,
            rationale=rationale,
            actor="mcp",
        )

    @mcp.tool(name="kb.capture_review_ingest")
    def capture_review_ingest(job_id: str | None = None, limit: int = 25, dry_run: bool = False):
        """Ingest approved Codex backfill capture-review jobs with redaction and audit metadata."""
        return service.ingest_capture_review_jobs(
            job_id=job_id,
            limit=limit,
            dry_run=dry_run,
            actor="mcp",
        )

    @mcp.tool(name="kb.retention_policies")
    def retention_policies():
        """List retention policies for claims, episodes, and corpus assets."""
        return service.list_retention_policies()

    @mcp.tool(name="kb.retention_quality")
    def retention_quality(limit: int = 25):
        """Report retention and memory quality candidates without raw content."""
        return service.retention_quality_report(limit=limit)

    @mcp.tool(name="kb.semantic_duplicates_refresh")
    def semantic_duplicates_refresh(memory_class: str = "all", root_name: str | None = None, threshold: float | None = None, limit: int = 1000):
        """Refresh advisory semantic duplicate clusters for corpus chunks, episodes, or claims."""
        return service.refresh_semantic_duplicate_clusters(
            memory_class=memory_class,
            root_name=root_name,
            threshold=threshold,
            limit=limit,
        )

    @mcp.tool(name="kb.semantic_duplicates_list")
    def semantic_duplicates_list(memory_class: str | None = None, root_name: str | None = None, limit: int = 50):
        """List active semantic duplicate clusters without raw suppressed content."""
        return service.list_semantic_duplicate_clusters(
            memory_class=memory_class,
            root_name=root_name,
            limit=limit,
        )

    @mcp.tool(name="kb.acceleration_status")
    def acceleration_status():
        """Return local acceleration capability, cache layout, and worker-family queue telemetry."""
        from .acceleration import collect_acceleration_status

        return collect_acceleration_status()

    @mcp.tool(name="kb.watch_probe")
    def watch_probe(timeout_seconds: float = 2.0):
        """Probe watcher backend behavior in a temporary directory without touching watched roots."""
        return service.watch_probe(timeout_seconds=timeout_seconds)

    @mcp.tool(name="kb.worker_status")
    def worker_status(family: str = "all"):
        """Return worker-family queue, cap, backpressure, retry, and slow-job status."""
        return service.worker_status(family=family)

    @mcp.tool(name="kb.crawl_backfill")
    def crawl_backfill(kind: str = "all", limit: int | None = None, workers: int | None = None, root_name: str | None = None, family: str | None = None, callback_url: str | None = None):
        """Enqueue a bounded corpus backfill by kind or exact worker family, optionally scoped to a root."""
        enqueue = getattr(service, "enqueue_corpus_backfill", None)
        if enqueue:
            return enqueue(
                kind=family or kind,
                limit=limit,
                workers=workers,
                root_name=root_name,
                callback_url=callback_url,
            )
        raise RuntimeError("crawl_backfill requires enqueue_corpus_backfill; direct inline backfill is not an MCP path")

    @mcp.tool(name="kb.benchmark_run")
    def benchmark_run(fixture: str = "all", files: int = 10, mode: str = "scan", passes: int = 1, label: str | None = None, compare_label: str | None = None, workers: int = 1, family: str = "all", scope: str = "synthetic", root_name: str | None = None, path: str | None = None, max_files: int | None = None, deployment_label: str | None = None, scenario: str = "standard", include_model_probe: bool = False):
        """Run deterministic synthetic or aggregate-only scoped benchmarks and record metadata-only history."""
        return service.run_benchmark(
            fixture=fixture,
            files=files,
            mode=mode,
            passes=passes,
            label=label,
            compare_label=compare_label,
            workers=workers,
            family=family,
            scope=scope,
            root_name=root_name,
            path=path,
            max_files=max_files,
            deployment_label=deployment_label,
            scenario=scenario,
            include_model_probe=include_model_probe,
        )

    @mcp.tool(name="kb.benchmark_history")
    def benchmark_history(fixture: str | None = None, mode: str | None = None, label: str | None = None, warm_state: str | None = None, scope_type: str | None = None, deployment_label: str | None = None, scenario: str | None = None, scope_hash: str | None = None, freshness_hours: int | None = None, limit: int = 20):
        """List metadata-only synthetic benchmark run history and previous-run deltas."""
        return service.benchmark_history(
            fixture=fixture,
            mode=mode,
            label=label,
            warm_state=warm_state,
            scope_type=scope_type,
            deployment_label=deployment_label,
            scenario=scenario,
            scope_hash=scope_hash,
            freshness_hours=freshness_hours,
            limit=limit,
        )

    @mcp.tool(name="kb.indexer_reliability_status")
    def indexer_reliability_status(root_name: str | None = None, path: str | None = None, label: str | None = None, deployment_label: str | None = None, compare_label: str | None = None, freshness_hours: int = 336, limit: int = 100):
        """Return the metadata-only indexer reliability evidence gate status."""
        return service.indexer_reliability_status(
            root_name=root_name,
            path=path,
            label=label,
            deployment_label=deployment_label,
            compare_label=compare_label,
            freshness_hours=freshness_hours,
            limit=limit,
        )

    @mcp.tool(name="kb.indexer_reliability_run")
    def indexer_reliability_run(scope: str = "synthetic", root_name: str | None = None, path: str | None = None, label: str | None = None, deployment_label: str | None = None, compare_label: str | None = None, max_files: int = 1000, passes: int = 2, include_cache_readiness: bool = False, include_tuning: bool = True, evidence_level: str = "standard"):
        """Run the indexer reliability validation suite without mutating settings."""
        return service.run_indexer_reliability(
            scope=scope,
            root_name=root_name,
            path=path,
            label=label,
            deployment_label=deployment_label,
            compare_label=compare_label,
            max_files=max_files,
            passes=passes,
            include_cache_readiness=include_cache_readiness,
            include_tuning=include_tuning,
            evidence_level=evidence_level,
        )

    @mcp.tool(name="kb.operator_evidence")
    def operator_evidence(label: str | None = None, deployment_label: str | None = None, compare_label: str | None = None, freshness_hours: int = 336, limit: int = 100):
        """Return combined operator evidence gates for reliability, code diagnostics, and blockers."""
        return service.operator_evidence(
            label=label,
            deployment_label=deployment_label,
            compare_label=compare_label,
            freshness_hours=freshness_hours,
            limit=limit,
        )

    @mcp.tool(name="kb.indexer_root_reliability")
    def indexer_root_reliability(root_name: str):
        """Return a monitored-root reliability card with sanitized counts and latest benchmark evidence."""
        return service.indexer_root_reliability(root_name=root_name)

    @mcp.tool(name="kb.indexer_reliability_roots")
    def indexer_reliability_roots(include_disabled: bool = False, freshness_hours: int = 336, limit: int = 100):
        """Return all monitored-root reliability cards with readiness totals and required actions."""
        return service.indexer_reliability_roots(
            include_disabled=include_disabled,
            freshness_hours=freshness_hours,
            limit=limit,
        )

    @mcp.tool(name="kb.code_status")
    def code_status(root_name: str | None = None, cwd: str | None = None):
        """Use kb.code_status for privacy-safe code index coverage, parser status, and fallback summaries; pass cwd to resolve the exact root_name."""
        def operation():
            return service.code_status(root_name=root_name, cwd=cwd)

        return runtime.call("kb.code_status", operation, readonly=True)

    @mcp.tool(name="kb.code_search")
    def code_search(query: str, root_name: str | None = None, cwd: str | None = None, mode: str = "literal_symbol", language: str | None = None, symbol_kind: str | None = None, relationship: str | None = None, path_glob: str | None = None, include_generated: bool = False, limit: int = 20):
        """Use kb.code_search in literal_symbol mode for symbols/paths or full_text mode for indexed code chunks; pass cwd rather than guessing root_name."""
        def operation():
            return service.code_search(
                query=query,
                root_name=root_name,
                cwd=cwd,
                mode=mode,
                language=language,
                symbol_kind=symbol_kind,
                relationship=relationship,
                path_glob=path_glob,
                include_generated=include_generated,
                limit=limit,
            )

        return runtime.call("kb.code_search", operation, readonly=True)

    @mcp.tool(name="kb.code_symbol_lookup")
    def code_symbol_lookup(symbol: str, root_name: str | None = None, language: str | None = None, include_references: bool = True, limit: int = 20):
        """Use kb.code_symbol_lookup to look up a code symbol and optional references with sanitized metadata."""
        def operation():
            return service.code_symbol_lookup(
                symbol=symbol,
                root_name=root_name,
                language=language,
                include_references=include_references,
                limit=limit,
            )

        return runtime.call("kb.code_symbol_lookup", operation, readonly=True)

    @mcp.tool(name="kb.code_feedback_record")
    def code_feedback_record(query: str, root_name: str | None = None, result_count: int = 0, surface: str = "mcp", miss_category: str = "other", expected_symbol: str | None = None, path: str | None = None):
        """Record privacy-safe code retrieval miss feedback without raw query or code persistence."""
        return service.record_code_feedback(
            query=query,
            root_name=root_name,
            result_count=result_count,
            surface=surface,
            miss_category=miss_category,
            expected_symbol=expected_symbol,
            path=path,
            metadata={},
        )

    @mcp.tool(name="kb.code_feedback_summary")
    def code_feedback_summary(root_name: str | None = None, limit: int = 20):
        """Summarize privacy-safe code retrieval feedback by miss category and root."""
        return service.code_feedback_summary(root_name=root_name, limit=limit)

    @mcp.tool(name="kb.operational_diagnostics")
    def operational_diagnostics(section: str = "all", limit: int = 25, root_name: str | None = None, status: str | None = None, family: str | None = None, since_hours: int | None = None, include_details: bool = False):
        """Return read-only operational diagnostics for retrieval, watcher, workers, jobs, and mail."""
        def operation():
            return service.operational_diagnostics(
                section=section,
                limit=limit,
                root_name=root_name,
                status=status,
                family=family,
                since_hours=since_hours,
                include_details=include_details,
            )

        return runtime.call("kb.operational_diagnostics", operation, readonly=True)

    @mcp.tool(name="kb.diagnostics_remediate")
    def diagnostics_remediate(action: str, target_type: str, target_id: str | None = None, root_name: str | None = None, family: str | None = None, reason: str = "operator diagnostic remediation"):
        """Run a confirmation-gated diagnostic remediation action; never mutates runtime settings."""
        return service.remediate_diagnostic(
            action=action,
            target_type=target_type,
            target_id=target_id,
            root_name=root_name,
            family=family,
            reason=reason,
            actor="mcp",
        )

    @mcp.tool(name="kb.retrieval_benchmark_run")
    def retrieval_benchmark_run(suite: str = "standard", label: str | None = None, compare_label: str | None = None, limit_per_query: int = 5, token_budget: int | None = None, persist: bool = True):
        """Run the synthetic retrieval-quality benchmark suite with confidence bands, calibration candidates, and metric deltas."""
        return service.run_retrieval_benchmark(
            suite=suite,
            label=label,
            compare_label=compare_label,
            limit_per_query=limit_per_query,
            token_budget=token_budget,
            persist=persist,
        )

    @mcp.tool(name="kb.retrieval_benchmark_history")
    def retrieval_benchmark_history(suite: str | None = None, label: str | None = None, limit: int = 20):
        """List metadata-only retrieval benchmark history with confidence bands, calibration candidates, and metric deltas."""
        return service.retrieval_benchmark_history(
            suite=suite,
            label=label,
            limit=limit,
        )

    @mcp.tool(name="kb.automation_status")
    def automation_status():
        """Read guarded operator automation status, eligible allowlisted actions, and manual-required items."""
        return service.operator_automation_status()

    @mcp.tool(name="kb.automation_run")
    def automation_run(mode: str = "guarded", limit: int = 25, dry_run: bool = False):
        """Enqueue one guarded operator automation pass; never mutates runtime settings inline."""
        return service.enqueue_operator_automation(mode=mode, trigger="manual", actor="mcp", limit=limit, dry_run=dry_run)

    @mcp.tool(name="kb.automation_actions")
    def automation_actions(status: str = "all", run_id: str | None = None, action: str | None = None, limit: int = 50):
        """List sanitized guarded automation action history with settings_mutated=false evidence."""
        return service.operator_automation_actions(status=status, run_id=run_id, action=action, limit=limit)

    @mcp.tool(name="kb.governance_run")
    def governance_run(mode: str = "shadow", limit: int = 25):
        """Enqueue a memory governance proposal run; defaults to shadow mode and never mutates runtime settings inline."""
        return service.enqueue_governance_run(mode=mode, actor="mcp", limit=limit)

    @mcp.tool(name="kb.governance_actions")
    def governance_actions(status: str = "proposed", limit: int = 50):
        """List sanitized memory governance actions with telemetry by source, action, risk, status, and mutation result."""
        return service.governance_actions(status=status, limit=limit)

    @mcp.tool(name="kb.governance_apply")
    def governance_apply(action_id: str, rationale: str, confirm: bool = False):
        """Apply a confirmed governance action when benchmark and protection guardrails allow it."""
        return service.governance_apply(action_id, rationale=rationale, confirm=confirm, actor="mcp")

    @mcp.tool(name="kb.governance_recover")
    def governance_recover(action_id: str, rationale: str, confirm: bool = False):
        """Recover a previously applied governance action using its captured before-state."""
        return service.governance_recover(action_id, rationale=rationale, confirm=confirm, actor="mcp")

    @mcp.tool(name="kb.governance_digest")
    def governance_digest():
        """Read the latest bounded governance digest for operator review."""
        return service.governance_digest()

    @mcp.tool(name="kb.governance_policy")
    def governance_policy():
        """Read the effective local governance automation policy and guardrail defaults."""
        return service.governance_policy()

    @mcp.tool(name="kb.finalize_turn")
    def finalize_turn(title: str, summary: str, cwd: str | None = None, root_name: str | None = None):
        """Finalize the current agent turn by storing a redacted durable summary. Finalize with kb.finalize_turn at turn end; avoid duplicating every prior kb.remember item."""
        def operation():
            return service.remember(title, summary, metadata={"source": "finalize_turn"}, cwd=cwd, root_name=root_name).__dict__

        return runtime.call("kb.finalize_turn", operation, readonly=False)

    @mcp.tool(name="kb.audit")
    def audit(limit: int = 50):
        """List recent audit events for memory and corpus operations."""
        return service.audit(limit=limit)

    @mcp.tool(name="kb.forget")
    def forget(memory_id: str, reason: str = "user_request"):
        """Forget a memory item by id with an audit reason."""
        return service.forget(memory_id, reason=reason)

    @mcp.tool(name="kb.status")
    def status():
        """Return Flux health, settings, extractor, and runtime status."""
        def operation():
            from .health import doctor_payload

            return doctor_payload()

        return runtime.call("kb.status", operation, readonly=True)

    @mcp.tool(name="kb.crawl_status")
    def crawl_status():
        """Return corpus crawler, watcher, job, and retrieval status."""
        from .health import collect_crawl_payload

        return collect_crawl_payload()

    @mcp.tool(name="kb.crawl_sync")
    def crawl_sync(root_name: str | None = None, path: str | None = None, dry_run: bool = False):
        """Sync monitored corpus roots or paths, optionally as a dry run."""
        return service.sync_corpus(root_name=root_name, path=path, dry_run=dry_run)

    @mcp.tool(name="kb.crawl_watch_status")
    def crawl_watch_status():
        """List watched corpus roots and watcher runtime state."""
        from . import database

        return database.crawl_status()

    @mcp.tool(name="kb.crawl_watch_enable")
    def crawl_watch_enable(root_name: str | None = None):
        """Enable corpus filesystem watching for one root or all roots."""
        from . import database

        return database.set_watch_enabled(root_name=root_name, enabled=True)

    @mcp.tool(name="kb.crawl_watch_disable")
    def crawl_watch_disable(root_name: str | None = None):
        """Disable corpus filesystem watching for one root or all roots."""
        from . import database

        return database.set_watch_enabled(root_name=root_name, enabled=False)

    @mcp.tool(name="kb.crawl_jobs")
    def crawl_jobs(limit: int = 50):
        """List recent corpus extraction and capture jobs."""
        from . import database

        return {"jobs": database.list_capture_jobs(limit=limit)}

    @mcp.tool(name="kb.mail_status")
    def mail_status():
        """Return mail ingestion, profile, OAuth, and scheduler status."""
        from .mail_ingestion import mail_status as collect_mail_status

        return collect_mail_status()

    return mcp


def main() -> None:
    create_server().run()


if __name__ == "__main__":
    main()
