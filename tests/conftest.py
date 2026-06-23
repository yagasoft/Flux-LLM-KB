from __future__ import annotations

import os
import sys
from pathlib import Path


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
