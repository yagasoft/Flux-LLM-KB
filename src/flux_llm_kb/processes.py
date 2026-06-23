from __future__ import annotations

import subprocess
import sys
from typing import Any


WINDOWS_CREATE_NO_WINDOW = 0x08000000


def run_no_window(*popenargs: Any, **kwargs: Any) -> subprocess.CompletedProcess:
    if "stdin" not in kwargs and "input" not in kwargs:
        kwargs["stdin"] = subprocess.DEVNULL
    if sys.platform == "win32":
        kwargs["creationflags"] = int(kwargs.get("creationflags") or 0) | WINDOWS_CREATE_NO_WINDOW
        startupinfo = kwargs.get("startupinfo")
        if startupinfo is None and hasattr(subprocess, "STARTUPINFO"):
            startupinfo = subprocess.STARTUPINFO()
        if startupinfo is not None:
            startupinfo.dwFlags |= getattr(subprocess, "STARTF_USESHOWWINDOW", 1)
            startupinfo.wShowWindow = 0
            kwargs["startupinfo"] = startupinfo
    return subprocess.run(*popenargs, **kwargs)
