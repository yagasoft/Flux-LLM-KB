from __future__ import annotations

from typing import Any


SUPPORTED_POLICIES = {"none", "move_to_processed", "remove_label", "trash"}
DESTRUCTIVE_POLICIES = {"trash"}


def apply_mail_post_process_policy(
    *,
    client: Any,
    profile: dict[str, Any],
    folder: str,
    uid: int,
    dry_run: bool = False,
) -> dict[str, Any]:
    policy = str(profile.get("post_process_policy") or "none")
    metadata = dict(profile.get("metadata") or {})
    provider = _provider(profile)

    if policy not in SUPPORTED_POLICIES:
        return _result(
            profile=profile,
            uid=uid,
            provider=provider,
            policy=policy,
            action="unsupported_policy",
            status="blocked_config",
            dry_run=dry_run,
            error=f"unsupported post-process policy: {policy}",
        )
    if policy == "none":
        return _result(
            profile=profile,
            uid=uid,
            provider=provider,
            policy=policy,
            action="none",
            status="skipped",
            dry_run=dry_run,
        )
    if provider == "outlook_com":
        return _result(
            profile=profile,
            uid=uid,
            provider=provider,
            policy=policy,
            action="outlook_unsupported_post_process",
            status="blocked_config",
            dry_run=dry_run,
            commands=[],
            error="Outlook COM post-process actions are not supported; leave the profile policy as none.",
        )
    if policy in DESTRUCTIVE_POLICIES and not _destructive_confirmed(metadata):
        return _result(
            profile=profile,
            uid=uid,
            provider=provider,
            policy=policy,
            action=f"{provider}_{policy}",
            status="blocked_config",
            dry_run=dry_run,
            error="destructive mail post-process policy requires explicit confirmation",
        )

    plan = _planned_commands(provider=provider, policy=policy, folder=folder, uid=uid, metadata=metadata)
    if plan["status"] == "blocked_config":
        return _result(
            profile=profile,
            uid=uid,
            provider=provider,
            policy=policy,
            action=plan["action"],
            status="blocked_config",
            dry_run=dry_run,
            error=plan["error"],
        )
    if dry_run:
        return _result(
            profile=profile,
            uid=uid,
            provider=provider,
            policy=policy,
            action=plan["action"],
            status="planned",
            dry_run=True,
            commands=plan["commands"],
        )

    try:
        for command in plan["commands"]:
            _execute_command(client, command)
    except Exception as exc:
        return _result(
            profile=profile,
            uid=uid,
            provider=provider,
            policy=policy,
            action=plan["action"],
            status="failed",
            dry_run=False,
            commands=plan["commands"],
            error=str(exc),
        )
    return _result(
        profile=profile,
        uid=uid,
        provider=provider,
        policy=policy,
        action=plan["action"],
        status="applied",
        dry_run=False,
        commands=plan["commands"],
    )


def _planned_commands(
    *,
    provider: str,
    policy: str,
    folder: str,
    uid: int,
    metadata: dict[str, Any],
) -> dict[str, Any]:
    if provider == "gmail":
        return _gmail_commands(policy=policy, folder=folder, uid=uid, metadata=metadata)
    return _imap_commands(policy=policy, uid=uid, metadata=metadata)


def _gmail_commands(*, policy: str, folder: str, uid: int, metadata: dict[str, Any]) -> dict[str, Any]:
    if policy == "remove_label":
        return {
            "action": "gmail_remove_label",
            "status": "planned",
            "commands": [_uid_command("STORE", uid, "-X-GM-LABELS", _label_atom(folder))],
        }
    if policy == "move_to_processed":
        processed_folder = str(metadata.get("processed_folder") or "").strip()
        if not processed_folder:
            return {
                "action": "gmail_move_label",
                "status": "blocked_config",
                "error": "processed_folder is required for move_to_processed",
                "commands": [],
            }
        return {
            "action": "gmail_move_label",
            "status": "planned",
            "commands": [
                _uid_command("STORE", uid, "+X-GM-LABELS", _label_atom(processed_folder)),
                _uid_command("STORE", uid, "-X-GM-LABELS", _label_atom(folder)),
            ],
        }
    return {
        "action": "gmail_trash",
        "status": "planned",
        "commands": [_uid_command("STORE", uid, "+X-GM-LABELS", r"(\Trash)")],
    }


def _imap_commands(*, policy: str, uid: int, metadata: dict[str, Any]) -> dict[str, Any]:
    if policy == "remove_label":
        return {
            "action": "noop_remove_label",
            "status": "blocked_config",
            "error": "remove_label is only supported for Gmail label-backed profiles",
            "commands": [],
        }
    if policy == "move_to_processed":
        processed_folder = str(metadata.get("processed_folder") or "").strip()
        if not processed_folder:
            return {
                "action": "imap_move_folder",
                "status": "blocked_config",
                "error": "processed_folder is required for move_to_processed",
                "commands": [],
            }
        return {
            "action": "imap_move_folder",
            "status": "planned",
            "commands": [
                _uid_command("COPY", uid, processed_folder),
                _uid_command("STORE", uid, "+FLAGS", r"(\Deleted)"),
                {"command": "EXPUNGE", "uid": uid, "args": []},
            ],
        }
    trash_folder = str(metadata.get("trash_folder") or "").strip()
    commands = []
    action = "imap_delete_expunge"
    if trash_folder:
        commands.append(_uid_command("COPY", uid, trash_folder))
        action = "imap_move_trash"
    commands.extend(
        [
            _uid_command("STORE", uid, "+FLAGS", r"(\Deleted)"),
            {"command": "EXPUNGE", "uid": uid, "args": []},
        ]
    )
    return {
        "action": action,
        "status": "planned",
        "commands": commands,
    }


def _execute_command(client: Any, command: dict[str, Any]) -> None:
    if command["command"] == "EXPUNGE":
        expunge = getattr(client, "expunge", None)
        if expunge:
            status, data = expunge()
            if status != "OK":
                raise RuntimeError(f"EXPUNGE failed with status {status}: {data}")
        return
    status, data = client.uid(command["command"], str(command["uid"]), *command["args"])
    if status != "OK":
        raise RuntimeError(f"{command['command']} failed with status {status}: {data}")


def _uid_command(command: str, uid: int, *args: str) -> dict[str, Any]:
    return {"command": command, "uid": uid, "args": list(args)}


def _provider(profile: dict[str, Any]) -> str:
    if str(profile.get("source_type") or "").strip().lower() == "outlook_com":
        return "outlook_com"
    metadata = dict(profile.get("metadata") or {})
    provider = str(metadata.get("provider") or "").strip().lower()
    if provider in {"gmail", "imap"}:
        return provider
    server = str(profile.get("server") or "").lower()
    account = str(profile.get("account") or "").lower()
    if "gmail" in server or account.endswith("@gmail.com") or account.endswith("@googlemail.com"):
        return "gmail"
    return "imap"


def _label_atom(value: str) -> str:
    escaped = value.replace("\\", "\\\\").replace(")", "\\)")
    return f"({escaped})"


def _destructive_confirmed(metadata: dict[str, Any]) -> bool:
    return bool(metadata.get("destructive_post_process_confirmed") or metadata.get("post_process_confirmed"))


def _result(
    *,
    profile: dict[str, Any],
    uid: int,
    provider: str,
    policy: str,
    action: str,
    status: str,
    dry_run: bool,
    commands: list[dict[str, Any]] | None = None,
    error: str | None = None,
) -> dict[str, Any]:
    payload = {
        "profile_name": profile.get("name"),
        "provider": provider,
        "policy": policy,
        "action": action,
        "status": status,
        "dry_run": dry_run,
        "commands": commands or [],
        "metadata": {"uid": uid},
    }
    if error:
        payload["error"] = error
    return payload
