import pytest

from flux_llm_kb.mail_post_process import apply_mail_post_process_policy


def _profile(
    *,
    policy: str,
    source_type: str = "imap",
    server: str = "imap.gmail.com",
    metadata: dict | None = None,
) -> dict:
    return {
        "name": "gmail-capture",
        "source_type": source_type,
        "account": "me@gmail.com",
        "server": server,
        "post_process_policy": policy,
        "metadata": metadata or {},
    }


class FakeImapClient:
    def __init__(self, *, fail_on: str | None = None, no_on: str | None = None):
        self.calls: list[tuple] = []
        self.fail_on = fail_on
        self.no_on = no_on

    def uid(self, command, *args):
        self.calls.append((command, *args))
        if self.fail_on and command == self.fail_on:
            raise RuntimeError(f"{command} failed")
        if self.no_on and command == self.no_on:
            return "NO", [b"denied"]
        return "OK", []

    def expunge(self):
        self.calls.append(("EXPUNGE",))
        return "OK", []


def test_gmail_move_to_processed_uses_labels_without_delete():
    client = FakeImapClient()

    result = apply_mail_post_process_policy(
        client=client,
        profile=_profile(policy="move_to_processed", metadata={"processed_folder": "FluxProcessed"}),
        folder="FluxCapture",
        uid=42,
    )

    assert result["status"] == "applied"
    assert result["provider"] == "gmail"
    assert result["action"] == "gmail_move_label"
    assert client.calls == [
        ("STORE", "42", "+X-GM-LABELS", "(FluxProcessed)"),
        ("STORE", "42", "-X-GM-LABELS", "(FluxCapture)"),
    ]


def test_gmail_remove_label_can_be_dry_run_without_client_calls():
    client = FakeImapClient()

    result = apply_mail_post_process_policy(
        client=client,
        profile=_profile(policy="remove_label"),
        folder="FluxCapture",
        uid=43,
        dry_run=True,
    )

    assert result["status"] == "planned"
    assert result["dry_run"] is True
    assert result["commands"] == [
        {"command": "STORE", "uid": 43, "args": ["-X-GM-LABELS", "(FluxCapture)"]},
    ]
    assert client.calls == []


def test_gmail_trash_requires_destructive_confirmation():
    client = FakeImapClient()

    result = apply_mail_post_process_policy(
        client=client,
        profile=_profile(policy="trash"),
        folder="FluxCapture",
        uid=44,
    )

    assert result["status"] == "blocked_config"
    assert "confirm" in result["error"].lower()
    assert client.calls == []


def test_gmail_trash_uses_trash_label_when_confirmed():
    client = FakeImapClient()

    result = apply_mail_post_process_policy(
        client=client,
        profile=_profile(policy="trash", metadata={"destructive_post_process_confirmed": True}),
        folder="FluxCapture",
        uid=45,
    )

    assert result["status"] == "applied"
    assert result["action"] == "gmail_trash"
    assert client.calls == [("STORE", "45", "+X-GM-LABELS", r"(\Trash)")]


def test_generic_imap_move_to_processed_copies_deletes_and_expunges():
    client = FakeImapClient()

    result = apply_mail_post_process_policy(
        client=client,
        profile=_profile(
            policy="move_to_processed",
            server="imap.example.com",
            metadata={"provider": "imap", "processed_folder": "FluxProcessed"},
        ),
        folder="FluxCapture",
        uid=46,
    )

    assert result["status"] == "applied"
    assert result["provider"] == "imap"
    assert result["action"] == "imap_move_folder"
    assert client.calls == [
        ("COPY", "46", "FluxProcessed"),
        ("STORE", "46", "+FLAGS", r"(\Deleted)"),
        ("EXPUNGE",),
    ]


def test_generic_imap_trash_delete_requires_confirmation():
    client = FakeImapClient()

    result = apply_mail_post_process_policy(
        client=client,
        profile=_profile(policy="trash", server="imap.example.com"),
        folder="FluxCapture",
        uid=47,
    )

    assert result["status"] == "blocked_config"
    assert client.calls == []


def test_generic_imap_trash_can_copy_to_configured_trash_folder():
    client = FakeImapClient()

    result = apply_mail_post_process_policy(
        client=client,
        profile=_profile(
            policy="trash",
            server="imap.example.com",
            metadata={
                "provider": "imap",
                "trash_folder": "Deleted Items",
                "destructive_post_process_confirmed": True,
            },
        ),
        folder="FluxCapture",
        uid=47,
    )

    assert result["status"] == "applied"
    assert result["action"] == "imap_move_trash"
    assert client.calls == [
        ("COPY", "47", "Deleted Items"),
        ("STORE", "47", "+FLAGS", r"(\Deleted)"),
        ("EXPUNGE",),
    ]


def test_policy_failures_return_failed_result_with_error():
    client = FakeImapClient(fail_on="COPY")

    result = apply_mail_post_process_policy(
        client=client,
        profile=_profile(
            policy="move_to_processed",
            server="imap.example.com",
            metadata={"provider": "imap", "processed_folder": "FluxProcessed"},
        ),
        folder="FluxCapture",
        uid=48,
    )

    assert result["status"] == "failed"
    assert "COPY failed" in result["error"]
    assert result["commands"][0]["command"] == "COPY"


def test_non_ok_imap_command_response_is_failed():
    client = FakeImapClient(no_on="COPY")

    result = apply_mail_post_process_policy(
        client=client,
        profile=_profile(
            policy="move_to_processed",
            server="imap.example.com",
            metadata={"provider": "imap", "processed_folder": "FluxProcessed"},
        ),
        folder="FluxCapture",
        uid=48,
    )

    assert result["status"] == "failed"
    assert "COPY failed with status NO" in result["error"]


def test_missing_processed_folder_blocks_move_policy():
    client = FakeImapClient()

    result = apply_mail_post_process_policy(
        client=client,
        profile=_profile(policy="move_to_processed", metadata={}),
        folder="FluxCapture",
        uid=49,
    )

    assert result["status"] == "blocked_config"
    assert "processed" in result["error"].lower()
    assert client.calls == []


def test_outlook_com_non_none_policy_blocks_without_imap_commands():
    client = FakeImapClient()

    result = apply_mail_post_process_policy(
        client=client,
        profile=_profile(
            policy="move_to_processed",
            source_type="outlook_com",
            server="imap.gmail.com",
            metadata={"processed_folder": "FluxProcessed"},
        ),
        folder="Mailbox - Me\\Inbox\\Flux Capture",
        uid=50,
        dry_run=True,
    )

    assert result["provider"] == "outlook_com"
    assert result["status"] == "blocked_config"
    assert result["action"] == "outlook_unsupported_post_process"
    assert "Outlook COM" in result["error"]
    assert result["commands"] == []
    assert client.calls == []
