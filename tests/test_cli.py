import json
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace

from flux_llm_kb import cli


def test_cli_doctor_reports_missing_docker_without_failing():
    repo_root = Path(__file__).resolve().parents[1]

    result = subprocess.run(
        [sys.executable, "-m", "flux_llm_kb.cli", "doctor", "--json"],
        cwd=repo_root,
        text=True,
        capture_output=True,
        check=True,
    )
    payload = json.loads(result.stdout)

    assert payload["checks"]["python"]["ok"] is True
    assert "docker" in payload["checks"]
    assert payload["checks"]["docker"]["ok"] in {True, False}
    assert payload["summary"]["ok"] in {True, False}


def test_cli_lint_requires_vector_and_index_migrations():
    repo_root = Path(__file__).resolve().parents[1]

    result = subprocess.run(
        [sys.executable, "-m", "flux_llm_kb.cli", "lint"],
        cwd=repo_root,
        text=True,
        capture_output=True,
        check=True,
    )
    payload = json.loads(result.stdout)

    assert payload["ok"] is True
    assert payload["missing"] == []


def test_cli_search_uses_service_search(monkeypatch, capsys):
    from flux_llm_kb import service

    class FakeService:
        def search(self, query, limit=5):
            return [{"kind": "corpus_chunk", "title": query, "limit": limit}]

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert cli.main(["search", "dashboard", "--limit", "2"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload == [{"kind": "corpus_chunk", "title": "dashboard", "limit": 2}]


def test_cli_search_forwards_code_retrieval_filters(monkeypatch, capsys):
    from flux_llm_kb import service

    class FakeService:
        def search(self, query, **kwargs):
            return [{"query": query, "kwargs": kwargs}]

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert (
        cli.main(
            [
                "search",
                "build_invoice",
                "--root",
                "app",
                "--kind",
                "file",
                "--language",
                "python",
                "--relationship",
                "call",
                "--path-glob",
                "src/*.py",
                "--include-generated",
            ]
        )
        == 0
    )
    payload = json.loads(capsys.readouterr().out)

    assert payload[0]["kwargs"]["root_name"] == "app"
    assert payload[0]["kwargs"]["filters"] == {
        "logical_kinds": ["file"],
        "current_only": False,
        "lifecycle_states": [],
        "include_suppressed": False,
        "languages": ["python"],
        "relationships": ["call"],
        "path_globs": ["src/*.py"],
        "include_generated": True,
    }


def test_crawl_edit_preserves_root_metadata_and_strict_indexing(monkeypatch):
    from flux_llm_kb import database

    captured = {}
    monkeypatch.setattr(
        database,
        "get_monitored_root_by_identifier",
        lambda _root: {
            "id": "root-1",
            "name": "docs",
            "root_path": "E:/docs",
            "enabled": True,
            "recursive": True,
            "watch_enabled": False,
            "trust_rank": 500,
            "include_globs": ["*.md"],
            "exclude_globs": ["private/**"],
            "glob_mode": "extend",
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
            "metadata": {"owner": "ops", "strict_indexing": True},
        },
    )
    monkeypatch.setattr(database, "update_monitored_root", lambda **kwargs: captured.setdefault("kwargs", kwargs))

    payload = cli._crawl_edit(
        SimpleNamespace(
            root="docs",
            name=None,
            path=None,
            enable=False,
            disable=False,
            enable_watch=False,
            disable_watch=False,
            recursive=False,
            no_recursive=False,
            trust_rank=None,
            include_glob=None,
            exclude_glob=None,
            glob_mode=None,
            max_inline_bytes=None,
            heavy_threshold_bytes=None,
            strict_indexing=None,
        )
    )

    assert payload is captured["kwargs"]
    assert captured["kwargs"]["metadata"] == {"owner": "ops", "strict_indexing": True, "source": "cli"}


def test_cli_explain_uses_service_explain(monkeypatch, capsys):
    from flux_llm_kb import service

    class FakeService:
        def explain(self, query, limit=5, token_budget=None, cwd=None, root_name=None, scope_mode="local_first"):
            return {
                "query": query,
                "limit": limit,
                "token_budget": token_budget,
                "cwd": cwd,
                "root_name": root_name,
                "scope_mode": scope_mode,
            }

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert (
        cli.main(
            [
                "explain",
                "dashboard",
                "--limit",
                "2",
                "--token-budget",
                "900",
                "--cwd",
                "E:/Repo",
                "--root-name",
                "repo",
                "--scope-mode",
                "local_only",
            ]
        )
        == 0
    )
    payload = json.loads(capsys.readouterr().out)

    assert payload == {
        "query": "dashboard",
        "limit": 2,
        "token_budget": 900,
        "cwd": "E:/Repo",
        "root_name": "repo",
        "scope_mode": "local_only",
    }


def test_cli_search_and_explain_forward_retrieval_filters(monkeypatch, capsys):
    from flux_llm_kb import service

    calls = []

    class FakeService:
        def search(self, query, limit=5, root_name=None, filters=None):
            calls.append(("search", query, limit, root_name, filters))
            return [{"query": query, "filters": filters}]

        def explain(self, query, limit=5, token_budget=None, cwd=None, root_name=None, scope_mode="local_first", filters=None):
            calls.append(("explain", query, limit, token_budget, cwd, root_name, scope_mode, filters))
            return {"query": query, "filters": filters}

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert (
        cli.main(
            [
                "search",
                "build_invoice",
                "--kind",
                "file",
                "--root",
                "repo",
                "--file-kind",
                "code",
                "--language",
                "python",
                "--symbol-kind",
                "method",
                "--relationship",
                "definition",
                "--path-glob",
                "src/*.py",
                "--current-only",
                "--include-suppressed",
            ]
        )
        == 0
    )
    search_payload = json.loads(capsys.readouterr().out)

    assert cli.main(["explain", "rfp", "--kind", "file", "--lifecycle-state", "active"]) == 0
    explain_payload = json.loads(capsys.readouterr().out)

    assert search_payload[0]["filters"] == {
        "logical_kinds": ["file"],
        "current_only": True,
        "lifecycle_states": [],
        "include_suppressed": True,
        "file_kinds": ["code"],
        "languages": ["python"],
        "symbol_kinds": ["method"],
        "relationships": ["definition"],
        "path_globs": ["src/*.py"],
        "include_generated": False,
    }
    assert explain_payload["filters"] == {
        "logical_kinds": ["file"],
        "current_only": False,
        "lifecycle_states": ["active"],
        "include_suppressed": False,
        "include_generated": False,
    }
    assert calls[0][0] == "search"
    assert calls[0][3] == "repo"
    assert calls[1][0] == "explain"


def test_cli_semantic_duplicates_refresh_and_list_use_service(monkeypatch, capsys):
    from flux_llm_kb import service

    calls = []

    class FakeService:
        def refresh_semantic_duplicate_clusters(self, memory_class="all", root_name=None, threshold=None, limit=1000):
            calls.append(("refresh", memory_class, root_name, threshold, limit))
            return {"created_clusters": 2, "memory_class": memory_class}

        def list_semantic_duplicate_clusters(self, memory_class=None, root_name=None, limit=50):
            calls.append(("list", memory_class, root_name, limit))
            return {"clusters": [{"id": "cluster-1", "memory_class": memory_class}]}

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert (
        cli.main(
            [
                "semantic-duplicates",
                "refresh",
                "--memory-class",
                "corpus",
                "--root-name",
                "docs",
                "--threshold",
                "0.91",
                "--limit",
                "25",
            ]
        )
        == 0
    )
    refresh_payload = json.loads(capsys.readouterr().out)

    assert cli.main(["semantic-duplicates", "list", "--memory-class", "claim", "--limit", "7"]) == 0
    list_payload = json.loads(capsys.readouterr().out)

    assert refresh_payload == {"created_clusters": 2, "memory_class": "corpus"}
    assert list_payload == {"clusters": [{"id": "cluster-1", "memory_class": "claim"}]}
    assert calls == [
        ("refresh", "corpus", "docs", 0.91, 25),
        ("list", "claim", None, 7),
    ]


def test_cli_acceleration_status_uses_status_collector(monkeypatch, capsys):
    from flux_llm_kb import acceleration

    monkeypatch.setattr(
        acceleration,
        "collect_acceleration_status",
        lambda: {
            "capabilities": {"local_model": {"state": "disabled"}},
            "cache": {"root": "D:/FluxLLMKB/private/cache", "source": "install_root", "directories": {}},
            "worker_families": [{"family": "media", "pending": 2, "ocr_cache_hits": 3, "ocr_cache_misses": 1}],
        },
    )

    assert cli.main(["acceleration", "status"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload["cache"]["root"] == "D:/FluxLLMKB/private/cache"
    assert payload["worker_families"][0]["family"] == "media"
    assert payload["worker_families"][0]["ocr_cache_hits"] == 3
    assert payload["worker_families"][0]["ocr_cache_misses"] == 1


def test_cli_acceleration_reliability_commands_use_service(monkeypatch, capsys):
    from flux_llm_kb import service

    calls = []

    class FakeService:
        def indexer_reliability_status(self, **kwargs):
            calls.append(("status", kwargs))
            return {"readiness": "partial", "scope": kwargs}

        def run_indexer_reliability(self, **kwargs):
            calls.append(("run", kwargs))
            return {"readiness": "ready", "run": kwargs, "settings_mutated": False}

        def operator_evidence(self, **kwargs):
            calls.append(("evidence", kwargs))
            return {"settings_mutated": False, "gates": {"vss_snapshot": {"state": "hold"}}}

        def indexer_root_reliability(self, root_name):
            calls.append(("root", root_name))
            return {"root_name": root_name, "readiness": "partial"}

        def indexer_reliability_roots(self, **kwargs):
            calls.append(("roots", kwargs))
            return {"roots": [{"root_name": "docs", "readiness": "ready"}], "settings_mutated": False}

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert cli.main(["acceleration", "reliability", "status", "--root", "docs", "--label", "nightly", "--freshness-hours", "12", "--compare-label", "baseline"]) == 0
    status_payload = json.loads(capsys.readouterr().out)

    assert cli.main(["acceleration", "reliability", "run", "--scope", "root", "--root", "docs", "--deployment-label", "desktop", "--include-cache-readiness", "--full", "--compare-label", "baseline"]) == 0
    run_payload = json.loads(capsys.readouterr().out)

    assert cli.main(["acceleration", "evidence", "--label", "nightly", "--compare-label", "baseline"]) == 0
    evidence_payload = json.loads(capsys.readouterr().out)

    assert cli.main(["acceleration", "reliability", "root-status", "--root", "docs"]) == 0
    root_payload = json.loads(capsys.readouterr().out)

    assert cli.main(["acceleration", "reliability", "roots", "--freshness-hours", "24"]) == 0
    roots_payload = json.loads(capsys.readouterr().out)

    assert status_payload["readiness"] == "partial"
    assert run_payload["settings_mutated"] is False
    assert evidence_payload["gates"]["vss_snapshot"]["state"] == "hold"
    assert root_payload == {"root_name": "docs", "readiness": "partial"}
    assert roots_payload["roots"][0]["root_name"] == "docs"
    assert calls[0] == (
        "status",
        {
            "root_name": "docs",
            "path": None,
            "label": "nightly",
            "deployment_label": None,
            "compare_label": "baseline",
            "freshness_hours": 12,
            "limit": 100,
        },
    )
    assert calls[1][0] == "run"
    assert calls[1][1]["scope"] == "root"
    assert calls[1][1]["include_cache_readiness"] is True
    assert calls[1][1]["evidence_level"] == "full"
    assert calls[1][1]["compare_label"] == "baseline"
    assert calls[2] == ("evidence", {"label": "nightly", "deployment_label": None, "compare_label": "baseline", "freshness_hours": 336, "limit": 100})
    assert calls[4] == ("roots", {"include_disabled": False, "freshness_hours": 24, "limit": 100})


def test_cli_code_and_diagnostics_commands_use_service(monkeypatch, capsys):
    from flux_llm_kb import service

    calls = []

    class FakeService:
        def code_status(self, **kwargs):
            calls.append(("code_status", kwargs))
            return {"totals": {"symbol_count": 2}, "roots": []}

        def code_search(self, **kwargs):
            calls.append(("code_search", kwargs))
            return {"query": kwargs["query"], "results": [{"symbol": "OrderService"}]}

        def code_symbol_lookup(self, **kwargs):
            calls.append(("code_symbol", kwargs))
            return {"query": kwargs["symbol"], "matches": [{"symbol": kwargs["symbol"]}]}

        def operational_diagnostics(self, **kwargs):
            calls.append(("diagnostics", kwargs))
            return {"section": kwargs["section"], "settings_mutated": False, "sections": {}}

        def record_code_feedback(self, **kwargs):
            calls.append(("code_feedback", kwargs))
            return {"id": "feedback-1", "settings_mutated": False}

        def code_feedback_summary(self, **kwargs):
            calls.append(("code_feedback_summary", kwargs))
            return {"settings_mutated": False, "rows": [{"miss_category": "missing_symbol"}]}

        def remediate_diagnostic(self, **kwargs):
            calls.append(("diagnostics_remediate", kwargs))
            return {"settings_mutated": False, "action": kwargs["action"]}

        def run_corpus_backfill(self, **kwargs):
            calls.append(("crawl_backfill", kwargs))
            return {"settings_mutated": False, "backfill": kwargs}

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert cli.main(["code", "status", "--root", "app"]) == 0
    status_payload = json.loads(capsys.readouterr().out)
    assert cli.main(["code", "search", "OrderService", "--language", "python", "--relationship", "call", "--path-glob", "src/*.py", "--include-generated"]) == 0
    search_payload = json.loads(capsys.readouterr().out)
    assert cli.main(["code", "symbol", "OrderService", "--no-references"]) == 0
    symbol_payload = json.loads(capsys.readouterr().out)
    assert cli.main(["code", "feedback", "add", "--query", "build invoice", "--root", "app", "--miss-category", "missing_symbol", "--result-count", "0", "--surface", "cli", "--expected-symbol", "OrderService.build_invoice", "--path", "E:/private/app/orders.py"]) == 0
    feedback_payload = json.loads(capsys.readouterr().out)
    assert cli.main(["code", "feedback", "summary", "--root", "app", "--limit", "5"]) == 0
    feedback_summary_payload = json.loads(capsys.readouterr().out)
    assert cli.main(["diagnostics", "workers", "--limit", "5", "--root", "app", "--status", "blocked_missing_dependency", "--family", "office", "--since-hours", "24", "--include-details"]) == 0
    diagnostics_payload = json.loads(capsys.readouterr().out)
    assert cli.main(["diagnostics", "remediate", "retry_corpus_job", "--target-type", "job", "--target-id", "job-1", "--root", "app", "--family", "office", "--reason", "operator retry"]) == 0
    remediation_payload = json.loads(capsys.readouterr().out)
    assert cli.main(["crawl", "backfill", "--root", "app", "--family", "office", "--limit", "4"]) == 0
    backfill_payload = json.loads(capsys.readouterr().out)

    assert status_payload["totals"]["symbol_count"] == 2
    assert search_payload["results"][0]["symbol"] == "OrderService"
    assert symbol_payload["matches"][0]["symbol"] == "OrderService"
    assert feedback_payload["id"] == "feedback-1"
    assert feedback_summary_payload["rows"][0]["miss_category"] == "missing_symbol"
    assert diagnostics_payload["settings_mutated"] is False
    assert remediation_payload["action"] == "retry_corpus_job"
    assert backfill_payload["backfill"] == {"kind": "office", "limit": 4, "workers": None, "root_name": "app"}
    assert calls == [
        ("code_status", {"root_name": "app"}),
        (
            "code_search",
            {
                "query": "OrderService",
                "root_name": None,
                "language": "python",
                "symbol_kind": None,
                "relationship": "call",
                "path_glob": "src/*.py",
                "include_generated": True,
                "limit": 20,
            },
        ),
        ("code_symbol", {"symbol": "OrderService", "root_name": None, "language": None, "include_references": False, "limit": 20}),
        (
            "code_feedback",
            {
                "query": "build invoice",
                "root_name": "app",
                "result_count": 0,
                "surface": "cli",
                "miss_category": "missing_symbol",
                "expected_symbol": "OrderService.build_invoice",
                "path": "E:/private/app/orders.py",
                "metadata": {},
            },
        ),
        ("code_feedback_summary", {"root_name": "app", "limit": 5}),
        (
            "diagnostics",
            {
                "section": "workers",
                "limit": 5,
                "root_name": "app",
                "status": "blocked_missing_dependency",
                "family": "office",
                "since_hours": 24,
                "include_details": True,
            },
        ),
        (
            "diagnostics_remediate",
            {
                "action": "retry_corpus_job",
                "target_type": "job",
                "target_id": "job-1",
                "root_name": "app",
                "family": "office",
                "reason": "operator retry",
                "actor": "cli",
            },
        ),
        ("crawl_backfill", {"kind": "office", "limit": 4, "workers": None, "root_name": "app"}),
    ]


def test_cli_watcher_probe_worker_status_and_benchmarks_use_service(monkeypatch, capsys):
    from flux_llm_kb import service

    calls = []

    class FakeService:
        def watch_probe(self, **kwargs):
            calls.append(("watch_probe", kwargs))
            return {"probe": kwargs, "path_scope": "temporary"}

        def worker_status(self, **kwargs):
            calls.append(("worker_status", kwargs))
            return {"families": [{"family": kwargs["family"], "backpressure": "cap_reached"}]}

        def run_benchmark(self, **kwargs):
            calls.append(("benchmark_run", kwargs))
            return {"fixture": kwargs["fixture"], "files": kwargs["files"], "runs": []}

        def benchmark_history(self, **kwargs):
            calls.append(("benchmark_history", kwargs))
            return {"fixture": kwargs["fixture"], "runs": [{"id": "run-1"}]}

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert cli.main(["crawl", "watch", "probe", "--timeout", "1.5"]) == 0
    probe_payload = json.loads(capsys.readouterr().out)
    assert probe_payload["path_scope"] == "temporary"

    assert cli.main(["crawl", "worker", "status", "--family", "media"]) == 0
    worker_payload = json.loads(capsys.readouterr().out)
    assert worker_payload["families"][0]["family"] == "media"

    assert (
        cli.main(
            [
                "acceleration",
                "benchmark",
                "run",
                "--fixture",
                "image-heavy",
                "--files",
                "4",
                "--mode",
                "soak",
                "--passes",
                "2",
                "--label",
                "after-deploy",
                "--compare-label",
                "before-deploy",
                "--workers",
                "3",
                "--family",
                "media",
                "--scope",
                "root",
                "--root",
                "docs",
                "--path",
                "E:\\Docs",
                "--max-files",
                "12",
                "--deployment-label",
                "desktop-after",
                "--scenario",
                "tuning",
                "--include-model-probe",
            ]
        )
        == 0
    )
    run_payload = json.loads(capsys.readouterr().out)
    assert run_payload["fixture"] == "image-heavy"

    assert (
        cli.main(
            [
                "acceleration",
                "benchmark",
                "history",
                "--fixture",
                "image-heavy",
                "--limit",
                "3",
                "--mode",
                "soak",
                "--label",
                "after-deploy",
                "--warm-state",
                "warm",
                "--scope-type",
                "monitored_root",
                "--deployment-label",
                "desktop-after",
            ]
        )
        == 0
    )
    history_payload = json.loads(capsys.readouterr().out)
    assert history_payload["runs"] == [{"id": "run-1"}]
    assert calls == [
        ("watch_probe", {"timeout_seconds": 1.5}),
        ("worker_status", {"family": "media"}),
        (
            "benchmark_run",
            {
                "fixture": "image-heavy",
                "files": 4,
                "mode": "soak",
                "passes": 2,
                "label": "after-deploy",
                "compare_label": "before-deploy",
                "workers": 3,
                "family": "media",
                "scope": "root",
                "root_name": "docs",
                "path": "E:\\Docs",
                "max_files": 12,
                "deployment_label": "desktop-after",
                "scenario": "tuning",
                "include_model_probe": True,
            },
        ),
        (
            "benchmark_history",
            {
                "fixture": "image-heavy",
                "mode": "soak",
                "label": "after-deploy",
                "warm_state": "warm",
                "scope_type": "monitored_root",
                "scope_hash": None,
                "deployment_label": "desktop-after",
                "scenario": None,
                "freshness_hours": None,
                "limit": 3,
            },
        ),
    ]


def test_cli_retrieval_benchmark_run_and_history_use_service(monkeypatch, capsys):
    from flux_llm_kb import service

    calls = []

    class FakeService:
        def run_retrieval_benchmark(self, **kwargs):
            calls.append(("retrieval_benchmark_run", kwargs))
            return {
                "suite": kwargs["suite"],
                "metrics": {"top1_accuracy": 1.0},
                "metric_deltas": {"top1_accuracy": 0.2},
                "calibration_summary": {"confidence_bands": {"high": 2}},
                "recommendations": {
                    "settings_mutated": False,
                    "candidates": [{"kind": "semantic_duplicate_threshold", "threshold": 0.86}],
                },
            }

        def retrieval_benchmark_history(self, **kwargs):
            calls.append(("retrieval_benchmark_history", kwargs))
            return {
                "suite": kwargs["suite"],
                "runs": [
                    {
                        "id": "retrieval-run-1",
                        "metric_deltas": {"top1_accuracy": 0.2},
                        "calibration_summary": {"confidence_bands": {"high": 2}},
                    }
                ],
            }

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert (
        cli.main(
            [
                "retrieval",
                "benchmark",
                "run",
                "--suite",
                "standard",
                "--label",
                "nightly",
                "--compare-label",
                "baseline",
                "--limit-per-query",
                "7",
                "--token-budget",
                "900",
            ]
        )
        == 0
    )
    run_payload = json.loads(capsys.readouterr().out)
    assert run_payload["suite"] == "standard"
    assert run_payload["metric_deltas"] == {"top1_accuracy": 0.2}
    assert run_payload["recommendations"]["candidates"][0]["kind"] == "semantic_duplicate_threshold"

    assert cli.main(["retrieval", "benchmark", "history", "--suite", "standard", "--label", "nightly", "--limit", "3"]) == 0
    history_payload = json.loads(capsys.readouterr().out)

    assert history_payload["runs"][0]["metric_deltas"] == {"top1_accuracy": 0.2}
    assert history_payload["runs"][0]["calibration_summary"]["confidence_bands"] == {"high": 2}
    assert calls == [
        (
            "retrieval_benchmark_run",
            {
                "suite": "standard",
                "label": "nightly",
                "compare_label": "baseline",
                "limit_per_query": 7,
                "token_budget": 900,
                "persist": True,
            },
        ),
        (
            "retrieval_benchmark_history",
            {
                "suite": "standard",
                "label": "nightly",
                "limit": 3,
            },
        ),
    ]


def test_cli_governance_commands_use_service(monkeypatch, capsys):
    from flux_llm_kb import service

    calls = []

    class FakeService:
        def run_governance(self, **kwargs):
            calls.append(("run", kwargs))
            return {"run": {"id": "run-1"}, "settings_mutated": False, "memory_mutated": False}

        def governance_actions(self, **kwargs):
            calls.append(("actions", kwargs))
            return {"actions": [{"id": "action-1", "status": "proposed"}], "telemetry": {"by_status": {"proposed": 1}}}

        def governance_apply(self, action_id, **kwargs):
            calls.append(("apply", {"action_id": action_id, **kwargs}))
            return {"action": {"id": action_id, "status": "applied"}, "settings_mutated": False, "memory_mutated": True}

        def governance_recover(self, action_id, **kwargs):
            calls.append(("recover", {"action_id": action_id, **kwargs}))
            return {"action": {"id": action_id, "status": "recovered"}, "settings_mutated": False, "memory_mutated": True}

        def governance_digest(self):
            calls.append(("digest", {}))
            return {"digest": {"summary": {"new_proposals": 1}}, "settings_mutated": False}

        def governance_policy(self):
            calls.append(("policy", {}))
            return {"policy": {"min_shadow_precision": 0.8}, "settings_mutated": False}

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert cli.main(["governance", "run", "--mode", "shadow", "--limit", "5"]) == 0
    assert json.loads(capsys.readouterr().out)["run"]["id"] == "run-1"
    assert cli.main(["governance", "actions", "list", "--status", "proposed", "--limit", "2"]) == 0
    assert json.loads(capsys.readouterr().out)["telemetry"]["by_status"]["proposed"] == 1
    assert cli.main(["governance", "actions", "apply", "action-1", "--rationale", "reviewed", "--confirm"]) == 0
    assert json.loads(capsys.readouterr().out)["action"]["status"] == "applied"
    assert cli.main(["governance", "actions", "recover", "action-1", "--rationale", "rollback", "--confirm"]) == 0
    assert json.loads(capsys.readouterr().out)["action"]["status"] == "recovered"
    assert cli.main(["governance", "digest"]) == 0
    assert json.loads(capsys.readouterr().out)["digest"]["summary"]["new_proposals"] == 1
    assert cli.main(["governance", "policy"]) == 0
    assert json.loads(capsys.readouterr().out)["policy"]["min_shadow_precision"] == 0.8
    assert calls == [
        ("run", {"mode": "shadow", "actor": "cli", "limit": 5}),
        ("actions", {"status": "proposed", "limit": 2}),
        ("apply", {"action_id": "action-1", "rationale": "reviewed", "confirm": True, "actor": "cli"}),
        ("recover", {"action_id": "action-1", "rationale": "rollback", "confirm": True, "actor": "cli"}),
        ("digest", {}),
        ("policy", {}),
    ]


def test_cli_remember_passes_workspace_scope(monkeypatch, capsys):
    from flux_llm_kb import service

    captured = {}

    class FakeService:
        def remember(self, title, body, metadata=None, cwd=None, root_name=None):
            captured.update({"title": title, "body": body, "metadata": metadata, "cwd": cwd, "root_name": root_name})
            return type("Result", (), {"id": "episode-1", "redaction_count": 0})()

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert cli.main(["remember", "Title", "Body", "--cwd", "E:/Repo", "--root-name", "repo"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload == {"id": "episode-1", "redaction_count": 0}
    assert captured["cwd"] == "E:/Repo"
    assert captured["root_name"] == "repo"


def test_cli_episodes_scope_backfill_requires_explicit_ids(monkeypatch, capsys):
    from flux_llm_kb import service

    captured = {}

    class FakeService:
        def backfill_episode_workspace_scope(self, **kwargs):
            captured.update(kwargs)
            return {"updated": 0, "dry_run": True, "episode_ids": kwargs["episode_ids"]}

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert (
        cli.main(
            [
                "episodes",
                "scope-backfill",
                "--cwd",
                "E:/Repo",
                "--id",
                "episode-1",
                "--id",
                "episode-2",
                "--dry-run",
            ]
        )
        == 0
    )
    payload = json.loads(capsys.readouterr().out)

    assert captured == {
        "episode_ids": ["episode-1", "episode-2"],
        "cwd": "E:/Repo",
        "root_name": None,
        "dry_run": True,
    }
    assert payload == {"updated": 0, "dry_run": True, "episode_ids": ["episode-1", "episode-2"]}


def test_cli_crawl_watch_enable_outputs_json(monkeypatch, capsys):
    monkeypatch.setattr(
        cli.database,
        "set_watch_enabled",
        lambda *, root_name, enabled: {"updated": 1, "root_name": root_name, "watch_enabled": enabled},
    )

    assert cli.main(["crawl", "watch", "enable", "--all"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload == {"updated": 1, "root_name": None, "watch_enabled": True}


def test_cli_crawl_add_persists_disabled_watch_by_default(monkeypatch, tmp_path, capsys):
    root = tmp_path / "docs"
    root.mkdir()

    def fake_add_monitored_root(**kwargs):
        return {"name": kwargs["name"], "root_path": kwargs["root_path"], "watch_enabled": kwargs["watch_enabled"]}

    monkeypatch.setattr(cli.database, "add_monitored_root", fake_add_monitored_root)

    assert cli.main(["crawl", "add", str(root), "--name", "docs"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload["name"] == "docs"
    assert payload["root_path"] == str(root.resolve())
    assert payload["watch_enabled"] is False


def test_cli_crawl_edit_updates_root(monkeypatch, tmp_path, capsys):
    calls = {}
    monkeypatch.setattr(
        cli.database,
        "update_monitored_root",
        lambda **kwargs: calls.update(kwargs)
        or {"id": kwargs["root_id"], "name": kwargs["name"], "root_path": kwargs["root_path"]},
    )
    monkeypatch.setattr(
        cli.database,
        "get_monitored_root_by_identifier",
        lambda _root: {
            "id": "root-1",
            "name": "docs",
            "root_path": str(tmp_path),
            "enabled": True,
            "recursive": True,
            "watch_enabled": True,
            "trust_rank": 500,
            "include_globs": [],
            "exclude_globs": [],
            "glob_mode": "extend",
            "max_inline_bytes": 262144,
            "heavy_threshold_bytes": 10485760,
        },
    )

    assert (
        cli.main(
            [
                "crawl",
                "edit",
                "root-1",
                "--name",
                "docs-edited",
                "--path",
                str(tmp_path),
                "--disable-watch",
                "--glob-mode",
                "override",
            ]
        )
        == 0
    )
    payload = json.loads(capsys.readouterr().out)

    assert payload["name"] == "docs-edited"
    assert calls["root_id"] == "root-1"
    assert calls["watch_enabled"] is False
    assert calls["glob_mode"] == "override"


def test_cli_crawl_delete_requires_and_passes_purge_index(monkeypatch, capsys):
    calls = []
    monkeypatch.setattr(
        cli.database,
        "delete_monitored_root",
        lambda **kwargs: calls.append(kwargs)
        or {"id": kwargs["root_id"], "deleted": True, "purged_index": kwargs["purge_index"]},
    )

    assert cli.main(["crawl", "delete", "root-1", "--purge-index"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload["deleted"] is True
    assert calls == [{"root_id": "root-1", "purge_index": True, "actor": "cli"}]


def test_cli_crawl_worker_run_once_invokes_backfill_loop(monkeypatch, capsys):
    from flux_llm_kb import service

    class FakeService:
        def run_corpus_worker(self, **kwargs):
            return {"worker": kwargs}

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert cli.main(["crawl", "worker", "run", "--once", "--limit", "2", "--interval", "0.1"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload["worker"]["once"] is True
    assert payload["worker"]["limit"] == 2


def test_cli_crawl_worker_run_omits_default_parallelism_knobs(monkeypatch, capsys):
    from flux_llm_kb import service

    class FakeService:
        def run_corpus_worker(self, **kwargs):
            return {"worker": kwargs}

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert cli.main(["crawl", "worker", "run", "--once"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload["worker"]["limit"] is None
    assert payload["worker"]["workers"] is None


def test_cli_crawl_backfill_and_worker_accept_specialized_kinds(monkeypatch, capsys):
    from flux_llm_kb import service

    calls = {}

    class FakeService:
        def run_corpus_backfill(self, **kwargs):
            calls["backfill"] = kwargs
            return {"backfill": kwargs}

        def run_corpus_worker(self, **kwargs):
            calls["worker"] = kwargs
            return {"worker": kwargs}

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert cli.main(["crawl", "backfill", "--kind", "diagrams", "--limit", "3"]) == 0
    backfill_payload = json.loads(capsys.readouterr().out)
    assert backfill_payload["backfill"] == {"kind": "diagrams", "limit": 3, "workers": None}
    assert calls["backfill"]["kind"] == "diagrams"

    assert cli.main(["crawl", "backfill", "--kind", "archives", "--limit", "5"]) == 0
    archive_payload = json.loads(capsys.readouterr().out)
    assert archive_payload["backfill"] == {"kind": "archives", "limit": 5, "workers": None}

    assert cli.main(["crawl", "backfill", "--kind", "data", "--limit", "6"]) == 0
    data_payload = json.loads(capsys.readouterr().out)
    assert data_payload["backfill"] == {"kind": "data", "limit": 6, "workers": None}

    assert cli.main(["crawl", "worker", "run", "--once", "--kind", "containers", "--limit", "4"]) == 0
    worker_payload = json.loads(capsys.readouterr().out)
    assert worker_payload["worker"]["kind"] == "containers"
    assert worker_payload["worker"]["limit"] == 4

    assert cli.main(["crawl", "worker", "run", "--once", "--kind", "reports", "--limit", "7"]) == 0
    report_worker_payload = json.loads(capsys.readouterr().out)
    assert report_worker_payload["worker"]["kind"] == "reports"
    assert report_worker_payload["worker"]["limit"] == 7


def test_cli_embeddings_status_enqueue_and_backfill_use_service(monkeypatch, capsys):
    from flux_llm_kb import service

    calls = {}

    class FakeService:
        def embedding_status(self, **kwargs):
            calls["status"] = kwargs
            return {"status": kwargs}

        def enqueue_embedding_jobs(self, **kwargs):
            calls["enqueue"] = kwargs
            return {"enqueue": kwargs}

        def refresh_embeddings(self, **kwargs):
            calls["backfill"] = kwargs
            return {"backfill": kwargs}

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert cli.main(["embeddings", "status", "--root", "docs"]) == 0
    status_payload = json.loads(capsys.readouterr().out)
    assert status_payload["status"] == {"root_name": "docs"}

    assert cli.main(["embeddings", "enqueue", "--owner-class", "corpus", "--root", "docs", "--limit", "25"]) == 0
    enqueue_payload = json.loads(capsys.readouterr().out)
    assert enqueue_payload["enqueue"] == {
        "owner_class": "corpus",
        "root_name": "docs",
        "stale_only": True,
        "limit": 25,
    }

    assert cli.main(["embeddings", "backfill", "--owner-class", "all", "--root", "docs", "--limit", "20"]) == 0
    backfill_payload = json.loads(capsys.readouterr().out)
    assert backfill_payload["backfill"] == {
        "owner_class": "all",
        "root_name": "docs",
        "stale_only": True,
        "limit": 20,
    }


def test_cli_claim_upsert_and_transition_use_service(monkeypatch, capsys):
    from flux_llm_kb import service

    calls = {}

    class FakeService:
        def upsert_claim(self, **kwargs):
            calls["upsert"] = kwargs
            return {"id": "claim-1", "lifecycle_state": "active"}

        def transition_claim(self, **kwargs):
            calls["transition"] = kwargs
            return {"id": "claim-1", "lifecycle_state": "confirmed"}

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert (
        cli.main(
            [
                "claim",
                "upsert",
                "--subject-type",
                "project",
                "--subject",
                "Flux",
                "--predicate",
                "uses",
                "--object",
                "PostgreSQL",
                "--confidence",
                "0.82",
            ]
        )
        == 0
    )
    upsert_payload = json.loads(capsys.readouterr().out)

    assert upsert_payload["id"] == "claim-1"
    assert calls["upsert"]["subject_type"] == "project"
    assert calls["upsert"]["object_text"] == "PostgreSQL"

    assert cli.main(["claim", "transition", "claim-1", "confirm", "--reason", "verified"]) == 0
    transition_payload = json.loads(capsys.readouterr().out)

    assert transition_payload["lifecycle_state"] == "confirmed"
    assert calls["transition"]["claim_id"] == "claim-1"
    assert calls["transition"]["transition"] == "confirm"


def test_cli_graph_traverse_uses_service(monkeypatch, capsys):
    from flux_llm_kb import service

    calls = {}

    class FakeService:
        def traverse_graph(self, **kwargs):
            calls.update(kwargs)
            return {"start_entity_id": kwargs["entity_id"], "edges": []}

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert (
        cli.main(
            [
                "graph",
                "traverse",
                "entity-1",
                "--relation-type",
                "depends_on",
                "--max-depth",
                "2",
                "--direction",
                "out",
            ]
        )
        == 0
    )
    payload = json.loads(capsys.readouterr().out)

    assert payload["start_entity_id"] == "entity-1"
    assert calls == {
        "entity_id": "entity-1",
        "relation_types": ["depends_on"],
        "max_depth": 2,
        "direction": "out",
        "limit": 100,
    }


def test_cli_capture_review_list_and_decide_use_service(monkeypatch, capsys):
    from flux_llm_kb import service

    calls = {}

    class FakeService:
        def list_capture_review_jobs(self, **kwargs):
            calls["list"] = kwargs
            return {"jobs": [{"id": "job-1", "status": "pending_review"}]}

        def ingest_capture_review_jobs(self, **kwargs):
            calls["ingest"] = kwargs
            return {"processed": 1, "ingested": 1, "dry_run": kwargs["dry_run"], "jobs": [{"id": "job-1"}]}

        def review_capture_job(self, **kwargs):
            calls["decide"] = kwargs
            return {
                "job": {"id": kwargs["job_id"], "status": "rejected"},
                "review": {"decision": kwargs["decision"], "rationale": kwargs["rationale"], "actor": kwargs["actor"]},
                "audit_event": {"id": "audit-1", "event_type": "capture.review_rejected"},
            }

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert cli.main(["capture", "review", "list", "--status", "approved", "--limit", "25"]) == 0
    list_payload = json.loads(capsys.readouterr().out)

    assert list_payload["jobs"][0]["id"] == "job-1"
    assert calls["list"] == {"status": "approved", "limit": 25}

    assert (
        cli.main(
            [
                "capture",
                "review",
                "decide",
                "job-1",
                "--decision",
                "reject",
                "--rationale",
                "not useful",
            ]
        )
        == 0
    )
    decide_payload = json.loads(capsys.readouterr().out)

    assert decide_payload["audit_event"]["event_type"] == "capture.review_rejected"
    assert calls["decide"] == {
        "job_id": "job-1",
        "decision": "reject",
        "rationale": "not useful",
        "actor": "cli",
    }

    assert (
        cli.main(
            [
                "capture",
                "review",
                "ingest",
                "--job-id",
                "job-1",
                "--limit",
                "10",
                "--dry-run",
            ]
        )
        == 0
    )
    ingest_payload = json.loads(capsys.readouterr().out)

    assert ingest_payload["processed"] == 1
    assert calls["ingest"] == {"job_id": "job-1", "limit": 10, "dry_run": True, "actor": "cli"}


def test_cli_retention_policy_and_quality_use_service(monkeypatch, capsys):
    from flux_llm_kb import service

    calls = {}

    class FakeService:
        def list_retention_policies(self):
            calls["list"] = True
            return {"policies": [{"memory_class": "claim", "half_life_days": 120, "min_confidence": 0.35, "action": "review"}]}

        def set_retention_policy(self, **kwargs):
            calls["set"] = kwargs
            return {
                "policy": {"memory_class": kwargs["memory_class"], "half_life_days": kwargs["half_life_days"]},
                "audit_event": {"id": "audit-1", "event_type": "retention.policy_updated"},
            }

        def retention_quality_report(self, **kwargs):
            calls["quality"] = kwargs
            return {
                "summary": {"total": 1, "needs_review": 1},
                "candidates": [{"id": "claim-1", "memory_class": "claim", "label": "Flux uses PostgreSQL"}],
            }

    monkeypatch.setattr(service, "KnowledgeService", FakeService)

    assert cli.main(["retention", "policy", "list"]) == 0
    list_payload = json.loads(capsys.readouterr().out)

    assert list_payload["policies"][0]["memory_class"] == "claim"
    assert calls["list"] is True

    assert (
        cli.main(
            [
                "retention",
                "policy",
                "set",
                "claim",
                "--half-life-days",
                "90",
                "--min-confidence",
                "0.45",
                "--action",
                "deprioritize",
                "--reason",
                "live review",
            ]
        )
        == 0
    )
    set_payload = json.loads(capsys.readouterr().out)

    assert set_payload["audit_event"]["event_type"] == "retention.policy_updated"
    assert calls["set"] == {
        "memory_class": "claim",
        "half_life_days": 90,
        "min_confidence": 0.45,
        "action": "deprioritize",
        "actor": "cli",
        "reason": "live review",
    }

    assert cli.main(["retention", "quality", "--limit", "10"]) == 0
    quality_payload = json.loads(capsys.readouterr().out)

    assert quality_payload["candidates"][0]["label"] == "Flux uses PostgreSQL"
    assert calls["quality"] == {"limit": 10}
