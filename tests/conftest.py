from __future__ import annotations

import os
import sys
from pathlib import Path

import pytest


_SRC = str(Path(__file__).resolve().parents[1] / "src")

if _SRC not in sys.path:
    sys.path.insert(0, _SRC)

_existing_pythonpath = os.environ.get("PYTHONPATH")
if _existing_pythonpath:
    paths = _existing_pythonpath.split(os.pathsep)
    if _SRC not in paths:
        os.environ["PYTHONPATH"] = os.pathsep.join([_SRC, *paths])
else:
    os.environ["PYTHONPATH"] = _SRC


@pytest.fixture(autouse=True)
def _isolate_gpu_scheduler_for_unit_tests(request, monkeypatch):
    if Path(str(request.fspath)).name != "test_settings.py":
        monkeypatch.setenv("FLUX_KB_GPU_SCHEDULER_MODE", "in_process")
    try:
        from flux_llm_kb.gpu_scheduler import reset_gpu_scheduler_for_tests
    except Exception:
        yield
        return
    reset_gpu_scheduler_for_tests()
    try:
        yield
    finally:
        reset_gpu_scheduler_for_tests()
