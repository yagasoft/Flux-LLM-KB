from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import time
from typing import Callable


@dataclass(frozen=True)
class WatchRoot:
    name: str
    root_path: Path
    watch_enabled: bool = False
    recursive: bool = True


@dataclass(frozen=True)
class WatchEvent:
    root_name: str
    root_path: Path
    path: Path
    relative_path: str
    action: str


class PollingCorpusWatcher:
    def __init__(
        self,
        roots: list[WatchRoot],
        *,
        on_change: Callable[[WatchEvent], None],
        interval_seconds: float = 2.0,
    ) -> None:
        self.roots = roots
        self.on_change = on_change
        self.interval_seconds = interval_seconds
        self._snapshots: dict[str, dict[str, tuple[int, int]]] = {}

    def poll_once(self, *, seed: bool = False) -> list[WatchEvent]:
        emitted: list[WatchEvent] = []
        for root in self.roots:
            if not root.watch_enabled:
                continue
            previous = self._snapshots.get(root.name, {})
            current = _snapshot(root)
            self._snapshots[root.name] = current
            if seed or root.name not in self._snapshots:
                continue
            for relative_path, fingerprint in current.items():
                if previous.get(relative_path) == fingerprint:
                    continue
                event = _event(root, relative_path, "changed")
                self.on_change(event)
                emitted.append(event)
            for relative_path in previous.keys() - current.keys():
                event = _event(root, relative_path, "deleted")
                self.on_change(event)
                emitted.append(event)
        return emitted

    def run_forever(self) -> None:
        self.poll_once(seed=True)
        while True:
            self.poll_once()
            time.sleep(self.interval_seconds)


def _snapshot(root: WatchRoot) -> dict[str, tuple[int, int]]:
    root_path = root.root_path.expanduser().resolve()
    iterator = root_path.rglob("*") if root.recursive else root_path.iterdir()
    snapshot: dict[str, tuple[int, int]] = {}
    for path in iterator:
        if not path.is_file():
            continue
        stat = path.stat()
        snapshot[path.relative_to(root_path).as_posix()] = (stat.st_size, stat.st_mtime_ns)
    return snapshot


def _event(root: WatchRoot, relative_path: str, action: str) -> WatchEvent:
    root_path = root.root_path.expanduser().resolve()
    return WatchEvent(
        root_name=root.name,
        root_path=root_path,
        path=root_path / relative_path,
        relative_path=relative_path,
        action=action,
    )
