from __future__ import annotations

from dataclasses import dataclass
import re
from typing import Any

from .redaction import redactions_enabled


Severity = str

_SECRET_KEY_RE = re.compile(r"(password|secret|token|api[_-]?key|authorization|credential)", re.IGNORECASE)
_SECRET_ASSIGNMENT_RE = re.compile(
    r"(?i)\b(password|secret|token|api[_-]?key|authorization|credential)(\s*[:=]\s*)([^\s,;]+)"
)


@dataclass
class FluxApiError(Exception):
    code: str
    message: str
    status_code: int = 400
    severity: Severity = "error"
    component: str = "api"
    stage: str | None = None
    retryable: bool = False
    user_action: str | None = None
    technical_detail: str | None = None
    target: dict[str, str] | None = None
    links: list[dict[str, str]] | None = None

    def __post_init__(self) -> None:
        super().__init__(self.message)

    def envelope(self) -> dict[str, Any]:
        return error_envelope(
            code=self.code,
            message=self.message,
            severity=self.severity,
            component=self.component,
            stage=self.stage,
            retryable=self.retryable,
            user_action=self.user_action,
            technical_detail=self.technical_detail,
            target=self.target,
            links=self.links,
            status_code=self.status_code,
        )


def error_envelope(
    *,
    code: str,
    message: str,
    severity: Severity = "error",
    component: str = "api",
    stage: str | None = None,
    retryable: bool = False,
    user_action: str | None = None,
    technical_detail: str | None = None,
    target: dict[str, str] | None = None,
    links: list[dict[str, str]] | None = None,
    status_code: int | None = None,
) -> dict[str, Any]:
    clean_message = redact_secrets(str(message))
    return {
        "code": code,
        "message": clean_message,
        "severity": severity if severity in {"error", "warning", "info"} else "error",
        "component": component,
        "stage": stage,
        "retryable": bool(retryable),
        "user_action": redact_secrets(user_action) if user_action else None,
        "technical_detail": redact_secrets(technical_detail) if technical_detail else clean_message,
        "target": redact_secret_values(target or {}),
        "links": [redact_secret_values(link) for link in (links or [])],
        "status_code": status_code,
    }


def error_response_payload(envelope: dict[str, Any]) -> dict[str, Any]:
    message = str(envelope.get("message") or "Request failed")
    return {"detail": message, "message": message, "error": envelope}


def http_error_envelope(status_code: int, detail: Any) -> dict[str, Any]:
    message = _detail_message(detail)
    code = {
        400: "api.bad_request",
        401: "api.unauthorized",
        403: "api.forbidden",
        404: "api.not_found",
        409: "api.conflict",
        422: "api.request_invalid",
    }.get(status_code, "api.error")
    return error_envelope(
        code=code,
        message=message,
        component="api",
        retryable=status_code >= 500,
        user_action="Review the request and try again." if status_code < 500 else "Retry after the service recovers.",
        technical_detail=message,
        status_code=status_code,
    )


def validation_error_envelope(errors: list[dict[str, Any]]) -> dict[str, Any]:
    parts: list[str] = []
    for item in errors:
        loc = ".".join(str(part) for part in item.get("loc", []) if part != "body")
        msg = str(item.get("msg") or "invalid value")
        parts.append(f"{loc}: {msg}" if loc else msg)
    message = "Request body is invalid"
    if parts:
        message = f"{message}: {'; '.join(parts)}"
    return error_envelope(
        code="api.request_invalid",
        message=message,
        component="api",
        retryable=False,
        user_action="Fix the highlighted request fields and try again.",
        technical_detail=message,
        status_code=422,
    )


def redact_secrets(value: str | None) -> str | None:
    if value is None:
        return None
    if not redactions_enabled():
        return str(value)
    return _SECRET_ASSIGNMENT_RE.sub(lambda match: f"{match.group(1)}{match.group(2)}***", str(value))


def redact_secret_values(value: dict[str, Any]) -> dict[str, Any]:
    if not redactions_enabled():
        return dict(value)
    cleaned: dict[str, Any] = {}
    for key, item in value.items():
        key_text = str(key)
        if _SECRET_KEY_RE.search(key_text):
            cleaned[key_text] = "***"
        elif isinstance(item, dict):
            cleaned[key_text] = redact_secret_values(item)
        elif isinstance(item, str):
            cleaned[key_text] = redact_secrets(item)
        else:
            cleaned[key_text] = item
    return cleaned


def coerce_error_detail(value: Any, *, default_code: str = "runtime.error", default_component: str = "runtime") -> dict[str, Any]:
    if isinstance(value, dict) and "code" in value and "message" in value:
        return error_envelope(
            code=str(value.get("code") or default_code),
            message=str(value.get("message") or "unknown error"),
            severity=str(value.get("severity") or "error"),
            component=str(value.get("component") or default_component),
            stage=value.get("stage") if isinstance(value.get("stage"), str) else None,
            retryable=bool(value.get("retryable", False)),
            user_action=value.get("user_action") if isinstance(value.get("user_action"), str) else None,
            technical_detail=value.get("technical_detail") if isinstance(value.get("technical_detail"), str) else None,
            target=value.get("target") if isinstance(value.get("target"), dict) else None,
            links=value.get("links") if isinstance(value.get("links"), list) else None,
            status_code=value.get("status_code") if isinstance(value.get("status_code"), int) else None,
        )
    return error_envelope(
        code=default_code,
        message=_detail_message(value),
        component=default_component,
        severity="error",
        retryable=True,
        user_action="Open the related dashboard panel and review the failing component.",
        status_code=None,
    )


def _detail_message(detail: Any) -> str:
    if isinstance(detail, str):
        return detail
    if isinstance(detail, list):
        return "; ".join(_detail_message(item) for item in detail)
    if isinstance(detail, dict):
        if "message" in detail:
            return str(detail["message"])
        if "detail" in detail:
            return _detail_message(detail["detail"])
        return ", ".join(f"{key}={item}" for key, item in detail.items())
    return str(detail or "Request failed")
