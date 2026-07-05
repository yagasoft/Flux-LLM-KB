from __future__ import annotations

import asyncio
import json
from typing import Any

from . import database, messaging


_STORM_COMMAND_QUEUES = (
    messaging.COMMAND_CORPUS_QUEUE,
    "flux.retry.corpus",
    messaging.COMMAND_CORPUS_HOST_AGENT_QUEUE,
    "flux.retry.corpus_host_agent",
    messaging.COMMAND_SEARCH_INDEX_QUEUE,
    "flux.retry.search_index",
    "flux.dead.letters",
)

_STORM_ROUTING_KEYS = {
    messaging.CORPUS_PROCESS_ROUTING_KEY,
    messaging.CORPUS_HOST_AGENT_PROCESS_ROUTING_KEY,
    messaging.SEARCH_INDEX_PROCESS_ROUTING_KEY,
}


def repair_capture_command_storm(
    *,
    apply: bool = False,
    confirm: str | None = None,
    purge_rabbitmq: bool = False,
) -> dict[str, Any]:
    result = database.repair_capture_command_storm(apply=apply, confirm=confirm)
    if not purge_rabbitmq:
        return result
    if not apply:
        return {**result, "rabbitmq_purge": {"applied": False, "reason": "dry_run"}}
    job_ids = [str(job.get("job_id")) for job in result.get("jobs", []) if job.get("job_id")]
    rabbitmq_result = asyncio.run(_purge_matching_rabbitmq_messages(job_ids=job_ids))
    return {**result, "rabbitmq_purge": rabbitmq_result}


async def _purge_matching_rabbitmq_messages(
    *,
    job_ids: list[str],
    queue_names: tuple[str, ...] = _STORM_COMMAND_QUEUES,
    max_messages_per_queue: int = 10000,
) -> dict[str, Any]:
    if not job_ids:
        return {"applied": True, "queues": [], "purged": 0, "aborted": False}
    aio_pika = messaging._load_aio_pika()
    config = messaging.RabbitMqConfig.from_env()
    connection = await aio_pika.connect_robust(config.url)
    purged_total = 0
    queue_results: list[dict[str, Any]] = []
    try:
        channel = await connection.channel()
        for queue_name in queue_names:
            queue = await channel.get_queue(queue_name, ensure=True)
            queue_purged = 0
            inspected = 0
            aborted = False
            while inspected < max(1, int(max_messages_per_queue)):
                incoming = await queue.get(no_ack=False, fail=False, timeout=1)
                if incoming is None:
                    break
                inspected += 1
                if _message_matches_capture_command_storm(incoming, job_ids=set(job_ids)):
                    await incoming.ack()
                    queue_purged += 1
                    continue
                await incoming.nack(requeue=True)
                aborted = True
                break
            purged_total += queue_purged
            queue_results.append(
                {
                    "queue": queue_name,
                    "inspected": inspected,
                    "purged": queue_purged,
                    "aborted_on_non_matching_message": aborted,
                }
            )
    finally:
        await connection.close()
    return {
        "applied": True,
        "queues": queue_results,
        "purged": purged_total,
        "aborted": any(item["aborted_on_non_matching_message"] for item in queue_results),
    }


def _message_matches_capture_command_storm(incoming: Any, *, job_ids: set[str]) -> bool:
    try:
        payload = json.loads(incoming.body.decode("utf-8"))
    except Exception:
        return False
    if not isinstance(payload, dict):
        return False
    routing_key = str(payload.get("routing_key") or getattr(incoming, "routing_key", "") or "")
    if routing_key not in _STORM_ROUTING_KEYS:
        return False
    message_job_id = str((payload.get("payload") or {}).get("job_id") or payload.get("job_id") or "")
    return message_job_id in job_ids
