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

    class MailSyncRequest(BaseModel):
        profile_name: str | None = None

    class GmailOAuthStartRequest(BaseModel):
        profile_name: str
        client_config_path: str
        redirect_uri: str | None = None

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

    @app.get("/api/settings")
    def settings_list():
        from .settings import SettingsService

        return SettingsService().public_list()

    @app.get("/api/settings/{key}")
    def settings_get(key: str):
        from .settings import SettingsService

        return SettingsService().resolve(key).to_public_dict()

    @app.put("/api/settings/{key}")
    def settings_put(key: str, request: SettingUpdateRequest):
        from .settings import SettingsService

        return SettingsService().set(
            key,
            request.value,
            actor="dashboard",
            reason=request.reason,
            confirmed=request.confirmed,
        )

    @app.post("/api/settings/apply")
    def settings_apply(request: SettingsApplyRequest):
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
    def mail_profile_add(request: MailProfileRequest):
        from .mail_ingestion import add_mail_profile

        return add_mail_profile(
            name=request.name,
            source_type=request.source_type,
            account=request.account,
            server=request.server,
            folder_paths=request.folder_paths,
            spool_path=request.spool_path,
            post_process_policy=request.post_process_policy,
        )

    @app.post("/api/mail/sync")
    def mail_sync(request: MailSyncRequest):
        from .mail_ingestion import sync_mail_profile

        return sync_mail_profile(profile_name=request.profile_name)

    @app.post("/api/mail/watch")
    def mail_watch(_: MailSyncRequest):
        return {"status": "watch_loop_runs_from_cli", "command": "flux-kb mail watch run"}

    @app.post("/api/mail/oauth/gmail/start")
    def mail_oauth_gmail_start(request: GmailOAuthStartRequest):
        from .mail_oauth import start_gmail_oauth

        return start_gmail_oauth(
            profile_name=request.profile_name,
            client_config_path=request.client_config_path,
            redirect_uri=request.redirect_uri,
        )

    @app.get("/api/mail/oauth/gmail/callback")
    def mail_oauth_gmail_callback(state: str, code: str | None = None, error: str | None = None):
        if error:
            return {"status": "error", "error": error, "state": state}
        if not code:
            return {"status": "error", "error": "missing authorization code", "state": state}
        from .mail_oauth import complete_gmail_oauth

        return complete_gmail_oauth(state=state, code=code)

    @app.get("/api/mail/oauth/status")
    def mail_oauth_status(profile_name: str | None = None):
        from .mail_oauth import oauth_status

        return oauth_status(profile_name=profile_name)

    return app
