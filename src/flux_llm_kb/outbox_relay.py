from __future__ import annotations

import asyncio
from typing import Any
from uuid import uuid4

from . import database
from .messaging import RabbitMqPublisher


class OutboxRelay:
    def __init__(
        self,
        *,
        database_module: Any = database,
        publisher: Any | None = None,
        worker_id: str | None = None,
    ) -> None:
        self.database = database_module
        self.publisher = publisher or RabbitMqPublisher()
        self.worker_id = worker_id or f"outbox-relay-{uuid4().hex[:8]}"

    async def run_once(self, *, limit: int = 100) -> dict[str, int]:
        rows = self.database.claim_pending_outbox_messages(limit=limit, worker_id=self.worker_id)
        published = 0
        failed = 0
        for row in rows:
            outbox_id = str(row["id"])
            try:
                result = await self.publisher.publish(
                    exchange=str(row["exchange"]),
                    routing_key=str(row["routing_key"]),
                    message=dict(row["payload"] or {}),
                    headers=dict(row.get("headers") or {}),
                )
                self.database.mark_outbox_message_published(
                    outbox_id=outbox_id,
                    broker_message_id=str(result.get("message_id") or ""),
                )
                published += 1
            except Exception as exc:
                self.database.mark_outbox_message_failed(outbox_id=outbox_id, error=str(exc))
                failed += 1
        return {"claimed": len(rows), "published": published, "failed": failed}


async def run_relay_loop(
    *,
    interval_seconds: float = 1.0,
    limit: int = 100,
    once: bool = False,
    relay: OutboxRelay | None = None,
) -> dict[str, Any]:
    active_relay = relay or OutboxRelay()
    runs = 0
    last_result: dict[str, Any] = {"claimed": 0, "published": 0, "failed": 0}
    while True:
        runs += 1
        last_result = await active_relay.run_once(limit=limit)
        if once:
            return {"status": "stopped", "runs": runs, **last_result}
        await asyncio.sleep(max(0.1, float(interval_seconds or 1.0)))
