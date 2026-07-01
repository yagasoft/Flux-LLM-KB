from __future__ import annotations

import subprocess
from types import SimpleNamespace

import pytest

from flux_llm_kb import host_vss


def test_snapshot_path_maps_drive_file_to_shadow_and_deletes(monkeypatch):
    deleted = []
    monkeypatch.setattr(host_vss.platform, "system", lambda: "Windows")
    monkeypatch.setattr(host_vss, "_file_size", lambda _path: 123)
    monkeypatch.setattr(host_vss, "_create_shadow_copy", lambda volume, timeout_seconds: host_vss.ShadowCopyCreation(
        shadow_id="{shadow-1}",
        device_object=r"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7",
        return_value=0,
    ))
    monkeypatch.setattr(host_vss, "_delete_shadow_copy", lambda shadow_id, timeout_seconds: deleted.append((shadow_id, timeout_seconds)))

    with host_vss.snapshot_path(r"E:\Docs\report.txt", max_file_bytes=1024, timeout_seconds=9) as snapshot:
        assert str(snapshot.path) == r"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7\Docs\report.txt"
        assert snapshot.telemetry["status"] == "completed"
        assert snapshot.telemetry["shadow_id"] == "redacted"
        assert snapshot.telemetry["volume"] == "E:\\"

    assert deleted == [("{shadow-1}", 9)]


def test_snapshot_path_rejects_oversized_file_before_creating_shadow(monkeypatch):
    created = []
    monkeypatch.setattr(host_vss.platform, "system", lambda: "Windows")
    monkeypatch.setattr(host_vss, "_file_size", lambda _path: 2048)
    monkeypatch.setattr(host_vss, "_create_shadow_copy", lambda *_args, **_kwargs: created.append(True))

    with pytest.raises(host_vss.VssSnapshotError) as exc_info:
        with host_vss.snapshot_path(r"E:\Docs\large.bin", max_file_bytes=1024, timeout_seconds=5):
            pass

    assert exc_info.value.reason == "file_too_large"
    assert created == []


def test_parse_shadow_copy_payload_uses_device_object_and_shadow_id():
    payload = (
        '{"return_value":0,'
        '"shadow_id":"{shadow-2}",'
        '"device_object":"\\\\\\\\?\\\\GLOBALROOT\\\\Device\\\\HarddiskVolumeShadowCopy9"}'
    )

    creation = host_vss._parse_shadow_copy_payload(payload)

    assert creation.return_value == 0
    assert creation.shadow_id == "{shadow-2}"
    assert creation.device_object == r"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy9"


def test_create_shadow_copy_raises_vss_error_for_access_denied(monkeypatch):
    monkeypatch.setattr(host_vss.shutil, "which", lambda _name: "powershell.exe")
    monkeypatch.setattr(
        host_vss,
        "run_no_window",
        lambda *_args, **_kwargs: SimpleNamespace(returncode=0, stdout='{"return_value":1,"shadow_id":"","device_object":""}', stderr=""),
    )

    with pytest.raises(host_vss.VssSnapshotError) as exc_info:
        host_vss._create_shadow_copy("E:\\", timeout_seconds=7)

    assert exc_info.value.reason == "access_denied"
    assert exc_info.value.telemetry["return_value"] == 1


def test_create_shadow_copy_binds_volume_as_scriptblock_parameter(monkeypatch):
    calls = []
    monkeypatch.setattr(host_vss.shutil, "which", lambda _name: "pwsh.exe")

    def capture(command, **_kwargs):
        calls.append(command)
        return SimpleNamespace(
            returncode=0,
            stdout='{"return_value":0,"shadow_id":"{shadow-1}","device_object":"\\\\\\\\?\\\\GLOBALROOT\\\\Device\\\\HarddiskVolumeShadowCopy1"}',
            stderr="",
        )

    monkeypatch.setattr(host_vss, "run_no_window", capture)

    host_vss._create_shadow_copy("E:\\", timeout_seconds=7)

    command = calls[0]
    assert command[command.index("-Command") + 1].lstrip().startswith("& {")
    assert command[-2:] == ["-Volume", "E:\\"]


def test_delete_shadow_copy_wraps_timeout(monkeypatch):
    monkeypatch.setattr(host_vss.shutil, "which", lambda _name: "powershell.exe")

    def timeout(*_args, **_kwargs):
        raise subprocess.TimeoutExpired(cmd=["powershell.exe"], timeout=3)

    monkeypatch.setattr(host_vss, "run_no_window", timeout)

    with pytest.raises(host_vss.VssSnapshotError) as exc_info:
        host_vss._delete_shadow_copy("{shadow-3}", timeout_seconds=3)

    assert exc_info.value.reason == "delete_timeout"


def test_delete_shadow_copy_binds_shadow_id_as_scriptblock_parameter(monkeypatch):
    calls = []
    monkeypatch.setattr(host_vss.shutil, "which", lambda _name: "pwsh.exe")

    def capture(command, **_kwargs):
        calls.append(command)
        return SimpleNamespace(returncode=0, stdout="", stderr="")

    monkeypatch.setattr(host_vss, "run_no_window", capture)

    host_vss._delete_shadow_copy("{shadow-2}", timeout_seconds=11)

    command = calls[0]
    script = command[command.index("-Command") + 1]
    assert script.lstrip().startswith("& {")
    assert "Trim('{}')" in script
    assert command[-2:] == ["-ShadowId", "{shadow-2}"]
