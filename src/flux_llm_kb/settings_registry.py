from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Callable

from .database import DEFAULT_DATABASE_URL
from .embeddings import DEFAULT_EMBEDDING_DIMENSIONS, DEFAULT_EMBEDDING_MODEL


APPLY_LIVE = "live"
APPLY_RELOAD = "reload"
APPLY_RESTART_COMPONENT = "restart_component"
APPLY_REINDEX_REQUIRED = "reindex_required"
APPLY_MANUAL_PROCESS_RESTART = "manual_process_restart"


@dataclass(frozen=True)
class SettingDefinition:
    key: str
    category: str
    default: Any
    value_type: str
    description: str
    env_var: str | None = None
    sensitive: bool = False
    read_only: bool = False
    apply_mode: str = APPLY_LIVE
    affected_components: tuple[str, ...] = ()
    validator: Callable[[Any], Any] | None = None

    def validate(self, value: Any) -> Any:
        parsed = _coerce(value, self.value_type)
        if self.validator:
            parsed = self.validator(parsed)
        return parsed


def get_definition(key: str) -> SettingDefinition:
    try:
        return _REGISTRY_BY_KEY[key]
    except KeyError as exc:
        raise KeyError(f"unknown setting: {key}") from exc


def public_definitions() -> list[SettingDefinition]:
    return sorted(SETTING_REGISTRY, key=lambda item: item.key)


def _coerce(value: Any, value_type: str) -> Any:
    if value_type == "str":
        return str(value)
    if value_type == "int":
        return int(value)
    if value_type == "float":
        return float(value)
    if value_type == "bool":
        if isinstance(value, bool):
            return value
        lowered = str(value).strip().lower()
        if lowered in {"1", "true", "yes", "on"}:
            return True
        if lowered in {"0", "false", "no", "off"}:
            return False
        raise ValueError(f"invalid boolean value: {value}")
    if value_type == "list[str]":
        if isinstance(value, (list, tuple)):
            return [str(item) for item in value]
        return [item.strip() for item in str(value).split(",") if item.strip()]
    return value


def _min_int(minimum: int) -> Callable[[Any], Any]:
    def validate(value: Any) -> int:
        parsed = int(value)
        if parsed < minimum:
            raise ValueError(f"value must be >= {minimum}")
        return parsed

    return validate


def _range_int(minimum: int, maximum: int) -> Callable[[Any], Any]:
    def validate(value: Any) -> int:
        parsed = int(value)
        if parsed < minimum or parsed > maximum:
            raise ValueError(f"value must be between {minimum} and {maximum}")
        return parsed

    return validate


def _choice(*choices: str) -> Callable[[Any], Any]:
    def validate(value: Any) -> str:
        parsed = str(value)
        if parsed not in choices:
            raise ValueError(f"value must be one of: {', '.join(choices)}")
        return parsed

    return validate


SETTING_REGISTRY: tuple[SettingDefinition, ...] = (
    SettingDefinition(
        key="database.url",
        category="bootstrap",
        default=DEFAULT_DATABASE_URL,
        value_type="str",
        description="Primary PostgreSQL connection string.",
        env_var="FLUX_KB_DATABASE_URL",
        sensitive=True,
        read_only=True,
        apply_mode=APPLY_MANUAL_PROCESS_RESTART,
        affected_components=("database", "api", "cli", "mcp"),
    ),
    SettingDefinition(
        key="database.test_url",
        category="bootstrap",
        default="",
        value_type="str",
        description="Optional PostgreSQL test database connection string.",
        env_var="FLUX_KB_TEST_DATABASE_URL",
        sensitive=True,
        read_only=True,
        apply_mode=APPLY_MANUAL_PROCESS_RESTART,
        affected_components=("tests",),
    ),
    SettingDefinition(
        key="api.host",
        category="bootstrap",
        default="127.0.0.1",
        value_type="str",
        description="REST API bind host.",
        env_var="FLUX_KB_API_HOST",
        read_only=True,
        apply_mode=APPLY_MANUAL_PROCESS_RESTART,
        affected_components=("api",),
    ),
    SettingDefinition(
        key="api.port",
        category="bootstrap",
        default=8765,
        value_type="int",
        description="REST API bind port.",
        env_var="FLUX_KB_API_PORT",
        read_only=True,
        apply_mode=APPLY_MANUAL_PROCESS_RESTART,
        affected_components=("api",),
        validator=_range_int(1, 65535),
    ),
    SettingDefinition(
        key="capture.enabled",
        category="capture",
        default=True,
        value_type="bool",
        description="Enable automatic capture paths when integrations call them.",
        env_var="FLUX_KB_CAPTURE_ENABLED",
        apply_mode=APPLY_RELOAD,
        affected_components=("hooks", "mail"),
    ),
    SettingDefinition(
        key="retrieval.token_budget",
        category="retrieval",
        default=1200,
        value_type="int",
        description="Default context brief token budget.",
        env_var="FLUX_KB_TOKEN_BUDGET",
        affected_components=("retrieval",),
        validator=_min_int(128),
    ),
    SettingDefinition(
        key="retrieval.default_limit",
        category="retrieval",
        default=5,
        value_type="int",
        description="Default result limit for search endpoints.",
        env_var="FLUX_KB_RETRIEVAL_DEFAULT_LIMIT",
        affected_components=("retrieval",),
        validator=_range_int(1, 50),
    ),
    SettingDefinition(
        key="embedding.model",
        category="retrieval",
        default=DEFAULT_EMBEDDING_MODEL,
        value_type="str",
        description="Embedding model/provider identifier used for new embeddings.",
        env_var="FLUX_KB_EMBEDDING_MODEL",
        apply_mode=APPLY_REINDEX_REQUIRED,
        affected_components=("retrieval", "worker"),
    ),
    SettingDefinition(
        key="embedding.dimensions",
        category="retrieval",
        default=DEFAULT_EMBEDDING_DIMENSIONS,
        value_type="int",
        description="Embedding dimensionality for new embeddings.",
        env_var="FLUX_KB_EMBEDDING_DIMENSIONS",
        apply_mode=APPLY_REINDEX_REQUIRED,
        affected_components=("retrieval", "worker", "database"),
        validator=_min_int(64),
    ),
    SettingDefinition(
        key="crawler.max_inline_bytes",
        category="crawler",
        default=256 * 1024,
        value_type="int",
        description="Default maximum file size for inline text extraction.",
        env_var="FLUX_KB_CRAWLER_MAX_INLINE_BYTES",
        affected_components=("crawler",),
        validator=_min_int(1),
    ),
    SettingDefinition(
        key="crawler.heavy_threshold_bytes",
        category="crawler",
        default=10 * 1024 * 1024,
        value_type="int",
        description="Default size above which files are deferred to background jobs.",
        env_var="FLUX_KB_CRAWLER_HEAVY_THRESHOLD_BYTES",
        affected_components=("crawler", "worker"),
        validator=_min_int(1),
    ),
    SettingDefinition(
        key="crawler.global_include_globs",
        category="crawler",
        default=[],
        value_type="list[str]",
        description="Global include glob defaults inherited by monitored roots unless overridden.",
        env_var="FLUX_KB_CRAWLER_GLOBAL_INCLUDE_GLOBS",
        apply_mode=APPLY_RELOAD,
        affected_components=("crawler", "watcher", "dashboard"),
    ),
    SettingDefinition(
        key="crawler.global_exclude_globs",
        category="crawler",
        default=["private/**", "node_modules/**", ".git/**"],
        value_type="list[str]",
        description="Global exclude glob defaults inherited by monitored roots unless overridden.",
        env_var="FLUX_KB_CRAWLER_GLOBAL_EXCLUDE_GLOBS",
        apply_mode=APPLY_RELOAD,
        affected_components=("crawler", "watcher", "dashboard"),
    ),
    SettingDefinition(
        key="watcher.interval_seconds",
        category="watcher",
        default=2.0,
        value_type="float",
        description="Filesystem watcher polling/reload interval.",
        env_var="FLUX_KB_WATCHER_INTERVAL_SECONDS",
        apply_mode=APPLY_RESTART_COMPONENT,
        affected_components=("watcher",),
    ),
    SettingDefinition(
        key="watcher.debounce_seconds",
        category="watcher",
        default=0.75,
        value_type="float",
        description="Debounce window for repeated filesystem events.",
        env_var="FLUX_KB_WATCHER_DEBOUNCE_SECONDS",
        apply_mode=APPLY_RESTART_COMPONENT,
        affected_components=("watcher",),
    ),
    SettingDefinition(
        key="watcher.max_queue_size",
        category="watcher",
        default=1000,
        value_type="int",
        description="Maximum pending watcher events before suppression.",
        env_var="FLUX_KB_WATCHER_MAX_QUEUE_SIZE",
        apply_mode=APPLY_RESTART_COMPONENT,
        affected_components=("watcher",),
        validator=_min_int(1),
    ),
    SettingDefinition(
        key="watcher.stale_after_seconds",
        category="watcher",
        default=120,
        value_type="int",
        description="Heartbeat age after which a watcher is marked stale.",
        env_var="FLUX_KB_WATCHER_STALE_AFTER_SECONDS",
        affected_components=("dashboard", "watcher"),
        validator=_min_int(1),
    ),
    SettingDefinition(
        key="watcher.reconcile_on_start",
        category="watcher",
        default=True,
        value_type="bool",
        description="Run a full watched-root reconciliation when watcher services start.",
        env_var="FLUX_KB_WATCHER_RECONCILE_ON_START",
        apply_mode=APPLY_RESTART_COMPONENT,
        affected_components=("watcher", "host-agent"),
    ),
    SettingDefinition(
        key="watcher.reconcile_interval_seconds",
        category="watcher",
        default=3600,
        value_type="int",
        description="Periodic full reconciliation interval for enabled watched roots.",
        env_var="FLUX_KB_WATCHER_RECONCILE_INTERVAL_SECONDS",
        apply_mode=APPLY_RESTART_COMPONENT,
        affected_components=("watcher", "host-agent"),
        validator=_min_int(60),
    ),
    SettingDefinition(
        key="worker.batch_size",
        category="worker",
        default=10,
        value_type="int",
        description="Default corpus/mail job batch size.",
        env_var="FLUX_KB_WORKER_BATCH_SIZE",
        affected_components=("worker",),
        validator=_min_int(1),
    ),
    SettingDefinition(
        key="worker.retry_cooldown_seconds",
        category="worker",
        default=300,
        value_type="int",
        description="Default retry cooldown for failed background jobs.",
        env_var="FLUX_KB_WORKER_RETRY_COOLDOWN_SECONDS",
        affected_components=("worker",),
        validator=_min_int(1),
    ),
    SettingDefinition(
        key="dashboard.poll_interval_seconds",
        category="dashboard",
        default=10,
        value_type="int",
        description="Dashboard JSON refresh interval.",
        env_var="FLUX_KB_DASHBOARD_POLL_INTERVAL_SECONDS",
        affected_components=("dashboard",),
        validator=_min_int(1),
    ),
    SettingDefinition(
        key="mail.imap.poll_interval_seconds",
        category="mail",
        default=60,
        value_type="int",
        description="IMAP reconciliation interval for monitored folders.",
        env_var="FLUX_KB_MAIL_IMAP_POLL_INTERVAL_SECONDS",
        apply_mode=APPLY_RESTART_COMPONENT,
        affected_components=("mail",),
        validator=_min_int(5),
    ),
    SettingDefinition(
        key="mail.imap.use_idle",
        category="mail",
        default=True,
        value_type="bool",
        description="Prefer IMAP IDLE when available, with polling reconciliation.",
        env_var="FLUX_KB_MAIL_IMAP_USE_IDLE",
        apply_mode=APPLY_RESTART_COMPONENT,
        affected_components=("mail",),
    ),
    SettingDefinition(
        key="mail.imap.oauth_refresh_token",
        category="mail",
        default="",
        value_type="str",
        description="Legacy manual IMAP XOAUTH2 token override. Prefer profile OAuth setup.",
        env_var="FLUX_KB_MAIL_IMAP_OAUTH_REFRESH_TOKEN",
        sensitive=True,
        apply_mode=APPLY_RELOAD,
        affected_components=("mail",),
    ),
    SettingDefinition(
        key="mail.oauth.google.client_config_path",
        category="mail",
        default="",
        value_type="str",
        description="Optional default private Google OAuth desktop client JSON path.",
        env_var="FLUX_KB_MAIL_GOOGLE_CLIENT_CONFIG_PATH",
        sensitive=True,
        affected_components=("mail", "dashboard"),
    ),
    SettingDefinition(
        key="mail.oauth.google.redirect_uri",
        category="mail",
        default="http://127.0.0.1:8765/api/mail/oauth/gmail/callback",
        value_type="str",
        description="Loopback redirect URI for Gmail installed-app OAuth.",
        env_var="FLUX_KB_MAIL_GOOGLE_REDIRECT_URI",
        apply_mode=APPLY_RELOAD,
        affected_components=("mail", "dashboard"),
    ),
    SettingDefinition(
        key="mail.oauth.google.scope",
        category="mail",
        default="https://mail.google.com/",
        value_type="str",
        description="Gmail IMAP XOAUTH2 scope.",
        env_var="FLUX_KB_MAIL_GOOGLE_SCOPE",
        read_only=True,
        affected_components=("mail",),
    ),
    SettingDefinition(
        key="mail.post_process.default_policy",
        category="mail",
        default="move_to_processed",
        value_type="str",
        description="Default action after successful mail export.",
        env_var="FLUX_KB_MAIL_POST_PROCESS_DEFAULT_POLICY",
        apply_mode=APPLY_RELOAD,
        affected_components=("mail",),
        validator=_choice("none", "move_to_processed", "remove_label", "trash"),
    ),
)

_REGISTRY_BY_KEY = {definition.key: definition for definition in SETTING_REGISTRY}
