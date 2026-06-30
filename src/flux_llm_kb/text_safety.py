from __future__ import annotations

import codecs
from pathlib import Path
from typing import Any


def strip_postgres_nul(value: str) -> str:
    return value.replace("\x00", "")


def sanitize_postgres_text_value(value: Any) -> Any:
    if value is None or isinstance(value, (bool, int, float)):
        return value
    if isinstance(value, str):
        return strip_postgres_nul(value)
    if isinstance(value, dict):
        return {
            (
                strip_postgres_nul(key)
                if isinstance(key, str)
                else key
            ): sanitize_postgres_text_value(item)
            for key, item in value.items()
        }
    if isinstance(value, list):
        return [sanitize_postgres_text_value(item) for item in value]
    if isinstance(value, tuple):
        return [sanitize_postgres_text_value(item) for item in value]
    return value


def decode_text_bytes(data: bytes) -> str:
    if data.startswith(codecs.BOM_UTF8):
        return data.decode("utf-8-sig", errors="replace")
    if data.startswith(codecs.BOM_UTF32_LE) or data.startswith(codecs.BOM_UTF32_BE):
        return data.decode("utf-32", errors="replace")
    if data.startswith(codecs.BOM_UTF16_LE) or data.startswith(codecs.BOM_UTF16_BE):
        return data.decode("utf-16", errors="replace")
    return data.decode("utf-8", errors="replace")


def read_text_with_bom(path: str | Path) -> str:
    return _normalize_newlines(decode_text_bytes(Path(path).read_bytes()))


def _normalize_newlines(value: str) -> str:
    return value.replace("\r\n", "\n").replace("\r", "\n")
