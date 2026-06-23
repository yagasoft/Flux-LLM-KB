from __future__ import annotations

from dataclasses import dataclass
import json
from pathlib import Path
from pathlib import PurePosixPath, PureWindowsPath
import re
import time
from typing import Any

from .crawler import CorpusPolicy, scan_path
from . import database
from .glob_policy import effective_glob_policy
from .processes import run_no_window
from .redaction import RedactionFinding, redact_text
from .result_details import collapse_mail_spool_search_results, decorate_corpus_search_item
from .scoring import ContextCandidate, pack_context
from .settings import SettingsService
from .versioning import collapse_version_families
from .watcher import WatchEvent, WatchRoot, create_corpus_watcher


LOCAL_SCOPE_SCORE_BOOST = 1.15
STRONG_VECTOR_MIN_SCORE = 0.35


@dataclass(frozen=True)
class RememberResult:
    id: str
    redaction_count: int


@dataclass(frozen=True)
class RetrievalScope:
    mode: str
    cwd: str | None = None
    root_name: str | None = None
    root_path: str | None = None
    workspace_root: str | None = None
    workspace_key: str | None = None

    @property
    def is_scoped(self) -> bool:
        return bool(self.cwd or self.root_name or self.root_path or self.workspace_key)


class KnowledgeService:
    def remember(
        self,
        title: str,
        body: str,
        metadata: dict[str, Any] | None = None,
        cwd: str | None = None,
        root_name: str | None = None,
    ) -> RememberResult:
        redacted_title, title_findings = redact_text(title)
        redacted, findings = redact_text(body)
        all_findings = title_findings + findings
        enriched_metadata = _enrich_workspace_metadata(metadata or {}, cwd=cwd, root_name=root_name)
        episode_id = database.insert_episode(
            title=redacted_title,
            summary=redacted,
            metadata={**enriched_metadata, "redactions": [finding.kind for finding in all_findings]},
        )
        return RememberResult(id=episode_id, redaction_count=len(all_findings))

    def search(
        self,
        query: str,
        limit: int = 5,
        cwd: str | None = None,
        root_name: str | None = None,
        scope_mode: str = "local_first",
    ) -> list[dict[str, Any]]:
        scope = _resolve_retrieval_scope(cwd=cwd, root_name=root_name, scope_mode=scope_mode)
        if scope.mode == "global" or not scope.is_scoped:
            return self._search_once(query, limit=limit, scope=RetrievalScope(mode="global"), label="global")
        if scope.mode == "workspace_boosted":
            return self._search_workspace_boosted(query, limit=limit, scope=scope)

        scoped_results = self._search_once(query, limit=limit, scope=scope, label="local")
        if scope.mode == "local_only" or _has_lexical_or_fuzzy_evidence(scoped_results):
            return scoped_results

        return self._search_once(
            query,
            limit=limit,
            scope=RetrievalScope(mode="global"),
            label="global_fallback",
        )

    def _search_workspace_boosted(self, query: str, *, limit: int, scope: RetrievalScope) -> list[dict[str, Any]]:
        limit = max(1, min(int(limit or 5), 50))
        local_results = self._search_once(query, limit=limit, scope=scope, label="local")
        local_keys = {_result_identity(item) for item in local_results}

        cross_candidate_limit = min(max(limit * 2, 8), 50)
        cross_results = self._search_once(
            query,
            limit=cross_candidate_limit,
            scope=RetrievalScope(mode="global"),
            label_scope=scope,
            label="cross_workspace",
        )
        cross_results = [
            item
            for item in cross_results
            if _result_identity(item) not in local_keys and _is_strong_cross_workspace_evidence(item)
        ]
        if _has_lexical_or_fuzzy_evidence(local_results):
            cross_results = cross_results[: max(1, limit // 2)]

        combined = _dedupe_search_results(
            [_with_scope_score_boost(item, LOCAL_SCOPE_SCORE_BOOST) for item in local_results] + cross_results
        )
        return collapse_version_families(
            sorted(combined, key=lambda item: float(item.get("score") or 0.0), reverse=True),
            limit=limit,
        )

    def _search_once(
        self,
        query: str,
        *,
        limit: int,
        scope: RetrievalScope,
        label: str,
        label_scope: RetrievalScope | None = None,
    ) -> list[dict[str, Any]]:
        corpus_limit = max(limit * 4, 20)
        is_local = label == "local"
        episode_items = (
            database.search_episodes(
                query,
                limit=limit,
                cwd=scope.cwd,
                root_path=scope.root_path,
                workspace_key=scope.workspace_key,
            )
            if not is_local or scope.cwd or scope.root_path or scope.workspace_key
            else []
        )
        corpus_items = (
            database.search_corpus_chunks(query, limit=corpus_limit, root_name=scope.root_name)
            if not is_local or scope.root_name
            else []
        )
        episodes = [
            {
                "kind": "episode",
                "logical_kind": "episode",
                **item,
                "excerpt": item.get("summary", ""),
                "detail_ref": {"kind": "episode", "id": item.get("id")},
                "related_evidence_count": 0,
            }
            for item in episode_items
        ]
        corpus = collapse_mail_spool_search_results(
            [
                decorate_corpus_search_item({"kind": "corpus_chunk", **_format_corpus_search_item(item)})
                for item in corpus_items
            ]
        )
        results = collapse_version_families(
            sorted(corpus + episodes, key=lambda item: item["score"], reverse=True),
            limit=limit,
        )
        return [_tag_retrieval_scope(item, label, scope=label_scope or scope) for item in results]

    def brief(
        self,
        query: str,
        token_budget: int | None = None,
        cwd: str | None = None,
        root_name: str | None = None,
        scope_mode: str = "local_first",
    ) -> str:
        if token_budget is None:
            token_budget = _configured_token_budget()
        search_results = self.search(
            query,
            limit=10,
            cwd=cwd,
            root_name=root_name,
            scope_mode=scope_mode,
        )
        current_results = [item for item in search_results if _is_current_evidence(item)]
        if current_results:
            search_results = current_results
        candidates = [
            ContextCandidate(
                id=item["id"],
                title=item["title"],
                body=item["summary"],
                score=item["score"],
            )
            for item in search_results
        ]
        return pack_context(candidates, token_budget=token_budget)

    def audit(self, limit: int = 50) -> list[dict[str, Any]]:
        return database.list_audit_events(limit=limit)

    def forget(self, memory_id: str, reason: str = "user_request") -> dict[str, Any]:
        deleted = database.forget_episode(memory_id, reason=reason)
        return {"id": memory_id, "deleted": deleted}

    def upsert_claim(
        self,
        *,
        subject_type: str,
        subject_name: str,
        predicate: str,
        object_text: str,
        confidence: float = 0.5,
        episode_id: str | None = None,
        metadata: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        redacted_subject, subject_findings = redact_text(subject_name)
        redacted_predicate, predicate_findings = redact_text(predicate)
        redacted_object, object_findings = redact_text(object_text)
        redacted_metadata, metadata_findings = _redact_metadata(metadata or {})
        all_findings = subject_findings + predicate_findings + object_findings + metadata_findings
        if all_findings:
            existing_redactions = redacted_metadata.get("redactions")
            redactions = existing_redactions if isinstance(existing_redactions, list) else []
            redacted_metadata = {
                **redacted_metadata,
                "redactions": [*redactions, *(finding.kind for finding in all_findings)],
            }
        return database.upsert_claim(
            subject_type=subject_type,
            subject_name=redacted_subject,
            predicate=redacted_predicate,
            object_text=redacted_object,
            confidence=confidence,
            episode_id=episode_id,
            metadata=redacted_metadata,
        )

    def get_claim(self, claim_id: str) -> dict[str, Any]:
        claim = database.get_claim(claim_id)
        if claim is None:
            raise LookupError(f"claim not found: {claim_id}")
        return claim

    def list_claims(
        self,
        *,
        review: str = "all",
        state: str | None = None,
        q: str | None = None,
        limit: int = 50,
    ) -> dict[str, Any]:
        return {
            "claims": database.list_claims(review=review, state=state, q=q, limit=limit),
            "counts": database.claim_review_counts(),
        }

    def list_retention_policies(self) -> dict[str, Any]:
        return {"policies": database.list_retention_policies()}

    def set_retention_policy(
        self,
        *,
        memory_class: str,
        half_life_days: int,
        min_confidence: float,
        action: str,
        actor: str = "system",
        reason: str,
    ) -> dict[str, Any]:
        return database.set_retention_policy(
            memory_class=memory_class,
            half_life_days=half_life_days,
            min_confidence=min_confidence,
            action=action,
            actor=actor,
            reason=reason,
        )

    def retention_quality_report(self, *, limit: int = 25) -> dict[str, Any]:
        return database.retention_quality_report(limit=limit)

    def list_capture_review_jobs(self, *, limit: int = 50) -> dict[str, Any]:
        return {"jobs": database.list_capture_review_jobs(limit=limit)}

    def review_capture_job(
        self,
        *,
        job_id: str,
        decision: str,
        rationale: str,
        actor: str = "system",
    ) -> dict[str, Any]:
        return database.review_capture_job(
            job_id=job_id,
            decision=decision,
            rationale=rationale,
            actor=actor,
        )

    def decide_capture_review_job(
        self,
        *,
        job_id: str,
        decision: str,
        reason: str | None = None,
        rationale: str | None = None,
        actor: str = "system",
    ) -> dict[str, Any]:
        return self.review_capture_job(
            job_id=job_id,
            decision=decision,
            rationale=rationale if rationale is not None else reason or "",
            actor=actor,
        )

    def transition_claim(
        self,
        *,
        claim_id: str,
        transition: str,
        related_claim_id: str | None = None,
        reason: str | None = None,
        actor: str = "system",
        confidence_delta: float = 0.0,
    ) -> dict[str, Any]:
        return database.transition_claim(
            claim_id=claim_id,
            transition=transition,
            related_claim_id=related_claim_id,
            reason=reason,
            actor=actor,
            confidence_delta=confidence_delta,
        )

    def traverse_graph(
        self,
        *,
        entity_id: str,
        relation_types: list[str] | None = None,
        max_depth: int = 2,
        direction: str = "out",
        limit: int = 100,
    ) -> dict[str, Any]:
        return database.traverse_entity_graph(
            entity_id=entity_id,
            relation_types=relation_types,
            max_depth=max_depth,
            direction=direction,
            limit=limit,
        )

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
        reason: str = "manual_sync",
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
            stability_quiet_seconds=_configured_stability_quiet_seconds() if reason == "watch_event" else 0.0,
            large_file_stability_quiet_seconds=_configured_large_file_stability_quiet_seconds() if reason == "watch_event" else 0.0,
        )
        plan = scan_path(root["root_path"], policy, target_path=path)
        return database.persist_crawl_plan(root_name=root["name"], plan=plan, dry_run=dry_run, reason=reason)

    def backfill_episode_workspace_scope(
        self,
        *,
        episode_ids: list[str],
        cwd: str | None,
        root_name: str | None = None,
        dry_run: bool = False,
    ) -> dict[str, Any]:
        metadata_patch = _workspace_metadata(cwd=cwd, root_name=root_name)
        if not metadata_patch.get("workspace_key"):
            raise ValueError("scope-backfill requires a cwd or root_name that can resolve to a workspace")
        return database.backfill_episode_workspace_scope(
            episode_ids=episode_ids,
            metadata_patch=metadata_patch,
            dry_run=dry_run,
        )

    def reconcile_watch_roots(
        self,
        *,
        root_name: str | None = None,
        reason: str = "periodic_reconcile",
        host_agent_roots: bool | None = None,
        component_name: str = "watch-reconciler:service",
    ) -> dict[str, Any]:
        roots = [
            root
            for root in database.list_monitored_roots(watch_enabled=True)
            if root.get("enabled")
            and root.get("watch_enabled")
            and (root_name is None or root.get("name") == root_name)
            and _root_matches_host_agent_filter(root, host_agent_roots)
        ]
        if not roots:
            payload = {"status": "no_enabled_watch_roots", "reason": reason, "roots": 0, "results": []}
            database.record_runtime_component_heartbeat(
                name=component_name,
                status="idle",
                metadata=payload,
            )
            return payload

        results: list[dict[str, Any]] = []
        totals = {"files_seen": 0, "files_changed": 0, "files_deleted": 0, "jobs_queued": 0}
        for root in roots:
            try:
                result = self.sync_corpus(root_name=root["name"], reason=reason)
                results.append(result)
                for key in totals:
                    totals[key] += int(result.get(key) or 0)
            except Exception as exc:
                error = {"root_name": root.get("name"), "status": "error", "error": str(exc)}
                results.append(error)
                database.record_watch_error(root_name=root["name"], error=str(exc))
        status = "completed" if all(item.get("status") != "error" for item in results) else "partial"
        payload = {"status": status, "reason": reason, "roots": len(roots), **totals, "results": results}
        database.record_runtime_component_heartbeat(
            name=component_name,
            status="running" if status == "completed" else "error",
            metadata=payload,
        )
        return payload

    def run_watch(self, *, root_name: str | None = None, interval_seconds: float = 2.0) -> dict[str, Any]:
        if not _load_watch_roots(root_name):
            return {"status": "no_enabled_roots", "root_name": root_name}

        watcher = create_corpus_watcher(
            lambda: _load_watch_roots(root_name),
            on_change=self._handle_watch_event,
            interval_seconds=interval_seconds,
            debounce_seconds=_configured_watcher_debounce_seconds(),
            stability_quiet_seconds=_configured_stability_quiet_seconds(),
            max_queue_size=_configured_watcher_max_queue_size(),
        )
        last_reconcile_at = 0.0
        if _configured_reconcile_on_start():
            self.reconcile_watch_roots(root_name=root_name, reason="startup_reconcile")
            last_reconcile_at = time.monotonic()
        watcher.poll_once(seed=True)
        while True:
            for root in _load_watch_roots(root_name):
                database.record_watcher_heartbeat(root_name=root.name)
            watcher.poll_once()
            reconcile_interval = _configured_reconcile_interval_seconds()
            if reconcile_interval > 0 and time.monotonic() - last_reconcile_at >= reconcile_interval:
                self.reconcile_watch_roots(root_name=root_name, reason="periodic_reconcile")
                last_reconcile_at = time.monotonic()
            time.sleep(interval_seconds)

    def run_corpus_backfill(
        self,
        *,
        kind: str = "all",
        limit: int = 10,
        workers: int = 1,
        root_name: str | None = None,
        host_agent_roots: bool | None = None,
    ) -> dict[str, Any]:
        from . import worker

        cancelled = database.cancel_duplicate_corpus_jobs(root_name=root_name)
        claim_kwargs: dict[str, Any] = {
            "limit": limit,
            "worker_id": f"flux-kb-backfill-{workers}",
            "root_name": root_name,
        }
        if host_agent_roots is not None:
            claim_kwargs["host_agent_roots"] = host_agent_roots
        claimed = database.claim_corpus_jobs(**claim_kwargs)
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
            elif process_result.status == "retrying_locked":
                if int(job.get("attempts") or 0) >= _configured_lock_max_attempts():
                    database.block_corpus_job(
                        job_id=job["id"],
                        error=process_result.message or "blocked_locked",
                        status="blocked_locked",
                    )
                    blocked += 1
                else:
                    database.retry_corpus_job(
                        job_id=job["id"],
                        error=process_result.message or "retrying_locked",
                        cooldown_seconds=_configured_lock_retry_cooldown_seconds(),
                        status="retrying_locked",
                    )
                    retried += 1
            else:
                database.retry_corpus_job(
                    job_id=job["id"],
                    error=process_result.message or process_result.status,
                    cooldown_seconds=300,
                )
                retried += 1
        repaired = database.repair_extracted_corpus_asset_statuses(root_name=root_name)
        cleared_errors = database.clear_completed_corpus_job_errors(root_name=root_name)
        database.record_audit_event(
            event_type="corpus.backfill",
            details={
                "kind": kind,
                "root_name": root_name,
                "host_agent_roots": host_agent_roots,
                "claimed": len(claimed),
                "completed": completed,
                "blocked": blocked,
                "retried": retried,
                "cancelled_duplicate": cancelled["cancelled"],
                "repaired_assets": repaired["repaired"],
                "cleared_completed_errors": cleared_errors["cleared"],
                "workers": workers,
            },
        )
        return {
            "kind": kind,
            "root_name": root_name,
            "host_agent_roots": host_agent_roots,
            "claimed": len(claimed),
            "completed": completed,
            "blocked": blocked,
            "retried": retried,
            "cancelled_duplicate": cancelled["cancelled"],
            "repaired_assets": repaired["repaired"],
            "cleared_completed_errors": cleared_errors["cleared"],
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
        host_agent_roots: bool | None = None,
        component_name: str = "corpus-worker:docker",
    ) -> dict[str, Any]:
        runs = 0
        last_result: dict[str, Any] | None = None
        while True:
            runs += 1
            database.record_runtime_component_heartbeat(
                name=component_name,
                status="running",
                metadata={
                    "kind": kind,
                    "limit": limit,
                    "workers": workers,
                    "root_name": root_name,
                    "host_agent_roots": host_agent_roots,
                    "runs": runs,
                },
            )
            last_result = self.run_corpus_backfill(
                kind=kind,
                limit=limit,
                workers=workers,
                root_name=root_name,
                host_agent_roots=host_agent_roots,
            )
            if host_agent_roots is not True:
                try:
                    from . import mail_ingestion

                    last_result["mail_sync"] = mail_ingestion.sync_due_mail_profiles(limit=limit, worker_id=component_name)
                except Exception as exc:
                    last_result["mail_sync"] = {"status": "failed", "error": str(exc)}
            database.record_runtime_component_heartbeat(
                name=component_name,
                status="running",
                metadata={"last_result": last_result, "runs": runs},
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
                    "host_agent_roots": host_agent_roots,
                    "runs": runs,
                    "last_result": last_result,
                }
            time.sleep(interval_seconds)

    def _handle_watch_event(self, event: WatchEvent) -> None:
        try:
            database.record_watch_event(root_name=event.root_name)
            self.sync_corpus(root_name=event.root_name, path=event.path, reason="watch_event")
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


def _format_corpus_search_item(item: dict[str, Any]) -> dict[str, Any]:
    result = dict(item)
    result.setdefault("excerpt", str(result.get("summary") or ""))
    source_path = str(result.get("source_path") or "").replace("\\", "/")
    if source_path.endswith("/manifest.json") or source_path == "manifest.json":
        manifest = _parse_json_object(result.get("summary"))
        if manifest:
            subject = str(manifest.get("subject") or "").strip()
            if subject:
                result["title"] = f"Mail: {subject}"
            summary = _mail_manifest_summary(manifest)
            if summary:
                result["summary"] = summary
                result["excerpt"] = summary
    return result


def _parse_json_object(value: Any) -> dict[str, Any]:
    if isinstance(value, dict):
        return value
    if not isinstance(value, str):
        return {}
    try:
        payload = json.loads(value)
    except json.JSONDecodeError:
        return {}
    return payload if isinstance(payload, dict) else {}


def _mail_manifest_summary(manifest: dict[str, Any]) -> str:
    parts: list[str] = []
    sender = _display_address(manifest.get("sender"))
    if sender:
        parts.append(f"From {sender}")
    recipients = manifest.get("recipients")
    if isinstance(recipients, list) and recipients:
        display_recipients = ", ".join(_display_address(item) for item in recipients[:3] if _display_address(item))
        if display_recipients:
            parts.append(f"to {display_recipients}")
    received_at = str(manifest.get("received_at") or "").strip()
    if received_at:
        parts.append(f"received {received_at}")
    source_folder = str(manifest.get("source_folder") or "").strip()
    if source_folder:
        parts.append(f"folder {source_folder}")
    if manifest.get("attachment_count") is not None:
        count = int(manifest.get("attachment_count") or 0)
        parts.append(f"{count} attachment{'s' if count != 1 else ''}")
    return "; ".join(parts) + "." if parts else ""


def _resolve_retrieval_scope(*, cwd: str | None, root_name: str | None, scope_mode: str) -> RetrievalScope:
    mode = _normalize_scope_mode(scope_mode)
    if mode == "global":
        return RetrievalScope(mode=mode)

    cleaned_cwd = _clean_optional_text(cwd)
    cleaned_root_name = _clean_optional_text(root_name)
    root = _retrieval_root(cleaned_root_name, cleaned_cwd)
    workspace = _workspace_identity(cwd=cleaned_cwd, root_name=cleaned_root_name, root=root)
    if root is None:
        return RetrievalScope(
            mode=mode,
            cwd=cleaned_cwd,
            root_name=cleaned_root_name,
            workspace_root=workspace.get("workspace_root"),
            workspace_key=workspace.get("workspace_key"),
        )
    return RetrievalScope(
        mode=mode,
        cwd=cleaned_cwd,
        root_name=str(root.get("name") or cleaned_root_name or ""),
        root_path=str(root.get("root_path") or "") or None,
        workspace_root=workspace.get("workspace_root"),
        workspace_key=workspace.get("workspace_key"),
    )


def _normalize_scope_mode(scope_mode: str) -> str:
    mode = str(scope_mode or "local_first").strip().lower()
    if mode == "expanded":
        mode = "workspace_boosted"
    if mode not in {"local_first", "local_only", "global", "workspace_boosted"}:
        raise ValueError("scope_mode must be one of: local_first, local_only, global, workspace_boosted")
    return mode


def _clean_optional_text(value: str | None) -> str | None:
    text = str(value or "").strip()
    return text or None


def _retrieval_root(root_name: str | None, cwd: str | None) -> dict[str, Any] | None:
    try:
        roots = database.list_monitored_roots()
    except Exception:
        roots = []
    enabled_roots = [root for root in roots if root.get("enabled", True)]
    if root_name:
        for root in enabled_roots:
            if root.get("name") == root_name:
                return root
        return None
    if cwd:
        matching = [
            root
            for root in enabled_roots
            if _path_is_under_root(cwd, str(root.get("root_path") or ""))
        ]
        if matching:
            return sorted(matching, key=lambda item: len(str(item.get("root_path") or "")), reverse=True)[0]
    return None


def _enrich_workspace_metadata(
    metadata: dict[str, Any],
    *,
    cwd: str | None = None,
    root_name: str | None = None,
) -> dict[str, Any]:
    result = dict(metadata)
    metadata_cwd = _clean_optional_text(cwd) or _clean_optional_text(result.get("cwd"))
    metadata_root_name = _clean_optional_text(root_name) or _clean_optional_text(result.get("root_name"))
    result.update(_workspace_metadata(cwd=metadata_cwd, root_name=metadata_root_name))
    return result


def _workspace_metadata(*, cwd: str | None = None, root_name: str | None = None) -> dict[str, str]:
    cleaned_cwd = _clean_optional_text(cwd)
    cleaned_root_name = _clean_optional_text(root_name)
    root = _retrieval_root(cleaned_root_name, cleaned_cwd)
    return _workspace_identity(cwd=cleaned_cwd, root_name=cleaned_root_name, root=root)


def _workspace_identity(
    *,
    cwd: str | None,
    root_name: str | None,
    root: dict[str, Any] | None,
) -> dict[str, str]:
    metadata: dict[str, str] = {}
    if cwd:
        metadata["cwd"] = cwd
    if root is not None:
        resolved_root_name = str(root.get("name") or root_name or "").strip()
        root_path = str(root.get("root_path") or "").strip()
        if resolved_root_name:
            metadata["root_name"] = resolved_root_name
            metadata["workspace_key"] = f"root:{resolved_root_name}"
        if root_path:
            metadata["workspace_root"] = root_path
        return metadata
    if root_name:
        metadata["root_name"] = root_name

    workspace_root = _git_repo_root(cwd) if cwd else None
    if not workspace_root and cwd:
        workspace_root = cwd
    if workspace_root:
        metadata["workspace_root"] = workspace_root
        metadata["workspace_key"] = f"path:{_normalised_workspace_path(workspace_root)}"
    return metadata


def _git_repo_root(cwd: str | None) -> str | None:
    if not cwd:
        return None
    try:
        completed = run_no_window(
            ["git", "rev-parse", "--show-toplevel"],
            cwd=str(cwd),
            text=True,
            capture_output=True,
            timeout=5,
            check=False,
        )
    except Exception:
        return None
    if completed.returncode != 0:
        return None
    root = completed.stdout.strip()
    return root or None


def _normalised_workspace_path(path: str) -> str:
    cleaned = str(path).strip().rstrip("\\/")
    return cleaned.replace("\\", "/").lower()


def _has_lexical_or_fuzzy_evidence(results: list[dict[str, Any]]) -> bool:
    for result in results:
        streams = {str(stream) for stream in result.get("streams", [])}
        if any("lexical" in stream or "fuzzy" in stream for stream in streams):
            return True
    return False


def _is_strong_cross_workspace_evidence(item: dict[str, Any]) -> bool:
    streams = {str(stream) for stream in item.get("streams", [])}
    if any("lexical" in stream or "fuzzy" in stream for stream in streams):
        return True
    if any("vector" in stream for stream in streams):
        return float(item.get("score") or 0.0) >= STRONG_VECTOR_MIN_SCORE
    return False


def _result_identity(item: dict[str, Any]) -> tuple[str, str]:
    kind = str(item.get("logical_kind") or item.get("kind") or "")
    identifier = str(item.get("id") or item.get("asset_id") or item.get("source_path") or item.get("title") or "")
    return kind, identifier


def _dedupe_search_results(items: list[dict[str, Any]]) -> list[dict[str, Any]]:
    deduped: list[dict[str, Any]] = []
    seen: set[tuple[str, str]] = set()
    for item in items:
        identity = _result_identity(item)
        if identity in seen:
            continue
        seen.add(identity)
        deduped.append(item)
    return deduped


def _with_scope_score_boost(item: dict[str, Any], boost: float) -> dict[str, Any]:
    result = dict(item)
    base_score = float(result.get("score") or 0.0)
    result["base_score"] = base_score
    result["scope_score_boost"] = boost
    result["score"] = base_score * boost
    return result


def _tag_retrieval_scope(item: dict[str, Any], label: str, *, scope: RetrievalScope | None = None) -> dict[str, Any]:
    result = dict(item)
    result["retrieval_scope"] = _truthful_retrieval_label(result, label, scope=scope)
    if scope:
        if scope.cwd:
            result["retrieval_cwd"] = scope.cwd
        if scope.root_name:
            result["retrieval_root_name"] = scope.root_name
        if scope.root_path:
            result["retrieval_root_path"] = scope.root_path
        if scope.workspace_root:
            result["retrieval_workspace_root"] = scope.workspace_root
        if scope.workspace_key:
            result["retrieval_workspace_key"] = scope.workspace_key
    return result


def _truthful_retrieval_label(item: dict[str, Any], label: str, *, scope: RetrievalScope | None = None) -> str:
    if label != "cross_workspace":
        return label
    if _matches_scope_provenance(item, scope):
        return "local"
    if _has_known_workspace_provenance(item):
        return "cross_workspace"
    return "unscoped_global"


def _matches_scope_provenance(item: dict[str, Any], scope: RetrievalScope | None) -> bool:
    if scope is None:
        return False
    metadata = item.get("metadata") if isinstance(item.get("metadata"), dict) else {}
    item_workspace_key = _clean_optional_text(metadata.get("workspace_key"))
    if item_workspace_key and scope.workspace_key:
        return item_workspace_key == scope.workspace_key
    item_root_name = _clean_optional_text(item.get("root_name")) or _clean_optional_text(metadata.get("root_name"))
    if item_root_name and scope.root_name:
        return item_root_name == scope.root_name
    item_cwd = _clean_optional_text(metadata.get("cwd"))
    if item_cwd and (scope.root_path or scope.cwd):
        return _path_is_under_root(item_cwd, scope.root_path or scope.cwd or "")
    return False


def _has_known_workspace_provenance(item: dict[str, Any]) -> bool:
    metadata = item.get("metadata") if isinstance(item.get("metadata"), dict) else {}
    return bool(
        _clean_optional_text(item.get("root_name"))
        or _clean_optional_text(metadata.get("root_name"))
        or _clean_optional_text(metadata.get("workspace_key"))
        or _clean_optional_text(metadata.get("workspace_root"))
        or _clean_optional_text(metadata.get("cwd"))
    )


def _redact_metadata(value: Any) -> tuple[Any, list[RedactionFinding]]:
    if isinstance(value, str):
        return redact_text(value)
    if isinstance(value, list):
        redacted_items: list[Any] = []
        findings: list[RedactionFinding] = []
        for item in value:
            redacted_item, item_findings = _redact_metadata(item)
            redacted_items.append(redacted_item)
            findings.extend(item_findings)
        return redacted_items, findings
    if isinstance(value, dict):
        redacted: dict[str, Any] = {}
        findings: list[RedactionFinding] = []
        for key, item in value.items():
            redacted_key, key_findings = redact_text(str(key))
            redacted_item, item_findings = _redact_metadata(item)
            redacted[redacted_key] = redacted_item
            findings.extend(key_findings)
            findings.extend(item_findings)
        return redacted, findings
    return value, []


def _is_current_evidence(item: dict[str, Any]) -> bool:
    lifecycle = item.get("lifecycle")
    if not isinstance(lifecycle, dict):
        return True
    if lifecycle.get("current") is False:
        return False
    return str(lifecycle.get("state") or "active") not in {"superseded", "contradicted", "retired"}


def _display_address(value: Any) -> str:
    text = str(value or "").strip()
    if "<" in text:
        text = text.split("<", 1)[0].strip()
    return text


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


def _configured_reconcile_on_start() -> bool:
    try:
        return bool(SettingsService().resolve("watcher.reconcile_on_start").raw_value)
    except Exception:
        return True


def _configured_reconcile_interval_seconds() -> int:
    try:
        return int(SettingsService().resolve("watcher.reconcile_interval_seconds").raw_value)
    except Exception:
        return 3600


def _configured_watcher_debounce_seconds() -> float:
    try:
        return float(SettingsService().resolve("watcher.debounce_seconds").raw_value)
    except Exception:
        return 0.75


def _configured_watcher_max_queue_size() -> int:
    try:
        return int(SettingsService().resolve("watcher.max_queue_size").raw_value)
    except Exception:
        return 1000


def _configured_stability_quiet_seconds() -> float:
    try:
        return float(SettingsService().resolve("watcher.stability_quiet_seconds").raw_value)
    except Exception:
        return 2.0


def _configured_large_file_stability_quiet_seconds() -> float:
    try:
        return float(SettingsService().resolve("watcher.large_file_stability_quiet_seconds").raw_value)
    except Exception:
        return 10.0


def _configured_lock_retry_cooldown_seconds() -> int:
    try:
        return int(SettingsService().resolve("worker.lock_retry_cooldown_seconds").raw_value)
    except Exception:
        return 300


def _configured_lock_max_attempts() -> int:
    try:
        return int(SettingsService().resolve("worker.lock_max_attempts").raw_value)
    except Exception:
        return 3


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


def _root_matches_host_agent_filter(root: dict[str, Any], host_agent_roots: bool | None) -> bool:
    if host_agent_roots is None:
        return True
    metadata = root.get("metadata") or {}
    is_host_root = metadata.get("host_access") == "host_agent" or _looks_windows(str(root.get("root_path") or ""))
    return is_host_root is host_agent_roots


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
