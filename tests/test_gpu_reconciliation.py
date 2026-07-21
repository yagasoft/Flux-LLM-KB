from __future__ import annotations

import threading
import time

from flux_llm_kb.gpu_reconciliation import (
    RuntimeInventorySnapshot,
    calculate_gpu_capacity,
    inventory_from_payload,
    reconcile_runtime_inventory,
)


def inventory(*, owner: str = "model-runner", capability: str = "measured", measured_mb: int = 0) -> RuntimeInventorySnapshot:
    return RuntimeInventorySnapshot(
        component=owner,
        owner_component=owner,
        state="present",
        allocator_capability=capability,
        known_measured_mb=measured_mb,
    )


def test_healthy_capacity_accounts_for_known_measured_memory():
    observation = calculate_gpu_capacity(
        driver_used_mb=4_000,
        driver_free_mb=8_000,
        inventories=[inventory(measured_mb=3_800)],
        context_allowance_mb=256,
        material_threshold_mb=512,
    )

    assert observation.state == "healthy"
    assert observation.raw_residual_mb == 0
    assert observation.unattributed_mb == 0


def test_known_unmeasured_owner_is_not_labelled_unattributed():
    observation = calculate_gpu_capacity(
        driver_used_mb=10_839,
        driver_free_mb=1_159,
        inventories=[inventory(owner="asr", capability="known_unmeasured")],
        context_allowance_mb=256,
        material_threshold_mb=512,
    )

    assert observation.state == "inventory_incomplete"
    assert observation.unresolved_known_owner_mb > 0
    assert observation.unattributed_mb == 0


def test_material_memory_without_a_known_owner_requires_reconciliation():
    observation = calculate_gpu_capacity(
        driver_used_mb=4_000,
        driver_free_mb=8_000,
        inventories=[],
        context_allowance_mb=256,
        material_threshold_mb=512,
    )

    assert observation.state == "reconciliation_required"
    assert observation.unattributed_mb == 4_000


def test_ollama_reported_vram_is_not_double_counted_when_its_allocator_reserves_memory():
    observation = calculate_gpu_capacity(
        driver_used_mb=2_000,
        driver_free_mb=8_000,
        inventories=[
            RuntimeInventorySnapshot(
                component="ollama", owner_component="ollama", state="present", allocator_capability="measured",
                known_measured_mb=2_000, known_reported_mb=2_000,
                allocators=({"capability": "measured", "reserved_mb": 2_000},),
            ),
        ],
        context_allowance_mb=0,
        material_threshold_mb=512,
    )

    assert observation.raw_residual_mb == 0
    assert observation.state == "healthy"


def test_ollama_reported_vram_remains_counted_when_another_process_has_a_reservation():
    observation = calculate_gpu_capacity(
        driver_used_mb=3_000,
        driver_free_mb=8_000,
        inventories=[
            RuntimeInventorySnapshot(component="ollama", state="present", known_reported_mb=2_000),
            RuntimeInventorySnapshot(
                component="asr", state="present", allocator_capability="measured", known_measured_mb=1_000,
                allocators=({"capability": "measured", "reserved_mb": 1_000},),
            ),
        ],
        context_allowance_mb=0,
        material_threshold_mb=512,
    )

    assert observation.raw_residual_mb == 0


def test_allocated_only_allocator_covers_the_same_process_reported_vram():
    observation = calculate_gpu_capacity(
        driver_used_mb=3_000,
        driver_free_mb=8_000,
        inventories=[
            RuntimeInventorySnapshot(
                component="ollama", state="present", known_measured_mb=2_000, known_reported_mb=2_000,
                allocators=({"capability": "measured", "allocated_mb": 2_000, "reserved_mb": 0},),
            ),
        ],
        context_allowance_mb=0,
        material_threshold_mb=512,
    )

    assert observation.raw_residual_mb == 1_000
    assert observation.unattributed_mb == 1_000
    assert observation.state == "reconciliation_required"


def test_reported_vram_remains_counted_without_an_allocator_measurement():
    observation = calculate_gpu_capacity(
        driver_used_mb=2_000,
        driver_free_mb=8_000,
        inventories=[
            RuntimeInventorySnapshot(
                component="ollama", state="present", known_reported_mb=2_000,
                allocators=({"capability": "known_unmeasured", "allocated_mb": 0, "reserved_mb": 0},),
            ),
        ],
        context_allowance_mb=0,
        material_threshold_mb=512,
    )

    assert observation.raw_residual_mb == 0


def test_context_allowance_is_applied_for_each_confirmed_gpu_process():
    observation = calculate_gpu_capacity(
        driver_used_mb=512,
        driver_free_mb=1_000,
        inventories=[inventory(owner="asr"), inventory(owner="model-runner")],
        context_allowance_mb=256,
        material_threshold_mb=512,
    )

    assert observation.raw_residual_mb == 0
    assert observation.context_allowance_mb == 512


def test_multi_worker_payload_preserves_process_cardinality_and_requires_aggregate_inventory():
    snapshot = inventory_from_payload(
        "model-runner",
        {
            "owner_component": "model-runner",
            "worker_count": 2,
            "process": {"id": "runner-pid-42"},
            "models": [],
        },
    )

    observation = calculate_gpu_capacity(
        driver_used_mb=512,
        driver_free_mb=1_000,
        inventories=[snapshot],
        context_allowance_mb=256,
        material_threshold_mb=512,
    )

    assert snapshot.process_identity == "runner-pid-42"
    assert snapshot.process_count == 2
    assert snapshot.process_inventory_aggregated is False
    assert observation.context_allowance_mb == 512
    assert observation.state == "inventory_incomplete"


def test_aggregated_multi_worker_inventory_is_accounted_per_process_without_incomplete_state():
    snapshot = inventory_from_payload(
        "model-runner",
        {"owner_component": "model-runner", "worker_count": 2, "inventory_aggregated": True, "models": []},
    )

    observation = calculate_gpu_capacity(
        driver_used_mb=512,
        driver_free_mb=1_000,
        inventories=[snapshot],
        context_allowance_mb=256,
        material_threshold_mb=512,
    )

    assert observation.context_allowance_mb == 512
    assert observation.state == "healthy"


def test_percentage_material_threshold_uses_the_configured_total_memory_basis():
    observation = calculate_gpu_capacity(
        driver_used_mb=900,
        driver_free_mb=19_100,
        inventories=[],
        context_allowance_mb=0,
        material_threshold_mb=512,
        material_threshold_percent=5,
        total_memory_mb=20_000,
    )

    assert observation.state == "healthy"


def test_unschedulable_when_driver_reports_no_usable_capacity():
    observation = calculate_gpu_capacity(
        driver_used_mb=12_000,
        driver_free_mb=0,
        inventories=[],
        context_allowance_mb=256,
        material_threshold_mb=512,
    )

    assert observation.state == "unschedulable"


def test_duplicate_model_owners_leave_inventory_incomplete():
    snapshot = inventory_from_payload(
        "model-runner",
        {
            "owner_component": "model-runner",
            "models": [
                {"task_type": "embedding", "model_id": "snowflake", "owner_component": "model-runner"},
                {"task_type": "embedding", "model_id": "snowflake", "owner_component": "another-owner"},
            ],
        },
    )

    observation = calculate_gpu_capacity(
        driver_used_mb=256, driver_free_mb=1_000, inventories=[snapshot], context_allowance_mb=256, material_threshold_mb=512
    )

    assert snapshot.state == "conflicted"
    assert observation.state == "inventory_incomplete"


def test_reconciliation_deadline_does_not_wait_for_slow_endpoint():
    release = threading.Event()

    class SlowAdapter:
        component = "asr"

        def read_inventory(self, timeout_seconds):
            release.wait(1)
            return inventory(owner="asr")

    started = time.monotonic()
    observation = reconcile_runtime_inventory(
        [SlowAdapter()],
        live_gpu_memory=lambda: {"gpus": [{"memory_used_mb": 0, "memory_free_mb": 1_000}]},
        timeout_seconds=0.02,
    )
    elapsed = time.monotonic() - started
    release.set()

    assert elapsed < 0.2
    assert observation.inventories[0].state == "unknown"
    assert observation.inventories[0].error_code == "deadline_exceeded"


def test_reconciliation_deadline_includes_the_driver_read():
    started = time.monotonic()
    observation = reconcile_runtime_inventory(
        [],
        live_gpu_memory=lambda: (time.sleep(0.2) or {"gpus": [{"memory_used_mb": 0, "memory_free_mb": 1_000}]}),
        timeout_seconds=0.02,
    )

    assert time.monotonic() - started < 0.1
    assert observation.state == "inventory_incomplete"
    assert observation.driver_observation_state == "timed_out"


def test_failed_driver_observation_is_incomplete_not_unschedulable():
    observation = reconcile_runtime_inventory(
        [],
        live_gpu_memory=lambda: (_ for _ in ()).throw(RuntimeError("driver unavailable")),
        timeout_seconds=0.1,
    )

    assert observation.state == "inventory_incomplete"
    assert observation.driver_observation_state == "failed"
    assert observation.driver_error == "driver unavailable"


def test_cross_component_duplicate_owners_are_marked_conflicted():
    class Adapter:
        def __init__(self, component, owner):
            self.component = component
            self.owner = owner

        def read_inventory(self, timeout_seconds):
            return RuntimeInventorySnapshot(
                component=self.component, owner_component=self.owner, state="present",
                models=({"task_type": "embedding", "model_id": "snowflake", "owner_component": self.owner},),
            )

    observation = reconcile_runtime_inventory(
        [Adapter("model-runner", "model-runner"), Adapter("asr", "asr")],
        live_gpu_memory=lambda: {"gpus": [{"memory_used_mb": 0, "memory_free_mb": 1_000}]},
    )

    assert observation.state == "inventory_incomplete"
    assert {snapshot.state for snapshot in observation.inventories} == {"conflicted"}


def test_ollama_inventory_fingerprint_is_reread_from_each_runtime_snapshot(monkeypatch):
    payloads = iter(
        [
            {"models": [{"name": "qwen3-vl:8b", "size_vram": 2 * 1024 * 1024}]},
            {"models": []},
        ]
    )
    monkeypatch.setattr("flux_llm_kb.gpu_reconciliation._get_json", lambda *_args, **_kwargs: next(payloads))
    adapter = __import__("flux_llm_kb.gpu_reconciliation", fromlist=["OllamaInventoryAdapter"]).OllamaInventoryAdapter("http://ollama")

    before = adapter.read_inventory(timeout_seconds=1)
    after = adapter.read_inventory(timeout_seconds=1)

    assert before.runtime_fingerprint
    assert after.runtime_fingerprint
    assert before.runtime_fingerprint != after.runtime_fingerprint
