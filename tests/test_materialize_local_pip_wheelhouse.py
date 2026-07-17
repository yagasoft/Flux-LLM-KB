from __future__ import annotations

import subprocess
import sys
import zipfile
from pathlib import Path


SCRIPT = Path("scripts/deploy/materialize-local-pip-wheelhouse.py")


def _write_cached_wheel(path: Path, *, distribution: str, version: str, tag: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    dist_info = f"{distribution}-{version}.dist-info"
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr(
            f"{dist_info}/METADATA",
            f"Metadata-Version: 2.1\nName: {distribution}\nVersion: {version}\n",
        )
        archive.writestr(
            f"{dist_info}/WHEEL",
            f"Wheel-Version: 1.0\nRoot-Is-Purelib: false\nTag: {tag}\n",
        )
        archive.writestr(f"{dist_info}/RECORD", "")
        archive.writestr("win32api.pyd", b"cached-binary")


def test_materializes_requested_wheel_from_local_pip_http_cache(tmp_path: Path) -> None:
    cache_dir = tmp_path / "pip-cache"
    cached_wheel = cache_dir / "http-v2" / "a" / "cached.body"
    _write_cached_wheel(
        cached_wheel,
        distribution="pywin32",
        version="312",
        tag="cp312-cp312-win_amd64",
    )
    wheelhouse = tmp_path / "wheelhouse"

    result = subprocess.run(
        [
            sys.executable,
            str(SCRIPT),
            "--cache-dir",
            str(cache_dir),
            "--wheelhouse",
            str(wheelhouse),
            "--require",
            "pywin32",
        ],
        capture_output=True,
        text=True,
        check=False,
    )

    assert result.returncode == 0, result.stderr
    materialized = wheelhouse / "pywin32-312-cp312-cp312-win_amd64.whl"
    assert materialized.read_bytes() == cached_wheel.read_bytes()
    assert "materialized pywin32" in result.stdout


def test_rejects_missing_required_wheel_from_local_pip_cache(tmp_path: Path) -> None:
    cache_dir = tmp_path / "pip-cache"
    (cache_dir / "http-v2").mkdir(parents=True)
    wheelhouse = tmp_path / "wheelhouse"

    result = subprocess.run(
        [
            sys.executable,
            str(SCRIPT),
            "--cache-dir",
            str(cache_dir),
            "--wheelhouse",
            str(wheelhouse),
            "--require",
            "pywin32",
        ],
        capture_output=True,
        text=True,
        check=False,
    )

    assert result.returncode != 0
    assert "No local pip-cache wheel is available for: pywin32" in result.stderr
