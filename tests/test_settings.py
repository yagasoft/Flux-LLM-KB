import os

import pytest

from flux_llm_kb import database
from flux_llm_kb.settings import SettingsService
from flux_llm_kb.settings_registry import APPLY_REINDEX_REQUIRED, APPLY_RELOAD, SETTING_REGISTRY, get_definition


EXPECTED_GENERATED_CACHE_EXCLUDES = [
    "**/.vs/**",
    "**/CopilotIndices/**",
    "**/*.db",
    "**/*.db-wal",
    "**/*.db-shm",
    "**/*.sqlite",
    "**/*.sqlite-*",
    "desktop.ini",
]


def test_settings_registry_contains_runtime_and_mail_defaults():
    keys = {definition.key for definition in SETTING_REGISTRY}

    assert "retrieval.token_budget" in keys
    assert "messaging.rabbitmq_url" in keys
    assert "messaging.rabbitmq_management_url" in keys
    assert "messaging.rabbitmq_username" in keys
    assert "messaging.rabbitmq_password" in keys
    assert "messaging.prefetch" in keys
    assert "messaging.retry_delay_ms" in keys
    assert "messaging.delivery_limit" in keys
    assert "messaging.consumer.corpus_concurrency" in keys
    assert "messaging.consumer.mail_concurrency" in keys
    assert "messaging.consumer.automation_concurrency" in keys
    assert "callbacks.allowlist" in keys
    assert "callbacks.signing_secret" in keys
    assert "callbacks.timeout_seconds" in keys
    assert "retrieval.search_engine" in keys
    assert "retrieval.vespa_base_url" in keys
    assert "retrieval.embedding_model" in keys
    assert "retrieval.embedding_dimensions" in keys
    assert "retrieval.reranker_model" in keys
    assert "retrieval.reranker_awq_model" in keys
    assert "retrieval.reranker_quantization" in keys
    assert "retrieval.rerank_top_n" in keys
    assert "retrieval.rerank_microbatch_size" in keys
    assert "retrieval.max_rerank_passage_tokens" in keys
    assert "retrieval.embedding_wait_timeout_seconds" in keys
    assert "retrieval.search_index_embedding_timeout_seconds" in keys
    assert "retrieval.rerank_wait_timeout_seconds" in keys
    assert "retrieval.rerank_total_budget_seconds" in keys
    assert "retrieval.query_embedding_cache_ttl_seconds" in keys
    assert "retrieval.query_embedding_cache_max_entries" in keys
    assert "retrieval.brief_search_limit" in keys
    assert "retrieval.brief_rerank_limit" in keys
    assert "retrieval.gpu_vram_budget_mb" in keys
    assert "gpu.scheduler.enabled" in keys
    assert "gpu.scheduler.mode" in keys
    assert "gpu.scheduler.total_vram_mb" in keys
    assert "gpu.scheduler.vram_budget_mb" in keys
    assert "gpu.scheduler.safety_margin_mb" in keys
    assert "gpu.scheduler.default_timeout_seconds" in keys
    assert "gpu.scheduler.lease_ttl_seconds" in keys
    assert "gpu.scheduler.heartbeat_interval_seconds" in keys
    assert "gpu.scheduler.stale_after_seconds" in keys
    assert "gpu.scheduler.eviction_enabled" in keys
    assert "gpu.scheduler.eviction_request_timeout_seconds" in keys
    assert "gpu.scheduler.eviction_max_models" in keys
    assert "gpu.scheduler.embedding_vram_mb" in keys
    assert "gpu.scheduler.rerank_vram_mb" in keys
    assert "gpu.scheduler.ocr_image_vram_mb" in keys
    assert "gpu.scheduler.ocr_document_vram_mb" in keys
    assert "gpu.scheduler.asr_vram_mb" in keys
    assert "gpu.scheduler.ollama_vision_vram_mb" in keys
    assert "model_runner.base_url" in keys
    assert "model_runner.paddle_runner_base_url" in keys
    assert "ocr.engine" in keys
    assert "ocr.simple_model" in keys
    assert "ocr.document_model" in keys
    assert "crawler.max_inline_bytes" in keys
    assert "crawler.content_hash_mode" in keys
    assert "crawler.container_max_depth" in keys
    assert "crawler.container_max_members" in keys
    assert "crawler.container_max_total_bytes" in keys
    assert "crawler.container_max_member_bytes" in keys
    assert "crawler.unseen_asset_purge_grace_seconds" in keys
    assert "crawler.unseen_asset_purge_batch_size" in keys
    assert "watcher.interval_seconds" in keys
    assert "watcher.stability_quiet_seconds" in keys
    assert "watcher.large_file_stability_quiet_seconds" in keys
    assert "watcher.reconcile_on_start" in keys
    assert "watcher.reconcile_interval_seconds" in keys
    assert "worker.failure_max_attempts" in keys
    assert "worker.gpu_busy_retry_base_cooldown_seconds" in keys
    assert "worker.gpu_busy_retry_max_cooldown_seconds" in keys
    assert "worker.gpu_busy_retry_block_after_seconds" in keys
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
    assert "acceleration.asr.provider" in keys
    assert "acceleration.asr.model" in keys
    assert "acceleration.asr.base_url" in keys
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
    assert "operator.automation.enabled" in keys
    assert "operator.automation.mode" in keys
    assert "operator.automation.interval_seconds" in keys
    assert "operator.automation.evidence_freshness_hours" in keys
    assert "operator.automation.max_actions_per_run" in keys
    assert "operator.automation.auto_refresh_evidence" in keys
    assert "operator.automation.auto_ingest_approved_capture" in keys
    assert "operator.automation.auto_remediate_diagnostics" in keys
    assert "operator.automation.auto_sync_search_index" in keys
    assert "operator.automation.auto_refresh_embeddings" not in keys
    assert "embedding.model" not in keys
    assert "embedding.dimensions" not in keys
    assert "operator.automation.auto_run_governance_shadow" in keys
    assert "privacy.redactions.enabled" in keys
    assert "worker.default_workers" in keys


def test_crawler_content_hash_mode_setting_metadata():
    definition = get_definition("crawler.content_hash_mode")

    assert definition.default == "inline_only"
    assert definition.value_type == "str"
    assert definition.env_var == "FLUX_KB_CRAWLER_CONTENT_HASH_MODE"
    assert definition.apply_mode == APPLY_RELOAD
    assert definition.affected_components == ("crawler", "watcher", "worker")
    assert definition.validate("inline_only") == "inline_only"
    assert definition.validate("all_eligible") == "all_eligible"
    with pytest.raises(ValueError, match="content_hash_mode"):
        definition.validate("everything")


def test_worker_default_workers_uses_environment_override(monkeypatch):
    monkeypatch.setenv("FLUX_KB_WORKER_DEFAULT_WORKERS", "12")
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)

    resolved = SettingsService().resolve("worker.default_workers")

    assert resolved.value == 12
    assert resolved.raw_value == 12
    assert resolved.source == "env"


def test_messaging_settings_defaults_and_env_overrides(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()

    assert service.resolve("messaging.rabbitmq_url").raw_value == "amqp://flux:flux@rabbitmq:5672/flux"
    assert service.resolve("messaging.rabbitmq_management_url").raw_value == "http://127.0.0.1:15672"
    assert service.resolve("messaging.prefetch").raw_value == 4
    assert service.resolve("messaging.delivery_limit").raw_value == 8
    assert service.resolve("messaging.consumer.corpus_concurrency").raw_value == 4
    assert service.resolve("callbacks.allowlist").raw_value == []
    assert service.resolve("callbacks.timeout_seconds").raw_value == 5

    monkeypatch.setenv("FLUX_KB_RABBITMQ_URL", "amqp://flux:secret@localhost:5672/flux")
    monkeypatch.setenv("FLUX_KB_RABBITMQ_PREFETCH", "12")
    monkeypatch.setenv("FLUX_KB_RABBITMQ_DELIVERY_LIMIT", "5")
    monkeypatch.setenv("FLUX_KB_CONSUMER_CORPUS_CONCURRENCY", "8")
    monkeypatch.setenv("FLUX_KB_CALLBACK_ALLOWLIST", "https://hooks.example.local/flux,example.internal")
    monkeypatch.setenv("FLUX_KB_CALLBACK_TIMEOUT_SECONDS", "9")

    assert service.resolve("messaging.rabbitmq_url").raw_value == "amqp://flux:secret@localhost:5672/flux"
    assert service.resolve("messaging.prefetch").raw_value == 12
    assert service.resolve("messaging.delivery_limit").raw_value == 5
    assert service.resolve("messaging.consumer.corpus_concurrency").raw_value == 8
    assert service.resolve("callbacks.allowlist").raw_value == ["https://hooks.example.local/flux", "example.internal"]
    assert service.resolve("callbacks.timeout_seconds").raw_value == 9


def test_gpu_scheduler_settings_defaults_and_env_overrides(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()

    assert service.resolve("gpu.scheduler.enabled").raw_value is True
    assert service.resolve("gpu.scheduler.mode").raw_value == "auto"
    assert service.resolve("gpu.scheduler.vram_budget_mb").raw_value == 10240
    assert service.resolve("gpu.scheduler.safety_margin_mb").raw_value == 1024
    assert service.resolve("gpu.scheduler.default_timeout_seconds").raw_value == 30
    assert service.resolve("gpu.scheduler.lease_ttl_seconds").raw_value == 120
    assert service.resolve("gpu.scheduler.eviction_enabled").raw_value is True
    assert service.resolve("gpu.scheduler.eviction_request_timeout_seconds").raw_value == 10
    assert service.resolve("gpu.scheduler.eviction_max_models").raw_value == 4
    assert service.resolve("gpu.scheduler.embedding_vram_mb").raw_value > 0

    monkeypatch.setenv("FLUX_KB_GPU_SCHEDULER_MODE", "postgres")
    monkeypatch.setenv("FLUX_KB_GPU_SCHEDULER_VRAM_BUDGET_MB", "8192")
    monkeypatch.setenv("FLUX_KB_GPU_SCHEDULER_DEFAULT_TIMEOUT_SECONDS", "9")
    monkeypatch.setenv("FLUX_KB_GPU_SCHEDULER_EVICTION_ENABLED", "false")
    monkeypatch.setenv("FLUX_KB_GPU_SCHEDULER_EVICTION_MAX_MODELS", "2")

    assert service.resolve("gpu.scheduler.mode").raw_value == "postgres"
    assert service.resolve("gpu.scheduler.vram_budget_mb").raw_value == 8192
    assert service.resolve("gpu.scheduler.default_timeout_seconds").raw_value == 9
    assert service.resolve("gpu.scheduler.eviction_enabled").raw_value is False
    assert service.resolve("gpu.scheduler.eviction_max_models").raw_value == 2


def test_gpu_runtime_reconciliation_settings_defaults_and_validators(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()
    expected_defaults = {
        "gpu.scheduler.runtime_reconciliation_mode": "observation",
        "gpu.scheduler.inventory_timeout_seconds": 2,
        "gpu.scheduler.control_lock_timeout_seconds": 2,
        "gpu.scheduler.context_allowance_mb": 256,
        "gpu.scheduler.unattributed_threshold_mb": 512,
        "gpu.scheduler.unattributed_threshold_percent": 5,
        "gpu.scheduler.reconciliation_retry_seconds": 15,
        "gpu.scheduler.calibration_min_samples": 5,
        "gpu.scheduler.calibration_guard_margin_mb": 512,
        "gpu.scheduler.priority_drain_enabled": False,
        "gpu.scheduler.retry_coalescing_enabled": False,
        "gpu.scheduler.eviction_expiry_enabled": False,
        "gpu.scheduler.idle_unload_enabled": False,
        "gpu.scheduler.idle_unload_seconds": 120,
        "gpu.scheduler.idle_sweep_interval_seconds": 30,
        "retrieval.vespa_lexical_fallback_enabled": True,
    }

    for key, expected in expected_defaults.items():
        definition = get_definition(key)
        assert service.resolve(key).raw_value == expected
        assert definition.default == expected
        assert definition.apply_mode == APPLY_RELOAD
        assert definition.affected_components

    assert get_definition("gpu.scheduler.embedding_vram_mb").default == 2500
    assert get_definition("gpu.scheduler.runtime_reconciliation_mode").validate("observation") == "observation"
    with pytest.raises(ValueError):
        get_definition("gpu.scheduler.runtime_reconciliation_mode").validate("invalid")

    numeric_keys = (
        "gpu.scheduler.inventory_timeout_seconds",
        "gpu.scheduler.control_lock_timeout_seconds",
        "gpu.scheduler.context_allowance_mb",
        "gpu.scheduler.unattributed_threshold_mb",
        "gpu.scheduler.unattributed_threshold_percent",
        "gpu.scheduler.reconciliation_retry_seconds",
        "gpu.scheduler.calibration_min_samples",
        "gpu.scheduler.calibration_guard_margin_mb",
        "gpu.scheduler.idle_unload_seconds",
        "gpu.scheduler.idle_sweep_interval_seconds",
    )
    for key in numeric_keys:
        with pytest.raises(ValueError):
            get_definition(key).validate(1_000_000)


def test_reranker_quantization_settings_canonicalize_legacy_aliases(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    definitions = {definition.key: definition for definition in SETTING_REGISTRY}
    quantization = definitions["retrieval.reranker_quantization"]
    awq_model = definitions["retrieval.reranker_awq_model"]

    assert quantization.default == "awq_int4"
    assert quantization.validate("awq_int4") == "awq_int4"
    assert quantization.validate("int4_awq") == "awq_int4"
    assert quantization.validate("awq") == "awq_int4"
    assert quantization.validate("nf4_4bit") == "nf4_4bit"
    assert quantization.validate("int4") == "nf4_4bit"
    assert quantization.validate("4bit") == "nf4_4bit"
    assert quantization.validate("fp16") == "fp16"
    with pytest.raises(ValueError, match="awq_int4"):
        quantization.validate("nf4_awq")

    service = SettingsService()
    assert service.resolve("retrieval.reranker_quantization").raw_value == "awq_int4"
    assert service.resolve("retrieval.reranker_awq_model").raw_value == "drawais/Qwen3-Reranker-4B-AWQ-INT4"
    assert awq_model.env_var == "FLUX_KB_RETRIEVAL_RERANKER_AWQ_MODEL"

    monkeypatch.setenv("FLUX_KB_RETRIEVAL_RERANKER_QUANTIZATION", "int4_awq")
    assert service.resolve("retrieval.reranker_quantization").raw_value == "awq_int4"


def test_retrieval_interactive_latency_settings_defaults_and_env_overrides(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()

    assert service.resolve("retrieval.embedding_wait_timeout_seconds").raw_value == 5
    assert service.resolve("retrieval.search_index_embedding_timeout_seconds").raw_value == 60
    assert service.resolve("retrieval.rerank_wait_timeout_seconds").raw_value == 5
    assert service.resolve("retrieval.rerank_total_budget_seconds").raw_value == 5
    assert service.resolve("retrieval.query_embedding_cache_ttl_seconds").raw_value == 120
    assert service.resolve("retrieval.query_embedding_cache_max_entries").raw_value == 256
    assert service.resolve("retrieval.brief_search_limit").raw_value == 5
    assert service.resolve("retrieval.brief_rerank_limit").raw_value == 3

    monkeypatch.setenv("FLUX_KB_RETRIEVAL_EMBEDDING_WAIT_TIMEOUT_SECONDS", "7")
    monkeypatch.setenv("FLUX_KB_RETRIEVAL_SEARCH_INDEX_EMBEDDING_TIMEOUT_SECONDS", "90")
    monkeypatch.setenv("FLUX_KB_RETRIEVAL_RERANK_WAIT_TIMEOUT_SECONDS", "9")
    monkeypatch.setenv("FLUX_KB_RETRIEVAL_RERANK_TOTAL_BUDGET_SECONDS", "4")
    monkeypatch.setenv("FLUX_KB_RETRIEVAL_QUERY_EMBEDDING_CACHE_TTL_SECONDS", "30")
    monkeypatch.setenv("FLUX_KB_RETRIEVAL_QUERY_EMBEDDING_CACHE_MAX_ENTRIES", "32")
    monkeypatch.setenv("FLUX_KB_RETRIEVAL_BRIEF_SEARCH_LIMIT", "4")
    monkeypatch.setenv("FLUX_KB_RETRIEVAL_BRIEF_RERANK_LIMIT", "2")

    assert service.resolve("retrieval.embedding_wait_timeout_seconds").raw_value == 7
    assert service.resolve("retrieval.search_index_embedding_timeout_seconds").raw_value == 90
    assert service.resolve("retrieval.rerank_wait_timeout_seconds").raw_value == 9
    assert service.resolve("retrieval.rerank_total_budget_seconds").raw_value == 4
    assert service.resolve("retrieval.query_embedding_cache_ttl_seconds").raw_value == 30
    assert service.resolve("retrieval.query_embedding_cache_max_entries").raw_value == 32
    assert service.resolve("retrieval.brief_search_limit").raw_value == 4
    assert service.resolve("retrieval.brief_rerank_limit").raw_value == 2


def test_crawler_global_excludes_skip_dedicated_worktrees(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()

    exclude_globs = service.resolve("crawler.global_exclude_globs").raw_value

    assert ".worktrees/**" in exclude_globs
    for pattern in EXPECTED_GENERATED_CACHE_EXCLUDES:
        assert pattern in exclude_globs


def test_crawler_global_excludes_reconcile_db_values_without_removing_custom_patterns(monkeypatch):
    def fake_get_runtime_setting(key):
        if key == "crawler.global_exclude_globs":
            return {
                "key": key,
                "value": ["custom/**", "**/.vs/**"],
                "updated_at": "2026-06-30T00:00:00+00:00",
            }
        return None

    monkeypatch.setattr(database, "get_runtime_setting", fake_get_runtime_setting)

    resolved = SettingsService().resolve("crawler.global_exclude_globs")

    assert resolved.source == "db"
    assert resolved.raw_value[0:2] == ["custom/**", "**/.vs/**"]
    assert resolved.raw_value.count("**/.vs/**") == 1
    for pattern in EXPECTED_GENERATED_CACHE_EXCLUDES:
        assert pattern in resolved.raw_value


def test_unseen_asset_purge_settings_defaults_and_env_overrides(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()

    assert service.resolve("crawler.unseen_asset_purge_grace_seconds").raw_value == 86400
    assert service.resolve("crawler.unseen_asset_purge_batch_size").raw_value == 500

    monkeypatch.setenv("FLUX_KB_UNSEEN_ASSET_PURGE_GRACE_SECONDS", "3600")
    monkeypatch.setenv("FLUX_KB_UNSEEN_ASSET_PURGE_BATCH_SIZE", "25")

    assert service.resolve("crawler.unseen_asset_purge_grace_seconds").raw_value == 3600
    assert service.resolve("crawler.unseen_asset_purge_batch_size").raw_value == 25


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


def test_operator_automation_settings_defaults_and_env_overrides(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()

    assert service.resolve("operator.automation.enabled").raw_value is False
    assert service.resolve("operator.automation.mode").raw_value == "guarded"
    assert service.resolve("operator.automation.interval_seconds").raw_value == 1800
    assert service.resolve("operator.automation.evidence_freshness_hours").raw_value == 336
    assert service.resolve("operator.automation.max_actions_per_run").raw_value == 25
    assert service.resolve("operator.automation.auto_refresh_evidence").raw_value is True
    assert service.resolve("operator.automation.auto_ingest_approved_capture").raw_value is True
    assert service.resolve("operator.automation.auto_remediate_diagnostics").raw_value is True
    assert service.resolve("operator.automation.auto_sync_search_index").raw_value is True
    assert service.resolve("operator.automation.auto_run_governance_shadow").raw_value is True

    monkeypatch.setenv("FLUX_KB_OPERATOR_AUTOMATION_ENABLED", "true")
    monkeypatch.setenv("FLUX_KB_OPERATOR_AUTOMATION_MODE", "suggest_only")
    monkeypatch.setenv("FLUX_KB_OPERATOR_AUTOMATION_INTERVAL_SECONDS", "2400")
    monkeypatch.setenv("FLUX_KB_OPERATOR_AUTOMATION_MAX_ACTIONS_PER_RUN", "9")

    assert service.resolve("operator.automation.enabled").raw_value is True
    assert service.resolve("operator.automation.mode").raw_value == "suggest_only"
    assert service.resolve("operator.automation.interval_seconds").raw_value == 2400
    assert service.resolve("operator.automation.max_actions_per_run").raw_value == 9


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

    assert service.resolve("watcher.debounce_seconds").raw_value == 2.0
    assert service.resolve("watcher.stability_quiet_seconds").raw_value == 2.0
    assert service.resolve("watcher.large_file_stability_quiet_seconds").raw_value == 30.0
    assert service.resolve("worker.failure_max_attempts").raw_value == 3
    assert service.resolve("worker.gpu_busy_retry_base_cooldown_seconds").raw_value == 60
    assert service.resolve("worker.gpu_busy_retry_max_cooldown_seconds").raw_value == 900
    assert service.resolve("worker.gpu_busy_retry_block_after_seconds").raw_value == 86400
    assert service.resolve("worker.lock_retry_cooldown_seconds").raw_value == 300
    assert service.resolve("worker.lock_max_attempts").raw_value == 3
    assert service.resolve("host_agent.vss_enabled").raw_value is True
    assert service.resolve("host_agent.vss_max_file_bytes").raw_value == 512 * 1024 * 1024
    assert service.resolve("host_agent.vss_timeout_seconds").raw_value == 30


def test_gpu_busy_retry_settings_env_overrides(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setenv("FLUX_KB_WORKER_GPU_BUSY_RETRY_BASE_COOLDOWN_SECONDS", "45")
    monkeypatch.setenv("FLUX_KB_WORKER_GPU_BUSY_RETRY_MAX_COOLDOWN_SECONDS", "600")
    monkeypatch.setenv("FLUX_KB_WORKER_GPU_BUSY_RETRY_BLOCK_AFTER_SECONDS", "7200")

    service = SettingsService()

    assert service.resolve("worker.gpu_busy_retry_base_cooldown_seconds").raw_value == 45
    assert service.resolve("worker.gpu_busy_retry_max_cooldown_seconds").raw_value == 600
    assert service.resolve("worker.gpu_busy_retry_block_after_seconds").raw_value == 7200


def test_asr_settings_defaults_and_env_overrides(monkeypatch, tmp_path):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()

    assert service.resolve("acceleration.asr.enabled").raw_value is True
    assert service.resolve("acceleration.asr.provider").raw_value == "local_faster_whisper"
    assert service.resolve("acceleration.asr.model").raw_value == ""
    assert service.resolve("acceleration.asr.base_url").raw_value == ""
    assert service.resolve("acceleration.asr.model_path").raw_value == ""
    assert service.resolve("acceleration.asr.max_duration_seconds").raw_value == 3600
    assert service.resolve("acceleration.asr.device").raw_value == "auto"
    assert service.resolve("acceleration.asr.compute_type").raw_value == "default"

    model_dir = tmp_path / "models" / "faster-whisper"
    monkeypatch.setenv("FLUX_KB_ASR_ENABLED", "false")
    monkeypatch.setenv("FLUX_KB_ASR_PROVIDER", "openai_compatible")
    monkeypatch.setenv("FLUX_KB_ASR_MODEL", "large-v3-turbo")
    monkeypatch.setenv("FLUX_KB_ASR_BASE_URL", "http://asr:8788/")
    monkeypatch.setenv("FLUX_KB_ASR_MODEL_PATH", str(model_dir))
    monkeypatch.setenv("FLUX_KB_ASR_MAX_DURATION_SECONDS", "42")
    monkeypatch.setenv("FLUX_KB_ASR_DEVICE", "cuda")
    monkeypatch.setenv("FLUX_KB_ASR_COMPUTE_TYPE", "float16")

    assert service.resolve("acceleration.asr.enabled").raw_value is False
    assert service.resolve("acceleration.asr.provider").raw_value == "openai_compatible"
    assert service.resolve("acceleration.asr.model").raw_value == "large-v3-turbo"
    assert service.resolve("acceleration.asr.base_url").raw_value == "http://asr:8788"
    assert service.resolve("acceleration.asr.model_path").raw_value == str(model_dir)
    assert service.resolve("acceleration.asr.max_duration_seconds").raw_value == 42
    assert service.resolve("acceleration.asr.device").raw_value == "cuda"
    assert service.resolve("acceleration.asr.compute_type").raw_value == "float16"


def test_vision_and_video_settings_defaults_and_env_overrides(monkeypatch, tmp_path):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()

    assert service.resolve("acceleration.vision.enabled").raw_value is True
    assert service.resolve("acceleration.vision.model").raw_value == "qwen3-vl:8b"
    assert service.resolve("acceleration.vision.max_image_pixels").raw_value == 4_096_000
    assert service.resolve("acceleration.video.frame_sampling.enabled").raw_value is True
    assert service.resolve("acceleration.video.frame_sample_count").raw_value == 3
    assert service.resolve("acceleration.video.scene_threshold").raw_value == 0.35
    assert service.resolve("acceleration.video.frame_max_duration_seconds").raw_value == 1800
    assert service.resolve("acceleration.local_inference.enabled").raw_value is True

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


def test_redaction_setting_defaults_disabled_and_env_override_enables(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()

    assert service.resolve("privacy.redactions.enabled").raw_value is False

    monkeypatch.setenv("FLUX_KB_REDACTIONS_ENABLED", "true")

    assert service.resolve("privacy.redactions.enabled").raw_value is True


def test_local_inference_base_url_accepts_docker_host_gateway():
    definitions = {definition.key: definition for definition in SETTING_REGISTRY}
    base_url = definitions["acceleration.local_inference.base_url"]

    assert base_url.validate("http://host.docker.internal:11434/") == "http://host.docker.internal:11434"
    assert base_url.validate("http://ollama:11434/") == "http://ollama:11434"

    with pytest.raises(ValueError, match="local"):
        base_url.validate("https://api.openai.com/v1")


def test_local_inference_keep_alive_defaults_and_env_override(monkeypatch):
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    service = SettingsService()

    assert service.resolve("acceleration.local_inference.keep_alive").raw_value == "2m"

    monkeypatch.setenv("FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE", "2m")

    assert service.resolve("acceleration.local_inference.keep_alive").raw_value == "2m"


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


def test_settings_service_uses_env_over_database_and_exposes_secret_when_redactions_disabled(monkeypatch):
    stored = {
        "retrieval.token_budget": {"value": 800, "updated_at": "db-time"},
        "mail.imap.oauth_refresh_token": {"value": "stored-token", "updated_at": "db-time"},
    }
    monkeypatch.delenv("FLUX_KB_REDACTIONS_ENABLED", raising=False)
    monkeypatch.setenv("FLUX_KB_TOKEN_BUDGET", "1600")
    monkeypatch.setattr(database, "get_runtime_setting", lambda key: stored.get(key))

    service = SettingsService()
    token_budget = service.resolve("retrieval.token_budget")
    sensitive_setting = service.resolve("mail.imap.oauth_refresh_token")

    assert token_budget.value == 1600
    assert token_budget.source == "env"
    assert sensitive_setting.value == "stored-token"
    assert sensitive_setting.raw_value == "stored-token"
    assert sensitive_setting.sensitive is True


def test_settings_service_masks_secret_when_redactions_enabled(monkeypatch):
    stored = {"mail.imap.oauth_refresh_token": {"value": "stored-token", "updated_at": "db-time"}}
    monkeypatch.setenv("FLUX_KB_REDACTIONS_ENABLED", "true")
    monkeypatch.setattr(database, "get_runtime_setting", lambda key: stored.get(key))

    sensitive_setting = SettingsService().resolve("mail.imap.oauth_refresh_token")

    assert sensitive_setting.value == "***"
    assert sensitive_setting.raw_value == "stored-token"
    assert sensitive_setting.sensitive is True


def test_setting_update_requires_confirmation_for_reindex(monkeypatch):
    calls = []
    monkeypatch.delenv("FLUX_KB_RETRIEVAL_EMBEDDING_MODEL", raising=False)
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(database, "set_runtime_setting", lambda **kwargs: calls.append(kwargs) or {"key": kwargs["key"]})
    monkeypatch.setattr(database, "enqueue_runtime_control_request", lambda **_kwargs: {"id": "request-1"})

    service = SettingsService()

    with pytest.raises(ValueError, match="confirmation"):
        service.set("retrieval.embedding_model", "Snowflake/snowflake-arctic-embed-l-v2.1", actor="tester")

    result = service.set(
        "retrieval.embedding_model",
        "Snowflake/snowflake-arctic-embed-l-v2.1",
        actor="tester",
        confirmed=True,
    )

    assert result["apply_mode"] == APPLY_REINDEX_REQUIRED
    assert calls[0]["key"] == "retrieval.embedding_model"
    assert calls[0]["value"] == "Snowflake/snowflake-arctic-embed-l-v2.1"


def test_setting_apply_enqueues_runtime_control_command(monkeypatch):
    calls = []
    monkeypatch.setattr(database, "enqueue_runtime_control_apply_command", lambda **kwargs: calls.append(kwargs) or {"accepted": True, "operation_id": "op-apply"})

    result = SettingsService().enqueue_apply(component="api", actor="tester")

    assert result["accepted"] is True
    assert result["operation_id"] == "op-apply"
    assert result["operation_type"] == "runtime_control_apply"
    assert calls == [{"component": "api", "actor": "tester"}]


def test_setting_reset_removes_database_override(monkeypatch):
    calls = []
    monkeypatch.setattr(database, "delete_runtime_setting", lambda **kwargs: calls.append(kwargs) or {"deleted": True})

    result = SettingsService().reset("retrieval.token_budget", actor="tester")

    assert result == {"deleted": True}
    assert calls[0]["key"] == "retrieval.token_budget"
