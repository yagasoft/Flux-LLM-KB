from flux_llm_kb import background_jobs


NOW = "2026-07-06T08:00:00+00:00"


def _install_projection_fixtures(monkeypatch):
    monkeypatch.setattr(
        background_jobs.database,
        "list_capture_jobs",
        lambda **_kwargs: [
            {
                "id": "cap-1",
                "job_type": "corpus_extract_pdf",
                "job_family": "office",
                "resource_class": "cpu",
                "status": "running",
                "payload": {"root_name": "docs", "path": "manual.pdf"},
                "attempts": 1,
                "last_error": None,
                "created_at": "2026-07-06T07:50:00+00:00",
                "updated_at": NOW,
                "started_at": "2026-07-06T07:51:00+00:00",
                "completed_at": None,
                "telemetry": {"progress_label": "OCR page 2/10"},
                "broker_message_id": "msg-cap-1",
            }
        ],
    )
    monkeypatch.setattr(
        background_jobs.database,
        "list_stranded_capture_commands",
        lambda **_kwargs: [
            {
                "id": "cap-stranded",
                "job_id": "cap-stranded",
                "job_type": "corpus_extract_image",
                "job_family": "image",
                "status": "stranded_command",
                "payload": {"root_name": "docs", "path": "logo.png"},
                "attempts": 0,
                "last_error": "missing active broker command",
                "created_at": "2026-07-06T07:35:00+00:00",
                "updated_at": "2026-07-06T07:40:00+00:00",
                "telemetry": {"diagnostic": "stranded"},
            }
        ],
        raising=False,
    )
    monkeypatch.setattr(
        background_jobs.database,
        "list_mail_sync_runs",
        lambda **_kwargs: [
            {
                "id": "mail-1",
                "profile_name": "gmail-capture",
                "status": "running",
                "trigger": "scheduler",
                "requested_by": "event-scheduler",
                "claimed_by": "mail-worker",
                "worker_id": "mail-worker",
                "attempt_count": 2,
                "last_error": None,
                "started_at": "2026-07-06T07:57:00+00:00",
                "finished_at": None,
                "updated_at": "2026-07-06T07:58:00+00:00",
                "messages_seen": 5,
                "messages_exported": 3,
            }
        ],
    )
    monkeypatch.setattr(
        background_jobs.database,
        "list_outlook_sync_requests",
        lambda **_kwargs: [
            {
                "id": "outlook-1",
                "profile_name": "outlook-catchup",
                "status": "pending",
                "requested_by": "dashboard",
                "claimed_by": None,
                "error": None,
                "result": {},
                "created_at": "2026-07-06T07:52:00+00:00",
                "updated_at": "2026-07-06T07:52:00+00:00",
            }
        ],
    )
    monkeypatch.setattr(
        background_jobs.database,
        "list_runtime_control_requests",
        lambda **_kwargs: [
            {
                "id": "runtime-1",
                "setting_key": "worker.batch_size",
                "action": "restart_component",
                "affected_components": ["worker"],
                "status": "pending",
                "actor": "dashboard",
                "requested_at": "2026-07-06T07:49:00+00:00",
                "updated_at": "2026-07-06T07:49:00+00:00",
                "metadata": {"reason": "setting changed"},
                "broker_message_id": "msg-runtime-1",
            }
        ],
        raising=False,
    )
    monkeypatch.setattr(
        background_jobs.database,
        "list_operator_automation_runs",
        lambda **_kwargs: [
            {
                "id": "automation-1",
                "mode": "guarded",
                "trigger": "scheduler",
                "status": "running",
                "actor": "event-scheduler",
                "summary": {},
                "started_at": "2026-07-06T07:48:00+00:00",
                "completed_at": None,
                "updated_at": "2026-07-06T07:48:30+00:00",
            }
        ],
    )
    monkeypatch.setattr(
        background_jobs.database,
        "list_memory_governance_runs",
        lambda **_kwargs: [
            {
                "id": "governance-1",
                "mode": "shadow",
                "trigger": "scheduler",
                "status": "blocked",
                "actor": "event-scheduler",
                "summary": {"proposed": 0},
                "created_at": "2026-07-06T07:47:00+00:00",
                "updated_at": "2026-07-06T07:47:10+00:00",
            }
        ],
    )
    monkeypatch.setattr(
        background_jobs.database,
        "list_callback_delivery_jobs",
        lambda **_kwargs: [
            {
                "id": "callback-1",
                "message_id": "callback-msg-1",
                "job_id": "cap-1",
                "callback_url": "https://example.invalid/callback",
                "status": "retrying",
                "attempts": 3,
                "last_status_code": 503,
                "last_error": "temporary upstream failure",
                "created_at": "2026-07-06T07:46:00+00:00",
                "updated_at": "2026-07-06T07:46:30+00:00",
            }
        ],
        raising=False,
    )
    monkeypatch.setattr(
        background_jobs.database,
        "list_gpu_lease_jobs",
        lambda **_kwargs: [
            {
                "id": "lease-1",
                "task_type": "ocr_document",
                "model_id": "paddle-vl",
                "status": "waiting",
                "component": "paddle-runner",
                "request_id": "cap-1",
                "created_at": "2026-07-06T07:45:00+00:00",
                "heartbeat_at": None,
                "expires_at": None,
                "metadata": {"owner": "corpus-worker"},
            }
        ],
        raising=False,
    )
    monkeypatch.setattr(
        background_jobs.database,
        "list_gpu_eviction_jobs",
        lambda **_kwargs: [
            {
                "id": "eviction-1",
                "lease_id": "lease-1",
                "task_type": "rerank",
                "model_id": "qwen",
                "component": "model-runner",
                "status": "queued",
                "error": "",
                "created_at": "2026-07-06T07:44:00+00:00",
                "queued_at": "2026-07-06T07:44:00+00:00",
                "started_at": None,
                "completed_at": None,
                "metadata": {"reason": "free_vram"},
                "broker_message_id": "msg-eviction-1",
            }
        ],
        raising=False,
    )
    monkeypatch.setattr(
        background_jobs.database,
        "list_model_activity_events",
        lambda **_kwargs: [
            {
                "id": "model-1",
                "service": "paddle-runner",
                "endpoint": "/v1/ocr",
                "action": "ocr",
                "activity_class": "vision_ocr",
                "caller_surface": "corpus_worker",
                "model": "paddle-vl",
                "status": "running",
                "started_at": "2026-07-06T07:43:00+00:00",
                "completed_at": None,
                "duration_ms": None,
                "error_class": None,
                "error_message": None,
                "metadata": {"component": "corpus-worker", "task_type": "ocr_document"},
            }
        ],
    )
    monkeypatch.setattr(
        background_jobs.database,
        "list_message_outbox_jobs",
        lambda **_kwargs: [
            {
                "id": "outbox-duplicate",
                "message_id": "msg-cap-1",
                "exchange": "flux.commands",
                "routing_key": "corpus.process",
                "message_type": "flux.corpus.process",
                "aggregate_type": "capture_jobs",
                "aggregate_id": "cap-1",
                "payload": {"job_id": "cap-1"},
                "status": "pending",
                "attempts": 1,
                "last_error": None,
                "created_at": "2026-07-06T07:59:00+00:00",
                "updated_at": "2026-07-06T07:59:00+00:00",
            },
            {
                "id": "outbox-unmatched",
                "message_id": "msg-automation-queued",
                "exchange": "flux.commands",
                "routing_key": "operator.automation.run",
                "message_type": "flux.operator.automation.run",
                "aggregate_type": "operator_automation_runs",
                "aggregate_id": "automation-queued",
                "payload": {"operation_id": "automation-queued", "mode": "guarded"},
                "status": "pending",
                "attempts": 0,
                "last_error": None,
                "created_at": "2026-07-06T07:42:00+00:00",
                "updated_at": "2026-07-06T07:42:00+00:00",
            },
        ],
        raising=False,
    )
    monkeypatch.setattr(
        background_jobs.database,
        "list_message_inbox_jobs",
        lambda **_kwargs: [
            {
                "consumer_name": "automation-worker",
                "message_id": "msg-inbox-1",
                "message_type": "flux.operator.automation.run",
                "status": "processing",
                "attempts": 1,
                "first_seen_at": "2026-07-06T07:41:00+00:00",
                "last_seen_at": "2026-07-06T07:41:10+00:00",
                "last_error": None,
                "metadata": {"routing_key": "operator.automation.run"},
            }
        ],
        raising=False,
    )


def test_dashboard_jobs_projection_reconciles_existing_background_sources(monkeypatch):
    _install_projection_fixtures(monkeypatch)

    payload = background_jobs.collect_dashboard_jobs_payload(limit=50)

    sources = {row["job_source"] for row in payload["jobs"]}
    assert {
        "capture_jobs",
        "stranded_capture_commands",
        "mail_sync_runs",
        "outlook_sync_requests",
        "runtime_control_requests",
        "operator_automation_runs",
        "memory_governance_runs",
        "message_outbox",
        "message_inbox",
        "callback_deliveries",
        "gpu_leases",
        "gpu_evictions",
        "model_activity_events",
    } <= sources
    ids = {row["id"] for row in payload["jobs"]}
    assert "capture_jobs:cap-1" in ids
    assert "message_outbox:outbox-duplicate" not in ids
    assert "message_outbox:outbox-unmatched" in ids
    assert payload["count"] == len(payload["jobs"])
    assert payload["filter_options"]["sources"] == sorted(sources)

    capture_row = next(row for row in payload["jobs"] if row["id"] == "capture_jobs:cap-1")
    assert capture_row["source_id"] == "cap-1"
    assert capture_row["target"] == "manual.pdf"
    assert capture_row["root_name"] == "docs"
    assert capture_row["status_group"] == "running"
    assert capture_row["progress"] == "OCR page 2/10"
    assert capture_row["details"]["payload"]["path"] == "manual.pdf"


def test_dashboard_jobs_projection_filters_by_source_status_and_type(monkeypatch):
    _install_projection_fixtures(monkeypatch)

    payload = background_jobs.collect_dashboard_jobs_payload(
        limit=10,
        job_source=["mail_sync_runs"],
        status=["running"],
        job_type=["mail_sync"],
    )

    assert payload["count"] == 1
    assert payload["jobs"][0]["id"] == "mail_sync_runs:mail-1"
    assert payload["jobs"][0]["target"] == "gmail-capture"
    assert payload["jobs"][0]["status_group"] == "running"


def test_dashboard_jobs_projection_shows_retryable_inbox_result_as_pending_retry(monkeypatch):
    _install_projection_fixtures(monkeypatch)
    monkeypatch.setattr(
        background_jobs.database,
        "list_message_inbox_jobs",
        lambda **_kwargs: [
            {
                "consumer_name": "flux-kb-event-worker",
                "message_id": "msg-retryable",
                "message_type": "flux.search_index.process",
                "status": "failed",
                "attempts": 5,
                "first_seen_at": "2026-07-06T07:41:00+00:00",
                "last_seen_at": "2026-07-06T07:45:00+00:00",
                "last_error": "retrying_gpu_busy",
                "metadata": {
                    "routing_key": "search_index.process",
                    "result": {
                        "job_id": "job-gpu",
                        "status": "retrying_gpu_busy",
                        "process_status": "failed",
                        "retryable": True,
                    },
                },
            }
        ],
        raising=False,
    )

    payload = background_jobs.collect_dashboard_jobs_payload(limit=50)

    row = next(item for item in payload["jobs"] if item["id"] == "message_inbox:flux-kb-event-worker:msg-retryable")
    assert row["status"] == "retrying_gpu_busy"
    assert row["status_group"] == "pending"
    assert row["last_error"] == "retrying_gpu_busy"
    assert row["details"]["inbox_status"] == "failed"
    assert row["details"]["metadata"]["result"]["retryable"] is True


def test_dashboard_jobs_filter_options_ignore_own_facet_and_keep_selected_zero_result_values(monkeypatch):
    rows = [
        {
            "id": "capture_jobs:failed-doc",
            "job_source": "capture_jobs",
            "status": "failed",
            "job_type": "corpus_extract_pdf",
            "root_name": "docs",
            "updated_at": NOW,
        },
        {
            "id": "capture_jobs:failed-mail",
            "job_source": "capture_jobs",
            "status": "failed",
            "job_type": "corpus_extract_pdf",
            "root_name": "mail",
            "updated_at": NOW,
        },
        {
            "id": "capture_jobs:completed-doc",
            "job_source": "capture_jobs",
            "status": "completed",
            "job_type": "corpus_extract_pdf",
            "root_name": "docs",
            "updated_at": NOW,
        },
        {
            "id": "capture_jobs:image-doc",
            "job_source": "capture_jobs",
            "status": "failed",
            "job_type": "corpus_extract_image",
            "root_name": "docs",
            "updated_at": NOW,
        },
        {
            "id": "message_inbox:worker:msg",
            "job_source": "message_inbox",
            "status": "failed",
            "job_type": "corpus_extract_pdf",
            "root_name": "docs",
            "updated_at": NOW,
        },
        {
            "id": "gpu_leases:lease",
            "job_source": "gpu_leases",
            "status": "completed",
            "job_type": "gpu_lease",
            "root_name": "gpu",
            "updated_at": NOW,
        },
    ]
    monkeypatch.setattr(background_jobs, "_collect_all_rows", lambda **_kwargs: rows)

    payload = background_jobs.collect_dashboard_jobs_payload(
        limit=10,
        status=["failed", "blocked_gpu_busy"],
        root_name=["docs"],
        job_type=["corpus_extract_pdf"],
        job_source=["capture_jobs"],
    )

    assert [row["id"] for row in payload["jobs"]] == ["capture_jobs:failed-doc"]
    assert payload["filter_options"]["statuses"] == ["blocked_gpu_busy", "completed", "failed"]
    assert payload["filter_options"]["roots"] == ["docs", "mail"]
    assert payload["filter_options"]["job_types"] == ["corpus_extract_image", "corpus_extract_pdf"]
    assert payload["filter_options"]["sources"] == ["capture_jobs", "message_inbox"]


def test_dashboard_jobs_projection_excludes_external_query_model_activity(monkeypatch):
    _install_projection_fixtures(monkeypatch)

    monkeypatch.setattr(
        background_jobs.database,
        "list_model_activity_events",
        lambda **_kwargs: [
            {
                "id": "mcp-query",
                "service": "model-runner",
                "endpoint": "/rerank",
                "action": "rerank",
                "activity_class": "retrieval",
                "caller_surface": "mcp",
                "model": "qwen",
                "status": "running",
                "started_at": "2026-07-06T07:43:00+00:00",
                "completed_at": None,
                "metadata": {},
            },
            {
                "id": "api-query",
                "service": "model-runner",
                "endpoint": "/embed",
                "action": "embed",
                "activity_class": "retrieval",
                "caller_surface": "api",
                "model": "snowflake",
                "status": "completed",
                "started_at": "2026-07-06T07:42:00+00:00",
                "completed_at": "2026-07-06T07:42:01+00:00",
                "metadata": {},
            },
            {
                "id": "worker-ocr",
                "service": "paddle-runner",
                "endpoint": "/v1/ocr",
                "action": "ocr",
                "activity_class": "vision_ocr",
                "caller_surface": "corpus_worker",
                "model": "paddle-vl",
                "status": "running",
                "started_at": "2026-07-06T07:41:00+00:00",
                "completed_at": None,
                "metadata": {"component": "corpus-worker"},
            },
        ],
    )

    payload = background_jobs.collect_dashboard_jobs_payload(limit=50, job_source=["model_activity_events"])

    assert [row["id"] for row in payload["jobs"]] == ["model_activity_events:worker-ocr"]
