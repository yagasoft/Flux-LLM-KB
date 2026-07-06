from __future__ import annotations

import asyncio
from datetime import UTC, datetime
import time
from typing import Any
from uuid import NAMESPACE_URL, uuid5

from . import database, governance, messaging, operator_automation
from .service import (
    _governance_policy_from_settings,
    _operator_automation_policy_from_settings,
)


DEFAULT_SCHEDULER_NAME = "event-scheduler:docker"


class EventScheduler:
    def __init__(
        self,
        *,
        database_module: Any = database,
        scheduler_id: str = DEFAULT_SCHEDULER_NAME,
    ) -> None:
        self.database = database_module
        self.scheduler_id = scheduler_id
        self._last_automation_at = 0.0
        self._last_governance_at = 0.0

    def run_once(self, *, limit: int = 25, force_due: bool = False) -> dict[str, Any]:
        capped_limit = max(1, min(int(limit or 25), 100))
        result: dict[str, Any] = {
            "status": "scheduled",
            "scheduler_id": self.scheduler_id,
            "limit": capped_limit,
            "settings_mutated": False,
        }
        try:
            result["mail_orphan_recovery"] = self.database.recover_interrupted_imap_sync_runs(
                worker_id=self.scheduler_id,
                worker_started_at=datetime.now(UTC),
            )
        except Exception as exc:
            result["mail_orphan_recovery"] = {"status": "failed", "error": str(exc)}
        result["imap"] = self.database.enqueue_due_imap_sync_commands(limit=capped_limit, requested_by=self.scheduler_id)
        result["outlook"] = self.database.enqueue_due_outlook_sync_commands(limit=capped_limit, requested_by=self.scheduler_id)
        try:
            result["stranded_capture_commands"] = self.database.repair_stranded_capture_commands(
                apply=True,
                confirm="stranded-capture-commands",
                min_age_seconds=60,
                limit=capped_limit,
            )
        except Exception as exc:
            result["stranded_capture_commands"] = {
                "applied": False,
                "affected_jobs": 0,
                "reset_jobs": 0,
                "enqueued": 0,
                "error": str(exc),
                "error_type": exc.__class__.__name__,
            }

        automation_policy = operator_automation.normalized_policy(_operator_automation_policy_from_settings())
        automation_interval = float(automation_policy.get("interval_seconds") or 1800)
        if bool(automation_policy.get("enabled")) and self._due(self._last_automation_at, automation_interval, force_due=force_due):
            result["automation"] = self._enqueue_command(
                routing_key=messaging.AUTOMATION_ROUTING_KEY,
                message_type="flux.operator.automation.run",
                payload={
                    "trigger": "scheduler",
                    "mode": str(automation_policy.get("mode") or "guarded"),
                    "limit": int(automation_policy.get("max_actions_per_run") or 25),
                    "requested_by": self.scheduler_id,
                },
                interval_seconds=automation_interval,
            )
            self._last_automation_at = time.monotonic()
        else:
            result["automation"] = {"queued": 0, "enabled": bool(automation_policy.get("enabled"))}

        governance_policy = governance.normalized_policy(_governance_policy_from_settings())
        governance_interval = float(governance_policy.get("interval_seconds") or 3600)
        if (
            not bool(automation_policy.get("enabled"))
            and bool(governance_policy.get("librarian_enabled"))
            and self._due(self._last_governance_at, governance_interval, force_due=force_due)
        ):
            mode = str(governance_policy.get("mode") or "shadow")
            if not bool(governance_policy.get("auto_apply_enabled")):
                mode = "shadow"
            result["governance"] = self._enqueue_command(
                routing_key=messaging.GOVERNANCE_ROUTING_KEY,
                message_type="flux.governance.run",
                payload={
                    "trigger": "scheduler",
                    "mode": mode,
                    "limit": int(governance_policy.get("max_actions_per_run") or 25),
                    "requested_by": self.scheduler_id,
                },
                interval_seconds=governance_interval,
            )
            self._last_governance_at = time.monotonic()
        else:
            result["governance"] = {"queued": 0, "enabled": bool(governance_policy.get("librarian_enabled"))}

        self.database.record_runtime_component_heartbeat(
            name=self.scheduler_id,
            status="running",
            metadata={"last_result": result},
        )
        return result

    def _enqueue_command(
        self,
        *,
        routing_key: str,
        message_type: str,
        payload: dict[str, Any],
        interval_seconds: float,
    ) -> dict[str, Any]:
        bucket = int(time.time() // max(1, int(interval_seconds or 1)))
        message_id = str(uuid5(NAMESPACE_URL, f"flux-llm-kb:scheduler:{routing_key}:{bucket}"))
        outbox = self.database.enqueue_message_outbox(
            message_id=message_id,
            exchange=messaging.COMMANDS_EXCHANGE,
            routing_key=routing_key,
            message_type=message_type,
            payload=payload,
            aggregate_type="event_scheduler",
            aggregate_id=f"{routing_key}:{bucket}",
        )
        return {"queued": 1 if outbox.get("status") in {"pending", "failed", "published"} else 0, "message_id": outbox.get("message_id")}

    @staticmethod
    def _due(last_run_at: float, interval_seconds: float, *, force_due: bool) -> bool:
        if force_due or last_run_at <= 0:
            return True
        return (time.monotonic() - last_run_at) >= max(1.0, float(interval_seconds or 1.0))


async def run_scheduler_loop(
    *,
    interval_seconds: float = 30.0,
    limit: int = 25,
    once: bool = False,
    scheduler: EventScheduler | None = None,
) -> dict[str, Any]:
    active_scheduler = scheduler or EventScheduler()
    runs = 0
    last_result: dict[str, Any] = {}
    while True:
        runs += 1
        last_result = active_scheduler.run_once(limit=limit, force_due=once)
        if once:
            return {"status": "stopped", "runs": runs, "last_result": last_result}
        await asyncio.sleep(max(1.0, float(interval_seconds or 30.0)))


def run_scheduler(*, interval_seconds: float = 30.0, limit: int = 25, once: bool = False) -> dict[str, Any]:
    return asyncio.run(run_scheduler_loop(interval_seconds=interval_seconds, limit=limit, once=once))
