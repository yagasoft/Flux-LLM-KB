from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass
import ctypes
import json
from pathlib import Path, PureWindowsPath
import platform
import shutil
import subprocess
from typing import Any, Iterator

from .processes import run_no_window


_VSS_RETURN_REASONS = {
    1: "access_denied",
    2: "invalid_argument",
    3: "volume_not_found",
    4: "volume_not_supported",
    5: "unsupported_context",
    6: "insufficient_storage",
    7: "volume_in_use",
    8: "max_shadow_copies",
    9: "operation_in_progress",
    10: "provider_veto",
    11: "provider_not_registered",
    12: "provider_failure",
    13: "unknown_error",
}


@dataclass(frozen=True)
class ShadowCopyCreation:
    shadow_id: str
    device_object: str
    return_value: int = 0


@dataclass(frozen=True)
class VssSnapshot:
    path: Path
    telemetry: dict[str, Any]


class VssSnapshotError(RuntimeError):
    def __init__(self, message: str, *, reason: str, telemetry: dict[str, Any] | None = None) -> None:
        super().__init__(message)
        self.reason = reason
        self.telemetry = dict(telemetry or {})
        self.telemetry.setdefault("status", "failed")
        self.telemetry.setdefault("reason", reason)


@contextmanager
def snapshot_path(
    source_path: str | Path,
    *,
    max_file_bytes: int,
    timeout_seconds: int,
) -> Iterator[VssSnapshot]:
    volume, relative_path = _eligible_windows_volume_path(source_path)
    size_bytes = _file_size(source_path)
    max_bytes = max(1, int(max_file_bytes))
    if size_bytes > max_bytes:
        raise VssSnapshotError(
            "VSS fallback skipped because the file is larger than the configured limit.",
            reason="file_too_large",
            telemetry={"status": "skipped", "reason": "file_too_large", "size_bytes": size_bytes, "max_file_bytes": max_bytes},
        )

    creation = _create_shadow_copy(volume, timeout_seconds=timeout_seconds)
    try:
        shadow_path = _shadow_file_path(creation.device_object, relative_path)
        yield VssSnapshot(
            path=Path(shadow_path),
            telemetry={
                "status": "completed",
                "reason": "snapshot_created",
                "shadow_id": "redacted",
                "volume": volume,
                "return_value": creation.return_value,
                "size_bytes": size_bytes,
            },
        )
    finally:
        _delete_shadow_copy(creation.shadow_id, timeout_seconds=timeout_seconds)


def capability_status(*, enabled: bool, max_file_bytes: int, timeout_seconds: int) -> dict[str, Any]:
    if not enabled:
        return {
            "enabled": False,
            "status": "disabled",
            "max_file_bytes": max_file_bytes,
            "timeout_seconds": timeout_seconds,
            "message": "VSS fallback is disabled; locked files use retry/cooldown states.",
        }
    if platform.system() != "Windows":
        return {
            "enabled": True,
            "status": "unavailable",
            "max_file_bytes": max_file_bytes,
            "timeout_seconds": timeout_seconds,
            "reason": "not_windows",
            "message": "VSS fallback is only available on Windows host-agent local volumes.",
        }
    if _powershell_executable() is None:
        return {
            "enabled": True,
            "status": "unavailable",
            "max_file_bytes": max_file_bytes,
            "timeout_seconds": timeout_seconds,
            "reason": "powershell_unavailable",
            "message": "VSS fallback needs PowerShell to create Win32_ShadowCopy snapshots.",
        }
    return {
        "enabled": True,
        "status": "ready",
        "max_file_bytes": max_file_bytes,
        "timeout_seconds": timeout_seconds,
        "message": "VSS fallback is available for locked Windows host-agent local files.",
    }


def _eligible_windows_volume_path(source_path: str | Path) -> tuple[str, PureWindowsPath]:
    if platform.system() != "Windows":
        raise VssSnapshotError(
            "VSS fallback is only available on Windows host-agent local volumes.",
            reason="not_windows",
            telemetry={"status": "skipped", "reason": "not_windows"},
        )
    windows_path = PureWindowsPath(str(source_path))
    if not windows_path.is_absolute() or not windows_path.drive or windows_path.drive.startswith("\\\\"):
        raise VssSnapshotError(
            "VSS fallback requires an absolute local Windows drive path.",
            reason="not_local_volume",
            telemetry={"status": "skipped", "reason": "not_local_volume"},
        )
    volume = f"{windows_path.drive}\\"
    if not _is_local_fixed_volume(volume):
        raise VssSnapshotError(
            "VSS fallback requires a local fixed Windows volume.",
            reason="not_local_volume",
            telemetry={"status": "skipped", "reason": "not_local_volume", "volume": volume},
        )
    try:
        relative_path = windows_path.relative_to(windows_path.anchor)
    except ValueError as exc:
        raise VssSnapshotError(
            "VSS fallback could not map the source path to its volume.",
            reason="path_mapping_failed",
            telemetry={"status": "failed", "reason": "path_mapping_failed", "volume": volume},
        ) from exc
    return volume, relative_path


def _shadow_file_path(device_object: str, relative_path: PureWindowsPath) -> str:
    clean_device = str(device_object or "").rstrip("\\/")
    if not clean_device:
        raise VssSnapshotError(
            "VSS shadow copy creation did not return a device object.",
            reason="missing_shadow_metadata",
            telemetry={"status": "failed", "reason": "missing_shadow_metadata"},
        )
    clean_relative = str(relative_path).lstrip("\\/")
    return f"{clean_device}\\{clean_relative}"


def _is_local_fixed_volume(volume: str) -> bool:
    windll = getattr(ctypes, "windll", None)
    if windll is None:
        return True
    try:
        return int(windll.kernel32.GetDriveTypeW(volume)) == 3
    except Exception:
        return True


def _file_size(path: str | Path) -> int:
    try:
        return int(Path(path).stat().st_size)
    except OSError as exc:
        raise VssSnapshotError(
            "VSS fallback could not stat the source file.",
            reason="stat_failed",
            telemetry={"status": "failed", "reason": "stat_failed", "error_type": exc.__class__.__name__},
        ) from exc


def _create_shadow_copy(volume: str, *, timeout_seconds: int) -> ShadowCopyCreation:
    powershell = _powershell_executable()
    if powershell is None:
        raise VssSnapshotError(
            "VSS fallback needs PowerShell to create a shadow copy.",
            reason="powershell_unavailable",
            telemetry={"status": "failed", "reason": "powershell_unavailable", "volume": volume},
        )
    script = r"""
param([string]$Volume)
$ErrorActionPreference = 'Stop'
$result = Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create -Arguments @{ Volume = $Volume; Context = 'ClientAccessible' }
$shadow = $null
if ($result.ShadowID) {
    $shadow = Get-CimInstance -ClassName Win32_ShadowCopy | Where-Object { $_.ID -eq $result.ShadowID } | Select-Object -First 1
}
[pscustomobject]@{
    return_value = [int]$result.ReturnValue
    shadow_id = [string]$result.ShadowID
    device_object = [string]$(if ($shadow) { $shadow.DeviceObject } else { "" })
} | ConvertTo-Json -Compress
"""
    try:
        completed = run_no_window(
            [
                powershell,
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                _scriptblock_command(script),
                "-Volume",
                volume,
            ],
            text=True,
            capture_output=True,
            timeout=max(1, int(timeout_seconds)),
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise VssSnapshotError(
            "VSS shadow copy creation timed out.",
            reason="create_timeout",
            telemetry={"status": "failed", "reason": "create_timeout", "volume": volume},
        ) from exc
    if completed.returncode != 0:
        raise VssSnapshotError(
            "VSS shadow copy creation failed.",
            reason="create_failed",
            telemetry={"status": "failed", "reason": "create_failed", "volume": volume, "return_code": int(completed.returncode)},
        )
    creation = _parse_shadow_copy_payload(completed.stdout)
    if creation.return_value != 0:
        reason = _VSS_RETURN_REASONS.get(creation.return_value, "unknown_error")
        raise VssSnapshotError(
            "VSS shadow copy creation was rejected by Windows.",
            reason=reason,
            telemetry={"status": "failed", "reason": reason, "volume": volume, "return_value": creation.return_value},
        )
    if not creation.shadow_id or not creation.device_object:
        raise VssSnapshotError(
            "VSS shadow copy creation returned incomplete metadata.",
            reason="missing_shadow_metadata",
            telemetry={"status": "failed", "reason": "missing_shadow_metadata", "volume": volume, "return_value": creation.return_value},
        )
    return creation


def _delete_shadow_copy(shadow_id: str, *, timeout_seconds: int) -> None:
    powershell = _powershell_executable()
    if powershell is None:
        raise VssSnapshotError(
            "VSS fallback needs PowerShell to delete the shadow copy.",
            reason="powershell_unavailable",
            telemetry={"status": "failed", "reason": "powershell_unavailable"},
        )
    script = r"""
param([string]$ShadowId)
$ErrorActionPreference = 'Stop'
$targetShadowId = ([string]$ShadowId).Trim('{}')
$shadow = Get-CimInstance -ClassName Win32_ShadowCopy | Where-Object { ([string]$_.ID).Trim('{}') -eq $targetShadowId } | Select-Object -First 1
if ($shadow) {
    $shadow | Remove-CimInstance
}
"""
    try:
        completed = run_no_window(
            [
                powershell,
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                _scriptblock_command(script),
                "-ShadowId",
                shadow_id,
            ],
            text=True,
            capture_output=True,
            timeout=max(1, int(timeout_seconds)),
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise VssSnapshotError(
            "VSS shadow copy deletion timed out.",
            reason="delete_timeout",
            telemetry={"status": "failed", "reason": "delete_timeout"},
        ) from exc
    if completed.returncode != 0:
        raise VssSnapshotError(
            "VSS shadow copy deletion failed.",
            reason="delete_failed",
            telemetry={"status": "failed", "reason": "delete_failed", "return_code": int(completed.returncode)},
        )


def _parse_shadow_copy_payload(payload: str) -> ShadowCopyCreation:
    try:
        parsed = json.loads(_last_json_line(payload))
    except Exception as exc:
        raise VssSnapshotError(
            "VSS shadow copy creation returned invalid output.",
            reason="invalid_create_output",
            telemetry={"status": "failed", "reason": "invalid_create_output"},
        ) from exc
    if isinstance(parsed, list):
        parsed = parsed[0] if parsed else {}
    if not isinstance(parsed, dict):
        raise VssSnapshotError(
            "VSS shadow copy creation returned unexpected output.",
            reason="invalid_create_output",
            telemetry={"status": "failed", "reason": "invalid_create_output"},
        )
    return ShadowCopyCreation(
        shadow_id=str(_payload_value(parsed, "shadow_id", "ShadowID", "ShadowId") or ""),
        device_object=str(_payload_value(parsed, "device_object", "DeviceObject") or ""),
        return_value=int(_payload_value(parsed, "return_value", "ReturnValue") or 0),
    )


def _last_json_line(payload: str) -> str:
    lines = [line.strip() for line in str(payload or "").splitlines() if line.strip()]
    if not lines:
        return ""
    return lines[-1]


def _payload_value(payload: dict[str, Any], *keys: str) -> Any:
    for key in keys:
        if key in payload:
            return payload[key]
    lower_payload = {str(key).lower(): value for key, value in payload.items()}
    for key in keys:
        lowered = key.lower()
        if lowered in lower_payload:
            return lower_payload[lowered]
    return None


def _scriptblock_command(script: str) -> str:
    return f"& {{\n{script.strip()}\n}}"


def _powershell_executable() -> str | None:
    return shutil.which("pwsh") or shutil.which("powershell")
