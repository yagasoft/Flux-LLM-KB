"""Service-local model runtime residency and operation tracking."""

from __future__ import annotations

from collections.abc import Callable, Iterable, Iterator
from contextlib import contextmanager
from dataclasses import dataclass
from threading import RLock
from time import time
from typing import Any
from uuid import uuid4


_PRIORITIES = {"background": 0, "interactive": 100}
_MEBIBYTE = 1024 * 1024
_PREEMPTION_REASONS = {
    "embedding": "synchronous embedding inference has no cooperative cancellation acknowledgement",
    "rerank": "synchronous reranking inference has no cooperative cancellation acknowledgement",
    "ocr_image": "Paddle OCR inference has no cooperative cancellation acknowledgement",
    "ocr_document": "Paddle document OCR inference has no cooperative cancellation acknowledgement",
    "asr": "ASR transcription has no cooperative cancellation acknowledgement",
    "ollama_vision": "the Ollama request path has no cooperative cancellation acknowledgement",
    "video_extraction": "video extraction has no checkpointed cancellation acknowledgement",
}


class RuntimeOperationNotReady(RuntimeError):
    """A local operation lost the safe-boundary race before CUDA work started."""


@dataclass(frozen=True)
class RuntimeModelKey:
    task_type: str
    model_id: str


@dataclass(frozen=True)
class AllocatorSnapshot:
    framework: str
    device: str
    capability: str
    allocated_mb: int | None
    reserved_mb: int | None
    peak_reserved_mb: int | None
    reason: str = ""


@dataclass
class RuntimeOperationTicket:
    id: str
    key: RuntimeModelKey
    priority_class: str
    priority: int = 0
    request_id: str = ""
    is_active: bool = False
    is_head: bool = False
    _order: int = 0


@dataclass(frozen=True)
class RuntimeOperationMeasurement:
    started_at: float
    activity_sequence: int
    in_flight: int
    process_in_flight: int
    process_activity_epoch: int


@dataclass
class _RuntimeModelState:
    activity_sequence: int = 0
    in_flight: int = 0
    active_ticket_id: str | None = None
    last_activity_at: float | None = None
    last_started_at: float | None = None
    is_loaded: bool = False


def normalise_priority_class(priority_class: str) -> str:
    """Return the canonical priority class accepted by the local queue."""
    normalised = str(priority_class).strip().lower()
    if normalised not in _PRIORITIES:
        raise ValueError(f"unsupported priority class: {priority_class!r}")
    return normalised


def runtime_request_priority(priority_class: str) -> int:
    """Return the local ordering priority for a runtime request."""
    return _PRIORITIES[normalise_priority_class(priority_class)]


def runtime_preemption_policy(owner_component: str, task_types: Iterable[str]) -> dict[str, Any]:
    """Expose the verified cancellation contract for a runtime service.

    MCP is the only caller class that may ever ask to cancel work.  Current
    runtimes do not expose a cooperative cancellation acknowledgement, so this
    policy deliberately reports every listed task as non-preemptive.  Callers
    must then use the normal priority queue at a safe operation boundary.
    """
    owner = str(owner_component or "unknown").strip() or "unknown"
    tasks = []
    for task_type in sorted({str(item or "unknown").strip() or "unknown" for item in task_types}):
        tasks.append(
            {
                "task_type": task_type,
                "owner_component": owner,
                "cancellation": "unsupported",
                "cooperative_confirmation": False,
                "reason": _PREEMPTION_REASONS.get(
                    task_type,
                    "runtime does not provide a cooperative cancellation acknowledgement",
                ),
            }
        )
    return {
        "mcp_only": True,
        "cancellation_request": "unavailable",
        "fallback": "priority_at_safe_boundary",
        "tasks": tasks,
    }


def _known_unmeasured(framework: str, reason: str, *, device: str = "") -> AllocatorSnapshot:
    return AllocatorSnapshot(
        framework=framework,
        device=device,
        capability="known_unmeasured",
        allocated_mb=None,
        reserved_mb=None,
        peak_reserved_mb=None,
        reason=reason,
    )


def torch_allocator_snapshot() -> AllocatorSnapshot:
    """Collect a process-global PyTorch allocator snapshot when supported."""
    try:
        import torch
    except (ImportError, OSError) as exc:
        return _known_unmeasured("torch", str(exc))

    try:
        cuda = getattr(torch, "cuda", None)
        if cuda is None or not callable(getattr(cuda, "is_available", None)) or not cuda.is_available():
            return _known_unmeasured("torch", "CUDA unavailable")
    except (RuntimeError, TypeError, ValueError, AttributeError, OSError) as exc:
        return _known_unmeasured("torch", str(exc))
    required = ("memory_allocated", "memory_reserved", "max_memory_reserved")
    if not all(callable(getattr(cuda, name, None)) for name in required):
        return _known_unmeasured("torch", "allocator metrics unavailable", device="cuda:0")
    try:
        return AllocatorSnapshot(
            framework="torch",
            device="cuda:0",
            capability="measured",
            allocated_mb=int(cuda.memory_allocated() // _MEBIBYTE),
            reserved_mb=int(cuda.memory_reserved() // _MEBIBYTE),
            peak_reserved_mb=int(cuda.max_memory_reserved() // _MEBIBYTE),
        )
    except (RuntimeError, TypeError, ValueError, AttributeError, OSError) as exc:
        return _known_unmeasured("torch", str(exc), device="cuda:0")


def paddle_allocator_snapshot() -> AllocatorSnapshot:
    """Collect a process-global Paddle allocator snapshot when supported."""
    try:
        import paddle
    except (ImportError, OSError) as exc:
        return _known_unmeasured("paddle", str(exc))

    try:
        if not callable(getattr(paddle, "is_compiled_with_cuda", None)) or not paddle.is_compiled_with_cuda():
            return _known_unmeasured("paddle", "CUDA unavailable")
    except (RuntimeError, TypeError, ValueError, AttributeError, OSError) as exc:
        return _known_unmeasured("paddle", str(exc))
    cuda = getattr(getattr(paddle, "device", None), "cuda", None)
    allocated = getattr(cuda, "memory_allocated", None)
    reserved = getattr(cuda, "memory_reserved", None)
    peak_reserved = getattr(cuda, "max_memory_reserved", None)
    if not all(callable(metric) for metric in (allocated, reserved, peak_reserved)):
        return _known_unmeasured("paddle", "allocator metrics unavailable", device="gpu:0")
    try:
        return AllocatorSnapshot(
            framework="paddle",
            device="gpu:0",
            capability="measured",
            allocated_mb=int(allocated() // _MEBIBYTE),
            reserved_mb=int(reserved() // _MEBIBYTE),
            peak_reserved_mb=int(peak_reserved() // _MEBIBYTE),
        )
    except (RuntimeError, TypeError, ValueError, AttributeError, OSError) as exc:
        return _known_unmeasured("paddle", str(exc), device="gpu:0")


class RuntimeResidencyTracker:
    """Track model activity and prioritised local operations for one process."""

    def __init__(
        self,
        *,
        owner_component: str,
        allocator_probes: Iterable[Callable[[], AllocatorSnapshot]] | None = None,
    ) -> None:
        self.owner_component = owner_component
        self.process_generation = uuid4().hex
        self._allocator_probes = tuple(
            (torch_allocator_snapshot, paddle_allocator_snapshot)
            if allocator_probes is None
            else allocator_probes
        )
        self._lock = RLock()
        self._states: dict[RuntimeModelKey, _RuntimeModelState] = {}
        self._queues: dict[RuntimeModelKey, list[RuntimeOperationTicket]] = {}
        self._ticket_order = 0
        self._process_activity_epoch = 0

    def enqueue(
        self,
        key: RuntimeModelKey,
        *,
        priority_class: str,
        priority: int = 0,
        request_id: str = "",
    ) -> RuntimeOperationTicket:
        priority_class = normalise_priority_class(priority_class)
        try:
            resolved_priority = int(priority)
        except (TypeError, ValueError):
            resolved_priority = 0
        resolved_priority = max(runtime_request_priority(priority_class), resolved_priority)
        with self._lock:
            self._mark_loaded(key)
            queue = self._queues.setdefault(key, [])
            ticket = RuntimeOperationTicket(
                id=uuid4().hex,
                key=key,
                priority_class=priority_class,
                priority=resolved_priority,
                request_id=request_id,
                is_head=not queue,
                _order=self._ticket_order,
            )
            self._ticket_order += 1
            queue.append(ticket)
            queue.sort(key=lambda item: (-item.priority, item._order))
            self._refresh_queue_heads(key)
            return ticket

    def ready_to_start(self, ticket: RuntimeOperationTicket) -> bool:
        """Whether a ticket is next and can safely seek a global GPU lease."""
        with self._lock:
            queue = self._queues.get(ticket.key, [])
            state = self._states.get(ticket.key)
            return bool(state is not None and queue and queue[0] is ticket and state.active_ticket_id is None)

    def should_yield(self, ticket: RuntimeOperationTicket) -> bool:
        """Whether a higher-priority *waiting* local operation is ahead of a ticket.

        A currently active operation is intentionally ignored: it has already
        crossed the non-pre-emptive CUDA boundary and must finish naturally.
        """
        with self._lock:
            for queued in self._queues.get(ticket.key, []):
                if queued is ticket:
                    return False
                if queued.is_active:
                    continue
                return queued.priority > ticket.priority
        return False

    @contextmanager
    def operation(self, ticket: RuntimeOperationTicket) -> Iterator[RuntimeOperationMeasurement]:
        with self._lock:
            queue = self._queues.get(ticket.key, [])
            state = self._states.get(ticket.key)
            if state is None or ticket not in queue:
                raise RuntimeError("runtime operation ticket is no longer queued")
            if state.active_ticket_id is not None:
                raise RuntimeOperationNotReady("a runtime operation is already active for this model")
            if queue[0] is not ticket:
                raise RuntimeOperationNotReady("runtime operation ticket is not at the queue head")
            now = time()
            state.active_ticket_id = ticket.id
            state.in_flight += 1
            state.last_started_at = now
            ticket.is_active = True
            ticket.is_head = True
            self._process_activity_epoch += 1
            measurement = RuntimeOperationMeasurement(
                started_at=now,
                activity_sequence=state.activity_sequence,
                in_flight=state.in_flight,
                process_in_flight=sum(item.in_flight for item in self._states.values()),
                process_activity_epoch=self._process_activity_epoch,
            )
        try:
            yield measurement
        finally:
            with self._lock:
                state = self._states[ticket.key]
                state.in_flight -= 1
                state.active_ticket_id = None
                state.activity_sequence += 1
                state.last_activity_at = time()
                self._process_activity_epoch += 1
                ticket.is_active = False
                ticket.is_head = False
                queue = self._queues[ticket.key]
                queue.remove(ticket)
                if not queue:
                    del self._queues[ticket.key]
                else:
                    self._refresh_queue_heads(ticket.key)

    def inventory(self, loaded_models: Iterable[RuntimeModelKey]) -> dict[str, Any]:
        with self._lock:
            loaded_keys = tuple(loaded_models)
            loaded_key_set = set(loaded_keys)
            for key, state in self._states.items():
                if key not in loaded_key_set and not state.in_flight and not self._queues.get(key):
                    state.is_loaded = False
            models = []
            for key in loaded_keys:
                state = self._mark_loaded(key)
                models.append(
                    {
                        "task_type": key.task_type,
                        "model_id": key.model_id,
                        "owner_component": self.owner_component,
                        "process_generation": self.process_generation,
                        "activity_sequence": state.activity_sequence,
                        "in_flight": state.in_flight,
                        "last_started_at": state.last_started_at,
                        "last_activity_at": state.last_activity_at,
                    }
                )
        return {"models": models, "allocator": self._allocator_snapshots()}

    def next_waiting_ticket_id(self, key: RuntimeModelKey) -> str | None:
        with self._lock:
            for ticket in self._queues.get(key, []):
                if not ticket.is_active:
                    return ticket.id
        return None

    def discard_waiting(self, ticket: RuntimeOperationTicket) -> bool:
        """Remove an abandoned inactive ticket without interrupting live work.

        A caller may fail global admission after enqueueing locally.  That
        ticket has never crossed the runtime-operation boundary and must not
        remain at the local queue head.  Active tickets are deliberately
        protected: this method is cleanup, never cancellation.
        """
        with self._lock:
            queue = self._queues.get(ticket.key, [])
            state = self._states.get(ticket.key)
            if ticket not in queue:
                return False
            if ticket.is_active or (state is not None and state.active_ticket_id == ticket.id):
                return False
            queue.remove(ticket)
            ticket.is_head = False
            if not queue:
                self._queues.pop(ticket.key, None)
            else:
                self._refresh_queue_heads(ticket.key)
            return True

    def process_in_flight(self) -> int:
        """Return active operations across every model in this service process."""
        with self._lock:
            return sum(state.in_flight for state in self._states.values())

    def process_activity_epoch(self) -> int:
        """Return a monotonically increasing lifecycle epoch for local operations."""
        with self._lock:
            return self._process_activity_epoch

    def unload(
        self,
        key: RuntimeModelKey,
        *,
        expected_generation: str,
        expected_activity_sequence: int,
        remove: Callable[[], bool],
        release_allocator: Callable[[], Any] | None = None,
    ) -> dict[str, Any]:
        with self._lock:
            if expected_generation != self.process_generation:
                return {"unloaded": False, "reason": "generation_mismatch"}
            state = self._states.get(key)
            if state is None:
                return {"unloaded": False, "reason": "absent"}
            if state.activity_sequence != expected_activity_sequence:
                return {"unloaded": False, "reason": "activity_mismatch"}
            if state.in_flight:
                return {"unloaded": False, "reason": "in_flight"}
            # Removing one model may release a process-wide CUDA/Paddle cache.
            # Do not touch that shared allocator while another model operation
            # in this runtime process is live.
            if any(item.in_flight for item in self._states.values()):
                return {"unloaded": False, "reason": "process_in_flight"}
            if self._queues.get(key):
                return {"unloaded": False, "reason": "queued"}
            if not state.is_loaded:
                return {"unloaded": False, "reason": "absent"}
            removed = remove()
            if removed:
                state.is_loaded = False
                state.last_activity_at = time()
                # ``empty_cache``-style calls touch process-wide framework
                # state, so execute them before releasing the tracker gate.
                if release_allocator is not None:
                    release_allocator()
            return {
                "unloaded": bool(removed),
                "reason": "unloaded" if removed else "remove_failed",
            }

    def trim_allocator(
        self,
        key: RuntimeModelKey,
        *,
        expected_generation: str,
        expected_activity_sequence: int,
        trim: Callable[[], Any],
    ) -> dict[str, Any]:
        """Release framework cache only while this runtime is completely idle.

        Cache trimming keeps the loaded model resident, but it still touches the
        process-wide CUDA/Paddle allocators.  Hold the tracker gate around the
        operation so no tracked CUDA call can start between the activity fence
        check and the trim itself.
        """
        with self._lock:
            if expected_generation != self.process_generation:
                return {"trimmed": False, "reason": "generation_mismatch"}
            state = self._states.get(key)
            if state is None or not state.is_loaded:
                return {"trimmed": False, "reason": "absent"}
            if state.activity_sequence != expected_activity_sequence:
                return {"trimmed": False, "reason": "activity_mismatch"}
            if any(item.in_flight for item in self._states.values()):
                return {"trimmed": False, "reason": "in_flight"}
            trim()
            return {"trimmed": True, "reason": "trimmed"}

    def _allocator_snapshots(self) -> list[AllocatorSnapshot]:
        snapshots = []
        for probe in self._allocator_probes:
            try:
                snapshots.append(probe())
            except (ImportError, OSError) as exc:
                snapshots.append(_known_unmeasured(probe.__name__, str(exc)))
        return snapshots

    def _refresh_queue_heads(self, key: RuntimeModelKey) -> None:
        for index, ticket in enumerate(self._queues.get(key, ())):
            ticket.is_head = index == 0

    def _mark_loaded(self, key: RuntimeModelKey) -> _RuntimeModelState:
        state = self._states.get(key)
        if state is None:
            state = _RuntimeModelState(is_loaded=True)
            self._states[key] = state
        elif not state.is_loaded:
            state.is_loaded = True
            state.activity_sequence += 1
            state.last_activity_at = time()
        return state
