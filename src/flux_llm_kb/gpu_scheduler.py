from __future__ import annotations

from dataclasses import dataclass, field
import json
import os
import threading
import time
from typing import Any, Iterable
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
    metadata: dict[str, Any] = field(default_factory=dict)


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
    model_runner_base_url: str = ""
    paddle_runner_base_url: str = ""
    asr_base_url: str = ""
    ollama_base_url: str = ""


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


class GpuLeaseRejected(GpuSchedulerError):
    pass


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

    def acquire(self, profile: GpuTaskProfile) -> GpuLease:
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


def plan_gpu_admission(
    profile: GpuTaskProfile,
    *,
    active_leases: Iterable[GpuLeaseRecord],
    config: GpuSchedulerConfig,
    resident_models: Iterable[GpuModelResidency] | None = None,
    waiting_leases: Iterable[GpuLeaseRecord] | None = None,
    live_free_vram_mb: int | None = None,
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
    available_vram = _available_vram_mb(config, live_free_vram_mb=live_free_vram_mb)
    # Live free VRAM already reflects loaded resident models and running GPU work.
    # Fall back to configured estimates only when live evidence is unavailable.
    estimated_resident_vram = 0 if live_free_vram_mb is not None else resident_vram
    estimated_active_vram = 0 if live_free_vram_mb is not None else active_vram
    effective_available = max(0, available_vram - estimated_resident_vram)
    requested_vram = 0 if resident_hit else max(0, int(profile.estimated_vram_mb or 0))

    if requested_vram > effective_available:
        eviction_candidates = (
            select_gpu_eviction_candidates(
                profile,
                resident_models=resident_items,
                active_leases=active,
                waiting_leases=waiting_leases or (),
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
            )
    if estimated_active_vram + requested_vram > effective_available:
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
        idle.append(
            GpuEvictionCandidate(
                task_type=residency.task_type,
                model_id=residency.model_id,
                estimated_vram_mb=max(0, int(residency.estimated_vram_mb or 0)),
                component=_resident_component(residency),
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
    def __init__(self, config: GpuSchedulerConfig | None = None) -> None:
        self.config = config or GpuSchedulerConfig()
        self._condition = threading.Condition()
        self._leases: dict[str, GpuLeaseRecord] = {}
        self._resident_models: dict[tuple[str, str], GpuModelResidency] = {}

    def acquire(self, profile: GpuTaskProfile) -> GpuLease:
        if not self.config.enabled:
            return GpuLease(self, self._make_record(profile, status="running", granted=True))
        self._reconcile_runtime_residency()
        lease_id = uuid4().hex
        deadline = time.monotonic() + _profile_timeout(profile, self.config)
        record = self._make_record(profile, lease_id=lease_id, status="waiting", granted=False)
        with self._condition:
            self._leases[lease_id] = record
            while True:
                now = time.time()
                self._recover_stale_locked(now)
                waiters = self._waiting_locked()
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
                        now=now,
                    )
                    for recovered_id in decision.recovered_lease_ids:
                        self._recover_locked(recovered_id, now=now)
                    if decision.rejected:
                        self._leases[lease_id] = _replace_record(record, status="rejected", released_at=now)
                        self._condition.notify_all()
                        raise GpuLeaseRejected(decision.reason)
                    if decision.granted:
                        expires_at = now + _profile_ttl(profile, self.config)
                        granted_record = _replace_record(
                            record,
                            status="running",
                            granted_at=now,
                            heartbeat_at=now,
                            expires_at=expires_at,
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
                "live_gpu_memory": live_gpu_memory(),
                "timeouts": counts.get("timed_out", 0),
                "rejections": counts.get("rejected", 0),
                "evictions": _empty_eviction_status(),
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
            expires_at=(now + _profile_ttl(profile, self.config)) if granted else None,
            released_at=None,
            metadata=dict(profile.metadata or {}),
        )

    def _running_locked(self) -> list[GpuLeaseRecord]:
        return [record for record in self._leases.values() if record.status == "running"]

    def _waiting_locked(self) -> list[GpuLeaseRecord]:
        return sorted(
            [record for record in self._leases.values() if record.status == "waiting"],
            key=lambda record: (-record.priority, record.created_at, record.id),
        )

    def _recover_stale_locked(self, now: float) -> None:
        for record in list(self._leases.values()):
            if record.status == "running" and _lease_is_stale(record, now=now):
                self._recover_locked(record.id, now=now)

    def _recover_locked(self, lease_id: str, *, now: float) -> None:
        record = self._leases.get(lease_id)
        if record is not None and record.status == "running":
            self._leases[lease_id] = _replace_record(record, status="recovered", released_at=now)

    def _reconcile_runtime_residency(self) -> None:
        loaded_ollama_models = _ollama_loaded_model_names(self.config)
        if loaded_ollama_models is None:
            return
        with self._condition:
            for key, residency in list(self._resident_models.items()):
                if _resident_component(residency) != "ollama":
                    continue
                if not _ollama_model_is_loaded(residency.model_id, loaded_ollama_models):
                    self._resident_models.pop(key, None)
            self._condition.notify_all()


class DisabledGpuScheduler(BaseGpuScheduler):
    def __init__(self, config: GpuSchedulerConfig | None = None) -> None:
        self.config = config or GpuSchedulerConfig(enabled=False)
        self._leases = InProcessGpuScheduler(GpuSchedulerConfig(enabled=False))

    def acquire(self, profile: GpuTaskProfile) -> GpuLease:
        return self._leases.acquire(profile)

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
    def __init__(self, config: GpuSchedulerConfig | None = None, *, database_url: str | None = None) -> None:
        self.config = config or GpuSchedulerConfig(mode="postgres")
        self.database_url = database_url

    def acquire(self, profile: GpuTaskProfile) -> GpuLease:
        if not self.config.enabled:
            return DisabledGpuScheduler(self.config).acquire(profile)
        lease_id = uuid4().hex
        timeout = _profile_timeout(profile, self.config)
        deadline = time.monotonic() + timeout
        self._insert_waiting(profile, lease_id)
        last_reason = "queue_wait"
        while True:
            decision_record = self._try_grant(profile, lease_id)
            if isinstance(decision_record, GpuLeaseRecord):
                return GpuLease(self, decision_record)
            if isinstance(decision_record, GpuAdmissionDecision):
                last_reason = decision_record.reason
                queued = self._enqueue_eviction_requests(profile, lease_id, list(decision_record.eviction_candidates))
                if int(queued.get("queued") or queued.get("deduped") or 0) > 0:
                    self._mark_terminal(lease_id, "timed_out")
                    raise GpuLeaseTimeout(
                        f"GPU scheduler queued eviction before retrying {profile.task_type}",
                        retry_after_seconds=_retry_after_seconds(last_reason),
                    )
                self._mark_terminal(lease_id, "rejected")
                raise GpuLeaseRejected("vram_budget_exceeded")
            if decision_record == "rejected":
                raise GpuLeaseRejected("vram_budget_exceeded")
            if isinstance(decision_record, str):
                last_reason = decision_record
            remaining = deadline - time.monotonic()
            if remaining <= 0:
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
            records = [_record_from_row(row) for row in rows]
            counts = _status_counts(records)
            return {
                "enabled": self.config.enabled,
                "mode": "postgres",
                "budget": _budget_payload(self.config),
                "counts": counts,
                "running": [_record_payload(record) for record in records if record.status == "running"],
                "waiting": [_record_payload(record) for record in records if record.status == "waiting"],
                "recent": [_record_payload(record) for record in records if record.status not in {"running", "waiting"}][-50:],
                "model_residency": [_residency_payload(_residency_from_row(row)) for row in residency_rows],
                "live_gpu_memory": live_gpu_memory(),
                "timeouts": counts.get("timed_out", 0),
                "rejections": counts.get("rejected", 0),
                "evictions": _eviction_status(eviction_rows),
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
                "live_gpu_memory": live_gpu_memory(),
                "timeouts": 0,
                "rejections": 0,
                "evictions": _empty_eviction_status(),
            }

    def _insert_waiting(self, profile: GpuTaskProfile, lease_id: str) -> None:
        Jsonb = _jsonb_adapter()
        self._execute(
            """
            INSERT INTO gpu_leases (
                id, task_type, model_id, status, estimated_vram_mb, exclusive, share_group,
                priority, component, request_id, created_at, metadata
            )
            VALUES (%s, %s, %s, 'waiting', %s, %s, %s, %s, %s, %s, now(), %s)
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
                Jsonb(dict(profile.metadata or {})),
            ),
        )

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
                    [record for record in records if record.status == "waiting"],
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
                if decision.rejected and decision.eviction_candidates:
                    return decision
                if decision.rejected:
                    _execute_cursor(
                        conn,
                        "UPDATE gpu_leases SET status = 'rejected', released_at = now() WHERE id = %s",
                        (lease_id,),
                    )
                    return "rejected"
                if not decision.granted:
                    return decision.reason
                _execute_cursor(
                    conn,
                    """
                    UPDATE gpu_leases
                       SET status = 'running',
                           granted_at = now(),
                           heartbeat_at = now(),
                           expires_at = now() + (%s * interval '1 second')
                     WHERE id = %s
                     RETURNING *
                    """,
                    (float(_profile_ttl(profile, self.config)), lease_id),
                )
                granted = _fetch_dicts(conn, "SELECT * FROM gpu_leases WHERE id = %s", (lease_id,))
                return _record_from_row(granted[0])

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
        if component == "ollama":
            return self._evict_ollama(candidate)
        base_url = {
            "model-runner": self.config.model_runner_base_url,
            "paddle-runner": self.config.paddle_runner_base_url,
            "asr": self.config.asr_base_url,
        }.get(component, "")
        if not base_url:
            raise GpuSchedulerError(f"GPU eviction URL is not configured for {component or candidate.task_type}")
        payload = {"task_type": candidate.task_type, "model_id": candidate.model_id}
        return _post_json(base_url, "/v1/gpu/unload", payload, timeout_seconds=self.config.eviction_request_timeout_seconds)

    def _evict_ollama(self, candidate: GpuEvictionCandidate) -> dict[str, Any]:
        base_url = self.config.ollama_base_url
        if not base_url:
            raise GpuSchedulerError("Ollama eviction URL is not configured")
        payload = {"model": candidate.model_id, "prompt": "", "keep_alive": 0, "stream": False}
        return _post_json(base_url, "/api/generate", payload, timeout_seconds=self.config.eviction_request_timeout_seconds)

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
            """,
            (),
        )

    def _reconcile_runtime_residency(self) -> None:
        loaded_ollama_models = _ollama_loaded_model_names(self.config)
        if loaded_ollama_models is None:
            return
        for row in self._runtime_residency_rows("ollama"):
            task_type = str(row.get("task_type") or "")
            model_id = str(row.get("model_id") or "")
            if not task_type or not model_id:
                continue
            if not _ollama_model_is_loaded(model_id, loaded_ollama_models):
                self._clear_runtime_residency(task_type, model_id, "ollama", "ollama_not_loaded")

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
    if request.get("status") in {"succeeded", "failed", "skipped"}:
        return {
            "eviction_id": request.get("eviction_id") or request.get("id"),
            "status": request.get("status"),
            "already_terminal": True,
            "retryable": False,
        }
    config = scheduler_config_from_settings()
    profile = _gpu_task_profile_from_eviction_request(request)
    candidate = _gpu_eviction_candidate_from_request(request)
    attempt = max(1, int(request.get("broker_delivery_count") or 1))
    scheduler = PostgresGpuScheduler(config)
    if not config.eviction_enabled:
        completed = db.complete_gpu_eviction_request(
            eviction_id=eviction_id,
            status="skipped",
            error="GPU eviction is disabled",
            metadata={"attempt": attempt},
            correlation_id=correlation_id,
            causation_id=causation_id or broker_message_id,
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
            correlation_id=correlation_id,
            causation_id=causation_id or broker_message_id,
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
            correlation_id=correlation_id,
            causation_id=causation_id or broker_message_id,
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
            correlation_id=correlation_id,
            causation_id=causation_id or broker_message_id,
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
        correlation_id=correlation_id,
        causation_id=causation_id or broker_message_id,
    )
    return {
        "eviction_id": eviction_id,
        "status": "retrying",
        "retryable": True,
        "result": retry or {},
    }


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
        model_runner_base_url=str(values.get("model_runner.base_url") or os.environ.get("FLUX_KB_MODEL_RUNNER_BASE_URL") or ""),
        paddle_runner_base_url=str(values.get("model_runner.paddle_runner_base_url") or os.environ.get("FLUX_KB_PADDLE_RUNNER_BASE_URL") or ""),
        asr_base_url=str(values.get("acceleration.asr.base_url") or os.environ.get("FLUX_KB_ASR_BASE_URL") or ""),
        ollama_base_url=str(values.get("acceleration.local_inference.base_url") or os.environ.get("FLUX_KB_LOCAL_INFERENCE_BASE_URL") or ""),
    )


def task_profile(
    task_type: str,
    *,
    model_id: str = "",
    component: str = "",
    request_id: str = "",
    priority: int = 0,
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
    return GpuTaskProfile(
        task_type=normalized,
        model_id=model_id,
        estimated_vram_mb=estimate,
        priority=priority,
        timeout_seconds=effective_timeout_seconds,
        lease_ttl_seconds=float(values["gpu.scheduler.lease_ttl_seconds"] or 120),
        exclusive=bool(exclusive),
        share_group=str(share_group or ""),
        component=component,
        request_id=request_id,
        metadata=dict(metadata or {}),
    )


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
    }


def _gpu_task_profile_from_eviction_request(request: dict[str, Any]) -> GpuTaskProfile:
    metadata = request.get("metadata") if isinstance(request.get("metadata"), dict) else {}
    raw_profile = metadata.get("request_profile") if isinstance(metadata.get("request_profile"), dict) else {}
    return GpuTaskProfile(
        task_type=str(raw_profile.get("task_type") or "unknown"),
        model_id=str(raw_profile.get("model_id") or ""),
        estimated_vram_mb=max(0, int(raw_profile.get("estimated_vram_mb") or 0)),
        priority=int(raw_profile.get("priority") or 0),
        timeout_seconds=_optional_float(raw_profile.get("timeout_seconds")),
        lease_ttl_seconds=_optional_float(raw_profile.get("lease_ttl_seconds")),
        exclusive=bool(raw_profile.get("exclusive", True)),
        share_group=str(raw_profile.get("share_group") or ""),
        component=str(raw_profile.get("component") or ""),
        request_id=str(raw_profile.get("request_id") or ""),
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


def _optional_float(value: Any) -> float | None:
    if value is None or value == "":
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


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
    configured = max(0, int(config.vram_budget_mb or config.total_vram_mb or 0) - int(config.safety_margin_mb or 0))
    if live_free_vram_mb is None:
        return configured
    live_available = max(0, int(live_free_vram_mb) - int(config.safety_margin_mb or 0))
    if configured <= 0:
        return live_available
    return min(configured, live_available)


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
        "last_errors": [],
        "recent": [],
    }


def _eviction_min_freed_vram_mb(candidate: GpuEvictionCandidate) -> int:
    estimate = max(0, int(candidate.estimated_vram_mb or 0))
    if estimate <= 0:
        return 1
    return min(estimate, max(GPU_EVICTION_MIN_FREED_MB, int(estimate * GPU_EVICTION_MIN_FREED_FRACTION)))


def _eviction_status(rows: Iterable[dict[str, Any]]) -> dict[str, Any]:
    items = [dict(row) for row in rows]
    queued = [item for item in items if str(item.get("status") or "") == "queued"]
    running = [item for item in items if str(item.get("status") or "") == "running"]
    retrying = [item for item in items if str(item.get("status") or "") == "retrying"]
    successes = [item for item in items if str(item.get("status") or "") == "succeeded"]
    failures = [item for item in items if str(item.get("status") or "") == "failed"]
    return {
        "attempts": len(items),
        "queued": len(queued),
        "running": len(running),
        "retrying": len(retrying),
        "successes": len(successes),
        "failures": len(failures),
        "estimated_freed_vram_mb": sum(int(item.get("estimated_freed_vram_mb") or 0) for item in successes),
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
        "metadata": dict(row.get("metadata") or {}),
    }


def _retry_after_seconds(reason: str) -> float:
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
