from __future__ import annotations

from contextlib import contextmanager
import contextvars
import subprocess
import sys
import threading
import time
from typing import Any

from . import database


WINDOWS_CREATE_NO_WINDOW = 0x08000000
_CURRENT_CAPTURE_CONTEXT: contextvars.ContextVar[dict[str, Any] | None] = contextvars.ContextVar(
    "flux_kb_capture_job_tool_invocations",
    default=None,
)


@contextmanager
def capture_job_tool_invocations(job_id: str, *, flush_interval_seconds: float = 1.0):
    token = _CURRENT_CAPTURE_CONTEXT.set(
        {
            "job_id": job_id,
            "flush_interval_seconds": max(0.05, float(flush_interval_seconds or 1.0)),
        }
    )
    try:
        yield
    finally:
        _CURRENT_CAPTURE_CONTEXT.reset(token)


def run_no_window(*popenargs: Any, **kwargs: Any) -> subprocess.CompletedProcess:
    kwargs = _prepare_subprocess_kwargs(kwargs)
    context = _CURRENT_CAPTURE_CONTEXT.get()
    if context:
        return _run_and_capture_tool_invocation(context, popenargs, kwargs)
    return subprocess.run(*popenargs, **kwargs)


def _prepare_subprocess_kwargs(kwargs: dict[str, Any]) -> dict[str, Any]:
    kwargs = dict(kwargs)
    if "stdin" not in kwargs and "input" not in kwargs:
        kwargs["stdin"] = subprocess.DEVNULL
    if kwargs.get("text") or kwargs.get("universal_newlines"):
        kwargs.setdefault("encoding", "utf-8")
        kwargs.setdefault("errors", "replace")
    if sys.platform == "win32":
        kwargs["creationflags"] = int(kwargs.get("creationflags") or 0) | WINDOWS_CREATE_NO_WINDOW
        startupinfo = kwargs.get("startupinfo")
        if startupinfo is None and hasattr(subprocess, "STARTUPINFO"):
            startupinfo = subprocess.STARTUPINFO()
        if startupinfo is not None:
            startupinfo.dwFlags |= getattr(subprocess, "STARTF_USESHOWWINDOW", 1)
            startupinfo.wShowWindow = 0
            kwargs["startupinfo"] = startupinfo
    return kwargs


def _run_and_capture_tool_invocation(
    context: dict[str, Any],
    popenargs: tuple[Any, ...],
    kwargs: dict[str, Any],
) -> subprocess.CompletedProcess:
    popen_kwargs = dict(kwargs)
    check = bool(popen_kwargs.pop("check", False))
    timeout = popen_kwargs.pop("timeout", None)
    input_value = popen_kwargs.pop("input", None)
    capture_output = bool(popen_kwargs.pop("capture_output", False))
    if capture_output:
        if "stdout" in popen_kwargs or "stderr" in popen_kwargs:
            raise ValueError("stdout and stderr arguments may not be used with capture_output.")
        popen_kwargs["stdout"] = subprocess.PIPE
        popen_kwargs["stderr"] = subprocess.PIPE
    else:
        popen_kwargs.setdefault("stdout", subprocess.PIPE)
        popen_kwargs.setdefault("stderr", subprocess.PIPE)
    if input_value is not None:
        if "stdin" in popen_kwargs:
            raise ValueError("stdin and input arguments may not both be used.")
        popen_kwargs["stdin"] = subprocess.PIPE

    args_for_record = _recorded_command(popenargs, popen_kwargs)
    invocation_id: str | None = None
    started = time.perf_counter()
    try:
        invocation = database.start_capture_job_tool_invocation(
            job_id=str(context["job_id"]),
            command=args_for_record,
            cwd=str(popen_kwargs.get("cwd")) if popen_kwargs.get("cwd") is not None else None,
        )
        invocation_id = str(invocation.get("id")) if isinstance(invocation, dict) and invocation.get("id") else None
    except Exception:
        invocation_id = None

    stdout_chunks: list[Any] = []
    stderr_chunks: list[Any] = []
    chunk_lock = threading.Lock()
    process: subprocess.Popen[Any] | None = None
    try:
        process = subprocess.Popen(*popenargs, **popen_kwargs)
    except Exception as exc:
        _complete_invocation(
            invocation_id,
            status="exception",
            return_code=None,
            stdout="",
            stderr="",
            duration_ms=_duration_ms(started),
            exception_type=exc.__class__.__name__,
            exception_message=str(exc),
        )
        raise

    stdout_thread = _start_reader(process.stdout, stdout_chunks, chunk_lock)
    stderr_thread = _start_reader(process.stderr, stderr_chunks, chunk_lock)
    if input_value is not None and process.stdin is not None:
        try:
            process.stdin.write(input_value)
            process.stdin.close()
        except BrokenPipeError:
            pass

    flush_interval = float(context.get("flush_interval_seconds") or 1.0)
    next_flush = time.monotonic() + flush_interval
    deadline = time.monotonic() + float(timeout) if timeout is not None else None
    timed_out = False
    try:
        while process.poll() is None:
            now = time.monotonic()
            if deadline is not None and now >= deadline:
                timed_out = True
                process.kill()
                break
            if now >= next_flush:
                _flush_invocation(invocation_id, stdout_chunks, stderr_chunks, chunk_lock, popen_kwargs)
                next_flush = now + flush_interval
            time.sleep(0.02)
        process.wait()
    finally:
        if stdout_thread is not None:
            stdout_thread.join(timeout=1.0)
        if stderr_thread is not None:
            stderr_thread.join(timeout=1.0)
    stdout_value = _joined_output(stdout_chunks, process.stdout is not None, popen_kwargs)
    stderr_value = _joined_output(stderr_chunks, process.stderr is not None, popen_kwargs)
    stdout_text = _output_text(stdout_value, popen_kwargs)
    stderr_text = _output_text(stderr_value, popen_kwargs)
    if timed_out:
        _complete_invocation(
            invocation_id,
            status="timeout",
            return_code=process.returncode,
            stdout=stdout_text,
            stderr=stderr_text,
            duration_ms=_duration_ms(started),
            exception_type="TimeoutExpired",
            exception_message=f"Command timed out after {timeout} seconds",
        )
        raise subprocess.TimeoutExpired(_completed_args(popenargs, popen_kwargs), timeout, output=stdout_value, stderr=stderr_value)

    status = "completed" if process.returncode == 0 else "failed"
    _complete_invocation(
        invocation_id,
        status=status,
        return_code=process.returncode,
        stdout=stdout_text,
        stderr=stderr_text,
        duration_ms=_duration_ms(started),
    )
    completed = subprocess.CompletedProcess(
        _completed_args(popenargs, popen_kwargs),
        process.returncode,
        stdout=stdout_value,
        stderr=stderr_value,
    )
    if check and process.returncode:
        raise subprocess.CalledProcessError(
            process.returncode,
            completed.args,
            output=stdout_value,
            stderr=stderr_value,
        )
    return completed


def _recorded_command(popenargs: tuple[Any, ...], kwargs: dict[str, Any]) -> list[str]:
    args = _completed_args(popenargs, kwargs)
    if isinstance(args, (list, tuple)):
        return [str(part) for part in args]
    return [str(args)]


def _completed_args(popenargs: tuple[Any, ...], kwargs: dict[str, Any]) -> Any:
    if popenargs:
        return popenargs[0]
    return kwargs.get("args")


def _start_reader(stream: Any, chunks: list[Any], lock: threading.Lock) -> threading.Thread | None:
    if stream is None:
        return None

    def read() -> None:
        while True:
            chunk = stream.read(1)
            if not chunk:
                break
            with lock:
                chunks.append(chunk)

    thread = threading.Thread(target=read, daemon=True)
    thread.start()
    return thread


def _joined_output(chunks: list[Any], captured: bool, kwargs: dict[str, Any]) -> Any:
    if not captured:
        return None
    if not chunks:
        return "" if _subprocess_text_mode(kwargs) else b""
    first = chunks[0]
    if isinstance(first, bytes):
        return b"".join(chunks)
    return "".join(str(chunk) for chunk in chunks)


def _subprocess_text_mode(kwargs: dict[str, Any]) -> bool:
    return bool(kwargs.get("text") or kwargs.get("universal_newlines") or kwargs.get("encoding") or kwargs.get("errors"))


def _output_text(value: Any, kwargs: dict[str, Any]) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        encoding = str(kwargs.get("encoding") or "utf-8")
        errors = str(kwargs.get("errors") or "replace")
        return value.decode(encoding, errors=errors)
    return str(value)


def _flush_invocation(
    invocation_id: str | None,
    stdout_chunks: list[Any],
    stderr_chunks: list[Any],
    lock: threading.Lock,
    kwargs: dict[str, Any],
) -> None:
    if not invocation_id:
        return
    with lock:
        stdout_text = _output_text(_joined_output(list(stdout_chunks), True, kwargs), kwargs)
        stderr_text = _output_text(_joined_output(list(stderr_chunks), True, kwargs), kwargs)
    try:
        database.update_capture_job_tool_invocation_output(
            invocation_id=invocation_id,
            stdout=stdout_text,
            stderr=stderr_text,
        )
    except Exception:
        return


def _complete_invocation(
    invocation_id: str | None,
    *,
    status: str,
    return_code: int | None,
    stdout: str,
    stderr: str,
    duration_ms: int,
    exception_type: str | None = None,
    exception_message: str | None = None,
) -> None:
    if not invocation_id:
        return
    try:
        database.complete_capture_job_tool_invocation(
            invocation_id=invocation_id,
            status=status,
            return_code=return_code,
            stdout=stdout,
            stderr=stderr,
            duration_ms=duration_ms,
            exception_type=exception_type,
            exception_message=exception_message,
        )
    except Exception:
        return


def _duration_ms(started: float) -> int:
    return max(0, int((time.perf_counter() - started) * 1000))
