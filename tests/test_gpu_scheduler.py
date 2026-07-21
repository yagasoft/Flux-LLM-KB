from __future__ import annotations

from contextlib import contextmanager
import threading
import time

import pytest

from flux_llm_kb import database, gpu_scheduler
from flux_llm_kb.gpu_scheduler import (
    GpuAdmissionDecision,
    GpuEvictionCandidate,
    GpuEvictionVerificationResult,
    GpuLeaseRecord,
    GpuLeaseRejected,
    GpuLeaseTimeout,
    GpuLease,
    GpuModelResidency,
    GpuRequestShape,
    GpuSchedulerConfig,
    GpuTaskProfile,
    GpuVramCalibration,
    InProcessGpuScheduler,
    PostgresGpuScheduler,
    plan_gpu_admission,
    resolve_vram_reservation,
    shape_bucket_for_profile,
    select_gpu_eviction_candidates,
    task_profile,
)
from flux_llm_kb.gpu_reconciliation import GpuReconciliationObservation, RuntimeInventorySnapshot


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


def test_in_process_scheduler_marks_permanent_capacity_rejection_unschedulable():
    scheduler = InProcessGpuScheduler(
        _config(vram_budget_mb=1_000, safety_margin_mb=0),
        reconciliation_provider=lambda: None,
    )

    with pytest.raises(GpuLeaseRejected) as exc_info:
        scheduler.acquire(GpuTaskProfile(task_type="embedding", model_id="Snowflake/test", estimated_vram_mb=2_000))

    assert str(exc_info.value) == "unschedulable"
    assert exc_info.value.capacity_state == "unschedulable"
    assert exc_info.value.retryable is False


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
    heartbeat_at: float = 2.0,
    metadata: dict[str, object] | None = None,
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
        heartbeat_at=heartbeat_at,
        expires_at=expires_at,
        released_at=None,
        metadata=dict(metadata or {}),
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
    def __init__(
        self,
        request: dict[str, object],
        *,
        complete_result: dict[str, object] | None = None,
        retry_result: dict[str, object] | None = None,
    ) -> None:
        self.request = request
        self.completed: list[dict[str, object]] = []
        self.retried: list[dict[str, object]] = []
        self.residency_updates: list[dict[str, object]] = []
        self.cas_rejections: list[dict[str, object]] = []
        self.complete_result = complete_result
        self.retry_result = retry_result

    def claim_gpu_eviction_request(self, **_kwargs: object) -> dict[str, object]:
        return dict(self.request)

    def complete_gpu_eviction_request(self, **kwargs: object) -> dict[str, object]:
        self.completed.append(kwargs)
        result = dict(self.complete_result or kwargs)
        verification = kwargs.get("residency_verification")
        if isinstance(verification, dict) and not result.get("cas_rejected"):
            self.residency_updates.append(dict(verification))
        return result

    def retry_gpu_eviction_request(self, **kwargs: object) -> dict[str, object]:
        self.retried.append(kwargs)
        return dict(self.retry_result or kwargs)

    @contextmanager
    def gpu_control_lock(self, **_kwargs: object):
        yield None

    def list_active_gpu_leases(self, **_kwargs: object) -> list[dict[str, object]]:
        return []

    def persist_gpu_runtime_observation(self, _observation: object, **_kwargs: object) -> None:
        return None

    def update_gpu_residency_verification(self, **kwargs: object) -> None:
        self.residency_updates.append(kwargs)

    def record_gpu_eviction_cas_rejection(self, **kwargs: object) -> None:
        self.cas_rejections.append(dict(kwargs))


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
    assert decision.reason == "unschedulable"


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


def test_resident_snowflake_hit_reserves_working_set_headroom():
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

    assert decision.granted is False
    assert decision.rejected is True
    assert decision.reason == "vram_budget_exceeded"
    assert decision.resident_hit is True
    assert decision.incremental_vram_mb > 0
    assert decision.working_set_mb > 0
    assert decision.reserved_peak_mb >= decision.working_set_mb
    assert decision.available_vram_mb == 0


def test_postgres_admission_trims_a_confirmed_resident_model_cache_before_rejecting():
    events: list[str] = []

    class RecordingScheduler(PostgresGpuScheduler):
        def __init__(self):
            super().__init__(_config(mode="postgres"), database_url="postgresql://example")
            self.attempts = 0

        def _insert_waiting(self, _profile, lease_id):
            return lease_id

        def _try_grant(self, _profile, lease_id):
            self.attempts += 1
            if self.attempts == 1:
                return GpuAdmissionDecision(
                    granted=False,
                    rejected=True,
                    reason="vram_budget_exceeded",
                    active_vram_mb=0,
                    resident_vram_mb=6_500,
                    available_vram_mb=500,
                    resident_hit=True,
                )
            return _lease(lease_id, task_type="embedding", model_id="Snowflake/test")

        def _trim_requested_model_allocator(self, _profile):
            events.append("trim")
            return {"trimmed": True, "retryable": False, "reason": "trimmed"}

        def _record_allocator_trim_outcome(self, _lease_id, _result):
            return None

        def _mark_terminal(self, _lease_id, _status):
            return None

    scheduler = RecordingScheduler()
    profile = GpuTaskProfile(task_type="embedding", model_id="Snowflake/test", estimated_vram_mb=2_500)

    with scheduler.acquire(profile) as lease:
        assert lease.id

    assert scheduler.attempts == 2
    assert events == ["trim"]


def test_allocator_trim_is_limited_to_runtime_owners_that_expose_a_fenced_trim_endpoint():
    scheduler = PostgresGpuScheduler(_config(mode="postgres"), database_url="postgresql://example")
    decision = GpuAdmissionDecision(
        granted=False,
        rejected=True,
        reason="vram_budget_exceeded",
        active_vram_mb=0,
        resident_vram_mb=6_500,
        available_vram_mb=500,
        resident_hit=True,
    )

    assert scheduler._should_trim_requested_model_allocator(
        GpuTaskProfile(task_type="embedding", model_id="Snowflake/test", estimated_vram_mb=2_500), decision
    )
    assert not scheduler._should_trim_requested_model_allocator(
        GpuTaskProfile(task_type="video_extraction", model_id="ffmpeg", estimated_vram_mb=2_500), decision
    )


def test_runtime_trim_posts_a_fenced_cache_release_without_unloading_the_model(monkeypatch):
    candidate = GpuEvictionCandidate("embedding", "Snowflake/test", 2_500, "model-runner")
    target = {
        "owner": "model-runner",
        "generation": "generation-1",
        "activity": 4,
    }
    posted: list[tuple[str, str, dict[str, object]]] = []

    def post(base_url, path, payload, **_kwargs):
        posted.append((base_url, path, payload))
        return {"trim_confirmed": True, "unloaded": False, "resident": True}

    monkeypatch.setattr(gpu_scheduler, "_post_json", post)
    result = gpu_scheduler._runtime_trim(
        _config(mode="postgres", model_runner_base_url="http://model-runner"),
        candidate,
        target,
    )

    assert result["trim_confirmed"] is True
    assert result["unloaded"] is False
    assert posted == [
        (
            "http://model-runner",
            "/v1/gpu/trim",
            {
                "task_type": "embedding",
                "model_id": "Snowflake/test",
                "expected_generation": "generation-1",
                "expected_activity_sequence": 4,
            },
        )
    ]


def test_embedding_shape_buckets_distinguish_batch_one_and_sixteen_without_text_content():
    one = GpuTaskProfile(
        task_type="embedding",
        model_id="Snowflake/snowflake-arctic-embed-l-v2.0",
        metadata={"input_count": 1, "total_input_characters": 40, "max_input_characters": 40, "dimensions": 1024},
    )
    sixteen = GpuTaskProfile(
        task_type="embedding",
        model_id="Snowflake/snowflake-arctic-embed-l-v2.0",
        metadata={"input_count": 16, "total_input_characters": 640, "max_input_characters": 40, "dimensions": 1024},
    )

    one_bucket = shape_bucket_for_profile(one)
    sixteen_bucket = shape_bucket_for_profile(sixteen)

    assert one_bucket != sixteen_bucket
    assert "count=1" in one_bucket
    assert "count=9-16" in sixteen_bucket
    assert "Snowflake" not in one_bucket
    assert "total_input_characters" not in one_bucket


def test_task_profile_uses_opaque_class_and_stable_admission_key():
    profile = task_profile(
        "embedding",
        model_id="Snowflake/test",
        component="worker",
        request_id="job-123",
        priority_class="interactive",
        metadata={"input_count": 1, "dimensions": 1024},
    )

    assert profile.priority_class == "interactive"
    assert profile.admission_key == "job-123:embedding:embedding:Snowflake/test:embedding|count=1|chars=0-256|item=0-256|dims=1024"
    assert profile.shape_bucket == "embedding|count=1|chars=0-256|item=0-256|dims=1024"


@pytest.mark.parametrize(
    ("task_type", "priority_class", "expected_priority", "expected_lane"),
    [
        ("embedding", "interactive", 500, "mcp_retrieval"),
        ("embedding", "background", 400, "document_indexing"),
        ("ocr_image", "background", 300, "image_ocr"),
        ("ocr_image", "interactive", 300, "image_ocr"),
        ("ollama_vision", "background", 200, "image_llm_enrichment"),
        ("ollama_vision", "interactive", 200, "image_llm_enrichment"),
        ("asr", "background", 100, "video_extraction"),
        ("asr", "interactive", 100, "video_extraction"),
    ],
)
def test_task_profile_assigns_explicit_gpu_priority_lanes(task_type, priority_class, expected_priority, expected_lane):
    profile = task_profile(
        task_type,
        model_id="model",
        component="worker",
        request_id="job-or-request",
        priority_class=priority_class,
    )

    assert profile.priority == expected_priority
    assert profile.metadata["priority_lane"] == expected_lane


def test_task_profile_cannot_promote_a_task_above_its_assigned_lane():
    profile = task_profile(
        "ocr_image",
        model_id="PP-OCRv5",
        request_id="request-1",
        priority=999,
        priority_class="interactive",
    )
    request = {"metadata": {"request_profile": {"task_type": "asr", "model_id": "large-v3-turbo", "priority": 999}}}

    replayed = gpu_scheduler._gpu_task_profile_from_eviction_request(request)

    assert profile.priority == 300
    assert profile.metadata["priority_lane"] == "image_ocr"
    assert replayed.priority == 100


def test_scheduler_status_exposes_mcp_only_non_preemptive_runtime_policy():
    payload = InProcessGpuScheduler(_config()).status()["preemption"]

    assert payload["mcp_only"] is True
    assert payload["fallback"] == "priority_at_safe_boundary"
    assert {item["task_type"] for item in payload["tasks"]} == {
        "embedding", "rerank", "ocr_image", "ocr_document", "asr", "ollama_vision", "video_extraction",
    }
    assert all(item["cancellation"] == "unsupported" for item in payload["tasks"])


def test_background_yield_detaches_without_an_asynchronous_grant():
    scheduler = InProcessGpuScheduler(_config(default_timeout_seconds=1.0, heartbeat_interval_seconds=0.0))
    held = GpuTaskProfile(task_type="ocr_document", model_id="qwen", estimated_vram_mb=1, exclusive=True)
    background = task_profile(
        "embedding", model_id="snowflake", request_id="job-1", priority_class="background",
        timeout_seconds=1.0, metadata={"input_count": 1, "dimensions": 1024},
    )

    with scheduler.acquire(held):
        with pytest.raises(Exception) as deferred:
            scheduler.acquire(background, yield_wait=lambda: True)
        assert deferred.value.__class__.__name__ == "GpuLeaseDeferred"

    status = scheduler.status()
    assert status["counts"]["waiting"] == 1
    assert status["counts"]["running"] == 0


def test_cold_request_reserves_load_delta_and_working_set():
    profile = GpuTaskProfile(task_type="embedding", model_id="Snowflake/test", estimated_vram_mb=2_500)
    calibration = GpuVramCalibration(load_delta_mb=1_500, working_set_mb=800, sample_count=5, source="measured")

    reservation = resolve_vram_reservation(profile, resident_hit=False, calibration=calibration)

    assert reservation.load_delta_mb == 1_500
    assert reservation.working_set_mb == 800
    assert reservation.reserved_peak_mb >= 2_300


def test_active_peak_reservations_remain_counted_until_the_lease_is_released():
    profile = GpuTaskProfile(task_type="embedding", model_id="Snowflake/test", estimated_vram_mb=2_500, exclusive=False, share_group="embedding")
    active = _lease("lease-1", estimated_vram_mb=3_000, exclusive=False, share_group="embedding")

    decision = plan_gpu_admission(
        profile,
        active_leases=[active],
        config=_config(vram_budget_mb=5_000, safety_margin_mb=0),
        now=10.0,
    )

    assert decision.granted is False
    assert decision.reason == "vram_busy"


def test_shape_larger_than_physical_or_configured_capacity_is_unschedulable():
    profile = GpuTaskProfile(task_type="embedding", model_id="Snowflake/test", estimated_vram_mb=2_500)
    calibration = GpuVramCalibration(load_delta_mb=3_500, working_set_mb=2_000, sample_count=5, source="measured")

    decision = plan_gpu_admission(
        profile,
        active_leases=[],
        config=_config(total_vram_mb=4_000, vram_budget_mb=4_500, safety_margin_mb=0),
        calibration=calibration,
        now=10.0,
    )

    assert decision.granted is False
    assert decision.rejected is True
    assert decision.reason == "unschedulable"


def test_shape_that_only_exceeds_post_safety_capacity_is_unschedulable():
    profile = GpuTaskProfile(task_type="embedding", model_id="Snowflake/test", estimated_vram_mb=3_500)

    decision = plan_gpu_admission(
        profile,
        active_leases=[],
        config=_config(total_vram_mb=4_000, vram_budget_mb=4_000, safety_margin_mb=1_024),
        now=10.0,
    )

    assert decision.rejected is True
    assert decision.reason == "unschedulable"


def test_eviction_candidates_exclude_requested_model_and_use_lru_idle_models():
    profile = GpuTaskProfile(task_type="ocr_document", model_id="PaddleOCR-VL", estimated_vram_mb=8_000)

    candidates = select_gpu_eviction_candidates(
        profile,
        resident_models=[
            _resident("ocr_document", "PaddleOCR-VL", estimated_vram_mb=8_000, last_used_at=1.0, component="paddle-runner"),
            _resident("ollama_vision", "qwen3-vl:8b", estimated_vram_mb=8_000, last_used_at=1.5, component="ollama"),
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


def test_runtime_confirmed_idle_targets_exclude_unfenced_ollama_inventory():
    observation = GpuReconciliationObservation(
        observation_id="observation-1", state="healthy", driver_used_mb=2_000, driver_free_mb=8_000,
        raw_residual_mb=0, unresolved_known_owner_mb=0, unattributed_mb=0,
        inventories=(RuntimeInventorySnapshot(
            component="ollama", owner_component="ollama", process_generation="fingerprint-1", state="present",
            models=({"task_type": "ollama_vision", "model_id": "qwen3-vl:8b", "owner_component": "ollama", "process_generation": "fingerprint-1", "activity_sequence": 0, "in_flight": 0},),
        ),),
    )

    assert gpu_scheduler._runtime_confirmed_idle_targets(observation) == set()


def test_direct_scheduler_ollama_eviction_never_calls_the_unfenced_api(monkeypatch):
    posted: list[object] = []
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: posted.append(True))
    scheduler = PostgresGpuScheduler(_config(mode="postgres", ollama_base_url="http://ollama"), database_url="postgresql://example")

    payload = scheduler._evict_candidate(
        GpuEvictionCandidate("ollama_vision", "qwen3-vl:8b", 8_000, "ollama")
    )

    assert payload == {
        "ok": True,
        "unloaded": False,
        "resident": True,
        "unload_confirmed": False,
        "reason": "unload_capability_unavailable",
    }
    assert posted == []


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


def test_postgres_try_grant_preserves_an_eviction_decision_for_broker_delivery(monkeypatch):
    class Connection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        @contextmanager
        def transaction(self):
            yield self

    waiting = _lease("lease-1", task_type="ocr_document", model_id="PaddleOCR-VL", status="waiting")
    waiting_row = dict(waiting.__dict__)
    resident = _resident(
        "embedding",
        "Snowflake/snowflake-arctic-embed-l-v2.0",
        estimated_vram_mb=2_500,
        last_used_at=1.0,
        component="model-runner",
    )
    resident_row = {
        "task_type": resident.task_type,
        "model_id": resident.model_id,
        "estimated_vram_mb": resident.estimated_vram_mb,
        "resident": resident.resident,
        "last_used_at": resident.last_used_at,
        "metadata": resident.metadata,
    }

    def fetch(_connection, statement, _params):
        if "FROM gpu_leases" in statement:
            return [waiting_row]
        if "FROM gpu_model_residency" in statement:
            return [resident_row]
        raise AssertionError(statement)

    scheduler = PostgresGpuScheduler(_config(mode="postgres"), database_url="postgresql://example")
    monkeypatch.setattr(scheduler, "_recover_stale", lambda: None)
    monkeypatch.setattr(scheduler, "_reconcile_runtime_residency", lambda: None)
    monkeypatch.setattr(scheduler, "_connection", lambda: Connection())
    monkeypatch.setattr(scheduler, "_resolve_vram_calibration", lambda _profile: None)
    monkeypatch.setattr(scheduler, "_admission_capacity_state", lambda: "healthy")
    monkeypatch.setattr(gpu_scheduler, "_fetch_dicts", fetch)
    monkeypatch.setattr(gpu_scheduler, "_live_free_vram_mb", lambda: 500)

    result = scheduler._try_grant(
        GpuTaskProfile(task_type="ocr_document", model_id="PaddleOCR-VL", estimated_vram_mb=8_000),
        "lease-1",
    )

    assert isinstance(result, GpuAdmissionDecision)
    assert result.rejected is True
    assert [(item.task_type, item.model_id) for item in result.eviction_candidates] == [
        ("embedding", "Snowflake/snowflake-arctic-embed-l-v2.0"),
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


def test_in_process_scheduler_keeps_residency_when_inventory_endpoint_is_unknown():
    observation = GpuReconciliationObservation(
        observation_id="failed", state="inventory_incomplete", driver_used_mb=0, driver_free_mb=1_000,
        raw_residual_mb=0, unresolved_known_owner_mb=0, unattributed_mb=0,
    )
    scheduler = InProcessGpuScheduler(_config(ollama_base_url="http://ollama:11434"), reconciliation_provider=lambda: observation)
    scheduler.record_model_residency(
        _resident("ollama_vision", "qwen3-vl:8b", estimated_vram_mb=9_000, last_used_at=1.0, component="ollama")
    )
    scheduler.record_model_residency(
        _resident("embedding", "Snowflake/snowflake-arctic-embed-l-v2.0", estimated_vram_mb=2_500, last_used_at=2.0)
    )

    residents = {(item["task_type"], item["model_id"]) for item in scheduler.status()["model_residency"]}

    assert ("ollama_vision", "qwen3-vl:8b") in residents
    assert ("embedding", "Snowflake/snowflake-arctic-embed-l-v2.0") in residents


def test_in_process_scheduler_exposes_injected_reconciliation_without_changing_admission():
    observation = GpuReconciliationObservation(
        observation_id="obs-1", state="reconciliation_required", driver_used_mb=3_000,
        driver_free_mb=7_000, raw_residual_mb=2_000, unresolved_known_owner_mb=0, unattributed_mb=2_000,
        inventories=(
            RuntimeInventorySnapshot(
                component="asr", process_identity="asr-1", process_count=2, process_inventory_aggregated=False,
            ),
        ),
    )
    calls = []
    scheduler = InProcessGpuScheduler(
        _config(runtime_reconciliation_mode="observation"),
        reconciliation_provider=lambda: calls.append(True) or observation,
    )

    status = scheduler.status()

    assert calls == [True]
    assert status["runtime_reconciliation"]["state"] == "reconciliation_required"
    assert status["runtime_reconciliation"]["driver_observation_state"] == "available"
    inventory = status["runtime_reconciliation"]["inventories"][0]
    assert {key: inventory[key] for key in ("component", "process_identity", "process_count", "process_inventory_aggregated", "state")} == {
        "component": "asr", "process_identity": "asr-1", "process_count": 2, "process_inventory_aggregated": False, "state": "unknown"
    }


def test_reconciliation_status_bounds_and_sanitises_external_inventory_fields():
    hostile = "owner\x00name\n" + ("x" * 300)
    observation = GpuReconciliationObservation(
        observation_id="obs-hostile", state="healthy", driver_used_mb=3_000,
        driver_free_mb=7_000, raw_residual_mb=0, unresolved_known_owner_mb=0, unattributed_mb=0,
        inventories=tuple(
            RuntimeInventorySnapshot(
                component=hostile,
                owner_component=hostile,
                process_generation=hostile,
                process_identity=hostile,
                state="external-state",
                allocator_capability="external-capability",
                known_measured_mb=100,
                known_reported_mb=80,
            )
            for _ in range(20)
        ),
    )
    scheduler = InProcessGpuScheduler(_config(), reconciliation_provider=lambda: observation)

    reconciliation = scheduler.status()["runtime_reconciliation"]
    inventory = reconciliation["inventories"]
    capacity = reconciliation["capacity"]

    assert len(inventory) == 10
    assert capacity["known_measured_mb"] == 2_000
    assert capacity["known_reported_mb"] == 1_600
    assert inventory[0]["state"] == "unknown"
    assert inventory[0]["allocator_capability"] == "unknown"
    for key in ("component", "owner_component", "process_generation", "process_identity"):
        assert len(inventory[0][key]) <= 128
        assert "\x00" not in inventory[0][key]
        assert "\n" not in inventory[0][key]


def test_scheduler_status_projects_cached_reconciliation_evidence_and_wait_counters():
    observation = GpuReconciliationObservation(
        observation_id="obs-evidence", state="reconciliation_required", driver_used_mb=9_000,
        driver_free_mb=1_000, raw_residual_mb=1_200, unresolved_known_owner_mb=300,
        unattributed_mb=900, context_allowance_mb=256, observed_at=1_000.0,
        inventories=(
            RuntimeInventorySnapshot(
                component="model-runner", owner_component="model-runner", process_generation="generation-1",
                allocator_capability="measured", known_measured_mb=7_500, known_reported_mb=7_200,
                observed_at=999.0,
            ),
        ),
    )
    calls: list[bool] = []
    scheduler = InProcessGpuScheduler(
        _config(runtime_reconciliation_mode="observation"),
        reconciliation_provider=lambda: calls.append(True) or observation,
    )
    waiting = _lease("waiting", status="waiting", expires_at=time.time() + 100)
    scheduler._leases["waiting"] = GpuLeaseRecord(
        **{
            **waiting.__dict__,
            "created_at": 900.0,
            "wait_reason": "waiting_eviction",
            "priority_class": "interactive",
            "priority": 500,
            "task_type": "rerank",
            "metadata": {"priority_lane": "mcp_retrieval"},
        }
    )

    first = scheduler.status()
    second = scheduler.status()

    evidence = first["runtime_reconciliation"]
    assert calls == [True]
    assert second["runtime_reconciliation"]["observation_id"] == evidence["observation_id"]
    assert evidence["observed_at"] == 1_000.0
    assert evidence["retry_after_seconds"] == 15
    assert evidence["capacity"] == {
        "driver_used_mb": 9_000,
        "driver_free_mb": 1_000,
        "known_measured_mb": 7_500,
        "known_reported_mb": 7_200,
        "context_allowance_mb": 256,
        "unresolved_known_owner_mb": 300,
        "unattributed_mb": 900,
        "raw_residual_mb": 1_200,
    }
    assert evidence["inventories"][0]["owner_component"] == "model-runner"
    assert evidence["inventories"][0]["process_generation"] == "generation-1"
    assert evidence["inventories"][0]["allocator_capability"] == "measured"
    assert evidence["queue"]["head"] == {
        "task_type": "rerank",
        "priority": 500,
        "priority_class": "interactive",
        "priority_lane": "mcp_retrieval",
        "wait_reason": "waiting_eviction",
    }
    assert evidence["queue"]["wait_reasons"] == {"waiting_eviction": 1}


def test_enforcement_mode_does_not_fall_back_to_mutating_legacy_ollama_reconciliation(monkeypatch):
    calls = []
    monkeypatch.setattr(
        gpu_scheduler,
        "_ollama_loaded_model_names",
        lambda _config: calls.append(True) or {"llama3:latest"},
    )
    scheduler = InProcessGpuScheduler(_config(runtime_reconciliation_mode="enforcement", ollama_base_url="http://ollama:11434"))

    scheduler._reconcile_runtime_residency()

    assert calls == []


@pytest.mark.parametrize("scheduler_type", [InProcessGpuScheduler, PostgresGpuScheduler])
def test_enforcement_provider_error_fails_closed_to_reconciliation_state(scheduler_type):
    def failed_provider():
        raise RuntimeError("inventory endpoint unavailable")

    scheduler = scheduler_type(
        _config(mode="postgres" if scheduler_type is PostgresGpuScheduler else "in_process", runtime_reconciliation_mode="enforcement"),
        reconciliation_provider=failed_provider,
    )

    scheduler._reconcile_runtime_residency()

    assert scheduler._admission_capacity_state() == "inventory_incomplete"


def test_postgres_scheduler_unavailable_status_keeps_reconciliation_contract(monkeypatch):
    scheduler = PostgresGpuScheduler(_config(mode="postgres"), database_url="postgresql://example")
    monkeypatch.setattr(scheduler, "_reconcile_runtime_residency", lambda: None)
    monkeypatch.setattr(scheduler, "_recover_stale", lambda: (_ for _ in ()).throw(RuntimeError("database unavailable")))

    status = scheduler.status()

    reconciliation = status["runtime_reconciliation"]
    assert status["status"] == "unavailable"
    assert reconciliation["state"] == "inventory_incomplete"
    assert reconciliation["driver_observation_state"] == "unavailable"
    assert reconciliation["counters"]["cas_rejections"] == 0


def test_postgres_vram_recording_surfaces_persistence_failures(monkeypatch):
    from flux_llm_kb import database

    scheduler = PostgresGpuScheduler(_config(mode="postgres"), database_url="postgresql://example")
    profile = GpuTaskProfile(task_type="embedding", model_id="Snowflake/test")
    monkeypatch.setattr(database, "record_gpu_vram_sample", lambda **_kwargs: (_ for _ in ()).throw(RuntimeError("missing table")))

    with pytest.raises(RuntimeError, match="missing table"):
        scheduler.record_vram_sample(
            profile,
            pre_load_reserved_mb=1,
            post_load_reserved_mb=2,
            execution_peak_reserved_mb=3,
            allocator_capability="measured",
            tracker_overlapped=False,
        )


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


def test_postgres_scheduler_does_not_clear_residency_when_inventory_endpoint_is_unknown(monkeypatch):
    cleared: list[tuple[str, str, str]] = []

    class RecordingScheduler(PostgresGpuScheduler):
        def _runtime_residency_rows(self, component: str) -> list[dict[str, object]]:
            assert component == "ollama"
            return [
                {"task_type": "ollama_vision", "model_id": "qwen3-vl:8b"},
                {"task_type": "ollama_vision", "model_id": "llama3:latest"},
            ]

        def _clear_runtime_residency(self, task_type: str, model_id: str, component: str, reason: str) -> None:
            cleared.append((task_type, model_id, reason))
            assert component == "ollama"

    observation = GpuReconciliationObservation(
        observation_id="failed", state="inventory_incomplete", driver_used_mb=0, driver_free_mb=1_000,
        raw_residual_mb=0, unresolved_known_owner_mb=0, unattributed_mb=0,
    )
    scheduler = RecordingScheduler(
        _config(mode="postgres", ollama_base_url="http://ollama:11434"),
        database_url="postgresql://example",
        reconciliation_provider=lambda: observation,
    )

    scheduler._reconcile_runtime_residency()

    assert cleared == []


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


def test_postgres_eviction_uses_authoritative_residency_fence_accepted_by_model_runner(monkeypatch):
    from fastapi.testclient import TestClient

    from flux_llm_kb import model_runner

    model = "Snowflake/eviction-fenced"
    model_runner._EMBEDDING_MODELS.clear()
    model_runner._EMBEDDING_MODELS[model] = object()
    client = TestClient(model_runner.create_app())
    requests = []

    def runtime_get(_base_url, path, *, timeout_seconds):
        assert path == "/v1/gpu/residency"
        assert timeout_seconds > 0
        return client.get(path).json()

    def runtime_post(_base_url, path, payload, *, timeout_seconds):
        requests.append(dict(payload))
        response = client.post(path, json=payload)
        assert response.status_code == 200
        return response.json()

    monkeypatch.setattr(gpu_scheduler, "_get_json", runtime_get)
    monkeypatch.setattr(gpu_scheduler, "_post_json", runtime_post)
    scheduler = PostgresGpuScheduler(_config(mode="postgres", model_runner_base_url="http://model-runner"), database_url="postgresql://example")

    result = scheduler._evict_candidate(
        GpuEvictionCandidate(task_type="embedding", model_id=model, estimated_vram_mb=2_500, component="model-runner")
    )

    assert result["unload_confirmed"] is True
    assert requests == [
        {
            "task_type": "embedding",
            "model_id": model,
            "expected_generation": requests[0]["expected_generation"],
            "expected_activity_sequence": 0,
        }
    ]
    assert requests[0]["expected_generation"]


@pytest.mark.parametrize("owner_component", ["", "paddle-runner"])
def test_postgres_eviction_does_not_post_without_a_matching_authoritative_owner(monkeypatch, owner_component):
    posted = []

    monkeypatch.setattr(
        gpu_scheduler,
        "_get_json",
        lambda *_args, **_kwargs: {
            "models": [
                {
                    "task_type": "embedding",
                    "model_id": "Snowflake/no-fence",
                    "owner_component": owner_component,
                    "process_generation": "generation-1",
                    "activity_sequence": 4,
                }
            ]
        },
    )
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: posted.append("unload"))
    scheduler = PostgresGpuScheduler(_config(mode="postgres", model_runner_base_url="http://model-runner"), database_url="postgresql://example")

    result = scheduler._evict_candidate(
        GpuEvictionCandidate(
            task_type="embedding",
            model_id="Snowflake/no-fence",
            estimated_vram_mb=2_500,
            component="model-runner",
        )
    )

    assert posted == []
    assert result == {
        "ok": True,
        "unloaded": False,
        "resident": False,
        "unload_confirmed": False,
        "reason": "model_not_resident",
    }


def test_postgres_eviction_does_not_post_without_a_complete_authoritative_fence(monkeypatch):
    posted = []

    monkeypatch.setattr(
        gpu_scheduler,
        "_get_json",
        lambda *_args, **_kwargs: {
            "models": [
                {
                    "task_type": "embedding",
                    "model_id": "Snowflake/no-fence",
                    "owner_component": "model-runner",
                    "process_generation": "",
                    "activity_sequence": None,
                }
            ]
        },
    )
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: posted.append("unload"))
    scheduler = PostgresGpuScheduler(_config(mode="postgres", model_runner_base_url="http://model-runner"), database_url="postgresql://example")

    result = scheduler._evict_candidate(
        GpuEvictionCandidate(
            task_type="embedding",
            model_id="Snowflake/no-fence",
            estimated_vram_mb=2_500,
            component="model-runner",
        )
    )

    assert posted == []
    assert result == {
        "ok": True,
        "unloaded": False,
        "resident": True,
        "unload_confirmed": False,
        "reason": "residency_fence_unavailable",
    }


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


def test_interactive_postgres_admission_retries_after_linked_eviction(monkeypatch):
    events = []
    candidate = GpuEvictionCandidate("embedding", "snowflake", 2_500, "model-runner")

    class RecordingScheduler(PostgresGpuScheduler):
        def __init__(self):
            super().__init__(_config(mode="postgres", heartbeat_interval_seconds=0.0), database_url="postgresql://example")
            self.attempts = 0

        def _insert_waiting(self, _profile, lease_id):
            return lease_id

        def _try_grant(self, _profile, lease_id):
            self.attempts += 1
            if self.attempts == 1:
                return GpuAdmissionDecision(False, True, "vram_budget_exceeded", 0, 2_500, 500, eviction_candidates=[candidate])
            return _lease(lease_id, task_type="rerank", model_id="qwen")

        def _enqueue_eviction_requests(self, _profile, _lease_id, _candidates):
            events.append("enqueue")
            return {"deduped": 1, "eviction_ids": ["eviction-1"]}

        def _mark_waiting_eviction(self, lease_id, *, eviction_id):
            events.append(("linked", lease_id, eviction_id))

        def _mark_terminal(self, lease_id, status):
            events.append(("terminal", lease_id, status))

    monkeypatch.setattr(gpu_scheduler.time, "sleep", lambda _seconds: None)
    scheduler = RecordingScheduler()
    profile = task_profile("rerank", model_id="qwen", request_id="request-1", priority_class="interactive")

    with scheduler.acquire(profile) as lease:
        assert lease.id

    assert scheduler.attempts == 2
    assert events == ["enqueue", ("linked", lease.id, "eviction-1"), ("terminal", lease.id, "released")]


def test_postgres_admission_reattachment_is_atomic_for_concurrent_same_key(monkeypatch):
    statements = []
    barrier = threading.Barrier(2)

    class RecordingScheduler(PostgresGpuScheduler):
        def _execute_fetchone(self, statement, params):
            statements.append((statement, params))
            barrier.wait(timeout=1.0)
            return ("shared-admission",)

        def _execute(self, *_args):
            raise AssertionError("keyed admission must use atomic upsert")

    scheduler = RecordingScheduler(_config(mode="postgres"), database_url="postgresql://example")
    profile = task_profile("embedding", model_id="snowflake", request_id="job-1", priority_class="background")
    results = []
    threads = [threading.Thread(target=lambda: results.append(scheduler._insert_waiting(profile, "lease-new"))) for _ in range(2)]

    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join(timeout=1.0)

    assert results == ["shared-admission", "shared-admission"]
    assert len(statements) == 2
    assert all("ON CONFLICT (admission_key)" in statement for statement, _params in statements)


def test_postgres_keyed_admission_binds_each_sql_placeholder():
    statements = []

    class RecordingScheduler(PostgresGpuScheduler):
        def _execute_fetchone(self, statement, params):
            statements.append((statement, params))
            return ("lease-new",)

    scheduler = RecordingScheduler(_config(mode="postgres"), database_url="postgresql://example")
    profile = task_profile("embedding", model_id="snowflake", request_id="job-1", priority_class="interactive")

    assert scheduler._insert_waiting(profile, "lease-new") == "lease-new"
    assert len(statements) == 1
    statement, params = statements[0]
    assert "ON CONFLICT (admission_key)" in statement
    assert statement.count("%s") == len(params)


def test_concurrent_reattached_acquires_reuse_one_linked_eviction_and_publish_once(monkeypatch):
    """Two callers attached to one admission must coalesce the brokered eviction."""
    candidate = GpuEvictionCandidate("embedding", "snowflake", 2_500, "model-runner")
    admission_barrier = threading.Barrier(2)
    eviction_barrier = threading.Barrier(2)
    eviction_lock = threading.Lock()
    published: list[str] = []
    linked: list[tuple[str, str]] = []
    thread_state = threading.local()
    active_evictions: dict[tuple[str, str, str], str] = {}

    def enqueue_eviction(**kwargs):
        candidate_payload = kwargs["candidate"]
        key = (
            candidate_payload["task_type"],
            candidate_payload["model_id"],
            candidate_payload["component"],
        )
        eviction_barrier.wait(timeout=1.0)
        with eviction_lock:
            existing = active_evictions.get(key)
            if existing:
                return {"eviction_id": existing, "deduped": True}
            active_evictions[key] = "eviction-1"
            published.append("eviction-1")
            return {"eviction_id": "eviction-1", "deduped": False}

    class RecordingScheduler(PostgresGpuScheduler):
        def _insert_waiting(self, _profile, _lease_id):
            return "shared-admission"

        def _try_grant(self, _profile, lease_id):
            attempts = getattr(thread_state, "attempts", 0)
            thread_state.attempts = attempts + 1
            if attempts == 0:
                admission_barrier.wait(timeout=1.0)
                return GpuAdmissionDecision(
                    False,
                    True,
                    "vram_budget_exceeded",
                    0,
                    2_500,
                    500,
                    eviction_candidates=[candidate],
                )
            return _lease(lease_id, task_type="rerank", model_id="qwen")

        def _mark_waiting_eviction(self, lease_id, *, eviction_id):
            linked.append((lease_id, eviction_id))

        def _mark_terminal(self, _lease_id, _status):
            return None

    monkeypatch.setattr(database, "enqueue_gpu_eviction_request", enqueue_eviction)
    monkeypatch.setattr(gpu_scheduler.time, "sleep", lambda _seconds: None)
    scheduler = RecordingScheduler(_config(mode="postgres", heartbeat_interval_seconds=0.0), database_url="postgresql://example")
    profile = task_profile("rerank", model_id="qwen", request_id="request-1", priority_class="interactive")
    leases: list[str] = []
    errors: list[BaseException] = []

    def acquire() -> None:
        try:
            with scheduler.acquire(profile) as lease:
                leases.append(lease.id)
        except BaseException as exc:  # pragma: no cover - assertion below retains thread diagnostics
            errors.append(exc)

    threads = [threading.Thread(target=acquire) for _ in range(2)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join(timeout=2.0)

    assert not errors
    assert leases == ["shared-admission", "shared-admission"]
    assert published == ["eviction-1"]
    assert linked == [("shared-admission", "eviction-1"), ("shared-admission", "eviction-1")]


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


def test_brokered_eviction_without_authoritative_inventory_defers_without_global_vram_proof(monkeypatch):
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

    assert result["status"] == "retrying"
    assert result["retryable"] is True
    assert db.completed == []
    assert db.retried[0]["metadata"]["terminal_reason"] == "inventory_incomplete"


def test_brokered_noop_not_resident_unload_skips_without_vram_polling(monkeypatch):
    calls: list[str] = []

    class RecordingScheduler(PostgresGpuScheduler):
        def _evict_candidate(self, candidate: GpuEvictionCandidate) -> dict[str, object]:
            calls.append(f"unload:{candidate.model_id}")
            return {"ok": True, "unloaded": False, "resident": False}

    def live_free_vram() -> int:
        calls.append("live-vram")
        return 128

    monkeypatch.setattr(gpu_scheduler, "_live_free_vram_mb", live_free_vram)
    scheduler = RecordingScheduler(
        _config(mode="postgres", eviction_request_timeout_seconds=10.0),
        database_url="postgresql://example",
    )
    profile = GpuTaskProfile(task_type="embedding", model_id="snowflake", estimated_vram_mb=2_500)
    candidate = GpuEvictionCandidate(
        task_type="ocr_document",
        model_id="PaddleOCR-VL",
        estimated_vram_mb=8_000,
        component="paddle-runner",
    )

    result = scheduler._evict_candidate_once(profile, candidate, attempt=1)

    assert calls == ["live-vram", "unload:PaddleOCR-VL"]
    assert result.verified is False
    assert result.payload == {"ok": True, "unloaded": False, "resident": False}
    assert result.error == "model not resident"
    assert result.metadata["terminal_reason"] == "model_not_resident"


def test_process_brokered_eviction_without_authoritative_absence_does_not_skip(monkeypatch):
    db = FakeGpuEvictionDb(_brokered_eviction_request())
    calls: list[str] = []

    def fake_evict_candidate(self, candidate: GpuEvictionCandidate) -> dict[str, object]:  # noqa: ANN001
        calls.append(f"unload:{candidate.model_id}")
        return {"ok": True, "unloaded": False, "resident": False}

    def live_free_vram() -> int:
        calls.append("live-vram")
        return 128

    monkeypatch.setattr(
        gpu_scheduler,
        "scheduler_config_from_settings",
        lambda: _config(mode="postgres", eviction_enabled=True, eviction_request_timeout_seconds=10.0),
    )
    monkeypatch.setattr(gpu_scheduler, "_live_free_vram_mb", live_free_vram)
    monkeypatch.setattr(PostgresGpuScheduler, "_evict_candidate", fake_evict_candidate)
    monkeypatch.setattr(
        PostgresGpuScheduler,
        "_verify_eviction_vram_recovered",
        lambda *_args, **_kwargs: pytest.fail("not-resident no-op unload must not poll VRAM recovery"),
    )
    monkeypatch.setattr(
        PostgresGpuScheduler,
        "_mark_residency_evicted",
        lambda *_args, **_kwargs: pytest.fail("not-resident no-op unload must not mark residency evicted"),
    )

    result = gpu_scheduler.process_gpu_eviction_request(
        eviction_id="eviction-1",
        worker_id="worker-1",
        broker_message_id="message-1",
        database_module=db,
    )

    assert calls == []
    assert result["status"] == "retrying"
    assert result["retryable"] is True
    assert db.completed == []
    assert db.retried[0]["metadata"]["terminal_reason"] == "inventory_incomplete"


def test_brokered_eviction_without_runtime_inventory_retries_as_inventory_incomplete(monkeypatch):
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
    assert db.retried[0]["error"] == "fresh runtime inventory is incomplete"
    assert db.retried[0]["metadata"]["terminal_reason"] == "inventory_incomplete"


def test_late_delivery_for_expired_brokered_eviction_is_acknowledged_without_unload(monkeypatch):
    request = _brokered_eviction_request()
    request["status"] = "expired"
    db = FakeGpuEvictionDb(request)

    monkeypatch.setattr(
        PostgresGpuScheduler,
        "_evict_candidate_once",
        lambda *_args, **_kwargs: pytest.fail("expired delivery must not unload"),
    )

    result = gpu_scheduler.process_gpu_eviction_request(
        eviction_id="eviction-1",
        worker_id="worker-1",
        broker_message_id="late-message-1",
        database_module=db,
    )

    assert result == {
        "eviction_id": "eviction-1",
        "status": "expired",
        "already_terminal": True,
        "retryable": False,
    }
    assert db.completed == []
    assert db.retried == []


def test_second_delivery_for_running_brokered_eviction_is_acknowledged_without_unload(monkeypatch):
    db = FakeGpuEvictionDb({"eviction_id": "eviction-1", "cas_rejected": True})
    monkeypatch.setattr(
        PostgresGpuScheduler,
        "_evict_candidate_once",
        lambda *_args, **_kwargs: pytest.fail("a rejected claim must not unload"),
    )

    result = gpu_scheduler.process_gpu_eviction_request(
        eviction_id="eviction-1", worker_id="worker-2", broker_message_id="duplicate-message", database_module=db
    )

    assert result == {"eviction_id": "eviction-1", "status": "cas_rejected", "already_terminal": True, "retryable": False}
    assert db.cas_rejections == [{
        "eviction_id": "eviction-1",
        "stage": "claim",
        "worker_id": "worker-2",
        "broker_message_id": "duplicate-message",
    }]


def test_postgres_status_counts_persisted_competing_cas_rejections(monkeypatch):
    scheduler = PostgresGpuScheduler(_config(mode="postgres"), database_url="postgresql://example")
    monkeypatch.setattr(scheduler, "_reconcile_runtime_residency", lambda: None)
    monkeypatch.setattr(scheduler, "_recover_stale", lambda: None)

    @contextmanager
    def connection():
        yield object()

    responses = iter([[], [], [], [{"cas_rejections": 1}]])
    monkeypatch.setattr(scheduler, "_connection", connection)
    monkeypatch.setattr(gpu_scheduler, "_fetch_dicts", lambda *_args, **_kwargs: next(responses))

    status = scheduler.status()

    assert status["evictions"]["counters"]["cas_rejections"] == 1
    assert status["runtime_reconciliation"] is None


def test_brokered_gpu_eviction_completion_cas_rejection_is_acknowledged_without_retry(monkeypatch):
    db = FakeGpuEvictionDb(_brokered_eviction_request(), complete_result={"eviction_id": "eviction-1", "cas_rejected": True})
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", eviction_enabled=True))
    monkeypatch.setattr(
        gpu_scheduler,
        "_runtime_evict_result",
        lambda **kwargs: (GpuEvictionVerificationResult(verified=True, metadata={"terminal_reason": "verified_unload"}), kwargs["claim_token"], kwargs["row_version"]),
    )

    result = gpu_scheduler.process_gpu_eviction_request(
        eviction_id="eviction-1", worker_id="worker-1", database_module=db
    )

    assert result == {"eviction_id": "eviction-1", "status": "cas_rejected", "already_terminal": True, "retryable": False}
    assert len(db.completed) == 1


def test_brokered_gpu_eviction_retry_cas_rejection_is_acknowledged_without_retry(monkeypatch):
    db = FakeGpuEvictionDb(_brokered_eviction_request(), retry_result={"eviction_id": "eviction-1", "cas_rejected": True})
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", eviction_enabled=True))
    monkeypatch.setattr(gpu_scheduler, "_gpu_eviction_delivery_limit", lambda: 8)
    monkeypatch.setattr(
        PostgresGpuScheduler,
        "_evict_candidate_once",
        lambda *_args, **_kwargs: GpuEvictionVerificationResult(verified=False, error="temporary failure"),
    )

    result = gpu_scheduler.process_gpu_eviction_request(
        eviction_id="eviction-1", worker_id="worker-1", database_module=db
    )

    assert result == {"eviction_id": "eviction-1", "status": "cas_rejected", "already_terminal": True, "retryable": False}
    assert len(db.retried) == 1


def test_brokered_eviction_routes_unload_to_fresh_inventory_owner_not_queued_component(monkeypatch):
    """The task-type/queued component must never select an unload endpoint."""
    request = _brokered_eviction_request()
    request.update({"claim_token": "claim-1", "row_version": 1, "runtime_generation": "generation-1", "runtime_activity_sequence": 4})
    db = FakeGpuEvictionDb(request)
    posted: list[str] = []

    def runtime_get(base_url, path, *, timeout_seconds):  # noqa: ANN001
        assert path == "/v1/gpu/residency"
        if base_url == "http://model-runner":
            return {"owner_component": "model-runner", "process_generation": "generation-1", "models": [], "allocator": []}
        return {
            "owner_component": "asr",
            "process_generation": "generation-1",
            "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "asr", "activity_sequence": 4, "in_flight": 0}],
            "allocator": [{"capability": "measured", "reserved_mb": 3_000}],
        }

    monkeypatch.setattr(gpu_scheduler, "_get_json", runtime_get)
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda base_url, *_args, **_kwargs: posted.append(base_url) or {"unload_confirmed": True})
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", model_runner_base_url="http://model-runner", asr_base_url="http://asr"))

    gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert posted == ["http://asr"]


def test_brokered_eviction_requires_allocator_release_not_global_free_vram_or_request_fit(monkeypatch):
    """Measured allocators prove their own release; a global free-VRAM value does not."""
    request = _brokered_eviction_request()
    request.update({"claim_token": "claim-1", "row_version": 1, "runtime_generation": "generation-1", "runtime_activity_sequence": 4})
    db = FakeGpuEvictionDb(request)
    phase = {"after": False}

    def runtime_get(_base_url, _path, *, timeout_seconds):  # noqa: ANN001
        if phase["after"]:
            return {
                "owner_component": "model-runner", "process_generation": "generation-1", "models": [],
                "allocator": [{"capability": "measured", "reserved_mb": 1_000}],
            }
        return {
            "owner_component": "model-runner", "process_generation": "generation-1",
            "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "activity_sequence": 4, "in_flight": 0}],
            "allocator": [{"capability": "measured", "reserved_mb": 3_000}],
        }

    monkeypatch.setattr(gpu_scheduler, "_get_json", runtime_get)
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: phase.update(after=True) or {"unload_confirmed": True, "target_present": False})
    monkeypatch.setattr(gpu_scheduler, "_live_free_vram_mb", lambda: 9_000)
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", model_runner_base_url="http://model-runner"))

    result = gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert result["status"] == "succeeded"
    assert db.completed[0]["metadata"]["terminal_reason"] == "verified_unload"
    assert db.completed[0]["metadata"]["allocator_reserved_drop_mb"] == 2_000


def test_brokered_eviction_does_not_unload_when_fresh_activity_fence_changed(monkeypatch):
    request = _brokered_eviction_request()
    request.update({"claim_token": "claim-1", "row_version": 1, "runtime_generation": "generation-1", "runtime_activity_sequence": 4})
    db = FakeGpuEvictionDb(request)
    posted: list[object] = []

    monkeypatch.setattr(
        gpu_scheduler,
        "_get_json",
        lambda *_args, **_kwargs: {
            "owner_component": "model-runner", "process_generation": "generation-1",
            "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "activity_sequence": 5, "in_flight": 0}],
            "allocator": [{"capability": "measured", "reserved_mb": 3_000}],
        },
    )
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: posted.append(True))
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", model_runner_base_url="http://model-runner"))

    result = gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert posted == []
    assert result["status"] == "failed"
    assert db.completed[0]["metadata"]["terminal_reason"] == "became_active"


def test_idle_eviction_becoming_active_is_skipped_instead_of_unloaded(monkeypatch):
    request = _brokered_eviction_request()
    request.update({
        "claim_token": "claim-1", "row_version": 1, "runtime_generation": "generation-1",
        "runtime_activity_sequence": 4, "request_reason": "idle",
    })
    db = FakeGpuEvictionDb(request)
    posted: list[object] = []
    monkeypatch.setattr(gpu_scheduler, "_get_json", lambda *_args, **_kwargs: {
        "owner_component": "model-runner", "process_generation": "generation-1",
        "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "activity_sequence": 5, "in_flight": 0}],
        "allocator": [{"capability": "measured", "reserved_mb": 3_000}],
    })
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: posted.append(True))
    monkeypatch.setattr(gpu_scheduler, "live_gpu_memory", lambda: {"gpus": [{"memory_used_mb": 3_000, "memory_free_mb": 7_000, "memory_total_mb": 10_000}]})
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", model_runner_base_url="http://model-runner"))

    result = gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert posted == []
    assert result["status"] == "skipped"
    assert db.completed[0]["metadata"]["terminal_reason"] == "became_active"


def test_idle_unload_maintenance_expires_then_queues_one_runtime_confirmed_idle_target(monkeypatch):
    events = []

    class Leader:
        connection = object()

        @staticmethod
        def is_valid():
            return True

    leader = Leader()

    @contextmanager
    def leader_lock(**_kwargs):
        events.append("leader")
        yield leader

    class FakeDatabase:
        gpu_eviction_maintenance_leader_lock = staticmethod(leader_lock)

        @staticmethod
        def expire_stale_gpu_eviction_requests(**kwargs):
            events.append(("expire", kwargs))
            return [{"id": "expired-1"}]

        @staticmethod
        def list_idle_gpu_eviction_candidates(**kwargs):
            events.append(("select", kwargs))
            return [
                {
                    "task_type": "embedding", "model_id": "snowflake", "estimated_vram_mb": 2500,
                    "component": "model-runner", "runtime_generation": "generation-1", "runtime_activity_sequence": 4,
                },
                {
                    "task_type": "embedding", "model_id": "not-confirmed", "estimated_vram_mb": 2500,
                    "component": "model-runner", "runtime_generation": "generation-1", "runtime_activity_sequence": 4,
                },
            ]

        @staticmethod
        def enqueue_gpu_eviction_request(**kwargs):
            events.append(("enqueue", kwargs))
            return {"eviction_id": "idle-1", "deduped": False}

    observation = GpuReconciliationObservation(
        observation_id="observation-1", state="healthy", driver_used_mb=2500, driver_free_mb=7500,
        raw_residual_mb=0, unresolved_known_owner_mb=0, unattributed_mb=0,
        inventories=(RuntimeInventorySnapshot(
            component="model-runner", owner_component="model-runner", process_generation="generation-1", state="present",
            models=({"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "process_generation": "generation-1", "activity_sequence": 4, "in_flight": 0},),
        ),),
    )
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", idle_unload_enabled=True, idle_unload_seconds=120))
    monkeypatch.setattr(gpu_scheduler, "_runtime_eviction_observation", lambda _config, _db, **kwargs: events.append(("reconcile", kwargs)) or observation)

    result = gpu_scheduler.run_gpu_idle_unload_maintenance(worker_id="gpu-1", database_module=FakeDatabase)

    assert result == {"status": "queued", "expired": 1, "queued": 1, "deduped": 0, "skipped": 1}
    assert events[:4] == [
        "leader",
        ("expire", {"connection": leader.connection}),
        ("reconcile", {"connection": leader.connection}),
        ("select", {"idle_unload_seconds": 120.0, "connection": leader.connection}),
    ]
    enqueue = next(item[1] for item in events if isinstance(item, tuple) and item[0] == "enqueue")
    assert enqueue["lease_id"] is None
    assert enqueue["request_reason"] == "idle"
    assert enqueue["runtime_generation"] == "generation-1"
    assert enqueue["runtime_activity_sequence"] == 4
    assert enqueue["reconciliation_observation_id"] == "observation-1"
    assert enqueue["connection"] is leader.connection


def test_idle_unload_maintenance_is_disabled_at_zero_seconds(monkeypatch):
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", idle_unload_enabled=True, idle_unload_seconds=0))

    result = gpu_scheduler.run_gpu_idle_unload_maintenance(worker_id="gpu-1", database_module=object())

    assert result == {"status": "disabled", "reason": "idle_unload_disabled"}


def test_idle_unload_maintenance_aborts_after_leader_loss_before_reconciliation_or_enqueue(monkeypatch):
    events = []

    class Leader:
        connection = object()

        def __init__(self):
            self.valid = True

        def is_valid(self):
            return self.valid

    leader = Leader()

    @contextmanager
    def leader_lock(**_kwargs):
        yield leader

    class FakeDatabase:
        gpu_eviction_maintenance_leader_lock = staticmethod(leader_lock)

        @staticmethod
        def expire_stale_gpu_eviction_requests(**kwargs):
            events.append(("expire", kwargs))
            leader.valid = False
            return []

        @staticmethod
        def list_idle_gpu_eviction_candidates(**kwargs):
            events.append(("select", kwargs))
            return []

    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", idle_unload_enabled=True, idle_unload_seconds=120))
    monkeypatch.setattr(gpu_scheduler, "_runtime_eviction_observation", lambda *_args: pytest.fail("leader loss must abort before reconciliation"))

    result = gpu_scheduler.run_gpu_idle_unload_maintenance(worker_id="gpu-1", database_module=FakeDatabase)

    assert result == {"status": "skipped", "reason": "leader_lost", "expired": 0, "queued": 0, "deduped": 0, "skipped": 0}
    assert events == [("expire", {"connection": leader.connection})]


def test_known_unmeasured_owner_defers_until_other_flux_gpu_leases_are_quiescent(monkeypatch):
    request = _brokered_eviction_request()
    request.update({"claim_token": "claim-1", "row_version": 1, "runtime_generation": "generation-1", "runtime_activity_sequence": 4})
    db = FakeGpuEvictionDb(request)
    db.list_active_gpu_leases = lambda **_kwargs: [{"id": "other-running", "task_type": "asr", "model_id": "whisper"}]  # type: ignore[method-assign]
    posted: list[object] = []

    monkeypatch.setattr(
        gpu_scheduler,
        "_get_json",
        lambda *_args, **_kwargs: {
            "owner_component": "model-runner", "process_generation": "generation-1",
            "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "activity_sequence": 4, "in_flight": 0}],
            "allocator": [{"capability": "known_unmeasured"}],
        },
    )
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: posted.append(True))
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", model_runner_base_url="http://model-runner"))

    result = gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert posted == []
    assert result["status"] == "retrying"
    assert db.retried[0]["metadata"]["terminal_reason"] == "verification_deferred"


def test_brokered_eviction_marks_target_still_present_as_unload_failed(monkeypatch):
    request = _brokered_eviction_request()
    request.update({"claim_token": "claim-1", "row_version": 1, "runtime_generation": "generation-1", "runtime_activity_sequence": 4})
    db = FakeGpuEvictionDb(request)
    inventory = {
        "owner_component": "model-runner", "process_generation": "generation-1",
        "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "activity_sequence": 4, "in_flight": 0}],
        "allocator": [{"capability": "measured", "reserved_mb": 3_000}],
    }
    monkeypatch.setattr(gpu_scheduler, "_get_json", lambda *_args, **_kwargs: inventory)
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: {"unload_confirmed": True, "target_present": True})
    monkeypatch.setattr(gpu_scheduler, "live_gpu_memory", lambda: {"gpus": [{"memory_used_mb": 3_000, "memory_free_mb": 7_000, "memory_total_mb": 10_000}]})
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", model_runner_base_url="http://model-runner"))

    result = gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert result["status"] == "failed"
    assert db.completed[0]["metadata"]["terminal_reason"] == "unload_failed"
    assert db.residency_updates[0]["runtime_state"] == "unload_failed"


def test_brokered_eviction_marks_absent_without_allocator_drop_unverified(monkeypatch):
    request = _brokered_eviction_request()
    request.update({"claim_token": "claim-1", "row_version": 1, "runtime_generation": "generation-1", "runtime_activity_sequence": 4})
    db = FakeGpuEvictionDb(request)
    phase = {"after": False}

    def runtime_get(*_args, **_kwargs):
        return (
            {"owner_component": "model-runner", "process_generation": "generation-1", "models": [], "allocator": [{"capability": "measured", "reserved_mb": 3_000}]}
            if phase["after"]
            else {"owner_component": "model-runner", "process_generation": "generation-1", "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "activity_sequence": 4, "in_flight": 0}], "allocator": [{"capability": "measured", "reserved_mb": 3_000}]}
        )

    monkeypatch.setattr(gpu_scheduler, "_get_json", runtime_get)
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: phase.update(after=True) or {"unload_confirmed": True})
    monkeypatch.setattr(gpu_scheduler, "live_gpu_memory", lambda: {"gpus": [{"memory_used_mb": 3_000, "memory_free_mb": 7_000, "memory_total_mb": 10_000}]})
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", model_runner_base_url="http://model-runner"))

    result = gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert result["status"] == "failed"
    assert db.completed[0]["metadata"]["terminal_reason"] == "memory_release_unverified"
    assert db.completed[0]["metadata"]["capacity_state"] == "reconciliation_required"
    assert db.residency_updates[0]["runtime_state"] == "memory_release_unverified"
    assert db.residency_updates[0]["capacity_state"] == "reconciliation_required"


def test_known_unmeasured_owner_defers_when_quiet_window_has_no_driver_release(monkeypatch):
    request = _brokered_eviction_request()
    request.update({"claim_token": "claim-1", "row_version": 1, "runtime_generation": "generation-1", "runtime_activity_sequence": 4})
    db = FakeGpuEvictionDb(request)
    phase = {"after": False}
    monkeypatch.setattr(
        gpu_scheduler,
        "_get_json",
        lambda *_args, **_kwargs: (
            {"owner_component": "model-runner", "process_generation": "generation-1", "models": [], "allocator": [{"capability": "known_unmeasured"}]}
            if phase["after"]
            else {"owner_component": "model-runner", "process_generation": "generation-1", "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "activity_sequence": 4, "in_flight": 0}], "allocator": [{"capability": "known_unmeasured"}]}
        ),
    )
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: phase.update(after=True) or {"unload_confirmed": True})
    monkeypatch.setattr(gpu_scheduler, "live_gpu_memory", lambda: {"gpus": [{"memory_used_mb": 3_000, "memory_free_mb": 7_000, "memory_total_mb": 10_000}]})
    monkeypatch.setattr(gpu_scheduler.time, "sleep", lambda _seconds: None)
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", model_runner_base_url="http://model-runner"))

    result = gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert result["status"] == "retrying"
    assert db.retried[0]["metadata"]["terminal_reason"] == "verification_deferred"


def test_ollama_eviction_is_skipped_without_a_fenced_runtime_unload_acknowledgement(monkeypatch):
    request = _brokered_eviction_request()
    request.update({"task_type": "ollama_vision", "model_id": "qwen3-vl:8b", "component": "ollama", "estimated_freed_vram_mb": 1_000, "claim_token": "claim-1", "row_version": 1})
    request["metadata"]["candidate"].update({"task_type": "ollama_vision", "model_id": "qwen3-vl:8b", "component": "ollama", "estimated_vram_mb": 1_000})
    db = FakeGpuEvictionDb(request)
    phase = {"after": False}
    posted: list[tuple[str, str]] = []

    monkeypatch.setattr(
        gpu_scheduler,
        "_get_json",
        lambda _base, path, **_kwargs: {"models": []} if phase["after"] else {"models": [{"name": "qwen3-vl:8b", "size_vram": 2 * 1024 * 1024}]},
    )
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda base, path, *_args, **_kwargs: posted.append((base, path)) or phase.update(after=True) or {"done": True})
    driver_used = iter([3_000, 2_000, 2_000])
    monkeypatch.setattr(gpu_scheduler, "live_gpu_memory", lambda: {"gpus": [{"memory_used_mb": next(driver_used), "memory_free_mb": 7_000, "memory_total_mb": 10_000}]})
    monkeypatch.setattr(gpu_scheduler.time, "sleep", lambda _seconds: None)
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", ollama_base_url="http://ollama"))

    result = gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert posted == []
    assert result["status"] == "skipped"
    assert db.completed[0]["metadata"]["terminal_reason"] == "unload_capability_unavailable"


def test_generation_mismatch_persists_fresh_runtime_fence_not_queued_fence(monkeypatch):
    request = _brokered_eviction_request()
    request.update({"claim_token": "claim-1", "row_version": 1, "runtime_generation": "generation-1", "runtime_activity_sequence": 4})
    db = FakeGpuEvictionDb(request)
    monkeypatch.setattr(gpu_scheduler, "_get_json", lambda *_args, **_kwargs: {"owner_component": "model-runner", "process_generation": "generation-2", "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "activity_sequence": 6, "in_flight": 0}], "allocator": [{"capability": "measured", "reserved_mb": 3_000}]})
    monkeypatch.setattr(gpu_scheduler, "live_gpu_memory", lambda: {"gpus": [{"memory_used_mb": 3_000, "memory_free_mb": 7_000, "memory_total_mb": 10_000}]})
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", model_runner_base_url="http://model-runner"))

    gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert db.residency_updates[0]["runtime_generation"] == "generation-2"
    assert db.residency_updates[0]["runtime_activity_sequence"] == 6


def test_activity_mismatch_persists_fresh_runtime_activity_not_queued_activity(monkeypatch):
    request = _brokered_eviction_request()
    request.update({"claim_token": "claim-1", "row_version": 1, "runtime_generation": "generation-1", "runtime_activity_sequence": 4})
    db = FakeGpuEvictionDb(request)
    monkeypatch.setattr(gpu_scheduler, "_get_json", lambda *_args, **_kwargs: {"owner_component": "model-runner", "process_generation": "generation-1", "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "activity_sequence": 5, "in_flight": 0}], "allocator": [{"capability": "measured", "reserved_mb": 3_000}]})
    monkeypatch.setattr(gpu_scheduler, "live_gpu_memory", lambda: {"gpus": [{"memory_used_mb": 3_000, "memory_free_mb": 7_000, "memory_total_mb": 10_000}]})
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", model_runner_base_url="http://model-runner"))

    gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert db.residency_updates[0]["runtime_activity_sequence"] == 5


@pytest.mark.parametrize("in_flight", [None, "", "not-a-number"])
def test_missing_or_malformed_in_flight_defers_without_unload(monkeypatch, in_flight):
    request = _brokered_eviction_request()
    request.update({"claim_token": "claim-1", "row_version": 1, "runtime_generation": "generation-1", "runtime_activity_sequence": 4})
    db = FakeGpuEvictionDb(request)
    posted: list[object] = []
    monkeypatch.setattr(gpu_scheduler, "_get_json", lambda *_args, **_kwargs: {"owner_component": "model-runner", "process_generation": "generation-1", "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "activity_sequence": 4, "in_flight": in_flight}], "allocator": [{"capability": "measured", "reserved_mb": 3_000}]})
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: posted.append(True))
    monkeypatch.setattr(gpu_scheduler, "live_gpu_memory", lambda: {"gpus": [{"memory_used_mb": 3_000, "memory_free_mb": 7_000, "memory_total_mb": 10_000}]})
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", model_runner_base_url="http://model-runner"))

    result = gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert posted == []
    assert result["status"] == "retrying"
    assert db.retried[0]["metadata"]["terminal_reason"] == "inventory_incomplete"


def test_quiet_window_rechecks_leases_started_after_unload(monkeypatch):
    request = _brokered_eviction_request()
    request.update({"claim_token": "claim-1", "row_version": 1, "runtime_generation": "generation-1", "runtime_activity_sequence": 4})
    db = FakeGpuEvictionDb(request)
    phase = {"after": False, "leases": 0}
    db.list_active_gpu_leases = lambda **_kwargs: [] if (phase.update(leases=phase["leases"] + 1) or phase["leases"] == 1) else [{"id": "new", "task_type": "asr", "model_id": "whisper"}]  # type: ignore[method-assign]
    monkeypatch.setattr(gpu_scheduler, "_get_json", lambda *_args, **_kwargs: ({"owner_component": "model-runner", "process_generation": "generation-1", "models": [], "allocator": [{"capability": "known_unmeasured"}]} if phase["after"] else {"owner_component": "model-runner", "process_generation": "generation-1", "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "activity_sequence": 4, "in_flight": 0}], "allocator": [{"capability": "known_unmeasured"}]}) )
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: phase.update(after=True) or {"unload_confirmed": True})
    driver_used = iter([3_000, 2_000, 2_000])
    monkeypatch.setattr(gpu_scheduler, "live_gpu_memory", lambda: {"gpus": [{"memory_used_mb": next(driver_used), "memory_free_mb": 7_000, "memory_total_mb": 10_000}]})
    monkeypatch.setattr(gpu_scheduler.time, "sleep", lambda _seconds: None)
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", model_runner_base_url="http://model-runner"))

    result = gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert result["status"] == "retrying"
    assert db.retried[0]["metadata"]["terminal_reason"] == "verification_deferred"


def test_terminal_cas_rejection_does_not_mutate_residency(monkeypatch):
    db = FakeGpuEvictionDb(_brokered_eviction_request(), complete_result={"eviction_id": "eviction-1", "cas_rejected": True})
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", eviction_enabled=True))
    monkeypatch.setattr(gpu_scheduler, "_runtime_evict_result", lambda **kwargs: (GpuEvictionVerificationResult(False, error="runtime generation changed", metadata={"terminal_reason": "generation_changed", "owner_component": "model-runner", "runtime_generation": "generation-2", "runtime_activity_sequence": 5}), kwargs["claim_token"], kwargs["row_version"]))

    result = gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert result["status"] == "cas_rejected"
    assert db.residency_updates == []


def test_absent_post_target_with_changed_generation_persists_post_owner_evidence(monkeypatch):
    request = _brokered_eviction_request()
    request.update({"claim_token": "claim-1", "row_version": 1, "runtime_generation": "generation-1", "runtime_activity_sequence": 4})
    db = FakeGpuEvictionDb(request)
    phase = {"after": False}
    monkeypatch.setattr(
        gpu_scheduler,
        "_get_json",
        lambda *_args, **_kwargs: (
            {"owner_component": "model-runner", "process_generation": "generation-2", "runtime_fingerprint": "post-fingerprint", "models": [], "allocator": [{"capability": "measured", "reserved_mb": 1_000}]}
            if phase["after"]
            else {"owner_component": "model-runner", "process_generation": "generation-1", "runtime_fingerprint": "pre-fingerprint", "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "activity_sequence": 4, "in_flight": 0}], "allocator": [{"capability": "measured", "reserved_mb": 3_000}]}
        ),
    )
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: phase.update(after=True) or {"unload_confirmed": True})
    monkeypatch.setattr(gpu_scheduler, "live_gpu_memory", lambda: {"gpus": [{"memory_used_mb": 3_000, "memory_free_mb": 7_000, "memory_total_mb": 10_000}]})
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", model_runner_base_url="http://model-runner"))

    result = gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert result["status"] == "failed"
    verification = db.residency_updates[0]
    assert verification["owner_component"] == "model-runner"
    assert verification["runtime_generation"] == "generation-2"
    assert verification["runtime_activity_sequence"] is None
    assert verification["runtime_fingerprint"] == "post-fingerprint"


def test_still_present_post_target_persists_post_activity_evidence(monkeypatch):
    request = _brokered_eviction_request()
    request.update({"claim_token": "claim-1", "row_version": 1, "runtime_generation": "generation-1", "runtime_activity_sequence": 4})
    db = FakeGpuEvictionDb(request)
    phase = {"after": False}
    monkeypatch.setattr(
        gpu_scheduler,
        "_get_json",
        lambda *_args, **_kwargs: (
            {"owner_component": "model-runner", "process_generation": "generation-1", "runtime_fingerprint": "post-fingerprint", "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "activity_sequence": 5, "in_flight": 0}], "allocator": [{"capability": "measured", "reserved_mb": 3_000}]}
            if phase["after"]
            else {"owner_component": "model-runner", "process_generation": "generation-1", "runtime_fingerprint": "pre-fingerprint", "models": [{"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner", "activity_sequence": 4, "in_flight": 0}], "allocator": [{"capability": "measured", "reserved_mb": 3_000}]}
        ),
    )
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: phase.update(after=True) or {"unload_confirmed": True})
    monkeypatch.setattr(gpu_scheduler, "live_gpu_memory", lambda: {"gpus": [{"memory_used_mb": 3_000, "memory_free_mb": 7_000, "memory_total_mb": 10_000}]})
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", model_runner_base_url="http://model-runner"))

    result = gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert result["status"] == "failed"
    verification = db.residency_updates[0]
    assert verification["runtime_generation"] == "generation-1"
    assert verification["runtime_activity_sequence"] == 5
    assert verification["runtime_fingerprint"] == "post-fingerprint"


def test_ollama_unfenced_request_is_skipped_without_replacing_runtime_identity(monkeypatch):
    request = _brokered_eviction_request()
    request.update({"task_type": "ollama_vision", "model_id": "qwen3-vl:8b", "component": "ollama", "estimated_freed_vram_mb": 1_000, "claim_token": "claim-1", "row_version": 1})
    request["metadata"]["candidate"].update({"task_type": "ollama_vision", "model_id": "qwen3-vl:8b", "component": "ollama", "estimated_vram_mb": 1_000})
    db = FakeGpuEvictionDb(request)
    phase = {"after": False}
    monkeypatch.setattr(gpu_scheduler, "_get_json", lambda *_args, **_kwargs: {"models": []} if phase["after"] else {"models": [{"name": "qwen3-vl:8b", "size_vram": 2 * 1024 * 1024}]})
    monkeypatch.setattr(gpu_scheduler, "_post_json", lambda *_args, **_kwargs: phase.update(after=True) or {"done": False})
    driver_used = iter([3_000, 2_000])
    monkeypatch.setattr(gpu_scheduler, "live_gpu_memory", lambda: {"gpus": [{"memory_used_mb": next(driver_used), "memory_free_mb": 7_000, "memory_total_mb": 10_000}]})
    monkeypatch.setattr(gpu_scheduler, "scheduler_config_from_settings", lambda: _config(mode="postgres", ollama_base_url="http://ollama"))

    result = gpu_scheduler.process_gpu_eviction_request(eviction_id="eviction-1", worker_id="worker-1", database_module=db)

    assert result["status"] == "skipped"
    assert db.completed[0]["metadata"]["terminal_reason"] == "unload_capability_unavailable"
    assert db.residency_updates == []


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


def test_admission_recovers_running_lease_past_metadata_max_active_seconds():
    profile = GpuTaskProfile(task_type="embedding", model_id="snowflake", estimated_vram_mb=2_000)
    overlong = _lease(
        "overlong-lease",
        expires_at=1000.0,
        heartbeat_at=9.0,
        metadata={"max_active_seconds": 5},
    )

    decision = plan_gpu_admission(
        profile,
        active_leases=[overlong],
        config=_config(),
        now=10.0,
    )

    assert decision.granted is True
    assert decision.recovered_lease_ids == ["overlong-lease"]


def test_postgres_scheduler_recovers_running_leases_past_metadata_max_active_seconds():
    calls: list[tuple[str, tuple[object, ...]]] = []

    class RecordingScheduler(PostgresGpuScheduler):
        def _execute(self, statement: str, params: tuple[object, ...]) -> None:
            calls.append((statement, params))

    scheduler = RecordingScheduler(_config(mode="postgres"), database_url="postgresql://example")

    scheduler._recover_stale()

    assert len(calls) == 1
    statement, params = calls[0]
    assert "expires_at < now()" in statement
    assert "metadata->>'max_active_seconds'" in statement
    assert "granted_at < now() -" in statement
    assert params == (60.0,)


def test_postgres_scheduler_times_out_stale_waiting_leases():
    calls: list[tuple[str, tuple[object, ...]]] = []

    class RecordingScheduler(PostgresGpuScheduler):
        def _execute(self, statement: str, params: tuple[object, ...]) -> None:
            calls.append((statement, params))

    scheduler = RecordingScheduler(_config(mode="postgres", stale_after_seconds=45.0), database_url="postgresql://example")

    scheduler._recover_stale()

    assert len(calls) == 1
    statement, params = calls[0]
    assert "status = 'waiting'" in statement
    assert "SET status = 'timed_out'" in statement
    assert "expires_at < now()" in statement
    assert "created_at < now() - (%s * interval '1 second')" in statement
    assert params == (45.0,)


def test_postgres_scheduler_inserts_waiting_lease_with_deadline(monkeypatch):
    calls: list[tuple[str, tuple[object, ...]]] = []

    class RecordingScheduler(PostgresGpuScheduler):
        def _execute(self, statement: str, params: tuple[object, ...]) -> None:
            calls.append((statement, params))

    monkeypatch.setattr(gpu_scheduler, "_jsonb_adapter", lambda: dict)
    scheduler = RecordingScheduler(_config(mode="postgres"), database_url="postgresql://example")

    scheduler._insert_waiting(
        GpuTaskProfile(
            task_type="ocr_image",
            model_id="PP-OCRv5",
            estimated_vram_mb=2_000,
            timeout_seconds=7.5,
        ),
        "lease-waiting",
    )

    statement, params = calls[0]
    assert "expires_at" in statement
    assert "now() + (%s * interval '1 second')" in statement
    assert params[9] == 7.5


def test_in_process_scheduler_times_out_stale_waiting_lease_head():
    scheduler = InProcessGpuScheduler(_config(default_timeout_seconds=1.0))
    stale = GpuLeaseRecord(
        id="stale-waiting",
        task_type="ocr_image",
        model_id="PP-OCRv5",
        status="waiting",
        estimated_vram_mb=2_000,
        exclusive=True,
        share_group="",
        priority=0,
        component="paddle-runner",
        request_id="",
        created_at=1.0,
        granted_at=None,
        heartbeat_at=None,
        expires_at=2.0,
        released_at=None,
        metadata={},
    )
    scheduler._leases[stale.id] = stale

    with scheduler.acquire(GpuTaskProfile(task_type="embedding", model_id="snowflake", estimated_vram_mb=2_000)):
        status = scheduler.status()

    assert status["counts"]["timed_out"] == 1
    assert status["counts"]["running"] == 1
    assert status["waiting"] == []


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
