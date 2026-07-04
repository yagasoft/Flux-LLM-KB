from __future__ import annotations

from dataclasses import dataclass
import os
import time
from typing import Any

from . import database
from .redaction import redactions_enabled
from .settings_registry import (
    APPLY_REINDEX_REQUIRED,
    APPLY_RESTART_COMPONENT,
    DEFAULT_CRAWLER_GLOBAL_EXCLUDE_GLOBS,
    SettingDefinition,
    get_definition,
    public_definitions,
)


_RUNTIME_SETTINGS_DB_RETRY_AFTER = 0.0
_RUNTIME_SETTINGS_DB_READER = database.get_runtime_setting


@dataclass(frozen=True)
class ResolvedSetting:
    key: str
    value: Any
    raw_value: Any
    source: str
    sensitive: bool
    category: str
    apply_mode: str
    read_only: bool
    affected_components: tuple[str, ...]
    description: str

    def to_public_dict(self) -> dict[str, Any]:
        return {
            "key": self.key,
            "value": self.value,
            "source": self.source,
            "sensitive": self.sensitive,
            "category": self.category,
            "apply_mode": self.apply_mode,
            "read_only": self.read_only,
            "affected_components": list(self.affected_components),
            "description": self.description,
        }


class SettingsService:
    def list(self) -> list[ResolvedSetting]:
        return [self.resolve(definition.key) for definition in public_definitions()]

    def public_list(self) -> list[dict[str, Any]]:
        return [setting.to_public_dict() for setting in self.list()]

    def resolve(self, key: str) -> ResolvedSetting:
        global _RUNTIME_SETTINGS_DB_RETRY_AFTER
        definition = get_definition(key)
        source = "default"
        raw_value = definition.default
        if definition.env_var and definition.env_var in os.environ:
            raw_value = os.environ[definition.env_var]
            source = "env"
        else:
            stored = None
            now = time.monotonic()
            reader = database.get_runtime_setting
            reader_replaced = reader is not _RUNTIME_SETTINGS_DB_READER
            if reader_replaced or now >= _RUNTIME_SETTINGS_DB_RETRY_AFTER:
                try:
                    stored = reader(key)
                    if not reader_replaced:
                        _RUNTIME_SETTINGS_DB_RETRY_AFTER = 0.0
                except Exception:
                    if not reader_replaced:
                        _RUNTIME_SETTINGS_DB_RETRY_AFTER = now + 30.0
            if stored is not None:
                raw_value = stored["value"]
                source = "db"
        validated = definition.validate(raw_value)
        if source == "db" and definition.key == "crawler.global_exclude_globs":
            validated = _append_missing_defaults(validated, DEFAULT_CRAWLER_GLOBAL_EXCLUDE_GLOBS)
        public_value = "***" if definition.sensitive and validated not in {None, ""} and redactions_enabled() else validated
        return _resolved(definition, value=public_value, raw_value=validated, source=source)

    def set(
        self,
        key: str,
        value: Any,
        *,
        actor: str = "cli",
        reason: str | None = None,
        confirmed: bool = False,
    ) -> dict[str, Any]:
        definition = get_definition(key)
        if definition.read_only:
            raise ValueError(f"setting is read-only: {key}")
        parsed = definition.validate(value)
        if _requires_confirmation(definition) and not confirmed:
            raise ValueError(f"setting {key} requires confirmation")
        database.set_runtime_setting(key=key, value=parsed, actor=actor, reason=reason)
        if definition.apply_mode in {APPLY_REINDEX_REQUIRED, APPLY_RESTART_COMPONENT}:
            database.enqueue_runtime_control_request(
                setting_key=key,
                action=definition.apply_mode,
                affected_components=list(definition.affected_components),
                actor=actor,
            )
        return self.resolve(key).to_public_dict()

    def reset(self, key: str, *, actor: str = "cli") -> dict[str, Any]:
        get_definition(key)
        return database.delete_runtime_setting(key=key, actor=actor)

    def apply(self, *, component: str | None = None, actor: str = "cli") -> dict[str, Any]:
        return database.ack_runtime_control_requests(component=component, actor=actor)


def _resolved(definition: SettingDefinition, *, value: Any, raw_value: Any, source: str) -> ResolvedSetting:
    return ResolvedSetting(
        key=definition.key,
        value=value,
        raw_value=raw_value,
        source=source,
        sensitive=definition.sensitive,
        category=definition.category,
        apply_mode=definition.apply_mode,
        read_only=definition.read_only,
        affected_components=definition.affected_components,
        description=definition.description,
    )


def _requires_confirmation(definition: SettingDefinition) -> bool:
    return definition.apply_mode in {APPLY_REINDEX_REQUIRED, APPLY_RESTART_COMPONENT}


def _append_missing_defaults(value: list[str], defaults: list[str]) -> list[str]:
    reconciled = list(value)
    seen = set(reconciled)
    for pattern in defaults:
        if pattern not in seen:
            reconciled.append(pattern)
            seen.add(pattern)
    return reconciled
