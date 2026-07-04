from flux_llm_kb import redaction
from flux_llm_kb.redaction import RedactionFinding, redact_text


def _synthetic_api_key() -> str:
    return "sk-" + ("a" * 24)


def test_redact_text_keeps_sensitive_text_when_redactions_disabled_by_default(monkeypatch):
    from flux_llm_kb import database

    monkeypatch.delenv("FLUX_KB_REDACTIONS_ENABLED", raising=False)
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    password_assignment = "password" + " = super-secret"
    text = (
        f"Use {_synthetic_api_key()} and "
        f"{password_assignment} and email user@example.com"
    )

    redacted, findings = redact_text(text)

    assert redaction.redactions_enabled() is False
    assert redacted == text
    assert findings == []


def test_redact_text_replaces_secrets_but_keeps_contact_identifiers_when_enabled(monkeypatch):
    monkeypatch.setenv("FLUX_KB_REDACTIONS_ENABLED", "true")
    password_assignment = "password" + " = super-secret"
    text = (
        f"Use {_synthetic_api_key()} and "
        f"{password_assignment} and email user@example.com"
    )

    redacted, findings = redact_text(text)

    assert _synthetic_api_key() not in redacted
    assert "super-secret" not in redacted
    assert "user@example.com" in redacted
    assert "[REDACTED:openai_api_key]" in redacted
    assert "[REDACTED:password_assignment]" in redacted
    assert "[REDACTED:email]" not in redacted
    assert RedactionFinding(kind="email", value="user@example.com") not in findings


def test_redact_text_is_idempotent_for_already_redacted_text_when_enabled(monkeypatch):
    monkeypatch.setenv("FLUX_KB_REDACTIONS_ENABLED", "true")
    text = "Token [REDACTED:openai_api_key] remains redacted."

    redacted, findings = redact_text(text)

    assert redacted == text
    assert findings == []


def test_claim_upsert_keeps_user_supplied_text_before_database_when_redactions_disabled(monkeypatch):
    from flux_llm_kb import database
    from flux_llm_kb.service import KnowledgeService

    monkeypatch.delenv("FLUX_KB_REDACTIONS_ENABLED", raising=False)
    captured = {}
    predicate_text = "password" + " = predicate-secret"
    object_text = "Store " + "password" + ": object-secret"

    def fake_upsert_claim(**kwargs):
        captured.update(kwargs)
        return {"id": "claim-1", **kwargs}

    monkeypatch.setattr(database, "upsert_claim", fake_upsert_claim)

    result = KnowledgeService().upsert_claim(
        subject_type="project",
        subject_name=_synthetic_api_key(),
        predicate=predicate_text,
        object_text=object_text,
        metadata={"note": f"Use {_synthetic_api_key()}", "safe": 7},
    )

    assert captured["subject_name"] == _synthetic_api_key()
    assert captured["predicate"] == predicate_text
    assert captured["object_text"] == object_text
    assert captured["metadata"]["note"] == f"Use {_synthetic_api_key()}"
    assert captured["metadata"]["safe"] == 7
    assert "redactions" not in captured["metadata"]
    assert result["metadata"] == captured["metadata"]


def test_claim_upsert_redacts_user_supplied_text_before_database_when_enabled(monkeypatch):
    from flux_llm_kb import database
    from flux_llm_kb.service import KnowledgeService

    monkeypatch.setenv("FLUX_KB_REDACTIONS_ENABLED", "true")
    captured = {}
    predicate_text = "password" + " = predicate-secret"
    object_text = "Store " + "password" + ": object-secret"

    def fake_upsert_claim(**kwargs):
        captured.update(kwargs)
        return {"id": "claim-1", **kwargs}

    monkeypatch.setattr(database, "upsert_claim", fake_upsert_claim)

    result = KnowledgeService().upsert_claim(
        subject_type="project",
        subject_name=_synthetic_api_key(),
        predicate=predicate_text,
        object_text=object_text,
        metadata={"note": f"Use {_synthetic_api_key()}", "safe": 7},
    )

    assert captured["subject_name"] == "[REDACTED:openai_api_key]"
    assert captured["predicate"] == "password" + " = [REDACTED:password_assignment]"
    assert captured["object_text"] == "Store " + "password" + ": [REDACTED:password_assignment]"
    assert captured["metadata"]["note"] == "Use [REDACTED:openai_api_key]"
    assert captured["metadata"]["safe"] == 7
    assert captured["metadata"]["redactions"] == [
        "openai_api_key",
        "password_assignment",
        "password_assignment",
        "openai_api_key",
    ]
    assert result["metadata"] == captured["metadata"]


def test_error_envelope_keeps_diagnostic_values_when_redactions_disabled(monkeypatch):
    from flux_llm_kb.error_diagnostics import error_envelope

    monkeypatch.delenv("FLUX_KB_REDACTIONS_ENABLED", raising=False)

    payload = error_envelope(
        code="test.error",
        message="failed with " + "password" + "=sample",
        target={"api_key": "sample-key"},
    )

    assert payload["message"] == "failed with " + "password" + "=sample"
    assert payload["technical_detail"] == "failed with " + "password" + "=sample"
    assert payload["target"] == {"api_key": "sample-key"}


def test_error_envelope_masks_diagnostic_values_when_enabled(monkeypatch):
    from flux_llm_kb.error_diagnostics import error_envelope

    monkeypatch.setenv("FLUX_KB_REDACTIONS_ENABLED", "true")

    payload = error_envelope(
        code="test.error",
        message="failed with " + "password" + "=sample",
        target={"api_key": "sample-key"},
    )

    assert payload["message"] == "failed with " + "password" + "=***"
    assert payload["technical_detail"] == "failed with " + "password" + "=***"
    assert payload["target"] == {"api_key": "***"}
