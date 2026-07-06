from __future__ import annotations

import asyncio
from dataclasses import dataclass, field
from datetime import UTC, datetime
import inspect
import json
import os
from typing import Any, Awaitable, Callable
from urllib.parse import quote, urlparse
from urllib.request import Request, urlopen
from uuid import uuid4

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


DEFAULT_MESSAGE_SCHEMA_VERSION = 1
DEFAULT_MAX_PAYLOAD_BYTES = 64 * 1024
DEFAULT_RABBITMQ_URL = "amqp://flux:flux@rabbitmq:5672/flux"
DEFAULT_RABBITMQ_MANAGEMENT_URL = "http://127.0.0.1:15672"

COMMANDS_EXCHANGE = "flux.commands"
EVENTS_EXCHANGE = "flux.events"
CALLBACKS_EXCHANGE = "flux.callbacks"
RETRY_EXCHANGE = "flux.retry"
DEAD_EXCHANGE = "flux.dead"

CORPUS_PROCESS_ROUTING_KEY = "corpus.process"
CORPUS_HOST_AGENT_PROCESS_ROUTING_KEY = "corpus.host_agent.process"
SEARCH_INDEX_PROCESS_ROUTING_KEY = "search_index.process"
MAIL_IMAP_SYNC_ROUTING_KEY = "mail.imap.sync"
OUTLOOK_SYNC_ROUTING_KEY = "mail.outlook.sync"
RUNTIME_CONTROL_ROUTING_KEY = "runtime.control.apply"
AUTOMATION_ROUTING_KEY = "operator.automation.run"
GOVERNANCE_ROUTING_KEY = "governance.run"
GPU_EVICTION_ROUTING_KEY = "gpu.eviction.request"
CALLBACK_DISPATCH_ROUTING_KEY = "callback.dispatch"

COMMAND_CORPUS_QUEUE = "flux.commands.corpus"
COMMAND_CORPUS_HOST_AGENT_QUEUE = "flux.commands.corpus_host_agent"
COMMAND_SEARCH_INDEX_QUEUE = "flux.commands.search_index"
COMMAND_MAIL_IMAP_QUEUE = "flux.commands.mail_imap"
COMMAND_OUTLOOK_QUEUE = "flux.commands.outlook"
COMMAND_RUNTIME_CONTROL_QUEUE = "flux.commands.runtime_control"
COMMAND_AUTOMATION_QUEUE = "flux.commands.automation"
COMMAND_GOVERNANCE_QUEUE = "flux.commands.governance"
COMMAND_GPU_EVICTION_QUEUE = "flux.commands.gpu_eviction"

EVENT_AUDIT_QUEUE = "flux.events.audit"
EVENT_DASHBOARD_QUEUE = "flux.events.dashboard"
EVENT_DIAGNOSTICS_QUEUE = "flux.events.diagnostics"
EVENT_SUBSCRIBER_QUEUES = (EVENT_AUDIT_QUEUE, EVENT_DASHBOARD_QUEUE, EVENT_DIAGNOSTICS_QUEUE)

COMMAND_EXCHANGE_KEY = "commands"
EVENTS_EXCHANGE_KEY = "events"
CALLBACKS_EXCHANGE_KEY = "callbacks"
RETRY_EXCHANGE_KEY = "retry"
DEAD_EXCHANGE_KEY = "dead"

_RAW_CONTENT_KEYS = {
    "raw",
    "raw_body",
    "raw_content",
    "raw_message",
    "body",
    "text_body",
    "html_body",
    "message_body",
    "attachment_payload",
    "attachment_bytes",
    "content",
}


class FluxMessage(BaseModel):
    model_config = ConfigDict(extra="forbid")

    message_id: str = Field(default_factory=lambda: uuid4().hex)
    correlation_id: str | None = None
    causation_id: str | None = None
    message_type: str
    schema_version: int = DEFAULT_MESSAGE_SCHEMA_VERSION
    job_id: str | None = None
    routing_key: str
    attempt: int = 0
    created_at: datetime = Field(default_factory=lambda: datetime.now(UTC))
    payload: dict[str, Any] = Field(default_factory=dict)

    @field_validator("message_id", "message_type", "routing_key")
    @classmethod
    def _not_blank(cls, value: str) -> str:
        clean = str(value or "").strip()
        if not clean:
            raise ValueError("field must not be blank")
        return clean

    @field_validator("attempt")
    @classmethod
    def _attempt_is_non_negative(cls, value: int) -> int:
        parsed = int(value or 0)
        if parsed < 0:
            raise ValueError("attempt must be >= 0")
        return parsed

    @field_validator("payload")
    @classmethod
    def _payload_is_safe(cls, value: dict[str, Any]) -> dict[str, Any]:
        _reject_raw_content(value)
        return value

    @model_validator(mode="after")
    def _correlation_defaults(self) -> "FluxMessage":
        if not self.correlation_id:
            self.correlation_id = self.message_id
        return self

    def to_broker_payload(self) -> dict[str, Any]:
        data = self.model_dump(mode="json")
        return data


def build_message(
    *,
    message_id: str | None = None,
    message_type: str,
    routing_key: str,
    payload: dict[str, Any] | None = None,
    job_id: str | None = None,
    correlation_id: str | None = None,
    causation_id: str | None = None,
    attempt: int = 0,
    created_at: datetime | None = None,
    max_payload_bytes: int = DEFAULT_MAX_PAYLOAD_BYTES,
) -> FluxMessage:
    body = payload or {}
    message = FluxMessage(
        message_id=message_id or uuid4().hex,
        message_type=message_type,
        routing_key=routing_key,
        payload=body,
        job_id=job_id,
        correlation_id=correlation_id,
        causation_id=causation_id,
        attempt=attempt,
        created_at=created_at or datetime.now(UTC),
    )
    encoded = json.dumps(message.payload, sort_keys=True, separators=(",", ":"), default=str).encode("utf-8")
    if len(encoded) > max(1, int(max_payload_bytes or DEFAULT_MAX_PAYLOAD_BYTES)):
        raise ValueError(f"message payload exceeds {max_payload_bytes} bytes")
    return message


def _reject_raw_content(value: Any, *, path: str = "payload") -> None:
    if isinstance(value, dict):
        for key, item in value.items():
            clean_key = str(key).strip().lower()
            if clean_key in _RAW_CONTENT_KEYS:
                raise ValueError(f"broker messages must not contain raw content at {path}.{key}")
            _reject_raw_content(item, path=f"{path}.{key}")
    elif isinstance(value, list):
        for index, item in enumerate(value):
            _reject_raw_content(item, path=f"{path}[{index}]")


@dataclass(frozen=True)
class RabbitMqConfig:
    url: str = DEFAULT_RABBITMQ_URL
    management_url: str = DEFAULT_RABBITMQ_MANAGEMENT_URL
    username: str = "flux"
    password: str = "flux"
    publisher_confirms: bool = True
    mandatory_routing: bool = True
    prefetch_count: int = 4
    retry_delay_ms: int = 30_000
    delivery_limit: int = 8

    @classmethod
    def from_env(cls) -> "RabbitMqConfig":
        return cls(
            url=os.environ.get("FLUX_KB_RABBITMQ_URL", DEFAULT_RABBITMQ_URL),
            management_url=os.environ.get("FLUX_KB_RABBITMQ_MANAGEMENT_URL", DEFAULT_RABBITMQ_MANAGEMENT_URL),
            username=os.environ.get("FLUX_KB_RABBITMQ_USERNAME", "flux"),
            password=os.environ.get("FLUX_KB_RABBITMQ_PASSWORD", "flux"),
            prefetch_count=_int_env("FLUX_KB_RABBITMQ_PREFETCH", 4, minimum=1),
            retry_delay_ms=_int_env("FLUX_KB_RABBITMQ_RETRY_DELAY_MS", 30_000, minimum=1000),
            delivery_limit=_int_env("FLUX_KB_RABBITMQ_DELIVERY_LIMIT", 8, minimum=1),
        )


@dataclass(frozen=True)
class ExchangeSpec:
    name: str
    kind: str = "topic"
    durable: bool = True


@dataclass(frozen=True)
class QueueSpec:
    name: str
    exchange: str
    routing_key: str
    durable: bool = True
    arguments: dict[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class RabbitMqTopology:
    exchanges: dict[str, ExchangeSpec]
    queues: tuple[QueueSpec, ...]


def default_topology(config: RabbitMqConfig | None = None) -> RabbitMqTopology:
    cfg = config or RabbitMqConfig.from_env()
    exchanges = {
        COMMAND_EXCHANGE_KEY: ExchangeSpec(COMMANDS_EXCHANGE),
        EVENTS_EXCHANGE_KEY: ExchangeSpec(EVENTS_EXCHANGE),
        CALLBACKS_EXCHANGE_KEY: ExchangeSpec(CALLBACKS_EXCHANGE),
        RETRY_EXCHANGE_KEY: ExchangeSpec(RETRY_EXCHANGE),
        DEAD_EXCHANGE_KEY: ExchangeSpec(DEAD_EXCHANGE),
    }
    commands = (
        (COMMAND_CORPUS_QUEUE, CORPUS_PROCESS_ROUTING_KEY, "flux.retry.corpus", "retry.corpus"),
        (
            COMMAND_CORPUS_HOST_AGENT_QUEUE,
            CORPUS_HOST_AGENT_PROCESS_ROUTING_KEY,
            "flux.retry.corpus_host_agent",
            "retry.corpus_host_agent",
        ),
        (COMMAND_SEARCH_INDEX_QUEUE, SEARCH_INDEX_PROCESS_ROUTING_KEY, "flux.retry.search_index", "retry.search_index"),
        (COMMAND_MAIL_IMAP_QUEUE, MAIL_IMAP_SYNC_ROUTING_KEY, "flux.retry.mail_imap", "retry.mail_imap"),
        (COMMAND_OUTLOOK_QUEUE, OUTLOOK_SYNC_ROUTING_KEY, "flux.retry.outlook", "retry.outlook"),
        (COMMAND_RUNTIME_CONTROL_QUEUE, RUNTIME_CONTROL_ROUTING_KEY, "flux.retry.runtime_control", "retry.runtime_control"),
        (COMMAND_AUTOMATION_QUEUE, AUTOMATION_ROUTING_KEY, "flux.retry.automation", "retry.automation"),
        (COMMAND_GOVERNANCE_QUEUE, GOVERNANCE_ROUTING_KEY, "flux.retry.governance", "retry.governance"),
        (COMMAND_GPU_EVICTION_QUEUE, GPU_EVICTION_ROUTING_KEY, "flux.retry.gpu_eviction", "retry.gpu_eviction"),
    )
    queues: list[QueueSpec] = []
    for queue_name, routing_key, retry_queue, retry_key in commands:
        queues.append(
            QueueSpec(
                name=queue_name,
                exchange=COMMANDS_EXCHANGE,
                routing_key=routing_key,
                arguments=_quorum_arguments(
                    delivery_limit=cfg.delivery_limit,
                    dead_letter_exchange=RETRY_EXCHANGE,
                    dead_letter_routing_key=retry_key,
                ),
            )
        )
        queues.append(
            QueueSpec(
                name=retry_queue,
                exchange=RETRY_EXCHANGE,
                routing_key=retry_key,
                arguments=_quorum_arguments(
                    delivery_limit=cfg.delivery_limit,
                    dead_letter_exchange=COMMANDS_EXCHANGE,
                    dead_letter_routing_key=routing_key,
                    message_ttl=cfg.retry_delay_ms,
                ),
            )
        )
    queues.append(
        QueueSpec(
            name="flux.callbacks.dispatch",
            exchange=CALLBACKS_EXCHANGE,
            routing_key=CALLBACK_DISPATCH_ROUTING_KEY,
            arguments=_quorum_arguments(
                delivery_limit=cfg.delivery_limit,
                dead_letter_exchange=RETRY_EXCHANGE,
                dead_letter_routing_key="retry.callback",
            ),
        )
    )
    queues.append(
        QueueSpec(
            name="flux.retry.callback",
            exchange=RETRY_EXCHANGE,
            routing_key="retry.callback",
            arguments=_quorum_arguments(
                delivery_limit=cfg.delivery_limit,
                dead_letter_exchange=CALLBACKS_EXCHANGE,
                dead_letter_routing_key=CALLBACK_DISPATCH_ROUTING_KEY,
                message_ttl=cfg.retry_delay_ms,
            ),
        )
    )
    event_subscribers = (
        (EVENT_AUDIT_QUEUE, "audit"),
        (EVENT_DASHBOARD_QUEUE, "dashboard"),
        (EVENT_DIAGNOSTICS_QUEUE, "diagnostics"),
    )
    for queue_name, subscriber in event_subscribers:
        retry_key = f"retry.events.{subscriber}"
        retry_queue = f"flux.retry.events.{subscriber}"
        queues.append(
            QueueSpec(
                name=queue_name,
                exchange=EVENTS_EXCHANGE,
                routing_key="#",
                arguments=_quorum_arguments(
                    delivery_limit=cfg.delivery_limit,
                    dead_letter_exchange=RETRY_EXCHANGE,
                    dead_letter_routing_key=retry_key,
                ),
            )
        )
        queues.append(
            QueueSpec(
                name=retry_queue,
                exchange=RETRY_EXCHANGE,
                routing_key=retry_key,
                arguments=_quorum_arguments(
                    delivery_limit=cfg.delivery_limit,
                    dead_letter_exchange="",
                    dead_letter_routing_key=queue_name,
                    message_ttl=cfg.retry_delay_ms,
                ),
            )
        )
    queues.append(
        QueueSpec(
            name="flux.dead.letters",
            exchange=DEAD_EXCHANGE,
            routing_key="#",
            arguments=_quorum_arguments(delivery_limit=max(cfg.delivery_limit, 20)),
        )
    )
    return RabbitMqTopology(exchanges=exchanges, queues=tuple(queues))


def _quorum_arguments(
    *,
    delivery_limit: int,
    dead_letter_exchange: str | None = None,
    dead_letter_routing_key: str | None = None,
    message_ttl: int | None = None,
) -> dict[str, Any]:
    arguments: dict[str, Any] = {
        "x-queue-type": "quorum",
        "x-delivery-limit": max(1, int(delivery_limit or 1)),
    }
    if dead_letter_exchange is not None:
        arguments["x-dead-letter-exchange"] = dead_letter_exchange
    if dead_letter_routing_key is not None:
        arguments["x-dead-letter-routing-key"] = dead_letter_routing_key
    if message_ttl is not None:
        arguments["x-message-ttl"] = max(1000, int(message_ttl))
    return arguments


class RabbitMqPublisher:
    def __init__(self, config: RabbitMqConfig | None = None, topology: RabbitMqTopology | None = None) -> None:
        self.config = config or RabbitMqConfig.from_env()
        self.topology = topology or default_topology(self.config)
        self._connection: Any | None = None
        self._channel: Any | None = None
        self._exchanges: dict[str, Any] = {}

    async def __aenter__(self) -> "RabbitMqPublisher":
        await self.connect()
        return self

    async def __aexit__(self, *_exc_info: Any) -> None:
        await self.close()

    async def connect(self) -> None:
        aio_pika = _load_aio_pika()
        self._connection = await aio_pika.connect_robust(self.config.url)
        self._channel = await self._connection.channel(publisher_confirms=self.config.publisher_confirms)
        await declare_topology(self._channel, self.topology)
        self._exchanges = {
            spec.name: await self._channel.get_exchange(spec.name, ensure=True)
            for spec in self.topology.exchanges.values()
        }

    async def close(self) -> None:
        if self._connection is not None:
            await self._connection.close()
        self._connection = None
        self._channel = None
        self._exchanges = {}

    async def publish(
        self,
        *,
        exchange: str,
        routing_key: str,
        message: dict[str, Any] | FluxMessage,
        headers: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        if self._channel is None:
            await self.connect()
        aio_pika = _load_aio_pika()
        payload = message.to_broker_payload() if isinstance(message, FluxMessage) else dict(message)
        body = json.dumps(payload, sort_keys=True, separators=(",", ":"), default=str).encode("utf-8")
        broker_message = aio_pika.Message(
            body,
            content_type="application/json",
            delivery_mode=aio_pika.DeliveryMode.PERSISTENT,
            message_id=str(payload.get("message_id") or uuid4().hex),
            correlation_id=str(payload.get("correlation_id") or payload.get("message_id") or ""),
            type=str(payload.get("message_type") or ""),
            timestamp=datetime.now(UTC),
            headers=headers or {},
        )
        exchange_obj = self._exchanges.get(exchange)
        if exchange_obj is None:
            exchange_obj = await self._channel.get_exchange(exchange, ensure=True)
            self._exchanges[exchange] = exchange_obj
        await exchange_obj.publish(broker_message, routing_key=routing_key, mandatory=self.config.mandatory_routing)
        return {"message_id": broker_message.message_id}


class RabbitMqConsumer:
    def __init__(self, config: RabbitMqConfig | None = None, topology: RabbitMqTopology | None = None) -> None:
        self.config = config or RabbitMqConfig.from_env()
        self.topology = topology or default_topology(self.config)
        self._connection: Any | None = None
        self._channel: Any | None = None

    async def __aenter__(self) -> "RabbitMqConsumer":
        await self.connect()
        return self

    async def __aexit__(self, *_exc_info: Any) -> None:
        await self.close()

    async def connect(self) -> None:
        aio_pika = _load_aio_pika()
        self._connection = await aio_pika.connect_robust(self.config.url)
        self._channel = await self._connection.channel()
        await self._channel.set_qos(prefetch_count=self.config.prefetch_count)
        await declare_topology(self._channel, self.topology)

    async def close(self) -> None:
        if self._connection is not None:
            await self._connection.close()
        self._connection = None
        self._channel = None

    async def consume(
        self,
        *,
        queue_name: str,
        handler: Callable[[FluxMessage], Awaitable[None] | None],
        reconnect_delay_seconds: float = 1.0,
    ) -> None:
        reconnect_delay = max(0.0, float(reconnect_delay_seconds))
        while True:
            try:
                if self._channel is None:
                    await self.connect()
                queue = await self._channel.get_queue(queue_name, ensure=True)

                async with queue.iterator() as iterator:
                    async for incoming in iterator:
                        try:
                            payload = json.loads(incoming.body.decode("utf-8"))
                            message = FluxMessage.model_validate(payload)
                            if inspect.iscoroutinefunction(handler):
                                result = handler(message)
                            else:
                                result = await asyncio.to_thread(handler, message)
                            if inspect.isawaitable(result):
                                await result
                        except RetryableMessageError:
                            if await self._dead_letter_if_delivery_limit_reached(incoming, queue_name=queue_name):
                                continue
                            await incoming.reject(requeue=False)
                        except Exception:
                            if await self._dead_letter_if_delivery_limit_reached(incoming, queue_name=queue_name):
                                continue
                            await incoming.reject(requeue=False)
                        else:
                            await incoming.ack()
            except asyncio.CancelledError:
                raise
            except Exception:
                await self.close()
                if reconnect_delay:
                    await asyncio.sleep(reconnect_delay)
                continue

            await self.close()
            if reconnect_delay:
                await asyncio.sleep(reconnect_delay)

    async def _dead_letter_if_delivery_limit_reached(self, incoming: Any, *, queue_name: str) -> bool:
        if _x_death_count(incoming) < self.config.delivery_limit:
            return False
        aio_pika = _load_aio_pika()
        if self._channel is None:
            return False
        exchange = await self._channel.get_exchange(DEAD_EXCHANGE, ensure=True)
        headers = dict(getattr(incoming, "headers", None) or {})
        headers["flux-dead-letter-reason"] = "delivery_limit_reached"
        headers["flux-source-queue"] = queue_name
        message = aio_pika.Message(
            incoming.body,
            content_type=getattr(incoming, "content_type", None) or "application/json",
            delivery_mode=aio_pika.DeliveryMode.PERSISTENT,
            message_id=getattr(incoming, "message_id", None),
            correlation_id=getattr(incoming, "correlation_id", None),
            type=getattr(incoming, "type", None),
            timestamp=datetime.now(UTC),
            headers=headers,
        )
        await exchange.publish(message, routing_key=f"{queue_name}.poison", mandatory=False)
        await incoming.ack()
        return True


class RetryableMessageError(RuntimeError):
    pass


async def declare_topology(channel: Any, topology: RabbitMqTopology) -> None:
    aio_pika = _load_aio_pika()
    for spec in topology.exchanges.values():
        exchange_type = getattr(aio_pika.ExchangeType, spec.kind.upper(), aio_pika.ExchangeType.TOPIC)
        await channel.declare_exchange(spec.name, exchange_type, durable=spec.durable)
    for queue in topology.queues:
        queue_obj = await channel.declare_queue(queue.name, durable=queue.durable, arguments=queue.arguments)
        exchange = await channel.get_exchange(queue.exchange, ensure=True)
        await queue_obj.bind(exchange, routing_key=queue.routing_key)


def _load_aio_pika() -> Any:
    try:
        import aio_pika
    except ImportError as exc:  # pragma: no cover - exercised only without optional runtime dependency
        raise RuntimeError("RabbitMQ support requires aio-pika. Install project dependencies or the Docker image.") from exc
    return aio_pika


def management_queue_status(config: RabbitMqConfig | None = None, *, timeout_seconds: float = 1.5) -> dict[str, Any]:
    cfg = config or RabbitMqConfig.from_env()
    vhost = _amqp_vhost(cfg.url)
    api_url = f"{cfg.management_url.rstrip('/')}/api/queues/{quote(vhost, safe='')}"
    credentials = f"{cfg.username}:{cfg.password}".encode("utf-8")
    import base64

    req = Request(api_url, headers={"Authorization": f"Basic {base64.b64encode(credentials).decode('ascii')}"})
    with urlopen(req, timeout=timeout_seconds) as response:
        queues = json.loads(response.read().decode("utf-8"))
    items = [
        {
            "name": str(queue.get("name") or ""),
            "messages_ready": int(queue.get("messages_ready") or 0),
            "messages_unacknowledged": int(queue.get("messages_unacknowledged") or 0),
            "messages": int(queue.get("messages") or 0),
            "consumers": int(queue.get("consumers") or 0),
            "state": str(queue.get("state") or "unknown"),
        }
        for queue in queues
        if isinstance(queue, dict)
    ]
    totals = {
        "messages_ready": sum(item["messages_ready"] for item in items),
        "messages_unacknowledged": sum(item["messages_unacknowledged"] for item in items),
        "messages": sum(item["messages"] for item in items),
        "consumers": sum(item["consumers"] for item in items),
    }
    topology = default_topology(cfg)
    live_queue_names = {item["name"] for item in items}
    required_queue_names = [queue.name for queue in topology.queues]
    missing_required = [name for name in required_queue_names if name not in live_queue_names]
    missing_event_subscribers = [name for name in EVENT_SUBSCRIBER_QUEUES if name not in live_queue_names]
    event_subscribers = [
        {
            "name": name,
            "present": name in live_queue_names,
            "consumers": next((item["consumers"] for item in items if item["name"] == name), 0),
        }
        for name in EVENT_SUBSCRIBER_QUEUES
    ]
    return {
        "available": True,
        "management_url": cfg.management_url,
        "vhost": vhost,
        "totals": totals,
        "queues": items,
        "topology": {
            "required_queues": required_queue_names,
            "missing_required_queues": missing_required,
            "required_event_subscribers": list(EVENT_SUBSCRIBER_QUEUES),
            "missing_event_subscribers": missing_event_subscribers,
            "event_subscribers": event_subscribers,
            "ok": not missing_required,
        },
    }


def _amqp_vhost(url: str) -> str:
    parsed = urlparse(url)
    path = (parsed.path or "/").lstrip("/")
    return path or "/"


def _x_death_count(incoming: Any) -> int:
    headers = dict(getattr(incoming, "headers", None) or {})
    x_death = headers.get("x-death") or []
    counts: list[int] = []
    if isinstance(x_death, list):
        for item in x_death:
            if isinstance(item, dict):
                try:
                    counts.append(int(item.get("count") or 0))
                except (TypeError, ValueError):
                    continue
    return max(counts or [0]) + 1


def _int_env(name: str, default: int, *, minimum: int) -> int:
    try:
        value = int(os.environ.get(name, default))
    except (TypeError, ValueError):
        value = default
    return max(minimum, value)
