from __future__ import annotations

from pathlib import Path

from flux_llm_kb import processes


ROOT = Path(__file__).resolve().parents[1]


def test_windows_run_no_window_sets_creation_flags(monkeypatch):
    calls: list[dict] = []

    class FakeCompleted:
        returncode = 0
        stdout = "ok"
        stderr = ""

    def fake_run(*_args, **kwargs):
        calls.append(kwargs)
        return FakeCompleted()

    monkeypatch.setattr(processes.sys, "platform", "win32")
    monkeypatch.setattr(processes.subprocess, "run", fake_run)

    result = processes.run_no_window(["tesseract", "image.png", "stdout"], text=True, capture_output=True)

    assert result.stdout == "ok"
    assert calls
    assert calls[0]["creationflags"] & processes.WINDOWS_CREATE_NO_WINDOW


def test_production_modules_do_not_call_subprocess_run_directly():
    offenders: list[str] = []
    for path in (ROOT / "src").rglob("*.py"):
        relative = path.relative_to(ROOT).as_posix()
        if relative == "src/flux_llm_kb/processes.py":
            continue
        text = path.read_text(encoding="utf-8")
        if "subprocess.run(" in text:
            offenders.append(relative)

    assert offenders == []
