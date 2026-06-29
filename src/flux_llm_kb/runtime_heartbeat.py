from __future__ import annotations

import threading
import time
from typing import Any, Callable


class WatcherHeartbeatRunner:
    def __init__(
        self,
        *,
        load_roots: Callable[[], list[Any]],
        record: Callable[[str, dict[str, Any]], None],
        interval_seconds: float = 10.0,
        clock: Callable[[], float] | None = None,
    ) -> None:
        self.load_roots = load_roots
        self.record = record
        self.interval_seconds = max(0.01, float(interval_seconds or 10.0))
        self.clock = clock or time.monotonic
        self._stop = threading.Event()
        self._lock = threading.Lock()
        self._thread: threading.Thread | None = None
        self._metadata: dict[str, Any] = {"stage": "idle", "busy": False}

    def start(self) -> None:
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, name="flux-watcher-heartbeat", daemon=True)
        self._thread.start()
        try:
            self.beat_once()
        except Exception:
            pass

    def stop(self, *, timeout: float = 2.0) -> None:
        self._stop.set()
        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=timeout)

    def update(self, **metadata: Any) -> None:
        with self._lock:
            self._metadata.update(metadata)

    def beat_once(self) -> None:
        with self._lock:
            metadata = dict(self._metadata)
        for root in self.load_roots():
            root_name = _root_name(root)
            if not root_name:
                continue
            self.record(root_name, metadata)

    def _run(self) -> None:
        while not self._stop.wait(self.interval_seconds):
            try:
                self.beat_once()
            except Exception:
                continue


def _root_name(root: Any) -> str | None:
    if isinstance(root, dict):
        value = root.get("name")
    else:
        value = getattr(root, "name", None)
    return str(value) if value else None
