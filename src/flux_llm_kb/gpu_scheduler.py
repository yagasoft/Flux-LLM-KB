from __future__ import annotations

from dataclasses import dataclass, field
import os
import threading
import time
from typing import Any, Iterable
from uuid import uuid4


GPU_LEASE_STATUSES = frozenset({"waiting", "running", "released", "timed_out", "recovered", "rejected"})


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
    lease_ttl_seconds: float = 120.0
    heartbeat_interval_seconds: float = 10.0
    stale_after_seconds: float = 180.0


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
class GpuAdmissionDecision:
    granted: bool
    rejected: bool
    reason: str
    active_vram_mb: int
    resident_vram_mb: int
    available_vram_mb: int
    recovered_lease_ids: list[str] = field(default_factory=list)


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

    @property
    def id(self) -> str:
        return self.record.id

    def heartbeat(self) -> None:
        self.scheduler.heartbeat(self.id)

    def release(self) -> None:
        if self.released:
            return
        self.scheduler.release(self.id)
        self.released = True

    def __enter__(self) -> "GpuLease":
        return self

    def __exit__(self, *_args: Any) -> bool:
        self.release()
        return False


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


def plan_gpu_admission(
    profile: GpuTaskProfile,
    *,
    active_leases: Iterable[GpuLeaseRecord],
    config: GpuSchedulerConfig,
    resident_models: Iterable[GpuModelResidency] | None = None,
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

    residents = [
        resident
        for resident in resident_models or ()
        if resident.resident and resident.model_id and resident.model_id != profile.model_id
    ]
    resident_vram = sum(max(0, int(resident.estimated_vram_mb or 0)) for resident in residents)
    active_vram = sum(max(0, int(lease.estimated_vram_mb or 0)) for lease in active)
    available_vram = _available_vram_mb(config, live_free_vram_mb=live_free_vram_mb)
    # Live free VRAM already reflects loaded resident models and running GPU work.
    # Fall back to configured estimates only when live evidence is unavailable.
    estimated_resident_vram = 0 if live_free_vram_mb is not None else resident_vram
    estimated_active_vram = 0 if live_free_vram_mb is not None else active_vram
    effective_available = max(0, available_vram - estimated_resident_vram)
    requested_vram = max(0, int(profile.estimated_vram_mb or 0))

    if requested_vram > effective_available:
        return GpuAdmissionDecision(
            granted=False,
            rejected=True,
            reason="vram_budget_exceeded",
            active_vram_mb=active_vram,
            resident_vram_mb=resident_vram,
            available_vram_mb=effective_available,
            recovered_lease_ids=recovered,
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
        )
    return GpuAdmissionDecision(
        granted=True,
        rejected=False,
        reason="granted",
        active_vram_mb=active_vram,
        resident_vram_mb=resident_vram,
        available_vram_mb=effective_available,
        recovered_lease_ids=recovered,
    )


class InProcessGpuScheduler(BaseGpuScheduler):
    def __init__(self, config: GpuSchedulerConfig | None = None) -> None:
        self.config = config or GpuSchedulerConfig()
        self._condition = threading.Condition()
        self._leases: dict[str, GpuLeaseRecord] = {}
        self._resident_models: dict[tuple[str, str], GpuModelResidency] = {}

    def acquire(self, profile: GpuTaskProfile) -> GpuLease:
        if not self.config.enabled:
            return GpuLease(self, self._make_record(profile, status="running", granted=True))
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
                        resident_models=(),
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

    def status(self) -> dict[str, Any]:
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

    def status(self) -> dict[str, Any]:
        try:
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

    def _try_grant(self, profile: GpuTaskProfile, lease_id: str) -> GpuLeaseRecord | str:
        self._recover_stale()
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

    def _recover_stale(self) -> None:
        self._execute(
            """
            UPDATE gpu_leases
               SET status = 'recovered', released_at = now()
             WHERE status = 'running'
               AND expires_at < now()
            """,
            (),
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
        lease_ttl_seconds=float(values["gpu.scheduler.lease_ttl_seconds"] or 120),
        heartbeat_interval_seconds=float(values["gpu.scheduler.heartbeat_interval_seconds"] or 10),
        stale_after_seconds=float(values["gpu.scheduler.stale_after_seconds"] or 180),
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
    return GpuTaskProfile(
        task_type=normalized,
        model_id=model_id,
        estimated_vram_mb=estimate,
        priority=priority,
        timeout_seconds=timeout_seconds,
        lease_ttl_seconds=float(values["gpu.scheduler.lease_ttl_seconds"] or 120),
        exclusive=bool(exclusive),
        share_group=str(share_group or ""),
        component=component,
        request_id=request_id,
        metadata=dict(metadata or {}),
    )


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
        "gpu.scheduler.lease_ttl_seconds": 120,
        "gpu.scheduler.heartbeat_interval_seconds": 10,
        "gpu.scheduler.stale_after_seconds": 180,
        "gpu.scheduler.embedding_vram_mb": 2_500,
        "gpu.scheduler.rerank_vram_mb": 7_000,
        "gpu.scheduler.ocr_image_vram_mb": 2_000,
        "gpu.scheduler.ocr_document_vram_mb": 8_000,
        "gpu.scheduler.asr_vram_mb": 6_000,
        "gpu.scheduler.ollama_vision_vram_mb": 9_000,
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
    if record.expires_at is not None:
        return float(record.expires_at) < now
    if record.heartbeat_at is None:
        return False
    return False


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
        "metadata": dict(residency.metadata or {}),
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
