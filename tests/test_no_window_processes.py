from __future__ import annotations

from pathlib import Path
import subprocess
import sys

import pytest

from flux_llm_kb import database
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


def test_run_no_window_detaches_child_stdin_by_default(monkeypatch):
    calls: list[dict] = []

    class FakeCompleted:
        returncode = 0
        stdout = "ok"
        stderr = ""

    def fake_run(*_args, **kwargs):
        calls.append(kwargs)
        return FakeCompleted()

    monkeypatch.setattr(processes.subprocess, "run", fake_run)

    processes.run_no_window(["git", "rev-parse", "--show-toplevel"], text=True, capture_output=True)
    processes.run_no_window(["tool"], stdin="explicit")
    processes.run_no_window(["tool"], input="payload", text=True)

    assert calls[0]["stdin"] is processes.subprocess.DEVNULL
    assert calls[1]["stdin"] == "explicit"
    assert "stdin" not in calls[2]


def test_run_no_window_text_mode_decodes_with_replacement_by_default(monkeypatch):
    calls: list[dict] = []

    class FakeCompleted:
        returncode = 0
        stdout = "ok"
        stderr = ""

    def fake_run(*_args, **kwargs):
        calls.append(kwargs)
        return FakeCompleted()

    monkeypatch.setattr(processes.subprocess, "run", fake_run)

    processes.run_no_window(["tesseract", "image.png", "stdout"], text=True, capture_output=True)
    processes.run_no_window(["tool"], text=True, capture_output=True, encoding="utf-16", errors="strict")

    assert calls[0]["encoding"] == "utf-8"
    assert calls[0]["errors"] == "replace"
    assert calls[1]["encoding"] == "utf-16"
    assert calls[1]["errors"] == "strict"


def test_run_no_window_captures_live_output_for_active_job(monkeypatch):
    started: list[dict] = []
    updates: list[tuple[str, str]] = []
    completions: list[dict] = []

    monkeypatch.setattr(
        database,
        "start_capture_job_tool_invocation",
        lambda **kwargs: started.append(kwargs) or {"id": "inv-1", "started_at": "2026-06-30T19:45:00+00:00"},
    )
    monkeypatch.setattr(
        database,
        "update_capture_job_tool_invocation_output",
        lambda **kwargs: updates.append((kwargs["stdout"], kwargs["stderr"])),
    )
    monkeypatch.setattr(database, "complete_capture_job_tool_invocation", lambda **kwargs: completions.append(kwargs))

    script = (
        "import sys, time\n"
        "print('stdout-one', flush=True)\n"
        "print('stderr-one', file=sys.stderr, flush=True)\n"
        "time.sleep(0.4)\n"
        "print('stdout-two', flush=True)\n"
    )

    with processes.capture_job_tool_invocations("job-1", flush_interval_seconds=0.05):
        result = processes.run_no_window([sys.executable, "-c", script], text=True, capture_output=True, timeout=5)

    assert result.returncode == 0
    assert result.stdout == "stdout-one\nstdout-two\n"
    assert result.stderr == "stderr-one\n"
    assert started[0]["job_id"] == "job-1"
    assert started[0]["command"][0] == sys.executable
    assert any(stdout == "stdout-one\n" and stderr == "stderr-one\n" for stdout, stderr in updates)
    assert completions[-1]["invocation_id"] == "inv-1"
    assert completions[-1]["status"] == "completed"
    assert completions[-1]["return_code"] == 0
    assert completions[-1]["stdout"] == "stdout-one\nstdout-two\n"


def test_run_no_window_records_failed_checked_process_for_active_job(monkeypatch):
    completions: list[dict] = []
    monkeypatch.setattr(database, "start_capture_job_tool_invocation", lambda **_kwargs: {"id": "inv-1"})
    monkeypatch.setattr(database, "update_capture_job_tool_invocation_output", lambda **_kwargs: None)
    monkeypatch.setattr(database, "complete_capture_job_tool_invocation", lambda **kwargs: completions.append(kwargs))

    with processes.capture_job_tool_invocations("job-1", flush_interval_seconds=0.05):
        try:
            processes.run_no_window(
                [sys.executable, "-c", "import sys; print('bad'); sys.exit(7)"],
                text=True,
                capture_output=True,
                check=True,
            )
        except subprocess.CalledProcessError as exc:
            error = exc
        else:  # pragma: no cover - assertion path
            raise AssertionError("checked failed process should raise")

    assert error.returncode == 7
    assert error.stdout == "bad\n"
    assert completions[-1]["status"] == "failed"
    assert completions[-1]["return_code"] == 7
    assert completions[-1]["stdout"] == "bad\n"


def test_run_no_window_records_timeout_for_active_job(monkeypatch):
    completions: list[dict] = []
    monkeypatch.setattr(database, "start_capture_job_tool_invocation", lambda **_kwargs: {"id": "inv-1"})
    monkeypatch.setattr(database, "update_capture_job_tool_invocation_output", lambda **_kwargs: None)
    monkeypatch.setattr(database, "complete_capture_job_tool_invocation", lambda **kwargs: completions.append(kwargs))

    with processes.capture_job_tool_invocations("job-1", flush_interval_seconds=0.05):
        try:
            processes.run_no_window(
                [sys.executable, "-c", "import time; print('before-timeout', flush=True); time.sleep(5)"],
                text=True,
                capture_output=True,
                timeout=0.2,
            )
        except subprocess.TimeoutExpired as exc:
            error = exc
        else:  # pragma: no cover - assertion path
            raise AssertionError("timed out process should raise")

    assert error.stdout == "before-timeout\n"
    assert completions[-1]["status"] == "timeout"
    assert completions[-1]["exception_type"] == "TimeoutExpired"
    assert completions[-1]["stdout"] == "before-timeout\n"


def test_run_no_window_preserves_binary_empty_output_for_active_job(monkeypatch):
    completions: list[dict] = []
    monkeypatch.setattr(database, "start_capture_job_tool_invocation", lambda **_kwargs: {"id": "inv-1"})
    monkeypatch.setattr(database, "update_capture_job_tool_invocation_output", lambda **_kwargs: None)
    monkeypatch.setattr(database, "complete_capture_job_tool_invocation", lambda **kwargs: completions.append(kwargs))

    with processes.capture_job_tool_invocations("job-1", flush_interval_seconds=0.05):
        result = processes.run_no_window([sys.executable, "-c", ""], capture_output=True)

    assert result.stdout == b""
    assert result.stderr == b""
    assert completions[-1]["status"] == "completed"
    assert completions[-1]["stdout"] == ""
    assert completions[-1]["stderr"] == ""


def test_run_no_window_records_start_exception_for_active_job(monkeypatch):
    completions: list[dict] = []
    monkeypatch.setattr(database, "start_capture_job_tool_invocation", lambda **_kwargs: {"id": "inv-1"})
    monkeypatch.setattr(database, "update_capture_job_tool_invocation_output", lambda **_kwargs: None)
    monkeypatch.setattr(database, "complete_capture_job_tool_invocation", lambda **kwargs: completions.append(kwargs))

    with processes.capture_job_tool_invocations("job-1", flush_interval_seconds=0.05):
        with pytest.raises(FileNotFoundError):
            processes.run_no_window(
                ["definitely-not-a-real-flux-llm-kb-tool"],
                text=True,
                capture_output=True,
            )

    assert completions[-1]["status"] == "exception"
    assert completions[-1]["exception_type"] == "FileNotFoundError"
    assert completions[-1]["stdout"] == ""
    assert completions[-1]["stderr"] == ""


def test_run_no_window_active_context_rejects_input_with_stdin(monkeypatch):
    starts: list[dict] = []
    monkeypatch.setattr(database, "start_capture_job_tool_invocation", lambda **kwargs: starts.append(kwargs) or {"id": "inv-1"})

    with processes.capture_job_tool_invocations("job-1", flush_interval_seconds=0.05):
        with pytest.raises(ValueError, match="stdin and input"):
            processes.run_no_window(
                [sys.executable, "-c", "print('unused')"],
                input="payload",
                stdin=subprocess.DEVNULL,
                text=True,
                capture_output=True,
            )

    assert starts == []


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
