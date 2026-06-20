from __future__ import annotations

from .service import KnowledgeService


def create_server():
    try:
        from mcp.server.fastmcp import FastMCP
    except ImportError as exc:  # pragma: no cover - optional dependency
        raise RuntimeError("Install MCP support with `pip install -e .[mcp]`") from exc

    service = KnowledgeService()
    mcp = FastMCP(
        "Flux-LLM-KB",
        instructions=(
            "Use kb.brief before non-trivial work. Store only redacted, durable "
            "knowledge. Do not persist secrets, raw transcripts, or private exports."
        ),
    )

    @mcp.tool(name="kb.search")
    def search(query: str, limit: int = 5):
        return service.search(query, limit=limit)

    @mcp.tool(name="kb.brief")
    def brief(query: str, token_budget: int = 1200):
        return service.brief(query, token_budget=token_budget)

    @mcp.tool(name="kb.remember")
    def remember(title: str, body: str):
        return service.remember(title, body).__dict__

    @mcp.tool(name="kb.finalize_turn")
    def finalize_turn(title: str, summary: str):
        return service.remember(title, summary, metadata={"source": "finalize_turn"}).__dict__

    @mcp.tool(name="kb.audit")
    def audit(limit: int = 50):
        return service.audit(limit=limit)

    @mcp.tool(name="kb.forget")
    def forget(memory_id: str, reason: str = "user_request"):
        return service.forget(memory_id, reason=reason)

    @mcp.tool(name="kb.status")
    def status():
        from .health import doctor_payload

        return doctor_payload()

    @mcp.tool(name="kb.crawl_status")
    def crawl_status():
        from .health import collect_crawl_payload

        return collect_crawl_payload()

    @mcp.tool(name="kb.crawl_sync")
    def crawl_sync(root_name: str | None = None, path: str | None = None, dry_run: bool = False):
        return service.sync_corpus(root_name=root_name, path=path, dry_run=dry_run)

    @mcp.tool(name="kb.crawl_watch_status")
    def crawl_watch_status():
        from . import database

        return database.crawl_status()

    @mcp.tool(name="kb.crawl_watch_enable")
    def crawl_watch_enable(root_name: str | None = None):
        from . import database

        return database.set_watch_enabled(root_name=root_name, enabled=True)

    @mcp.tool(name="kb.crawl_watch_disable")
    def crawl_watch_disable(root_name: str | None = None):
        from . import database

        return database.set_watch_enabled(root_name=root_name, enabled=False)

    @mcp.tool(name="kb.crawl_jobs")
    def crawl_jobs(limit: int = 50):
        from . import database

        return {"jobs": database.list_capture_jobs(limit=limit)}

    @mcp.tool(name="kb.mail_status")
    def mail_status():
        from .mail_ingestion import mail_status as collect_mail_status

        return collect_mail_status()

    return mcp


def main() -> None:
    create_server().run()


if __name__ == "__main__":
    main()
