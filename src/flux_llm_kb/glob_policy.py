from __future__ import annotations

from typing import Any


GLOB_MODE_INHERIT = "inherit"
GLOB_MODE_EXTEND = "extend"
GLOB_MODE_OVERRIDE = "override"
VALID_GLOB_MODES = {GLOB_MODE_INHERIT, GLOB_MODE_EXTEND, GLOB_MODE_OVERRIDE}


def effective_glob_policy(
    root: dict[str, Any],
    *,
    global_include: list[str] | tuple[str, ...] = (),
    global_exclude: list[str] | tuple[str, ...] = (),
) -> dict[str, Any]:
    mode = str(root.get("glob_mode") or root.get("metadata", {}).get("glob_mode") or GLOB_MODE_EXTEND)
    if mode not in VALID_GLOB_MODES:
        mode = GLOB_MODE_EXTEND
    root_include = _clean(root.get("include_globs", []))
    root_exclude = _clean(root.get("exclude_globs", []))
    base_include = _clean(global_include)
    base_exclude = _clean(global_exclude)

    if mode == GLOB_MODE_INHERIT:
        include_globs = base_include
        exclude_globs = base_exclude
    elif mode == GLOB_MODE_OVERRIDE:
        include_globs = root_include
        exclude_globs = root_exclude
    else:
        include_globs = [*base_include, *root_include]
        exclude_globs = [*base_exclude, *root_exclude]

    return {
        "mode": mode,
        "include_globs": _dedupe(include_globs),
        "exclude_globs": _dedupe(exclude_globs),
    }


def _clean(values: Any) -> list[str]:
    if isinstance(values, str):
        values = values.replace(",", "\n").splitlines()
    return [str(item).strip() for item in values or [] if str(item).strip()]


def _dedupe(values: list[str]) -> list[str]:
    seen: set[str] = set()
    result: list[str] = []
    for value in values:
        if value in seen:
            continue
        seen.add(value)
        result.append(value)
    return result
