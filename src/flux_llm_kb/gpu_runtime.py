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
        request_id: str = "",
    ) -> RuntimeOperationTicket:
        priority_class = normalise_priority_class(priority_class)
        with self._lock:
            self._mark_loaded(key)
            queue = self._queues.setdefault(key, [])
            ticket = RuntimeOperationTicket(
                id=uuid4().hex,
                key=key,
                priority_class=priority_class,
                request_id=request_id,
                is_head=not queue,
                _order=self._ticket_order,
            )
            self._ticket_order += 1
            queue.append(ticket)
            queue.sort(key=lambda item: (-runtime_request_priority(item.priority_class), item._order))
            self._refresh_queue_heads(key)
            return ticket

    @contextmanager
    def operation(self, ticket: RuntimeOperationTicket) -> Iterator[RuntimeOperationMeasurement]:
        with self._lock:
            queue = self._queues.get(ticket.key, [])
            state = self._states.get(ticket.key)
            if state is None or ticket not in queue:
                raise RuntimeError("runtime operation ticket is no longer queued")
            if state.active_ticket_id is not None:
                raise RuntimeError("a runtime operation is already active for this model")
            if queue[0] is not ticket:
                raise RuntimeError("runtime operation ticket is not at the queue head")
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
            if self._queues.get(key):
                return {"unloaded": False, "reason": "queued"}
            if not state.is_loaded:
                return {"unloaded": False, "reason": "absent"}
            removed = remove()
            if removed:
                state.is_loaded = False
                state.last_activity_at = time()
            return {
                "unloaded": bool(removed),
                "reason": "unloaded" if removed else "remove_failed",
            }

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
