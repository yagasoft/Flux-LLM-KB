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
