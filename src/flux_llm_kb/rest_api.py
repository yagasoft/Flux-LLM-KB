from json import JSONDecodeError
from pathlib import Path

from .host_agent import (
    path_requires_host_agent,
    remote_backfill,
    remote_browse_folder,
    remote_status,
    remote_sync,
    remote_validate_path,
    validate_host_path,
)
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
        from fastapi import Body, FastAPI, HTTPException
        from fastapi.responses import HTMLResponse
        from fastapi.staticfiles import StaticFiles
        from pydantic import BaseModel, Field
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

    class WatchRequest(BaseModel):
        root_name: str | None = None
        enabled: bool

    class CrawlBackfillRequest(BaseModel):
        kind: str = "all"
        limit: int = 10
        workers: int = 1
        root_name: str | None = None

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
        sync_enabled: bool = False
        sync_interval_seconds: int = 900
        sync_window_days: int = 30
        max_messages_per_run: int = 200

    class MailSyncRequest(BaseModel):
        profile_name: str | None = None

    class GmailOAuthStartRequest(BaseModel):
        profile_name: str
        client_config_path: str
        redirect_uri: str | None = None

    class GmailOAuthClientConfigRequest(BaseModel):
        client_config_path: str

    class OutlookHostSyncRequest(BaseModel):
        profile_name: str

    app = FastAPI(title="Flux-LLM-KB")
    service = KnowledgeService()
    dashboard_assets = Path(__file__).resolve().parent / "dashboard_static" / "assets"
    if dashboard_assets.exists():
        app.mount("/dashboard/assets", StaticFiles(directory=str(dashboard_assets)), name="dashboard-assets")

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
    def search(request: SearchRequest = Body(...)):
        return service.search(request.query, limit=request.limit)

    @app.get("/api/search")
    def search_get(query: str, limit: int = 5):
        return service.search(query, limit=limit)

    @app.post("/api/brief")
    def brief(request: SearchRequest = Body(...)):
        return {"brief": service.brief(request.query)}

    @app.get("/api/brief")
    def brief_get(query: str, token_budget: int | None = None):
        return {"brief": service.brief(query, token_budget=token_budget)}

    @app.get("/api/corpus/assets")
    def corpus_assets(root_name: str | None = None, path: str | None = None, limit: int = 50):
        from . import database

        return {"assets": database.list_source_assets(root_name=root_name, path=path, limit=limit)}

    @app.get("/api/corpus/assets/{asset_id}")
    def corpus_asset(asset_id: str):
        from . import database

        asset = database.get_source_asset(asset_id)
        if asset is None:
            raise HTTPException(status_code=404, detail="source asset not found")
        return asset

    @app.get("/api/corpus/chunks/{chunk_id}")
    def corpus_chunk(chunk_id: str):
        from . import database

        chunk = database.get_asset_chunk(chunk_id)
        if chunk is None:
            raise HTTPException(status_code=404, detail="asset chunk not found")
        return chunk

    @app.post("/api/remember")
    def remember(request: RememberRequest = Body(...)):
        return service.remember(request.title, request.body).__dict__

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
            raise HTTPException(status_code=400, detail="root name is required")
        root_path_text = request.root_path.strip()
        validation = _validate_root_path(root_path_text)
        if validation["status"] != "ok":
            raise HTTPException(status_code=400, detail=validation["message"])

        settings = SettingsService()
        max_inline_bytes = request.max_inline_bytes
        if max_inline_bytes is None:
            max_inline_bytes = int(settings.resolve("crawler.max_inline_bytes").raw_value)
        heavy_threshold_bytes = request.heavy_threshold_bytes
        if heavy_threshold_bytes is None:
            heavy_threshold_bytes = int(settings.resolve("crawler.heavy_threshold_bytes").raw_value)

        if max_inline_bytes <= 0 or heavy_threshold_bytes <= 0:
            raise HTTPException(status_code=400, detail="size thresholds must be positive")

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
            metadata={
                "source": "dashboard",
                "host_access": "host_agent" if validation.get("host_agent") else "direct",
                "host_validation": validation,
            },
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
            raise HTTPException(status_code=400, detail="root name is required")
        root_path_text = request.root_path.strip()
        validation = _validate_root_path(root_path_text)
        if validation["status"] != "ok":
            raise HTTPException(status_code=400, detail=validation["message"])

        settings = SettingsService()
        max_inline_bytes = request.max_inline_bytes
        if max_inline_bytes is None:
            max_inline_bytes = int(settings.resolve("crawler.max_inline_bytes").raw_value)
        heavy_threshold_bytes = request.heavy_threshold_bytes
        if heavy_threshold_bytes is None:
            heavy_threshold_bytes = int(settings.resolve("crawler.heavy_threshold_bytes").raw_value)

        if max_inline_bytes <= 0 or heavy_threshold_bytes <= 0:
            raise HTTPException(status_code=400, detail="size thresholds must be positive")

        try:
            return database.update_monitored_root(
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
                metadata={
                    "source": "dashboard",
                    "host_access": "host_agent" if validation.get("host_agent") else "direct",
                    "host_validation": validation,
                },
            )
        except ValueError as exc:
            raise HTTPException(status_code=404, detail=str(exc)) from exc

    @app.delete("/api/crawl/roots/{root_id}")
    def crawl_root_delete(root_id: str, purge_index: bool = True):
        from . import database

        try:
            return database.delete_monitored_root(root_id=root_id, purge_index=purge_index, actor="dashboard")
        except ValueError as exc:
            raise HTTPException(status_code=400, detail=str(exc)) from exc

    @app.get("/api/crawl/jobs")
    def crawl_jobs(limit: int = 50):
        return collect_jobs_payload(limit=limit)

    @app.post("/api/crawl/backfill")
    def crawl_backfill(request: CrawlBackfillRequest = Body(...)):
        kwargs: dict[str, object] = {"kind": request.kind, "limit": request.limit, "workers": request.workers}
        if request.root_name is not None:
            kwargs["root_name"] = request.root_name
            if _should_proxy_host_root(request.root_name):
                return host_agent_backfill(**kwargs)
        return service.run_corpus_backfill(**kwargs)

    @app.post("/api/crawl/watch")
    def crawl_watch(request: WatchRequest = Body(...)):
        from . import database

        return database.set_watch_enabled(root_name=request.root_name, enabled=request.enabled)

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
            sync_enabled=request.sync_enabled,
            sync_interval_seconds=request.sync_interval_seconds,
            sync_window_days=request.sync_window_days,
            max_messages_per_run=request.max_messages_per_run,
        )

    @app.put("/api/mail/profiles/{profile_name}/oauth-client-config")
    def mail_profile_oauth_client_config(profile_name: str, request: GmailOAuthClientConfigRequest = Body(...)):
        from .mail_ingestion import update_mail_profile_oauth_client_config_path

        try:
            return update_mail_profile_oauth_client_config_path(
                profile_name=profile_name,
                client_config_path=request.client_config_path,
            )
        except ValueError as exc:
            raise HTTPException(status_code=404, detail=str(exc)) from exc

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

    @app.get("/api/outlook-host/status")
    def outlook_host_status():
        from .outlook_host import status

        return status()

    @app.post("/api/outlook-host/request-sync")
    def outlook_host_request_sync(request: OutlookHostSyncRequest = Body(...)):
        from .outlook_host import request_sync

        return request_sync(request.profile_name, actor="dashboard")

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
    limit: int = 10,
    workers: int = 1,
    root_name: str | None = None,
) -> dict:
    return remote_backfill(kind=kind, limit=limit, workers=workers, root_name=root_name)


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


def _should_proxy_crawl_sync(root_name: str | None, path: str | None) -> bool:
    from . import database

    if path and path_requires_host_agent(path):
        return True
    if not root_name:
        return False
    root = database.get_monitored_root(root_name)
    return bool(root and _root_requires_host_agent(root))


def _should_proxy_host_root(root_name: str) -> bool:
    from . import database

    root = database.get_monitored_root(root_name)
    return bool(root and _root_requires_host_agent(root))


def _root_requires_host_agent(root: dict) -> bool:
    metadata = root.get("metadata") or {}
    return metadata.get("host_access") == "host_agent" or path_requires_host_agent(str(root.get("root_path") or ""))
