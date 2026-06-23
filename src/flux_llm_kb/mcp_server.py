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
            "Use kb.brief before non-trivial work for compact workspace-scoped context. "
            "You may query mid-turn with expanded kb.search using "
            "scope_mode=\"workspace_boosted\" when "
            "you need prior decisions, unresolved project context, patterns from other "
            "workspaces, general indexed documents, previous fixes, or user-referenced "
            "history. Skip KB queries when local files, the prompt, or current tool "
            "output already answer the question. Store only redacted, durable knowledge. "
            "Do not persist secrets, raw transcripts, or private exports."
        ),
    )

    @mcp.tool(name="kb.search")
    def search(query: str, limit: int = 5, cwd: str | None = None, root_name: str | None = None, scope_mode: str = "local_first"):
        """Search Flux memory. Use scope_mode="workspace_boosted" for explicit expanded mid-turn discovery."""
        return service.search(query, limit=limit, cwd=cwd, root_name=root_name, scope_mode=scope_mode)

    @mcp.tool(name="kb.brief")
    def brief(query: str, token_budget: int = 1200, cwd: str | None = None, root_name: str | None = None, scope_mode: str = "local_first"):
        """Build a compact brief. Default local_first keeps automatic context workspace scoped."""
        return service.brief(
            query,
            token_budget=token_budget,
            cwd=cwd,
            root_name=root_name,
            scope_mode=scope_mode,
        )

    @mcp.tool(name="kb.remember")
    def remember(title: str, body: str, cwd: str | None = None, root_name: str | None = None):
        return service.remember(title, body, cwd=cwd, root_name=root_name).__dict__

    @mcp.tool(name="kb.claim_upsert")
    def claim_upsert(
        subject_type: str,
        subject: str,
        predicate: str,
        object_text: str,
        confidence: float = 0.5,
        episode_id: str | None = None,
    ):
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
        return service.traverse_graph(
            entity_id=entity_id,
            relation_types=relation_types,
            max_depth=max_depth,
            direction=direction,
            limit=limit,
        )

    @mcp.tool(name="kb.capture_review")
    def capture_review(limit: int = 50):
        return service.list_capture_review_jobs(limit=limit)

    @mcp.tool(name="kb.capture_review_decide")
    def capture_review_decide(job_id: str, decision: str, rationale: str):
        return service.review_capture_job(
            job_id=job_id,
            decision=decision,
            rationale=rationale,
            actor="mcp",
        )

    @mcp.tool(name="kb.retention_policies")
    def retention_policies():
        return service.list_retention_policies()

    @mcp.tool(name="kb.retention_quality")
    def retention_quality(limit: int = 25):
        return service.retention_quality_report(limit=limit)

    @mcp.tool(name="kb.finalize_turn")
    def finalize_turn(title: str, summary: str, cwd: str | None = None, root_name: str | None = None):
        return service.remember(title, summary, metadata={"source": "finalize_turn"}, cwd=cwd, root_name=root_name).__dict__

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
