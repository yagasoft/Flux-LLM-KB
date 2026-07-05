from __future__ import annotations

import threading
import time

import pytest

from flux_llm_kb import gpu_scheduler
from flux_llm_kb.gpu_scheduler import (
    GpuAdmissionDecision,
    GpuEvictionCandidate,
    GpuEvictionVerificationResult,
    GpuLeaseRecord,
    GpuLeaseTimeout,
    GpuLease,
    GpuModelResidency,
    GpuSchedulerConfig,
    GpuTaskProfile,
    InProcessGpuScheduler,
    PostgresGpuScheduler,
    plan_gpu_admission,
    select_gpu_eviction_candidates,
    task_profile,
)


def _config(**overrides):
    values = {
        "enabled": True,
        "mode": "in_process",
        "vram_budget_mb": 10_000,
        "safety_margin_mb": 1_000,
        "default_timeout_seconds": 1.0,
        "lease_ttl_seconds": 30.0,
        "heartbeat_interval_seconds": 5.0,
        "stale_after_seconds": 60.0,
    }
    values.update(overrides)
    return GpuSchedulerConfig(**values)


def _lease(
    lease_id: str,
    *,
    task_type: str = "embedding",
    model_id: str = "model-a",
    estimated_vram_mb: int = 2_000,
    exclusive: bool = True,
    share_group: str = "",
    status: str = "running",
    expires_at: float = 100.0,
) -> GpuLeaseRecord:
    return GpuLeaseRecord(
        id=lease_id,
        task_type=task_type,
        model_id=model_id,
        status=status,
        estimated_vram_mb=estimated_vram_mb,
        exclusive=exclusive,
        share_group=share_group,
        priority=0,
        component="tests",
        request_id="",
        created_at=1.0,
        granted_at=2.0,
        heartbeat_at=2.0,
        expires_at=expires_at,
        released_at=None,
        metadata={},
    )


def _resident(
    task_type: str,
    model_id: str,
    *,
    estimated_vram_mb: int,
    last_used_at: float,
    component: str | None = None,
) -> GpuModelResidency:
    metadata = {"component": component} if component else {}
    return GpuModelResidency(
        model_id=model_id,
        task_type=task_type,
        estimated_vram_mb=estimated_vram_mb,
        resident=True,
        last_used_at=last_used_at,
        metadata=metadata,
    )


def _brokered_eviction_request(*, broker_delivery_count: int = 1) -> dict[str, object]:
    return {
        "id": "eviction-1",
        "eviction_id": "eviction-1",
        "lease_id": "lease-1",
        "task_type": "embedding",
        "model_id": "snowflake",
        "component": "model-runner",
        "status": "running",
        "estimated_freed_vram_mb": 2_500,
        "error": "",
        "routing_key": "gpu.eviction.requested",
        "correlation_id": "corr-1",
        "causation_id": "cause-1",
        "broker_delivery_count": broker_delivery_count,
        "metadata": {
            "request_profile": {
                "task_type": "rerank",
                "model_id": "qwen-reranker",
                "estimated_vram_mb": 7_000,
                "priority": 0,
                "exclusive": True,
                "share_group": "",
                "component": "worker",
                "request_id": "job-1",
                "metadata": {},
            },
            "candidate": {
                "task_type": "embedding",
                "model_id": "snowflake",
                "estimated_vram_mb": 2_500,
                "component": "model-runner",
                "metadata": {},
            },
        },
    }


class FakeGpuEvictionDb:
    def __init__(self, request: dict[str, object]) -> None:
        self.request = request
        self.completed: list[dict[str, object]] = []
        self.retried: list[dict[str, object]] = []

    def claim_gpu_eviction_request(self, **_kwargs: object) -> dict[str, object]:
        return dict(self.request)

    def complete_gpu_eviction_request(self, **kwargs: object) -> dict[str, object]:
        self.completed.append(kwargs)
        return dict(kwargs)

    def retry_gpu_eviction_request(self, **kwargs: object) -> dict[str, object]:
        self.retried.append(kwargs)
        return dict(kwargs)


def test_admission_blocks_exclusive_work_behind_active_lease():
    profile = GpuTaskProfile(task_type="rerank", model_id="qwen", estimated_vram_mb=7_000, exclusive=True)

    decision = plan_gpu_admission(
        profile,
        active_leases=[_lease("lease-1", exclusive=True)],
        config=_config(),
        now=10.0,
    )

    assert decision.granted is False
    assert decision.rejected is False
    assert decision.reason == "exclusive_conflict"


def test_admission_allows_same_share_group_within_budget():
    profile = GpuTaskProfile(
        task_type="embedding",
        model_id="snowflake",
        estimated_vram_mb=2_500,
        exclusive=False,
        share_group="embedding",
    )

    decision = plan_gpu_admission(
        profile,
        active_leases=[
            _lease(
                "lease-1",
                task_type="embedding",
                estimated_vram_mb=2_500,
                exclusive=False,
                share_group="embedding",
            )
        ],
        config=_config(vram_budget_mb=8_000, safety_margin_mb=1_000),
        now=10.0,
    )

    assert decision.granted is True
    assert decision.reason == "granted"


def test_task_profile_makes_embedding_shareable_by_default():
    profile = task_profile("embedding", model_id="snowflake")

    assert profile.exclusive is False
    assert profile.share_group == "embedding"


def test_task_profile_keeps_heavy_work_exclusive_by_default():
    profile = task_profile("rerank", model_id="qwen")

    assert profile.exclusive is True
    assert profile.share_group == ""


def test_task_profile_honours_explicit_exclusive_override():
    profile = task_profile("embedding", model_id="snowflake", exclusive=True, share_group="")

    assert profile.exclusive is True
    assert profile.share_group == ""


def test_admission_rejects_when_profile_exceeds_available_vram():
    profile = GpuTaskProfile(task_type="ollama_vision", model_id="qwen3-vl:8b", estimated_vram_mb=8_000)

    decision = plan_gpu_admission(
        profile,
        active_leases=[],
        config=_config(vram_budget_mb=8_000, safety_margin_mb=1_500),
        live_free_vram_mb=7_000,
        now=10.0,
    )

    assert decision.granted is False
    assert decision.rejected is True
    assert decision.reason == "vram_budget_exceeded"


def test_live_memory_admission_does_not_double_count_resident_models():
    profile = GpuTaskProfile(
        task_type="rerank",
        model_id="drawais/Qwen3-Reranker-4B-AWQ-INT4",
        estimated_vram_mb=7_000,
        exclusive=True,
    )
    resident_embedding = GpuModelResidency(
        model_id="Snowflake/snowflake-arctic-embed-l-v2.0",
        task_type="embedding",
        estimated_vram_mb=2_500,
        resident=True,
        last_used_at=10.0,
        metadata={},
    )

    decision = plan_gpu_admission(
        profile,
        active_leases=[],
        resident_models=[resident_embedding],
        config=_config(vram_budget_mb=10_000, safety_margin_mb=1_000),
        live_free_vram_mb=8_500,
        now=10.0,
    )

    assert decision.granted is True
    assert decision.reason == "granted"
    assert decision.resident_vram_mb == 2_500
    assert decision.available_vram_mb == 7_500


def test_loaded_idle_requested_model_grants_with_zero_incremental_vram_despite_low_live_free_memory():
    profile = GpuTaskProfile(
        task_type="embedding",
        model_id="Snowflake/snowflake-arctic-embed-l-v2.0",
        estimated_vram_mb=2_500,
        exclusive=False,
        share_group="embedding",
    )

    decision = plan_gpu_admission(
        profile,
        active_leases=[],
        resident_models=[
            _resident(
                "embedding",
                "Snowflake/snowflake-arctic-embed-l-v2.0",
                estimated_vram_mb=2_500,
                last_used_at=10.0,
                component="model-runner",
            )
        ],
        config=_config(vram_budget_mb=10_000, safety_margin_mb=1_000),
        live_free_vram_mb=512,
        now=20.0,
    )

    assert decision.granted is True
    assert decision.rejected is False
    assert decision.reason == "granted"
    assert decision.resident_hit is True
    assert decision.incremental_vram_mb == 0
    assert decision.available_vram_mb == 0


def test_eviction_candidates_exclude_requested_model_and_use_lru_idle_models():
    profile = GpuTaskProfile(task_type="ocr_document", model_id="PaddleOCR-VL", estimated_vram_mb=8_000)

    candidates = select_gpu_eviction_candidates(
        profile,
        resident_models=[
            _resident("ocr_document", "PaddleOCR-VL", estimated_vram_mb=8_000, last_used_at=1.0, component="paddle-runner"),
            _resident("embedding", "Snowflake/snowflake-arctic-embed-l-v2.0", estimated_vram_mb=2_500, last_used_at=2.0),
            _resident("rerank", "drawais/Qwen3-Reranker-4B-AWQ-INT4", estimated_vram_mb=7_000, last_used_at=3.0),
        ],
        active_leases=[],
        waiting_leases=[],
        required_vram_mb=8_000,
        available_vram_mb=1_000,
    )

    assert [(item.task_type, item.model_id) for item in candidates] == [
        ("embedding", "Snowflake/snowflake-arctic-embed-l-v2.0"),
        ("rerank", "drawais/Qwen3-Reranker-4B-AWQ-INT4"),
    ]
    assert candidates[0].component == "model-runner"
    assert candidates[1].component == "model-runner"


def test_eviction_candidates_protect_active_and_waiting_residents():
    profile = GpuTaskProfile(task_type="ocr_document", model_id="PaddleOCR-VL", estimated_vram_mb=8_000)

    candidates = select_gpu_eviction_candidates(
        profile,
        resident_models=[
            _resident("embedding", "Snowflake/snowflake-arctic-embed-l-v2.0", estimated_vram_mb=2_500, last_used_at=1.0),
            _resident("rerank", "drawais/Qwen3-Reranker-4B-AWQ-INT4", estimated_vram_mb=7_000, last_used_at=2.0),
            _resident("asr", "large-v3-turbo", estimated_vram_mb=6_000, last_used_at=3.0),
        ],
        active_leases=[
            _lease(
                "active-rerank",
                task_type="rerank",
                model_id="drawais/Qwen3-Reranker-4B-AWQ-INT4",
                estimated_vram_mb=7_000,
            )
        ],
        waiting_leases=[
            _lease(
                "waiting-asr",
                task_type="asr",
                model_id="large-v3-turbo",
                estimated_vram_mb=6_000,
                status="waiting",
            )
        ],
        required_vram_mb=8_000,
        available_vram_mb=1_000,
    )

    assert [(item.task_type, item.model_id) for item in candidates] == [
        ("embedding", "Snowflake/snowflake-arctic-embed-l-v2.0")
    ]


def test_admission_reports_eviction_candidates_before_rejecting_for_vram():
    profile = GpuTaskProfile(task_type="ocr_document", model_id="PaddleOCR-VL", estimated_vram_mb=8_000)

    decision = plan_gpu_admission(
        profile,
        active_leases=[],
        resident_models=[
            _resident("embedding", "Snowflake/snowflake-arctic-embed-l-v2.0", estimated_vram_mb=2_500, last_used_at=1.0),
            _resident("rerank", "drawais/Qwen3-Reranker-4B-AWQ-INT4", estimated_vram_mb=7_000, last_used_at=2.0),
        ],
        config=_config(vram_budget_mb=10_000, safety_margin_mb=1_000),
        live_free_vram_mb=2_000,
        now=20.0,
    )

    assert decision.granted is False
    assert decision.rejected is True
    assert decision.reason == "vram_budget_exceeded"
    assert [(item.task_type, item.model_id) for item in decision.eviction_candidates] == [
        ("embedding", "Snowflake/snowflake-arctic-embed-l-v2.0"),
        ("rerank", "drawais/Qwen3-Reranker-4B-AWQ-INT4"),
    ]


def test_in_process_scheduler_clears_residency_for_one_component():
    scheduler = InProcessGpuScheduler(_config())
    scheduler.record_model_residency(
        _resident(
            "embedding",
            "Snowflake/snowflake-arctic-embed-l-v2.0",
            estimated_vram_mb=2_500,
            last_used_at=1.0,
            component="model-runner",
        )
    )
    scheduler.record_model_residency(
        _resident("ocr_document", "PaddleOCR-VL", estimated_vram_mb=8_000, last_used_at=2.0, component="paddle-runner")
    )

    scheduler.reset_component_residency("model-runner")

    residents = {(item["task_type"], item["model_id"]) for item in scheduler.status()["model_residency"]}
    assert ("embedding", "Snowflake/snowflake-arctic-embed-l-v2.0") not in residents
    assert ("ocr_document", "PaddleOCR-VL") in residents


def test_postgres_scheduler_reset_component_residency_casts_sql_parameters():
    calls: list[tuple[str, tuple[object, ...]]] = []

    class RecordingScheduler(PostgresGpuScheduler):
        def _execute(self, statement: str, params: tuple[object, ...]) -> None:
            calls.append((statement, params))

    scheduler = RecordingScheduler(_config(mode="postgres"), database_url="postgresql://example")

    scheduler.reset_component_residency("model-runner")

    assert len(calls) == 1
    statement, params = calls[0]
    assert "jsonb_build_object('startup_cleared_by', %s::text)" in statement
    assert "task_type = ANY(%s::text[])" in statement
    assert params == (
        "model-runner",
        "model-runner",
        "model-runner",
        ["embedding", "rerank"],
    )


def test_postgres_eviction_keeps_residency_when_vram_does_not_recover(monkeypatch):
    records: list[dict[str, object]] = []
    evicted: list[GpuEvictionCandidate] = []
    sleeps: list[float] = []

    class RecordingScheduler(PostgresGpuScheduler):
        def _evict_candidate(self, candidate: GpuEvictionCandidate) -> dict[str, object]:
            return {"ok": True, "candidate": candidate.model_id}

        def _record_eviction(
            self,
            lease_id: str,
            candidate: GpuEvictionCandidate,
            *,
            status: str,
            error: str = "",
            metadata: dict[str, object] | None = None,
        ) -> None:
            records.append(
                {
                    "lease_id": lease_id,
                    "candidate": candidate,
                    "status": status,
                    "error": error,
                    "metadata": metadata or {},
                }
            )

        def _mark_residency_evicted(self, candidate: GpuEvictionCandidate) -> None:
            evicted.append(candidate)

    monkeypatch.setattr(gpu_scheduler, "_live_free_vram_mb", lambda: 1_200)
    monkeypatch.setattr(gpu_scheduler.time, "sleep", lambda seconds: sleeps.append(float(seconds)))
    scheduler = RecordingScheduler(_config(mode="postgres", eviction_request_timeout_seconds=0.0), database_url="postgresql://example")
    profile = GpuTaskProfile(task_type="ollama_vision", model_id="qwen3-vl:8b", estimated_vram_mb=9_000)
    candidate = GpuEvictionCandidate(task_type="embedding", model_id="snowflake", estimated_vram_mb=2_500, component="model-runner")

    assert scheduler._attempt_evictions(profile, "lease-1", [candidate]) is False

    assert evicted == []
    assert len(records) == 1
    assert records[0]["status"] == "failed"
    assert "VRAM did not recover" in str(records[0]["error"])
    assert sleeps == [10.0, 10.0]


def test_postgres_eviction_retries_twice_before_failed_verification(monkeypatch):
    attempts: list[str] = []
    sleeps: list[float] = []

    class RecordingScheduler(PostgresGpuScheduler):
        def _evict_candidate(self, candidate: GpuEvictionCandidate) -> dict[str, object]:
            attempts.append(candidate.model_id)
            return {"ok": True}

        def _record_eviction(
            self,
            lease_id: str,
            candidate: GpuEvictionCandidate,
            *,
            status: str,
            error: str = "",
            metadata: dict[str, object] | None = None,
        ) -> None:
            attempts.append(f"{status}:{metadata.get('attempts') if metadata else None}")

        def _mark_residency_evicted(self, candidate: GpuEvictionCandidate) -> None:
            attempts.append("marked")

    monkeypatch.setattr(gpu_scheduler, "_live_free_vram_mb", lambda: 500)
    monkeypatch.setattr(gpu_scheduler.time, "sleep", lambda seconds: sleeps.append(float(seconds)))
    scheduler = RecordingScheduler(_config(mode="postgres", eviction_request_timeout_seconds=0.0), database_url="postgresql://example")
    profile = GpuTaskProfile(task_type="ocr_document", model_id="PaddleOCR-VL", estimated_vram_mb=8_000)
    candidate = GpuEvictionCandidate(task_type="rerank", model_id="qwen", estimated_vram_mb=7_000, component="model-runner")

    assert scheduler._attempt_evictions(profile, "lease-1", [candidate]) is False

    assert attempts == ["qwen", "qwen", "qwen", "failed:3"]
    assert sleeps == [10.0, 10.0]


def test_postgres_eviction_marks_residency_only_after_vram_recovers(monkeypatch):
    records: list[dict[str, object]] = []
    evicted: list[GpuEvictionCandidate] = []
    free_vram = iter([1_200, 5_000])

    class RecordingScheduler(PostgresGpuScheduler):
        def _evict_candidate(self, candidate: GpuEvictionCandidate) -> dict[str, object]:
            return {"ok": True, "candidate": candidate.model_id}

        def _record_eviction(
            self,
            lease_id: str,
            candidate: GpuEvictionCandidate,
            *,
            status: str,
            error: str = "",
            metadata: dict[str, object] | None = None,
        ) -> None:
            records.append({"status": status, "error": error, "metadata": metadata or {}})

        def _mark_residency_evicted(self, candidate: GpuEvictionCandidate) -> None:
            evicted.append(candidate)

    monkeypatch.setattr(gpu_scheduler, "_live_free_vram_mb", lambda: next(free_vram))
    scheduler = RecordingScheduler(_config(mode="postgres", eviction_request_timeout_seconds=0.0), database_url="postgresql://example")
    profile = GpuTaskProfile(task_type="ocr_image", model_id="PP-OCRv5", estimated_vram_mb=2_000)
    candidate = GpuEvictionCandidate(task_type="embedding", model_id="snowflake", estimated_vram_mb=2_500, component="model-runner")

    assert scheduler._attempt_evictions(profile, "lease-1", [candidate]) is True

    assert evicted == [candidate]
    assert records == [{"status": "succeeded", "error": "", "metadata": records[0]["metadata"]}]
    assert records[0]["metadata"]["attempts"] == 1


def test_postgres_eviction_waits_for_verification_before_retrying_admission(monkeypatch):
    order: list[str] = []
    free_vram = iter([1_200, 5_000])

    class RecordingScheduler(PostgresGpuScheduler):
        def _evict_candidate(self, candidate: GpuEvictionCandidate) -> dict[str, object]:
            order.append("unload")
            return {"ok": True}

        def _record_eviction(
            self,
            lease_id: str,
            candidate: GpuEvictionCandidate,
            *,
            status: str,
            error: str = "",
            metadata: dict[str, object] | None = None,
        ) -> None:
            order.append(f"record:{status}")

        def _mark_residency_evicted(self, candidate: GpuEvictionCandidate) -> None:
            order.append("mark-residency")

    def live_free_vram() -> int:
        order.append("verify")
        return next(free_vram)

    monkeypatch.setattr(gpu_scheduler, "_live_free_vram_mb", live_free_vram)
    scheduler = RecordingScheduler(_config(mode="postgres", eviction_request_timeout_seconds=0.0), database_url="postgresql://example")
    profile = GpuTaskProfile(task_type="ocr_image", model_id="PP-OCRv5", estimated_vram_mb=2_000)
    candidate = GpuEvictionCandidate(task_type="embedding", model_id="snowflake", estimated_vram_mb=2_500, component="model-runner")

    assert scheduler._attempt_evictions(profile, "lease-1", [candidate]) is True

    assert order == ["verify", "unload", "verify", "record:succeeded", "mark-residency"]


def test_postgres_scheduler_queues_eviction_and_returns_retryable_busy_instead_of_inline_unload(monkeypatch):
    events: list[tuple[str, object]] = []
    candidate = GpuEvictionCandidate(
        task_type="embedding",
        model_id="snowflake",
        estimated_vram_mb=2_500,
        component="model-runner",
    )

    class RecordingScheduler(PostgresGpuScheduler):
        def _insert_waiting(self, profile: GpuTaskProfile, lease_id: str) -> None:
            events.append(("insert", lease_id))

        def _try_grant(self, profile: GpuTaskProfile, lease_id: str):
            return GpuAdmissionDecision(
                granted=False,
                rejected=True,
                reason="vram_busy",
                active_vram_mb=0,
                resident_vram_mb=2_500,
                available_vram_mb=500,
                eviction_candidates=[candidate],
            )

        def _enqueue_eviction_requests(
            self,
            profile: GpuTaskProfile,
            lease_id: str,
            candidates: list[GpuEvictionCandidate],
        ) -> dict[str, object]:
            events.append(("enqueue", lease_id, candidates))
            return {"queued": len(candidates)}

        def _attempt_evictions(self, *_args, **_kwargs) -> bool:
            raise AssertionError("lease admission must not unload models inline")

        def _mark_terminal(self, lease_id: str, status: str) -> None:
            events.append(("terminal", lease_id, status))

    monkeypatch.setattr(gpu_scheduler.time, "sleep", lambda _seconds: None)
    scheduler = RecordingScheduler(_config(mode="postgres", eviction_enabled=True), database_url="postgresql://example")
    profile = GpuTaskProfile(task_type="ocr_document", model_id="PaddleOCR-VL", estimated_vram_mb=8_000)

    with pytest.raises(GpuLeaseTimeout) as exc:
        scheduler.acquire(profile)

    assert exc.value.retry_after_seconds == 5.0
    assert events[1] == ("enqueue", events[0][1], [candidate])
    assert events[2] == ("terminal", events[0][1], "timed_out")


def test_brokered_gpu_eviction_noop_becomes_terminal_without_retry(monkeypatch):
    db = FakeGpuEvictionDb(_brokered_eviction_request(broker_delivery_count=3))

    def fake_evict_once(self, profile, candidate, *, attempt):  # noqa: ANN001
        return GpuEvictionVerificationResult(
            verified=False,
            payload={"unloaded": False},
            error=(
                "VRAM did not recover after eviction "
                "(before_free_vram_mb=7556, after_free_vram_mb=7554, "
                "freed_vram_mb=0, required_vram_mb=7000, available_vram_mb=6530)"
            ),
            metadata={
                "attempt": attempt,
                "before_free_vram_mb": 7556,
                "after_free_vram_mb": 7554,
                "freed_vram_mb": 0,
                "required_vram_mb": 7000,
                "available_vram_mb": 6530,
                "candidate_estimated_vram_mb": 2500,
            },
        )

    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", eviction_enabled=True))
    monkeypatch.setattr(gpu_scheduler, "_gpu_eviction_delivery_limit", lambda: 8)
    monkeypatch.setattr(PostgresGpuScheduler, "_evict_candidate_once", fake_evict_once)
    monkeypatch.setattr(
        PostgresGpuScheduler,
        "_mark_residency_evicted",
        lambda *_args, **_kwargs: pytest.fail("residency must not be marked evicted for a no-op unload"),
    )

    result = gpu_scheduler.process_gpu_eviction_request(
        eviction_id="eviction-1",
        worker_id="worker-1",
        broker_message_id="message-1",
        database_module=db,
    )

    assert result["status"] == "failed"
    assert result["retryable"] is False
    assert db.retried == []
    assert len(db.completed) == 1
    assert db.completed[0]["status"] == "failed"
    assert db.completed[0]["metadata"]["terminal_reason"] == "eviction_did_not_free_vram"


def test_brokered_gpu_eviction_transient_failure_still_retries(monkeypatch):
    db = FakeGpuEvictionDb(_brokered_eviction_request(broker_delivery_count=1))

    def fake_evict_once(self, profile, candidate, *, attempt):  # noqa: ANN001
        return GpuEvictionVerificationResult(
            verified=False,
            error="HTTP 503 while unloading model",
            metadata={"attempt": attempt, "error_type": "HTTPError"},
        )

    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", eviction_enabled=True))
    monkeypatch.setattr(gpu_scheduler, "_gpu_eviction_delivery_limit", lambda: 8)
    monkeypatch.setattr(PostgresGpuScheduler, "_evict_candidate_once", fake_evict_once)

    result = gpu_scheduler.process_gpu_eviction_request(
        eviction_id="eviction-1",
        worker_id="worker-1",
        broker_message_id="message-1",
        database_module=db,
    )

    assert result["status"] == "retrying"
    assert result["retryable"] is True
    assert db.completed == []
    assert len(db.retried) == 1
    assert db.retried[0]["error"] == "HTTP 503 while unloading model"


def test_worker_task_profile_uses_background_timeout_setting(monkeypatch):
    values = {
        "gpu.scheduler.vram_budget_mb": 10_240,
        "gpu.scheduler.lease_ttl_seconds": 120,
        "gpu.scheduler.background_timeout_seconds": 0.5,
        "gpu.scheduler.ocr_image_vram_mb": 2_000,
    }
    monkeypatch.setattr(gpu_scheduler, "_scheduler_setting_values", lambda: values)

    worker_profile = task_profile("ocr_image", model_id="PP-OCRv5", component="worker")
    model_runner_profile = task_profile("ocr_image", model_id="PP-OCRv5", component="model-runner")

    assert worker_profile.timeout_seconds == 0.5
    assert model_runner_profile.timeout_seconds is None


def test_gpu_lease_heartbeats_until_released():
    heartbeats: list[str] = []
    heartbeat_event = threading.Event()

    class FakeScheduler:
        config = GpuSchedulerConfig(heartbeat_interval_seconds=0.01)

        def heartbeat(self, lease_id: str) -> None:
            heartbeats.append(lease_id)
            heartbeat_event.set()

        def release(self, lease_id: str) -> None:
            heartbeats.append(f"release:{lease_id}")

    lease = GpuLease(
        FakeScheduler(),  # type: ignore[arg-type]
        _lease("lease-heartbeat", status="running"),
    )

    assert heartbeat_event.wait(timeout=0.2)
    lease.release()
    heartbeat_count = len([item for item in heartbeats if item == "lease-heartbeat"])
    time.sleep(0.03)

    assert heartbeat_count >= 1
    assert len([item for item in heartbeats if item == "lease-heartbeat"]) == heartbeat_count
    assert "release:lease-heartbeat" in heartbeats


def test_admission_recovers_stale_running_leases_before_planning():
    profile = GpuTaskProfile(task_type="embedding", model_id="snowflake", estimated_vram_mb=2_000)

    decision = plan_gpu_admission(
        profile,
        active_leases=[_lease("stale-lease", expires_at=5.0)],
        config=_config(),
        now=10.0,
    )

    assert decision.granted is True
    assert decision.recovered_lease_ids == ["stale-lease"]


def test_in_process_scheduler_times_out_waiting_for_exclusive_work():
    scheduler = InProcessGpuScheduler(_config(default_timeout_seconds=0.02))
    held = GpuTaskProfile(task_type="rerank", model_id="qwen", estimated_vram_mb=7_000)
    waiting = GpuTaskProfile(task_type="embedding", model_id="snowflake", estimated_vram_mb=2_000, timeout_seconds=0.02)

    with scheduler.acquire(held):
        with pytest.raises(GpuLeaseTimeout) as exc_info:
            scheduler.acquire(waiting)

    assert exc_info.value.retry_after_seconds > 0


def test_in_process_scheduler_allows_default_embedding_work_to_share_gpu():
    scheduler = InProcessGpuScheduler(_config(default_timeout_seconds=0.02))
    held = task_profile("embedding", model_id="snowflake", timeout_seconds=0.02)
    waiting = task_profile("embedding", model_id="snowflake", timeout_seconds=0.02)

    with scheduler.acquire(held):
        with scheduler.acquire(waiting):
            status = scheduler.status()

    assert status["counts"]["running"] == 2


def test_in_process_scheduler_grants_waiting_work_after_release():
    scheduler = InProcessGpuScheduler(_config(default_timeout_seconds=1.0))
    held = GpuTaskProfile(task_type="rerank", model_id="qwen", estimated_vram_mb=7_000)
    waiting = GpuTaskProfile(task_type="embedding", model_id="snowflake", estimated_vram_mb=2_000, timeout_seconds=1.0)
    granted = threading.Event()

    def wait_for_lease() -> None:
        with scheduler.acquire(waiting):
            granted.set()

    with scheduler.acquire(held):
        thread = threading.Thread(target=wait_for_lease)
        thread.start()
        time.sleep(0.05)
        assert granted.is_set() is False
    thread.join(timeout=1.0)

    assert granted.is_set() is True
    status = scheduler.status()
    assert status["counts"]["released"] == 2
