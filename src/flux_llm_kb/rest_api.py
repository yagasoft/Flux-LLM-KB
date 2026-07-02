from html import escape
from json import JSONDecodeError
from pathlib import Path
from typing import Any

from . import database
from .host_agent import (
    path_requires_host_agent,
    remote_backfill,
    remote_benchmark,
    remote_browse_folder,
    remote_status,
    remote_sync,
    remote_validate_path,
    remote_file_action,
    remote_job_file_action,
    validate_host_path,
)
from .error_diagnostics import (
    FluxApiError,
    error_response_payload,
    http_error_envelope,
    validation_error_envelope,
)
from .health import (
    build_dashboard_html,
    collect_crawl_payload,
    collect_dashboard_payload,
    collect_jobs_payload,
    collect_retrieval_payload,
    doctor_payload,
)
from .service import KnowledgeService, normalize_retrieval_filters


def _filters_from_body(filters: dict | None) -> dict[str, Any] | None:
    if filters is None:
        return None
    return normalize_retrieval_filters(filters)


def _filters_from_query(
    *,
    kind: list[str] | None,
    current_only: bool | None,
    lifecycle_state: list[str] | None,
    include_suppressed: bool | None,
    file_kind: list[str] | None = None,
    language: list[str] | None = None,
    symbol_kind: list[str] | None = None,
    relationship: list[str] | None = None,
    path_glob: list[str] | None = None,
    include_generated: bool | None = None,
) -> dict[str, Any] | None:
    if (
        not kind
        and current_only is None
        and not lifecycle_state
        and include_suppressed is None
        and not file_kind
        and not language
        and not symbol_kind
        and not relationship
        and not path_glob
        and include_generated is None
    ):
        return None
    return normalize_retrieval_filters(
        {
            "logical_kinds": kind or [],
            "current_only": bool(current_only),
            "lifecycle_states": lifecycle_state or [],
            "include_suppressed": bool(include_suppressed),
            "file_kind": file_kind or [],
            "language": language or [],
            "symbol_kind": symbol_kind or [],
            "relationship": relationship or [],
            "path_glob": path_glob or [],
            "include_generated": bool(include_generated),
        }
    )


def _service_retrieval_kwargs(**kwargs: Any) -> dict[str, Any]:
    filters = kwargs.pop("filters", None)
    result = dict(kwargs)
    if filters is not None:
        result["filters"] = filters
    return result


def create_app():
    try:
        from fastapi import Body, FastAPI, HTTPException, Query, Request
        from fastapi.exceptions import RequestValidationError
        from fastapi.responses import HTMLResponse, JSONResponse
        from fastapi.staticfiles import StaticFiles
        from pydantic import BaseModel, ConfigDict, Field
    except ImportError as exc:  # pragma: no cover - optional dependency
        raise RuntimeError("Install REST support with `pip install -e .[api]`") from exc

    class RememberRequest(BaseModel):
        title: str
        body: str
        cwd: str | None = None
        root_name: str | None = None

    class SearchRequest(BaseModel):
        query: str
        limit: int = 5
        cwd: str | None = None
        root_name: str | None = None
        scope_mode: str = "local_first"
        filters: dict | None = None

    class BriefRequest(BaseModel):
        query: str
        token_budget: int | None = None
        cwd: str | None = None
        root_name: str | None = None
        scope_mode: str = "local_first"
        filters: dict | None = None

    class ExplainRequest(BaseModel):
        query: str
        limit: int = 5
        token_budget: int | None = None
        cwd: str | None = None
        root_name: str | None = None
        scope_mode: str = "local_first"
        filters: dict | None = None

    class JobDeleteRequest(BaseModel):
        reason: str = "operator_cleanup"

    class JobFileActionRequest(BaseModel):
        model_config = ConfigDict(extra="forbid")

        action: str

    class RetrievalBenchmarkRunRequest(BaseModel):
        suite: str = "standard"
        label: str | None = None
        compare_label: str | None = None
        limit_per_query: int = 5
        token_budget: int | None = None
        persist: bool = True

    class GovernanceRunRequest(BaseModel):
        mode: str = "shadow"
        limit: int = 25

    class AutomationRunRequest(BaseModel):
        mode: str = "guarded"
        limit: int = 25
        dry_run: bool = False

    class GovernanceActionRequest(BaseModel):
        rationale: str
        confirm: bool = False

    class ClaimRequest(BaseModel):
        subject_type: str
        subject: str
        predicate: str
        object_text: str
        confidence: float = 0.5
        episode_id: str | None = None
        metadata: dict | None = None

    class ClaimTransitionRequest(BaseModel):
        transition: str
        related_claim_id: str | None = None
        reason: str | None = None
        confidence_delta: float = 0.0

    class CaptureReviewDecisionRequest(BaseModel):
        decision: str
        rationale: str

    class CaptureReviewIngestRequest(BaseModel):
        job_id: str | None = None
        limit: int = 25
        dry_run: bool = False

    class RetentionPolicyRequest(BaseModel):
        half_life_days: int
        min_confidence: float
        action: str
        reason: str

    class SemanticDuplicateRefreshRequest(BaseModel):
        memory_class: str = "all"
        root_name: str | None = None
        threshold: float | None = None
        limit: int = 1000

    class EmbeddingRefreshRequest(BaseModel):
        owner_class: str = "all"
        root_name: str | None = None
        stale_only: bool = True
        limit: int = 100

    class ForgetRequest(BaseModel):
        memory_id: str
        reason: str = "user_request"

    class CrawlSyncRequest(BaseModel):
        root_name: str | None = None
        path: str | None = None
        dry_run: bool = False

    class CrawlRootRequest(BaseModel):
        name: str
        root_path: str
        enabled: bool = True
        recursive: bool = True
        watch_enabled: bool = True
        initial_crawl: bool = True
        dry_run: bool = False
        trust_rank: int = 500
        include_globs: list[str] = Field(default_factory=list)
        exclude_globs: list[str] = Field(default_factory=list)
        glob_mode: str = "extend"
        max_inline_bytes: int | None = None
        heavy_threshold_bytes: int | None = None
        strict_indexing: bool = False

    class CrawlRootUpdateRequest(BaseModel):
        name: str
        root_path: str
        enabled: bool = True
        recursive: bool = True
        watch_enabled: bool = True
        trust_rank: int = 500
        include_globs: list[str] = Field(default_factory=list)
        exclude_globs: list[str] = Field(default_factory=list)
        glob_mode: str = "extend"
        max_inline_bytes: int | None = None
        heavy_threshold_bytes: int | None = None
        strict_indexing: bool | None = None

    class WatchRequest(BaseModel):
        root_name: str | None = None
        enabled: bool

    class WatchProbeRequest(BaseModel):
        timeout_seconds: float = 2.0

    class CrawlBackfillRequest(BaseModel):
        kind: str = "all"
        limit: int | None = None
        workers: int | None = None
        root_name: str | None = None
        family: str | None = None

    class BenchmarkRunRequest(BaseModel):
        fixture: str = "all"
        files: int = 10
        mode: str = "scan"
        passes: int = 1
        label: str | None = None
        compare_label: str | None = None
        workers: int = 1
        family: str = "all"
        scope: str = "synthetic"
        root_name: str | None = None
        path: str | None = None
        max_files: int | None = None
        deployment_label: str | None = None
        scenario: str = "standard"
        include_model_probe: bool = False

    class ReliabilityRunRequest(BaseModel):
        scope: str = "synthetic"
        root_name: str | None = None
        path: str | None = None
        label: str | None = None
        deployment_label: str | None = None
        compare_label: str | None = None
        max_files: int = 1000
        passes: int = 2
        include_cache_readiness: bool = False
        include_tuning: bool = True
        evidence_level: str = "standard"

    class CodeFeedbackRequest(BaseModel):
        query: str
        root_name: str | None = None
        result_count: int = 0
        surface: str = "rest"
        miss_category: str = "other"
        expected_symbol: str | None = None
        path: str | None = None
        metadata: dict | None = None

    class DiagnosticsActionRequest(BaseModel):
        action: str
        target_type: str
        target_id: str | None = None
        root_name: str | None = None
        family: str | None = None
        reason: str = "operator diagnostic remediation"

    class SettingUpdateRequest(BaseModel):
        value: object
        confirmed: bool = False
        reason: str | None = None

    class SettingsApplyRequest(BaseModel):
        component: str | None = None

    class MailProfileRequest(BaseModel):
        name: str
        source_type: str
        folder_paths: list[str]
        spool_path: str
        account: str | None = None
        server: str | None = None
        post_process_policy: str = "move_to_processed"
        processed_folder: str | None = None
        trash_folder: str | None = None
        destructive_post_process_confirmed: bool = False
        sync_enabled: bool = False
        sync_interval_seconds: int = 900
        sync_window_days: int = 30
        max_messages_per_run: int = 200
        include_subfolders: bool | None = None
        outlook_incremental_basis: str | None = None

    class MailSyncRequest(BaseModel):
        profile_name: str | None = None

    class MailPostProcessDryRunRequest(BaseModel):
        limit: int = 5

    class GmailOAuthStartRequest(BaseModel):
        profile_name: str
        client_config_path: str
        redirect_uri: str | None = None

    class GmailOAuthClientConfigRequest(BaseModel):
        client_config_path: str

    class OutlookHostSyncRequest(BaseModel):
        profile_name: str

    class FileActionRequest(BaseModel):
        model_config = ConfigDict(extra="forbid")

        action: str

    app = FastAPI(title="Flux-LLM-KB")
    service = KnowledgeService()
    dashboard_assets = Path(__file__).resolve().parent / "dashboard_static" / "assets"
    if dashboard_assets.exists():
        app.mount("/dashboard/assets", StaticFiles(directory=str(dashboard_assets)), name="dashboard-assets")

    @app.exception_handler(FluxApiError)
    async def flux_api_error_handler(_request: Request, exc: FluxApiError):
        return JSONResponse(status_code=exc.status_code, content=error_response_payload(exc.envelope()))

    @app.exception_handler(HTTPException)
    async def http_exception_handler(_request: Request, exc: HTTPException):
        envelope = http_error_envelope(exc.status_code, exc.detail)
        return JSONResponse(status_code=exc.status_code, content=error_response_payload(envelope))

    @app.exception_handler(RequestValidationError)
    async def validation_exception_handler(_request: Request, exc: RequestValidationError):
        envelope = validation_error_envelope(exc.errors())
        return JSONResponse(status_code=422, content=error_response_payload(envelope))

    @app.get("/", response_class=HTMLResponse)
    def root(state: str | None = None, code: str | None = None, error: str | None = None):
        if not state and not code and not error:
            return HTMLResponse(
                "<!doctype html><meta http-equiv=\"refresh\" content=\"0; url=/dashboard\">"
                "<a href=\"/dashboard\">Open Flux dashboard</a>"
            )
        return HTMLResponse(_mail_oauth_callback_html(_mail_oauth_callback_payload(state=state, code=code, error=error)))

    @app.get("/api/health")
    def health():
        return doctor_payload()

    @app.get("/api/acceleration/status")
    def acceleration_status():
        from . import acceleration

        return acceleration.collect_acceleration_status()

    @app.post("/api/acceleration/benchmarks/run")
    def acceleration_benchmark_run(request: BenchmarkRunRequest = Body(...)):
        if _should_proxy_benchmark(request.scope, request.root_name, request.path):
            return host_agent_benchmark(
                fixture=request.fixture,
                files=request.files,
                mode=request.mode,
                passes=request.passes,
                label=request.label,
                compare_label=request.compare_label,
                workers=request.workers,
                family=request.family,
                scope=request.scope,
                root_name=request.root_name,
                path=request.path,
                max_files=request.max_files,
                deployment_label=request.deployment_label,
                scenario=request.scenario,
                include_model_probe=request.include_model_probe,
            )
        return service.run_benchmark(
            fixture=request.fixture,
            files=request.files,
            mode=request.mode,
            passes=request.passes,
            label=request.label,
            compare_label=request.compare_label,
            workers=request.workers,
            family=request.family,
            scope=request.scope,
            root_name=request.root_name,
            path=request.path,
            max_files=request.max_files,
            deployment_label=request.deployment_label,
            scenario=request.scenario,
            include_model_probe=request.include_model_probe,
        )

    @app.get("/api/acceleration/benchmarks")
    def acceleration_benchmark_history(
        fixture: str | None = None,
        mode: str | None = None,
        label: str | None = None,
        warm_state: str | None = None,
        scope_type: str | None = None,
        scope_hash: str | None = None,
        deployment_label: str | None = None,
        scenario: str | None = None,
        freshness_hours: int | None = None,
        limit: int = 20,
    ):
        return service.benchmark_history(
            fixture=fixture,
            mode=mode,
            label=label,
            warm_state=warm_state,
            scope_type=scope_type,
            scope_hash=scope_hash,
            deployment_label=deployment_label,
            scenario=scenario,
            freshness_hours=freshness_hours,
            limit=limit,
        )

    @app.get("/api/acceleration/reliability")
    def acceleration_reliability_status(
        root_name: str | None = None,
        path: str | None = None,
        label: str | None = None,
        deployment_label: str | None = None,
        compare_label: str | None = None,
        freshness_hours: int = 336,
        limit: int = 100,
    ):
        return service.indexer_reliability_status(
            root_name=root_name,
            path=path,
            label=label,
            deployment_label=deployment_label,
            compare_label=compare_label,
            freshness_hours=freshness_hours,
            limit=limit,
        )

    @app.post("/api/acceleration/reliability/run")
    def acceleration_reliability_run(request: ReliabilityRunRequest = Body(...)):
        if _should_proxy_benchmark(request.scope, request.root_name, request.path):
            return _run_indexer_reliability_with_benchmark_proxy(service, request)
        return service.run_indexer_reliability(
            scope=request.scope,
            root_name=request.root_name,
            path=request.path,
            label=request.label,
            deployment_label=request.deployment_label,
            compare_label=request.compare_label,
            max_files=request.max_files,
            passes=request.passes,
            include_cache_readiness=request.include_cache_readiness,
            include_tuning=request.include_tuning,
            evidence_level=request.evidence_level,
        )

    @app.get("/api/acceleration/evidence")
    def acceleration_operator_evidence(
        label: str | None = None,
        deployment_label: str | None = None,
        compare_label: str | None = None,
        freshness_hours: int = 336,
        limit: int = 100,
    ):
        return service.operator_evidence(
            label=label,
            deployment_label=deployment_label,
            compare_label=compare_label,
            freshness_hours=freshness_hours,
            limit=limit,
        )

    @app.get("/api/acceleration/reliability/roots")
    def acceleration_reliability_roots(
        include_disabled: bool = False,
        freshness_hours: int = 336,
        limit: int = 100,
    ):
        return service.indexer_reliability_roots(
            include_disabled=include_disabled,
            freshness_hours=freshness_hours,
            limit=limit,
        )

    @app.get("/api/acceleration/reliability/root/{root_name}")
    def acceleration_reliability_root(root_name: str):
        return service.indexer_root_reliability(root_name)

    @app.get("/api/code/status")
    def code_status(root_name: str | None = None, cwd: str | None = None):
        return service.code_status(root_name=root_name, cwd=cwd)

    @app.get("/api/code/search")
    def code_search(
        query: str,
        root_name: str | None = None,
        cwd: str | None = None,
        mode: str = "literal_symbol",
        language: str | None = None,
        symbol_kind: str | None = None,
        relationship: str | None = None,
        path_glob: str | None = None,
        include_generated: bool = False,
        limit: int = 20,
    ):
        return service.code_search(
            query=query,
            root_name=root_name,
            cwd=cwd,
            mode=mode.replace("-", "_"),
            language=language,
            symbol_kind=symbol_kind,
            relationship=relationship,
            path_glob=path_glob,
            include_generated=include_generated,
            limit=limit,
        )

    @app.get("/api/code/symbols")
    def code_symbols(
        symbol: str,
        root_name: str | None = None,
        language: str | None = None,
        include_references: bool = True,
        limit: int = 20,
    ):
        return service.code_symbol_lookup(
            symbol=symbol,
            root_name=root_name,
            language=language,
            include_references=include_references,
            limit=limit,
        )

    @app.post("/api/code/feedback")
    def code_feedback(request: CodeFeedbackRequest = Body(...)):
        return service.record_code_feedback(
            query=request.query,
            root_name=request.root_name,
            result_count=request.result_count,
            surface=request.surface,
            miss_category=request.miss_category,
            expected_symbol=request.expected_symbol,
            path=request.path,
            metadata=request.metadata or {},
        )

    @app.get("/api/code/feedback/summary")
    def code_feedback_summary(root_name: str | None = None, limit: int = 20):
        return service.code_feedback_summary(root_name=root_name, limit=limit)

    @app.post("/api/diagnostics/actions")
    def diagnostics_action(request: DiagnosticsActionRequest = Body(...)):
        return service.remediate_diagnostic(
            action=request.action,
            target_type=request.target_type,
            target_id=request.target_id,
            root_name=request.root_name,
            family=request.family,
            reason=request.reason,
            actor="api",
        )

    @app.get("/api/diagnostics/{section}")
    def operational_diagnostics(
        section: str,
        limit: int = 25,
        root_name: str | None = None,
        status: str | None = None,
        family: str | None = None,
        since_hours: int | None = None,
        include_details: bool = False,
    ):
        return service.operational_diagnostics(
            section=section,
            limit=limit,
            root_name=root_name,
            status=status,
            family=family,
            since_hours=since_hours,
            include_details=include_details,
        )

    @app.get("/dashboard", response_class=HTMLResponse)
    def dashboard():
        return build_dashboard_html()

    @app.get("/api/dashboard/health")
    def dashboard_health():
        return collect_dashboard_payload()

    @app.get("/api/dashboard/crawl")
    def dashboard_crawl():
        return collect_crawl_payload()

    @app.get("/api/dashboard/jobs")
    def dashboard_jobs(
        limit: int = 50,
        offset: int = 0,
        status: list[str] | None = Query(None),
        root_name: list[str] | None = Query(None),
        job_type: list[str] | None = Query(None),
        updated_from: str | None = None,
        updated_to: str | None = None,
    ):
        return collect_jobs_payload(
            limit=limit,
            offset=offset,
            status=status,
            root_name=root_name,
            job_type=job_type,
            updated_from=updated_from,
            updated_to=updated_to,
        )

    @app.get("/api/dashboard/jobs/{job_id}/tool-invocations")
    def dashboard_job_tool_invocations(job_id: str, limit: int = 100):
        from . import database

        return {
            "job_id": job_id,
            "invocations": database.list_capture_job_tool_invocations(job_id=job_id, limit=limit),
        }

    @app.post("/api/dashboard/jobs/{job_id}/cancel")
    def dashboard_job_cancel(job_id: str):
        from . import database

        payload = database.cancel_corpus_job(job_id=job_id, actor="dashboard")
        if not payload.get("cancelled"):
            status_code = 404 if payload.get("status") == "not_found" else 409
            raise HTTPException(status_code=status_code, detail=payload.get("error") or "Corpus job cannot be cancelled.")
        return payload

    @app.post("/api/dashboard/jobs/{job_id}/retry")
    def dashboard_job_retry(job_id: str):
        try:
            return service.remediate_diagnostic(
                action="retry_corpus_job",
                target_type="job",
                target_id=job_id,
                reason="operator forced retry from dashboard",
                actor="dashboard",
            )
        except LookupError as exc:
            raise HTTPException(status_code=409, detail=str(exc)) from exc
        except ValueError as exc:
            raise HTTPException(status_code=400, detail=str(exc)) from exc

    @app.post("/api/dashboard/jobs/{job_id}/delete-request")
    def dashboard_job_delete_request(job_id: str, request: JobDeleteRequest | None = Body(None)):
        payload = database.mark_capture_job_for_deletion(
            job_id=job_id,
            actor="dashboard",
            reason=(request.reason if request else "operator_cleanup"),
        )
        if not payload.get("delete_requested"):
            status_code = 404 if payload.get("status") == "not_found" else 409
            raise HTTPException(status_code=status_code, detail=payload.get("error") or "Corpus job cannot be marked for deletion.")
        return payload

    @app.post("/api/dashboard/jobs/{job_id}/file-actions")
    def dashboard_job_file_action(job_id: str, request: JobFileActionRequest = Body(...)):
        if request.action not in {"open", "reveal"}:
            raise HTTPException(status_code=400, detail="job file action must be open or reveal")
        return host_agent_job_file_action(job_id=job_id, action=request.action)

    @app.get("/api/dashboard/retrieval-stats")
    def dashboard_retrieval_stats():
        return collect_retrieval_payload()

    @app.post("/api/search")
    def search(request: SearchRequest = Body(...)):
        kwargs = _service_retrieval_kwargs(
            limit=request.limit,
            cwd=request.cwd,
            root_name=request.root_name,
            scope_mode=request.scope_mode,
            filters=_filters_from_body(request.filters),
        )
        return service.search(request.query, **kwargs)

    @app.get("/api/search")
    def search_get(
        query: str,
        limit: int = 5,
        cwd: str | None = None,
        root_name: str | None = None,
        scope_mode: str = "local_first",
        kind: list[str] | None = Query(None),
        current_only: bool | None = None,
        lifecycle_state: list[str] | None = Query(None),
        include_suppressed: bool | None = None,
        file_kind: list[str] | None = Query(None),
        language: list[str] | None = Query(None),
        symbol_kind: list[str] | None = Query(None),
        relationship: list[str] | None = Query(None),
        path_glob: list[str] | None = Query(None),
        include_generated: bool | None = None,
    ):
        filters = _filters_from_query(
            kind=kind,
            current_only=current_only,
            lifecycle_state=lifecycle_state,
            include_suppressed=include_suppressed,
            file_kind=file_kind,
            language=language,
            symbol_kind=symbol_kind,
            relationship=relationship,
            path_glob=path_glob,
            include_generated=include_generated,
        )
        kwargs = _service_retrieval_kwargs(limit=limit, cwd=cwd, root_name=root_name, scope_mode=scope_mode, filters=filters)
        return service.search(query, **kwargs)

    @app.post("/api/brief")
    def brief(request: BriefRequest = Body(...)):
        kwargs = _service_retrieval_kwargs(
            token_budget=request.token_budget,
            cwd=request.cwd,
            root_name=request.root_name,
            scope_mode=request.scope_mode,
            filters=_filters_from_body(request.filters),
        )
        return {"brief": service.brief(request.query, **kwargs)}

    @app.get("/api/brief")
    def brief_get(
        query: str,
        token_budget: int | None = None,
        cwd: str | None = None,
        root_name: str | None = None,
        scope_mode: str = "local_first",
        kind: list[str] | None = Query(None),
        current_only: bool | None = None,
        lifecycle_state: list[str] | None = Query(None),
        include_suppressed: bool | None = None,
    ):
        filters = _filters_from_query(
            kind=kind,
            current_only=current_only,
            lifecycle_state=lifecycle_state,
            include_suppressed=include_suppressed,
        )
        kwargs = _service_retrieval_kwargs(token_budget=token_budget, cwd=cwd, root_name=root_name, scope_mode=scope_mode, filters=filters)
        return {"brief": service.brief(query, **kwargs)}

    @app.post("/api/explain")
    def explain(request: ExplainRequest = Body(...)):
        kwargs = _service_retrieval_kwargs(
            limit=request.limit,
            token_budget=request.token_budget,
            cwd=request.cwd,
            root_name=request.root_name,
            scope_mode=request.scope_mode,
            filters=_filters_from_body(request.filters),
        )
        return service.explain(request.query, **kwargs)

    @app.get("/api/explain")
    def explain_get(
        query: str,
        limit: int = 5,
        token_budget: int | None = None,
        cwd: str | None = None,
        root_name: str | None = None,
        scope_mode: str = "local_first",
        kind: list[str] | None = Query(None),
        current_only: bool | None = None,
        lifecycle_state: list[str] | None = Query(None),
        include_suppressed: bool | None = None,
    ):
        filters = _filters_from_query(
            kind=kind,
            current_only=current_only,
            lifecycle_state=lifecycle_state,
            include_suppressed=include_suppressed,
        )
        kwargs = _service_retrieval_kwargs(
            limit=limit,
            token_budget=token_budget,
            cwd=cwd,
            root_name=root_name,
            scope_mode=scope_mode,
            filters=filters,
        )
        return service.explain(query, **kwargs)

    @app.post("/api/retrieval/benchmarks/run")
    def retrieval_benchmark_run(request: RetrievalBenchmarkRunRequest = Body(...)):
        return service.run_retrieval_benchmark(
            suite=request.suite,
            label=request.label,
            compare_label=request.compare_label,
            limit_per_query=request.limit_per_query,
            token_budget=request.token_budget,
            persist=request.persist,
        )

    @app.get("/api/retrieval/benchmarks")
    def retrieval_benchmark_history(
        suite: str | None = None,
        label: str | None = None,
        limit: int = 20,
    ):
        return service.retrieval_benchmark_history(
            suite=suite,
            label=label,
            limit=limit,
        )

    @app.get("/api/automation/status")
    def automation_status():
        return service.operator_automation_status()

    @app.post("/api/automation/run")
    def automation_run(request: AutomationRunRequest = Body(...)):
        return service.run_operator_automation(mode=request.mode, actor="api", trigger="manual", limit=request.limit, dry_run=request.dry_run)

    @app.get("/api/automation/actions")
    def automation_actions(status: str = "all", run_id: str | None = None, action: str | None = None, limit: int = 50):
        return service.operator_automation_actions(status=status, run_id=run_id, action=action, limit=limit)

    @app.get("/api/governance/runs")
    def governance_runs(limit: int = 20):
        return service.governance_runs(limit=limit)

    @app.post("/api/governance/run")
    def governance_run(request: GovernanceRunRequest = Body(...)):
        return service.run_governance(mode=request.mode, actor="api", limit=request.limit)

    @app.get("/api/governance/actions")
    def governance_actions(status: str = "proposed", limit: int = 50):
        return service.governance_actions(status=status, limit=limit)

    @app.post("/api/governance/actions/{action_id}/apply")
    def governance_apply(action_id: str, request: GovernanceActionRequest = Body(...)):
        try:
            return service.governance_apply(action_id, rationale=request.rationale, confirm=request.confirm, actor="api")
        except LookupError as exc:
            raise FluxApiError(
                code="governance.action_not_found",
                message=str(exc),
                status_code=404,
                component="review",
                retryable=False,
                user_action="Refresh governance actions before retrying.",
                target={"type": "memory_governance_action", "id": action_id},
            ) from exc
        except ValueError as exc:
            raise FluxApiError(
                code="governance.apply_blocked",
                message=str(exc),
                status_code=400,
                component="review",
                retryable=False,
                user_action="Confirm the action, include rationale, and ensure the governance shadow gate is ready.",
                target={"type": "memory_governance_action", "id": action_id},
            ) from exc

    @app.post("/api/governance/actions/{action_id}/recover")
    def governance_recover(action_id: str, request: GovernanceActionRequest = Body(...)):
        try:
            return service.governance_recover(action_id, rationale=request.rationale, confirm=request.confirm, actor="api")
        except LookupError as exc:
            raise FluxApiError(
                code="governance.action_not_found",
                message=str(exc),
                status_code=404,
                component="review",
                retryable=False,
                user_action="Refresh governance actions before retrying.",
                target={"type": "memory_governance_action", "id": action_id},
            ) from exc
        except ValueError as exc:
            raise FluxApiError(
                code="governance.recovery_blocked",
                message=str(exc),
                status_code=400,
                component="review",
                retryable=False,
                user_action="Recover only applied governance actions with confirmation and rationale.",
                target={"type": "memory_governance_action", "id": action_id},
            ) from exc

    @app.get("/api/governance/digest")
    def governance_digest():
        return service.governance_digest()

    @app.get("/api/governance/policy")
    def governance_policy():
        return service.governance_policy()

    @app.post("/api/claims")
    def claim_upsert(request: ClaimRequest = Body(...)):
        return service.upsert_claim(
            subject_type=request.subject_type,
            subject_name=request.subject,
            predicate=request.predicate,
            object_text=request.object_text,
            confidence=request.confidence,
            episode_id=request.episode_id,
            metadata=request.metadata,
        )

    @app.get("/api/claims")
    def claim_list(
        review: str = "all",
        state: str | None = None,
        q: str | None = None,
        limit: int = 50,
    ):
        try:
            return service.list_claims(review=review, state=state, q=q, limit=limit)
        except ValueError as exc:
            raise FluxApiError(
                code="claim.review_filter_invalid",
                message=str(exc),
                status_code=400,
                component="retrieval",
                retryable=False,
                user_action="Use review=all, review=needs_review, or review=current.",
                target={"type": "claim_review", "id": review},
            ) from exc

    @app.get("/api/claims/{claim_id}")
    def claim_get(claim_id: str):
        try:
            return service.get_claim(claim_id)
        except LookupError as exc:
            raise FluxApiError(
                code="claim.not_found",
                message=str(exc),
                status_code=404,
                component="retrieval",
                retryable=False,
                user_action="Refresh claim results or create the claim before reading it.",
                target={"type": "claim", "id": claim_id},
            ) from exc

    @app.post("/api/claims/{claim_id}/transitions")
    def claim_transition(claim_id: str, request: ClaimTransitionRequest = Body(...)):
        try:
            return service.transition_claim(
                claim_id=claim_id,
                transition=request.transition,
                related_claim_id=request.related_claim_id,
                reason=request.reason,
                confidence_delta=request.confidence_delta,
                actor="api",
            )
        except LookupError as exc:
            raise FluxApiError(
                code="claim.not_found",
                message=str(exc),
                status_code=404,
                component="retrieval",
                retryable=False,
                user_action="Refresh claim results or create the claim before transitioning it.",
                target={"type": "claim", "id": claim_id},
            ) from exc
        except ValueError as exc:
            raise FluxApiError(
                code="claim.transition_invalid",
                message=str(exc),
                status_code=400,
                component="retrieval",
                retryable=False,
                user_action="Use a supported claim lifecycle transition.",
                target={"type": "claim", "id": claim_id},
            ) from exc

    @app.get("/api/graph/traverse")
    def graph_traverse(
        entity_id: str,
        relation_type: list[str] | None = Query(None),
        max_depth: int = 2,
        direction: str = "out",
        limit: int = 100,
    ):
        return service.traverse_graph(
            entity_id=entity_id,
            relation_types=relation_type,
            max_depth=max_depth,
            direction=direction,
            limit=limit,
        )

    @app.get("/api/capture/review")
    def capture_review(status: str = "pending_review", limit: int = 50):
        return service.list_capture_review_jobs(status=status, limit=limit)

    @app.post("/api/capture/review/ingest")
    def capture_review_ingest(request: CaptureReviewIngestRequest = Body(...)):
        try:
            return service.ingest_capture_review_jobs(
                job_id=request.job_id,
                limit=request.limit,
                dry_run=request.dry_run,
                actor="api",
            )
        except LookupError as exc:
            raise FluxApiError(
                code="capture_review.ingest_not_found",
                message=str(exc),
                status_code=404,
                component="review",
                retryable=False,
                user_action="Refresh approved capture review jobs before retrying ingestion.",
                target={"type": "capture_review_job", "id": request.job_id},
            ) from exc
        except ValueError as exc:
            raise FluxApiError(
                code="capture_review.ingest_invalid",
                message=str(exc),
                status_code=400,
                component="review",
                retryable=False,
                user_action="Use a positive limit and an approved capture review job id when scoping ingestion.",
                target={"type": "capture_review_job", "id": request.job_id},
            ) from exc

    @app.post("/api/capture/review/{job_id}/decision")
    def capture_review_decision(job_id: str, request: CaptureReviewDecisionRequest = Body(...)):
        try:
            return service.review_capture_job(
                job_id=job_id,
                decision=request.decision,
                rationale=request.rationale,
                actor="api",
            )
        except ValueError as exc:
            raise FluxApiError(
                code="capture_review.decision_invalid",
                message=str(exc),
                status_code=400,
                component="review",
                retryable=False,
                user_action="Use decision approve or reject and include a rationale.",
                target={"type": "capture_review_job", "id": job_id},
            ) from exc
        except LookupError as exc:
            raise FluxApiError(
                code="capture_review.job_not_found",
                message=str(exc),
                status_code=404,
                component="review",
                retryable=False,
                user_action="Refresh the capture review queue before retrying.",
                target={"type": "capture_review_job", "id": job_id},
            ) from exc
        except RuntimeError as exc:
            raise FluxApiError(
                code="capture_review.job_conflict",
                message=str(exc),
                status_code=409,
                component="review",
                retryable=False,
                user_action="Refresh the capture review queue; this job is no longer pending review.",
                target={"type": "capture_review_job", "id": job_id},
            ) from exc

    @app.get("/api/retention/policies")
    def retention_policies():
        return service.list_retention_policies()

    @app.put("/api/retention/policies/{memory_class}")
    def retention_policy_update(memory_class: str, request: RetentionPolicyRequest = Body(...)):
        try:
            return service.set_retention_policy(
                memory_class=memory_class,
                half_life_days=request.half_life_days,
                min_confidence=request.min_confidence,
                action=request.action,
                actor="api",
                reason=request.reason,
            )
        except ValueError as exc:
            raise FluxApiError(
                code="retention.policy_invalid",
                message=str(exc),
                status_code=400,
                component="review",
                retryable=False,
                user_action="Use memory_class claim, episode, or corpus and action review, deprioritize, or retire.",
                target={"type": "retention_policy", "id": memory_class},
            ) from exc

    @app.get("/api/retention/quality")
    def retention_quality(limit: int = 25):
        return service.retention_quality_report(limit=limit)

    @app.post("/api/semantic-duplicates/refresh")
    def semantic_duplicates_refresh(request: SemanticDuplicateRefreshRequest = Body(...)):
        try:
            return service.refresh_semantic_duplicate_clusters(
                memory_class=request.memory_class,
                root_name=request.root_name,
                threshold=request.threshold,
                limit=request.limit,
            )
        except ValueError as exc:
            raise FluxApiError(
                code="semantic_duplicates.invalid_request",
                message=str(exc),
                status_code=400,
                component="retrieval",
                retryable=False,
                user_action="Use memory_class all, corpus, episode, or claim and a threshold between 0.0 and 1.0.",
                target={"type": "semantic_duplicates", "id": request.memory_class},
            ) from exc

    @app.get("/api/semantic-duplicates")
    def semantic_duplicates_list(
        memory_class: str | None = None,
        root_name: str | None = None,
        limit: int = 50,
    ):
        try:
            return service.list_semantic_duplicate_clusters(
                memory_class=memory_class,
                root_name=root_name,
                limit=limit,
            )
        except ValueError as exc:
            raise FluxApiError(
                code="semantic_duplicates.invalid_request",
                message=str(exc),
                status_code=400,
                component="retrieval",
                retryable=False,
                user_action="Use memory_class corpus, episode, or claim.",
                target={"type": "semantic_duplicates", "id": memory_class or "all"},
            ) from exc

    @app.get("/api/embeddings/status")
    def embeddings_status(root_name: str | None = None):
        return service.embedding_status(root_name=root_name)

    @app.post("/api/embeddings/enqueue")
    def embeddings_enqueue(request: EmbeddingRefreshRequest = Body(...)):
        return service.enqueue_embedding_jobs(
            owner_class=request.owner_class,
            root_name=request.root_name,
            stale_only=request.stale_only,
            limit=request.limit,
        )

    @app.post("/api/embeddings/backfill")
    def embeddings_backfill(request: EmbeddingRefreshRequest = Body(...)):
        return service.refresh_embeddings(
            owner_class=request.owner_class,
            root_name=request.root_name,
            stale_only=request.stale_only,
            limit=request.limit,
        )

    @app.get("/api/corpus/assets")
    def corpus_assets(root_name: str | None = None, path: str | None = None, limit: int = 50):
        from . import database

        return {"assets": database.list_source_assets(root_name=root_name, path=path, limit=limit)}

    @app.get("/api/corpus/assets/{asset_id}")
    def corpus_asset(asset_id: str):
        from . import database

        asset = database.get_source_asset(asset_id)
        if asset is None:
            raise FluxApiError(
                code="corpus.asset_not_found",
                message="source asset not found",
                status_code=404,
                component="corpus",
                retryable=False,
                user_action="Refresh the corpus view or sync the watched path again.",
                target={"type": "asset", "id": asset_id},
                links=[{"label": "Corpus", "tab": "corpus"}],
            )
        return asset

    @app.get("/api/corpus/chunks/{chunk_id}")
    def corpus_chunk(chunk_id: str):
        from . import database

        chunk = database.get_asset_chunk(chunk_id)
        if chunk is None:
            raise FluxApiError(
                code="corpus.chunk_not_found",
                message="asset chunk not found",
                status_code=404,
                component="corpus",
                retryable=False,
                user_action="Refresh search results and open the result again.",
                target={"type": "chunk", "id": chunk_id},
                links=[{"label": "Retrieval", "tab": "retrieval"}],
            )
        return chunk

    @app.get("/api/results/{kind}/{result_id}")
    def result_detail(kind: str, result_id: str):
        from .result_details import result_detail as build_result_detail

        try:
            return build_result_detail(kind, result_id)
        except LookupError as exc:
            raise FluxApiError(
                code="result.not_found",
                message=str(exc),
                status_code=404,
                component="retrieval",
                retryable=False,
                user_action="Refresh search results and open the result again.",
                target={"type": kind, "id": result_id},
                links=[{"label": "Retrieval", "tab": "retrieval"}],
            ) from exc
        except ValueError as exc:
            raise FluxApiError(
                code="result.kind_invalid",
                message=str(exc),
                status_code=400,
                component="retrieval",
                retryable=False,
                user_action="Use a supported result detail kind.",
                target={"type": kind, "id": result_id},
                links=[{"label": "Retrieval", "tab": "retrieval"}],
            ) from exc

    @app.post("/api/corpus/assets/{asset_id}/actions")
    def corpus_asset_action(asset_id: str, request: FileActionRequest = Body(...)):
        if request.action not in {"open", "reveal"}:
            return {
                "state": "not_allowed",
                "asset_id": asset_id,
                "action": request.action,
                "reason": "unsupported_action",
                "message": "file action must be open or reveal",
            }
        return host_agent_file_action(asset_id=asset_id, action=request.action)

    @app.post("/api/remember")
    def remember(request: RememberRequest = Body(...)):
        return service.remember(request.title, request.body, cwd=request.cwd, root_name=request.root_name).__dict__

    @app.get("/api/audit")
    def audit(limit: int = 50):
        return service.audit(limit=limit)

    @app.post("/api/forget")
    def forget(request: ForgetRequest = Body(...)):
        return service.forget(request.memory_id, reason=request.reason)

    @app.get("/api/crawl/status")
    def crawl_status():
        return collect_crawl_payload()

    @app.post("/api/crawl/sync")
    def crawl_sync(request: CrawlSyncRequest = Body(...)):
        if _should_proxy_crawl_sync(request.root_name, request.path):
            return host_agent_sync(root_name=request.root_name, path=request.path, dry_run=request.dry_run)
        return service.sync_corpus(root_name=request.root_name, path=request.path, dry_run=request.dry_run)

    @app.post("/api/crawl/roots")
    def crawl_root_add(request: CrawlRootRequest = Body(...)):
        from . import database
        from .settings import SettingsService

        name = request.name.strip()
        if not name:
            raise _crawl_root_error("root name is required", root_name=name, root_path=request.root_path)
        root_path_text = request.root_path.strip()
        validation = _validate_root_path(root_path_text)
        if validation["status"] != "ok":
            raise _crawl_root_error(str(validation["message"]), root_name=name, root_path=root_path_text)

        settings = SettingsService()
        max_inline_bytes = request.max_inline_bytes
        if max_inline_bytes is None:
            max_inline_bytes = int(settings.resolve("crawler.max_inline_bytes").raw_value)
        heavy_threshold_bytes = request.heavy_threshold_bytes
        if heavy_threshold_bytes is None:
            heavy_threshold_bytes = int(settings.resolve("crawler.heavy_threshold_bytes").raw_value)

        if max_inline_bytes <= 0 or heavy_threshold_bytes <= 0:
            raise _crawl_root_error("size thresholds must be positive", root_name=name, root_path=root_path_text)

        metadata = {
            "source": "dashboard",
            "host_access": "host_agent" if validation.get("host_agent") else "direct",
            "host_validation": validation,
        }
        if request.strict_indexing:
            metadata["strict_indexing"] = True
        root = database.add_monitored_root(
            name=name,
            root_path=root_path_text,
            enabled=request.enabled,
            recursive=request.recursive,
            watch_enabled=request.watch_enabled,
            trust_rank=request.trust_rank,
            include_globs=[item.strip() for item in request.include_globs if item.strip()],
            exclude_globs=[item.strip() for item in request.exclude_globs if item.strip()],
            glob_mode=request.glob_mode,
            max_inline_bytes=max_inline_bytes,
            heavy_threshold_bytes=heavy_threshold_bytes,
            metadata=metadata,
        )
        payload: dict[str, object] = {"root": root}
        if request.initial_crawl:
            if validation.get("host_agent"):
                payload["sync"] = host_agent_sync(root_name=root["name"], dry_run=request.dry_run)
            else:
                payload["sync"] = service.sync_corpus(root_name=root["name"], dry_run=request.dry_run)
        return payload

    @app.patch("/api/crawl/roots/{root_id}")
    def crawl_root_update(root_id: str, request: CrawlRootUpdateRequest = Body(...)):
        from . import database
        from .settings import SettingsService

        name = request.name.strip()
        if not name:
            raise _crawl_root_error("root name is required", root_name=name, root_path=request.root_path)
        root_path_text = request.root_path.strip()
        validation = _validate_root_path(root_path_text)
        if validation["status"] != "ok":
            raise _crawl_root_error(str(validation["message"]), root_name=name, root_path=root_path_text)

        settings = SettingsService()
        max_inline_bytes = request.max_inline_bytes
        if max_inline_bytes is None:
            max_inline_bytes = int(settings.resolve("crawler.max_inline_bytes").raw_value)
        heavy_threshold_bytes = request.heavy_threshold_bytes
        if heavy_threshold_bytes is None:
            heavy_threshold_bytes = int(settings.resolve("crawler.heavy_threshold_bytes").raw_value)

        if max_inline_bytes <= 0 or heavy_threshold_bytes <= 0:
            raise _crawl_root_error("size thresholds must be positive", root_name=name, root_path=root_path_text)

        try:
            existing = database.get_monitored_root_by_identifier(root_id)
            metadata = dict((existing or {}).get("metadata") or {})
            metadata.update(
                {
                    "source": "dashboard",
                    "host_access": "host_agent" if validation.get("host_agent") else "direct",
                    "host_validation": validation,
                }
            )
            if request.strict_indexing is not None:
                metadata["strict_indexing"] = bool(request.strict_indexing)
            updated = database.update_monitored_root(
                root_id=root_id,
                name=name,
                root_path=root_path_text,
                enabled=request.enabled,
                recursive=request.recursive,
                watch_enabled=request.watch_enabled,
                trust_rank=request.trust_rank,
                include_globs=[item.strip() for item in request.include_globs if item.strip()],
                exclude_globs=[item.strip() for item in request.exclude_globs if item.strip()],
                glob_mode=request.glob_mode,
                max_inline_bytes=max_inline_bytes,
                heavy_threshold_bytes=heavy_threshold_bytes,
                metadata=metadata,
            )
            service.reconcile_unseen_assets_for_root(root_name=updated["name"], reason="root_policy_update")
            return updated
        except ValueError as exc:
            raise FluxApiError(
                code="crawl.root_not_found",
                message=str(exc),
                status_code=404,
                component="crawler",
                retryable=False,
                user_action="Refresh the Corpus tab and choose an existing watched path.",
                target={"type": "root", "id": root_id},
                links=[{"label": "Corpus", "tab": "corpus"}],
            ) from exc

    @app.delete("/api/crawl/roots/{root_id}")
    def crawl_root_delete(root_id: str, purge_index: bool = True):
        from . import database

        try:
            return database.delete_monitored_root(root_id=root_id, purge_index=purge_index, actor="dashboard")
        except ValueError as exc:
            raise FluxApiError(
                code="crawl.root_not_found",
                message=str(exc),
                status_code=400,
                component="crawler",
                retryable=False,
                user_action="Refresh the Corpus tab and choose an existing watched path.",
                target={"type": "root", "id": root_id},
                links=[{"label": "Corpus", "tab": "corpus"}],
            ) from exc

    @app.get("/api/crawl/jobs")
    def crawl_jobs(
        limit: int = 50,
        offset: int = 0,
        status: list[str] | None = Query(None),
        root_name: list[str] | None = Query(None),
        job_type: list[str] | None = Query(None),
        updated_from: str | None = None,
        updated_to: str | None = None,
    ):
        return collect_jobs_payload(
            limit=limit,
            offset=offset,
            status=status,
            root_name=root_name,
            job_type=job_type,
            updated_from=updated_from,
            updated_to=updated_to,
        )

    @app.post("/api/crawl/jobs/{job_id}/cancel")
    def crawl_job_cancel(job_id: str):
        from . import database

        payload = database.cancel_corpus_job(job_id=job_id, actor="dashboard")
        if not payload.get("cancelled"):
            status_code = 404 if payload.get("status") == "not_found" else 409
            raise HTTPException(status_code=status_code, detail=payload.get("error") or "Corpus job cannot be cancelled.")
        return payload

    @app.post("/api/crawl/backfill")
    def crawl_backfill(request: CrawlBackfillRequest = Body(...)):
        kwargs: dict[str, object] = {"kind": request.kind, "limit": request.limit, "workers": request.workers}
        if request.family is not None:
            kwargs["family"] = request.family
        if request.root_name is not None:
            kwargs["root_name"] = request.root_name
            if _should_proxy_host_root(request.root_name):
                return host_agent_backfill(**kwargs)
        return service.run_corpus_backfill(**kwargs)

    @app.post("/api/crawl/watch")
    def crawl_watch(request: WatchRequest = Body(...)):
        from . import database

        return database.set_watch_enabled(root_name=request.root_name, enabled=request.enabled)

    @app.post("/api/crawl/watch/probe")
    def crawl_watch_probe(request: WatchProbeRequest = Body(default=WatchProbeRequest())):
        return service.watch_probe(timeout_seconds=request.timeout_seconds)

    @app.get("/api/crawl/watch/events")
    def crawl_watch_events(limit: int = 50):
        return service.watch_events(limit=limit)

    @app.get("/api/crawl/workers")
    def crawl_workers(family: str = "all"):
        return service.worker_status(family=family)

    @app.get("/api/host/status")
    def host_status():
        return host_agent_status()

    @app.post("/api/host/browse-folder")
    def host_browse_folder():
        return host_agent_browse_folder()

    @app.post("/api/host/validate-path")
    def host_validate_path(request: dict[str, object] = Body(...)):
        path = str(request.get("path") or "")
        return host_agent_validate_path(path)

    @app.get("/api/settings")
    def settings_list():
        from .settings import SettingsService

        return SettingsService().public_list()

    @app.get("/api/settings/{key}")
    def settings_get(key: str):
        from .settings import SettingsService

        return SettingsService().resolve(key).to_public_dict()

    @app.put("/api/settings/{key}")
    def settings_put(key: str, request: SettingUpdateRequest = Body(...)):
        from .settings import SettingsService

        return SettingsService().set(
            key,
            request.value,
            actor="dashboard",
            reason=request.reason,
            confirmed=request.confirmed,
        )

    @app.post("/api/settings/apply")
    def settings_apply(request: SettingsApplyRequest = Body(...)):
        from .settings import SettingsService

        return SettingsService().apply(component=request.component, actor="dashboard")

    @app.post("/api/settings/{key}/reset")
    def settings_reset(key: str):
        from .settings import SettingsService

        return SettingsService().reset(key, actor="dashboard")

    @app.get("/api/mail/status")
    def mail_status():
        from .mail_ingestion import mail_status

        return mail_status()

    @app.get("/api/mail/profiles")
    def mail_profiles():
        from . import database

        return database.list_mail_profiles()

    @app.post("/api/mail/profiles")
    def mail_profile_add(request: MailProfileRequest = Body(...)):
        from .mail_ingestion import add_mail_profile

        return add_mail_profile(
            name=request.name,
            source_type=request.source_type,
            account=request.account,
            server=request.server,
            folder_paths=request.folder_paths,
            spool_path=request.spool_path,
            post_process_policy=request.post_process_policy,
            processed_folder=request.processed_folder,
            trash_folder=request.trash_folder,
            destructive_post_process_confirmed=request.destructive_post_process_confirmed,
            sync_enabled=request.sync_enabled,
            sync_interval_seconds=request.sync_interval_seconds,
            sync_window_days=request.sync_window_days,
            max_messages_per_run=request.max_messages_per_run,
            include_subfolders=request.include_subfolders,
            outlook_incremental_basis=request.outlook_incremental_basis,
        )

    @app.post("/api/mail/profiles/{profile_name}/post-process/dry-run")
    def mail_post_process_dry_run(profile_name: str, request: MailPostProcessDryRunRequest = Body(default=MailPostProcessDryRunRequest())):
        from .mail_ingestion import dry_run_mail_post_process

        try:
            return dry_run_mail_post_process(profile_name=profile_name, limit=request.limit)
        except ValueError as exc:
            raise FluxApiError(
                code="mail.profile_not_found",
                message=str(exc),
                status_code=404,
                component="mail",
                retryable=False,
                user_action="Refresh the Mail tab and choose an existing profile.",
                target={"type": "mail_profile", "id": profile_name},
                links=[{"label": "Mail", "tab": "mail", "profile": profile_name}],
            ) from exc

    @app.get("/api/mail/post-process/events")
    def mail_post_process_events(profile_name: str | None = None, limit: int = 20):
        from . import database

        return {"events": database.list_mail_post_process_events(profile_name=profile_name, limit=limit)}

    @app.put("/api/mail/profiles/{profile_name}/oauth-client-config")
    def mail_profile_oauth_client_config(profile_name: str, request: GmailOAuthClientConfigRequest = Body(...)):
        from .mail_ingestion import update_mail_profile_oauth_client_config_path

        try:
            return update_mail_profile_oauth_client_config_path(
                profile_name=profile_name,
                client_config_path=request.client_config_path,
            )
        except ValueError as exc:
            raise FluxApiError(
                code="mail.profile_not_found",
                message=str(exc),
                status_code=404,
                component="mail",
                retryable=False,
                user_action="Refresh the Mail tab and choose an existing profile.",
                target={"type": "mail_profile", "id": profile_name},
                links=[{"label": "Mail", "tab": "mail", "profile": profile_name}],
            ) from exc

    @app.post("/api/mail/sync")
    def mail_sync(request: MailSyncRequest = Body(...)):
        from .mail_ingestion import sync_mail_profile

        return sync_mail_profile(profile_name=request.profile_name)

    @app.post("/api/mail/watch")
    def mail_watch(_: MailSyncRequest = Body(...)):
        return {"status": "watch_loop_runs_from_cli", "command": "flux-kb mail watch run"}

    @app.post("/api/mail/oauth/gmail/start")
    def mail_oauth_gmail_start(request: GmailOAuthStartRequest = Body(...)):
        from .mail_oauth import start_gmail_oauth

        try:
            payload = start_gmail_oauth(
                profile_name=request.profile_name,
                client_config_path=request.client_config_path,
                redirect_uri=request.redirect_uri,
            )
            if payload.get("authorization_url") and not payload.get("auth_url"):
                payload["auth_url"] = payload["authorization_url"]
            return payload
        except FileNotFoundError as exc:
            return {
                "profile_name": request.profile_name,
                "provider": "gmail",
                "status": "blocked_config_missing",
                "message": str(exc),
            }
        except (JSONDecodeError, ValueError) as exc:
            return {
                "profile_name": request.profile_name,
                "provider": "gmail",
                "status": "blocked_config_invalid",
                "message": str(exc),
            }

    @app.get("/api/mail/oauth/gmail/callback")
    def mail_oauth_gmail_callback(state: str, code: str | None = None, error: str | None = None):
        return _mail_oauth_callback_payload(state=state, code=code, error=error)

    @app.get("/api/mail/oauth/status")
    def mail_oauth_status(profile_name: str | None = None):
        from .mail_oauth import oauth_status

        return oauth_status(profile_name=profile_name)

    @app.get("/api/outlook-host/status")
    def outlook_host_status():
        from .outlook_host import status

        return status()

    @app.post("/api/outlook-host/request-sync")
    def outlook_host_request_sync(request: OutlookHostSyncRequest = Body(...)):
        from .outlook_host import request_sync

        return request_sync(request.profile_name, actor="dashboard")

    @app.post("/api/outlook-host/requests/{request_id}/cancel")
    def outlook_host_cancel_request(request_id: str):
        from .outlook_host import cancel_request

        payload = cancel_request(request_id, actor="dashboard")
        if not payload.get("cancelled"):
            status_code = 404 if payload.get("status") == "not_found" else 409
            raise HTTPException(status_code=status_code, detail=payload.get("error") or "Outlook sync request cannot be cancelled.")
        return payload

    @app.post("/api/outlook-host/profiles/{name}/enable")
    def outlook_host_profile_enable(name: str):
        from .outlook_host import set_profile_enabled

        return set_profile_enabled(name, enabled=True)

    @app.post("/api/outlook-host/profiles/{name}/disable")
    def outlook_host_profile_disable(name: str):
        from .outlook_host import set_profile_enabled

        return set_profile_enabled(name, enabled=False)

    return app


def host_agent_status() -> dict:
    return remote_status()


def host_agent_browse_folder() -> dict:
    return remote_browse_folder()


def host_agent_validate_path(path: str) -> dict:
    if path_requires_host_agent(path):
        return remote_validate_path(path)
    return validate_host_path(path)


def host_agent_sync(*, root_name: str | None = None, path: str | None = None, dry_run: bool = False) -> dict:
    return remote_sync(root_name=root_name, path=path, dry_run=dry_run)


def host_agent_backfill(
    *,
    kind: str = "all",
    limit: int | None = None,
    workers: int | None = None,
    root_name: str | None = None,
    family: str | None = None,
) -> dict:
    return remote_backfill(kind=kind, limit=limit, workers=workers, root_name=root_name, family=family)


def host_agent_benchmark(
    *,
    fixture: str = "all",
    files: int = 10,
    mode: str = "scan",
    passes: int = 1,
    label: str | None = None,
    compare_label: str | None = None,
    workers: int = 1,
    family: str = "all",
    scope: str = "synthetic",
    root_name: str | None = None,
    path: str | None = None,
    max_files: int | None = None,
    deployment_label: str | None = None,
    scenario: str = "standard",
    include_model_probe: bool = False,
) -> dict:
    return remote_benchmark(
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


def _run_indexer_reliability_with_benchmark_proxy(service: KnowledgeService, request: Any) -> dict[str, Any]:
    passes = max(1, int(getattr(request, "passes", 2) or 2))
    max_files = max(1, int(getattr(request, "max_files", 1000) or 1000))
    scope = getattr(request, "scope", "synthetic")
    root_name = getattr(request, "root_name", None)
    path = getattr(request, "path", None)
    label = getattr(request, "label", None)
    deployment_label = getattr(request, "deployment_label", None)
    compare_label = getattr(request, "compare_label", None)
    evidence_level = str(getattr(request, "evidence_level", "standard") or "standard").strip().lower()
    include_cache_readiness = bool(getattr(request, "include_cache_readiness", False)) or evidence_level == "full"
    include_tuning = bool(getattr(request, "include_tuning", True)) or evidence_level == "full"
    service.run_benchmark(
        fixture="all",
        files=10,
        mode="all",
        passes=passes,
        label=label,
        compare_label=compare_label,
        deployment_label=deployment_label,
        scenario="reliability",
    )
    host_agent_benchmark(
        fixture="all",
        files=10,
        mode="scan",
        passes=passes,
        label=label,
        compare_label=compare_label,
        scope=scope,
        root_name=root_name,
        path=path,
        max_files=max_files,
        deployment_label=deployment_label,
        scenario="host_cloud",
    )
    if include_cache_readiness:
        service.run_benchmark(
            fixture="image-heavy",
            files=10,
            mode="model",
            passes=1,
            label=label,
            compare_label=compare_label,
            deployment_label=deployment_label,
            scenario="cache_readiness",
        )
    if include_tuning:
        host_agent_benchmark(
            fixture="all",
            files=10,
            mode="scan",
            passes=passes,
            label=label,
            compare_label=compare_label,
            scope=scope,
            root_name=root_name,
            path=path,
            max_files=max_files,
            deployment_label=deployment_label,
            scenario="tuning",
        )
    return service.indexer_reliability_status(
        root_name=root_name,
        path=path if str(scope or "").strip().lower().replace("-", "_") == "path" else None,
        label=label,
        deployment_label=deployment_label,
        compare_label=compare_label,
    )


def host_agent_file_action(*, asset_id: str, action: str) -> dict:
    return remote_file_action(asset_id=asset_id, action=action)


def host_agent_job_file_action(*, job_id: str, action: str) -> dict:
    return remote_job_file_action(job_id=job_id, action=action)


def _crawl_root_error(message: str, *, root_name: str | None = None, root_path: str | None = None) -> FluxApiError:
    return FluxApiError(
        code="crawl.root_invalid",
        message=message,
        status_code=400,
        component="crawler",
        retryable=False,
        user_action="Choose an existing directory and valid crawl thresholds, then save the watched path again.",
        technical_detail=message,
        target={"type": "root", "id": root_name or root_path or "new"},
        links=[{"label": "Corpus", "tab": "corpus"}],
    )


def _validate_root_path(root_path_text: str) -> dict:
    validation = host_agent_validate_path(root_path_text)
    if validation.get("status") == "ok":
        validation["host_agent"] = path_requires_host_agent(root_path_text)
        return validation
    if validation.get("status") == "host_agent_offline":
        validation["message"] = (
            "host agent offline; start `flux-kb host-agent run` to add local host paths from Docker"
        )
    return validation


def _mail_oauth_callback_payload(*, state: str | None, code: str | None, error: str | None) -> dict:
    if error:
        return {"status": "error", "error": error, "state": state}
    if not state:
        return {"status": "error", "error": "missing OAuth state", "state": state}
    if not code:
        return {"status": "error", "error": "missing authorization code", "state": state}
    from .mail_oauth import complete_gmail_oauth

    try:
        return complete_gmail_oauth(state=state, code=code)
    except Exception as exc:  # pragma: no cover - provider/database errors vary by environment.
        return {"status": "error", "error": str(exc), "state": state}


def _mail_oauth_callback_html(payload: dict) -> str:
    configured = payload.get("status") == "configured"
    title = "Gmail OAuth configured" if configured else "Gmail OAuth did not complete"
    profile = payload.get("profile_name") or "selected profile"
    detail = f"{profile} is ready for Gmail IMAP sync." if configured else str(payload.get("error") or payload.get("status") or "unknown error")
    return f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{escape(title)}</title>
  <style>
    body {{ margin: 0; min-height: 100vh; display: grid; place-items: center; font-family: Arial, sans-serif; background: #f3f7fb; color: #172033; }}
    main {{ width: min(560px, calc(100vw - 32px)); padding: 32px; border: 1px solid #d9e4ee; border-radius: 14px; background: white; box-shadow: 0 20px 60px rgba(15, 34, 52, .12); }}
    h1 {{ margin: 0 0 12px; font-size: 28px; }}
    p {{ line-height: 1.5; color: #526174; }}
    a {{ display: inline-block; margin-top: 16px; padding: 12px 16px; border-radius: 8px; background: #078aa2; color: white; text-decoration: none; font-weight: 700; }}
  </style>
</head>
<body>
  <main>
    <h1>{escape(title)}</h1>
    <p>{escape(detail)}</p>
    <a href="/dashboard?tab=mail">Return to Flux Mail</a>
  </main>
</body>
</html>"""


def _should_proxy_crawl_sync(root_name: str | None, path: str | None) -> bool:
    if path and path_requires_host_agent(path):
        return True
    if not root_name:
        return False
    root = database.get_monitored_root(root_name)
    return bool(root and _root_requires_host_agent(root))


def _should_proxy_benchmark(scope: str | None, root_name: str | None, path: str | None) -> bool:
    normalized = str(scope or "synthetic").strip().lower().replace("-", "_")
    if normalized in {"", "synthetic", "fixture", "fixtures"}:
        return False
    if path and path_requires_host_agent(path):
        return True
    if not root_name:
        return False
    root = database.get_monitored_root(root_name)
    return bool(root and _root_requires_host_agent(root))


def _should_proxy_host_root(root_name: str) -> bool:
    root = database.get_monitored_root(root_name)
    return bool(root and _root_requires_host_agent(root))


def _root_requires_host_agent(root: dict) -> bool:
    metadata = root.get("metadata") or {}
    return metadata.get("host_access") == "host_agent" or path_requires_host_agent(str(root.get("root_path") or ""))
