from flux_llm_kb.redaction import RedactionFinding, redact_text


def test_redact_text_replaces_secrets_but_keeps_contact_identifiers():
    text = (
        "Use sk-abcdefghijklmnopqrstuvwxyz1234567890 and "
        "password = super-secret and email user@example.com"
    )

    redacted, findings = redact_text(text)

    assert "sk-abcdefghijklmnopqrstuvwxyz1234567890" not in redacted
    assert "super-secret" not in redacted
    assert "user@example.com" in redacted
    assert "[REDACTED:openai_api_key]" in redacted
    assert "[REDACTED:password_assignment]" in redacted
    assert "[REDACTED:email]" not in redacted
    assert RedactionFinding(kind="email", value="user@example.com") not in findings


def test_redact_text_is_idempotent_for_already_redacted_text():
    text = "Token [REDACTED:openai_api_key] remains redacted."

    redacted, findings = redact_text(text)

    assert redacted == text
    assert findings == []


def test_claim_upsert_redacts_user_supplied_text_before_database(monkeypatch):
    from flux_llm_kb import database
    from flux_llm_kb.service import KnowledgeService

    captured = {}

    def fake_upsert_claim(**kwargs):
        captured.update(kwargs)
        return {"id": "claim-1", **kwargs}

    monkeypatch.setattr(database, "upsert_claim", fake_upsert_claim)

    result = KnowledgeService().upsert_claim(
        subject_type="project",
        subject_name="sk-abcdefghijklmnopqrstuvwxyz1234567890",
        predicate="password = predicate-secret",
        object_text="Store password: object-secret",
        metadata={"note": "Use sk-1234567890abcdefghijklmnop", "safe": 7},
    )

    assert captured["subject_name"] == "[REDACTED:openai_api_key]"
    assert captured["predicate"] == "password = [REDACTED:password_assignment]"
    assert captured["object_text"] == "Store password: [REDACTED:password_assignment]"
    assert captured["metadata"]["note"] == "Use [REDACTED:openai_api_key]"
    assert captured["metadata"]["safe"] == 7
    assert captured["metadata"]["redactions"] == [
        "openai_api_key",
        "password_assignment",
        "password_assignment",
        "openai_api_key",
    ]
    assert result["metadata"] == captured["metadata"]
