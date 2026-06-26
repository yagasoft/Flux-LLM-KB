import os

import pytest

from flux_llm_kb import database
from flux_llm_kb.settings import SettingsService
from flux_llm_kb.settings_registry import APPLY_REINDEX_REQUIRED, SETTING_REGISTRY


def test_settings_registry_contains_runtime_and_mail_defaults():
    keys = {definition.key for definition in SETTING_REGISTRY}

    assert "retrieval.token_budget" in keys
    assert "crawler.max_inline_bytes" in keys
    assert "crawler.container_max_depth" in keys
    assert "crawler.container_max_members" in keys
    assert "crawler.container_max_total_bytes" in keys
    assert "crawler.container_max_member_bytes" in keys
    assert "watcher.interval_seconds" in keys
    assert "watcher.stability_quiet_seconds" in keys
    assert "watcher.large_file_stability_quiet_seconds" in keys
    assert "watcher.reconcile_on_start" in keys
    assert "watcher.reconcile_interval_seconds" in keys
    assert "worker.lock_retry_cooldown_seconds" in keys
    assert "worker.lock_max_attempts" in keys
    assert "host_agent.vss_enabled" in keys
    assert "host_agent.vss_max_file_bytes" in keys
    assert "host_agent.vss_timeout_seconds" in keys
    assert "mail.imap.poll_interval_seconds" in keys
    assert "mail.post_process.default_policy" in keys
    assert "codex.hooks.enabled" in keys
    assert "codex.hooks.preflight_enabled" in keys
    assert "codex.hooks.capture_enabled" in keys
    assert "codex.hooks.capture_guidance_enabled" in keys
    assert "codex.hooks.reference_indexing_enabled" in keys
    assert "codex.hooks.reference_max_count" in keys
    assert "codex.hooks.reference_max_bytes" in keys
    assert "codex.hooks.reference_fetch_timeout_seconds" in keys
    assert "codex.hooks.reference_allow_private_urls" in keys
    assert "codex.hooks.token_budget" in keys
    assert "codex.hooks.min_prompt_chars" in keys
    assert "codex.hooks.capture_min_chars" in keys
    assert "codex.hooks.capture_max_chars" in keys
    assert "acceleration.asr.enabled" in keys
    assert "acceleration.asr.model_path" in keys
    assert "acceleration.asr.max_duration_seconds" in keys
    assert "acceleration.vision.enabled" in keys
    assert "acceleration.vision.model" in keys
    assert "acceleration.vision.max_image_pixels" in keys
    assert "acceleration.video.frame_sampling.enabled" in keys
    assert "acceleration.video.frame_sample_count" in keys
    assert "acceleration.video.scene_threshold" in keys
    assert "acceleration.video.frame_max_duration_seconds" in keys
    assert "governance.librarian.enabled" in keys
    assert "governance.librarian.interval_seconds" in keys
    assert "governance.librarian.mode" in keys
    assert "governance.librarian.max_actions_per_run" in keys
    assert "governance.librarian.min_shadow_precision" in keys
    assert "governance.librarian.auto_apply_enabled" in keys
    assert "governance.librarian.auto_apply_risk_ceiling" in keys
    assert "governance.librarian.digest_retention_days" in keys
    assert "governance.librarian.protected_memory_rules" in keys
    assert "governance.local_model_rationale.enabled" in keys
    assert "governance.local_model_rationale.model" in keys


def test_governance_settings_defaults_and_env_overrides(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()

    assert service.resolve("governance.librarian.enabled").raw_value is False
    assert service.resolve("governance.librarian.mode").raw_value == "shadow"
    assert service.resolve("governance.librarian.interval_seconds").raw_value == 3600
    assert service.resolve("governance.librarian.max_actions_per_run").raw_value == 25
    assert service.resolve("governance.librarian.min_shadow_precision").raw_value == 0.8
    assert service.resolve("governance.librarian.auto_apply_enabled").raw_value is False
    assert service.resolve("governance.librarian.auto_apply_risk_ceiling").raw_value == "low"
    assert service.resolve("governance.librarian.digest_retention_days").raw_value == 30
    assert "protect_confirmed_confidence" in service.resolve("governance.librarian.protected_memory_rules").raw_value
    assert service.resolve("governance.local_model_rationale.enabled").raw_value is False
    assert service.resolve("governance.local_model_rationale.model").raw_value == ""

    monkeypatch.setenv("FLUX_KB_GOVERNANCE_LIBRARIAN_ENABLED", "true")
    monkeypatch.setenv("FLUX_KB_GOVERNANCE_LIBRARIAN_MODE", "auto")
    monkeypatch.setenv("FLUX_KB_GOVERNANCE_MIN_SHADOW_PRECISION", "0.91")
    monkeypatch.setenv("FLUX_KB_GOVERNANCE_LOCAL_MODEL_RATIONALE_MODEL", "llama3.1:8b")

    assert service.resolve("governance.librarian.enabled").raw_value is True
    assert service.resolve("governance.librarian.mode").raw_value == "auto"
    assert service.resolve("governance.librarian.min_shadow_precision").raw_value == 0.91
    assert service.resolve("governance.local_model_rationale.model").raw_value == "llama3.1:8b"


def test_codex_hook_settings_are_enabled_by_default(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)

    service = SettingsService()

    assert service.resolve("codex.hooks.enabled").raw_value is True
    assert service.resolve("codex.hooks.preflight_enabled").raw_value is True
    assert service.resolve("codex.hooks.capture_enabled").raw_value is True
    assert service.resolve("codex.hooks.capture_guidance_enabled").raw_value is True
    assert service.resolve("codex.hooks.reference_indexing_enabled").raw_value is True
    assert service.resolve("codex.hooks.reference_max_count").raw_value == 5
    assert service.resolve("codex.hooks.reference_max_bytes").raw_value == 1024 * 1024
    assert service.resolve("codex.hooks.reference_fetch_timeout_seconds").raw_value == 3
    assert service.resolve("codex.hooks.reference_allow_private_urls").raw_value is False
    assert service.resolve("codex.hooks.token_budget").raw_value == 900
    assert service.resolve("codex.hooks.min_prompt_chars").raw_value == 32
    assert service.resolve("codex.hooks.capture_min_chars").raw_value == 160
    assert service.resolve("codex.hooks.capture_max_chars").raw_value == 8000


def test_lock_tolerant_indexing_settings_defaults(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)

    service = SettingsService()

    assert service.resolve("watcher.stability_quiet_seconds").raw_value == 2.0
    assert service.resolve("watcher.large_file_stability_quiet_seconds").raw_value == 10.0
    assert service.resolve("worker.lock_retry_cooldown_seconds").raw_value == 300
    assert service.resolve("worker.lock_max_attempts").raw_value == 3
    assert service.resolve("host_agent.vss_enabled").raw_value is False
    assert service.resolve("host_agent.vss_max_file_bytes").raw_value == 512 * 1024 * 1024
    assert service.resolve("host_agent.vss_timeout_seconds").raw_value == 30


def test_asr_settings_defaults_and_env_overrides(monkeypatch, tmp_path):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()

    assert service.resolve("acceleration.asr.enabled").raw_value is True
    assert service.resolve("acceleration.asr.model_path").raw_value == ""
    assert service.resolve("acceleration.asr.max_duration_seconds").raw_value == 3600

    model_dir = tmp_path / "models" / "faster-whisper"
    monkeypatch.setenv("FLUX_KB_ASR_ENABLED", "false")
    monkeypatch.setenv("FLUX_KB_ASR_MODEL_PATH", str(model_dir))
    monkeypatch.setenv("FLUX_KB_ASR_MAX_DURATION_SECONDS", "42")

    assert service.resolve("acceleration.asr.enabled").raw_value is False
    assert service.resolve("acceleration.asr.model_path").raw_value == str(model_dir)
    assert service.resolve("acceleration.asr.max_duration_seconds").raw_value == 42


def test_vision_and_video_settings_defaults_and_env_overrides(monkeypatch, tmp_path):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()

    assert service.resolve("acceleration.vision.enabled").raw_value is False
    assert service.resolve("acceleration.vision.model").raw_value == ""
    assert service.resolve("acceleration.vision.max_image_pixels").raw_value == 4_096_000
    assert service.resolve("acceleration.video.frame_sampling.enabled").raw_value is False
    assert service.resolve("acceleration.video.frame_sample_count").raw_value == 3
    assert service.resolve("acceleration.video.scene_threshold").raw_value == 0.35
    assert service.resolve("acceleration.video.frame_max_duration_seconds").raw_value == 1800

    monkeypatch.setenv("FLUX_KB_VISION_ENABLED", "true")
    monkeypatch.setenv("FLUX_KB_VISION_MODEL", "llava:latest")
    monkeypatch.setenv("FLUX_KB_VISION_MAX_IMAGE_PIXELS", "1024")
    monkeypatch.setenv("FLUX_KB_VIDEO_FRAME_SAMPLING_ENABLED", "true")
    monkeypatch.setenv("FLUX_KB_VIDEO_FRAME_SAMPLE_COUNT", "2")
    monkeypatch.setenv("FLUX_KB_VIDEO_SCENE_THRESHOLD", "0.42")
    monkeypatch.setenv("FLUX_KB_VIDEO_FRAME_MAX_DURATION_SECONDS", "60")

    assert service.resolve("acceleration.vision.enabled").raw_value is True
    assert service.resolve("acceleration.vision.model").raw_value == "llava:latest"
    assert service.resolve("acceleration.vision.max_image_pixels").raw_value == 1024
    assert service.resolve("acceleration.video.frame_sampling.enabled").raw_value is True
    assert service.resolve("acceleration.video.frame_sample_count").raw_value == 2
    assert service.resolve("acceleration.video.scene_threshold").raw_value == 0.42
    assert service.resolve("acceleration.video.frame_max_duration_seconds").raw_value == 60


def test_vision_model_setting_description_is_provider_neutral():
    definitions = {definition.key: definition for definition in SETTING_REGISTRY}
    description = definitions["acceleration.vision.model"].description.lower()

    assert "local vision model identifier" in description
    assert "provider" in description
    assert "ollama" not in description


def test_container_cap_settings_defaults_and_env_overrides(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()

    assert service.resolve("crawler.container_max_depth").raw_value == 2
    assert service.resolve("crawler.container_max_members").raw_value == 200
    assert service.resolve("crawler.container_max_total_bytes").raw_value == 50 * 1024 * 1024
    assert service.resolve("crawler.container_max_member_bytes").raw_value == 10 * 1024 * 1024

    monkeypatch.setenv("FLUX_KB_CRAWLER_CONTAINER_MAX_DEPTH", "4")
    monkeypatch.setenv("FLUX_KB_CRAWLER_CONTAINER_MAX_MEMBERS", "17")
    monkeypatch.setenv("FLUX_KB_CRAWLER_CONTAINER_MAX_TOTAL_BYTES", "4096")
    monkeypatch.setenv("FLUX_KB_CRAWLER_CONTAINER_MAX_MEMBER_BYTES", "512")

    assert service.resolve("crawler.container_max_depth").raw_value == 4
    assert service.resolve("crawler.container_max_members").raw_value == 17
    assert service.resolve("crawler.container_max_total_bytes").raw_value == 4096
    assert service.resolve("crawler.container_max_member_bytes").raw_value == 512


def test_settings_service_uses_env_over_database_and_masks_secret(monkeypatch):
    stored = {
        "retrieval.token_budget": {"value": 800, "updated_at": "db-time"},
        "mail.imap.oauth_refresh_token": {"value": "stored-token", "updated_at": "db-time"},
    }
    monkeypatch.setenv("FLUX_KB_TOKEN_BUDGET", "1600")
    monkeypatch.setattr(database, "get_runtime_setting", lambda key: stored.get(key))

    service = SettingsService()
    token_budget = service.resolve("retrieval.token_budget")
    secret = service.resolve("mail.imap.oauth_refresh_token")

    assert token_budget.value == 1600
    assert token_budget.source == "env"
    assert secret.value == "***"
    assert secret.raw_value == "stored-token"
    assert secret.sensitive is True


def test_setting_update_requires_confirmation_for_reindex(monkeypatch):
    calls = []
    monkeypatch.delenv("FLUX_KB_EMBEDDING_MODEL", raising=False)
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(database, "set_runtime_setting", lambda **kwargs: calls.append(kwargs) or {"key": kwargs["key"]})
    monkeypatch.setattr(database, "enqueue_runtime_control_request", lambda **_kwargs: {"id": "request-1"})

    service = SettingsService()

    with pytest.raises(ValueError, match="confirmation"):
        service.set("embedding.model", "flux-hash-v2", actor="tester")

    result = service.set("embedding.model", "flux-hash-v2", actor="tester", confirmed=True)

    assert result["apply_mode"] == APPLY_REINDEX_REQUIRED
    assert calls[0]["key"] == "embedding.model"
    assert calls[0]["value"] == "flux-hash-v2"


def test_setting_reset_removes_database_override(monkeypatch):
    calls = []
    monkeypatch.setattr(database, "delete_runtime_setting", lambda **kwargs: calls.append(kwargs) or {"deleted": True})

    result = SettingsService().reset("retrieval.token_budget", actor="tester")

    assert result == {"deleted": True}
    assert calls[0]["key"] == "retrieval.token_budget"
