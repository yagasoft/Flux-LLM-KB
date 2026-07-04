from __future__ import annotations

import asyncio
from typing import Any
from uuid import uuid4

from . import database, messaging


DEFAULT_EVENT_SUBSCRIBER_NAME = "audit"


class EventSubscriber:
    def __init__(
        self,
        *,
        subscriber_name: str = DEFAULT_EVENT_SUBSCRIBER_NAME,
        database_module: Any = database,
        worker_id: str | None = None,
    ) -> None:
        clean_name = str(subscriber_name or DEFAULT_EVENT_SUBSCRIBER_NAME).strip() or DEFAULT_EVENT_SUBSCRIBER_NAME
        self.subscriber_name = clean_name
        self.consumer_name = f"flux-kb-event-subscriber:{clean_name}"
        self.database = database_module
        self.worker_id = worker_id or f"{self.consumer_name}-{uuid4().hex[:8]}"

    def handle(self, message: messaging.FluxMessage) -> dict[str, Any]:
        should_process = self.database.begin_message_inbox(
            consumer_name=self.consumer_name,
            message_id=message.message_id,
            message_type=message.message_type,
            metadata={"routing_key": message.routing_key, "attempt": message.attempt, "subscriber": self.subscriber_name},
        )
        if not should_process:
            return {"status": "duplicate", "message_id": message.message_id, "acked": True}
        try:
            journal = self.database.record_event_journal(
                subscriber_name=self.subscriber_name,
                message_id=message.message_id,
                message_type=message.message_type,
                exchange=messaging.EVENTS_EXCHANGE,
                routing_key=message.routing_key,
                correlation_id=message.correlation_id,
                causation_id=message.causation_id,
                job_id=message.job_id,
                payload=message.to_broker_payload(),
                metadata={"worker_id": self.worker_id},
            )
            self.database.complete_message_inbox(
                consumer_name=self.consumer_name,
                message_id=message.message_id,
                status="handled",
                metadata={"journal": journal},
            )
            return {"status": "handled", "message_id": message.message_id, "acked": True, "journal": journal}
        except messaging.RetryableMessageError:
            raise
        except Exception as exc:
            self.database.complete_message_inbox(
                consumer_name=self.consumer_name,
                message_id=message.message_id,
                status="failed",
                error=str(exc),
                metadata={"error_type": exc.__class__.__name__},
            )
            raise


async def run_subscriber_loop(*, queue_name: str = messaging.EVENT_AUDIT_QUEUE, subscriber_name: str = DEFAULT_EVENT_SUBSCRIBER_NAME) -> dict[str, Any]:
    subscriber = EventSubscriber(subscriber_name=subscriber_name)
    consumer = messaging.RabbitMqConsumer()
    async with consumer:
        await consumer.consume(queue_name=queue_name, handler=lambda message: subscriber.handle(message))
    return {"status": "stopped", "queue": queue_name, "subscriber": subscriber_name}


def run_subscriber(*, queue_name: str = messaging.EVENT_AUDIT_QUEUE, subscriber_name: str = DEFAULT_EVENT_SUBSCRIBER_NAME) -> dict[str, Any]:
    return asyncio.run(run_subscriber_loop(queue_name=queue_name, subscriber_name=subscriber_name))
