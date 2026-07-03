from __future__ import annotations

import threading
import time

import pytest

from flux_llm_kb.gpu_scheduler import (
    GpuLeaseRecord,
    GpuLeaseTimeout,
    GpuModelResidency,
    GpuSchedulerConfig,
    GpuTaskProfile,
    InProcessGpuScheduler,
    plan_gpu_admission,
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
    estimated_vram_mb: int = 2_000,
    exclusive: bool = True,
    share_group: str = "",
    status: str = "running",
    expires_at: float = 100.0,
) -> GpuLeaseRecord:
    return GpuLeaseRecord(
        id=lease_id,
        task_type=task_type,
        model_id="model-a",
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
