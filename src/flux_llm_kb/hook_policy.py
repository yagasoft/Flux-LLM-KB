from __future__ import annotations

from dataclasses import dataclass
from html.parser import HTMLParser
import ipaddress
from pathlib import Path
import re
import socket
from typing import Any
from urllib.parse import unquote, urlparse
from urllib.request import Request, urlopen

from . import database
from .service import KnowledgeService
from .settings import SettingsService


@dataclass(frozen=True)
class CodexHookPolicySettings:
    enabled: bool = True
    preflight_enabled: bool = True
    capture_enabled: bool = True
    capture_guidance_enabled: bool = True
    reference_indexing_enabled: bool = True
    capture_setting_enabled: bool = True
    reference_max_count: int = 5
    reference_max_bytes: int = 1024 * 1024
    reference_fetch_timeout_seconds: int = 3
    reference_allow_private_urls: bool = False
    token_budget: int = 900
    min_prompt_chars: int = 32
    capture_min_chars: int = 160
    capture_max_chars: int = 8000


CAPTURE_GUIDANCE = """Make the final assistant message indexable for Flux-LLM-KB:
- Summarize concrete decisions, implementation details, and unresolved gaps.
- Include files changed or referenced, commands or tests run, and important web/file references.
- Do not include secrets, tokens, credentials, raw private transcripts, or unnecessary noise."""


NON_CODE_PREFLIGHT_FILTERS = {
    "logical_kinds": ["file"],
    "file_kinds": ["text", "document", "image"],
}


def handle_user_prompt_submit(payload: dict[str, Any]) -> dict[str, Any]:
    settings = load_policy_settings()
    prompt = str(payload.get("prompt") or "")
    skip_reason = _preflight_skip_reason(prompt, settings)
    common = _common_details(payload)
    if skip_reason:
        _audit("codex_hook.preflight_skipped", {**common, "reason": skip_reason, "prompt_chars": len(prompt.strip())})
        return {"continue": True}

    try:
        service = KnowledgeService()
        scope_mode = "local_first"
        results = service.search(prompt, limit=5, cwd=common["cwd"], scope_mode=scope_mode)
        fallback_reason: str | None = None
        brief_filters: dict[str, Any] | None = None
        if _should_rerun_preflight_with_non_code_filters(prompt, results):
            fallback_results = service.search(
                prompt,
                limit=5,
                cwd=common["cwd"],
                scope_mode=scope_mode,
                filters=NON_CODE_PREFLIGHT_FILTERS,
            )
            if _has_relevant_evidence(fallback_results):
                results = fallback_results
                brief_filters = NON_CODE_PREFLIGHT_FILTERS
                fallback_reason = "code_dominated_non_code_prompt"
        retrieval_scope = _retrieval_scope_label(results)
        if not _has_relevant_evidence(results):
            if settings.capture_guidance_enabled:
                _audit(
                    "codex_hook.preflight_guidance",
                    {
                        **common,
                        "reason": "no_relevant_evidence",
                        "result_count": len(results),
                        "scope_mode": scope_mode,
                        "retrieval_scope": "skipped",
                    },
                )
                return _additional_context("UserPromptSubmit", CAPTURE_GUIDANCE)
            _audit(
                "codex_hook.preflight_skipped",
                {
                    **common,
                    "reason": "no_relevant_evidence",
                    "result_count": len(results),
                    "scope_mode": scope_mode,
                    "retrieval_scope": "skipped",
                },
            )
            return {"continue": True}

        brief_kwargs: dict[str, Any] = {
            "token_budget": settings.token_budget,
            "cwd": common["cwd"],
            "scope_mode": scope_mode,
        }
        if brief_filters is not None:
            brief_kwargs["filters"] = brief_filters
        brief = service.brief(prompt, **brief_kwargs).strip()
        if not brief:
            if settings.capture_guidance_enabled:
                _audit(
                    "codex_hook.preflight_guidance",
                    {
                        **common,
                        "reason": "empty_brief",
                        "result_count": len(results),
                        "scope_mode": scope_mode,
                        "retrieval_scope": retrieval_scope,
                    },
                )
                return _additional_context("UserPromptSubmit", CAPTURE_GUIDANCE)
            _audit(
                "codex_hook.preflight_skipped",
                {
                    **common,
                    "reason": "empty_brief",
                    "result_count": len(results),
                    "scope_mode": scope_mode,
                    "retrieval_scope": retrieval_scope,
                },
            )
            return {"continue": True}

        _audit(
            "codex_hook.preflight_injected",
            {
                **common,
                "result_count": len(results),
                "token_budget": settings.token_budget,
                "brief_chars": len(brief),
                "scope_mode": scope_mode,
                "retrieval_scope": retrieval_scope,
                **({"fallback_reason": fallback_reason} if fallback_reason else {}),
            },
        )
        prefix = f"{CAPTURE_GUIDANCE}\n\n" if settings.capture_guidance_enabled else ""
        header = (
            "Flux-LLM-KB global fallback memory"
            if retrieval_scope == "global_fallback"
            else "Flux-LLM-KB relevant memory"
        )
        return _additional_context("UserPromptSubmit", f"{prefix}{header}:\n{brief}")
    except Exception as exc:  # pragma: no cover - exact integration failures are environment-specific
        _audit("codex_hook.preflight_error", {**common, "error": str(exc), "error_type": type(exc).__name__})
        return {"continue": True, "systemMessage": "Flux-LLM-KB preflight failed; continuing without memory brief."}


def handle_stop(payload: dict[str, Any]) -> dict[str, Any]:
    settings = load_policy_settings()
    common = _common_details(payload)
    message = str(payload.get("last_assistant_message") or "").strip()
    skip_reason = _capture_skip_reason(message, settings, payload)
    if skip_reason:
        _audit("codex_hook.capture_skipped", {**common, "reason": skip_reason, "message_chars": len(message)})
        return {"continue": True}

    assert common["session_id"] is not None
    assert common["turn_id"] is not None
    try:
        if database.codex_hook_capture_exists(session_id=common["session_id"], turn_id=common["turn_id"]):
            _audit("codex_hook.capture_skipped", {**common, "reason": "duplicate_turn", "message_chars": len(message)})
            return {"continue": True}

        body, truncated = _truncate_capture(message, settings.capture_max_chars)
        result = KnowledgeService().remember(
            f"Codex turn {common['turn_id']}",
            body,
            metadata={
                "source": "codex_hook_stop",
                "session_id": common["session_id"],
                "turn_id": common["turn_id"],
                "cwd": common["cwd"],
                "model": common["model"],
                "truncated": truncated,
            },
        )
        reference_summary = _index_references(message, settings, common, parent_episode_id=result.id)
        _audit(
            "codex_hook.capture_saved",
            {
                **common,
                "message_chars": len(message),
                "captured_chars": len(body),
                "redaction_count": result.redaction_count,
                **reference_summary,
            },
            target_table="episodes",
            target_id=result.id,
        )
        return {"continue": True}
    except Exception as exc:  # pragma: no cover - exact integration failures are environment-specific
        _audit("codex_hook.capture_error", {**common, "error": str(exc), "error_type": type(exc).__name__})
        return {"continue": True, "systemMessage": "Flux-LLM-KB capture failed; continuing without storing this turn."}


def codex_hook_policy_status() -> dict[str, Any]:
    settings = load_policy_settings()
    if not settings.enabled:
        status = "disabled"
    elif settings.preflight_enabled or (settings.capture_enabled and settings.capture_setting_enabled):
        status = "active"
    else:
        status = "disabled"
    try:
        recent_events = database.recent_codex_hook_audit_events(limit=5)
    except Exception:
        recent_events = []
    return {
        "status": status,
        "enabled": settings.enabled,
        "preflight_enabled": settings.preflight_enabled,
        "capture_enabled": settings.capture_enabled and settings.capture_setting_enabled,
        "capture_guidance_enabled": settings.capture_guidance_enabled,
        "reference_indexing_enabled": settings.reference_indexing_enabled,
        "capture_setting_enabled": settings.capture_setting_enabled,
        "reference_max_count": settings.reference_max_count,
        "reference_max_bytes": settings.reference_max_bytes,
        "reference_fetch_timeout_seconds": settings.reference_fetch_timeout_seconds,
        "reference_allow_private_urls": settings.reference_allow_private_urls,
        "token_budget": settings.token_budget,
        "min_prompt_chars": settings.min_prompt_chars,
        "capture_min_chars": settings.capture_min_chars,
        "capture_max_chars": settings.capture_max_chars,
        "recent_events": recent_events,
    }


def load_policy_settings() -> CodexHookPolicySettings:
    settings = SettingsService()
    return CodexHookPolicySettings(
        enabled=_resolve_bool(settings, "codex.hooks.enabled", True),
        preflight_enabled=_resolve_bool(settings, "codex.hooks.preflight_enabled", True),
        capture_enabled=_resolve_bool(settings, "codex.hooks.capture_enabled", True),
        capture_guidance_enabled=_resolve_bool(settings, "codex.hooks.capture_guidance_enabled", True),
        reference_indexing_enabled=_resolve_bool(settings, "codex.hooks.reference_indexing_enabled", True),
        capture_setting_enabled=_resolve_bool(settings, "capture.enabled", True),
        reference_max_count=_resolve_int(settings, "codex.hooks.reference_max_count", 5),
        reference_max_bytes=_resolve_int(settings, "codex.hooks.reference_max_bytes", 1024 * 1024),
        reference_fetch_timeout_seconds=_resolve_int(settings, "codex.hooks.reference_fetch_timeout_seconds", 3),
        reference_allow_private_urls=_resolve_bool(settings, "codex.hooks.reference_allow_private_urls", False),
        token_budget=_resolve_int(settings, "codex.hooks.token_budget", 900),
        min_prompt_chars=_resolve_int(settings, "codex.hooks.min_prompt_chars", 32),
        capture_min_chars=_resolve_int(settings, "codex.hooks.capture_min_chars", 160),
        capture_max_chars=_resolve_int(settings, "codex.hooks.capture_max_chars", 8000),
    )


def _preflight_skip_reason(prompt: str, settings: CodexHookPolicySettings) -> str | None:
    stripped = prompt.strip()
    if not settings.enabled:
        return "hooks_disabled"
    if not settings.preflight_enabled:
        return "preflight_disabled"
    if not stripped:
        return "empty_prompt"
    if stripped.startswith("/"):
        return "slash_command"
    if len(stripped) < settings.min_prompt_chars:
        return "prompt_too_short"
    if _is_trivial_prompt(stripped):
        return "trivial_prompt"
    return None


def _capture_skip_reason(message: str, settings: CodexHookPolicySettings, payload: dict[str, Any]) -> str | None:
    if not settings.enabled:
        return "hooks_disabled"
    if not settings.capture_enabled or not settings.capture_setting_enabled:
        return "capture_disabled"
    if not payload.get("session_id") or not payload.get("turn_id"):
        return "missing_turn_identity"
    if not message:
        return "empty_message"
    if len(message) < settings.capture_min_chars:
        return "message_too_short"
    return None


def _is_trivial_prompt(prompt: str) -> bool:
    normalized = " ".join(prompt.lower().strip(" .!?\t\r\n").split())
    return normalized in {
        "hi",
        "hello",
        "hey",
        "thanks",
        "thank you",
        "ok",
        "okay",
        "continue",
        "go on",
        "proceed",
        "yes",
        "no",
        "yep",
        "nope",
    }


def _has_relevant_evidence(results: list[dict[str, Any]]) -> bool:
    for result in results:
        streams = {str(stream) for stream in result.get("streams", [])}
        if any("lexical" in stream or "fuzzy" in stream for stream in streams):
            return True
    return False


def _should_rerun_preflight_with_non_code_filters(prompt: str, results: list[dict[str, Any]]) -> bool:
    if database._has_code_implementation_intent(prompt) or _prompt_requests_code_evidence(prompt):
        return False
    if not results:
        return False
    inspected = results[:5]
    code_results = [result for result in inspected if _is_code_result(result)]
    if not code_results:
        return False
    return len(code_results) == len(inspected)


def _prompt_requests_code_evidence(prompt: str) -> bool:
    tokens = {token.lower() for token in re.findall(r"[A-Za-z_]+", prompt or "")}
    return bool(tokens.intersection(database._CODE_NON_IMPLEMENTATION_INTENT_TERMS))


def _is_code_result(result: dict[str, Any]) -> bool:
    file_kind = str(result.get("file_kind") or "").strip().lower().replace("-", "_")
    return file_kind == "code" or isinstance(result.get("code"), dict)


def _retrieval_scope_label(results: list[dict[str, Any]]) -> str:
    scopes = [str(result.get("retrieval_scope") or "global") for result in results]
    if "local" in scopes:
        return "local"
    if "global_fallback" in scopes:
        return "global_fallback"
    if scopes:
        return scopes[0]
    return "none"


def _truncate_capture(message: str, max_chars: int) -> tuple[str, bool]:
    if len(message) <= max_chars:
        return message, False
    marker = "\n\n[truncated]"
    keep = max(0, max_chars - len(marker))
    return f"{message[:keep].rstrip()}{marker}", True


def _index_references(
    message: str,
    settings: CodexHookPolicySettings,
    common: dict[str, Any],
    *,
    parent_episode_id: str,
) -> dict[str, int]:
    summary = {"references_seen": 0, "references_indexed": 0, "references_skipped": 0, "references_failed": 0}
    if not settings.reference_indexing_enabled:
        return summary
    references = _extract_references(message, common.get("cwd"), limit=settings.reference_max_count)
    service = KnowledgeService()
    for reference in references:
        summary["references_seen"] += 1
        details = {**common, "reference": reference["value"], "reference_type": reference["type"]}
        try:
            if _reference_was_indexed(common, reference["value"]):
                summary["references_skipped"] += 1
                _audit("codex_hook.reference_skipped", {**details, "reason": "duplicate_reference"})
                continue
            if reference["type"] == "web":
                result = _index_web_reference(service, reference["value"], settings, common, parent_episode_id)
            else:
                result = _index_file_reference(service, reference["value"], common, parent_episode_id)
            summary["references_indexed"] += 1
            _audit("codex_hook.reference_indexed", {**details, **result})
        except _ReferenceSkipped as skipped:
            summary["references_skipped"] += 1
            _audit("codex_hook.reference_skipped", {**details, "reason": skipped.reason})
        except Exception as exc:  # pragma: no cover - environment-specific
            summary["references_failed"] += 1
            _audit("codex_hook.reference_error", {**details, "error": str(exc), "error_type": type(exc).__name__})
    return summary


class _ReferenceSkipped(Exception):
    def __init__(self, reason: str):
        super().__init__(reason)
        self.reason = reason


def _reference_was_indexed(common: dict[str, Any], reference: str) -> bool:
    try:
        return database.codex_hook_reference_exists(
            session_id=str(common["session_id"] or ""),
            turn_id=str(common["turn_id"] or ""),
            reference=reference,
        )
    except Exception:
        return False


def _index_web_reference(
    service: KnowledgeService,
    url: str,
    settings: CodexHookPolicySettings,
    common: dict[str, Any],
    parent_episode_id: str,
) -> dict[str, Any]:
    if _is_private_url(url, allow_private=settings.reference_allow_private_urls):
        raise _ReferenceSkipped("private_url")
    fetched = _fetch_web_reference(url, settings)
    title = str(fetched.get("title") or url).strip()
    text = str(fetched.get("text") or "").strip()
    if not text:
        raise _ReferenceSkipped("empty_web_text")
    result = service.remember(
        f"Referenced web page: {title}",
        text,
        metadata={
            "source": "codex_hook_reference",
            "reference_type": "web",
            "url": url,
            "session_id": common["session_id"],
            "turn_id": common["turn_id"],
            "cwd": common["cwd"],
            "model": common["model"],
            "parent_episode_id": parent_episode_id,
        },
    )
    return {"episode_id": result.id, "title": title, "chars": len(text)}


def _index_file_reference(
    service: KnowledgeService,
    path_text: str,
    common: dict[str, Any],
    parent_episode_id: str,
) -> dict[str, Any]:
    path = Path(path_text).expanduser().resolve()
    if not path.exists():
        raise _ReferenceSkipped("file_missing")
    root = _monitored_root_for_path(path)
    if root is None:
        raise _ReferenceSkipped("file_not_under_monitored_root")
    result = service.sync_corpus(path=str(path), reason="codex_hook_reference")
    return {
        "root_name": root["name"],
        "path": str(path),
        "parent_episode_id": parent_episode_id,
        "sync": result,
    }


def _extract_references(message: str, cwd: str | None, *, limit: int) -> list[dict[str, str]]:
    references: list[dict[str, str]] = []
    seen: set[str] = set()

    def add(reference_type: str, value: str) -> None:
        cleaned = _clean_reference(value)
        if not cleaned or cleaned in seen or len(references) >= limit:
            return
        seen.add(cleaned)
        references.append({"type": reference_type, "value": cleaned})

    for match in re.finditer(r"https?://[^\s<>\]\)\"']+", message):
        add("web", match.group(0))
    for match in re.finditer(r"file://[^\s<>\]\)\"']+", message):
        parsed = urlparse(match.group(0))
        add("file", unquote(parsed.path))

    masked = re.sub(r"https?://[^\s<>\]\)\"']+", " ", message)
    masked = re.sub(r"file://[^\s<>\]\)\"']+", " ", masked)
    path_pattern = (
        r"(?:[A-Za-z]:\\[^\n\r]+?\.[A-Za-z0-9]{1,8}|"
        r"[A-Za-z0-9_.-]+(?:[/\\][A-Za-z0-9_.-]+)*[/\\][A-Za-z0-9_.-]+\.[A-Za-z0-9]{1,8})"
    )
    for candidate in re.findall(path_pattern, masked):
        path = _resolve_candidate_path(candidate, cwd)
        if path:
            add("file", str(path))
    return references


def _resolve_candidate_path(candidate: str, cwd: str | None) -> Path | None:
    cleaned = _clean_reference(candidate)
    if not cleaned:
        return None
    path = Path(cleaned)
    if not path.is_absolute():
        if cwd is None:
            return None
        path = Path(cwd) / path
    return path


def _clean_reference(value: str) -> str:
    return value.strip().strip("`'\"<>[](){}.,;:")


def _monitored_root_for_path(path: Path) -> dict[str, Any] | None:
    try:
        roots = database.list_monitored_roots()
    except Exception:
        return None
    for root in roots:
        if not root.get("enabled", True):
            continue
        root_path = Path(str(root.get("root_path") or "")).expanduser().resolve()
        try:
            path.relative_to(root_path)
            return root
        except ValueError:
            continue
    return None


def _is_private_url(url: str, *, allow_private: bool) -> bool:
    if allow_private:
        return False
    parsed = urlparse(url)
    if parsed.scheme not in {"http", "https"}:
        return True
    host = parsed.hostname
    if not host:
        return True
    if host.lower() in {"localhost", "localhost.localdomain"}:
        return True
    try:
        addresses = socket.getaddrinfo(host, None)
    except socket.gaierror:
        return False
    for item in addresses:
        ip = ipaddress.ip_address(item[4][0])
        if ip.is_private or ip.is_loopback or ip.is_link_local or ip.is_multicast or ip.is_reserved:
            return True
    return False


def _fetch_web_reference(url: str, settings: CodexHookPolicySettings) -> dict[str, str]:
    request = Request(url, headers={"User-Agent": "Flux-LLM-KB/0.1 reference-indexer"})
    with urlopen(request, timeout=settings.reference_fetch_timeout_seconds) as response:
        data = response.read(settings.reference_max_bytes + 1)
    if len(data) > settings.reference_max_bytes:
        data = data[: settings.reference_max_bytes]
    text = data.decode("utf-8", errors="replace")
    parser = _ReadableHTMLParser()
    parser.feed(text)
    readable = parser.readable_text()
    return {"title": parser.title.strip(), "text": readable or text.strip()}


class _ReadableHTMLParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self._title_parts: list[str] = []
        self._body_parts: list[str] = []
        self._in_title = False
        self._skip_depth = 0

    @property
    def title(self) -> str:
        return " ".join(part.strip() for part in self._title_parts if part.strip())

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        normalized = tag.lower()
        if normalized == "title":
            self._in_title = True
        if normalized in {"script", "style", "noscript", "svg"}:
            self._skip_depth += 1

    def handle_endtag(self, tag: str) -> None:
        normalized = tag.lower()
        if normalized == "title":
            self._in_title = False
        if normalized in {"script", "style", "noscript", "svg"} and self._skip_depth:
            self._skip_depth -= 1

    def handle_data(self, data: str) -> None:
        if self._in_title:
            self._title_parts.append(data)
        elif not self._skip_depth:
            self._body_parts.append(data)

    def readable_text(self) -> str:
        return re.sub(r"\s+", " ", " ".join(self._body_parts)).strip()


def _common_details(payload: dict[str, Any]) -> dict[str, Any]:
    return {
        "session_id": _optional_str(payload.get("session_id")),
        "turn_id": _optional_str(payload.get("turn_id")),
        "cwd": _optional_str(payload.get("cwd")),
        "model": _optional_str(payload.get("model")),
    }


def _additional_context(event_name: str, text: str) -> dict[str, Any]:
    return {"hookSpecificOutput": {"hookEventName": event_name, "additionalContext": text}}


def _audit(
    event_type: str,
    details: dict[str, Any],
    *,
    target_table: str | None = None,
    target_id: str | None = None,
) -> None:
    try:
        database.record_audit_event(
            event_type=event_type,
            target_table=target_table,
            target_id=target_id,
            details=details,
        )
    except Exception:
        return


def _resolve_bool(settings: SettingsService, key: str, fallback: bool) -> bool:
    try:
        return bool(settings.resolve(key).raw_value)
    except Exception:
        return fallback


def _resolve_int(settings: SettingsService, key: str, fallback: int) -> int:
    try:
        return int(settings.resolve(key).raw_value)
    except Exception:
        return fallback


def _optional_str(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if text else None
