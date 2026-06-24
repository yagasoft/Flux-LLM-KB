from __future__ import annotations

from dataclasses import dataclass
import base64
import hashlib
import json
from pathlib import Path
from pathlib import PurePosixPath, PureWindowsPath
import re
import tempfile
import time
from typing import Any

from .acceleration import (
    BENCHMARK_FIXTURES,
    FAMILY_DEFAULT_CAPS,
    JOB_FAMILIES,
    collect_acceleration_status,
    job_family_for_type,
    kind_to_job_families,
)
from .crawler import CorpusPolicy, scan_path
from . import database
from .glob_policy import effective_glob_policy
from .processes import run_no_window
from .redaction import RedactionFinding, redact_text
from .retrieval_explain import enrich_search_result
from .result_details import collapse_mail_spool_search_results, decorate_corpus_search_item
from .scoring import ContextCandidate, pack_context, pack_context_with_trace
from .settings import SettingsService
from .versioning import collapse_version_families
from .watcher import WatchEvent, WatchRoot, create_corpus_watcher, probe_watcher_backend


LOCAL_SCOPE_SCORE_BOOST = 1.15
STRONG_VECTOR_MIN_SCORE = 0.35
ALLOWED_RETRIEVAL_LOGICAL_KINDS = {"episode", "file", "mail"}


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
        filters: dict[str, Any] | None = None,
    ) -> list[dict[str, Any]]:
        normalized_filters = normalize_retrieval_filters(filters) if filters is not None else None
        raw_results = self._search_raw(query, limit=limit, cwd=cwd, root_name=root_name, scope_mode=scope_mode)
        filtered_results, _excluded = _apply_retrieval_filters(raw_results, normalized_filters)
        return _enrich_search_results(query, filtered_results, retrieval_filters=normalized_filters)

    def _search_raw(
        self,
        query: str,
        *,
        limit: int,
        cwd: str | None,
        root_name: str | None,
        scope_mode: str,
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
        filters: dict[str, Any] | None = None,
    ) -> str:
        if token_budget is None:
            token_budget = _configured_token_budget()
        search_kwargs: dict[str, Any] = {
            "limit": 10,
            "cwd": cwd,
            "root_name": root_name,
            "scope_mode": scope_mode,
        }
        if filters is not None:
            search_kwargs["filters"] = filters
        search_results = self.search(query, **search_kwargs)
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

    def explain(
        self,
        query: str,
        limit: int = 5,
        token_budget: int | None = None,
        cwd: str | None = None,
        root_name: str | None = None,
        scope_mode: str = "local_first",
        filters: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        if token_budget is None:
            token_budget = _configured_token_budget()
        result_limit = max(1, min(int(limit or 5), 50))
        if filters is None:
            search_results = self.search(
                query,
                limit=max(result_limit, 10),
                cwd=cwd,
                root_name=root_name,
                scope_mode=scope_mode,
            )
            return {
                "query": query,
                "results": search_results[:result_limit],
                "brief": _brief_selection_trace(search_results, token_budget=token_budget),
            }
        normalized_filters = normalize_retrieval_filters(filters) if filters is not None else None
        raw_results = self._search_raw(
            query,
            limit=max(result_limit, 10),
            cwd=cwd,
            root_name=root_name,
            scope_mode=scope_mode,
        )
        filtered_results, excluded = _apply_retrieval_filters(raw_results, normalized_filters)
        search_results = _enrich_search_results(query, filtered_results, retrieval_filters=normalized_filters)
        payload = {
            "query": query,
            "results": search_results[:result_limit],
            "brief": _brief_selection_trace(search_results, token_budget=token_budget),
        }
        if normalized_filters is not None:
            payload["filters"] = normalized_filters
            payload["filter_trace"] = {"excluded": excluded}
            if normalized_filters.get("include_suppressed"):
                payload["suppression"] = _suppression_trace(raw_results)
        return payload

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

    def refresh_semantic_duplicate_clusters(
        self,
        *,
        memory_class: str = "all",
        root_name: str | None = None,
        threshold: float | None = None,
        limit: int = 1000,
    ) -> dict[str, Any]:
        return database.refresh_semantic_duplicate_clusters(
            memory_class=memory_class,
            root_name=root_name,
            threshold=threshold,
            limit=limit,
        )

    def list_semantic_duplicate_clusters(
        self,
        *,
        memory_class: str | None = None,
        root_name: str | None = None,
        limit: int = 50,
    ) -> dict[str, Any]:
        return database.list_semantic_duplicate_clusters(
            memory_class=memory_class,
            root_name=root_name,
            limit=limit,
        )

    def embedding_status(self, *, root_name: str | None = None) -> dict[str, Any]:
        return database.embedding_status(root_name=root_name)

    def enqueue_embedding_jobs(
        self,
        *,
        owner_class: str = "all",
        root_name: str | None = None,
        stale_only: bool = True,
        limit: int = 100,
    ) -> dict[str, Any]:
        return database.enqueue_embedding_jobs(
            owner_class=owner_class,
            root_name=root_name,
            stale_only=stale_only,
            limit=limit,
        )

    def refresh_embeddings(
        self,
        *,
        owner_class: str = "all",
        root_name: str | None = None,
        stale_only: bool = True,
        limit: int = 100,
    ) -> dict[str, Any]:
        return database.refresh_embeddings(
            owner_class=owner_class,
            root_name=root_name,
            stale_only=stale_only,
            limit=limit,
        )

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
            **_configured_container_limits(),
            hash_parallelism=_configured_hash_parallelism(),
            manifest_lookup=_manifest_lookup(root["name"]),
            stability_quiet_seconds=_configured_stability_quiet_seconds() if reason == "watch_event" else 0.0,
            large_file_stability_quiet_seconds=_configured_large_file_stability_quiet_seconds() if reason == "watch_event" else 0.0,
        )
        plan = scan_path(root["root_path"], policy, target_path=path)
        return database.persist_crawl_plan(root_name=root["name"], plan=plan, dry_run=dry_run, reason=reason)

    def watch_probe(self, *, timeout_seconds: float = 2.0) -> dict[str, Any]:
        return probe_watcher_backend(
            backend_policy=_configured_watcher_backend(),
            timeout_seconds=max(0.1, float(timeout_seconds or 2.0)),
        )

    def worker_status(self, *, family: str = "all") -> dict[str, Any]:
        rows = collect_acceleration_status()["worker_families"]
        normalized = str(family or "all").lower()
        if normalized != "all":
            rows = [row for row in rows if row.get("family") == normalized]
        return {"family": normalized, "families": rows}

    def watch_events(self, *, limit: int = 50) -> dict[str, Any]:
        return {"events": database.list_watch_events(limit=limit)}

    def benchmark_history(
        self,
        *,
        fixture: str | None = None,
        mode: str | None = None,
        label: str | None = None,
        warm_state: str | None = None,
        limit: int = 20,
    ) -> dict[str, Any]:
        normalized = None if fixture in {None, "", "all"} else str(fixture)
        normalized_mode = None if mode in {None, "", "all"} else str(mode)
        return {
            "fixture": normalized or "all",
            "mode": normalized_mode or "all",
            "runs": database.list_benchmark_runs(
                fixture=normalized,
                mode=normalized_mode,
                label=label or None,
                warm_state=warm_state or None,
                limit=limit,
            ),
        }

    def run_benchmark(
        self,
        *,
        fixture: str = "all",
        files: int = 10,
        mode: str = "scan",
        passes: int = 1,
        label: str | None = None,
        compare_label: str | None = None,
        workers: int = 1,
        family: str = "all",
    ) -> dict[str, Any]:
        fixture_names = [item["name"] for item in BENCHMARK_FIXTURES]
        requested = str(fixture or "all")
        names = fixture_names if requested == "all" else [requested]
        unknown = [name for name in names if name not in fixture_names]
        if unknown:
            raise ValueError(f"unknown benchmark fixture: {unknown[0]}")
        normalized_mode = _normalize_benchmark_mode(mode, allow_all=True)
        modes = ["scan", "soak", "watcher"] if normalized_mode == "all" else [normalized_mode]
        normalized_family = _normalize_benchmark_family(family)
        file_count = max(1, min(int(files or 10), 500))
        pass_count = max(1, min(int(passes or 1), 10))
        worker_count = max(1, min(int(workers or 1), 32))
        runs: list[dict[str, Any]] = []
        for name in names:
            for selected_mode in modes:
                if selected_mode == "scan":
                    runs.extend(
                        self._run_scan_benchmark(
                            name,
                            file_count,
                            passes=pass_count,
                            label=label,
                            compare_label=compare_label,
                            worker_count=worker_count,
                        )
                    )
                elif selected_mode == "soak":
                    runs.append(
                        self._run_soak_benchmark(
                            name,
                            file_count,
                            label=label,
                            compare_label=compare_label,
                            worker_count=worker_count,
                            family=normalized_family,
                        )
                    )
                elif selected_mode == "watcher":
                    runs.append(
                        self._run_watcher_benchmark(
                            name,
                            file_count,
                            label=label,
                            compare_label=compare_label,
                            worker_count=worker_count,
                        )
                    )
        return {
            "fixture": requested if requested != "all" else "all",
            "mode": normalized_mode,
            "files": file_count,
            "runs": runs,
            "recommendations": _benchmark_recommendations(runs),
        }

    def _run_scan_benchmark(
        self,
        fixture: str,
        files: int,
        *,
        passes: int,
        label: str | None,
        compare_label: str | None,
        worker_count: int,
    ) -> list[dict[str, Any]]:
        runs: list[dict[str, Any]] = []
        with tempfile.TemporaryDirectory(prefix="flux-kb-benchmark-") as temp_dir:
            root = Path(temp_dir)
            _write_benchmark_fixture(root, fixture, files)
            manifest: dict[str, dict[str, Any]] = {}
            hash_parallelism = _configured_hash_parallelism()
            for pass_index in range(1, passes + 1):
                started = time.perf_counter()
                plan = scan_path(
                    root,
                    CorpusPolicy(
                        root_path=root,
                        hash_parallelism=hash_parallelism,
                        manifest_lookup=lambda relative_path, store=manifest: store.get(relative_path),
                    ),
                )
                elapsed_ms = max(0, int((time.perf_counter() - started) * 1000))
                for asset in plan.assets:
                    manifest[asset.relative_path] = {
                        "path": asset.relative_path,
                        "size_bytes": asset.size_bytes,
                        "mtime_ns": asset.mtime_ns,
                        "quick_hash": asset.quick_hash,
                        "content_hash": asset.content_hash,
                    }
                manifest_skipped = sum(1 for asset in plan.assets if asset.metadata.get("manifest_skipped_unchanged"))
                per_file_ms = max(0, int(elapsed_ms / max(1, len(plan.assets))))
                family_breakdown = _benchmark_family_breakdown(plan)
                warm_state = "cold" if pass_index == 1 else "warm"
                metadata = {
                    "provider": "synthetic",
                    "path_scope": "temporary",
                    "watcher_backend": _configured_watcher_backend(),
                    "hash_parallelism": hash_parallelism,
                }
                recorded = database.record_benchmark_run(
                    fixture=fixture,
                    mode="scan",
                    label=label,
                    compare_label=compare_label,
                    file_count=len(plan.assets),
                    elapsed_ms=elapsed_ms,
                    timings_ms=[per_file_ms for _asset in plan.assets],
                    warm_state=warm_state,
                    pass_index=pass_index,
                    hash_parallelism=hash_parallelism,
                    worker_count=worker_count,
                    manifest_skipped_unchanged=manifest_skipped,
                    cache_hits=manifest_skipped,
                    cache_misses=max(0, len(plan.assets) - manifest_skipped),
                    jobs_queued=len(plan.deferred_jobs),
                    jobs_completed=len(plan.assets) - len(plan.deferred_jobs),
                    jobs_blocked=len(plan.errors),
                    worker_family_breakdown=family_breakdown,
                    metadata=metadata,
                )
                runs.append(
                    _benchmark_run_payload(
                        recorded=recorded,
                        fixture=fixture,
                        mode="scan",
                        file_count=len(plan.assets),
                        elapsed_ms=elapsed_ms,
                        jobs_queued=len(plan.deferred_jobs),
                        jobs_completed=len(plan.assets) - len(plan.deferred_jobs),
                        jobs_blocked=len(plan.errors),
                        worker_family_breakdown=family_breakdown,
                        warm_state=warm_state,
                        pass_index=pass_index,
                        hash_parallelism=hash_parallelism,
                        worker_count=worker_count,
                        manifest_skipped_unchanged=manifest_skipped,
                        cache_hits=manifest_skipped,
                        cache_misses=max(0, len(plan.assets) - manifest_skipped),
                        metadata=metadata,
                    )
                )
        return runs

    def _run_soak_benchmark(
        self,
        fixture: str,
        files: int,
        *,
        label: str | None,
        compare_label: str | None,
        worker_count: int,
        family: str,
    ) -> dict[str, Any]:
        tag = hashlib.sha256(f"{fixture}:{label or ''}:{time.time_ns()}".encode("utf-8")).hexdigest()[:16]
        family_caps = _configured_worker_caps()
        normalized_family = str(family or "all").lower()
        family_filter = None if normalized_family == "all" else [normalized_family]
        created = database.create_benchmark_soak_jobs(
            tag=tag,
            fixture=fixture,
            file_count=files,
            family=normalized_family,
            label=label,
        )
        completed = 0
        blocked = 0
        timings: list[int] = []
        family_breakdown: dict[str, dict[str, int]] = {}
        started = time.perf_counter()
        try:
            claimed = database.claim_corpus_jobs(
                limit=files,
                worker_id=f"flux-kb-benchmark-{tag}",
                job_families=family_filter,
                family_caps=family_caps,
            )
            for index, job in enumerate(claimed):
                duration_ms = max(1, index + 1)
                timings.append(duration_ms)
                job_family = str(job.get("job_family") or "general")
                row = family_breakdown.setdefault(job_family, {"claimed": 0, "completed": 0, "blocked": 0})
                row["claimed"] += 1
                telemetry = {
                    "benchmark_mode": "soak",
                    "benchmark_tag": tag,
                    "benchmark_fixture": fixture,
                    "benchmark_file_count": files,
                    "job_family": job_family,
                    "resource_class": job.get("resource_class"),
                }
                payload = job.get("payload") if isinstance(job.get("payload"), dict) else {}
                if payload.get("benchmark_outcome") == "blocked":
                    database.block_corpus_job(
                        job_id=job["id"],
                        error="benchmark synthetic blocked",
                        status="blocked_benchmark",
                        duration_ms=duration_ms,
                        telemetry=telemetry,
                    )
                    row["blocked"] += 1
                    blocked += 1
                else:
                    database.complete_corpus_job(job_id=job["id"], duration_ms=duration_ms, telemetry=telemetry)
                    row["completed"] += 1
                    completed += 1
        finally:
            purged = database.purge_benchmark_soak_jobs(tag=tag)
        elapsed_ms = max(0, int((time.perf_counter() - started) * 1000))
        metadata = {
            "provider": "synthetic",
            "path_scope": "temporary",
            "benchmark_tag": tag,
            "family": normalized_family,
            "worker_caps": family_caps,
            "purged": purged.get("purged"),
        }
        recorded = database.record_benchmark_run(
            fixture=fixture,
            mode="soak",
            label=label,
            compare_label=compare_label,
            file_count=int(created.get("created") or files),
            elapsed_ms=elapsed_ms,
            timings_ms=timings,
            warm_state="warm",
            pass_index=1,
            hash_parallelism=_configured_hash_parallelism(),
            worker_count=worker_count,
            cache_hits=0,
            cache_misses=int(created.get("created") or files),
            jobs_queued=int(created.get("created") or files),
            jobs_completed=completed,
            jobs_blocked=blocked,
            worker_family_breakdown=family_breakdown,
            metadata=metadata,
        )
        return _benchmark_run_payload(
            recorded=recorded,
            fixture=fixture,
            mode="soak",
            file_count=int(created.get("created") or files),
            elapsed_ms=elapsed_ms,
            jobs_queued=int(created.get("created") or files),
            jobs_completed=completed,
            jobs_blocked=blocked,
            worker_family_breakdown=family_breakdown,
            warm_state="warm",
            pass_index=1,
            hash_parallelism=_configured_hash_parallelism(),
            worker_count=worker_count,
            cache_hits=0,
            cache_misses=int(created.get("created") or files),
            metadata=metadata,
        )

    def _run_watcher_benchmark(
        self,
        fixture: str,
        files: int,
        *,
        label: str | None,
        compare_label: str | None,
        worker_count: int,
    ) -> dict[str, Any]:
        started = time.perf_counter()
        probe = probe_watcher_backend(backend_policy=_configured_watcher_backend(), timeout_seconds=2.0)
        elapsed_ms = int(probe.get("latency_ms") or max(0, int((time.perf_counter() - started) * 1000)))
        metadata = {
            "provider": "synthetic",
            "path_scope": "temporary",
            "watcher_backend": probe.get("backend") or {},
            "watcher_events": probe.get("events") or {},
            "latency_ms": elapsed_ms,
        }
        recorded = database.record_benchmark_run(
            fixture=fixture,
            mode="watcher",
            label=label,
            compare_label=compare_label,
            file_count=files,
            elapsed_ms=elapsed_ms,
            timings_ms=[elapsed_ms],
            warm_state="warm",
            pass_index=1,
            hash_parallelism=_configured_hash_parallelism(),
            worker_count=worker_count,
            cache_hits=0,
            cache_misses=0,
            jobs_queued=0,
            jobs_completed=0,
            jobs_blocked=0,
            worker_family_breakdown={},
            metadata=metadata,
        )
        return _benchmark_run_payload(
            recorded=recorded,
            fixture=fixture,
            mode="watcher",
            file_count=files,
            elapsed_ms=elapsed_ms,
            jobs_queued=0,
            jobs_completed=0,
            jobs_blocked=0,
            worker_family_breakdown={},
            warm_state="warm",
            pass_index=1,
            hash_parallelism=_configured_hash_parallelism(),
            worker_count=worker_count,
            cache_hits=0,
            cache_misses=0,
            metadata=metadata,
        )

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
            backend_policy=_configured_watcher_backend(),
        )
        last_reconcile_at = 0.0
        if _configured_reconcile_on_start():
            self.reconcile_watch_roots(root_name=root_name, reason="startup_reconcile")
            last_reconcile_at = time.monotonic()
        watcher.poll_once(seed=True)
        while True:
            for root in _load_watch_roots(root_name):
                database.record_watcher_heartbeat(root_name=root.name, metadata={"watcher_backend": watcher.backend_status})
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
        job_families = kind_to_job_families(kind)
        claim_kwargs: dict[str, Any] = {
            "limit": limit,
            "worker_id": f"flux-kb-backfill-{workers}",
            "root_name": root_name,
        }
        if job_families is not None:
            claim_kwargs["job_families"] = list(job_families)
        if host_agent_roots is not None:
            claim_kwargs["host_agent_roots"] = host_agent_roots
        claim_kwargs["family_caps"] = _configured_worker_caps()
        claimed = database.claim_corpus_jobs(**claim_kwargs)
        completed = 0
        blocked = 0
        retried = 0
        for job in claimed:
            started = time.perf_counter()
            if job.get("job_type") == "corpus_embed":
                process_result = worker.process_embedding_job(job)
            else:
                process_result = worker.process_corpus_job(job)
            duration_ms = max(0, int((time.perf_counter() - started) * 1000))
            telemetry = {
                "job_family": job.get("job_family"),
                "resource_class": job.get("resource_class"),
                "result_status": process_result.status,
            }
            telemetry.update(process_result.telemetry or {})
            if process_result.status in {"indexed", "metadata_only"}:
                database.complete_corpus_job(job_id=job["id"], duration_ms=duration_ms, telemetry=telemetry)
                completed += 1
            elif process_result.status == "blocked_missing_dependency":
                database.block_corpus_job(
                    job_id=job["id"],
                    error=process_result.message or "blocked_missing_dependency",
                    duration_ms=duration_ms,
                    telemetry=telemetry,
                )
                blocked += 1
            elif process_result.status == "retrying_locked":
                if int(job.get("attempts") or 0) >= _configured_lock_max_attempts():
                    database.block_corpus_job(
                        job_id=job["id"],
                        error=process_result.message or "blocked_locked",
                        status="blocked_locked",
                        duration_ms=duration_ms,
                        telemetry=telemetry,
                    )
                    blocked += 1
                else:
                    database.retry_corpus_job(
                        job_id=job["id"],
                        error=process_result.message or "retrying_locked",
                        cooldown_seconds=_configured_lock_retry_cooldown_seconds(),
                        status="retrying_locked",
                        duration_ms=duration_ms,
                        telemetry=telemetry,
                    )
                    retried += 1
            else:
                database.retry_corpus_job(
                    job_id=job["id"],
                    error=process_result.message or process_result.status,
                    cooldown_seconds=300,
                    duration_ms=duration_ms,
                    telemetry=telemetry,
                )
                retried += 1
        repaired = database.repair_extracted_corpus_asset_statuses(root_name=root_name)
        cleared_errors = database.clear_completed_corpus_job_errors(root_name=root_name)
        database.record_audit_event(
            event_type="corpus.backfill",
            details={
                "kind": kind,
                "job_families": list(job_families) if job_families else None,
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
            "job_families": list(job_families) if job_families else None,
            "root_name": root_name,
            "host_agent_roots": host_agent_roots,
            "claimed": len(claimed),
            "completed": completed,
            "blocked": blocked,
            "retried": retried,
            "cancelled_duplicate": cancelled["cancelled"],
            "repaired_assets": repaired["repaired"],
            "cleared_completed_errors": cleared_errors["cleared"],
            "jobs": claimed,
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
            database.record_watch_event(
                root_name=event.root_name,
                action=event.action,
                path_hash=_watch_event_path_hash(event),
                metadata={"action": event.action},
            )
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


def normalize_retrieval_filters(filters: dict[str, Any] | None = None) -> dict[str, Any]:
    filters = filters or {}
    logical_kinds = _normalize_filter_values(filters.get("logical_kinds") or filters.get("kind"))
    invalid_kinds = [kind for kind in logical_kinds if kind not in ALLOWED_RETRIEVAL_LOGICAL_KINDS]
    if invalid_kinds:
        raise ValueError("logical_kinds must contain only: episode, file, mail")
    return {
        "logical_kinds": logical_kinds,
        "current_only": bool(filters.get("current_only", False)),
        "lifecycle_states": _normalize_filter_values(filters.get("lifecycle_states") or filters.get("lifecycle_state")),
        "include_suppressed": bool(filters.get("include_suppressed", False)),
    }


def _normalize_filter_values(value: Any) -> list[str]:
    if value is None:
        return []
    raw_values = value if isinstance(value, list) else [value]
    normalized: list[str] = []
    seen: set[str] = set()
    for item in raw_values:
        text = str(item or "").strip().lower().replace("-", "_")
        if not text or text in seen:
            continue
        seen.add(text)
        normalized.append(text)
    return sorted(normalized)


def _apply_retrieval_filters(
    results: list[dict[str, Any]],
    filters: dict[str, Any] | None,
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    if filters is None:
        return results, []
    included: list[dict[str, Any]] = []
    excluded: list[dict[str, Any]] = []
    for item in results:
        reason = _retrieval_filter_exclusion_reason(item, filters)
        if reason:
            excluded.append(_filter_excluded_item(item, reason=reason))
        else:
            included.append(item)
    return included, excluded


def _retrieval_filter_exclusion_reason(item: dict[str, Any], filters: dict[str, Any]) -> str | None:
    logical_kinds = set(filters.get("logical_kinds") or [])
    item_kind = str(item.get("logical_kind") or item.get("kind") or "").lower()
    if logical_kinds and item_kind not in logical_kinds:
        return "logical_kind"

    lifecycle_state = _item_lifecycle_state(item)
    lifecycle_states = set(filters.get("lifecycle_states") or [])
    if lifecycle_states and lifecycle_state not in lifecycle_states:
        return "lifecycle_state"

    if filters.get("current_only") and not _is_current_evidence(item):
        return "current_only"
    return None


def _item_lifecycle_state(item: dict[str, Any]) -> str:
    lifecycle = item.get("lifecycle") if isinstance(item.get("lifecycle"), dict) else {}
    return str(lifecycle.get("state") or item.get("lifecycle_state") or "active").lower().replace("-", "_")


def _filter_excluded_item(item: dict[str, Any], *, reason: str) -> dict[str, Any]:
    lifecycle = item.get("lifecycle") if isinstance(item.get("lifecycle"), dict) else {}
    excluded = {
        "id": str(item.get("id") or ""),
        "title": str(item.get("title") or item.get("id") or "Untitled"),
        "kind": str(item.get("logical_kind") or item.get("kind") or ""),
        "score": float(item.get("score") or 0.0),
        "reason": reason,
        "lifecycle_state": str(lifecycle.get("state") or item.get("lifecycle_state") or ""),
    }
    if item.get("source_path"):
        excluded["source_path"] = str(item.get("source_path"))
    return excluded


def _enrich_search_results(
    query: str,
    results: list[dict[str, Any]],
    *,
    retrieval_filters: dict[str, Any] | None = None,
) -> list[dict[str, Any]]:
    return [enrich_search_result(query, _with_retrieval_filters(item, retrieval_filters)) for item in results]


def _with_retrieval_filters(item: dict[str, Any], filters: dict[str, Any] | None) -> dict[str, Any]:
    if filters is None:
        return item
    result = dict(item)
    result["retrieval_filters"] = filters
    return result


def _suppression_trace(results: list[dict[str, Any]]) -> dict[str, Any]:
    exact_duplicates: list[dict[str, Any]] = []
    version_families: list[dict[str, Any]] = []
    semantic_duplicates: list[dict[str, Any]] = []
    for item in results:
        duplicate_count = _positive_int(item.get("duplicate_count"))
        if duplicate_count:
            exact = {
                "id": str(item.get("id") or ""),
                "title": str(item.get("title") or item.get("id") or "Untitled"),
                "suppressed_count": duplicate_count,
                "reason": "exact_content_duplicate",
            }
            if item.get("source_path"):
                exact["canonical_source_path"] = str(item.get("source_path"))
            if item.get("asset_id"):
                exact["canonical_asset_id"] = str(item.get("asset_id"))
            exact_duplicates.append(exact)

        version_family = item.get("version_family")
        if isinstance(version_family, dict) and _positive_int(version_family.get("suppressed_count")):
            family = {
                "id": str(item.get("id") or ""),
                "title": str(item.get("title") or item.get("id") or "Untitled"),
                "suppressed_count": _positive_int(version_family.get("suppressed_count")),
                "reason": "same_document_version_family",
            }
            for key in ("key", "canonical_source_path", "suppressed_source_paths"):
                if version_family.get(key) is not None:
                    family[key] = version_family.get(key)
            version_families.append(family)

        semantic_cluster = item.get("semantic_duplicate_cluster")
        if isinstance(semantic_cluster, dict) and _positive_int(semantic_cluster.get("suppressed_count")):
            semantic = {
                "id": str(item.get("id") or ""),
                "title": str(item.get("title") or item.get("id") or "Untitled"),
                "cluster_id": str(semantic_cluster.get("cluster_id") or ""),
                "suppressed_count": _positive_int(semantic_cluster.get("suppressed_count")),
                "reason": "semantic_near_duplicate",
            }
            for key in ("threshold", "max_similarity", "suppressed"):
                if semantic_cluster.get(key) is not None:
                    semantic[key] = semantic_cluster.get(key)
            semantic_duplicates.append(semantic)

    trace: dict[str, Any] = {}
    if exact_duplicates:
        trace["exact_duplicates"] = exact_duplicates
    if version_families:
        trace["version_families"] = version_families
    if semantic_duplicates:
        trace["semantic_duplicates"] = semantic_duplicates
    return trace


def _positive_int(value: Any) -> int:
    try:
        number = int(value or 0)
    except (TypeError, ValueError):
        return 0
    return max(0, number)


def _brief_selection_trace(search_results: list[dict[str, Any]], *, token_budget: int) -> dict[str, Any]:
    current_results = [item for item in search_results if _is_current_evidence(item)]
    excluded: list[dict[str, Any]] = []
    packing_results = search_results
    if current_results:
        packing_results = current_results
        excluded.extend(_brief_excluded_item(item, reason="non_current") for item in search_results if item not in current_results)
    candidates = [
        ContextCandidate(
            id=str(item.get("id") or ""),
            title=str(item.get("title") or item.get("id") or "Untitled"),
            body=str(item.get("summary") or ""),
            score=float(item.get("score") or 0.0),
        )
        for item in packing_results
    ]
    packed = pack_context_with_trace(candidates, token_budget=token_budget)
    return {
        "text": packed.text,
        "token_budget": token_budget,
        "packed": list(packed.packed),
        "excluded": excluded + list(packed.excluded),
    }


def _brief_excluded_item(item: dict[str, Any], *, reason: str) -> dict[str, Any]:
    lifecycle = item.get("lifecycle") if isinstance(item.get("lifecycle"), dict) else {}
    return {
        "id": str(item.get("id") or ""),
        "title": str(item.get("title") or item.get("id") or "Untitled"),
        "score": float(item.get("score") or 0.0),
        "reason": reason,
        "lifecycle_state": str(lifecycle.get("state") or item.get("lifecycle_state") or ""),
    }


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
    if kind == "diagrams":
        return job_type == "corpus_extract_diagram"
    if kind in {"archives", "containers"}:
        return job_type in {"corpus_extract_archive", "corpus_extract_container"}
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


def _configured_watcher_backend() -> str:
    try:
        return str(SettingsService().resolve("watcher.backend").raw_value)
    except Exception:
        return "auto"


def _configured_hash_parallelism() -> int:
    try:
        return int(SettingsService().resolve("crawler.hash_parallelism").raw_value)
    except Exception:
        return 1


def _configured_worker_caps() -> dict[str, int]:
    settings = SettingsService()
    caps: dict[str, int] = {}
    for family, default in FAMILY_DEFAULT_CAPS.items():
        try:
            caps[family] = int(settings.resolve(f"acceleration.worker_cap.{family}").raw_value)
        except Exception:
            caps[family] = int(default)
    return caps


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


def _configured_container_limits() -> dict[str, int]:
    settings = SettingsService()
    defaults = CorpusPolicy(root_path=Path("."))
    keys = {
        "container_max_depth": "crawler.container_max_depth",
        "container_max_members": "crawler.container_max_members",
        "container_max_total_bytes": "crawler.container_max_total_bytes",
        "container_max_member_bytes": "crawler.container_max_member_bytes",
    }
    resolved: dict[str, int] = {}
    for field_name, setting_key in keys.items():
        try:
            resolved[field_name] = int(settings.resolve(setting_key).raw_value)
        except Exception:
            resolved[field_name] = int(getattr(defaults, field_name))
    return resolved


def _manifest_lookup(root_name: str):
    def lookup(relative_path: str) -> dict[str, Any] | None:
        try:
            return database.lookup_scan_manifest(root_name=root_name, path=relative_path)
        except Exception:
            return None

    return lookup


def _watch_event_path_hash(event: WatchEvent) -> str:
    digest = hashlib.sha256(f"{event.root_name}:{event.relative_path}".encode("utf-8", errors="ignore")).hexdigest()
    return f"sha256:{digest}"


def _benchmark_family_breakdown(plan: Any) -> dict[str, dict[str, int]]:
    breakdown: dict[str, dict[str, int]] = {}
    for asset in plan.assets:
        family = job_family_for_type(f"corpus_extract_{asset.file_kind}")
        row = breakdown.setdefault(family, {"files": 0, "deferred": 0, "inline": 0, "metadata_only": 0})
        row["files"] += 1
        if asset.extraction_tier == "deferred":
            row["deferred"] += 1
        elif asset.extraction_tier == "inline":
            row["inline"] += 1
        else:
            row["metadata_only"] += 1
    return breakdown


def _benchmark_run_payload(
    *,
    recorded: dict[str, Any],
    fixture: str,
    mode: str,
    file_count: int,
    elapsed_ms: int,
    jobs_queued: int,
    jobs_completed: int,
    jobs_blocked: int,
    worker_family_breakdown: dict[str, Any],
    warm_state: str,
    pass_index: int,
    hash_parallelism: int,
    worker_count: int,
    manifest_skipped_unchanged: int = 0,
    cache_hits: int,
    cache_misses: int,
    metadata: dict[str, Any],
) -> dict[str, Any]:
    return {
        "id": recorded.get("id"),
        "fixture": fixture,
        "mode": mode,
        "file_count": file_count,
        "elapsed_ms": elapsed_ms,
        "throughput_files_per_second": (file_count / (elapsed_ms / 1000.0)) if elapsed_ms else 0.0,
        "jobs_queued": jobs_queued,
        "jobs_completed": jobs_completed,
        "jobs_blocked": jobs_blocked,
        "worker_family_breakdown": worker_family_breakdown,
        "warm_state": warm_state,
        "pass_index": pass_index,
        "hash_parallelism": hash_parallelism,
        "worker_count": worker_count,
        "manifest_skipped_unchanged": manifest_skipped_unchanged,
        "cache_hits": cache_hits,
        "cache_misses": cache_misses,
        "metadata": metadata,
    }


def _benchmark_recommendations(runs: list[dict[str, Any]]) -> dict[str, Any]:
    best_scan = max(
        (run for run in runs if run.get("mode") == "scan"),
        key=lambda run: float(run.get("throughput_files_per_second") or 0.0),
        default=None,
    )
    best_soak = max(
        (run for run in runs if run.get("mode") == "soak"),
        key=lambda run: int(run.get("jobs_completed") or 0),
        default=None,
    )
    return {
        "settings_mutated": False,
        "observed_hash_parallelism": best_scan.get("hash_parallelism") if best_scan else None,
        "observed_worker_count": best_soak.get("worker_count") if best_soak else None,
        "basis": "diagnostic_observation_only",
    }


def _normalize_benchmark_mode(value: str | None, *, allow_all: bool = False) -> str:
    normalized = str(value or "scan").strip().lower()
    allowed = {"scan", "soak", "watcher"} | ({"all"} if allow_all else set())
    if normalized not in allowed:
        raise ValueError("benchmark mode must be scan, soak, watcher, or all")
    return normalized


def _normalize_benchmark_family(value: str | None) -> str:
    normalized = str(value or "all").strip().lower()
    if normalized != "all" and normalized not in JOB_FAMILIES:
        raise ValueError(f"benchmark family must be all or one of: {', '.join(JOB_FAMILIES)}")
    return normalized


def _write_benchmark_fixture(root: Path, fixture: str, files: int) -> None:
    root.mkdir(parents=True, exist_ok=True)
    writers = {
        "text-heavy": _write_text_fixture,
        "office-pdf-heavy": _write_office_pdf_fixture,
        "archive-container-heavy": _write_archive_fixture,
        "image-heavy": _write_image_fixture,
        "audio-video-heavy": _write_media_fixture,
    }
    writers[fixture](root, files)


def _write_text_fixture(root: Path, files: int) -> None:
    for index in range(files):
        (root / f"note-{index:04d}.md").write_text(
            f"# Synthetic note {index}\n\nThis benchmark fixture contains deterministic public-safe text.\n",
            encoding="utf-8",
        )


def _write_office_pdf_fixture(root: Path, files: int) -> None:
    extensions = [".pdf", ".docx", ".xlsx", ".pptx"]
    for index in range(files):
        (root / f"document-{index:04d}{extensions[index % len(extensions)]}").write_bytes(
            f"synthetic office/pdf fixture {index}".encode("utf-8")
        )


def _write_archive_fixture(root: Path, files: int) -> None:
    extensions = [".zip", ".tar", ".whl", ".jar"]
    for index in range(files):
        (root / f"package-{index:04d}{extensions[index % len(extensions)]}").write_bytes(b"PK synthetic container")


def _write_image_fixture(root: Path, files: int) -> None:
    png = base64.b64decode(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAIAAADZrBkAAAAAD0lEQVR4nGP8z8AARLJAgAEACPwDAaz3RyoAAAAASUVORK5CYII="
    )
    for index in range(files):
        (root / f"image-{index:04d}.png").write_bytes(png)


def _write_media_fixture(root: Path, files: int) -> None:
    extensions = [".mp3", ".wav", ".mp4", ".webm"]
    for index in range(files):
        (root / f"media-{index:04d}{extensions[index % len(extensions)]}").write_bytes(b"synthetic media")


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
