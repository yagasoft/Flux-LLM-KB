from __future__ import annotations

from dataclasses import dataclass, field
import json
import os
import threading
import time
from typing import Any, Callable, Iterable
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin
from urllib.request import Request, urlopen
from uuid import uuid4


GPU_LEASE_STATUSES = frozenset({"waiting", "running", "released", "timed_out", "recovered", "rejected"})
GPU_EVICTION_MAX_ATTEMPTS = 3
GPU_EVICTION_RETRY_DELAY_SECONDS = 10.0
GPU_EVICTION_VERIFICATION_POLL_INTERVAL_SECONDS = 0.5
GPU_EVICTION_MIN_FREED_FRACTION = 0.5
GPU_EVICTION_MIN_FREED_MB = 256


@dataclass(frozen=True)
class GpuTaskProfile:
    task_type: str
    model_id: str = ""
    estimated_vram_mb: int = 0
    priority: int = 0
    timeout_seconds: float | None = None
    lease_ttl_seconds: float | None = None
    exclusive: bool = True
    share_group: str = ""
    component: str = ""
    request_id: str = ""
    priority_class: str = "background"
    admission_key: str = ""
    shape_bucket: str = ""
    metadata: dict[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class GpuRequestShape:
    task_type: str
    bucket: str


@dataclass(frozen=True)
class GpuVramCalibration:
    load_delta_mb: int = 0
    working_set_mb: int = 0
    sample_count: int = 0
    source: str = "configured"


@dataclass(frozen=True)
class GpuVramReservation:
    shape_bucket: str
    resident_floor_mb: int
    load_delta_mb: int
    working_set_mb: int
    guard_margin_mb: int
    reserved_peak_mb: int
    calibration_source: str


@dataclass(frozen=True)
class GpuSchedulerConfig:
    enabled: bool = True
    mode: str = "auto"
    total_vram_mb: int = 0
    vram_budget_mb: int = 10_240
    safety_margin_mb: int = 1_024
    default_timeout_seconds: float = 30.0
    background_timeout_seconds: float = 1.0
    lease_ttl_seconds: float = 120.0
    heartbeat_interval_seconds: float = 10.0
    stale_after_seconds: float = 180.0
    eviction_enabled: bool = True
    eviction_request_timeout_seconds: float = 10.0
    eviction_max_models: int = 4
    allocator_trim_enabled: bool = True
    idle_unload_enabled: bool = True
    idle_unload_seconds: float = 120.0
    idle_sweep_interval_seconds: float = 30.0
    model_runner_base_url: str = ""
    paddle_runner_base_url: str = ""
    asr_base_url: str = ""
    ollama_base_url: str = ""
    runtime_reconciliation_mode: str = "observation"
    inventory_timeout_seconds: float = 2.0
    control_lock_timeout_seconds: float = 2.0
    context_allowance_mb: int = 256
    unattributed_threshold_mb: int = 512
    unattributed_threshold_percent: int = 5
    reconciliation_retry_seconds: float = 15.0


@dataclass(frozen=True)
class GpuLeaseRecord:
    id: str
    task_type: str
    model_id: str
    status: str
    estimated_vram_mb: int
    exclusive: bool
    share_group: str
    priority: int
    component: str
    request_id: str
    created_at: float
    granted_at: float | None
    heartbeat_at: float | None
    expires_at: float | None
    released_at: float | None
    metadata: dict[str, Any] = field(default_factory=dict)
    priority_class: str = "background"
    admission_key: str = ""
    shape_bucket: str = ""
    caller_attached: bool = True
    wait_reason: str = ""
    eviction_id: str = ""


@dataclass(frozen=True)
class GpuModelResidency:
    model_id: str
    task_type: str
    estimated_vram_mb: int
    resident: bool = True
    last_used_at: float | None = None
    metadata: dict[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class GpuEvictionCandidate:
    task_type: str
    model_id: str
    estimated_vram_mb: int
    component: str
    last_used_at: float | None = None
    metadata: dict[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class GpuAdmissionDecision:
    granted: bool
    rejected: bool
    reason: str
    active_vram_mb: int
    resident_vram_mb: int
    available_vram_mb: int
    recovered_lease_ids: list[str] = field(default_factory=list)
    incremental_vram_mb: int = 0
    resident_hit: bool = False
    eviction_candidates: list[GpuEvictionCandidate] = field(default_factory=list)
    capacity_state: str = "healthy"
    observation_id: str | None = None
    calibration_source: str = "configured"
    load_delta_mb: int = 0
    working_set_mb: int = 0
    reserved_peak_mb: int = 0


@dataclass(frozen=True)
class GpuEvictionVerificationResult:
    verified: bool
    payload: dict[str, Any] = field(default_factory=dict)
    error: str = ""
    metadata: dict[str, Any] = field(default_factory=dict)


class GpuSchedulerError(RuntimeError):
    pass


class GpuLeaseTimeout(GpuSchedulerError):
    def __init__(self, message: str, *, retry_after_seconds: float = 1.0) -> None:
        super().__init__(message)
        self.retry_after_seconds = max(0.1, float(retry_after_seconds))


class GpuLeaseDeferred(GpuSchedulerError):
    def __init__(
        self,
        message: str,
        *,
        capacity_state: str = "healthy",
        admission_id: str = "",
        eviction_id: str = "",
        retry_after_seconds: float = 1.0,
    ) -> None:
        super().__init__(message)
        self.capacity_state = str(capacity_state or "healthy")
        self.admission_id = str(admission_id or "")
        self.eviction_id = str(eviction_id or "")
        self.retry_after_seconds = max(0.1, float(retry_after_seconds))


class GpuLeaseRejected(GpuSchedulerError):
    def __init__(
        self,
        message: str,
        *,
        capacity_state: str | None = None,
        retry_after_seconds: float = 1.0,
    ) -> None:
        super().__init__(message)
        self.capacity_state = str(capacity_state or "")
        self.retry_after_seconds = max(0.1, float(retry_after_seconds))
        self.retryable = self.capacity_state != "unschedulable"


class GpuLease:
    def __init__(self, scheduler: "BaseGpuScheduler", record: GpuLeaseRecord) -> None:
        self.scheduler = scheduler
        self.record = record
        self.released = False
        self._heartbeat_stop = threading.Event()
        self._heartbeat_thread: threading.Thread | None = None
        self._release_lock = threading.Lock()
        self._start_heartbeat_loop()

    @property
    def id(self) -> str:
        return self.record.id

    def heartbeat(self) -> None:
        self.scheduler.heartbeat(self.id)

    def release(self) -> None:
        with self._release_lock:
            if self.released:
                return
            self.released = True
            self._heartbeat_stop.set()
        self.scheduler.release(self.id)

    def __enter__(self) -> "GpuLease":
        return self

    def __exit__(self, *_args: Any) -> bool:
        self.release()
        return False

    def _start_heartbeat_loop(self) -> None:
        interval = float(getattr(self.scheduler.config, "heartbeat_interval_seconds", 0.0) or 0.0)
        if interval <= 0:
            return

        def _heartbeat_until_released() -> None:
            while not self._heartbeat_stop.wait(interval):
                try:
                    self.scheduler.heartbeat(self.id)
                except Exception:
                    pass

        self._heartbeat_thread = threading.Thread(
            target=_heartbeat_until_released,
            name=f"gpu-lease-heartbeat-{self.id[:8]}",
            daemon=True,
        )
        self._heartbeat_thread.start()


class BaseGpuScheduler:
    config: GpuSchedulerConfig

    def acquire(self, profile: GpuTaskProfile, *, yield_wait: Callable[[], bool] | None = None) -> GpuLease:
        raise NotImplementedError

    def release(self, lease_id: str) -> None:
        raise NotImplementedError

    def heartbeat(self, lease_id: str) -> None:
        raise NotImplementedError

    def status(self) -> dict[str, Any]:
        raise NotImplementedError

    def record_model_residency(self, residency: GpuModelResidency) -> None:
        return None

    def reset_component_residency(self, component: str) -> None:
        return None

    def record_vram_sample(
        self,
        profile: GpuTaskProfile,
        *,
        pre_load_reserved_mb: int | None,
        post_load_reserved_mb: int | None,
        execution_peak_reserved_mb: int | None,
        allocator_capability: str,
        tracker_overlapped: bool,
        sample_skipped_reason: str = "",
    ) -> None:
        return None


def _numeric_bucket(value: Any, *, ranges: tuple[tuple[int, str], ...]) -> str:
    try:
        number = max(0, int(value or 0))
    except (TypeError, ValueError):
        number = 0
    for upper_bound, label in ranges:
        if number <= upper_bound:
            return label
    return ranges[-1][1]


def shape_bucket_for_profile(profile: GpuTaskProfile) -> str:
    """Return a bounded numeric-only request shape bucket suitable for persistence."""
    metadata = dict(profile.metadata or {})
    task_type = str(profile.task_type or "unknown").strip().lower()[:40]
    if task_type == "embedding":
        count = _numeric_bucket(metadata.get("input_count"), ranges=((1, "1"), (4, "2-4"), (8, "5-8"), (16, "9-16"), (32, "17-32"), (10**9, "33+")))
        total = _numeric_bucket(metadata.get("total_input_characters"), ranges=((256, "0-256"), (1024, "257-1024"), (4096, "1025-4096"), (16384, "4097-16384"), (10**9, "16385+")))
        maximum = _numeric_bucket(metadata.get("max_input_characters"), ranges=((256, "0-256"), (1024, "257-1024"), (4096, "1025-4096"), (10**9, "4097+")))
        dimensions = _numeric_bucket(metadata.get("dimensions"), ranges=((384, "0-384"), (768, "385-768"), (1023, "769-1023"), (1024, "1024"), (10**9, "1025+")))
        return f"embedding|count={count}|chars={total}|item={maximum}|dims={dimensions}"
    if task_type == "rerank":
        passages = _numeric_bucket(metadata.get("passage_count"), ranges=((1, "1"), (4, "2-4"), (16, "5-16"), (64, "17-64"), (10**9, "65+")))
        tokens = _numeric_bucket(metadata.get("total_token_count"), ranges=((256, "0-256"), (1024, "257-1024"), (4096, "1025-4096"), (10**9, "4097+")))
        return f"rerank|passages={passages}|tokens={tokens}"
    workload = str(metadata.get("workload_class") or "unknown").strip().lower()
    workload = workload if workload in {"tiny", "small", "medium", "large", "unknown"} else "unknown"
    return f"{task_type}|workload={workload}"


def _seeded_calibration(profile: GpuTaskProfile, shape_bucket: str) -> GpuVramCalibration | None:
    if (
        str(profile.task_type or "").lower() == "embedding"
        and str(profile.model_id or "").lower().startswith("snowflake/")
        and "count=9-16" in shape_bucket
        and "dims=1024" in shape_bucket
    ):
        # The observation was a 1024-dimension, 9-16 item Snowflake batch. It
        # is a working-set floor only; it is intentionally not a model-wide load estimate.
        return GpuVramCalibration(working_set_mb=12_186, sample_count=0, source="observed_seed")
    return None


def resolve_vram_reservation(
    profile: GpuTaskProfile,
    *,
    resident_hit: bool,
    calibration: GpuVramCalibration | None = None,
) -> GpuVramReservation:
    shape_bucket = shape_bucket_for_profile(profile)
    resolved = calibration or _seeded_calibration(profile, shape_bucket)
    fallback = max(0, int(profile.estimated_vram_mb or 0))
    if resolved is None:
        resolved = GpuVramCalibration(working_set_mb=fallback, source="configured")
    load_delta = max(0, int(resolved.load_delta_mb or 0))
    working_set = max(0, int(resolved.working_set_mb or 0))
    resident_floor = 0
    # Configured estimates are a safe request working-set fallback. Measured
    # values preserve the distinct cold-load and execution components.
    reserved_peak = resident_floor + working_set + (0 if resident_hit else load_delta)
    if not resident_hit:
        reserved_peak = max(reserved_peak, fallback)
    return GpuVramReservation(
        shape_bucket=shape_bucket,
        resident_floor_mb=resident_floor,
        load_delta_mb=load_delta,
        working_set_mb=working_set,
        guard_margin_mb=0,
        reserved_peak_mb=reserved_peak,
        calibration_source=str(resolved.source or "configured"),
    )


def plan_gpu_admission(
    profile: GpuTaskProfile,
    *,
    active_leases: Iterable[GpuLeaseRecord],
    config: GpuSchedulerConfig,
    resident_models: Iterable[GpuModelResidency] | None = None,
    waiting_leases: Iterable[GpuLeaseRecord] | None = None,
    live_free_vram_mb: int | None = None,
    calibration: GpuVramCalibration | None = None,
    capacity_state: str = "healthy",
    observation_id: str | None = None,
    now: float | None = None,
) -> GpuAdmissionDecision:
    timestamp = time.time() if now is None else float(now)
    recovered: list[str] = []
    active: list[GpuLeaseRecord] = []
    for lease in active_leases:
        if lease.status != "running":
            continue
        if _lease_is_stale(lease, now=timestamp):
            recovered.append(lease.id)
            continue
        active.append(lease)

    resident_items = [
        resident
        for resident in resident_models or ()
        if resident.resident and resident.model_id
    ]
    requested_key = (profile.task_type, profile.model_id)
    resident_hit = any((resident.task_type, resident.model_id) == requested_key for resident in resident_items)
    residents = [
        resident
        for resident in resident_items
        if (resident.task_type, resident.model_id) != requested_key
    ]
    resident_vram = sum(max(0, int(resident.estimated_vram_mb or 0)) for resident in residents)
    active_vram = sum(max(0, int(lease.estimated_vram_mb or 0)) for lease in active)
    raw_headroom = _available_vram_mb(config, live_free_vram_mb=live_free_vram_mb)
    # Driver free memory is already the physical anchor for resident allocations.
    # Only process-local active peak reservations need an additional admission hold.
    estimated_resident_vram = 0 if live_free_vram_mb is not None else resident_vram
    effective_available = max(0, raw_headroom - estimated_resident_vram - active_vram)
    reservation = resolve_vram_reservation(profile, resident_hit=resident_hit, calibration=calibration)
    requested_vram = reservation.reserved_peak_mb
    details = {
        "capacity_state": str(capacity_state or "healthy"),
        "observation_id": observation_id,
        "calibration_source": reservation.calibration_source,
        "load_delta_mb": reservation.load_delta_mb,
        "working_set_mb": reservation.working_set_mb,
        "reserved_peak_mb": reservation.reserved_peak_mb,
    }

    if str(capacity_state or "healthy") in {"inventory_incomplete", "reconciliation_required"}:
        return GpuAdmissionDecision(
            granted=False, rejected=False, reason="reconciliation_required",
            active_vram_mb=active_vram, resident_vram_mb=resident_vram,
            available_vram_mb=effective_available, recovered_lease_ids=recovered,
            incremental_vram_mb=requested_vram, resident_hit=resident_hit, **details,
        )
    configured_capacity = _configured_capacity_mb(config)
    if configured_capacity > 0 and requested_vram > configured_capacity:
        return GpuAdmissionDecision(
            granted=False, rejected=True, reason="unschedulable",
            active_vram_mb=active_vram, resident_vram_mb=resident_vram,
            available_vram_mb=effective_available, recovered_lease_ids=recovered,
            incremental_vram_mb=requested_vram, resident_hit=resident_hit, **details,
        )

    if requested_vram > effective_available:
        if active:
            return GpuAdmissionDecision(
                granted=False, rejected=False, reason="vram_busy",
                active_vram_mb=active_vram, resident_vram_mb=resident_vram,
                available_vram_mb=effective_available, recovered_lease_ids=recovered,
                incremental_vram_mb=requested_vram, resident_hit=resident_hit, **details,
            )
        eviction_candidates = (
            select_gpu_eviction_candidates(
                profile,
                resident_models=resident_items,
                active_leases=active,
                # The head waiter is protected by the drain barrier. Lower-priority
                # waiters must not keep an otherwise idle model resident.
                waiting_leases=list(waiting_leases or ())[:1],
                required_vram_mb=requested_vram,
                available_vram_mb=effective_available,
                max_candidates=config.eviction_max_models if config.eviction_enabled else 0,
            )
            if config.eviction_enabled
            else []
        )
        return GpuAdmissionDecision(
            granted=False,
            rejected=True,
            reason="vram_budget_exceeded",
            active_vram_mb=active_vram,
            resident_vram_mb=resident_vram,
            available_vram_mb=effective_available,
            recovered_lease_ids=recovered,
            incremental_vram_mb=requested_vram,
            resident_hit=resident_hit,
            eviction_candidates=eviction_candidates,
            **details,
        )
    if any(lease.exclusive for lease in active) or (profile.exclusive and active):
        return GpuAdmissionDecision(
            granted=False,
            rejected=False,
            reason="exclusive_conflict",
            active_vram_mb=active_vram,
            resident_vram_mb=resident_vram,
            available_vram_mb=effective_available,
            recovered_lease_ids=recovered,
            incremental_vram_mb=requested_vram,
            resident_hit=resident_hit,
            **details,
        )
    if active and not profile.exclusive:
        active_groups = {lease.share_group for lease in active if lease.share_group}
        if profile.share_group and active_groups and active_groups != {profile.share_group}:
            return GpuAdmissionDecision(
                granted=False,
                rejected=False,
                reason="share_group_conflict",
                active_vram_mb=active_vram,
                resident_vram_mb=resident_vram,
                available_vram_mb=effective_available,
                recovered_lease_ids=recovered,
                incremental_vram_mb=requested_vram,
                resident_hit=resident_hit,
                **details,
            )
    if requested_vram > effective_available:
        return GpuAdmissionDecision(
            granted=False,
            rejected=False,
            reason="vram_busy",
            active_vram_mb=active_vram,
            resident_vram_mb=resident_vram,
            available_vram_mb=effective_available,
            recovered_lease_ids=recovered,
            incremental_vram_mb=requested_vram,
            resident_hit=resident_hit,
            **details,
        )
    return GpuAdmissionDecision(
        granted=True,
        rejected=False,
        reason="granted",
        active_vram_mb=active_vram,
        resident_vram_mb=resident_vram,
        available_vram_mb=effective_available,
        recovered_lease_ids=recovered,
        incremental_vram_mb=requested_vram,
        resident_hit=resident_hit,
        **details,
    )


def select_gpu_eviction_candidates(
    profile: GpuTaskProfile,
    *,
    resident_models: Iterable[GpuModelResidency],
    active_leases: Iterable[GpuLeaseRecord],
    waiting_leases: Iterable[GpuLeaseRecord] | None = None,
    required_vram_mb: int,
    available_vram_mb: int,
    max_candidates: int | None = None,
) -> list[GpuEvictionCandidate]:
    deficit = max(0, int(required_vram_mb or 0) - int(available_vram_mb or 0))
    if deficit <= 0:
        return []
    if max_candidates is not None and int(max_candidates) <= 0:
        return []
    protected: set[tuple[str, str]] = set()
    if profile.model_id:
        protected.add((profile.task_type, profile.model_id))
    for lease in list(active_leases or ()) + list(waiting_leases or ()):
        if lease.status not in {"running", "waiting"} or not lease.model_id:
            continue
        protected.add((lease.task_type, lease.model_id))

    idle: list[GpuEvictionCandidate] = []
    for residency in resident_models:
        if not residency.resident or not residency.model_id:
            continue
        key = (residency.task_type, residency.model_id)
        if key in protected:
            continue
        component = _resident_component(residency)
        if not _runtime_owner_supports_fenced_unload(component):
            continue
        idle.append(
            GpuEvictionCandidate(
                task_type=residency.task_type,
                model_id=residency.model_id,
                estimated_vram_mb=max(0, int(residency.estimated_vram_mb or 0)),
                component=component,
                last_used_at=residency.last_used_at,
                metadata=dict(residency.metadata or {}),
            )
        )

    idle.sort(key=lambda item: (float(item.last_used_at or 0.0), item.task_type, item.model_id))
    selected: list[GpuEvictionCandidate] = []
    freed = 0
    limit = int(max_candidates) if max_candidates is not None else None
    for candidate in idle:
        if limit is not None and len(selected) >= limit:
            break
        selected.append(candidate)
        freed += candidate.estimated_vram_mb
        if freed >= deficit:
            break
    return selected


class InProcessGpuScheduler(BaseGpuScheduler):
    def __init__(
        self,
        config: GpuSchedulerConfig | None = None,
        *,
        reconciliation_provider: Callable[[], Any] | None = None,
    ) -> None:
        self.config = config or GpuSchedulerConfig()
        self._reconciliation_provider = reconciliation_provider or _build_runtime_reconciliation_provider(self.config)
        self._last_reconciliation: Any | None = None
        self._last_reconciliation_monotonic: float | None = None
        self._condition = threading.Condition()
        self._leases: dict[str, GpuLeaseRecord] = {}
        self._resident_models: dict[tuple[str, str], GpuModelResidency] = {}
        self._vram_samples: dict[tuple[str, str, str], list[tuple[int, int]]] = {}
        self._sample_skipped_reasons: dict[str, int] = {}

    def acquire(self, profile: GpuTaskProfile, *, yield_wait: Callable[[], bool] | None = None) -> GpuLease:
        if not self.config.enabled:
            return GpuLease(self, self._make_record(profile, status="running", granted=True))
        self._reconcile_runtime_residency()
        lease_id = uuid4().hex
        deadline = time.monotonic() + _profile_timeout(profile, self.config)
        with self._condition:
            record = self._reattach_waiting_locked(profile)
            if record is None:
                record = self._make_record(profile, lease_id=lease_id, status="waiting", granted=False)
                self._leases[lease_id] = record
            else:
                lease_id = record.id
            while True:
                now = time.time()
                self._recover_stale_locked(now)
                waiters = self._waiting_locked()
                if yield_wait is not None and not record.caller_attached and profile.priority_class == "background":
                    raise GpuLeaseDeferred(
                        f"GPU scheduler deferred {profile.task_type} while a higher-priority request waits",
                        capacity_state=self._admission_capacity_state(),
                        admission_id=lease_id,
                        eviction_id=record.eviction_id,
                        retry_after_seconds=1.0,
                    )
                if yield_wait is not None and bool(yield_wait()):
                    detached = _replace_record(record, caller_attached=False, wait_reason="yielded_to_higher_priority")
                    self._leases[lease_id] = detached
                    self._condition.notify_all()
                    raise GpuLeaseDeferred(
                        f"GPU scheduler deferred {profile.task_type} while a higher-priority request waits",
                        capacity_state=self._admission_capacity_state(),
                        admission_id=lease_id,
                        eviction_id=detached.eviction_id,
                        retry_after_seconds=1.0,
                    )
                if waiters and waiters[0].id != lease_id:
                    decision = None
                else:
                    decision = plan_gpu_admission(
                        profile,
                        active_leases=self._running_locked(),
                        waiting_leases=waiters,
                        resident_models=self._resident_models.values(),
                        config=self.config,
                        live_free_vram_mb=None,
                        calibration=self._resolve_vram_calibration(profile),
                        capacity_state=self._admission_capacity_state(),
                        observation_id=_reconciliation_observation_id(self._last_reconciliation),
                        now=now,
                    )
                    for recovered_id in decision.recovered_lease_ids:
                        self._recover_locked(recovered_id, now=now)
                    if decision.rejected:
                        self._leases[lease_id] = _replace_record(record, status="rejected", released_at=now)
                        self._condition.notify_all()
                        raise GpuLeaseRejected(
                            decision.reason,
                            capacity_state="unschedulable" if decision.reason == "unschedulable" else decision.capacity_state,
                        )
                    if decision.granted:
                        expires_at = now + _profile_ttl(profile, self.config)
                        granted_record = _replace_record(
                            record,
                            status="running",
                            estimated_vram_mb=decision.reserved_peak_mb,
                            granted_at=now,
                            heartbeat_at=now,
                            expires_at=expires_at,
                            metadata={
                                **dict(record.metadata or {}),
                                "calibration_source": decision.calibration_source,
                                "load_delta_mb": decision.load_delta_mb,
                                "working_set_mb": decision.working_set_mb,
                                "reserved_peak_mb": decision.reserved_peak_mb,
                            },
                        )
                        self._leases[lease_id] = granted_record
                        self._condition.notify_all()
                        return GpuLease(self, granted_record)
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    timed_out = _replace_record(record, status="timed_out", released_at=time.time())
                    self._leases[lease_id] = timed_out
                    self._condition.notify_all()
                    raise GpuLeaseTimeout(
                        f"GPU scheduler timed out waiting for {profile.task_type}",
                        retry_after_seconds=_retry_after_seconds(decision.reason if decision else "queue_wait"),
                    )
                self._condition.wait(timeout=min(0.1, remaining))

    def release(self, lease_id: str) -> None:
        with self._condition:
            record = self._leases.get(lease_id)
            if record is None:
                return
            if record.status == "running":
                self._leases[lease_id] = _replace_record(record, status="released", released_at=time.time())
            self._condition.notify_all()

    def heartbeat(self, lease_id: str) -> None:
        with self._condition:
            record = self._leases.get(lease_id)
            if record is None or record.status != "running":
                return
            now = time.time()
            self._leases[lease_id] = _replace_record(
                record,
                heartbeat_at=now,
                expires_at=now + self.config.lease_ttl_seconds,
            )

    def record_model_residency(self, residency: GpuModelResidency) -> None:
        with self._condition:
            key = (residency.task_type, residency.model_id)
            if residency.resident:
                self._resident_models[key] = residency
            else:
                self._resident_models.pop(key, None)
            self._condition.notify_all()
        _record_model_residency_activity(residency)

    def reset_component_residency(self, component: str) -> None:
        target = str(component or "").strip()
        if not target:
            return
        with self._condition:
            for key, residency in list(self._resident_models.items()):
                if _resident_component(residency) == target:
                    self._resident_models.pop(key, None)
            self._condition.notify_all()

    def status(self) -> dict[str, Any]:
        self._reconcile_runtime_residency()
        with self._condition:
            self._recover_stale_locked(time.time())
            records = list(self._leases.values())
            counts = _status_counts(records)
            return {
                "enabled": self.config.enabled,
                "mode": "in_process",
                "budget": _budget_payload(self.config),
                "counts": counts,
                "running": [_record_payload(record) for record in records if record.status == "running"],
                "waiting": [_record_payload(record) for record in self._waiting_locked()],
                "recent": [_record_payload(record) for record in records if record.status not in {"running", "waiting"}][-20:],
                "model_residency": [_residency_payload(item) for item in self._resident_models.values()],
                "runtime_reconciliation": _reconciliation_payload(
                    self._last_reconciliation, records=records,
                    retry_after_seconds=self.config.reconciliation_retry_seconds,
                ),
                "live_gpu_memory": live_gpu_memory(),
                "timeouts": counts.get("timed_out", 0),
                "rejections": counts.get("rejected", 0),
                "evictions": _empty_eviction_status(),
                "vram_samples": {"skipped": dict(self._sample_skipped_reasons)},
                "preemption": _runtime_preemption_payload(),
            }

    def _make_record(
        self,
        profile: GpuTaskProfile,
        *,
        lease_id: str | None = None,
        status: str,
        granted: bool,
    ) -> GpuLeaseRecord:
        now = time.time()
        expires_at = None
        if granted:
            expires_at = now + _profile_ttl(profile, self.config)
        elif status == "waiting":
            expires_at = now + _profile_timeout(profile, self.config)
        return GpuLeaseRecord(
            id=lease_id or uuid4().hex,
            task_type=profile.task_type,
            model_id=profile.model_id,
            status=status,
            estimated_vram_mb=max(0, int(profile.estimated_vram_mb or 0)),
            exclusive=bool(profile.exclusive),
            share_group=profile.share_group,
            priority=int(profile.priority or 0),
            component=profile.component,
            request_id=profile.request_id,
            created_at=now,
            granted_at=now if granted else None,
            heartbeat_at=now if granted else None,
            expires_at=expires_at,
            released_at=None,
            metadata=dict(profile.metadata or {}),
            priority_class=profile.priority_class,
            admission_key=profile.admission_key,
            shape_bucket=profile.shape_bucket or shape_bucket_for_profile(profile),
        )

    def _running_locked(self) -> list[GpuLeaseRecord]:
        return [record for record in self._leases.values() if record.status == "running"]

    def _waiting_locked(self) -> list[GpuLeaseRecord]:
        return sorted(
            [record for record in self._leases.values() if record.status == "waiting" and record.caller_attached],
            key=lambda record: (-record.priority, record.created_at, record.id),
        )

    def _reattach_waiting_locked(self, profile: GpuTaskProfile) -> GpuLeaseRecord | None:
        if not profile.admission_key:
            return None
        for record in self._leases.values():
            if record.status == "waiting" and record.admission_key == profile.admission_key:
                updated = _replace_record(record, caller_attached=True, wait_reason="")
                self._leases[record.id] = updated
                self._condition.notify_all()
                return updated
        return None

    def _recover_stale_locked(self, now: float) -> None:
        for record in list(self._leases.values()):
            if record.status == "running" and _lease_is_stale(record, now=now):
                self._recover_locked(record.id, now=now)
            elif record.status == "waiting" and _lease_is_stale(record, now=now):
                self._timeout_waiting_locked(record.id, now=now)

    def _recover_locked(self, lease_id: str, *, now: float) -> None:
        record = self._leases.get(lease_id)
        if record is not None and record.status == "running":
            self._leases[lease_id] = _replace_record(record, status="recovered", released_at=now)

    def _timeout_waiting_locked(self, lease_id: str, *, now: float) -> None:
        record = self._leases.get(lease_id)
        if record is not None and record.status == "waiting":
            self._leases[lease_id] = _replace_record(record, status="timed_out", released_at=now)

    def _reconcile_runtime_residency(self) -> None:
        now = time.monotonic()
        cooldown = max(0.0, float(self.config.reconciliation_retry_seconds or 0.0))
        if self._last_reconciliation_monotonic is not None and now - self._last_reconciliation_monotonic < cooldown:
            return
        if self._reconciliation_provider is not None:
            try:
                self._last_reconciliation = self._reconciliation_provider()
            except Exception as exc:
                self._last_reconciliation = _failed_reconciliation_observation(exc)
            self._last_reconciliation_monotonic = now
            # Observation mode deliberately retains existing admission and residency behaviour.
            return

    def _admission_capacity_state(self) -> str:
        if str(self.config.runtime_reconciliation_mode or "").lower() != "enforcement":
            return "healthy"
        return str(getattr(self._last_reconciliation, "state", "healthy") or "healthy")

    def _resolve_vram_calibration(self, profile: GpuTaskProfile) -> GpuVramCalibration | None:
        key = (profile.task_type, profile.model_id, shape_bucket_for_profile(profile))
        samples = self._vram_samples.get(key, ())
        if len(samples) < 5:
            return None
        return GpuVramCalibration(
            load_delta_mb=max(sample[0] for sample in samples),
            working_set_mb=max(sample[1] for sample in samples),
            sample_count=len(samples), source="measured",
        )

    def record_vram_sample(
        self,
        profile: GpuTaskProfile,
        *,
        pre_load_reserved_mb: int | None,
        post_load_reserved_mb: int | None,
        execution_peak_reserved_mb: int | None,
        allocator_capability: str,
        tracker_overlapped: bool,
        sample_skipped_reason: str = "",
    ) -> None:
        if str(allocator_capability or "") != "measured":
            self._increment_sample_skip(sample_skipped_reason or "allocator_unmeasured")
            return
        if tracker_overlapped:
            self._increment_sample_skip(sample_skipped_reason or "tracker_overlap")
            return
        try:
            pre = max(0, int(pre_load_reserved_mb))
            post = max(0, int(post_load_reserved_mb))
            peak = max(post, int(execution_peak_reserved_mb))
        except (TypeError, ValueError):
            self._increment_sample_skip(sample_skipped_reason or "allocator_values_missing")
            return
        key = (profile.task_type, profile.model_id, shape_bucket_for_profile(profile))
        with self._condition:
            samples = self._vram_samples.setdefault(key, [])
            samples.append((max(0, post - pre), max(0, peak - post)))
            del samples[:-32]

    def _increment_sample_skip(self, reason: str) -> None:
        key = str(reason or "unknown")[:80]
        with self._condition:
            self._sample_skipped_reasons[key] = min(1_000_000, self._sample_skipped_reasons.get(key, 0) + 1)
        return


class DisabledGpuScheduler(BaseGpuScheduler):
    def __init__(self, config: GpuSchedulerConfig | None = None) -> None:
        self.config = config or GpuSchedulerConfig(enabled=False)
        self._leases = InProcessGpuScheduler(GpuSchedulerConfig(enabled=False))

    def acquire(self, profile: GpuTaskProfile, *, yield_wait: Callable[[], bool] | None = None) -> GpuLease:
        return self._leases.acquire(profile, yield_wait=yield_wait)

    def release(self, lease_id: str) -> None:
        self._leases.release(lease_id)

    def heartbeat(self, lease_id: str) -> None:
        self._leases.heartbeat(lease_id)

    def reset_component_residency(self, component: str) -> None:
        self._leases.reset_component_residency(component)

    def status(self) -> dict[str, Any]:
        payload = self._leases.status()
        payload["enabled"] = False
        payload["mode"] = "disabled"
        return payload


class PostgresGpuScheduler(BaseGpuScheduler):
    def __init__(
        self,
        config: GpuSchedulerConfig | None = None,
        *,
        database_url: str | None = None,
        reconciliation_provider: Callable[[], Any] | None = None,
    ) -> None:
        self.config = config or GpuSchedulerConfig(mode="postgres")
        self.database_url = database_url
        self._reconciliation_provider = reconciliation_provider or _build_runtime_reconciliation_provider(
            self.config, database_url=database_url
        )
        self._last_reconciliation: Any | None = None
        self._last_reconciliation_monotonic: float | None = None
        self._sample_skipped_reasons: dict[str, int] = {}

    def acquire(self, profile: GpuTaskProfile, *, yield_wait: Callable[[], bool] | None = None) -> GpuLease:
        if not self.config.enabled:
            return DisabledGpuScheduler(self.config).acquire(profile, yield_wait=yield_wait)
        lease_id = uuid4().hex
        timeout = _profile_timeout(profile, self.config)
        deadline = time.monotonic() + timeout
        attached_lease_id = self._insert_waiting(profile, lease_id)
        if attached_lease_id:
            lease_id = str(attached_lease_id)
        last_reason = "queue_wait"
        allocator_trim_attempted = False
        while True:
            if yield_wait is not None and bool(yield_wait()):
                self._detach_waiting(lease_id, reason="yielded_to_higher_priority")
                raise GpuLeaseDeferred(
                    f"GPU scheduler deferred {profile.task_type} while a higher-priority request waits",
                    capacity_state=self._admission_capacity_state(),
                    admission_id=lease_id,
                    retry_after_seconds=1.0,
                )
            decision_record = self._try_grant(profile, lease_id)
            if isinstance(decision_record, GpuLeaseRecord):
                return GpuLease(self, decision_record)
            if isinstance(decision_record, GpuAdmissionDecision):
                last_reason = decision_record.reason
                if self._should_trim_requested_model_allocator(profile, decision_record):
                    if allocator_trim_attempted:
                        self._mark_terminal(lease_id, "rejected")
                        raise GpuLeaseRejected(
                            "vram_budget_exceeded_after_allocator_trim",
                            capacity_state=decision_record.capacity_state,
                        )
                    trim_result = self._trim_requested_model_allocator(profile)
                    self._record_allocator_trim_outcome(lease_id, trim_result)
                    last_reason = str(trim_result.get("reason") or "allocator_trim_failed")
                    if bool(trim_result.get("trimmed")):
                        allocator_trim_attempted = True
                        # Reconcile and re-run admission against fresh driver and
                        # allocator observations before declaring capacity lost.
                        continue
                    if bool(trim_result.get("retryable")):
                        # A runtime operation or control lock is still live.
                        # Keep the lease queued; no model is cancelled or evicted.
                        decision_record = last_reason
                    else:
                        self._mark_terminal(lease_id, "rejected")
                        raise GpuLeaseRejected(last_reason, capacity_state=decision_record.capacity_state)
                else:
                    queued = self._enqueue_eviction_requests(profile, lease_id, list(decision_record.eviction_candidates))
                    if int(queued.get("queued") or queued.get("deduped") or 0) > 0:
                        eviction_id = str((queued.get("eviction_ids") or [""])[0] or "")
                        if eviction_id:
                            self._mark_waiting_eviction(lease_id, eviction_id=eviction_id)
                        if profile.priority_class == "background" and profile.admission_key:
                            self._detach_waiting(lease_id, reason="waiting_eviction")
                            raise GpuLeaseDeferred(
                                f"GPU scheduler deferred {profile.task_type} pending eviction",
                                capacity_state=decision_record.capacity_state,
                                admission_id=lease_id,
                                eviction_id=eviction_id,
                                retry_after_seconds=_retry_after_seconds(last_reason),
                            )
                        # Interactive callers retain their attached admission while
                        # the brokered eviction is verified, then retry normally.
                        if profile.priority_class == "interactive":
                            pass
                        else:
                            self._mark_terminal(lease_id, "timed_out")
                            raise GpuLeaseTimeout(
                                f"GPU scheduler queued eviction before retrying {profile.task_type}",
                                retry_after_seconds=_retry_after_seconds(last_reason),
                            )
                    if int(queued.get("queued") or queued.get("deduped") or 0) <= 0:
                        self._mark_terminal(lease_id, "rejected")
                        raise GpuLeaseRejected(
                            decision_record.reason,
                            capacity_state="unschedulable" if decision_record.reason == "unschedulable" else decision_record.capacity_state,
                        )
            if decision_record == "rejected":
                raise GpuLeaseRejected("vram_budget_exceeded")
            if isinstance(decision_record, str):
                last_reason = decision_record
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                if profile.priority_class == "background" and profile.admission_key:
                    self._detach_waiting(lease_id, reason=last_reason)
                    raise GpuLeaseDeferred(
                        f"GPU scheduler deferred {profile.task_type}",
                        capacity_state=self._admission_capacity_state(),
                        admission_id=lease_id,
                        retry_after_seconds=_retry_after_seconds(last_reason),
                    )
                self._mark_terminal(lease_id, "timed_out")
                raise GpuLeaseTimeout(
                    f"GPU scheduler timed out waiting for {profile.task_type}",
                    retry_after_seconds=_retry_after_seconds(last_reason),
                )
            time.sleep(min(0.25, remaining))

    def release(self, lease_id: str) -> None:
        self._mark_terminal(lease_id, "released")

    def heartbeat(self, lease_id: str) -> None:
        now_sql = "now()"
        self._execute(
            f"""
            UPDATE gpu_leases
               SET heartbeat_at = {now_sql},
                   expires_at = {now_sql} + (%s * interval '1 second')
             WHERE id = %s
               AND status = 'running'
            """,
            (float(self.config.lease_ttl_seconds), lease_id),
        )

    def record_model_residency(self, residency: GpuModelResidency) -> None:
        Jsonb = _jsonb_adapter()
        self._execute(
            """
            INSERT INTO gpu_model_residency (
                model_id, task_type, estimated_vram_mb, resident, last_used_at, metadata
            )
            VALUES (%s, %s, %s, %s, now(), %s)
            ON CONFLICT (model_id, task_type)
            DO UPDATE SET
                estimated_vram_mb = EXCLUDED.estimated_vram_mb,
                resident = EXCLUDED.resident,
                last_used_at = now(),
                metadata = EXCLUDED.metadata
            """,
            (
                residency.model_id,
                residency.task_type,
                int(residency.estimated_vram_mb or 0),
                bool(residency.resident),
                Jsonb(dict(residency.metadata or {})),
            ),
        )
        _record_model_residency_activity(residency)

    def record_vram_sample(
        self,
        profile: GpuTaskProfile,
        *,
        pre_load_reserved_mb: int | None,
        post_load_reserved_mb: int | None,
        execution_peak_reserved_mb: int | None,
        allocator_capability: str,
        tracker_overlapped: bool,
        sample_skipped_reason: str = "",
    ) -> None:
        if str(allocator_capability or "") != "measured" or tracker_overlapped:
            self._increment_sample_skip(sample_skipped_reason or ("tracker_overlap" if tracker_overlapped else "allocator_unmeasured"))
            return
        try:
            pre = max(0, int(pre_load_reserved_mb))
            post = max(0, int(post_load_reserved_mb))
            peak = max(post, int(execution_peak_reserved_mb))
        except (TypeError, ValueError):
            self._increment_sample_skip(sample_skipped_reason or "allocator_values_missing")
            return
        from . import database
        database.record_gpu_vram_sample(
            task_type=profile.task_type, model_id=profile.model_id,
            shape_bucket=shape_bucket_for_profile(profile),
            load_delta_mb=max(0, post - pre), working_set_mb=max(0, peak - post),
            allocator_capability="measured", tracker_overlapped=False,
            url=self.database_url,
        )

    def _increment_sample_skip(self, reason: str) -> None:
        key = str(reason or "unknown")[:80]
        self._sample_skipped_reasons[key] = min(1_000_000, self._sample_skipped_reasons.get(key, 0) + 1)

    def reset_component_residency(self, component: str) -> None:
        target = str(component or "").strip()
        if not target:
            return
        task_types = _task_types_for_component(target)
        self._execute(
            """
            UPDATE gpu_model_residency
               SET resident = false,
                   last_used_at = now(),
                   metadata = COALESCE(metadata, '{}'::jsonb) || jsonb_build_object('startup_cleared_by', %s::text)
             WHERE resident = true
               AND (
                    metadata->>'component' = %s
                 OR metadata->>'owner' = %s
                 OR task_type = ANY(%s::text[])
               )
            """,
            (target, target, target, task_types),
        )

    def status(self) -> dict[str, Any]:
        try:
            self._reconcile_runtime_residency()
            self._recover_stale()
            with self._connection() as conn:
                rows = _fetch_dicts(
                    conn,
                    """
                    SELECT *
                      FROM gpu_leases
                     WHERE created_at >= now() - interval '6 hours'
                     ORDER BY created_at ASC
                    """,
                    (),
                )
                residency_rows = _fetch_dicts(
                    conn,
                    """
                    SELECT *
                      FROM gpu_model_residency
                     ORDER BY last_used_at DESC NULLS LAST
                     LIMIT 50
                    """,
                    (),
                )
                eviction_rows = _fetch_dicts(
                    conn,
                    """
                    SELECT *
                      FROM gpu_evictions
                     WHERE created_at >= now() - interval '6 hours'
                     ORDER BY created_at ASC
                    """,
                    (),
                )
                cas_rejection_rows = _fetch_dicts(
                    conn,
                    """
                    SELECT count(*) AS cas_rejections
                      FROM audit_events
                     WHERE event_type = 'gpu_eviction.cas_rejected'
                       AND created_at >= now() - interval '6 hours'
                    """,
                    (),
                )
            records = [_record_from_row(row) for row in rows]
            counts = _status_counts(records)
            cas_rejections = _status_counter(cas_rejection_rows, "cas_rejections")
            evictions = _eviction_status(eviction_rows, cas_rejections=cas_rejections)
            return {
                "enabled": self.config.enabled,
                "mode": "postgres",
                "budget": _budget_payload(self.config),
                "counts": counts,
                "running": [_record_payload(record) for record in records if record.status == "running"],
                "waiting": [_record_payload(record) for record in records if record.status == "waiting"],
                "recent": [_record_payload(record) for record in records if record.status not in {"running", "waiting"}][-50:],
                "model_residency": [_residency_payload(_residency_from_row(row)) for row in residency_rows],
                "runtime_reconciliation": _reconciliation_payload(
                    self._last_reconciliation, records=records, evictions=evictions,
                    retry_after_seconds=self.config.reconciliation_retry_seconds,
                ),
                "live_gpu_memory": live_gpu_memory(),
                "timeouts": counts.get("timed_out", 0),
                "rejections": counts.get("rejected", 0),
                "evictions": evictions,
                "preemption": _runtime_preemption_payload(),
            }
        except Exception as exc:  # pragma: no cover - deployment-dependent
            return {
                "enabled": self.config.enabled,
                "mode": "postgres",
                "status": "unavailable",
                "error": str(exc),
                "budget": _budget_payload(self.config),
                "running": [],
                "waiting": [],
                "recent": [],
                "model_residency": [],
                "runtime_reconciliation": _unavailable_reconciliation_payload(
                    exc, retry_after_seconds=self.config.reconciliation_retry_seconds,
                ),
                "live_gpu_memory": live_gpu_memory(),
                "timeouts": 0,
                "rejections": 0,
                "evictions": _empty_eviction_status(),
                "preemption": _runtime_preemption_payload(),
            }

    def _insert_waiting(self, profile: GpuTaskProfile, lease_id: str) -> str:
        Jsonb = _jsonb_adapter()
        if profile.admission_key:
            attached = self._execute_fetchone(
                """
                INSERT INTO gpu_leases (
                    id, task_type, model_id, status, estimated_vram_mb, exclusive, share_group,
                    priority, component, request_id, created_at, expires_at, metadata,
                    priority_class, admission_key, shape_bucket, caller_attached
                )
                VALUES (%s, %s, %s, 'waiting', %s, %s, %s, %s, %s, %s, now(), now() + (%s * interval '1 second'), %s, %s, %s, %s, true)
                ON CONFLICT (admission_key)
                    WHERE admission_key <> '' AND status IN ('waiting', 'running')
                DO UPDATE SET
                    caller_attached = true,
                    priority = EXCLUDED.priority,
                    priority_class = EXCLUDED.priority_class,
                    wait_reason = ''
                WHERE gpu_leases.status = 'waiting'
                 RETURNING id::text
                """,
                (
                    lease_id,
                    profile.task_type,
                    profile.model_id,
                    int(profile.estimated_vram_mb or 0),
                    bool(profile.exclusive),
                    profile.share_group,
                    int(profile.priority or 0),
                    profile.component,
                    profile.request_id,
                    float(_profile_timeout(profile, self.config)),
                    Jsonb(dict(profile.metadata or {})),
                    profile.priority_class,
                    profile.admission_key,
                    profile.shape_bucket,
                ),
            )
            if attached:
                return str(attached[0])
            existing = self._execute_fetchone(
                """
                SELECT id::text
                  FROM gpu_leases
                 WHERE admission_key = %s
                   AND status IN ('waiting', 'running')
                 ORDER BY created_at ASC
                 LIMIT 1
                """,
                (profile.admission_key,),
            )
            if existing:
                return str(existing[0])
        self._execute(
            """
            INSERT INTO gpu_leases (
                id, task_type, model_id, status, estimated_vram_mb, exclusive, share_group,
                priority, component, request_id, created_at, expires_at, metadata,
                priority_class, admission_key, shape_bucket, caller_attached
            )
            VALUES (%s, %s, %s, 'waiting', %s, %s, %s, %s, %s, %s, now(), now() + (%s * interval '1 second'), %s, %s, %s, %s, true)
            """,
            (
                lease_id,
                profile.task_type,
                profile.model_id,
                int(profile.estimated_vram_mb or 0),
                bool(profile.exclusive),
                profile.share_group,
                int(profile.priority or 0),
                profile.component,
                profile.request_id,
                float(_profile_timeout(profile, self.config)),
                Jsonb(dict(profile.metadata or {})),
                profile.priority_class,
                profile.admission_key,
                profile.shape_bucket,
            ),
        )
        return lease_id

    def _try_grant(self, profile: GpuTaskProfile, lease_id: str) -> GpuLeaseRecord | GpuAdmissionDecision | str:
        self._recover_stale()
        self._reconcile_runtime_residency()
        with self._connection() as conn:
            with conn.transaction():
                rows = _fetch_dicts(
                    conn,
                    """
                    SELECT *
                      FROM gpu_leases
                     WHERE status IN ('waiting', 'running')
                     ORDER BY
                         CASE status WHEN 'running' THEN 0 ELSE 1 END,
                         priority DESC,
                         created_at ASC
                     FOR UPDATE
                    """,
                    (),
                )
                records = [_record_from_row(row) for row in rows]
                waiting = sorted(
                    [record for record in records if record.status == "waiting" and record.caller_attached],
                    key=lambda record: (-record.priority, record.created_at, record.id),
                )
                if not waiting or waiting[0].id != lease_id:
                    return "queue_wait"
                active = [record for record in records if record.status == "running"]
                residents = [
                    _residency_from_row(row)
                    for row in _fetch_dicts(
                        conn,
                        "SELECT * FROM gpu_model_residency WHERE resident = true",
                        (),
                    )
                ]
                decision = plan_gpu_admission(
                    profile,
                    active_leases=active,
                    waiting_leases=waiting,
                    resident_models=residents,
                    config=self.config,
                    live_free_vram_mb=_live_free_vram_mb(),
                    calibration=self._resolve_vram_calibration(profile),
                    capacity_state=self._admission_capacity_state(),
                    observation_id=_reconciliation_observation_id(self._last_reconciliation),
                )
                for recovered_id in decision.recovered_lease_ids:
                    _execute_cursor(
                        conn,
                        """
                        UPDATE gpu_leases
                           SET status = 'recovered', released_at = now()
                         WHERE id = %s
                           AND status = 'running'
                        """,
                        (recovered_id,),
                    )
                if (
                    decision.rejected
                    and not decision.eviction_candidates
                    and not self._should_trim_requested_model_allocator(profile, decision)
                ):
                    _execute_cursor(
                        conn,
                        "UPDATE gpu_leases SET status = 'rejected', released_at = now() WHERE id = %s",
                        (lease_id,),
                    )
                    return decision
                if not decision.granted:
                    # Rejected decisions carry the authoritative idle-owner
                    # candidates that ``acquire`` must turn into brokered,
                    # runtime-confirmed eviction requests. Returning only the
                    # reason here silently loses those candidates and leaves
                    # the waiter retrying the same capacity state forever.
                    return decision if decision.rejected else decision.reason
                _execute_cursor(
                    conn,
                    """
                    UPDATE gpu_leases
                       SET status = 'running',
                           estimated_vram_mb = %s,
                           granted_at = now(),
                           heartbeat_at = now(),
                           expires_at = now() + (%s * interval '1 second'),
                           metadata = COALESCE(metadata, '{}'::jsonb) || jsonb_build_object(
                               'calibration_source', %s::text,
                               'load_delta_mb', %s::integer,
                               'working_set_mb', %s::integer,
                               'reserved_peak_mb', %s::integer
                           )
                     WHERE id = %s
                     RETURNING *
                    """,
                    (
                        int(decision.reserved_peak_mb), float(_profile_ttl(profile, self.config)),
                        str(decision.calibration_source or "configured"), int(decision.load_delta_mb or 0),
                        int(decision.working_set_mb or 0), int(decision.reserved_peak_mb or 0), lease_id,
                    ),
                )
                granted = _fetch_dicts(conn, "SELECT * FROM gpu_leases WHERE id = %s", (lease_id,))
                return _record_from_row(granted[0])

    def _should_trim_requested_model_allocator(
        self,
        profile: GpuTaskProfile,
        decision: GpuAdmissionDecision,
    ) -> bool:
        """Whether a resident request may safely try a cache trim before rejection."""
        owner = _component_for_task_type(profile.task_type)
        return bool(
            self.config.allocator_trim_enabled
            and decision.rejected
            and decision.reason == "vram_budget_exceeded"
            and decision.resident_hit
            and not decision.eviction_candidates
            and str(profile.task_type or "").strip()
            and str(profile.model_id or "").strip()
            and owner in {"model-runner", "paddle-runner", "asr"}
        )

    def _trim_requested_model_allocator(self, profile: GpuTaskProfile) -> dict[str, Any]:
        """Trim only a fresh, runtime-confirmed idle owner and retain its model."""
        from . import database

        candidate = GpuEvictionCandidate(
            task_type=profile.task_type,
            model_id=profile.model_id,
            estimated_vram_mb=max(0, int(profile.estimated_vram_mb or 0)),
            component=_component_for_task_type(profile.task_type),
        )
        try:
            with database.gpu_control_lock(
                timeout_seconds=self.config.control_lock_timeout_seconds,
                url=self.database_url,
            ) as connection:
                pre = _runtime_eviction_observation(self.config, database, connection=connection)
                target_state, target = _runtime_eviction_target(pre, candidate)
                if target_state != "present" or target is None:
                    return {
                        "trimmed": False,
                        "retryable": target_state == "inventory_incomplete",
                        "reason": f"allocator_trim_{target_state}",
                        "pre_observation_id": str(getattr(pre, "observation_id", "") or ""),
                    }
                in_flight = _required_int(target["model"].get("in_flight"))
                if in_flight is None:
                    return {
                        "trimmed": False,
                        "retryable": True,
                        "reason": "allocator_trim_inventory_incomplete",
                        "pre_observation_id": str(getattr(pre, "observation_id", "") or ""),
                    }
                if in_flight != 0:
                    return {
                        "trimmed": False,
                        "retryable": True,
                        "reason": "allocator_trim_runtime_busy",
                        "pre_observation_id": str(getattr(pre, "observation_id", "") or ""),
                    }
                payload = _runtime_trim(
                    self.config,
                    candidate,
                    target,
                    timeout_seconds=min(
                        float(self.config.eviction_request_timeout_seconds),
                        float(self.config.inventory_timeout_seconds),
                    ),
                )
                if not bool(payload.get("trim_confirmed")) or bool(payload.get("unloaded")):
                    return {
                        "trimmed": False,
                        "retryable": False,
                        "reason": "allocator_trim_unconfirmed",
                        "pre_observation_id": str(getattr(pre, "observation_id", "") or ""),
                    }
                post = _runtime_eviction_observation(self.config, database, connection=connection)
                post_state, post_target = _runtime_eviction_target(post, candidate)
                if post_state != "present" or post_target is None:
                    return {
                        "trimmed": False,
                        "retryable": True,
                        "reason": "allocator_trim_post_inventory_incomplete",
                        "pre_observation_id": str(getattr(pre, "observation_id", "") or ""),
                        "post_observation_id": str(getattr(post, "observation_id", "") or ""),
                    }
                if str(post_target["generation"]) != str(target["generation"]):
                    return {
                        "trimmed": False,
                        "retryable": True,
                        "reason": "allocator_trim_generation_changed",
                        "pre_observation_id": str(getattr(pre, "observation_id", "") or ""),
                        "post_observation_id": str(getattr(post, "observation_id", "") or ""),
                    }
                return {
                    "trimmed": True,
                    "retryable": False,
                    "reason": "allocator_trimmed",
                    "pre_observation_id": str(getattr(pre, "observation_id", "") or ""),
                    "post_observation_id": str(getattr(post, "observation_id", "") or ""),
                    "driver_free_delta_mb": int(getattr(post, "driver_free_mb", 0) or 0) - int(getattr(pre, "driver_free_mb", 0) or 0),
                }
        except Exception as exc:  # pragma: no cover - deployment-dependent transport failures
            return {
                "trimmed": False,
                "retryable": True,
                "reason": "allocator_trim_control_or_transport_busy",
                "error": str(exc)[:240],
            }

    def _record_allocator_trim_outcome(self, lease_id: str, result: dict[str, Any]) -> None:
        """Retain a bounded decision trace on the waiting lease for operators."""
        Jsonb = _jsonb_adapter()
        trace = {
            key: value
            for key, value in dict(result or {}).items()
            if key in {"trimmed", "retryable", "reason", "pre_observation_id", "post_observation_id", "driver_free_delta_mb"}
        }
        self._execute(
            """
            UPDATE gpu_leases
               SET metadata = COALESCE(metadata, '{}'::jsonb) || jsonb_build_object('allocator_trim', %s::jsonb)
             WHERE id = %s
               AND status = 'waiting'
            """,
            (Jsonb(trace), lease_id),
        )

    def _detach_waiting(self, lease_id: str, *, reason: str) -> None:
        self._execute(
            """
            UPDATE gpu_leases
               SET caller_attached = false,
                   wait_reason = %s
             WHERE id = %s
               AND status = 'waiting'
            """,
            (reason, lease_id),
        )

    def _mark_waiting_eviction(self, lease_id: str, *, eviction_id: str) -> None:
        self._execute(
            """
            UPDATE gpu_leases
               SET wait_reason = 'waiting_eviction',
                   linked_eviction_id = NULLIF(%s, '')
             WHERE id = %s
               AND status = 'waiting'
            """,
            (eviction_id, lease_id),
        )

    def _attempt_evictions(
        self,
        profile: GpuTaskProfile,
        lease_id: str,
        candidates: Iterable[GpuEvictionCandidate],
    ) -> bool:
        if not self.config.eviction_enabled:
            return False
        attempted = False
        for candidate in list(candidates)[: max(0, int(self.config.eviction_max_models or 0))]:
            attempted = True
            result = self._evict_candidate_with_retries(profile, candidate)
            metadata = {"request_task_type": profile.task_type, **dict(result.metadata or {})}
            if result.verified:
                if result.payload:
                    metadata["response"] = result.payload
                self._record_eviction(lease_id, candidate, status="succeeded", metadata=metadata)
                self._mark_residency_evicted(candidate)
                return True
            self._record_eviction(
                lease_id,
                candidate,
                status="failed",
                error=result.error,
                metadata=metadata,
            )
        return False if attempted else False

    def _enqueue_eviction_requests(
        self,
        profile: GpuTaskProfile,
        lease_id: str,
        candidates: list[GpuEvictionCandidate],
    ) -> dict[str, Any]:
        if not self.config.eviction_enabled:
            return {"queued": 0, "deduped": 0, "eviction_ids": []}
        selected = list(candidates)[: max(0, int(self.config.eviction_max_models or 0))]
        if not selected:
            return {"queued": 0, "deduped": 0, "eviction_ids": []}
        from . import database

        request_profile = _gpu_task_profile_payload(profile)
        queued = 0
        deduped = 0
        eviction_ids: list[str] = []
        for candidate in selected:
            result = database.enqueue_gpu_eviction_request(
                lease_id=lease_id,
                request_profile=request_profile,
                candidate=_gpu_eviction_candidate_payload(candidate),
                metadata={"source": "gpu_scheduler"},
                runtime_generation=str(candidate.metadata.get("runtime_generation") or ""),
                runtime_activity_sequence=int(candidate.metadata.get("runtime_activity_sequence") or 0),
                request_reason="demand",
                reconciliation_observation_id=str(candidate.metadata.get("reconciliation_observation_id") or ""),
            )
            eviction_id = str(result.get("eviction_id") or result.get("id") or "")
            if eviction_id:
                eviction_ids.append(eviction_id)
            if result.get("deduped"):
                deduped += 1
            else:
                queued += 1
        return {"queued": queued, "deduped": deduped, "eviction_ids": eviction_ids}

    def _evict_candidate_with_retries(
        self,
        profile: GpuTaskProfile,
        candidate: GpuEvictionCandidate,
    ) -> GpuEvictionVerificationResult:
        errors: list[str] = []
        last_result = GpuEvictionVerificationResult(
            verified=False,
            error="eviction was not attempted",
            metadata={"attempts": 0},
        )
        for attempt in range(1, GPU_EVICTION_MAX_ATTEMPTS + 1):
            try:
                result = self._evict_candidate_once(profile, candidate, attempt=attempt)
            except Exception as exc:  # pragma: no cover - network/deployment-specific
                result = GpuEvictionVerificationResult(
                    verified=False,
                    error=str(exc),
                    metadata={"attempt": attempt},
                )
            if result.verified:
                metadata = {
                    **dict(result.metadata or {}),
                    "attempts": attempt,
                }
                if errors:
                    metadata["attempt_errors"] = errors
                return GpuEvictionVerificationResult(
                    verified=True,
                    payload=result.payload,
                    metadata=metadata,
                )
            error = result.error or "eviction verification failed"
            errors.append(error)
            last_result = result
            if attempt < GPU_EVICTION_MAX_ATTEMPTS:
                time.sleep(GPU_EVICTION_RETRY_DELAY_SECONDS)
        return GpuEvictionVerificationResult(
            verified=False,
            payload=last_result.payload,
            error=errors[-1] if errors else last_result.error,
            metadata={
                **dict(last_result.metadata or {}),
                "attempts": GPU_EVICTION_MAX_ATTEMPTS,
                "attempt_errors": errors,
            },
        )

    def _evict_candidate_once(
        self,
        profile: GpuTaskProfile,
        candidate: GpuEvictionCandidate,
        *,
        attempt: int,
    ) -> GpuEvictionVerificationResult:
        before_free_vram_mb = _live_free_vram_mb()
        if before_free_vram_mb is None:
            return GpuEvictionVerificationResult(
                verified=False,
                error="live GPU memory was unavailable before eviction",
                metadata={"attempt": attempt, "before_free_vram_mb": None},
            )
        payload = self._evict_candidate(candidate)
        if str(payload.get("reason") or "") == "unload_capability_unavailable":
            return GpuEvictionVerificationResult(
                verified=False,
                payload=payload,
                error="runtime owner does not expose a fenced unload acknowledgement",
                metadata={"attempt": attempt, "terminal_reason": "unload_capability_unavailable"},
            )
        if (
            _optional_bool(payload.get("unloaded")) is False
            and _optional_bool(payload.get("resident")) is False
        ):
            return GpuEvictionVerificationResult(
                verified=False,
                payload=payload,
                error="model not resident",
                metadata={
                    "attempt": attempt,
                    "before_free_vram_mb": before_free_vram_mb,
                    "terminal_reason": "model_not_resident",
                },
            )
        return self._verify_eviction_vram_recovered(
            profile,
            candidate,
            before_free_vram_mb=before_free_vram_mb,
            payload=payload,
            attempt=attempt,
        )

    def _verify_eviction_vram_recovered(
        self,
        profile: GpuTaskProfile,
        candidate: GpuEvictionCandidate,
        *,
        before_free_vram_mb: int,
        payload: dict[str, Any],
        attempt: int,
    ) -> GpuEvictionVerificationResult:
        timeout = max(0.0, float(self.config.eviction_request_timeout_seconds or 0.0))
        deadline = time.monotonic() + timeout
        last_after_free_vram_mb: int | None = None
        while True:
            after_free_vram_mb = _live_free_vram_mb()
            if after_free_vram_mb is None:
                return GpuEvictionVerificationResult(
                    verified=False,
                    payload=payload,
                    error="live GPU memory was unavailable after eviction",
                    metadata={
                        "attempt": attempt,
                        "before_free_vram_mb": before_free_vram_mb,
                        "after_free_vram_mb": None,
                    },
                )
            last_after_free_vram_mb = after_free_vram_mb
            verification = self._eviction_verification_metadata(
                profile,
                candidate,
                before_free_vram_mb=before_free_vram_mb,
                after_free_vram_mb=after_free_vram_mb,
                attempt=attempt,
            )
            if verification["verified"]:
                return GpuEvictionVerificationResult(
                    verified=True,
                    payload=payload,
                    metadata=verification,
                )
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return GpuEvictionVerificationResult(
                    verified=False,
                    payload=payload,
                    error=(
                        "VRAM did not recover after eviction"
                        f" (before_free_vram_mb={before_free_vram_mb}, "
                        f"after_free_vram_mb={last_after_free_vram_mb}, "
                        f"freed_vram_mb={verification['freed_vram_mb']}, "
                        f"required_vram_mb={verification['required_vram_mb']}, "
                        f"available_vram_mb={verification['available_vram_mb']})"
                    ),
                    metadata=verification,
                )
            time.sleep(min(GPU_EVICTION_VERIFICATION_POLL_INTERVAL_SECONDS, remaining))

    def _eviction_verification_metadata(
        self,
        profile: GpuTaskProfile,
        candidate: GpuEvictionCandidate,
        *,
        before_free_vram_mb: int,
        after_free_vram_mb: int,
        attempt: int,
    ) -> dict[str, Any]:
        required_vram_mb = max(0, int(profile.estimated_vram_mb or 0))
        available_vram_mb = _available_vram_mb(self.config, live_free_vram_mb=after_free_vram_mb)
        freed_vram_mb = max(0, int(after_free_vram_mb) - int(before_free_vram_mb))
        min_freed_vram_mb = _eviction_min_freed_vram_mb(candidate)
        request_fits = required_vram_mb <= available_vram_mb
        freed_enough = freed_vram_mb >= min_freed_vram_mb
        verified = bool(request_fits or freed_enough)
        reason = ""
        if request_fits:
            reason = "request_fits_after_eviction"
        elif freed_enough:
            reason = "freed_vram_threshold_met"
        return {
            "attempt": attempt,
            "verified": verified,
            "verification_reason": reason,
            "before_free_vram_mb": before_free_vram_mb,
            "after_free_vram_mb": after_free_vram_mb,
            "freed_vram_mb": freed_vram_mb,
            "min_freed_vram_mb": min_freed_vram_mb,
            "required_vram_mb": required_vram_mb,
            "available_vram_mb": available_vram_mb,
            "candidate_estimated_vram_mb": max(0, int(candidate.estimated_vram_mb or 0)),
        }

    def _evict_candidate(self, candidate: GpuEvictionCandidate) -> dict[str, Any]:
        component = candidate.component or _component_for_task_type(candidate.task_type)
        if not _runtime_owner_supports_fenced_unload(component):
            return {
                "ok": True,
                "unloaded": False,
                "resident": True,
                "unload_confirmed": False,
                "reason": "unload_capability_unavailable",
            }
        base_url = {
            "model-runner": self.config.model_runner_base_url,
            "paddle-runner": self.config.paddle_runner_base_url,
            "asr": self.config.asr_base_url,
        }.get(component, "")
        if not base_url:
            raise GpuSchedulerError(f"GPU eviction URL is not configured for {component or candidate.task_type}")
        residency = _matching_runtime_residency(
            base_url,
            candidate,
            timeout_seconds=self.config.eviction_request_timeout_seconds,
        )
        if residency is None:
            return {
                "ok": True,
                "unloaded": False,
                "resident": False,
                "unload_confirmed": False,
                "reason": "model_not_resident",
            }
        expected_generation = str(residency.get("process_generation") or "").strip()
        expected_activity_sequence = _optional_int(residency.get("activity_sequence"))
        if not expected_generation or expected_activity_sequence is None:
            return {
                "ok": True,
                "unloaded": False,
                "resident": True,
                "unload_confirmed": False,
                "reason": "residency_fence_unavailable",
            }
        payload = {
            "task_type": candidate.task_type,
            "model_id": candidate.model_id,
            "expected_generation": expected_generation,
            "expected_activity_sequence": expected_activity_sequence,
        }
        return _post_json(base_url, "/v1/gpu/unload", payload, timeout_seconds=self.config.eviction_request_timeout_seconds)

    def _record_eviction(
        self,
        lease_id: str,
        candidate: GpuEvictionCandidate,
        *,
        status: str,
        error: str = "",
        metadata: dict[str, Any] | None = None,
    ) -> None:
        Jsonb = _jsonb_adapter()
        self._execute(
            """
            INSERT INTO gpu_evictions (
                lease_id, task_type, model_id, component, status, estimated_freed_vram_mb,
                error, created_at, completed_at, metadata
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, now(), now(), %s)
            """,
            (
                lease_id,
                candidate.task_type,
                candidate.model_id,
                candidate.component,
                status,
                int(candidate.estimated_vram_mb or 0),
                error[:1000],
                Jsonb(dict(metadata or {})),
            ),
        )

    def _mark_residency_evicted(self, candidate: GpuEvictionCandidate) -> None:
        self._execute(
            """
            UPDATE gpu_model_residency
               SET resident = false,
                   last_used_at = now(),
                   metadata = metadata || '{"evicted_by_scheduler": true}'::jsonb
             WHERE task_type = %s
               AND model_id = %s
            """,
            (candidate.task_type, candidate.model_id),
        )

    def _recover_stale(self) -> None:
        self._execute(
            """
            WITH timed_out_waiting AS (
                UPDATE gpu_leases
                   SET status = 'timed_out', released_at = now()
                 WHERE status = 'waiting'
                   AND (
                        expires_at < now()
                     OR (
                        expires_at IS NULL
                        AND created_at < now() - (%s * interval '1 second')
                     )
                   )
                 RETURNING id
            ),
            recovered_running AS (
                UPDATE gpu_leases
                   SET status = 'recovered', released_at = now()
                 WHERE status = 'running'
                   AND (
                        expires_at < now()
                     OR (
                        granted_at IS NOT NULL
                        AND metadata->>'max_active_seconds' ~ '^[0-9]+(\\.[0-9]+)?$'
                        AND granted_at < now() - ((metadata->>'max_active_seconds')::double precision * interval '1 second')
                     )
                   )
                 RETURNING id
            )
            SELECT 1
            """,
            (float(self.config.stale_after_seconds),),
        )

    def _reconcile_runtime_residency(self) -> None:
        now = time.monotonic()
        cooldown = max(0.0, float(self.config.reconciliation_retry_seconds or 0.0))
        if self._last_reconciliation_monotonic is not None and now - self._last_reconciliation_monotonic < cooldown:
            return
        if self._reconciliation_provider is not None:
            try:
                self._last_reconciliation = self._reconciliation_provider()
            except Exception as exc:
                self._last_reconciliation = _failed_reconciliation_observation(exc)
            self._last_reconciliation_monotonic = now
            return
        return

    def _admission_capacity_state(self) -> str:
        if str(self.config.runtime_reconciliation_mode or "").lower() != "enforcement":
            return "healthy"
        return str(getattr(self._last_reconciliation, "state", "healthy") or "healthy")

    def _resolve_vram_calibration(self, profile: GpuTaskProfile) -> GpuVramCalibration | None:
        try:
            from . import database
            calibration = database.resolve_gpu_vram_calibration(
                task_type=profile.task_type, model_id=profile.model_id,
                shape_bucket=shape_bucket_for_profile(profile), url=self.database_url,
            )
            return calibration if calibration.sample_count >= 5 else None
        except Exception:
            return None

    def _runtime_residency_rows(self, component: str) -> list[dict[str, Any]]:
        task_types = _task_types_for_component(component)
        with self._connection() as conn:
            return _fetch_dicts(
                conn,
                """
                SELECT task_type, model_id
                  FROM gpu_model_residency
                 WHERE resident = true
                   AND (
                        metadata->>'component' = %s
                     OR metadata->>'owner' = %s
                     OR task_type = ANY(%s::text[])
                   )
                """,
                (component, component, task_types),
            )

    def _clear_runtime_residency(self, task_type: str, model_id: str, component: str, reason: str) -> None:
        self._execute(
            """
            UPDATE gpu_model_residency
               SET resident = false,
                   last_used_at = now(),
                   metadata = COALESCE(metadata, '{}'::jsonb)
                       || jsonb_build_object('runtime_reconciled_by', %s::text, 'runtime_reconcile_reason', %s::text)
             WHERE resident = true
               AND task_type = %s
               AND model_id = %s
            """,
            (component, reason, task_type, model_id),
        )

    def _mark_terminal(self, lease_id: str, status: str) -> None:
        if status not in {"released", "timed_out", "recovered", "rejected"}:
            raise ValueError(f"invalid terminal lease status: {status}")
        self._execute(
            """
            UPDATE gpu_leases
               SET status = %s,
                   released_at = COALESCE(released_at, now())
             WHERE id = %s
               AND status IN ('waiting', 'running')
            """,
            (status, lease_id),
        )

    def _execute(self, statement: str, params: tuple[Any, ...]) -> None:
        with self._connection() as conn:
            with conn.cursor() as cur:
                cur.execute(statement, params)

    def _execute_fetchone(self, statement: str, params: tuple[Any, ...]) -> Any:
        with self._connection() as conn:
            with conn.cursor() as cur:
                cur.execute(statement, params)
                return cur.fetchone()

    def _connection(self) -> Any:
        from . import database

        psycopg = database._load_psycopg()
        return psycopg.connect(self.database_url or database.database_url())


_SCHEDULER: BaseGpuScheduler | None = None
_SCHEDULER_LOCK = threading.Lock()


def get_gpu_scheduler() -> BaseGpuScheduler:
    global _SCHEDULER
    with _SCHEDULER_LOCK:
        if _SCHEDULER is None:
            _SCHEDULER = create_gpu_scheduler()
        return _SCHEDULER


def reset_gpu_scheduler_for_tests() -> None:
    global _SCHEDULER
    with _SCHEDULER_LOCK:
        _SCHEDULER = None


def create_gpu_scheduler(component: str = "") -> BaseGpuScheduler:
    config = scheduler_config_from_settings()
    if not config.enabled:
        return DisabledGpuScheduler(config)
    mode = str(config.mode or "auto").lower()
    if mode == "postgres" or (mode == "auto" and os.environ.get("FLUX_KB_DATABASE_URL")):
        return PostgresGpuScheduler(config)
    return InProcessGpuScheduler(config)


def process_gpu_eviction_request(
    *,
    eviction_id: str,
    worker_id: str,
    broker_message_id: str | None = None,
    correlation_id: str | None = None,
    causation_id: str | None = None,
    database_module: Any | None = None,
) -> dict[str, Any]:
    from . import database as default_database

    db = database_module or default_database
    request = db.claim_gpu_eviction_request(
        eviction_id=eviction_id,
        worker_id=worker_id,
        broker_message_id=broker_message_id,
    )
    if request is None:
        raise ValueError(f"GPU eviction request not found: {eviction_id}")
    if request.get("cas_rejected"):
        return _gpu_eviction_cas_rejected_result(
            eviction_id, db=db, stage="claim", worker_id=worker_id, broker_message_id=broker_message_id,
        )
    if request.get("status") in {"succeeded", "failed", "skipped", "expired"}:
        return {
            "eviction_id": request.get("eviction_id") or request.get("id"),
            "status": request.get("status"),
            "already_terminal": True,
            "retryable": False,
        }
    claim_token = str(request.get("claim_token") or "")
    row_version = int(request.get("row_version") or 0)
    config = scheduler_config_from_settings()
    profile = _gpu_task_profile_from_eviction_request(request)
    candidate = _gpu_eviction_candidate_from_request(request)
    attempt = max(1, int(request.get("broker_delivery_count") or 1))
    if config.eviction_enabled:
        return _process_runtime_confirmed_eviction(
            db=db,
            config=config,
            request=request,
            profile=profile,
            candidate=candidate,
            eviction_id=eviction_id,
            claim_token=claim_token,
            row_version=row_version,
            attempt=attempt,
            correlation_id=correlation_id,
            causation_id=causation_id or broker_message_id,
            worker_id=worker_id,
            broker_message_id=broker_message_id,
        )
    scheduler = PostgresGpuScheduler(config)
    if not config.eviction_enabled:
        completed = db.complete_gpu_eviction_request(
            eviction_id=eviction_id,
            status="skipped",
            error="GPU eviction is disabled",
            metadata={"attempt": attempt},
            claim_token=claim_token,
            row_version=row_version,
            correlation_id=correlation_id,
            causation_id=causation_id or broker_message_id,
        )
        if _gpu_eviction_cas_rejected(completed):
            return _gpu_eviction_cas_rejected_result(
                eviction_id, db=db, stage="complete_disabled", worker_id=worker_id, broker_message_id=broker_message_id,
            )
        return {
            "eviction_id": eviction_id,
            "status": "skipped",
            "retryable": False,
            "result": completed or {},
        }
    try:
        result = scheduler._evict_candidate_once(profile, candidate, attempt=attempt)
        if result.verified:
            scheduler._mark_residency_evicted(candidate)
    except Exception as exc:  # pragma: no cover - deployment/network specific
        result = GpuEvictionVerificationResult(
            verified=False,
            error=str(exc),
            metadata={"attempt": attempt, "error_type": exc.__class__.__name__},
        )
    metadata = {
        "request_task_type": profile.task_type,
        "request_model_id": profile.model_id,
        "attempt": attempt,
        **dict(result.metadata or {}),
    }
    if result.payload:
        metadata["response"] = result.payload
    if result.verified:
        completed = db.complete_gpu_eviction_request(
            eviction_id=eviction_id,
            status="succeeded",
            metadata=metadata,
            claim_token=claim_token,
            row_version=row_version,
            correlation_id=correlation_id,
            causation_id=causation_id or broker_message_id,
        )
        if _gpu_eviction_cas_rejected(completed):
            return _gpu_eviction_cas_rejected_result(
                eviction_id, db=db, stage="complete", worker_id=worker_id, broker_message_id=broker_message_id,
            )
        return {
            "eviction_id": eviction_id,
            "status": "succeeded",
            "retryable": False,
            "result": completed or {},
        }
    error = result.error or "GPU eviction verification failed"
    terminal_outcome = _terminal_gpu_eviction_outcome(result, attempt=attempt)
    if terminal_outcome is not None:
        terminal_status, terminal_reason = terminal_outcome
        failed = db.complete_gpu_eviction_request(
            eviction_id=eviction_id,
            status=terminal_status,
            error=error,
            metadata={**metadata, "terminal_reason": terminal_reason},
            claim_token=claim_token,
            row_version=row_version,
            correlation_id=correlation_id,
            causation_id=causation_id or broker_message_id,
        )
        if _gpu_eviction_cas_rejected(failed):
            return _gpu_eviction_cas_rejected_result(
                eviction_id, db=db, stage="complete_terminal", worker_id=worker_id, broker_message_id=broker_message_id,
            )
        return {
            "eviction_id": eviction_id,
            "status": terminal_status,
            "retryable": False,
            "result": failed or {},
        }
    if attempt >= _gpu_eviction_delivery_limit():
        failed = db.complete_gpu_eviction_request(
            eviction_id=eviction_id,
            status="failed",
            error=error,
            metadata=metadata,
            claim_token=claim_token,
            row_version=row_version,
            correlation_id=correlation_id,
            causation_id=causation_id or broker_message_id,
        )
        if _gpu_eviction_cas_rejected(failed):
            return _gpu_eviction_cas_rejected_result(
                eviction_id, db=db, stage="complete_terminal", worker_id=worker_id, broker_message_id=broker_message_id,
            )
        return {
            "eviction_id": eviction_id,
            "status": "failed",
            "retryable": False,
            "result": failed or {},
        }
    retry = db.retry_gpu_eviction_request(
        eviction_id=eviction_id,
        error=error,
        metadata=metadata,
        claim_token=claim_token,
        row_version=row_version,
        broker_delivery_count=attempt,
        correlation_id=correlation_id,
        causation_id=causation_id or broker_message_id,
    )
    if _gpu_eviction_cas_rejected(retry):
        return _gpu_eviction_cas_rejected_result(
            eviction_id, db=db, stage="retry", worker_id=worker_id, broker_message_id=broker_message_id,
        )
    return {
        "eviction_id": eviction_id,
        "status": "retrying",
        "retryable": True,
        "result": retry or {},
    }


def run_gpu_idle_unload_maintenance(
    *, worker_id: str, database_module: Any | None = None, stop_event: threading.Event | None = None,
) -> dict[str, Any]:
    """Reconcile and enqueue fenced unloads for runtime-confirmed idle models.

    This CPU-only sweep deliberately never calls a runtime unload endpoint. The
    brokered eviction worker repeats the runtime generation/activity fence under
    its service-operation gate before an unload can occur.
    """
    from . import database as default_database

    config = scheduler_config_from_settings()
    if not config.idle_unload_enabled or config.idle_unload_seconds <= 0:
        return {"status": "disabled", "reason": "idle_unload_disabled"}
    db = database_module or default_database
    with db.gpu_eviction_maintenance_leader_lock() as leader:
        if not leader:
            return {"status": "skipped", "reason": "not_leader"}
        connection = getattr(leader, "connection", None)
        if _maintenance_stop_requested(stop_event) or not _maintenance_leader_valid(leader):
            return _maintenance_stopped_or_lost(stop_event=stop_event)
        expired = db.expire_stale_gpu_eviction_requests(connection=connection)
        if _maintenance_stop_requested(stop_event) or not _maintenance_leader_valid(leader):
            return _maintenance_stopped_or_lost(stop_event=stop_event, expired=len(expired))
        observation = _runtime_eviction_observation(config, db, connection=connection)
        if _maintenance_stop_requested(stop_event) or not _maintenance_leader_valid(leader):
            return _maintenance_stopped_or_lost(stop_event=stop_event, expired=len(expired))
        confirmed = _runtime_confirmed_idle_targets(observation)
        candidates = db.list_idle_gpu_eviction_candidates(
            idle_unload_seconds=float(config.idle_unload_seconds), connection=connection,
        )
        queued = 0
        deduped = 0
        skipped = 0
        observation_id = _reconciliation_observation_id(observation) or ""
        for candidate in candidates:
            if _maintenance_stop_requested(stop_event) or not _maintenance_leader_valid(leader):
                return _maintenance_stopped_or_lost(
                    stop_event=stop_event, expired=len(expired), queued=queued, deduped=deduped, skipped=skipped,
                )
            key = (
                str(candidate.get("task_type") or ""),
                str(candidate.get("model_id") or ""),
                str(candidate.get("component") or ""),
                str(candidate.get("runtime_generation") or ""),
                int(candidate.get("runtime_activity_sequence") or 0),
            )
            if key not in confirmed:
                skipped += 1
                continue
            lease_id = "idle:" + ":".join(key[:4])
            result = db.enqueue_gpu_eviction_request(
                lease_id=lease_id,
                request_profile={"task_type": candidate["task_type"], "model_id": candidate["model_id"], "worker_id": worker_id},
                candidate=candidate,
                metadata={"source": "gpu_idle_unload", "worker_id": worker_id},
                runtime_generation=key[3],
                runtime_activity_sequence=key[4],
                request_reason="idle",
                reconciliation_observation_id=observation_id,
                connection=connection,
            )
            if result.get("deduped"):
                deduped += 1
            else:
                queued += 1
        return {"status": "queued", "expired": len(expired), "queued": queued, "deduped": deduped, "skipped": skipped}


def _maintenance_stop_requested(stop_event: threading.Event | None) -> bool:
    return bool(stop_event is not None and stop_event.is_set())


def _maintenance_leader_valid(leader: Any) -> bool:
    validator = getattr(leader, "is_valid", None)
    return bool(validator()) if callable(validator) else bool(leader)


def _maintenance_stopped_or_lost(
    *, stop_event: threading.Event | None, expired: int = 0, queued: int = 0, deduped: int = 0, skipped: int = 0,
) -> dict[str, Any]:
    if _maintenance_stop_requested(stop_event):
        return {"status": "stopped", "reason": "shutdown", "expired": expired, "queued": queued, "deduped": deduped, "skipped": skipped}
    return {"status": "skipped", "reason": "leader_lost", "expired": expired, "queued": queued, "deduped": deduped, "skipped": skipped}


@dataclass(frozen=True)
class _SchedulerRuntimeInventoryAdapter:
    component: str
    base_url: str
    ollama: bool = False

    def read_inventory(self, timeout_seconds: float) -> Any:
        from .gpu_reconciliation import inventory_from_payload, ollama_inventory_from_payload

        path = "/api/ps" if self.ollama else "/v1/gpu/residency"
        payload = _get_json(self.base_url, path, timeout_seconds=timeout_seconds)
        return ollama_inventory_from_payload(payload, component=self.component) if self.ollama else inventory_from_payload(self.component, payload)


def _runtime_eviction_observation(config: GpuSchedulerConfig, db: Any, *, connection: Any | None = None) -> Any:
    """Take and persist a bounded, fresh inventory without acquiring another control lock."""
    from .gpu_reconciliation import reconcile_runtime_inventory

    adapters = [
        _SchedulerRuntimeInventoryAdapter(component, str(base_url))
        for component, base_url in (
            ("model-runner", config.model_runner_base_url),
            ("paddle-runner", config.paddle_runner_base_url),
            ("asr", config.asr_base_url),
        )
        if str(base_url or "").strip()
    ]
    if str(config.ollama_base_url or "").strip():
        adapters.append(_SchedulerRuntimeInventoryAdapter("ollama", str(config.ollama_base_url), ollama=True))
    persist = getattr(db, "persist_gpu_runtime_observation", None)
    return reconcile_runtime_inventory(
        adapters,
        live_gpu_memory=live_gpu_memory,
        timeout_seconds=config.inventory_timeout_seconds,
        context_allowance_mb=config.context_allowance_mb,
        material_threshold_mb=config.unattributed_threshold_mb,
        material_threshold_percent=config.unattributed_threshold_percent,
        total_memory_mb=config.vram_budget_mb,
        persist=(lambda observation: persist(observation, connection=connection)) if callable(persist) else None,
    )


def _runtime_confirmed_idle_targets(observation: Any) -> set[tuple[str, str, str, str, int]]:
    """Return fresh present targets whose operation gate is currently quiet."""
    confirmed: set[tuple[str, str, str, str, int]] = set()
    for inventory in tuple(getattr(observation, "inventories", ()) or ()):
        if str(getattr(inventory, "state", "unknown")) != "present":
            continue
        inventory_owner = str(getattr(inventory, "owner_component", "") or getattr(inventory, "component", "") or "").strip()
        inventory_generation = str(getattr(inventory, "process_generation", "") or "").strip()
        for model in tuple(getattr(inventory, "models", ()) or ()):
            if not isinstance(model, dict):
                continue
            try:
                in_flight = int(model.get("in_flight"))
                activity_sequence = int(model.get("activity_sequence") or 0)
            except (TypeError, ValueError):
                continue
            task_type = str(model.get("task_type") or "").strip()
            model_id = str(model.get("model_id") or "").strip()
            owner = str(model.get("owner_component") or inventory_owner).strip()
            generation = str(model.get("process_generation") or inventory_generation).strip()
            if task_type and model_id and owner and generation and in_flight == 0 and _runtime_owner_supports_fenced_unload(owner):
                confirmed.add((task_type, model_id, owner, generation, activity_sequence))
    return confirmed


def _runtime_eviction_target(observation: Any, candidate: GpuEvictionCandidate) -> tuple[str, dict[str, Any] | None]:
    """Return one fresh owner-backed target, or an authoritative absence/incomplete result."""
    inventories = tuple(getattr(observation, "inventories", ()) or ())
    matches: list[dict[str, Any]] = []
    for inventory in inventories:
        if str(getattr(inventory, "state", "unknown")) != "present":
            continue
        for model in tuple(getattr(inventory, "models", ()) or ()):
            if not isinstance(model, dict):
                continue
            if str(model.get("task_type") or "") != candidate.task_type or str(model.get("model_id") or "") != candidate.model_id:
                continue
            owner = str(model.get("owner_component") or getattr(inventory, "owner_component", "") or "").strip()
            generation = str(model.get("process_generation") or getattr(inventory, "process_generation", "") or "").strip()
            activity = _optional_int(model.get("activity_sequence"))
            if not owner or not generation or activity is None:
                return "inventory_incomplete", None
            matches.append({"inventory": inventory, "model": model, "owner": owner, "generation": generation, "activity": activity})
    if len(matches) == 1:
        return "present", matches[0]
    if len(matches) > 1:
        return "inventory_incomplete", None
    authoritative_absence = bool(inventories) and all(str(getattr(item, "state", "unknown")) == "present" for item in inventories)
    return ("absent" if authoritative_absence else "inventory_incomplete"), None


def _runtime_owner_base_url(config: GpuSchedulerConfig, owner: str) -> str:
    return {
        "model-runner": config.model_runner_base_url,
        "paddle-runner": config.paddle_runner_base_url,
        "asr": config.asr_base_url,
        "ollama": config.ollama_base_url,
    }.get(owner, "")


def _runtime_owner_supports_fenced_unload(owner: str) -> bool:
    """Whether the owner can prove idle state and fence an unload atomically."""
    return str(owner or "").strip().lower() in {"model-runner", "paddle-runner", "asr"}


def _runtime_identity_metadata(
    *,
    owner_component: str,
    generation: str,
    activity_sequence: int | None,
    fingerprint: str,
) -> dict[str, Any]:
    return {
        "owner_component": str(owner_component or ""),
        "runtime_generation": str(generation or ""),
        "runtime_activity_sequence": activity_sequence,
        "runtime_fingerprint": str(fingerprint or ""),
    }


def _runtime_target_metadata(target: dict[str, Any]) -> dict[str, Any]:
    return _runtime_identity_metadata(
        owner_component=str(target["owner"]),
        generation=str(target["generation"]),
        activity_sequence=_required_int(target["activity"]),
        fingerprint=str(getattr(target["inventory"], "runtime_fingerprint", "") or ""),
    )


def _runtime_owner_inventory_metadata(inventory: Any) -> dict[str, Any] | None:
    if inventory is None or str(getattr(inventory, "state", "unknown")) != "present":
        return None
    owner = str(getattr(inventory, "owner_component", "") or "")
    generation = str(getattr(inventory, "process_generation", "") or "")
    fingerprint = str(getattr(inventory, "runtime_fingerprint", "") or "")
    if not owner or (not generation and not fingerprint):
        return None
    return _runtime_identity_metadata(
        owner_component=owner,
        generation=generation,
        activity_sequence=None,
        fingerprint=fingerprint,
    )


def _runtime_unload(
    config: GpuSchedulerConfig,
    candidate: GpuEvictionCandidate,
    target: dict[str, Any],
) -> dict[str, Any]:
    owner = str(target["owner"])
    base_url = _runtime_owner_base_url(config, owner)
    if not base_url:
        raise GpuSchedulerError(f"GPU eviction URL is not configured for authoritative owner {owner}")
    if owner == "ollama":
        raise GpuSchedulerError("Ollama does not expose a fenced unload acknowledgement")
    return _post_json(
        base_url,
        "/v1/gpu/unload",
        {
            "task_type": candidate.task_type,
            "model_id": candidate.model_id,
            "expected_generation": target["generation"],
            "expected_activity_sequence": target["activity"],
        },
        timeout_seconds=config.eviction_request_timeout_seconds,
    )


def _runtime_trim(
    config: GpuSchedulerConfig,
    candidate: GpuEvictionCandidate,
    target: dict[str, Any],
    *,
    timeout_seconds: float | None = None,
) -> dict[str, Any]:
    """Ask the authoritative owner to release allocator cache, not its model."""
    owner = str(target["owner"])
    if owner == "ollama":
        raise GpuSchedulerError("Ollama does not expose a fenced allocator-trim endpoint")
    base_url = _runtime_owner_base_url(config, owner)
    if not base_url:
        raise GpuSchedulerError(f"GPU trim URL is not configured for authoritative owner {owner}")
    return _post_json(
        base_url,
        "/v1/gpu/trim",
        {
            "task_type": candidate.task_type,
            "model_id": candidate.model_id,
            "expected_generation": target["generation"],
            "expected_activity_sequence": target["activity"],
        },
        timeout_seconds=max(0.1, float(timeout_seconds if timeout_seconds is not None else config.eviction_request_timeout_seconds)),
    )


def _runtime_evict_result(
    *,
    db: Any,
    config: GpuSchedulerConfig,
    request: dict[str, Any],
    candidate: GpuEvictionCandidate,
    attempt: int,
    claim_token: str,
    row_version: int,
) -> tuple[GpuEvictionVerificationResult, str, int]:
    """Run the entire fresh-inventory/unload/post-inventory protocol under the caller's session lock."""
    pre = _runtime_eviction_observation(config, db)
    target_state, target = _runtime_eviction_target(pre, candidate)
    base_metadata = {"attempt": attempt, "pre_observation_id": getattr(pre, "observation_id", "")}
    if target_state == "absent":
        return GpuEvictionVerificationResult(False, error="target already absent", metadata={**base_metadata, "terminal_reason": "target_already_absent"}), claim_token, row_version
    if target_state != "present" or target is None:
        return GpuEvictionVerificationResult(False, error="fresh runtime inventory is incomplete", metadata={**base_metadata, "terminal_reason": "inventory_incomplete"}), claim_token, row_version
    target_metadata = _runtime_target_metadata(target)
    if not _runtime_owner_supports_fenced_unload(str(target["owner"])):
        return GpuEvictionVerificationResult(
            False,
            error="runtime owner does not expose a fenced unload acknowledgement",
            metadata={**base_metadata, **target_metadata, "terminal_reason": "unload_capability_unavailable"},
        ), claim_token, row_version
    expected_generation = str(request.get("runtime_generation") or "").strip()
    expected_activity = _optional_int(request.get("runtime_activity_sequence"))
    if expected_generation and expected_generation != target["generation"]:
        return GpuEvictionVerificationResult(False, error="runtime generation changed", metadata={**base_metadata, **target_metadata, "terminal_reason": "generation_changed"}), claim_token, row_version
    if expected_activity is not None and expected_activity != target["activity"]:
        return GpuEvictionVerificationResult(False, error="runtime activity changed", metadata={**base_metadata, **target_metadata, "terminal_reason": "became_active"}), claim_token, row_version
    in_flight = _required_int(target["model"].get("in_flight"))
    if in_flight is None:
        return GpuEvictionVerificationResult(False, error="runtime in-flight state is incomplete", metadata={**base_metadata, **target_metadata, "terminal_reason": "inventory_incomplete"}), claim_token, row_version
    if in_flight != 0:
        return GpuEvictionVerificationResult(False, error="runtime model is active", metadata={**base_metadata, **target_metadata, "terminal_reason": "became_active"}), claim_token, row_version
    active_leases = getattr(db, "list_active_gpu_leases", lambda: [])()
    target_active = any(str(row.get("task_type") or "") == candidate.task_type and str(row.get("model_id") or "") == candidate.model_id for row in active_leases if isinstance(row, dict))
    if target_active:
        return GpuEvictionVerificationResult(False, error="target has an active GPU lease", metadata={**base_metadata, **target_metadata, "terminal_reason": "became_active"}), claim_token, row_version
    unmeasured = str(getattr(target["inventory"], "allocator_capability", "unknown")) != "measured"
    if unmeasured and active_leases:
        return GpuEvictionVerificationResult(False, error="other GPU leases have not reached a quiet window", metadata={**base_metadata, **target_metadata, "terminal_reason": "verification_deferred"}), claim_token, row_version
    heartbeat = getattr(db, "heartbeat_gpu_eviction_request", None)
    if callable(heartbeat):
        refreshed = heartbeat(eviction_id=str(request.get("eviction_id") or request.get("id") or ""), claim_token=claim_token, row_version=row_version)
        if isinstance(refreshed, dict) and refreshed.get("cas_rejected"):
            return GpuEvictionVerificationResult(False, error="claim fence rejected", metadata={**base_metadata, "cas_rejected": True}), claim_token, row_version
        claim_token = str(refreshed.get("claim_token") or claim_token)
        row_version = int(refreshed.get("row_version") or row_version)
    try:
        payload = _runtime_unload(config, candidate, target)
    except Exception as exc:
        return GpuEvictionVerificationResult(False, error=str(exc), metadata={**base_metadata, **target_metadata, "terminal_reason": "unload_failed"}), claim_token, row_version
    post = _runtime_eviction_observation(config, db)
    post_state, post_target = _runtime_eviction_target(post, candidate)
    metadata = {**base_metadata, **target_metadata, "post_observation_id": getattr(post, "observation_id", ""), "driver_used_delta_mb": int(getattr(pre, "driver_used_mb", 0)) - int(getattr(post, "driver_used_mb", 0))}
    post_owner_inventory = next(
        (item for item in tuple(getattr(post, "inventories", ()) or ()) if str(getattr(item, "owner_component", "")) == target["owner"]),
        None,
    )
    post_metadata = _runtime_target_metadata(post_target) if post_target is not None else _runtime_owner_inventory_metadata(post_owner_inventory)
    if post_metadata is None:
        metadata.update(_runtime_identity_metadata(owner_component="", generation="", activity_sequence=None, fingerprint=""))
    else:
        metadata.update(post_metadata)
    if post_state == "present" or post_target is not None:
        return GpuEvictionVerificationResult(False, payload=payload, error="target remains present after unload", metadata={**metadata, "terminal_reason": "unload_failed"}), claim_token, row_version
    if post_state != "absent" or str(getattr(post, "driver_observation_state", "")) != "available":
        return GpuEvictionVerificationResult(False, payload=payload, error="post-unload inventory is incomplete", metadata={**metadata, "terminal_reason": "verification_deferred"}), claim_token, row_version
    post_generation = str(getattr(post_owner_inventory, "process_generation", "") or "")
    if target["owner"] == "ollama":
        if not str(getattr(post_owner_inventory, "runtime_fingerprint", "") or "") or str(getattr(post_owner_inventory, "runtime_fingerprint", "")) == target_metadata["runtime_fingerprint"]:
            return GpuEvictionVerificationResult(False, payload=payload, error="Ollama inventory fingerprint was not refreshed", metadata={**metadata, "terminal_reason": "verification_deferred"}), claim_token, row_version
    elif post_generation != target["generation"]:
        return GpuEvictionVerificationResult(False, payload=payload, error="runtime generation changed during unload", metadata={**metadata, "terminal_reason": "generation_changed"}), claim_token, row_version
    unload_confirmed = _optional_bool(payload.get("unload_confirmed")) is True
    if target["owner"] == "ollama":
        unload_confirmed = unload_confirmed or _optional_bool(payload.get("done")) is True
    if not unload_confirmed:
        return GpuEvictionVerificationResult(False, payload=payload, error="runtime did not confirm unload", metadata={**metadata, "terminal_reason": "unload_failed"}), claim_token, row_version
    if not unmeasured:
        allocator_drop = max(0, int(getattr(target["inventory"], "known_measured_mb", 0)) - int(next((item.known_measured_mb for item in post.inventories if item.owner_component == target["owner"]), 0)))
        metadata["allocator_reserved_drop_mb"] = allocator_drop
        if allocator_drop >= _eviction_min_freed_vram_mb(candidate):
            return GpuEvictionVerificationResult(True, payload=payload, metadata={**metadata, "terminal_reason": "verified_unload"}), claim_token, row_version
        return GpuEvictionVerificationResult(False, payload=payload, error="allocator memory release could not be attributed", metadata={**metadata, "terminal_reason": "memory_release_unverified", "capacity_state": "reconciliation_required"}), claim_token, row_version
    time.sleep(GPU_EVICTION_VERIFICATION_POLL_INTERVAL_SECONDS)
    quiet = _runtime_eviction_observation(config, db)
    quiet_active_leases = getattr(db, "list_active_gpu_leases", lambda: [])()
    if quiet_active_leases:
        return GpuEvictionVerificationResult(False, payload=payload, error="a GPU lease started during the quiet window", metadata={**metadata, "terminal_reason": "verification_deferred"}), claim_token, row_version
    driver_drop = int(getattr(pre, "driver_used_mb", 0)) - int(getattr(quiet, "driver_used_mb", 0))
    stable = abs(int(getattr(post, "driver_used_mb", 0)) - int(getattr(quiet, "driver_used_mb", 0))) <= GPU_EVICTION_MIN_FREED_MB
    metadata.update({"quiet_window_driver_drop_mb": driver_drop, "quiet_window_stable": stable})
    if stable and driver_drop >= _eviction_min_freed_vram_mb(candidate):
        return GpuEvictionVerificationResult(True, payload=payload, metadata={**metadata, "terminal_reason": "verified_unload"}), claim_token, row_version
    return GpuEvictionVerificationResult(False, payload=payload, error="quiet-window release verification deferred", metadata={**metadata, "terminal_reason": "verification_deferred"}), claim_token, row_version


def _process_runtime_confirmed_eviction(
    *, db: Any, config: GpuSchedulerConfig, request: dict[str, Any], profile: GpuTaskProfile,
    candidate: GpuEvictionCandidate, eviction_id: str, claim_token: str, row_version: int, attempt: int,
    correlation_id: str | None, causation_id: str | None, worker_id: str, broker_message_id: str | None,
) -> dict[str, Any]:
    lock = getattr(db, "gpu_control_lock")
    with lock(timeout_seconds=config.control_lock_timeout_seconds):
        result, claim_token, row_version = _runtime_evict_result(db=db, config=config, request=request, candidate=candidate, attempt=attempt, claim_token=claim_token, row_version=row_version)
        metadata = {"request_task_type": profile.task_type, "request_model_id": profile.model_id, **dict(result.metadata or {})}
        if result.payload:
            metadata["response"] = result.payload
        if metadata.get("cas_rejected"):
            return _gpu_eviction_cas_rejected_result(
                eviction_id, db=db, stage="runtime_fence", worker_id=worker_id, broker_message_id=broker_message_id,
            )
        reason = str(metadata.get("terminal_reason") or "")
        if result.verified:
            completed = db.complete_gpu_eviction_request(eviction_id=eviction_id, status="succeeded", metadata=metadata, claim_token=claim_token, row_version=row_version, correlation_id=correlation_id, causation_id=causation_id)
            return _gpu_eviction_cas_rejected_result(eviction_id, db=db, stage="complete", worker_id=worker_id, broker_message_id=broker_message_id) if _gpu_eviction_cas_rejected(completed) else {"eviction_id": eviction_id, "status": "succeeded", "retryable": False, "result": completed or {}}
        terminal = _terminal_gpu_eviction_outcome(result, attempt=attempt)
        if terminal is not None:
            status, terminal_reason = terminal
            if (
                str(request.get("request_reason") or "").lower() == "idle"
                and terminal_reason == "became_active"
            ):
                status = "skipped"
            residency_verification = None
            if reason in {"generation_changed", "became_active", "unload_failed", "memory_release_unverified"}:
                residency_verification = {
                    "task_type": candidate.task_type,
                    "model_id": candidate.model_id,
                    "runtime_state": "memory_release_unverified" if reason == "memory_release_unverified" else "unload_failed",
                    "failure_reason": result.error,
                    "observation_id": str(metadata.get("post_observation_id") or metadata.get("pre_observation_id") or ""),
                    "owner_component": str(metadata.get("owner_component") or ""),
                    "runtime_generation": str(metadata.get("runtime_generation") or ""),
                    "runtime_activity_sequence": _required_int(metadata.get("runtime_activity_sequence")),
                    "runtime_fingerprint": str(metadata.get("runtime_fingerprint") or ""),
                    "replace_runtime_identity": bool(metadata.get("post_observation_id")),
                    "capacity_state": str(metadata.get("capacity_state") or ""),
                }
            completed = db.complete_gpu_eviction_request(eviction_id=eviction_id, status=status, error=result.error, metadata={**metadata, "terminal_reason": terminal_reason}, claim_token=claim_token, row_version=row_version, correlation_id=correlation_id, causation_id=causation_id, residency_verification=residency_verification)
            return _gpu_eviction_cas_rejected_result(eviction_id, db=db, stage="complete_terminal", worker_id=worker_id, broker_message_id=broker_message_id) if _gpu_eviction_cas_rejected(completed) else {"eviction_id": eviction_id, "status": status, "retryable": False, "result": completed or {}}
        retry = db.retry_gpu_eviction_request(eviction_id=eviction_id, error=result.error or "GPU eviction verification deferred", metadata=metadata, claim_token=claim_token, row_version=row_version, broker_delivery_count=attempt, correlation_id=correlation_id, causation_id=causation_id)
        return _gpu_eviction_cas_rejected_result(eviction_id, db=db, stage="retry", worker_id=worker_id, broker_message_id=broker_message_id) if _gpu_eviction_cas_rejected(retry) else {"eviction_id": eviction_id, "status": "retrying", "retryable": True, "result": retry or {}}


def scheduler_config_from_settings() -> GpuSchedulerConfig:
    values = _scheduler_setting_values()
    mode = str(values["gpu.scheduler.mode"] or "auto")
    return GpuSchedulerConfig(
        enabled=bool(values["gpu.scheduler.enabled"]) and mode != "disabled",
        mode=mode,
        total_vram_mb=int(values["gpu.scheduler.total_vram_mb"] or 0),
        vram_budget_mb=int(values["gpu.scheduler.vram_budget_mb"] or 10_240),
        safety_margin_mb=int(values["gpu.scheduler.safety_margin_mb"] or 1_024),
        default_timeout_seconds=float(values["gpu.scheduler.default_timeout_seconds"] or 30),
        background_timeout_seconds=float(values["gpu.scheduler.background_timeout_seconds"] or 1),
        lease_ttl_seconds=float(values["gpu.scheduler.lease_ttl_seconds"] or 120),
        heartbeat_interval_seconds=float(values["gpu.scheduler.heartbeat_interval_seconds"] or 10),
        stale_after_seconds=float(values["gpu.scheduler.stale_after_seconds"] or 180),
        eviction_enabled=bool(values["gpu.scheduler.eviction_enabled"]),
        eviction_request_timeout_seconds=float(values["gpu.scheduler.eviction_request_timeout_seconds"] or 10),
        eviction_max_models=int(values["gpu.scheduler.eviction_max_models"] or 4),
        allocator_trim_enabled=bool(values.get("gpu.scheduler.allocator_trim_enabled", True)),
        idle_unload_enabled=bool(values.get("gpu.scheduler.idle_unload_enabled")),
        idle_unload_seconds=float(values.get("gpu.scheduler.idle_unload_seconds") or 0),
        idle_sweep_interval_seconds=float(values.get("gpu.scheduler.idle_sweep_interval_seconds") or 30),
        model_runner_base_url=str(values.get("model_runner.base_url") or os.environ.get("FLUX_KB_MODEL_RUNNER_BASE_URL") or ""),
        paddle_runner_base_url=str(values.get("model_runner.paddle_runner_base_url") or os.environ.get("FLUX_KB_PADDLE_RUNNER_BASE_URL") or ""),
        asr_base_url=str(values.get("acceleration.asr.base_url") or os.environ.get("FLUX_KB_ASR_BASE_URL") or ""),
        ollama_base_url=str(values.get("acceleration.local_inference.base_url") or os.environ.get("FLUX_KB_LOCAL_INFERENCE_BASE_URL") or ""),
        runtime_reconciliation_mode=str(values.get("gpu.scheduler.runtime_reconciliation_mode") or "observation"),
        inventory_timeout_seconds=float(values.get("gpu.scheduler.inventory_timeout_seconds") or 2),
        control_lock_timeout_seconds=float(values.get("gpu.scheduler.control_lock_timeout_seconds") or 2),
        context_allowance_mb=int(values.get("gpu.scheduler.context_allowance_mb") or 256),
        unattributed_threshold_mb=int(values.get("gpu.scheduler.unattributed_threshold_mb") or 512),
        unattributed_threshold_percent=int(values.get("gpu.scheduler.unattributed_threshold_percent") or 5),
        reconciliation_retry_seconds=float(values.get("gpu.scheduler.reconciliation_retry_seconds") or 15),
    )


def _gpu_priority_lane(task_type: str, priority_class: str) -> tuple[str, int]:
    """Map trusted request class and work type to the user-visible GPU order."""
    normalized = str(task_type or "").strip().lower()
    if str(priority_class or "").strip().lower() == "interactive" and normalized in {"embedding", "rerank"}:
        # Interactive retrieval shares the MCP lane. Direct OCR, vision and
        # ASR requests retain their task-family lanes; only MCP may ever gain a
        # separately verified running-work cancellation capability.
        return "mcp_retrieval", 500
    if normalized in {"embedding", "rerank"}:
        return "document_indexing", 400
    if normalized in {"ocr_image", "ocr_document"}:
        return "image_ocr", 300
    if normalized == "ollama_vision":
        return "image_llm_enrichment", 200
    if normalized in {"asr", "video_extraction", "video"}:
        return "video_extraction", 100
    return "background", 0


def task_profile(
    task_type: str,
    *,
    model_id: str = "",
    component: str = "",
    request_id: str = "",
    priority: int = 0,
    priority_class: str = "background",
    timeout_seconds: float | None = None,
    metadata: dict[str, Any] | None = None,
    exclusive: bool | None = None,
    share_group: str | None = None,
) -> GpuTaskProfile:
    values = _scheduler_setting_values()
    normalized = str(task_type or "unknown")
    estimate_key = {
        "embedding": "gpu.scheduler.embedding_vram_mb",
        "rerank": "gpu.scheduler.rerank_vram_mb",
        "ocr_image": "gpu.scheduler.ocr_image_vram_mb",
        "ocr_document": "gpu.scheduler.ocr_document_vram_mb",
        "asr": "gpu.scheduler.asr_vram_mb",
        "ollama_vision": "gpu.scheduler.ollama_vision_vram_mb",
    }.get(normalized)
    estimate = int(values.get(estimate_key or "", values["gpu.scheduler.vram_budget_mb"]) or 0)
    if exclusive is None:
        exclusive = normalized != "embedding"
    if share_group is None:
        share_group = normalized if not exclusive else ""
    effective_timeout_seconds = timeout_seconds
    if effective_timeout_seconds is None and str(component or "") == "worker":
        effective_timeout_seconds = float(values.get("gpu.scheduler.background_timeout_seconds") or 1)
    canonical_class = "interactive" if str(priority_class or "").strip().lower() == "interactive" else "background"
    priority_lane, lane_priority = _gpu_priority_lane(normalized, canonical_class)
    # Task-family lanes are fixed policy.  Never allow a caller-provided value
    # to promote OCR/vision/ASR or unknown work above its assigned lane.
    del priority
    resolved_priority = lane_priority
    profile_metadata = dict(metadata or {})
    profile_metadata.setdefault("priority_lane", priority_lane)
    provisional = GpuTaskProfile(
        task_type=normalized,
        model_id=model_id,
        estimated_vram_mb=estimate,
        priority=resolved_priority,
        timeout_seconds=effective_timeout_seconds,
        lease_ttl_seconds=float(values["gpu.scheduler.lease_ttl_seconds"] or 120),
        exclusive=bool(exclusive),
        share_group=str(share_group or ""),
        component=component,
        request_id=request_id,
        priority_class=canonical_class,
        metadata=profile_metadata,
    )
    bucket = shape_bucket_for_profile(provisional)
    stage = str(profile_metadata.get("stage") or normalized).strip()[:40] or normalized
    admission_key = ":".join((str(request_id or "").strip(), stage, normalized, str(model_id or "").strip(), bucket))
    return GpuTaskProfile(**{**provisional.__dict__, "shape_bucket": bucket, "admission_key": admission_key})


def _gpu_task_profile_payload(profile: GpuTaskProfile) -> dict[str, Any]:
    return {
        "task_type": profile.task_type,
        "model_id": profile.model_id,
        "estimated_vram_mb": max(0, int(profile.estimated_vram_mb or 0)),
        "priority": int(profile.priority or 0),
        "timeout_seconds": profile.timeout_seconds,
        "lease_ttl_seconds": profile.lease_ttl_seconds,
        "exclusive": bool(profile.exclusive),
        "share_group": profile.share_group,
        "component": profile.component,
        "request_id": profile.request_id,
        "priority_class": profile.priority_class,
        "admission_key": profile.admission_key,
        "shape_bucket": profile.shape_bucket,
        "metadata": dict(profile.metadata or {}),
    }


def _gpu_eviction_candidate_payload(candidate: GpuEvictionCandidate) -> dict[str, Any]:
    return {
        "task_type": candidate.task_type,
        "model_id": candidate.model_id,
        "estimated_vram_mb": max(0, int(candidate.estimated_vram_mb or 0)),
        "component": candidate.component,
        "last_used_at": candidate.last_used_at,
        "metadata": dict(candidate.metadata or {}),
        "runtime_generation": str(candidate.metadata.get("runtime_generation") or ""),
        "runtime_activity_sequence": max(0, int(candidate.metadata.get("runtime_activity_sequence") or 0)),
        "reconciliation_observation_id": str(candidate.metadata.get("reconciliation_observation_id") or ""),
    }


def _gpu_task_profile_from_eviction_request(request: dict[str, Any]) -> GpuTaskProfile:
    metadata = request.get("metadata") if isinstance(request.get("metadata"), dict) else {}
    raw_profile = metadata.get("request_profile") if isinstance(metadata.get("request_profile"), dict) else {}
    priority_class = "interactive" if str(raw_profile.get("priority_class") or "").lower() == "interactive" else "background"
    _lane, lane_priority = _gpu_priority_lane(str(raw_profile.get("task_type") or "unknown"), priority_class)
    return GpuTaskProfile(
        task_type=str(raw_profile.get("task_type") or "unknown"),
        model_id=str(raw_profile.get("model_id") or ""),
        estimated_vram_mb=max(0, int(raw_profile.get("estimated_vram_mb") or 0)),
        priority=lane_priority,
        timeout_seconds=_optional_float(raw_profile.get("timeout_seconds")),
        lease_ttl_seconds=_optional_float(raw_profile.get("lease_ttl_seconds")),
        exclusive=bool(raw_profile.get("exclusive", True)),
        share_group=str(raw_profile.get("share_group") or ""),
        component=str(raw_profile.get("component") or ""),
        request_id=str(raw_profile.get("request_id") or ""),
        priority_class=priority_class,
        admission_key=str(raw_profile.get("admission_key") or ""),
        shape_bucket=str(raw_profile.get("shape_bucket") or ""),
        metadata=dict(raw_profile.get("metadata") if isinstance(raw_profile.get("metadata"), dict) else {}),
    )


def _gpu_eviction_candidate_from_request(request: dict[str, Any]) -> GpuEvictionCandidate:
    metadata = request.get("metadata") if isinstance(request.get("metadata"), dict) else {}
    raw_candidate = metadata.get("candidate") if isinstance(metadata.get("candidate"), dict) else {}
    return GpuEvictionCandidate(
        task_type=str(request.get("task_type") or raw_candidate.get("task_type") or ""),
        model_id=str(request.get("model_id") or raw_candidate.get("model_id") or ""),
        estimated_vram_mb=max(0, int(request.get("estimated_freed_vram_mb") or raw_candidate.get("estimated_vram_mb") or 0)),
        component=str(request.get("component") or raw_candidate.get("component") or ""),
        last_used_at=_optional_float(raw_candidate.get("last_used_at")),
        metadata=dict(raw_candidate.get("metadata") if isinstance(raw_candidate.get("metadata"), dict) else {}),
    )


def _gpu_eviction_delivery_limit() -> int:
    try:
        from . import messaging

        return max(1, int(messaging.RabbitMqConfig.from_env().delivery_limit or 1))
    except Exception:
        return 8


def _gpu_eviction_cas_rejected(result: Any) -> bool:
    return isinstance(result, dict) and bool(result.get("cas_rejected"))


def _gpu_eviction_cas_rejected_result(
    eviction_id: str,
    *,
    db: Any | None = None,
    stage: str = "unknown",
    worker_id: str = "",
    broker_message_id: str | None = None,
) -> dict[str, Any]:
    _record_gpu_eviction_cas_rejection(
        db, eviction_id=eviction_id, stage=stage, worker_id=worker_id, broker_message_id=broker_message_id,
    )
    return {"eviction_id": eviction_id, "status": "cas_rejected", "already_terminal": True, "retryable": False}


def _record_gpu_eviction_cas_rejection(
    db: Any | None,
    *,
    eviction_id: str,
    stage: str,
    worker_id: str,
    broker_message_id: str | None,
) -> None:
    recorder = getattr(db, "record_gpu_eviction_cas_rejection", None)
    if not callable(recorder):
        return
    try:
        recorder(
            eviction_id=str(eviction_id),
            stage=str(stage),
            worker_id=str(worker_id or ""),
            broker_message_id=str(broker_message_id or ""),
        )
    except Exception:
        # A diagnostic write must never turn an idempotent broker acknowledgement into a retry.
        return


def _terminal_gpu_eviction_outcome(
    result: GpuEvictionVerificationResult,
    *,
    attempt: int,
) -> tuple[str, str] | None:
    payload = result.payload if isinstance(result.payload, dict) else {}
    metadata = result.metadata if isinstance(result.metadata, dict) else {}
    error = str(result.error or "").lower()
    unloaded = _optional_bool(payload.get("unloaded"))
    resident = _optional_bool(payload.get("resident"))
    freed_vram_mb = _optional_int(metadata.get("freed_vram_mb"))
    after_retry = max(1, int(attempt or 1)) > 1

    explicit_reason = str(metadata.get("terminal_reason") or "")
    if explicit_reason == "target_already_absent":
        return ("skipped", "target_already_absent")
    if explicit_reason == "unload_capability_unavailable":
        return ("skipped", "unload_capability_unavailable")
    if explicit_reason in {"generation_changed", "became_active", "unload_failed", "memory_release_unverified"}:
        return ("failed", explicit_reason)
    if explicit_reason in {"verification_deferred", "inventory_incomplete"}:
        return None

    if resident is False or "model not resident" in error or "not resident" in error or "not loaded" in error:
        return ("skipped", "model_not_resident")
    if after_retry and freed_vram_mb is not None and freed_vram_mb <= 0 and "vram did not recover" in error:
        return ("failed", "eviction_did_not_free_vram")
    if after_retry and unloaded is False:
        return ("failed", "eviction_unload_declined")
    return None


def _optional_bool(value: Any) -> bool | None:
    if isinstance(value, bool):
        return value
    if isinstance(value, str):
        normalized = value.strip().lower()
        if normalized in {"true", "yes", "1"}:
            return True
        if normalized in {"false", "no", "0"}:
            return False
    return None


def _optional_int(value: Any) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def _required_int(value: Any) -> int | None:
    """Parse an explicitly supplied integer without treating absent evidence as zero."""
    if value is None or isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    if isinstance(value, str):
        text = value.strip()
        if not text:
            return None
        try:
            return int(text)
        except ValueError:
            return None
    return None


def _optional_float(value: Any) -> float | None:
    if value is None or value == "":
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _build_runtime_reconciliation_provider(
    config: GpuSchedulerConfig,
    *,
    database_url: str | None = None,
) -> Callable[[], Any] | None:
    """Create the observation-only provider when any runtime endpoint is configured."""
    if str(config.runtime_reconciliation_mode or "observation").lower() == "disabled":
        return None
    from .gpu_reconciliation import HttpRuntimeInventoryAdapter, OllamaInventoryAdapter, reconcile_runtime_inventory

    adapters: list[Any] = []
    for component, base_url in (
        ("model-runner", config.model_runner_base_url),
        ("paddle-runner", config.paddle_runner_base_url),
        ("asr", config.asr_base_url),
    ):
        if str(base_url or "").strip():
            adapters.append(HttpRuntimeInventoryAdapter(component, str(base_url)))
    if str(config.ollama_base_url or "").strip():
        adapters.append(OllamaInventoryAdapter(str(config.ollama_base_url)))
    if not adapters:
        return None

    def provider() -> Any:
        kwargs = {
            "live_gpu_memory": live_gpu_memory,
            "timeout_seconds": config.inventory_timeout_seconds,
            "context_allowance_mb": config.context_allowance_mb,
            "material_threshold_mb": config.unattributed_threshold_mb,
            "material_threshold_percent": config.unattributed_threshold_percent,
            "total_memory_mb": config.vram_budget_mb,
        }
        if str(config.mode or "").lower() != "postgres":
            return reconcile_runtime_inventory(adapters, **kwargs)
        from . import database

        with database.gpu_control_lock(timeout_seconds=config.control_lock_timeout_seconds, url=database_url):
            return reconcile_runtime_inventory(
                adapters,
                persist=lambda observation: database.persist_gpu_runtime_observation(observation, url=database_url),
                **kwargs,
            )

    return provider


def live_gpu_memory() -> dict[str, Any]:
    try:
        from .processes import run_no_window

        result = run_no_window(
            [
                "nvidia-smi",
                "--query-gpu=memory.total,memory.used,memory.free",
                "--format=csv,noheader,nounits",
            ],
            text=True,
            capture_output=True,
            timeout=3,
            check=False,
        )
    except FileNotFoundError:
        return {"ok": False, "state": "missing", "gpus": []}
    except Exception as exc:  # pragma: no cover - host-specific
        return {"ok": False, "state": "unavailable", "error": str(exc), "gpus": []}
    if result.returncode != 0:
        return {
            "ok": False,
            "state": "unavailable",
            "error": (result.stderr or result.stdout or "").strip()[:500],
            "gpus": [],
        }
    gpus: list[dict[str, int]] = []
    for line in result.stdout.splitlines():
        parts = [part.strip() for part in line.split(",")]
        if len(parts) < 3:
            continue
        try:
            total, used, free = int(parts[0]), int(parts[1]), int(parts[2])
        except ValueError:
            continue
        gpus.append({"memory_total_mb": total, "memory_used_mb": used, "memory_free_mb": free})
    return {"ok": bool(gpus), "state": "available" if gpus else "unavailable", "gpus": gpus}


def _live_free_vram_mb() -> int | None:
    payload = live_gpu_memory()
    gpus = payload.get("gpus") if isinstance(payload, dict) else None
    if not isinstance(gpus, list) or not gpus:
        return None
    values = [int(gpu.get("memory_free_mb") or 0) for gpu in gpus if isinstance(gpu, dict)]
    return max(values) if values else None


def _scheduler_setting_values() -> dict[str, Any]:
    keys = {
        "gpu.scheduler.enabled": True,
        "gpu.scheduler.mode": "auto",
        "gpu.scheduler.total_vram_mb": 0,
        "gpu.scheduler.vram_budget_mb": 10_240,
        "gpu.scheduler.safety_margin_mb": 1_024,
        "gpu.scheduler.default_timeout_seconds": 30,
        "gpu.scheduler.background_timeout_seconds": 1,
        "gpu.scheduler.lease_ttl_seconds": 120,
        "gpu.scheduler.heartbeat_interval_seconds": 10,
        "gpu.scheduler.stale_after_seconds": 180,
        "gpu.scheduler.eviction_enabled": True,
        "gpu.scheduler.eviction_request_timeout_seconds": 10,
        "gpu.scheduler.eviction_max_models": 4,
        "gpu.scheduler.allocator_trim_enabled": True,
        "gpu.scheduler.idle_unload_enabled": True,
        "gpu.scheduler.idle_unload_seconds": 120,
        "gpu.scheduler.idle_sweep_interval_seconds": 30,
        "gpu.scheduler.embedding_vram_mb": 2_500,
        "gpu.scheduler.rerank_vram_mb": 7_000,
        "gpu.scheduler.ocr_image_vram_mb": 2_000,
        "gpu.scheduler.ocr_document_vram_mb": 8_000,
        "gpu.scheduler.asr_vram_mb": 6_000,
        "gpu.scheduler.ollama_vision_vram_mb": 9_000,
        "model_runner.base_url": os.environ.get("FLUX_KB_MODEL_RUNNER_BASE_URL", ""),
        "model_runner.paddle_runner_base_url": os.environ.get("FLUX_KB_PADDLE_RUNNER_BASE_URL", ""),
        "acceleration.asr.base_url": os.environ.get("FLUX_KB_ASR_BASE_URL", ""),
        "acceleration.local_inference.base_url": os.environ.get("FLUX_KB_LOCAL_INFERENCE_BASE_URL", ""),
        "gpu.scheduler.runtime_reconciliation_mode": "observation",
        "gpu.scheduler.inventory_timeout_seconds": 2,
        "gpu.scheduler.control_lock_timeout_seconds": 2,
        "gpu.scheduler.context_allowance_mb": 256,
        "gpu.scheduler.unattributed_threshold_mb": 512,
        "gpu.scheduler.unattributed_threshold_percent": 5,
        "gpu.scheduler.reconciliation_retry_seconds": 15,
    }
    values = dict(keys)
    try:
        from .settings import SettingsService

        service = SettingsService()
        for key in keys:
            values[key] = service.resolve(key).raw_value
    except Exception:
        pass
    return values


def _available_vram_mb(config: GpuSchedulerConfig, *, live_free_vram_mb: int | None) -> int:
    configured = _configured_capacity_mb(config)
    if live_free_vram_mb is None:
        return configured
    live_available = max(0, int(live_free_vram_mb) - int(config.safety_margin_mb or 0))
    if configured <= 0:
        return live_available
    return min(configured, live_available)


def _configured_capacity_mb(config: GpuSchedulerConfig) -> int:
    return max(0, _configured_total_capacity_mb(config) - int(config.safety_margin_mb or 0))


def _configured_total_capacity_mb(config: GpuSchedulerConfig) -> int:
    values = [int(value) for value in (config.vram_budget_mb, config.total_vram_mb) if int(value or 0) > 0]
    return min(values) if values else 0


def _profile_timeout(profile: GpuTaskProfile, config: GpuSchedulerConfig) -> float:
    return max(0.0, float(profile.timeout_seconds if profile.timeout_seconds is not None else config.default_timeout_seconds))


def _profile_ttl(profile: GpuTaskProfile, config: GpuSchedulerConfig) -> float:
    return max(1.0, float(profile.lease_ttl_seconds if profile.lease_ttl_seconds is not None else config.lease_ttl_seconds))


def _lease_is_stale(record: GpuLeaseRecord, *, now: float) -> bool:
    if record.expires_at is not None and float(record.expires_at) < now:
        return True
    max_active_seconds = _metadata_positive_float(record.metadata, "max_active_seconds")
    if max_active_seconds is not None and record.granted_at is not None:
        return float(record.granted_at) + max_active_seconds < now
    if record.heartbeat_at is None:
        return False
    return False


def _metadata_positive_float(metadata: dict[str, Any] | None, key: str) -> float | None:
    value = dict(metadata or {}).get(key)
    try:
        numeric = float(value)
    except (TypeError, ValueError):
        return None
    if numeric <= 0:
        return None
    return numeric


def _replace_record(record: GpuLeaseRecord, **changes: Any) -> GpuLeaseRecord:
    data = record.__dict__.copy()
    data.update(changes)
    return GpuLeaseRecord(**data)


def _status_counts(records: Iterable[GpuLeaseRecord]) -> dict[str, int]:
    counts = {status: 0 for status in sorted(GPU_LEASE_STATUSES)}
    for record in records:
        counts[record.status] = counts.get(record.status, 0) + 1
    return counts


def _budget_payload(config: GpuSchedulerConfig) -> dict[str, Any]:
    return {
        "total_vram_mb": config.total_vram_mb,
        "vram_budget_mb": config.vram_budget_mb,
        "safety_margin_mb": config.safety_margin_mb,
        "available_vram_mb": _available_vram_mb(config, live_free_vram_mb=_live_free_vram_mb()),
        "default_timeout_seconds": config.default_timeout_seconds,
        "lease_ttl_seconds": config.lease_ttl_seconds,
        "heartbeat_interval_seconds": config.heartbeat_interval_seconds,
        "stale_after_seconds": config.stale_after_seconds,
        "eviction_enabled": config.eviction_enabled,
        "eviction_request_timeout_seconds": config.eviction_request_timeout_seconds,
        "eviction_max_models": config.eviction_max_models,
        "allocator_trim_enabled": config.allocator_trim_enabled,
        "idle_unload_enabled": config.idle_unload_enabled,
        "idle_unload_seconds": config.idle_unload_seconds,
    }


def _runtime_preemption_payload() -> dict[str, Any]:
    """Surface the verified no-force-cancellation policy across GPU owners."""
    from .gpu_runtime import runtime_preemption_policy

    policies = (
        runtime_preemption_policy("model-runner", ("embedding", "rerank")),
        runtime_preemption_policy("paddle-runner", ("ocr_image", "ocr_document")),
        runtime_preemption_policy("asr", ("asr",)),
        runtime_preemption_policy("ollama", ("ollama_vision",)),
        runtime_preemption_policy("worker", ("video_extraction",)),
    )
    return {
        "mcp_only": True,
        "cancellation_request": "unavailable",
        "fallback": "priority_at_safe_boundary",
        "tasks": [task for policy in policies for task in policy["tasks"]],
    }


def _record_payload(record: GpuLeaseRecord) -> dict[str, Any]:
    return {
        "id": record.id,
        "task_type": record.task_type,
        "model_id": record.model_id,
        "status": record.status,
        "estimated_vram_mb": record.estimated_vram_mb,
        "exclusive": record.exclusive,
        "share_group": record.share_group,
        "priority": record.priority,
        "component": record.component,
        "request_id": record.request_id,
        "priority_class": record.priority_class,
        "admission_key": record.admission_key,
        "shape_bucket": record.shape_bucket,
        "caller_attached": record.caller_attached,
        "wait_reason": record.wait_reason,
        "eviction_id": record.eviction_id,
        "created_at": record.created_at,
        "granted_at": record.granted_at,
        "heartbeat_at": record.heartbeat_at,
        "expires_at": record.expires_at,
        "released_at": record.released_at,
        "metadata": dict(record.metadata or {}),
    }


def _residency_payload(residency: GpuModelResidency) -> dict[str, Any]:
    return {
        "model_id": residency.model_id,
        "task_type": residency.task_type,
        "estimated_vram_mb": residency.estimated_vram_mb,
        "resident": residency.resident,
        "last_used_at": residency.last_used_at,
        "component": _resident_component(residency),
        "metadata": dict(residency.metadata or {}),
    }


def _reconciliation_payload(
    observation: Any | None,
    *,
    records: Iterable[GpuLeaseRecord] = (),
    evictions: dict[str, Any] | None = None,
    retry_after_seconds: float = 15.0,
) -> dict[str, Any] | None:
    if observation is None:
        return None
    all_inventories = tuple(getattr(observation, "inventories", ()) or ())
    known_measured_mb = sum(max(0, int(getattr(item, "known_measured_mb", 0) or 0)) for item in all_inventories)
    known_reported_mb = sum(max(0, int(getattr(item, "known_reported_mb", 0) or 0)) for item in all_inventories)
    inventories = all_inventories[:10]
    payload = {
        "observation_id": str(getattr(observation, "observation_id", "")),
        "state": str(getattr(observation, "state", "unknown")),
        "retry_after_seconds": max(0.0, float(retry_after_seconds or 0.0)),
        "observed_at": _epoch_or_none(getattr(observation, "observed_at", None)),
        "raw_residual_mb": int(getattr(observation, "raw_residual_mb", 0) or 0),
        "unresolved_known_owner_mb": int(getattr(observation, "unresolved_known_owner_mb", 0) or 0),
        "unattributed_mb": int(getattr(observation, "unattributed_mb", 0) or 0),
        "driver_observation_state": str(getattr(observation, "driver_observation_state", "unknown")),
        "driver_error": str(getattr(observation, "driver_error", ""))[:300],
        "capacity": {
            "driver_used_mb": max(0, int(getattr(observation, "driver_used_mb", 0) or 0)),
            "driver_free_mb": max(0, int(getattr(observation, "driver_free_mb", 0) or 0)),
            "known_measured_mb": known_measured_mb,
            "known_reported_mb": known_reported_mb,
            "context_allowance_mb": max(0, int(getattr(observation, "context_allowance_mb", 0) or 0)),
            "unresolved_known_owner_mb": int(getattr(observation, "unresolved_known_owner_mb", 0) or 0),
            "unattributed_mb": int(getattr(observation, "unattributed_mb", 0) or 0),
            "raw_residual_mb": int(getattr(observation, "raw_residual_mb", 0) or 0),
        },
        "inventories": [
            {
                "component": _bounded_reconciliation_identifier(getattr(inventory, "component", "")),
                "owner_component": _bounded_reconciliation_identifier(getattr(inventory, "owner_component", "")),
                "process_generation": _bounded_reconciliation_identifier(getattr(inventory, "process_generation", "")),
                "process_identity": _bounded_reconciliation_identifier(getattr(inventory, "process_identity", "")),
                "process_count": min(1_024, max(1, int(getattr(inventory, "process_count", 1) or 1))),
                "process_inventory_aggregated": bool(getattr(inventory, "process_inventory_aggregated", True)),
                "state": _reconciliation_inventory_state(getattr(inventory, "state", "unknown")),
                "allocator_capability": _reconciliation_allocator_capability(getattr(inventory, "allocator_capability", "unknown")),
                "observed_at": _epoch_or_none(getattr(inventory, "observed_at", None)),
            }
            for inventory in inventories
        ],
    }
    payload.update(_reconciliation_status_evidence(records, evictions=evictions))
    return payload


def _bounded_reconciliation_identifier(value: Any) -> str:
    text = str(value or "")
    allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._:@/-"
    return "".join(char if char in allowed else "_" for char in text)[:128]


def _reconciliation_inventory_state(value: Any) -> str:
    state = str(value or "unknown")
    return state if state in {"unknown", "present", "conflicted", "unavailable", "error"} else "unknown"


def _reconciliation_allocator_capability(value: Any) -> str:
    capability = str(value or "unknown")
    return capability if capability in {"unknown", "measured", "known_unmeasured", "reported"} else "unknown"


def _reconciliation_status_evidence(
    records: Iterable[GpuLeaseRecord], *, evictions: dict[str, Any] | None,
) -> dict[str, Any]:
    items = list(records)
    waiting = sorted((record for record in items if record.status == "waiting"), key=lambda item: (-item.priority, item.created_at, item.id))
    now = time.time()
    wait_groups: dict[tuple[str, str], dict[str, int | str]] = {}
    for record in waiting:
        reason = str(record.wait_reason or "queue_wait")[:80]
        priority_class = str(record.priority_class or "background")[:40]
        row = wait_groups.setdefault((priority_class, reason), {"priority_class": priority_class, "wait_reason": reason, "count": 0, "total_wait_ms": 0, "max_wait_ms": 0})
        wait_ms = max(0, int((now - float(record.created_at or now)) * 1000))
        row["count"] = int(row["count"]) + 1
        row["total_wait_ms"] = int(row["total_wait_ms"]) + wait_ms
        row["max_wait_ms"] = max(int(row["max_wait_ms"]), wait_ms)
    running = [record for record in items if record.status == "running" and int(record.estimated_vram_mb or 0) > 0]
    eviction_counters = dict((evictions or {}).get("counters") or {})
    counters = {
        "resident_reservations": len(running),
        "resident_reservation_mb": sum(max(0, int(record.estimated_vram_mb or 0)) for record in running),
        "coalesced_retries": int(eviction_counters.get("coalesced_retries") or 0),
        "stale_expiries": int(eviction_counters.get("stale_expiries") or 0),
        "cas_rejections": int(eviction_counters.get("cas_rejections") or 0),
        "unload_failures": int(eviction_counters.get("unload_failures") or 0),
        "unverified_release": int(eviction_counters.get("unverified_release") or 0),
        "idle_unload_queued": int(eviction_counters.get("idle_unload_queued") or 0),
        "idle_unload_completed": int(eviction_counters.get("idle_unload_completed") or 0),
        "lexical_vespa_degradation": 0,
    }
    calibration_sources: dict[str, int] = {}
    for record in running:
        source = str(dict(record.metadata or {}).get("calibration_source") or "unknown")[:80]
        calibration_sources[source] = calibration_sources.get(source, 0) + 1
    queue_head = waiting[0] if waiting else None
    if queue_head is None:
        queue_head_payload = None
        drain_reason = ""
    else:
        task_type = str(queue_head.task_type or "unknown")[:80]
        priority_class = str(queue_head.priority_class or "background")[:40]
        configured_lane = str(dict(queue_head.metadata or {}).get("priority_lane") or "").strip()
        derived_lane, _derived_priority = _gpu_priority_lane(task_type, priority_class)
        priority_lane = configured_lane if configured_lane in {
            "mcp_retrieval", "document_indexing", "image_ocr", "image_llm_enrichment", "video_extraction", "background",
        } else derived_lane
        queue_head_payload = {
            "task_type": task_type,
            "priority": int(queue_head.priority or 0),
            "priority_class": priority_class,
            "priority_lane": priority_lane,
            "wait_reason": str(queue_head.wait_reason or "queue_wait"),
        }
        drain_reason = f"{priority_lane}_queue_head" if priority_lane != "background" else ""
    return {
        "queue": {
            "head": queue_head_payload,
            "drain_reason": drain_reason,
            "wait_reasons": {str(key[1]): int(value["count"]) for key, value in sorted(wait_groups.items())},
            "wait_duration_by_reason_class": [wait_groups[key] for key in sorted(wait_groups)],
        },
        "reservation": {
            "running_count": len(running),
            "reserved_peak_mb": counters["resident_reservation_mb"],
            "calibration_sources": calibration_sources,
        },
        "eviction": {"claim_outcomes": dict(eviction_counters), "verification_outcomes": dict(eviction_counters)},
        "counters": counters,
    }


def _failed_reconciliation_observation(error: Exception) -> Any:
    from .gpu_reconciliation import GpuReconciliationObservation

    return GpuReconciliationObservation(
        observation_id=uuid4().hex,
        state="inventory_incomplete",
        driver_used_mb=0,
        driver_free_mb=0,
        raw_residual_mb=0,
        unresolved_known_owner_mb=0,
        unattributed_mb=0,
        driver_observation_state="failed",
        driver_error=str(error)[:300],
    )


def _unavailable_reconciliation_payload(
    error: Exception,
    *,
    retry_after_seconds: float,
) -> dict[str, Any]:
    payload = _reconciliation_payload(
        _failed_reconciliation_observation(error),
        retry_after_seconds=retry_after_seconds,
    )
    assert payload is not None
    payload["driver_observation_state"] = "unavailable"
    return payload


def _reconciliation_observation_id(observation: Any | None) -> str | None:
    value = getattr(observation, "observation_id", None)
    return str(value) if value else None


def _resident_component(residency: GpuModelResidency) -> str:
    metadata = dict(residency.metadata or {})
    component = str(metadata.get("component") or metadata.get("owner") or "").strip()
    return component or _component_for_task_type(residency.task_type)


def _record_model_residency_activity(residency: GpuModelResidency) -> None:
    if not residency.resident:
        return
    try:
        from .model_activity import record_model_activity

        component = _resident_component(residency)
        metadata = {
            "resident": True,
            "task_type": residency.task_type,
        }
        if component:
            metadata["component"] = component
        with record_model_activity(
            service=component or "unknown",
            endpoint="/gpu/residency",
            action="model_loading",
            activity_class="model_loading",
            caller_surface="gpu_scheduler",
            model=residency.model_id,
            metadata=metadata,
        ):
            pass
    except Exception:
        pass


def _component_for_task_type(task_type: str) -> str:
    if task_type in {"embedding", "rerank"}:
        return "model-runner"
    if task_type in {"ocr_image", "ocr_document"}:
        return "paddle-runner"
    if task_type == "asr":
        return "asr"
    if task_type == "ollama_vision":
        return "ollama"
    return ""


def _task_types_for_component(component: str) -> list[str]:
    if component == "model-runner":
        return ["embedding", "rerank"]
    if component == "paddle-runner":
        return ["ocr_image", "ocr_document"]
    if component == "asr":
        return ["asr"]
    if component == "ollama":
        return ["ollama_vision"]
    return []


def _ollama_loaded_model_names(config: GpuSchedulerConfig) -> set[str] | None:
    base_url = str(config.ollama_base_url or "").strip()
    if not base_url:
        return None
    try:
        payload = _get_json(
            base_url,
            "/api/ps",
            timeout_seconds=min(2.0, max(0.1, float(config.eviction_request_timeout_seconds or 2.0))),
        )
    except Exception:
        return None
    models = payload.get("models") if isinstance(payload, dict) else None
    if not isinstance(models, list):
        return set()
    names: set[str] = set()
    for item in models:
        if not isinstance(item, dict):
            continue
        for key in ("model", "name"):
            value = str(item.get(key) or "").strip()
            if value:
                names.add(value)
    return names


def _ollama_model_is_loaded(model_id: str, loaded_models: set[str]) -> bool:
    model = str(model_id or "").strip()
    if not model:
        return False
    if model in loaded_models:
        return True
    if ":" not in model:
        return any(item.split(":", 1)[0] == model for item in loaded_models)
    return False


def _get_json(base_url: str, path: str, *, timeout_seconds: float) -> dict[str, Any]:
    request = Request(
        urljoin(f"{base_url.rstrip('/')}/", path.lstrip("/")),
        method="GET",
    )
    try:
        with urlopen(request, timeout=max(0.1, float(timeout_seconds))) as response:
            raw = response.read().decode("utf-8")
    except HTTPError as exc:  # pragma: no cover - deployment-dependent
        raw_error = exc.read().decode("utf-8", errors="replace")
        raise GpuSchedulerError(f"GPU runtime endpoint returned HTTP {exc.code}: {raw_error[:300]}") from exc
    except URLError as exc:  # pragma: no cover - deployment-dependent
        raise GpuSchedulerError(str(exc)) from exc
    try:
        parsed = json.loads(raw or "{}")
    except json.JSONDecodeError as exc:
        raise GpuSchedulerError("GPU runtime endpoint returned invalid JSON") from exc
    if not isinstance(parsed, dict):
        raise GpuSchedulerError("GPU runtime endpoint returned a non-object payload")
    return parsed


def _matching_runtime_residency(
    base_url: str,
    candidate: GpuEvictionCandidate,
    *,
    timeout_seconds: float,
) -> dict[str, Any] | None:
    payload = _get_json(base_url, "/v1/gpu/residency", timeout_seconds=timeout_seconds)
    models = payload.get("models") if isinstance(payload, dict) else None
    if not isinstance(models, list):
        return None
    expected_component = candidate.component or _component_for_task_type(candidate.task_type)
    for model in models:
        if not isinstance(model, dict):
            continue
        if str(model.get("task_type") or "") != candidate.task_type:
            continue
        if str(model.get("model_id") or "") != candidate.model_id:
            continue
        owner_component = str(model.get("owner_component") or payload.get("owner_component") or "").strip()
        if not expected_component or not owner_component or owner_component != expected_component:
            continue
        return model
    return None


def _post_json(base_url: str, path: str, payload: dict[str, Any], *, timeout_seconds: float) -> dict[str, Any]:
    body = json.dumps(payload).encode("utf-8")
    request = Request(
        urljoin(f"{base_url.rstrip('/')}/", path.lstrip("/")),
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urlopen(request, timeout=max(0.1, float(timeout_seconds))) as response:
            raw = response.read().decode("utf-8")
    except HTTPError as exc:  # pragma: no cover - deployment-dependent
        raw_error = exc.read().decode("utf-8", errors="replace")
        raise GpuSchedulerError(f"GPU eviction endpoint returned HTTP {exc.code}: {raw_error[:300]}") from exc
    except URLError as exc:  # pragma: no cover - deployment-dependent
        raise GpuSchedulerError(str(exc)) from exc
    try:
        parsed = json.loads(raw or "{}")
    except json.JSONDecodeError as exc:
        raise GpuSchedulerError("GPU eviction endpoint returned invalid JSON") from exc
    if not isinstance(parsed, dict):
        raise GpuSchedulerError("GPU eviction endpoint returned a non-object payload")
    if parsed.get("ok") is False:
        raise GpuSchedulerError(str(parsed.get("message") or "GPU eviction endpoint rejected unload"))
    return parsed


def _empty_eviction_status() -> dict[str, Any]:
    return {
        "attempts": 0,
        "queued": 0,
        "running": 0,
        "retrying": 0,
        "successes": 0,
        "failures": 0,
        "estimated_freed_vram_mb": 0,
        "counters": {
            "coalesced_retries": 0,
            "stale_expiries": 0,
            "cas_rejections": 0,
            "unload_failures": 0,
            "unverified_release": 0,
            "idle_unload_queued": 0,
            "idle_unload_completed": 0,
        },
        "last_errors": [],
        "recent": [],
    }


def _eviction_min_freed_vram_mb(candidate: GpuEvictionCandidate) -> int:
    estimate = max(0, int(candidate.estimated_vram_mb or 0))
    if estimate <= 0:
        return 1
    return min(estimate, max(GPU_EVICTION_MIN_FREED_MB, int(estimate * GPU_EVICTION_MIN_FREED_FRACTION)))


def _eviction_status(
    rows: Iterable[dict[str, Any]],
    *,
    cas_rejections: int = 0,
) -> dict[str, Any]:
    items = [dict(row) for row in rows]
    queued = [item for item in items if str(item.get("status") or "") == "queued"]
    running = [item for item in items if str(item.get("status") or "") == "running"]
    retrying = [item for item in items if str(item.get("status") or "") == "retrying"]
    successes = [item for item in items if str(item.get("status") or "") == "succeeded"]
    failures = [item for item in items if str(item.get("status") or "") == "failed"]
    def terminal_reason(item: dict[str, Any]) -> str:
        metadata = item.get("metadata") if isinstance(item.get("metadata"), dict) else {}
        return str(item.get("terminal_reason") or metadata.get("terminal_reason") or "")
    counters = {
        "coalesced_retries": sum(max(0, int(item.get("broker_delivery_count") or 0) - 1) for item in items),
        "stale_expiries": sum(1 for item in items if str(item.get("status") or "") == "expired" or terminal_reason(item) == "stale_request_expired"),
        "cas_rejections": max(0, int(cas_rejections or 0)),
        "unload_failures": sum(1 for item in items if terminal_reason(item) == "unload_failed"),
        "unverified_release": sum(1 for item in items if terminal_reason(item) == "memory_release_unverified"),
        "idle_unload_queued": sum(1 for item in items if str(item.get("request_reason") or "") == "idle"),
        "idle_unload_completed": sum(1 for item in successes if str(item.get("request_reason") or "") == "idle"),
    }
    return {
        "attempts": len(items),
        "queued": len(queued),
        "running": len(running),
        "retrying": len(retrying),
        "successes": len(successes),
        "failures": len(failures),
        "estimated_freed_vram_mb": sum(int(item.get("estimated_freed_vram_mb") or 0) for item in successes),
        "counters": counters,
        "last_errors": [
            {
                "task_type": str(item.get("task_type") or ""),
                "model_id": str(item.get("model_id") or ""),
                "component": str(item.get("component") or ""),
                "error": str(item.get("error") or ""),
                "created_at": _epoch_or_none(item.get("created_at")),
            }
            for item in failures[-5:]
        ],
        "recent": [_eviction_payload(item) for item in items[-20:]],
    }


def _eviction_payload(row: dict[str, Any]) -> dict[str, Any]:
    return {
        "id": str(row.get("id") or ""),
        "lease_id": str(row.get("lease_id") or ""),
        "task_type": str(row.get("task_type") or ""),
        "model_id": str(row.get("model_id") or ""),
        "component": str(row.get("component") or ""),
        "status": str(row.get("status") or ""),
        "estimated_freed_vram_mb": int(row.get("estimated_freed_vram_mb") or 0),
        "error": str(row.get("error") or ""),
        "created_at": _epoch_or_none(row.get("created_at")),
        "queued_at": _epoch_or_none(row.get("queued_at")),
        "started_at": _epoch_or_none(row.get("started_at")),
        "completed_at": _epoch_or_none(row.get("completed_at")),
        "broker_message_id": str(row.get("broker_message_id") or ""),
        "routing_key": str(row.get("routing_key") or ""),
        "broker_delivery_count": int(row.get("broker_delivery_count") or 0),
        "request_reason": str(row.get("request_reason") or ""),
        "terminal_reason": str(row.get("terminal_reason") or ""),
        "metadata": dict(row.get("metadata") or {}),
    }


def _retry_after_seconds(reason: str) -> float:
    if reason == "reconciliation_required":
        return 15.0
    if reason == "vram_busy":
        return 5.0
    if reason == "exclusive_conflict":
        return 2.0
    return 1.0


def _fetch_dicts(conn: Any, statement: str, params: tuple[Any, ...]) -> list[dict[str, Any]]:
    try:
        from psycopg.rows import dict_row

        with conn.cursor(row_factory=dict_row) as cur:
            cur.execute(statement, params)
            return [dict(row) for row in cur.fetchall()]
    except TypeError:
        with conn.cursor() as cur:
            cur.execute(statement, params)
            columns = [item.name if hasattr(item, "name") else item[0] for item in cur.description or []]
            return [dict(zip(columns, row)) for row in cur.fetchall()]


def _status_counter(rows: Iterable[dict[str, Any]], key: str) -> int:
    first = next(iter(rows), {})
    try:
        return max(0, int(first.get(key) or 0)) if isinstance(first, dict) else 0
    except (TypeError, ValueError):
        return 0


def _execute_cursor(conn: Any, statement: str, params: tuple[Any, ...]) -> None:
    with conn.cursor() as cur:
        cur.execute(statement, params)


def _jsonb_adapter() -> Any:
    from psycopg.types.json import Jsonb

    return Jsonb


def _record_from_row(row: dict[str, Any]) -> GpuLeaseRecord:
    return GpuLeaseRecord(
        id=str(row.get("id") or ""),
        task_type=str(row.get("task_type") or ""),
        model_id=str(row.get("model_id") or ""),
        status=str(row.get("status") or ""),
        estimated_vram_mb=int(row.get("estimated_vram_mb") or 0),
        exclusive=bool(row.get("exclusive")),
        share_group=str(row.get("share_group") or ""),
        priority=int(row.get("priority") or 0),
        component=str(row.get("component") or ""),
        request_id=str(row.get("request_id") or ""),
        created_at=_epoch(row.get("created_at")),
        granted_at=_epoch_or_none(row.get("granted_at")),
        heartbeat_at=_epoch_or_none(row.get("heartbeat_at")),
        expires_at=_epoch_or_none(row.get("expires_at")),
        released_at=_epoch_or_none(row.get("released_at")),
        metadata=dict(row.get("metadata") or {}),
        priority_class=str(row.get("priority_class") or "background"),
        admission_key=str(row.get("admission_key") or ""),
        shape_bucket=str(row.get("shape_bucket") or ""),
        caller_attached=bool(row.get("caller_attached", True)),
        wait_reason=str(row.get("wait_reason") or ""),
        eviction_id=str(row.get("linked_eviction_id") or ""),
    )


def _residency_from_row(row: dict[str, Any]) -> GpuModelResidency:
    return GpuModelResidency(
        model_id=str(row.get("model_id") or ""),
        task_type=str(row.get("task_type") or ""),
        estimated_vram_mb=int(row.get("estimated_vram_mb") or 0),
        resident=bool(row.get("resident")),
        last_used_at=_epoch_or_none(row.get("last_used_at")),
        metadata=dict(row.get("metadata") or {}),
    )


def _epoch(value: Any) -> float:
    parsed = _epoch_or_none(value)
    return parsed if parsed is not None else 0.0


def _epoch_or_none(value: Any) -> float | None:
    if value is None:
        return None
    if isinstance(value, (int, float)):
        return float(value)
    if hasattr(value, "timestamp"):
        return float(value.timestamp())
    try:
        return float(value)
    except (TypeError, ValueError):
        return None
