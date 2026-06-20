from __future__ import annotations

from .health import (
    build_dashboard_html,
    collect_crawl_payload,
    collect_dashboard_payload,
    collect_jobs_payload,
    collect_retrieval_payload,
    doctor_payload,
)
from .service import KnowledgeService


def create_app():
    try:
        from fastapi import FastAPI
        from fastapi.responses import HTMLResponse
        from pydantic import BaseModel
    except ImportError as exc:  # pragma: no cover - optional dependency
        raise RuntimeError("Install REST support with `pip install -e .[api]`") from exc

    class RememberRequest(BaseModel):
        title: str
        body: str

    class SearchRequest(BaseModel):
        query: str
        limit: int = 5

    class ForgetRequest(BaseModel):
        memory_id: str
        reason: str = "user_request"

    class CrawlSyncRequest(BaseModel):
        root_name: str | None = None
        path: str | None = None
        dry_run: bool = False

    class WatchRequest(BaseModel):
        root_name: str | None = None
        enabled: bool

    app = FastAPI(title="Flux-LLM-KB")
    service = KnowledgeService()

    @app.get("/api/health")
    def health():
        return doctor_payload()

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
    def dashboard_jobs(limit: int = 50):
        return collect_jobs_payload(limit=limit)

    @app.get("/api/dashboard/retrieval-stats")
    def dashboard_retrieval_stats():
        return collect_retrieval_payload()

    @app.post("/api/search")
    def search(request: SearchRequest):
        return service.search(request.query, limit=request.limit)

    @app.post("/api/brief")
    def brief(request: SearchRequest):
        return {"brief": service.brief(request.query)}

    @app.post("/api/remember")
    def remember(request: RememberRequest):
        return service.remember(request.title, request.body).__dict__

    @app.get("/api/audit")
    def audit(limit: int = 50):
        return service.audit(limit=limit)

    @app.post("/api/forget")
    def forget(request: ForgetRequest):
        return service.forget(request.memory_id, reason=request.reason)

    @app.get("/api/crawl/status")
    def crawl_status():
        return collect_crawl_payload()

    @app.post("/api/crawl/sync")
    def crawl_sync(request: CrawlSyncRequest):
        return service.sync_corpus(root_name=request.root_name, path=request.path, dry_run=request.dry_run)

    @app.get("/api/crawl/jobs")
    def crawl_jobs(limit: int = 50):
        return collect_jobs_payload(limit=limit)

    @app.post("/api/crawl/watch")
    def crawl_watch(request: WatchRequest):
        from . import database

        return database.set_watch_enabled(root_name=request.root_name, enabled=request.enabled)

    return app
