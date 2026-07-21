from __future__ import annotations

import builtins
from types import SimpleNamespace
import sys
import threading

import pytest

from flux_llm_kb.gpu_runtime import (
    AllocatorSnapshot,
    RuntimeModelKey,
    RuntimeResidencyTracker,
    normalise_priority_class,
    paddle_allocator_snapshot,
    runtime_request_priority,
    torch_allocator_snapshot,
)


def test_priority_classes_normalise_and_rank_interactive_before_background() -> None:
    assert normalise_priority_class(" Interactive ") == "interactive"
    assert normalise_priority_class("BACKGROUND") == "background"
    assert runtime_request_priority("interactive") == 100
    assert runtime_request_priority("background") == 0

    with pytest.raises(ValueError, match="priority"):
        normalise_priority_class("batch")


def test_interactive_ticket_moves_ahead_of_waiting_background_at_boundary() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    active = tracker.enqueue(key, priority_class="background")
    with tracker.operation(active):
        background = tracker.enqueue(key, priority_class="background")
        interactive = tracker.enqueue(key, priority_class="interactive")
        assert active.is_active
        assert tracker.next_waiting_ticket_id(key) == interactive.id

    with tracker.operation(interactive):
        assert interactive.is_active
    assert background.is_head


def test_explicit_lane_priority_orders_internal_work_at_the_next_safe_boundary() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    active = tracker.enqueue(key, priority_class="background", priority=400)
    with tracker.operation(active):
        ocr = tracker.enqueue(key, priority_class="background", priority=300)
        vision = tracker.enqueue(key, priority_class="background", priority=200)
        video = tracker.enqueue(key, priority_class="background", priority=100)
        document = tracker.enqueue(key, priority_class="background", priority=400)
        assert tracker.next_waiting_ticket_id(key) == document.id

    with tracker.operation(document):
        assert document.is_active
    with tracker.operation(ocr):
        assert ocr.is_active
    with tracker.operation(vision):
        assert vision.is_active
    with tracker.operation(video):
        assert video.is_active


def test_runtime_operation_uses_a_specific_exception_for_a_safe_boundary_queue_race() -> None:
    from flux_llm_kb.gpu_runtime import RuntimeOperationNotReady

    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    active = tracker.enqueue(key, priority_class="background")
    waiting = tracker.enqueue(key, priority_class="background")

    with tracker.operation(active):
        with pytest.raises(RuntimeOperationNotReady, match="already active"):
            with tracker.operation(waiting):
                pass


def test_discard_waiting_ticket_removes_only_an_inactive_ticket() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    active = tracker.enqueue(key, priority_class="background")
    waiting = tracker.enqueue(key, priority_class="background")

    with tracker.operation(active):
        assert tracker.discard_waiting(waiting) is True
        assert tracker.next_waiting_ticket_id(key) is None
        assert tracker.discard_waiting(active) is False

    replacement = tracker.enqueue(key, priority_class="background")
    assert tracker.ready_to_start(replacement) is True


def test_completed_operations_increment_model_activity_sequence() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    ticket = tracker.enqueue(key, priority_class="background")

    with tracker.operation(ticket) as measurement:
        assert measurement.activity_sequence == 0
        assert measurement.in_flight == 1

    model = tracker.inventory([key])["models"][0]
    assert model["activity_sequence"] == 1
    assert model["in_flight"] == 0
    assert model["last_activity_at"] is not None


def test_operation_measurement_reports_process_local_overlap_across_model_keys() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    first = tracker.enqueue(RuntimeModelKey("embedding", "snowflake-a"), priority_class="background")
    second = tracker.enqueue(RuntimeModelKey("embedding", "snowflake-b"), priority_class="background")

    with tracker.operation(first) as first_measurement:
        with tracker.operation(second) as second_measurement:
            assert first_measurement.process_in_flight == 1
            assert second_measurement.process_in_flight == 2
            assert tracker.process_in_flight() == 2
        assert tracker.process_in_flight() == 1
        assert tracker.process_activity_epoch() > first_measurement.process_activity_epoch


def test_active_operation_is_not_preempted_by_interactive_ticket() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    background = tracker.enqueue(key, priority_class="background")

    with tracker.operation(background):
        interactive = tracker.enqueue(key, priority_class="interactive")
        assert background.is_active
        assert not interactive.is_active
        assert tracker.next_waiting_ticket_id(key) == interactive.id


def test_unload_refuses_generation_mismatch_without_calling_remove() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    called = False

    def remove() -> bool:
        nonlocal called
        called = True
        return True

    result = tracker.unload(
        key,
        expected_generation="another-process",
        expected_activity_sequence=0,
        remove=remove,
    )

    assert result == {"unloaded": False, "reason": "generation_mismatch"}
    assert not called


def test_unload_refuses_activity_mismatch_without_calling_remove() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    ticket = tracker.enqueue(key, priority_class="background")
    with tracker.operation(ticket):
        pass
    called = False

    def remove() -> bool:
        nonlocal called
        called = True
        return True

    result = tracker.unload(
        key,
        expected_generation=tracker.process_generation,
        expected_activity_sequence=0,
        remove=remove,
    )

    assert result == {"unloaded": False, "reason": "activity_mismatch"}
    assert not called


def test_unload_refuses_model_with_in_flight_operation() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    ticket = tracker.enqueue(key, priority_class="background")
    called = False

    def remove() -> bool:
        nonlocal called
        called = True
        return True

    with tracker.operation(ticket):
        result = tracker.unload(
            key,
            expected_generation=tracker.process_generation,
            expected_activity_sequence=0,
            remove=remove,
        )

    assert result == {"unloaded": False, "reason": "in_flight"}
    assert not called


def test_unload_refuses_model_with_queued_operation_without_stranding_ticket() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    ticket = tracker.enqueue(key, priority_class="background")
    called = False

    def remove() -> bool:
        nonlocal called
        called = True
        return True

    result = tracker.unload(
        key,
        expected_generation=tracker.process_generation,
        expected_activity_sequence=0,
        remove=remove,
    )

    assert result == {"unloaded": False, "reason": "queued"}
    assert not called
    with tracker.operation(ticket):
        assert ticket.is_active


def test_unload_refuses_target_when_another_model_is_in_flight() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    target = RuntimeModelKey("embedding", "snowflake")
    active = RuntimeModelKey("rerank", "qwen")
    tracker.inventory([target, active])
    ticket = tracker.enqueue(active, priority_class="background")
    remove_called = False

    def remove() -> bool:
        nonlocal remove_called
        remove_called = True
        return True

    with tracker.operation(ticket):
        result = tracker.unload(
            target,
            expected_generation=tracker.process_generation,
            expected_activity_sequence=0,
            remove=remove,
        )

    assert result == {"unloaded": False, "reason": "process_in_flight"}
    assert not remove_called


def test_unload_keeps_the_process_gate_through_allocator_release() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    target = RuntimeModelKey("embedding", "snowflake")
    other = RuntimeModelKey("rerank", "qwen")
    tracker.inventory([target, other])
    other_ticket = tracker.enqueue(other, priority_class="background")
    started = threading.Event()
    entered_operation = threading.Event()

    def release_allocator() -> None:
        def start_other_operation() -> None:
            started.set()
            with tracker.operation(other_ticket):
                entered_operation.set()

        thread = threading.Thread(target=start_other_operation)
        thread.start()
        assert started.wait(1)
        assert not entered_operation.wait(0.05)
        thread.join(timeout=0.01)
        assert thread.is_alive()
        release_allocator.thread = thread

    result = tracker.unload(
        target,
        expected_generation=tracker.process_generation,
        expected_activity_sequence=0,
        remove=lambda: True,
        release_allocator=release_allocator,
    )

    release_allocator.thread.join(timeout=1)
    assert not release_allocator.thread.is_alive()
    assert entered_operation.is_set()
    assert result == {"unloaded": True, "reason": "unloaded"}


def test_allocator_trim_is_fenced_and_never_runs_during_an_active_operation() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    ticket = tracker.enqueue(key, priority_class="background")
    calls = []

    with tracker.operation(ticket):
        blocked = tracker.trim_allocator(
            key,
            expected_generation=tracker.process_generation,
            expected_activity_sequence=0,
            trim=lambda: calls.append("trim"),
        )

    assert blocked == {"trimmed": False, "reason": "in_flight"}
    assert calls == []
    fence = tracker.inventory([key])["models"][0]
    completed = tracker.trim_allocator(
        key,
        expected_generation=fence["process_generation"],
        expected_activity_sequence=fence["activity_sequence"],
        trim=lambda: calls.append("trim"),
    )

    assert completed == {"trimmed": True, "reason": "trimmed"}
    assert calls == ["trim"]


def test_mcp_preemption_policy_requires_runtime_confirmed_cooperative_cancellation() -> None:
    from flux_llm_kb.gpu_runtime import runtime_preemption_policy

    policy = runtime_preemption_policy(
        "model-runner",
        ("embedding", "rerank", "ocr_image", "ocr_document"),
    )

    assert policy["mcp_only"] is True
    assert policy["fallback"] == "priority_at_safe_boundary"
    assert all(item["cancellation"] == "unsupported" for item in policy["tasks"])
    assert all(item["cooperative_confirmation"] is False for item in policy["tasks"])


def test_unload_after_reload_refuses_stale_activity_fence() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    first_operation = tracker.enqueue(key, priority_class="background")
    with tracker.operation(first_operation):
        pass
    first_fence = tracker.inventory([key])["models"][0]

    assert tracker.unload(
        key,
        expected_generation=first_fence["process_generation"],
        expected_activity_sequence=first_fence["activity_sequence"],
        remove=lambda: True,
    ) == {"unloaded": True, "reason": "unloaded"}

    reloaded = tracker.inventory([key])["models"][0]
    assert reloaded["activity_sequence"] > first_fence["activity_sequence"]
    remove_called = False

    def stale_remove() -> bool:
        nonlocal remove_called
        remove_called = True
        return True

    assert tracker.unload(
        key,
        expected_generation=first_fence["process_generation"],
        expected_activity_sequence=first_fence["activity_sequence"],
        remove=stale_remove,
    ) == {"unloaded": False, "reason": "activity_mismatch"}
    assert not remove_called


def test_repeated_unload_after_real_operation_confirms_known_absence() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    ticket = tracker.enqueue(key, priority_class="background")
    with tracker.operation(ticket):
        pass
    fence = tracker.inventory([key])["models"][0]

    assert tracker.unload(
        key,
        expected_generation=fence["process_generation"],
        expected_activity_sequence=fence["activity_sequence"],
        remove=lambda: True,
    ) == {"unloaded": True, "reason": "unloaded"}
    remove_called = False

    def remove_absent_model() -> bool:
        nonlocal remove_called
        remove_called = True
        return False

    assert tracker.unload(
        key,
        expected_generation=fence["process_generation"],
        expected_activity_sequence=fence["activity_sequence"],
        remove=remove_absent_model,
    ) == {"unloaded": False, "reason": "absent"}
    assert not remove_called


def test_allocator_probe_failures_are_known_but_unmeasured() -> None:
    def unavailable_probe() -> AllocatorSnapshot:
        raise ImportError("CUDA library unavailable")

    tracker = RuntimeResidencyTracker(
        owner_component="model-runner",
        allocator_probes=[unavailable_probe],
    )

    allocator = tracker.inventory([])["allocator"]
    assert allocator == [
        AllocatorSnapshot(
            framework="unavailable_probe",
            device="",
            capability="known_unmeasured",
            allocated_mb=None,
            reserved_mb=None,
            peak_reserved_mb=None,
            reason="CUDA library unavailable",
        )
    ]


def test_successful_allocator_probe_keeps_process_global_values() -> None:
    snapshot = AllocatorSnapshot(
        framework="torch",
        device="cuda:0",
        capability="measured",
        allocated_mb=1024,
        reserved_mb=2048,
        peak_reserved_mb=3072,
    )
    tracker = RuntimeResidencyTracker(
        owner_component="model-runner",
        allocator_probes=[lambda: snapshot],
    )
    keys = [RuntimeModelKey("embedding", "snowflake"), RuntimeModelKey("rerank", "bge")]

    assert tracker.inventory(keys)["allocator"] == [snapshot]


def test_allocator_capability_probe_errors_become_known_unmeasured(monkeypatch) -> None:
    def unavailable() -> bool:
        raise RuntimeError("driver probe failed")

    monkeypatch.setitem(sys.modules, "torch", SimpleNamespace(cuda=SimpleNamespace(is_available=unavailable)))
    monkeypatch.setitem(
        sys.modules,
        "paddle",
        SimpleNamespace(is_compiled_with_cuda=unavailable),
    )

    assert torch_allocator_snapshot().capability == "known_unmeasured"
    assert paddle_allocator_snapshot().capability == "known_unmeasured"


def test_allocator_probe_oserror_becomes_known_unmeasured() -> None:
    def unavailable_probe() -> AllocatorSnapshot:
        raise OSError("CUDA DLL unavailable")

    tracker = RuntimeResidencyTracker(
        owner_component="model-runner",
        allocator_probes=[unavailable_probe],
    )

    assert tracker.inventory([])["allocator"][0].capability == "known_unmeasured"


def test_allocator_framework_import_oserror_becomes_known_unmeasured(monkeypatch) -> None:
    original_import = builtins.__import__

    def unavailable_import(name, *args, **kwargs):
        if name in {"torch", "paddle"}:
            raise OSError("CUDA DLL unavailable")
        return original_import(name, *args, **kwargs)

    monkeypatch.setattr(builtins, "__import__", unavailable_import)

    assert torch_allocator_snapshot().capability == "known_unmeasured"
    assert paddle_allocator_snapshot().capability == "known_unmeasured"


def test_priority_classes_preserve_fifo_order_and_current_head() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    active = tracker.enqueue(key, priority_class="background")
    with tracker.operation(active):
        background_first = tracker.enqueue(key, priority_class="background")
        background_second = tracker.enqueue(key, priority_class="background")
        interactive_first = tracker.enqueue(key, priority_class="interactive")
        interactive_second = tracker.enqueue(key, priority_class="interactive")
        assert interactive_first.is_head
        assert not active.is_head

    activation_order = []
    for ticket in (interactive_first, interactive_second, background_first, background_second):
        assert ticket.is_head
        with tracker.operation(ticket):
            activation_order.append(ticket.id)
        assert not ticket.is_head

    assert activation_order == [
        interactive_first.id,
        interactive_second.id,
        background_first.id,
        background_second.id,
    ]


def test_operation_exception_releases_ticket_and_records_activity() -> None:
    tracker = RuntimeResidencyTracker(owner_component="model-runner", allocator_probes=[])
    key = RuntimeModelKey("embedding", "snowflake")
    failed = tracker.enqueue(key, priority_class="background")
    waiting = tracker.enqueue(key, priority_class="background")

    with pytest.raises(RuntimeError, match="operation failed"):
        with tracker.operation(failed):
            raise RuntimeError("operation failed")

    model = tracker.inventory([key])["models"][0]
    assert model["in_flight"] == 0
    assert model["activity_sequence"] == 1
    assert not failed.is_active
    assert not failed.is_head
    assert waiting.is_head
    with tracker.operation(waiting):
        assert waiting.is_active
