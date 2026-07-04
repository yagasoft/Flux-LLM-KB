from __future__ import annotations

import asyncio
from typing import Any, Callable
from urllib import error, request

from . import callbacks, database, messaging
from .settings import SettingsService


DEFAULT_CALLBACK_WORKER_NAME = "flux-kb-callback-worker"


class CallbackDispatcher:
    def __init__(
        self,
        *,
        database_module: Any = database,
        http_post: Callable[..., dict[str, Any]] | None = None,
        worker_id: str = DEFAULT_CALLBACK_WORKER_NAME,
        settings_service: SettingsService | None = None,
    ) -> None:
        self.database = database_module
        self.http_post = http_post or _post_callback
        self.worker_id = worker_id
        self.settings = settings_service or SettingsService()

    def handle(self, message: messaging.FluxMessage) -> dict[str, Any]:
        should_process = self.database.begin_message_inbox(
            consumer_name=self.worker_id,
            message_id=message.message_id,
            message_type=message.message_type,
            metadata={"routing_key": message.routing_key},
        )
        if not should_process:
            return {"status": "duplicate", "message_id": message.message_id, "acked": True}

        try:
            result = self._dispatch(message)
            self.database.complete_message_inbox(
                consumer_name=self.worker_id,
                message_id=message.message_id,
                status="handled" if not result.get("retryable") else "failed",
                error=result.get("error"),
                metadata={"result": result},
            )
            if result.get("retryable"):
                raise messaging.RetryableMessageError(str(result.get("error") or "callback retryable"))
            return {"status": "handled", "message_id": message.message_id, "acked": True, "result": result}
        except messaging.RetryableMessageError:
            raise
        except Exception as exc:
            self.database.complete_message_inbox(
                consumer_name=self.worker_id,
                message_id=message.message_id,
                status="failed",
                error=str(exc),
                metadata={"error_type": exc.__class__.__name__},
            )
            raise

    def _dispatch(self, message: messaging.FluxMessage) -> dict[str, Any]:
        delivery_id = str(message.payload.get("callback_delivery_id") or "").strip()
        if not delivery_id:
            raise ValueError("callback dispatch command requires callback_delivery_id")
        delivery = self.database.claim_callback_delivery(
            delivery_id=delivery_id,
            worker_id=self.worker_id,
            broker_message_id=message.message_id,
        )
        if not delivery:
            return {"status": "not_claimable", "callback_delivery_id": delivery_id, "retryable": False}

        allowlist = self.settings.resolve("callbacks.allowlist").raw_value or []
        policy = callbacks.CallbackPolicy(allowlist=tuple(str(item) for item in allowlist))
        decision = callbacks.validate_callback_url(str(delivery["callback_url"]), policy)
        if not decision.allowed:
            self.database.complete_callback_delivery(
                delivery_id=delivery_id,
                status="blocked",
                error=decision.reason,
            )
            return {"status": "blocked", "callback_delivery_id": delivery_id, "error": decision.reason, "retryable": False}

        secret = str(self.settings.resolve("callbacks.signing_secret").raw_value or "").strip()
        if not secret:
            error_message = "callback signing secret is not configured"
            self.database.complete_callback_delivery(
                delivery_id=delivery_id,
                status="blocked",
                error=error_message,
            )
            return {"status": "blocked", "callback_delivery_id": delivery_id, "error": error_message, "retryable": False}

        body = callbacks.build_callback_body(dict(delivery.get("payload") or {}))
        headers = callbacks.sign_callback(
            body=body,
            secret=secret,
            message_id=str(delivery.get("idempotency_key") or delivery.get("message_id") or message.message_id),
        )
        timeout_seconds = int(self.settings.resolve("callbacks.timeout_seconds").raw_value or 5)
        try:
            response = self.http_post(str(delivery["callback_url"]), body=body, headers=headers, timeout_seconds=timeout_seconds)
        except Exception as exc:
            return self._record_retry_or_failure(delivery, status_code=None, error=str(exc))

        status_code = int(response.get("status_code") or 0)
        if 200 <= status_code < 300:
            self.database.complete_callback_delivery(
                delivery_id=delivery_id,
                status="delivered",
                status_code=status_code,
            )
            return {"status": "delivered", "callback_delivery_id": delivery_id, "status_code": status_code, "retryable": False}
        if status_code in {408, 409, 425, 429} or status_code >= 500:
            return self._record_retry_or_failure(delivery, status_code=status_code, error=f"HTTP {status_code}")
        self.database.complete_callback_delivery(
            delivery_id=delivery_id,
            status="failed",
            status_code=status_code,
            error=f"HTTP {status_code}",
        )
        return {"status": "failed", "callback_delivery_id": delivery_id, "status_code": status_code, "retryable": False}

    def _record_retry_or_failure(self, delivery: dict[str, Any], *, status_code: int | None, error: str) -> dict[str, Any]:
        delivery_limit = messaging.RabbitMqConfig.from_env().delivery_limit
        delivery_id = str(delivery["id"])
        if int(delivery.get("attempts") or 0) >= delivery_limit:
            self.database.complete_callback_delivery(
                delivery_id=delivery_id,
                status="failed",
                status_code=status_code,
                error=error,
            )
            return {"status": "failed", "callback_delivery_id": delivery_id, "status_code": status_code, "error": error, "retryable": False}
        self.database.complete_callback_delivery(
            delivery_id=delivery_id,
            status="retrying",
            status_code=status_code,
            error=error,
        )
        return {"status": "retrying", "callback_delivery_id": delivery_id, "status_code": status_code, "error": error, "retryable": True}


def _post_callback(url: str, *, body: bytes, headers: dict[str, str], timeout_seconds: int) -> dict[str, Any]:
    req = request.Request(url, data=body, headers=headers, method="POST")
    try:
        with request.urlopen(req, timeout=timeout_seconds) as response:
            response.read(4096)
            return {"status_code": int(response.status)}
    except error.HTTPError as exc:
        exc.read(4096)
        return {"status_code": int(exc.code)}


async def run_dispatcher_loop(*, queue_name: str = "flux.callbacks.dispatch") -> dict[str, Any]:
    dispatcher = CallbackDispatcher()
    consumer = messaging.RabbitMqConsumer()
    async with consumer:
        await consumer.consume(queue_name=queue_name, handler=lambda message: dispatcher.handle(message))
    return {"status": "stopped", "queue": queue_name}


def run_dispatcher(*, queue_name: str = "flux.callbacks.dispatch") -> dict[str, Any]:
    return asyncio.run(run_dispatcher_loop(queue_name=queue_name))
