from __future__ import annotations

import importlib
from typing import Any, Callable


ONNXRUNTIME_WARNING_SEVERITY = 3


def configure_onnxruntime_logging(
    module_importer: Callable[[str], Any] | None = None,
    *,
    severity: int = ONNXRUNTIME_WARNING_SEVERITY,
) -> Any:
    importer = module_importer or importlib.import_module
    module = importer("onnxruntime")
    set_default_logger_severity = getattr(module, "set_default_logger_severity", None)
    if callable(set_default_logger_severity):
        set_default_logger_severity(severity)
    return module
