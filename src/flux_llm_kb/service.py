from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import re
from typing import Any

from . import database
from .redaction import redact_text
from .scoring import ContextCandidate, pack_context


@dataclass(frozen=True)
class RememberResult:
    id: str
    redaction_count: int


class KnowledgeService:
    def remember(self, title: str, body: str, metadata: dict[str, Any] | None = None) -> RememberResult:
        redacted_title, title_findings = redact_text(title)
        redacted, findings = redact_text(body)
        all_findings = title_findings + findings
        episode_id = database.insert_episode(
            title=redacted_title,
            summary=redacted,
            metadata={**(metadata or {}), "redactions": [finding.kind for finding in all_findings]},
        )
        return RememberResult(id=episode_id, redaction_count=len(all_findings))

    def search(self, query: str, limit: int = 5) -> list[dict[str, Any]]:
        return database.search_episodes(query, limit=limit)

    def brief(self, query: str, token_budget: int = 1200) -> str:
        candidates = [
            ContextCandidate(
                id=item["id"],
                title=item["title"],
                body=item["summary"],
                score=item["score"],
            )
            for item in self.search(query, limit=10)
        ]
        return pack_context(candidates, token_budget=token_budget)

    def audit(self, limit: int = 50) -> list[dict[str, Any]]:
        return database.list_audit_events(limit=limit)

    def forget(self, memory_id: str, reason: str = "user_request") -> dict[str, Any]:
        deleted = database.forget_episode(memory_id, reason=reason)
        return {"id": memory_id, "deleted": deleted}

    def export_wiki(self, output_dir: str | Path, limit: int = 500) -> dict[str, Any]:
        path = Path(output_dir)
        path.mkdir(parents=True, exist_ok=True)
        episodes = database.list_episodes(limit=limit)

        index_lines = [
            "# Flux-LLM-KB Export",
            "",
            "This export is generated from the local private database. Review before sharing.",
            "",
        ]
        for episode in episodes:
            filename = f"{_slugify(episode['title'])}-{episode['id'][:8]}.md"
            target = path / filename
            target.write_text(_episode_markdown(episode), encoding="utf-8")
            index_lines.append(f"- [{episode['title']}]({filename})")

        (path / "index.md").write_text("\n".join(index_lines) + "\n", encoding="utf-8")
        database.record_audit_event(
            event_type="wiki.exported",
            details={"output_dir": str(path), "episode_count": len(episodes)},
        )
        return {"output_dir": str(path), "episode_count": len(episodes)}

    def queue_codex_backfill(self, source_dir: str | Path, *, dry_run: bool = False) -> dict[str, Any]:
        root = Path(source_dir).expanduser()
        candidates = [
            path
            for path in root.rglob("*")
            if path.is_file() and path.suffix.lower() in {".json", ".jsonl", ".md", ".txt"}
        ]
        if dry_run:
            return {"source_dir": str(root), "candidate_count": len(candidates), "queued": 0}

        queued = 0
        for path in candidates:
            database.enqueue_capture_job(
                job_type="codex_backfill",
                payload={"path": str(path), "status": "pending_review"},
            )
            queued += 1
        return {"source_dir": str(root), "candidate_count": len(candidates), "queued": queued}


def _episode_markdown(episode: dict[str, Any]) -> str:
    return "\n".join(
        [
            f"# {episode['title']}",
            "",
            f"- ID: `{episode['id']}`",
            f"- Source kind: `{episode['source_kind']}`",
            f"- Updated: `{episode['updated_at']}`",
            "",
            episode["summary"].strip(),
            "",
        ]
    )


def _slugify(value: str) -> str:
    slug = re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")
    return slug[:72] or "memory"
