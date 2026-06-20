from __future__ import annotations

from dataclasses import dataclass
from collections import deque
import importlib.util
from pathlib import Path
import time
from typing import Callable, Iterable


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


class ReloadableCorpusWatcher:
    def __init__(
        self,
        load_roots: Callable[[], list[WatchRoot]],
        *,
        on_change: Callable[[WatchEvent], None] | None = None,
        interval_seconds: float = 2.0,
        debounce_seconds: float = 0.75,
        max_queue_size: int = 1000,
    ) -> None:
        self.load_roots = load_roots
        self.on_change = on_change
        self.interval_seconds = interval_seconds
        self.debounce_seconds = debounce_seconds
        self._queue: deque[WatchEvent] = deque()
        self._max_queue_size = max(1, max_queue_size)
        self._snapshots: dict[str, dict[str, tuple[int, int]]] = {}
        self._last_event_at: dict[tuple[str, str], float] = {}

    def poll_once(self, *, seed: bool = False) -> list[WatchEvent]:
        active_roots = {root.name: root for root in self.load_roots() if root.watch_enabled}
        for root_name in set(self._snapshots) - set(active_roots):
            self._snapshots.pop(root_name, None)
        emitted: list[WatchEvent] = []
        for root in active_roots.values():
            previous = self._snapshots.get(root.name)
            current = _snapshot(root)
            self._snapshots[root.name] = current
            if seed or previous is None:
                continue
            emitted.extend(self._changed_events(root, previous, current))
        return emitted

    def drain_events(self) -> list[WatchEvent]:
        events = list(self._queue)
        self._queue.clear()
        return events

    def run_forever(self) -> None:
        self.poll_once(seed=True)
        while True:
            self.poll_once()
            time.sleep(self.interval_seconds)

    def _changed_events(
        self,
        root: WatchRoot,
        previous: dict[str, tuple[int, int]],
        current: dict[str, tuple[int, int]],
    ) -> list[WatchEvent]:
        events: list[WatchEvent] = []
        for relative_path, fingerprint in current.items():
            if previous.get(relative_path) != fingerprint:
                event = _event(root, relative_path, "changed")
                if self._enqueue(event):
                    events.append(event)
        for relative_path in previous.keys() - current.keys():
            event = _event(root, relative_path, "deleted")
            if self._enqueue(event):
                events.append(event)
        return events

    def _enqueue(self, event: WatchEvent) -> bool:
        key = (event.root_name, event.relative_path)
        now = time.monotonic()
        if now - self._last_event_at.get(key, -1_000_000.0) < self.debounce_seconds:
            return False
        self._last_event_at[key] = now
        if len(self._queue) >= self._max_queue_size:
            return False
        self._queue.append(event)
        if self.on_change:
            self.on_change(event)
        return True


class WatchdogCorpusWatcher(ReloadableCorpusWatcher):
    def __init__(self, *args, **kwargs) -> None:
        if importlib.util.find_spec("watchdog") is None:
            raise RuntimeError("watchdog is not installed")
        super().__init__(*args, **kwargs)
        self._observer = None
        self._watches: dict[str, object] = {}

    def poll_once(self, *, seed: bool = False) -> list[WatchEvent]:
        self._ensure_observer()
        self._sync_watches()
        return []

    def run_forever(self) -> None:
        self.poll_once(seed=True)
        try:
            while True:
                self.poll_once()
                time.sleep(self.interval_seconds)
        finally:
            if self._observer is not None:
                self._observer.stop()
                self._observer.join(timeout=5)

    def _ensure_observer(self) -> None:
        if self._observer is not None:
            return
        from watchdog.observers import Observer

        self._observer = Observer()
        self._observer.start()

    def _sync_watches(self) -> None:
        from watchdog.events import FileSystemEventHandler

        active_roots = {root.name: root for root in self.load_roots() if root.watch_enabled}
        for root_name in set(self._watches) - set(active_roots):
            self._observer.unschedule(self._watches.pop(root_name))
        for root_name, root in active_roots.items():
            if root_name in self._watches:
                continue
            self._watches[root_name] = self._observer.schedule(
                self._handler_for(root, FileSystemEventHandler),
                str(root.root_path),
                recursive=root.recursive,
            )

    def _handler_for(self, root: WatchRoot, base_handler):
        watcher = self

        class Handler(base_handler):
            def on_any_event(self, event):
                if event.is_directory:
                    return
                event_path = Path(getattr(event, "dest_path", None) or event.src_path)
                try:
                    relative_path = event_path.resolve().relative_to(root.root_path.resolve()).as_posix()
                except ValueError:
                    return
                action = "deleted" if event.event_type == "deleted" else "changed"
                watcher._enqueue(_event(root, relative_path, action))

        return Handler()


def create_corpus_watcher(
    load_roots: Callable[[], list[WatchRoot]],
    *,
    on_change: Callable[[WatchEvent], None] | None = None,
    interval_seconds: float = 2.0,
    debounce_seconds: float = 0.75,
    max_queue_size: int = 1000,
):
    watcher_cls = WatchdogCorpusWatcher if importlib.util.find_spec("watchdog") is not None else ReloadableCorpusWatcher
    return watcher_cls(
        load_roots,
        on_change=on_change,
        interval_seconds=interval_seconds,
        debounce_seconds=debounce_seconds,
        max_queue_size=max_queue_size,
    )


def summarize_watcher_staleness(
    states: Iterable[dict],
    *,
    stale_after_seconds: int = 120,
) -> dict:
    summarized: list[dict] = []
    stale_count = 0
    for state in states:
        item = dict(state)
        if item.get("status") == "running" and (item.get("heartbeat_age_seconds") or 0) > stale_after_seconds:
            item["status"] = "stale"
            stale_count += 1
        summarized.append(item)
    return {"stale_count": stale_count, "states": summarized}


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
