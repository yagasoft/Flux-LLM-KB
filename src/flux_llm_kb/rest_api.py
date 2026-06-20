from __future__ import annotations

from .cli import doctor_payload
from .service import KnowledgeService


def create_app():
    try:
        from fastapi import FastAPI
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

    app = FastAPI(title="Flux-LLM-KB")
    service = KnowledgeService()

    @app.get("/api/health")
    def health():
        return doctor_payload()

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

    return app
