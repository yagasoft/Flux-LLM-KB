from __future__ import annotations

import anyio
import json

import pytest

from flux_llm_kb import cli, database, gpu_scheduler, local_detail_projections, mcp_server
from flux_llm_kb.operational_diagnostics import summarize_operational_diagnostics
from flux_llm_kb.service import KnowledgeService


def _diagnostic_inputs() -> dict:
    return {
        "watcher": {
            "events": [
                {
                    "id": "watch-1",
                    "root_name": "app",
                    "action": "modified",
                    "path": "E:/Private/App/src/Parser.cs",
                    "content_hash": "sha256:parser",
                    "runtime_detail": {"worker": "retained-csharp-code", "elapsed_ms": 17},
                    "parser_diagnostic": "CS1002 ; expected",
                    "retained_provenance": {"binding": "retained-v1", "member": "src/Parser.cs"},
                }
            ]
        }
    }


def test_named_local_diagnostic_projection_exposes_bounded_raw_evidence_but_public_projection_stays_sanitised():
    inputs = _diagnostic_inputs()

    local = local_detail_projections.project_local_operational_diagnostics(
        summarize_operational_diagnostics(**inputs),
        **inputs,
    )
    item = local["items"][0]

    assert item["path"] == "E:/Private/App/src/Parser.cs"
    assert item["hash"] == "sha256:parser"
    assert item["runtime_detail"] == {"worker": "retained-csharp-code", "elapsed_ms": 17}
    assert item["parser_diagnostic"] == "CS1002 ; expected"
    assert item["retained_provenance"] == {"binding": "retained-v1", "member": "src/Parser.cs"}

    public = summarize_operational_diagnostics(**inputs)
    public_item = public["items"][0]
    assert "path" not in public_item
    assert "hash" not in public_item
    assert "runtime_detail" not in public_item
    assert "parser_diagnostic" not in public_item
    assert "retained_provenance" not in public_item
    assert "E:/Private/App" not in json.dumps(public)


def test_local_diagnostic_and_audit_projections_withhold_secrets_and_bound_oversized_evidence():
    inputs = {
        "jobs": {
            "jobs": [
                {
                    "id": "job-1",
                    "job_family": "code",
                    "status": "failed",
                    "path": "E:/Private/App/secret.cs",
                    "runtime_detail": "token=secret-content-sentinel",
                    "parser_diagnostic": "x" * 5000,
                    "retained_provenance": {"artifact_hash": "sha256:secret", "source": "retained"},
                }
            ]
        }
    }
    local = local_detail_projections.project_local_operational_diagnostics(summarize_operational_diagnostics(**inputs), **inputs)
    item = local["items"][0]

    assert item["path"] == "E:/Private/App/secret.cs"
    assert item["runtime_detail"] is None
    assert item["runtime_detail_reason_code"] == "secret-content-withheld"
    assert len(item["parser_diagnostic"]) <= 4096
    assert "secret-content-sentinel" not in json.dumps(local)

    events = [
        {
            "id": "audit-1",
            "event_type": "processor.blocked",
            "details": {
                "path": "E:/Private/App/src/Parser.cs",
                "content_hash": "sha256:parser",
                "artifact_hash": "sha256:artifact",
                "runtime_detail": "token=secret-content-sentinel",
                "parser_diagnostic": "CS1002 ; expected",
                "retained_provenance": {"binding": "retained-v1"},
            },
        }
    ]
    local_audit = local_detail_projections.project_local_audit_events(events)
    public_audit = local_detail_projections.project_public_audit_events(events)

    assert local_audit[0]["path"] == "E:/Private/App/src/Parser.cs"
    assert local_audit[0]["hash"] == "sha256:parser"
    assert local_audit[0]["runtime_detail"] is None
    assert local_audit[0]["runtime_detail_reason_code"] == "secret-content-withheld"
    assert local_audit[0]["parser_diagnostic"] == "CS1002 ; expected"
    assert "E:/Private/App" not in json.dumps(public_audit)
    assert "secret-content-sentinel" not in json.dumps(public_audit)
    assert "sha256:artifact" not in json.dumps(public_audit)


def test_audit_projections_recursively_remove_public_private_fields_but_keep_safe_local_evidence():
    events = [
        {
            "id": "audit-1",
            "event_type": "export.completed",
            "actor": "private-operator",
            "target_id": "E:/Private/App/src/Parser.cs",
            "output_dir": "E:/Private/Exports",
            "target_path": "E:/Private/App/src/Parser.cs",
            "message": "Exported E:/Private/Exports/wiki.md",
            "authorization": "Bearer secret-content-sentinel",
            "details": {
                "nested": {
                    "actor": "private-worker",
                    "target_id": "E:/Private/App/src/Parser.cs",
                    "output_dir": "E:/Private/Exports",
                    "target_path": "E:/Private/App/src/Parser.cs",
                    "message": "Copied E:/Private/Exports/wiki.md",
                    "diagnostic": "token=secret-content-sentinel",
                    "token": "secret-content-sentinel",
                    "private_key": "not-a-real-key-but-must-never-be-projected",
                },
                "parser_diagnostic": "CS1002 ; expected",
            },
        }
    ]

    public = local_detail_projections.project_public_audit_events(events)
    local = local_detail_projections.project_local_audit_events(events)

    public_json = json.dumps(public)
    local_json = json.dumps(local)
    assert "private-operator" not in public_json
    assert "private-worker" not in public_json
    assert "E:/Private" not in public_json
    assert "secret-content-sentinel" not in public_json
    assert "actor" not in public[0]
    assert "target_id" not in public[0]
    assert "output_dir" not in public[0]
    assert "target_path" not in public[0]
    assert "actor" not in public[0]["details"]["nested"]
    assert "target_id" not in public[0]["details"]["nested"]
    assert "output_dir" not in public[0]["details"]["nested"]
    assert "target_path" not in public[0]["details"]["nested"]
    assert "private_key" not in public[0]["details"]["nested"]
    assert "secret-content-withheld" in public_json

    assert local[0]["actor"] == "private-operator"
    assert local[0]["target_id"] == "E:/Private/App/src/Parser.cs"
    assert local[0]["output_dir"] == "E:/Private/Exports"
    assert local[0]["target_path"] == "E:/Private/App/src/Parser.cs"
    assert local[0]["details"]["nested"]["actor"] == "private-worker"
    assert local[0]["details"]["nested"]["output_dir"] == "E:/Private/Exports"
    assert "token" not in local[0]["details"]["nested"]
    assert "private_key" not in local[0]["details"]["nested"]
    assert "secret-content-sentinel" not in local_json


def test_local_diagnostic_projection_maps_retrieval_mail_post_process_and_gpu_eviction_rows():
    inputs = {
        "retrieval": {
            "recent_explains": [
                {
                    "id": "retrieval-1",
                    "query_hash": "sha256:query",
                    "source_path": "E:/Private/App/docs/architecture.md",
                    "artifact_hash": "sha256:retrieval",
                    "runtime_detail": "ranker local-v1",
                    "provenance": {"source": "retained"},
                }
            ]
        },
        "workers": {
            "families": [
                {
                    "family": "office",
                    "source_path": "E:/Private/App/retained/office.docx",
                    "checksum": "sha256:office",
                    "last_error": "parser retry pending",
                    "provenance": {"source": "retained"},
                }
            ],
            "gpu_evictions": {
                "recent": [
                    {
                        "id": "eviction-1",
                        "status": "retrying",
                        "model_id": "local-model",
                        "component": "model-runner",
                        "error": "runtime E:/Private/Runtimes/model.bin did not release",
                        "content_hash": "sha256:gpu",
                        "retained_binding": {"binding": "retained-v1"},
                    }
                ]
            },
        },
        "mail": {
            "sync_runs": [
                {
                    "id": "mail-sync-1",
                    "profile_name": "private-profile",
                    "status": "failed",
                    "asset_path": "E:/Private/Mail/sync-state.json",
                    "content_hash": "sha256:sync",
                    "last_error": "transport unavailable",
                    "provenance": {"source": "retained"},
                }
            ],
            "post_process_events": [
                {
                    "id": "mail-post-1",
                    "profile_name": "private-profile",
                    "status": "failed",
                    "action": "move",
                    "target_path": "E:/Private/Mail/retained.eml",
                    "checksum": "sha256:mail",
                    "last_error": "parser failed",
                    "retained_provenance": {"source": "retained"},
                }
            ],
        },
    }

    local = local_detail_projections.project_local_operational_diagnostics(
        summarize_operational_diagnostics(**inputs),
        **inputs,
    )
    by_id = {item["target"]["id"]: item for item in local["items"]}

    assert by_id["retrieval-1"]["path"] == "E:/Private/App/docs/architecture.md"
    assert by_id["retrieval-1"]["hash"] == "sha256:retrieval"
    assert by_id["retrieval-1"]["runtime_detail"] == "ranker local-v1"
    assert by_id["retrieval-1"]["retained_provenance"] == {"source": "retained"}
    assert by_id["office"]["path"] == "E:/Private/App/retained/office.docx"
    assert by_id["office"]["hash"] == "sha256:office"
    assert by_id["office"]["runtime_detail"] == "parser retry pending"
    assert by_id["office"]["retained_provenance"] == {"source": "retained"}
    assert by_id["eviction-1"]["hash"] == "sha256:gpu"
    assert by_id["eviction-1"]["runtime_detail"] == "runtime E:/Private/Runtimes/model.bin did not release"
    assert by_id["eviction-1"]["retained_provenance"] == {"binding": "retained-v1"}
    assert by_id["mail-post-1"]["path"] == "E:/Private/Mail/retained.eml"
    assert by_id["mail-post-1"]["hash"] == "sha256:mail"
    assert by_id["mail-post-1"]["runtime_detail"] == "parser failed"
    assert by_id["mail-post-1"]["retained_provenance"] == {"source": "retained"}
    assert by_id["mail-sync-1"]["path"] == "E:/Private/Mail/sync-state.json"
    assert by_id["mail-sync-1"]["hash"] == "sha256:sync"
    assert by_id["mail-sync-1"]["runtime_detail"] == "transport unavailable"
    assert by_id["mail-sync-1"]["retained_provenance"] == {"source": "retained"}


@pytest.mark.filterwarnings(
    "ignore:Using `httpx` with `starlette.testclient` is deprecated:starlette.exceptions.StarletteDeprecationWarning"
)
def test_named_local_diagnostic_and_audit_adapters_use_only_read_contracts(monkeypatch, capsys):
    from fastapi.testclient import TestClient
    from flux_llm_kb.rest_api import create_app

    calls: list[tuple[str, object]] = []
    local_diagnostics = {"section": "jobs", "settings_mutated": False, "items": [{"path": "E:/Private/App/src/Parser.cs"}]}
    local_audit = [{"id": "audit-1", "path": "E:/Private/App/src/Parser.cs"}]

    class FakeService:
        def local_operational_diagnostics(self, **kwargs):
            calls.append(("local_diagnostics", kwargs))
            return local_diagnostics

        def local_audit(self, *, limit):
            calls.append(("local_audit", limit))
            return local_audit

        def remediate_diagnostic(self, **kwargs):
            calls.append(("remediate", kwargs))
            return {"settings_mutated": False, "action": kwargs["action"]}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", FakeService)
    app = create_app()
    local_rest = TestClient(app, client=("127.0.0.1", 50100)).get("/api/local/diagnostics/jobs", params={"include_details": "true"})
    local_audit_rest = TestClient(app, client=("127.0.0.1", 50100)).get("/api/local/audit")
    remote = TestClient(app, client=("192.0.2.20", 50100)).get("/api/local/diagnostics/jobs")
    mutation = TestClient(app, client=("127.0.0.1", 50100)).post(
        "/api/diagnostics/actions",
        json={"action": "retry_corpus_job", "target_type": "job", "target_id": "job-1"},
    )

    monkeypatch.setattr("flux_llm_kb.service.KnowledgeService", FakeService)
    assert cli.main(["local-detail", "diagnostics", "jobs"]) == 0
    assert json.loads(capsys.readouterr().out) == local_diagnostics
    assert cli.main(["local-detail", "audit"]) == 0
    assert json.loads(capsys.readouterr().out) == local_audit

    server = mcp_server.create_server(service_factory=FakeService, retry_sleep=lambda _seconds: None)

    async def call_mcp():
        diagnostics = await server.call_tool("kb.local_operational_diagnostics", {"section": "jobs"})
        audit = await server.call_tool("kb.local_audit", {"limit": 20})
        return json.loads(diagnostics[0].text), json.loads(audit[0].text)

    mcp_diagnostics, mcp_audit = anyio.run(call_mcp)

    assert local_rest.status_code == 200
    assert local_audit_rest.status_code == 200
    assert remote.status_code == 403
    assert local_rest.json() == local_diagnostics
    assert local_audit_rest.json() == local_audit
    assert mcp_diagnostics == local_diagnostics
    assert mcp_audit == {"events": local_audit}
    assert mutation.status_code == 200
    assert any(name == "remediate" for name, _value in calls)


def test_service_local_diagnostic_and_audit_readers_do_not_mutate_persisted_audit_events(monkeypatch):
    event = {"id": "audit-1", "event_type": "processor.completed", "details": {"path": "E:/Private/App/src/Parser.cs"}}
    monkeypatch.setattr(database, "list_audit_events", lambda **_kwargs: [event])
    monkeypatch.setattr(database, "list_watch_events", lambda **_kwargs: _diagnostic_inputs()["watcher"]["events"])

    service = KnowledgeService()
    local_event = service.local_audit(limit=10)[0]
    public_event = service.audit(limit=10)[0]
    local_diagnostics = service.local_operational_diagnostics(section="watcher", limit=10)

    assert local_event["path"] == "E:/Private/App/src/Parser.cs"
    assert "path" not in public_event
    assert local_diagnostics["items"][0]["path"] == "E:/Private/App/src/Parser.cs"
    assert event["details"]["path"] == "E:/Private/App/src/Parser.cs"


def test_public_audit_projection_removes_hash_shaped_keys_and_inline_hashes_recursively():
    events = [
        {
            "id": "audit-hash-1",
            "event_type": "export.completed",
            "details": {
                "documentHash": "private-sha",
                "sha256": "private-digest",
                "nested_value": {"content_digest": "private-nested-digest"},
                "message": "Copied input sha256:private-inline to the export.",
            },
            "summary": "Completed with sha256:top-level-inline.",
        }
    ]

    public = local_detail_projections.project_public_audit_events(events)
    public_json = json.dumps(public)

    assert "documentHash" not in public_json
    assert "sha256" not in public_json
    assert "content_digest" not in public_json
    assert "private-sha" not in public_json
    assert "private-digest" not in public_json
    assert "private-nested-digest" not in public_json
    assert "sha256:private-inline" not in public_json
    assert "sha256:top-level-inline" not in public_json
    assert "<hash>" in public_json


def test_local_diagnostics_use_retrieval_query_hash_and_producer_metadata_nesting():
    retrieval = {
        "recent_explains": [
            {
                # Exact public retrieval diagnostic row shape from
                # database.recent_retrieval_explain_diagnostics.
                "query_hash": "sha256:retrieval-query",
                "result_count": 2,
                "confidence": "unknown",
                "failed_case_count": 0,
                "created_at": "2026-08-19T12:00:00+00:00",
            }
        ]
    }
    mail = {
        "post_process_events": [
            {
                # Exact public list_mail_post_process_events row shape: evidence
                # belongs in metadata, rather than invented top-level fields.
                "id": "mail-post-producer-1",
                "profile_name": "private-profile",
                "sync_run_id": "sync-1",
                "mail_message_id": "message-1",
                "provider": "imap",
                "policy": "move_to_processed",
                "action": "move",
                "status": "failed",
                "dry_run": False,
                "commands": [],
                "error": "post-process failed",
                "metadata": {
                    "target_path": "E:/Private/Mail/retained.eml",
                    "checksum": "sha256:mail-metadata",
                    "runtime_detail": "imap local worker",
                    "retained_provenance": {"source": "retained-mail"},
                },
                "created_at": "2026-08-19T12:00:00+00:00",
            }
        ]
    }
    workers = {
        "gpu_evictions": {
            "recent": [
                {
                    # Exact list_gpu_eviction_jobs row shape: detailed evidence
                    # is nested under metadata.
                    "id": "eviction-producer-1",
                    "lease_id": "lease-1",
                    "task_type": "embedding",
                    "model_id": "local-model",
                    "component": "model-runner",
                    "status": "retrying",
                    "estimated_freed_vram_mb": 1024,
                    "error": "retry pending",
                    "created_at": "2026-08-19T12:00:00+00:00",
                    "completed_at": None,
                    "broker_message_id": "broker-1",
                    "routing_key": "gpu.eviction.retrying",
                    "correlation_id": "correlation-1",
                    "causation_id": "cause-1",
                    "queued_at": "2026-08-19T12:00:00+00:00",
                    "started_at": "2026-08-19T12:00:00+00:00",
                    "broker_delivery_count": 2,
                    "metadata": {
                        "path": "E:/Private/Runtimes/model.bin",
                        "artifact_hash": "sha256:gpu-metadata",
                        "runtime_detail": "runtime inventory local-v1",
                        "retained_provenance": {"source": "gpu-runtime"},
                    },
                }
            ]
        }
    }
    audit = [
        {
            "id": "audit-producer-1",
            "event_type": "mail.post_process.failed",
            # Audit producer's extensible evidence shape is details -> metadata.
            "details": {
                "metadata": {
                    "source_path": "E:/Private/Mail/retained.eml",
                    "content_hash": "sha256:audit-metadata",
                    "parser_diagnostic": "mail parser failed",
                    "retained_provenance": {"source": "retained-mail"},
                }
            },
        }
    ]

    report = summarize_operational_diagnostics(retrieval=retrieval, workers=workers, mail=mail)
    local = local_detail_projections.project_local_operational_diagnostics(
        report,
        retrieval=retrieval,
        workers=workers,
        mail=mail,
    )
    by_id = {item["target"]["id"]: item for item in local["items"]}
    local_audit = local_detail_projections.project_local_audit_events(audit)

    assert by_id["sha256:retrieval-query"]["hash"] == "sha256:retrieval-query"
    assert by_id["mail-post-producer-1"]["path"] == "E:/Private/Mail/retained.eml"
    assert by_id["mail-post-producer-1"]["hash"] == "sha256:mail-metadata"
    assert by_id["mail-post-producer-1"]["retained_provenance"] == {"source": "retained-mail"}
    assert by_id["eviction-producer-1"]["path"] == "E:/Private/Runtimes/model.bin"
    assert by_id["eviction-producer-1"]["hash"] == "sha256:gpu-metadata"
    assert by_id["eviction-producer-1"]["retained_provenance"] == {"source": "gpu-runtime"}
    assert local_audit[0]["path"] == "E:/Private/Mail/retained.eml"
    assert local_audit[0]["hash"] == "sha256:audit-metadata"
    assert local_audit[0]["parser_diagnostic"] == "mail parser failed"


def test_public_audit_projection_keeps_numeric_hash_aggregates_without_raw_hash_values():
    public = local_detail_projections.project_public_audit_events(
        [{
            "id": "audit-aggregate-1",
            "event_type": "export.completed",
            "details": {
                "hash_count": 7,
                "digest_count": 2,
                "checksum_present": True,
                "content_hash": "sha256:private-content",
                "sha256": "private-algorithm-value",
            },
        }]
    )

    details = public[0]["details"]
    assert details["hash_count"] == 7
    assert details["digest_count"] == 2
    assert details["checksum_present"] is True
    assert "content_hash" not in details
    assert "sha256" not in details


def test_public_diagnostics_exclude_mail_and_gpu_runtime_identity_but_named_local_projection_preserves_it():
    inputs = {
        "workers": {
            "gpu_evictions": {
                "recent": [
                    {
                        "id": "eviction-boundary-1",
                        "status": "retrying",
                        "component": "model-runner",
                        "error": "unload retry pending",
                        "owner_component": "model-runner",
                        "runtime_generation": "generation-private-7",
                        "runtime_activity_sequence": 33,
                        "runtime_fingerprint": "sha256:runtime-private-7",
                        "reconciliation_observation_id": "observation-private-7",
                        "metadata": {
                            "runtime_fingerprint": "sha256:runtime-private-7",
                            "owner_component": "model-runner",
                        },
                    }
                ]
            }
        },
        "mail": {
            "post_process_events": [
                {
                    "id": "mail-boundary-1",
                    "profile_name": "private-profile",
                    "mail_message_id": "message-1",
                    "provider": "imap",
                    "status": "failed",
                    "error": "IMAP move failed",
                    "commands": [{"command": "STORE", "uid": 42}],
                    "metadata": {"folder": "Inbox/Private", "uid": 42, "uidvalidity": 9},
                }
            ]
        },
    }

    public = summarize_operational_diagnostics(**inputs)
    local = local_detail_projections.project_local_operational_diagnostics(public, **inputs)
    public_json = json.dumps(public, sort_keys=True)
    local_by_id = {item["target"]["id"]: item for item in local["items"]}

    for private_value in (
        "Inbox/Private",
        "generation-private-7",
        "sha256:runtime-private-7",
        "observation-private-7",
    ):
        assert private_value not in public_json
    assert '"uid": 42' not in public_json
    assert "owner_component" not in public_json
    assert "runtime_activity_sequence" not in public_json

    assert local_by_id["mail-boundary-1"]["path"] == "Inbox/Private"
    assert local_by_id["mail-boundary-1"]["retained_provenance"] == {
        "folder": "Inbox/Private",
        "uid": 42,
        "uidvalidity": 9,
    }
    assert local_by_id["eviction-boundary-1"]["hash"] == "sha256:runtime-private-7"
    assert local_by_id["eviction-boundary-1"]["runtime_detail"]["runtime_generation"] == "generation-private-7"


def test_gpu_decoder_default_sequence_does_not_replace_error_without_substantive_runtime_evidence():
    inputs = {
        "workers": {
            "gpu_evictions": {
                "recent": [
                    {
                        "id": "eviction-default-sequence",
                        "status": "retrying",
                        "component": "model-runner",
                        "error": "the useful retry failure",
                        "runtime_activity_sequence": 0,
                    }
                ]
            }
        }
    }

    local = local_detail_projections.project_local_operational_diagnostics(
        summarize_operational_diagnostics(**inputs),
        **inputs,
    )

    assert local["items"][0]["runtime_detail"] == "the useful retry failure"


def test_shared_gpu_scheduler_status_excludes_nested_local_runtime_identity_but_named_local_reader_preserves_it(monkeypatch):
    producer_row = database._gpu_eviction_request_from_row(
        (
            "eviction-shared-1",
            "lease-1",
            "embedding",
            "local-model",
            "model-runner",
            "retrying",
            1024,
            "retry pending",
            {
                "owner_component": "model-runner",
                "runtime_fingerprint": "sha256:runtime-private-9",
                "terminal_reason": "verification_deferred",
                "verification": {
                    "runtime_generation": "nested-generation-private-9",
                    "reconciliation_observation_id": "nested-observation-private-9",
                },
            },
            "broker-1",
            "gpu.eviction.retrying",
            "correlation-1",
            "cause-1",
            2,
            "generation-private-9",
            51,
            "claim-1",
            4,
            None,
            None,
            None,
            None,
            "idle",
            "verification_deferred",
            "observation-private-9",
        )
    )
    scheduler = gpu_scheduler.PostgresGpuScheduler(
        gpu_scheduler.GpuSchedulerConfig(mode="postgres"),
        database_url="postgresql://example",
    )

    class Connection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

    def fetch(_connection, statement, _params):
        if "FROM gpu_leases" in statement or "FROM gpu_model_residency" in statement:
            return []
        if "FROM gpu_evictions" in statement:
            return [producer_row]
        if "FROM audit_events" in statement:
            return [{"cas_rejections": 0}]
        raise AssertionError(statement)

    monkeypatch.setattr(scheduler, "_reconcile_runtime_residency", lambda: None)
    monkeypatch.setattr(scheduler, "_recover_stale", lambda: None)
    monkeypatch.setattr(scheduler, "_connection", lambda: Connection())
    monkeypatch.setattr(gpu_scheduler, "_fetch_dicts", fetch)
    monkeypatch.setattr(gpu_scheduler, "live_gpu_memory", lambda: {})
    monkeypatch.setattr(gpu_scheduler, "_SCHEDULER", scheduler)

    shared_status = gpu_scheduler.get_gpu_scheduler().status()
    shared_json = json.dumps(shared_status, sort_keys=True)
    shared_evictions_json = json.dumps(shared_status["evictions"]["recent"], sort_keys=True)

    for marker in (
        "owner_component",
        "runtime_generation",
        "runtime_activity_sequence",
        "runtime_fingerprint",
        "reconciliation_observation_id",
    ):
        assert marker not in shared_evictions_json
    for marker in (
        "sha256:runtime-private-9",
        "nested-generation-private-9",
        "nested-observation-private-9",
    ):
        assert marker not in shared_json
    assert shared_status["evictions"]["recent"][0]["metadata"] == {
        "terminal_reason": "verification_deferred",
        "verification": {},
    }

    monkeypatch.setattr(database, "worker_family_stats", lambda: [])
    monkeypatch.setattr(database, "list_local_gpu_eviction_diagnostics", lambda **_kwargs: [producer_row])
    local = KnowledgeService().local_operational_diagnostics(section="workers", limit=10)
    local_item = next(item for item in local["items"] if item["target"]["id"] == "eviction-shared-1")

    assert producer_row["metadata"]["runtime_fingerprint"] == "sha256:runtime-private-9"
    assert local_item["hash"] == "sha256:runtime-private-9"
    assert local_item["runtime_detail"] == {
        "owner_component": "model-runner",
        "runtime_generation": "generation-private-9",
        "runtime_activity_sequence": 51,
        "runtime_fingerprint": "sha256:runtime-private-9",
        "reconciliation_observation_id": "observation-private-9",
        "terminal_reason": "verification_deferred",
    }
