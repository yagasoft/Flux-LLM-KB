from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from pathlib import PurePosixPath, PureWindowsPath
import re
import time
from typing import Any

from .crawler import CorpusPolicy, scan_path
from . import database
from .glob_policy import effective_glob_policy
from .redaction import redact_text
from .scoring import ContextCandidate, pack_context
from .settings import SettingsService
from .versioning import collapse_version_families
from .watcher import WatchEvent, WatchRoot, create_corpus_watcher


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
        corpus_limit = max(limit * 4, 20)
        episodes = [
            {"kind": "episode", **item}
            for item in database.search_episodes(query, limit=limit)
        ]
        corpus = [
            {"kind": "corpus_chunk", **item}
            for item in database.search_corpus_chunks(query, limit=corpus_limit)
        ]
        return collapse_version_families(
            sorted(corpus + episodes, key=lambda item: item["score"], reverse=True),
            limit=limit,
        )

    def brief(self, query: str, token_budget: int | None = None) -> str:
        if token_budget is None:
            token_budget = _configured_token_budget()
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

    def sync_corpus(
        self,
        *,
        root_name: str | None = None,
        path: str | Path | None = None,
        dry_run: bool = False,
    ) -> dict[str, Any]:
        root = _select_root(root_name=root_name, path=path)
        glob_policy = _configured_glob_policy(root)
        policy = CorpusPolicy(
            root_path=Path(root["root_path"]),
            recursive=root["recursive"],
            include_globs=tuple(glob_policy["include_globs"]),
            exclude_globs=tuple(glob_policy["exclude_globs"]),
            max_inline_bytes=root["max_inline_bytes"],
            heavy_threshold_bytes=root["heavy_threshold_bytes"],
        )
        plan = scan_path(root["root_path"], policy, target_path=path)
        return database.persist_crawl_plan(root_name=root["name"], plan=plan, dry_run=dry_run)

    def run_watch(self, *, root_name: str | None = None, interval_seconds: float = 2.0) -> dict[str, Any]:
        if not _load_watch_roots(root_name):
            return {"status": "no_enabled_roots", "root_name": root_name}

        watcher = create_corpus_watcher(
            lambda: _load_watch_roots(root_name),
            on_change=self._handle_watch_event,
            interval_seconds=interval_seconds,
        )
        watcher.poll_once(seed=True)
        while True:
            for root in _load_watch_roots(root_name):
                database.record_watcher_heartbeat(root_name=root.name)
            watcher.poll_once()
            time.sleep(interval_seconds)

    def run_corpus_backfill(
        self,
        *,
        kind: str = "all",
        limit: int = 10,
        workers: int = 1,
        root_name: str | None = None,
    ) -> dict[str, Any]:
        from . import worker

        cancelled = database.cancel_duplicate_corpus_jobs(root_name=root_name)
        claimed = database.claim_corpus_jobs(
            limit=limit,
            worker_id=f"flux-kb-backfill-{workers}",
            root_name=root_name,
        )
        filtered = [
            job
            for job in claimed
            if kind == "all" or _job_matches_kind(job["job_type"], kind)
        ]
        filtered_ids = {job["id"] for job in filtered}
        for job in claimed:
            if job["id"] not in filtered_ids:
                database.retry_corpus_job(
                    job_id=job["id"],
                    error=f"released by {kind} backfill filter",
                    cooldown_seconds=30,
                )
        completed = 0
        blocked = 0
        retried = 0
        for job in filtered:
            process_result = worker.process_corpus_job(job)
            if process_result.status in {"indexed", "metadata_only"}:
                database.complete_corpus_job(job_id=job["id"])
                completed += 1
            elif process_result.status == "blocked_missing_dependency":
                database.block_corpus_job(
                    job_id=job["id"],
                    error=process_result.message or "blocked_missing_dependency",
                )
                blocked += 1
            else:
                database.retry_corpus_job(
                    job_id=job["id"],
                    error=process_result.message or process_result.status,
                    cooldown_seconds=300,
                )
                retried += 1
        database.record_audit_event(
            event_type="corpus.backfill",
            details={
                "kind": kind,
                "root_name": root_name,
                "claimed": len(claimed),
                "completed": completed,
                "blocked": blocked,
                "retried": retried,
                "cancelled_duplicate": cancelled["cancelled"],
                "workers": workers,
            },
        )
        return {
            "kind": kind,
            "root_name": root_name,
            "claimed": len(claimed),
            "completed": completed,
            "blocked": blocked,
            "retried": retried,
            "cancelled_duplicate": cancelled["cancelled"],
            "jobs": filtered,
        }

    def run_corpus_worker(
        self,
        *,
        kind: str = "all",
        limit: int = 10,
        workers: int = 1,
        interval_seconds: float = 5.0,
        once: bool = False,
        root_name: str | None = None,
    ) -> dict[str, Any]:
        runs = 0
        last_result: dict[str, Any] | None = None
        while True:
            runs += 1
            last_result = self.run_corpus_backfill(
                kind=kind,
                limit=limit,
                workers=workers,
                root_name=root_name,
            )
            if once:
                return {
                    "status": "completed_once",
                    "once": True,
                    "kind": kind,
                    "limit": limit,
                    "workers": workers,
                    "interval_seconds": interval_seconds,
                    "root_name": root_name,
                    "runs": runs,
                    "last_result": last_result,
                }
            time.sleep(interval_seconds)

    def _handle_watch_event(self, event: WatchEvent) -> None:
        try:
            database.record_watch_event(root_name=event.root_name)
            self.sync_corpus(root_name=event.root_name)
        except Exception as exc:  # pragma: no cover - environment-specific watcher loop
            database.record_watch_error(root_name=event.root_name, error=str(exc))


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


def _select_root(*, root_name: str | None, path: str | Path | None) -> dict[str, Any]:
    roots = database.list_monitored_roots()
    if root_name:
        for root in roots:
            if root["name"] == root_name:
                return root
        raise ValueError(f"monitored root not found: {root_name}")
    if path:
        target = str(path)
        for root in roots:
            if _path_is_under_root(target, str(root["root_path"])):
                return root
        raise ValueError(f"path is not under a monitored root: {path}")
    if len(roots) == 1:
        return roots[0]
    raise ValueError("specify --root or --path")


def _job_matches_kind(job_type: str, kind: str) -> bool:
    if kind == "images":
        return job_type == "corpus_extract_image"
    if kind == "media":
        return job_type in {"corpus_extract_audio", "corpus_extract_video"}
    if kind == "text":
        return job_type in {"corpus_extract_text", "corpus_extract_code", "corpus_extract_document"}
    if kind == "embeddings":
        return job_type == "corpus_embed"
    return True


def _configured_token_budget() -> int:
    try:
        return int(SettingsService().resolve("retrieval.token_budget").raw_value)
    except Exception:
        return 1200


def _configured_glob_policy(root: dict[str, Any]) -> dict[str, Any]:
    settings = SettingsService()
    try:
        global_include = settings.resolve("crawler.global_include_globs").raw_value
    except Exception:
        global_include = []
    try:
        global_exclude = settings.resolve("crawler.global_exclude_globs").raw_value
    except Exception:
        global_exclude = []
    return effective_glob_policy(root, global_include=global_include, global_exclude=global_exclude)


def _path_is_under_root(path: str, root_path: str) -> bool:
    if _looks_windows(path) or _looks_windows(root_path):
        target = PureWindowsPath(path)
        root = PureWindowsPath(root_path)
        try:
            target.relative_to(root)
            return True
        except ValueError:
            return False
    target_posix = PurePosixPath(path)
    root_posix = PurePosixPath(root_path)
    try:
        target_posix.relative_to(root_posix)
        return True
    except ValueError:
        pass
    try:
        target_local = Path(path).expanduser().resolve()
        root_local = Path(root_path).expanduser().resolve()
        return target_local == root_local or target_local.is_relative_to(root_local)
    except Exception:
        return False


def _looks_windows(path: str) -> bool:
    return bool(PureWindowsPath(path).drive) or str(path).startswith("\\\\")


def _load_watch_roots(root_name: str | None = None) -> list[WatchRoot]:
    roots = [
        root
        for root in database.list_monitored_roots(watch_enabled=True)
        if root["enabled"] and (root_name is None or root["name"] == root_name)
    ]
    return [
        WatchRoot(
            name=root["name"],
            root_path=Path(root["root_path"]),
            watch_enabled=root["watch_enabled"],
            recursive=root["recursive"],
        )
        for root in roots
    ]
