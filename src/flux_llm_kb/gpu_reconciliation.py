"""Bounded, observation-only reconciliation of GPU runtime inventory."""

from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor, wait
from dataclasses import dataclass, field, replace
import hashlib
import json
import time
from typing import Any, Callable, Protocol, Sequence
from urllib.parse import urljoin
from urllib.request import Request, urlopen
from uuid import uuid4


RUNTIME_STATES = frozenset({"healthy", "inventory_incomplete", "reconciliation_required", "unschedulable"})


@dataclass(frozen=True)
class RuntimeInventorySnapshot:
    component: str
    owner_component: str = ""
    process_generation: str = ""
    process_identity: str = ""
    process_count: int = 1
    process_inventory_aggregated: bool = True
    runtime_fingerprint: str = ""
    state: str = "unknown"
    allocator_capability: str = "unknown"
    known_measured_mb: int = 0
    known_reported_mb: int = 0
    models: tuple[dict[str, Any], ...] = ()
    allocators: tuple[dict[str, Any], ...] = ()
    error_code: str = ""
    error_metadata: dict[str, Any] = field(default_factory=dict)
    observed_at: float = field(default_factory=time.time)


@dataclass(frozen=True)
class GpuReconciliationObservation:
    observation_id: str
    state: str
    driver_used_mb: int
    driver_free_mb: int
    raw_residual_mb: int
    unresolved_known_owner_mb: int
    unattributed_mb: int
    inventories: tuple[RuntimeInventorySnapshot, ...] = ()
    context_allowance_mb: int = 0
    driver_observation_state: str = "available"
    driver_error: str = ""
    observed_at: float = field(default_factory=time.time)
    error_metadata: dict[str, Any] = field(default_factory=dict)


class RuntimeInventoryAdapter(Protocol):
    component: str

    def read_inventory(self, timeout_seconds: float) -> RuntimeInventorySnapshot: ...


@dataclass(frozen=True)
class HttpRuntimeInventoryAdapter:
    component: str
    base_url: str

    def read_inventory(self, timeout_seconds: float) -> RuntimeInventorySnapshot:
        payload = _get_json(self.base_url, "/v1/gpu/residency", timeout_seconds=timeout_seconds)
        return inventory_from_payload(self.component, payload)


@dataclass(frozen=True)
class OllamaInventoryAdapter:
    base_url: str
    component: str = "ollama"

    def read_inventory(self, timeout_seconds: float) -> RuntimeInventorySnapshot:
        payload = _get_json(self.base_url, "/api/ps", timeout_seconds=timeout_seconds)
        return ollama_inventory_from_payload(payload, component=self.component)


def ollama_inventory_from_payload(payload: dict[str, Any], *, component: str = "ollama") -> RuntimeInventorySnapshot:
    """Normalise one authoritative `/api/ps` response, including its fresh fingerprint."""
    models = []
    reported_mb = 0
    for item in payload.get("models", ()) if isinstance(payload.get("models"), list) else ():
        if not isinstance(item, dict):
            continue
        model_id = str(item.get("name") or item.get("model") or "").strip()
        if not model_id:
            continue
        size_vram = _mb(item.get("size_vram"), bytes_value=True)
        models.append({"task_type": "ollama_vision", "model_id": model_id, "owner_component": "ollama", "size_vram_mb": size_vram})
        reported_mb += size_vram
    fingerprint_source = json.dumps(models, sort_keys=True, separators=(",", ":"))
    fingerprint = hashlib.sha256(fingerprint_source.encode("utf-8")).hexdigest()[:32]
    models = [
        {
            **model,
            "process_generation": fingerprint,
            "activity_sequence": 0,
            "in_flight": 0,
            "runtime_fence": "ollama_inventory_fingerprint",
        }
        for model in models
    ]
    return RuntimeInventorySnapshot(
        component=component,
        owner_component="ollama",
        state="present",
        allocator_capability="reported",
        known_reported_mb=reported_mb,
        models=tuple(models),
        runtime_fingerprint=fingerprint,
    )


def inventory_from_payload(component: str, payload: dict[str, Any]) -> RuntimeInventorySnapshot:
    allocators = tuple(item for item in payload.get("allocator", ()) if isinstance(item, dict)) if isinstance(payload.get("allocator"), list) else ()
    models = tuple(item for item in payload.get("models", ()) if isinstance(item, dict)) if isinstance(payload.get("models"), list) else ()
    measured = 0
    capability = "unknown"
    for allocator in allocators:
        current_capability = str(allocator.get("capability") or "unknown")
        if current_capability == "measured":
            capability = "measured"
            measured += max(_mb(allocator.get("reserved_mb")), _mb(allocator.get("allocated_mb")))
        elif capability != "measured" and current_capability == "known_unmeasured":
            capability = "known_unmeasured"
    generation = ""
    if models:
        generation = str(models[0].get("process_generation") or "")
    process = payload.get("process") if isinstance(payload.get("process"), dict) else {}
    generation = generation or str(process.get("generation") or payload.get("process_generation") or "")
    process_count = max(1, _mb(process.get("worker_count") or process.get("process_count") or payload.get("worker_count") or payload.get("process_count") or 1))
    aggregated_value = process.get("inventory_aggregated", payload.get("inventory_aggregated"))
    process_inventory_aggregated = _bool(aggregated_value, default=process_count == 1)
    process_identity = str(
        process.get("id") or process.get("identity") or payload.get("process_id") or generation
    )
    declared_owner = str(payload.get("owner_component") or "").strip()
    model_owners: dict[tuple[str, str], set[str]] = {}
    missing_model_owner = False
    for model in models:
        key = (str(model.get("task_type") or ""), str(model.get("model_id") or ""))
        if not all(key):
            continue
        owner = str(model.get("owner_component") or declared_owner).strip()
        if not owner:
            missing_model_owner = True
            continue
        model_owners.setdefault(key, set()).add(owner)
    conflicted = any(len(owners) > 1 for owners in model_owners.values())
    return RuntimeInventorySnapshot(
        component=component,
        owner_component=declared_owner or component,
        process_generation=generation,
        process_identity=process_identity,
        runtime_fingerprint=str(payload.get("runtime_fingerprint") or ""),
        process_count=process_count,
        process_inventory_aggregated=process_inventory_aggregated,
        state="unknown" if missing_model_owner else ("conflicted" if conflicted else "present"),
        allocator_capability=capability,
        known_measured_mb=measured,
        models=models,
        allocators=allocators,
        error_code="owner_missing" if missing_model_owner else ("owner_conflict" if conflicted else ""),
    )


def calculate_gpu_capacity(
    *,
    driver_used_mb: int,
    driver_free_mb: int,
    inventories: Sequence[RuntimeInventorySnapshot],
    context_allowance_mb: int,
    material_threshold_mb: int,
    material_threshold_percent: int = 0,
    total_memory_mb: int | None = None,
    driver_observation_available: bool = True,
    driver_observation_state: str = "available",
    driver_error: str = "",
    observation_id: str | None = None,
) -> GpuReconciliationObservation:
    known_measured_mb = sum(max(0, item.known_measured_mb) for item in inventories)
    # Exact Ollama ``size_vram`` is only redundant when that *same process*
    # exposes a positive measured allocator amount (allocated or reserved).
    # Another service's allocator amount is not evidence that Ollama's
    # reported memory is already accounted for.
    known_reported_mb = sum(
        max(0, item.known_reported_mb)
        for item in inventories
        if not any(
            max(0, _mb(allocator.get("reserved_mb")), _mb(allocator.get("allocated_mb")))
            for allocator in item.allocators
        )
    )
    confirmed_processes = sum(
        max(1, int(item.process_count or 1)) for item in inventories if item.state in {"present", "conflicted"}
    )
    allowance = max(0, int(context_allowance_mb)) * confirmed_processes
    raw_residual_mb = max(0, int(driver_used_mb) - known_measured_mb - known_reported_mb - allowance)
    known_unmeasured = any(item.allocator_capability == "known_unmeasured" for item in inventories)
    unresolved_known_owner_mb = raw_residual_mb if known_unmeasured else 0
    unattributed_mb = 0 if known_unmeasured else raw_residual_mb
    percentage_threshold_mb = 0
    if total_memory_mb is not None:
        percentage_threshold_mb = max(0, int(total_memory_mb)) * max(0, int(material_threshold_percent)) // 100
    if not driver_observation_available:
        state = "inventory_incomplete"
    elif int(driver_free_mb) <= 0:
        state = "unschedulable"
    elif (
        known_unmeasured
        or any(item.state in {"unknown", "conflicted"} for item in inventories)
        or any(not item.process_inventory_aggregated for item in inventories if item.state in {"present", "conflicted"})
    ):
        state = "inventory_incomplete"
    elif unattributed_mb >= max(0, int(material_threshold_mb), percentage_threshold_mb):
        state = "reconciliation_required"
    else:
        state = "healthy"
    return GpuReconciliationObservation(
        observation_id=observation_id or uuid4().hex,
        state=state,
        driver_used_mb=max(0, int(driver_used_mb)),
        driver_free_mb=max(0, int(driver_free_mb)),
        raw_residual_mb=raw_residual_mb,
        unresolved_known_owner_mb=unresolved_known_owner_mb,
        unattributed_mb=unattributed_mb,
        inventories=tuple(inventories),
        context_allowance_mb=allowance,
        driver_observation_state=driver_observation_state,
        driver_error=driver_error[:300],
    )


def reconcile_runtime_inventory(
    adapters: Sequence[RuntimeInventoryAdapter],
    *,
    live_gpu_memory: Callable[[], dict[str, Any]],
    timeout_seconds: float = 2.0,
    context_allowance_mb: int = 256,
    material_threshold_mb: int = 512,
    material_threshold_percent: int = 0,
    total_memory_mb: int | None = None,
    persist: Callable[[GpuReconciliationObservation], Any] | None = None,
) -> GpuReconciliationObservation:
    """Read all endpoints within one absolute deadline; never wait on stragglers."""
    deadline = time.monotonic() + max(0.01, float(timeout_seconds))
    executor = ThreadPoolExecutor(max_workers=4)
    futures = {executor.submit(adapter.read_inventory, max(0.01, deadline - time.monotonic())): adapter for adapter in adapters}
    driver_future = executor.submit(live_gpu_memory)
    done, pending = wait((*futures, driver_future), timeout=max(0.0, deadline - time.monotonic()))
    inventories: list[RuntimeInventorySnapshot] = []
    for future in done:
        if future is driver_future:
            continue
        adapter = futures[future]
        try:
            inventories.append(future.result())
        except Exception as exc:
            inventories.append(_failed_snapshot(adapter.component, exc))
    for future in pending:
        if future is driver_future:
            continue
        adapter = futures[future]
        future.cancel()
        inventories.append(RuntimeInventorySnapshot(component=adapter.component, state="unknown", error_code="deadline_exceeded", error_metadata={"message": "inventory endpoint exceeded reconciliation deadline"}))
    # Deliberately avoid executor context manager: it would wait for running HTTP calls.
    executor.shutdown(wait=False, cancel_futures=True)
    driver_state = "available"
    driver_error = ""
    if driver_future.done():
        try:
            memory = driver_future.result()
        except Exception as exc:
            memory = {"gpus": []}
            driver_state = "failed"
            driver_error = str(exc).replace("\x00", " ")[:300]
    else:
        driver_future.cancel()
        memory = {"gpus": []}
        driver_state = "timed_out"
        driver_error = "driver observation exceeded reconciliation deadline"
    inventories = _mark_cross_component_conflicts(inventories)
    gpus = memory.get("gpus") if isinstance(memory, dict) else []
    gpu = gpus[0] if isinstance(gpus, list) and gpus and isinstance(gpus[0], dict) else {}
    if driver_state == "available" and (
        not isinstance(memory, dict)
        or memory.get("ok") is False
        or not isinstance(gpus, list)
        or not gpus
    ):
        driver_state = str(memory.get("state") or "unavailable")[:80] if isinstance(memory, dict) else "unavailable"
        driver_error = str(memory.get("error") or "driver observation did not include a GPU reading")[:300] if isinstance(memory, dict) else "driver observation did not include a GPU reading"
    observation = calculate_gpu_capacity(
        driver_used_mb=_mb(gpu.get("memory_used_mb")),
        driver_free_mb=_mb(gpu.get("memory_free_mb")),
        inventories=inventories,
        context_allowance_mb=context_allowance_mb,
        material_threshold_mb=material_threshold_mb,
        material_threshold_percent=material_threshold_percent,
        total_memory_mb=total_memory_mb if total_memory_mb is not None else _mb(gpu.get("memory_total_mb")),
        driver_observation_available=driver_state == "available",
        driver_observation_state=driver_state,
        driver_error=driver_error,
    )
    if persist is not None:
        persist(observation)
    return observation


def _failed_snapshot(component: str, exc: Exception) -> RuntimeInventorySnapshot:
    message = str(exc).replace("\x00", " ")[:300]
    return RuntimeInventorySnapshot(component=component, state="unknown", error_code=type(exc).__name__.lower()[:80], error_metadata={"message": message})


def _mark_cross_component_conflicts(inventories: Sequence[RuntimeInventorySnapshot]) -> list[RuntimeInventorySnapshot]:
    owners_by_key: dict[tuple[str, str], set[str]] = {}
    for inventory in inventories:
        for model in inventory.models:
            if not isinstance(model, dict):
                continue
            key = (str(model.get("task_type") or ""), str(model.get("model_id") or ""))
            if not all(key):
                continue
            owner = str(model.get("owner_component") or inventory.owner_component or inventory.component)
            owners_by_key.setdefault(key, set()).add(owner)
    conflicted = {key for key, owners in owners_by_key.items() if len(owners) > 1}
    if not conflicted:
        return list(inventories)
    marked: list[RuntimeInventorySnapshot] = []
    for inventory in inventories:
        has_conflict = any(
            isinstance(model, dict)
            and (str(model.get("task_type") or ""), str(model.get("model_id") or "")) in conflicted
            for model in inventory.models
        )
        marked.append(replace(inventory, state="conflicted", error_code="owner_conflict") if has_conflict else inventory)
    return marked


def _mb(value: Any, *, bytes_value: bool = False) -> int:
    try:
        number = int(value or 0)
    except (TypeError, ValueError):
        return 0
    return max(0, number // (1024 * 1024) if bytes_value else number)


def _bool(value: Any, *, default: bool) -> bool:
    if isinstance(value, bool):
        return value
    if isinstance(value, str):
        return value.strip().lower() in {"1", "true", "yes", "on"}
    return default


def _get_json(base_url: str, path: str, *, timeout_seconds: float) -> dict[str, Any]:
    request = Request(urljoin(f"{base_url.rstrip('/')}/", path.lstrip("/")), method="GET")
    with urlopen(request, timeout=max(0.01, float(timeout_seconds))) as response:
        payload = json.loads(response.read().decode("utf-8") or "{}")
    if not isinstance(payload, dict):
        raise ValueError("runtime inventory endpoint returned a non-object payload")
    return payload
