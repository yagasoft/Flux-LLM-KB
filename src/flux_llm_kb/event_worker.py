from __future__ import annotations

import asyncio
from typing import Any
from uuid import uuid4

from . import database, messaging
from .gpu_scheduler import process_gpu_eviction_request
from .service import KnowledgeService


DEFAULT_CONSUMER_NAME = "flux-kb-event-worker"


class EventWorker:
    def __init__(
        self,
        *,
        service: KnowledgeService | None = None,
        database_module: Any = database,
        consumer_name: str = DEFAULT_CONSUMER_NAME,
        worker_id: str | None = None,
    ) -> None:
        self.service = service or KnowledgeService()
        self.database = database_module
        self.consumer_name = consumer_name
        self.worker_id = worker_id or f"{consumer_name}-{uuid4().hex[:8]}"

    def handle(self, message: messaging.FluxMessage) -> dict[str, Any]:
        should_process = self.database.begin_message_inbox(
            consumer_name=self.consumer_name,
            message_id=message.message_id,
            message_type=message.message_type,
            metadata={"routing_key": message.routing_key, "attempt": message.attempt},
        )
        if not should_process:
            return {"status": "duplicate", "message_id": message.message_id, "acked": True}
        try:
            result = self._dispatch(message)
            if result.get("retryable"):
                self.database.complete_message_inbox(
                    consumer_name=self.consumer_name,
                    message_id=message.message_id,
                    status="failed",
                    error=str(result.get("status") or result.get("process_status") or "retryable"),
                    metadata={"result": result},
                )
                raise messaging.RetryableMessageError(str(result.get("status") or "retryable"))
            self.database.complete_message_inbox(
                consumer_name=self.consumer_name,
                message_id=message.message_id,
                status="handled",
                metadata={"result": result},
            )
            return {"status": "handled", "message_id": message.message_id, "acked": True, "result": result}
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

    def _dispatch(self, message: messaging.FluxMessage) -> dict[str, Any]:
        if message.routing_key in {
            messaging.CORPUS_PROCESS_ROUTING_KEY,
            messaging.CORPUS_HOST_AGENT_PROCESS_ROUTING_KEY,
            messaging.SEARCH_INDEX_PROCESS_ROUTING_KEY,
        }:
            job_id = str(message.payload.get("job_id") or message.job_id or "").strip()
            if not job_id:
                raise ValueError("corpus/search-index command requires job_id")
            return self.service.process_corpus_job_by_id(
                job_id=job_id,
                worker_id=self.worker_id,
                broker_message_id=message.message_id,
                correlation_id=message.correlation_id,
                causation_id=message.causation_id,
            )
        if message.routing_key == messaging.MAIL_IMAP_SYNC_ROUTING_KEY:
            from . import mail_ingestion

            run_id = str(message.payload.get("run_id") or "").strip()
            if not run_id:
                queued = self.database.enqueue_due_imap_sync_commands(limit=1, requested_by=self.worker_id)
                return {"status": "imap_sync_enqueued", **queued}
            return mail_ingestion.process_imap_sync_run(
                run_id=run_id,
                worker_id=self.worker_id,
                broker_message_id=message.message_id,
            )
        if message.routing_key == messaging.OUTLOOK_SYNC_ROUTING_KEY:
            from . import outlook_host

            request_id = str(message.payload.get("request_id") or "").strip()
            if not request_id:
                queued = self.database.enqueue_due_outlook_sync_commands(limit=1, requested_by=self.worker_id)
                return {"status": "outlook_sync_enqueued", **queued}
            return outlook_host.process_request_by_id(
                request_id=request_id,
                host_id=self.worker_id,
                broker_message_id=message.message_id,
            )
        if message.routing_key == messaging.AUTOMATION_ROUTING_KEY:
            return self.service.run_operator_automation(
                mode=str(message.payload.get("mode") or "guarded"),
                trigger=str(message.payload.get("trigger") or "broker"),
                actor=str(message.payload.get("requested_by") or self.worker_id),
                limit=int(message.payload.get("limit") or 25),
                dry_run=bool(message.payload.get("dry_run")),
            )
        if message.routing_key == messaging.GOVERNANCE_ROUTING_KEY:
            return self.service.run_governance(
                mode=str(message.payload.get("mode") or "shadow"),
                actor=str(message.payload.get("requested_by") or self.worker_id),
                limit=int(message.payload.get("limit") or 25),
            )
        if message.routing_key == messaging.RUNTIME_CONTROL_ROUTING_KEY:
            component = str(message.payload.get("component") or "").strip() or None
            return {
                "status": "acknowledged",
                **self.database.ack_runtime_control_requests(component=component, actor=self.worker_id),
            }
        if message.routing_key == messaging.GPU_EVICTION_ROUTING_KEY:
            eviction_id = str(message.payload.get("eviction_id") or "").strip()
            if not eviction_id:
                raise ValueError("GPU eviction command requires eviction_id")
            return process_gpu_eviction_request(
                eviction_id=eviction_id,
                worker_id=self.worker_id,
                broker_message_id=message.message_id,
                correlation_id=message.correlation_id,
                causation_id=message.causation_id,
            )
        raise ValueError(f"unsupported routing key: {message.routing_key}")


async def run_worker_loop(*, queue_name: str = messaging.COMMAND_CORPUS_QUEUE, worker_id: str | None = None) -> dict[str, Any]:
    worker = EventWorker(worker_id=worker_id)
    consumer = messaging.RabbitMqConsumer()
    try:
        await consumer.consume(queue_name=queue_name, handler=lambda message: worker.handle(message))
    finally:
        close = getattr(consumer, "close", None)
        if close is not None:
            await close()
    return {"status": "stopped", "queue": queue_name}


def run_worker(*, queue_name: str = messaging.COMMAND_CORPUS_QUEUE, worker_id: str | None = None) -> dict[str, Any]:
    return asyncio.run(run_worker_loop(queue_name=queue_name, worker_id=worker_id))
