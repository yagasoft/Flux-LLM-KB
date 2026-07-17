from __future__ import annotations

import argparse
import re
import shutil
import sys
import zipfile
from pathlib import Path


def _normalise_distribution(value: str) -> str:
    return re.sub(r"[-_.]+", "-", value).lower()


def _metadata_value(text: str, field: str) -> str | None:
    prefix = f"{field}:"
    for line in text.splitlines():
        if line.startswith(prefix):
            return line[len(prefix) :].strip()
    return None


def _wheel_tags(text: str) -> list[str]:
    return [line.partition(":")[2].strip() for line in text.splitlines() if line.startswith("Tag:")]


def _cached_wheel_metadata(path: Path) -> tuple[str, str, list[str]] | None:
    try:
        with zipfile.ZipFile(path) as archive:
            metadata_path = next(
                name for name in archive.namelist() if name.endswith(".dist-info/METADATA")
            )
            dist_info_root = metadata_path.rsplit("/", 1)[0]
            wheel_path = f"{dist_info_root}/WHEEL"
            metadata = archive.read(metadata_path).decode("utf-8", "replace")
            wheel = archive.read(wheel_path).decode("utf-8", "replace")
    except (KeyError, OSError, StopIteration, zipfile.BadZipFile):
        return None

    name = _metadata_value(metadata, "Name")
    version = _metadata_value(metadata, "Version")
    tags = _wheel_tags(wheel)
    if not name or not version or not tags:
        return None
    return name, version, tags


def _existing_distributions(wheelhouse: Path) -> set[str]:
    return {
        _normalise_distribution(path.name.split("-", 1)[0])
        for path in wheelhouse.glob("*.whl")
        if "-" in path.name
    }


def materialize(cache_dir: Path, wheelhouse: Path, required: list[str]) -> set[str]:
    required_by_key = {_normalise_distribution(name): name for name in required}
    wheelhouse.mkdir(parents=True, exist_ok=True)
    found = _existing_distributions(wheelhouse) & set(required_by_key)
    http_cache = cache_dir / "http-v2"
    if not http_cache.is_dir():
        return found

    for cached_body in http_cache.rglob("*.body"):
        metadata = _cached_wheel_metadata(cached_body)
        if metadata is None:
            continue
        name, version, tags = metadata
        key = _normalise_distribution(name)
        if key not in required_by_key:
            continue
        filename_name = re.sub(r"-+", "_", name)
        for tag in tags:
            target = wheelhouse / f"{filename_name}-{version}-{tag}.whl"
            if not target.exists():
                shutil.copyfile(cached_body, target)
                print(f"materialized {name}: {target.name}")
        found.add(key)
    return found


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Materialize cached wheel responses from pip's local HTTP cache."
    )
    parser.add_argument("--cache-dir", type=Path, required=True)
    parser.add_argument("--wheelhouse", type=Path, required=True)
    parser.add_argument("--require", action="append", required=True)
    args = parser.parse_args()

    required_by_key = {_normalise_distribution(name): name for name in args.require}
    found = materialize(args.cache_dir, args.wheelhouse, args.require)
    missing = [required_by_key[key] for key in required_by_key if key not in found]
    if missing:
        print(
            f"No local pip-cache wheel is available for: {', '.join(missing)}",
            file=sys.stderr,
        )
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
