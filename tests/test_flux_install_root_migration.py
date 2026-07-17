import json
import shutil
import subprocess
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[1]
SCRIPT_PATH = ROOT / "scripts" / "deploy" / "migrate-flux-install-root.ps1"
POWERSHELL = shutil.which("pwsh") or shutil.which("powershell")
WINDOWS_POWERSHELL = shutil.which("powershell")
EXPECTED_COMPOSE_SERVICES = (
    "postgres",
    "rabbitmq",
    "vespa",
    "paddle-runner",
    "model-runner",
    "ollama",
    "asr",
    "api",
    "worker",
    "search-index-worker",
    "mail-worker",
    "automation-worker",
    "governance-worker",
    "runtime-control-worker",
    "gpu-eviction-worker",
    "event-scheduler",
    "callback-worker",
    "event-audit-worker",
    "event-dashboard-worker",
    "event-diagnostics-worker",
    "outbox-relay",
)


def _script() -> str:
    return SCRIPT_PATH.read_text(encoding="utf-8")


def test_byte_pattern_accepts_an_empty_byte_array() -> None:
    if POWERSHELL is None:
        pytest.skip("PowerShell is required for migration script tests")

    script_path = str(SCRIPT_PATH).replace("'", "''")
    command = f"""
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile('{script_path}', [ref]$tokens, [ref]$errors)
$definition = @($ast.FindAll({{
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-FluxBytePattern'
}}, $true))[0]
. ([scriptblock]::Create($definition.Extent.Text))
$empty = [byte[]]@()
$pattern = [byte[]]@(1)
if (Test-FluxBytePattern -Bytes $empty -Pattern $pattern) {{
    throw 'An empty byte array must not match a non-empty pattern.'
}}
"""

    result = subprocess.run(
        [POWERSHELL, "-NoProfile", "-NonInteractive", "-Command", command],
        cwd=ROOT,
        check=False,
        capture_output=True,
        text=True,
    )

    assert result.returncode == 0, result.stderr


def test_native_command_allows_successful_stderr() -> None:
    if WINDOWS_POWERSHELL is None:
        pytest.skip("Windows PowerShell is required for native stderr regression coverage")

    script_path = str(SCRIPT_PATH).replace("'", "''")
    root_path = str(ROOT).replace("'", "''")
    command = f"""
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile('{script_path}', [ref]$tokens, [ref]$errors)
$definition = @($ast.FindAll({{
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-FluxNativeCommand'
}}, $true))[0]
. ([scriptblock]::Create($definition.Extent.Text))
$childArguments = @('/d', '/c', 'echo normal-progress 1>&2')
& {{
    Invoke-FluxNativeCommand -FilePath $env:ComSpec -Arguments $childArguments -WorkingDirectory '{root_path}' -StepName 'test native stderr'
}} *>&1 | Tee-Object -Variable nativeOutput | Out-Null
"""

    result = subprocess.run(
        [WINDOWS_POWERSHELL, "-NoProfile", "-NonInteractive", "-Command", command],
        cwd=ROOT,
        check=False,
        capture_output=True,
        text=True,
    )

    assert result.returncode == 0, result.stderr


def test_updates_codex_mcp_config_to_the_destination_root(tmp_path: Path) -> None:
    if POWERSHELL is None:
        pytest.skip("PowerShell is required for migration script tests")

    config_path = tmp_path / "config.toml"
    config_path.write_text(
        '\n'.join(
            (
                'source = "D:\\\\FluxLLMKB\\\\app"',
                'command = "D:\\\\FluxLLMKB\\\\app\\\\.venv\\\\Scripts\\\\python.exe"',
                'cwd = "D:\\\\FluxLLMKB\\\\app"',
                '',
            )
        ),
        encoding="utf-8",
    )
    script_path = str(SCRIPT_PATH).replace("'", "''")
    escaped_config_path = str(config_path).replace("'", "''")
    command = f"""
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile('{script_path}', [ref]$tokens, [ref]$errors)
foreach ($name in @('Replace-FluxRootText', 'Update-FluxRootReferenceFile', 'Update-FluxCodexMcpConfiguration')) {{
    $definition = @($ast.FindAll({{
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }}, $true))[0]
    . ([scriptblock]::Create($definition.Extent.Text))
}}
Update-FluxCodexMcpConfiguration -SourceRoot 'D:\\FluxLLMKB' -DestinationRoot 'J:\\FluxLLMKB' -ConfigPath '{escaped_config_path}' | Out-Null
"""

    result = subprocess.run(
        [POWERSHELL, "-NoProfile", "-NonInteractive", "-Command", command],
        cwd=ROOT,
        check=False,
        capture_output=True,
        text=True,
    )

    assert result.returncode == 0, result.stderr
    updated = config_path.read_text(encoding="utf-8")
    assert "D:\\\\FluxLLMKB" not in updated
    assert "J:\\\\FluxLLMKB" in updated


def _run(command: list[str], *, cwd: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, cwd=cwd, check=True, capture_output=True, text=True)


def _compose_definition(services: tuple[str, ...] = EXPECTED_COMPOSE_SERVICES) -> str:
    lines = ["services:"]
    for service in services:
        lines.extend((f"  {service}:", "    image: scratch"))
    return "\n".join(lines) + "\n"


@pytest.fixture
def preflight_fixture(tmp_path: Path) -> dict[str, Path]:
    if POWERSHELL is None:
        pytest.skip("PowerShell is required for migration preflight tests")
    if shutil.which("git") is None:
        pytest.skip("Git is required for migration preflight tests")

    code_root = tmp_path / "code"
    code_root.mkdir()
    (code_root / "README.md").write_text("fixture\n", encoding="utf-8")
    _run(["git", "init", "-q"], cwd=code_root)
    _run(["git", "config", "user.email", "fixture@example.invalid"], cwd=code_root)
    _run(["git", "config", "user.name", "Fixture"], cwd=code_root)
    _run(["git", "add", "README.md"], cwd=code_root)
    _run(["git", "commit", "-qm", "fixture"], cwd=code_root)
    revision = _run(["git", "rev-parse", "--short", "HEAD"], cwd=code_root).stdout.strip()

    source_root = tmp_path / "source"
    source_app = source_root / "app"
    source_app.mkdir(parents=True)
    (source_app / "VERSION").write_text(f"{revision}\n", encoding="utf-8")
    (source_app / "docker-compose.yml").write_text(_compose_definition(), encoding="utf-8")
    (source_app / ".env").write_text("FIXTURE=true\n", encoding="utf-8")

    docker_data_root = tmp_path / "Docker" / "data" / "wsl"
    docker_vhdx_path = docker_data_root / "disk" / "docker_data.vhdx"
    docker_vhdx_path.parent.mkdir(parents=True)
    docker_vhdx_path.write_bytes(b"fixture-vhdx")
    docker_settings_path = tmp_path / "settings-store.json"
    docker_settings_path.write_text(
        json.dumps({"CustomWslDistroDir": str(docker_data_root)}),
        encoding="utf-8",
    )

    return {
        "code_root": code_root,
        "source_root": source_root,
        "destination_root": tmp_path / "destination",
        "docker_data_root": docker_data_root,
        "docker_vhdx_path": docker_vhdx_path,
        "docker_settings_path": docker_settings_path,
    }


def _run_preflight(
    fixture: dict[str, Path],
    *,
    source_root: Path | None = None,
    destination_root: Path | None = None,
    source_code_root: Path | None | object = ...,
    docker_data_root: Path | None | object = ...,
    docker_settings_path: Path | None | object = ...,
) -> subprocess.CompletedProcess[str]:
    command = [
        POWERSHELL,
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        str(SCRIPT_PATH),
        "-SourceRoot",
        str(source_root or fixture["source_root"]),
        "-DestinationRoot",
        str(destination_root or fixture["destination_root"]),
        "-Json",
    ]
    if source_code_root is not None:
        resolved_source_code_root = fixture["code_root"] if source_code_root is ... else source_code_root
        command.extend(["-SourceCodeRoot", str(resolved_source_code_root)])
    if docker_data_root is not None:
        resolved_docker_data_root = fixture["docker_data_root"] if docker_data_root is ... else docker_data_root
        command.extend(["-DockerDataRoot", str(resolved_docker_data_root)])
    if docker_settings_path is not None:
        resolved_docker_settings_path = fixture["docker_settings_path"] if docker_settings_path is ... else docker_settings_path
        command.extend(["-DockerSettingsPath", str(resolved_docker_settings_path)])

    return subprocess.run(command, capture_output=True, text=True)


def test_install_root_migration_defaults_to_guarded_json_preflight():
    script = _script()

    assert '"D:\\FluxLLMKB"' in script
    assert '"J:\\FluxLLMKB"' in script
    assert "[switch]$Apply" in script
    assert "[switch]$Json" in script
    assert "if (-not $Apply)" in script
    assert "preflight" in script.lower()
    assert "ConvertTo-Json" in script
    assert '"I:\\Docker\\data\\wsl\\DockerDesktopWSL"' in script
    assert 'settings-store.json' in script
    assert "[string]$SourceCodeRoot" in script
    assert "git -C $SourceCodeRoot status --porcelain" in script


def test_install_root_migration_requires_an_explicit_safe_partial_destination_resume():
    script = _script()
    preflight_signature = script.split("function Assert-FluxMigrationPreflight", 1)[1].split(
        "if ([string]::IsNullOrWhiteSpace($SourceCodeRoot)",
        1,
    )[0]

    assert "[switch]$ResumePartialDestination" in script
    assert "[bool]$ResumePartialDestination" in preflight_signature
    assert "Destination root must be empty" in script
    assert "Partial destination root cannot be resumed" in script
    assert "resume_partial_destination" in script
    assert "Remove-Item" not in script


def test_install_root_migration_validates_roots_revisions_and_stopped_runtime():
    script = _script()

    assert "function Assert-FluxMigrationPreflight" in script
    assert "Test-Path -LiteralPath $SourceRoot -PathType Container" in script
    assert "Test-Path -LiteralPath $destinationParent -PathType Container" in script
    assert 'Join-Path $SourceRoot "app\\VERSION"' in script
    assert "git -C $SourceCodeRoot rev-parse --short HEAD" in script
    assert "Source runtime revision does not match source code revision" in script
    assert '"FluxKB Host Agent"' in script
    assert '"FluxKB Outlook Host"' in script
    assert "Get-ScheduledTask" in script
    assert "Running" in script
    assert "Docker data root does not exist" in script
    assert "CustomWslDistroDir" in script
    assert '"config", "--services"' in script
    assert "Get-FluxComposeState" in script
    assert "compose_state" in script
    preflight_body = script.split("function Assert-FluxMigrationPreflight", 1)[1].split(
        "function New-FluxMigrationReport",
        1,
    )[0]
    assert preflight_body.index("Get-FluxComposeConfigurationServices") < preflight_body.index(
        "Get-ScheduledTask"
    )


def test_install_root_migration_recreates_runtime_without_docker_or_path_collateral_damage():
    script = _script()

    assert "robocopy" in script
    assert "Repair-FluxRootReferences" in script
    assert "Rebuild-FluxPythonRuntime" in script
    assert "--no-index" in script
    assert "--find-links" in script
    assert "Set-FluxPathEntries" in script
    assert 'GetEnvironmentVariable("Path", "User")' in script
    assert 'GetEnvironmentVariable("Path", "Machine")' in script
    assert "Docker" in script
    assert "Register-ScheduledTask" in script
    assert "Export-ScheduledTask" in script
    assert "Register-ScheduledTask -TaskName $taskName -Xml" in script
    assert "Restore-FluxMigrationSnapshot" in script
    assert "Start-ScheduledTask" in script
    assert '"down"' in script
    assert '"up", "-d", "--no-build"' in script
    assert '"--clear"' in script
    assert "compileall" in script
    assert "Assert-FluxTargetReferences" in script

    for service in (
        "postgres",
        "rabbitmq",
        "vespa",
        "paddle-runner",
        "model-runner",
        "ollama",
        "asr",
        "api",
        "worker",
        "search-index-worker",
        "mail-worker",
        "automation-worker",
        "governance-worker",
        "runtime-control-worker",
        "gpu-eviction-worker",
        "event-scheduler",
        "callback-worker",
        "event-audit-worker",
        "event-dashboard-worker",
        "event-diagnostics-worker",
        "outbox-relay",
    ):
        assert f'"{service}"' in script


def test_install_root_migration_handles_windows_path_separators_exactly():
    script = _script()
    separator = chr(92)

    assert f'TrimEnd("{separator}")' in script
    assert f'StartsWith($sourceRootFull + "{separator}"' in script
    assert f'.Replace("{separator}", "{separator}{separator}")' in script


def test_install_root_migration_never_removes_source_volumes_or_caches():
    script = _script().lower()

    for forbidden in (
        "remove-item",
        "docker volume rm",
        "docker builder prune",
        "docker buildx prune",
        "--volumes",
        "compose down -v",
    ):
        assert forbidden not in script


def test_install_root_migration_repairs_only_active_files():
    script = _script()
    repair_body = script.split("function Repair-FluxRootReferences", 1)[1].split(
        "function Rebuild-FluxPythonRuntime",
        1,
    )[0]

    assert "Get-ChildItem -LiteralPath $DestinationRoot -Recurse" not in repair_body
    assert "logs" not in repair_body.lower()
    assert "worktree" not in repair_body.lower()
    assert "app\\.env" in repair_body
    assert "run-host-agent.pyw" in repair_body
    assert "run-outlook-host.pyw" in repair_body


def test_apply_snapshots_source_state_and_defers_path_and_task_cutover():
    script = _script()
    apply_body = script.split("$preflight =", 1)[1]

    snapshot_index = apply_body.index("$snapshot = New-FluxMigrationSnapshot")
    stop_tasks_index = apply_body.index("Stop-FluxScheduledTasks")
    source_down_index = apply_body.index('"down"', stop_tasks_index)
    target_health_index = apply_body.index("Assert-FluxTargetComposeHealthy")
    path_index = apply_body.index("Set-FluxPathEntries")
    task_index = apply_body.index("Register-FluxMigrationTasks")

    assert snapshot_index < stop_tasks_index < source_down_index
    assert target_health_index < path_index < task_index
    assert "Restore-FluxMigrationSnapshot" in script
    assert "source_compose_restored" in script
    assert "$report.rollback" in script


def test_apply_rebuilds_the_docker_wheelhouse_image_from_the_copied_local_cache():
    script = _script()
    apply_body = script.split("$preflight =", 1)[1]
    wheelhouse_builder = script.split("function Build-FluxLocalWheelhouseImage", 1)[1].split(
        "function Start-FluxTargetCompose",
        1,
    )[0]

    assert 'Join-Path $DestinationRoot "package-cache\\wheelhouse"' in wheelhouse_builder
    assert 'Join-Path $SourceCodeRoot "docker\\wheelhouse.Dockerfile"' in wheelhouse_builder
    assert '"build", "--progress=plain", "--pull=false"' in wheelhouse_builder
    assert '"flux-llm-kb-wheelhouse:local"' in wheelhouse_builder
    assert "Build-FluxLocalWheelhouseImage" in apply_body
    assert apply_body.index("Rebuild-FluxPythonRuntime") < apply_body.index(
        "Build-FluxLocalWheelhouseImage"
    ) < apply_body.index("Start-FluxTargetCompose")


def test_apply_materializes_required_windows_wheels_from_the_local_pip_cache_before_rebuilding():
    script = _script()
    apply_body = script.split("$preflight =", 1)[1]
    materializer_body = script.split("function Materialize-FluxLocalPipCacheWheels", 1)[1].split(
        "function Build-FluxLocalWheelhouseImage",
        1,
    )[0]

    assert 'Join-Path $PSScriptRoot "materialize-local-pip-wheelhouse.py"' in materializer_body
    assert '$materializerArguments += @("--require", $distribution)' in materializer_body
    for distribution in (
        "pywin32",
        "watchdog",
        "cryptography",
        "pillow",
        "duckdb",
        "pyarrow",
        "numpy",
        "psutil",
        "pyyaml",
        "torch",
        "safetensors",
        "ctranslate2",
        "tokenizers",
        "onnxruntime",
        "av",
        "scikit-learn",
        "scipy",
        "regex",
        "yarl",
        "cffi",
        "tzdata",
        "psycopg-binary",
        "pydantic-core",
        "lxml",
        "colorama",
        "rpds-py",
        "protobuf",
        "win32-setctime",
        "charset-normalizer",
        "multidict",
        "propcache",
        "markupsafe",
    ):
        assert f'"{distribution}"' in materializer_body
    assert "& $sourcePython -m pip cache dir" in materializer_body
    assert apply_body.index("Materialize-FluxLocalPipCacheWheels") < apply_body.index(
        "Rebuild-FluxPythonRuntime"
    )


def test_apply_quiesces_source_processes_and_retains_robocopy_failure_diagnostics():
    script = _script()
    apply_body = script.split("$preflight =", 1)[1]
    copy_body = script.split("function Copy-FluxInstallRoot", 1)[1].split(
        "function Replace-FluxRootText",
        1,
    )[0]
    process_stop_body = script.split("function Stop-FluxSourceRootProcesses", 1)[1].split(
        "function Start-FluxComposeServices",
        1,
    )[0]
    task_stop_body = script.split("function Stop-FluxScheduledTasks", 1)[1].split(
        "function Stop-FluxSourceRootProcesses",
        1,
    )[0]

    assert "Disable-ScheduledTask -TaskName $taskName" in task_stop_body
    assert "Get-CimInstance -ClassName Win32_Process" in process_stop_body
    assert "Stop-Process -Id $process.ProcessId -Force" in process_stop_body
    assert "Flux processes still reference source root" in process_stop_body
    assert "/ZB" in copy_body
    assert "$robocopyOutput" in copy_body
    assert "robocopy diagnostics" in copy_body
    assert apply_body.index("Stop-FluxScheduledTasks") < apply_body.index(
        "Stop-FluxSourceRootProcesses"
    ) < apply_body.index("Copy-FluxInstallRoot")
    assert apply_body.count("Stop-FluxSourceRootProcesses -SourceRoot $preflight.SourceRoot") == 2


def test_compose_start_passes_explicit_service_arguments_for_target_and_rollback():
    script = _script()
    compose_start_body = script.split("function Start-FluxComposeServices", 1)[1].split(
        "function Restore-FluxMigrationSnapshot",
        1,
    )[0]
    rollback_body = script.split("function Restore-FluxMigrationSnapshot", 1)[1].split(
        "function Copy-FluxInstallRoot",
        1,
    )[0]
    target_start_body = script.split("function Start-FluxTargetCompose", 1)[1].split(
        "function Assert-FluxTargetComposeHealthy",
        1,
    )[0]

    assert "$composeArguments = @(" in compose_start_body
    assert ") + $Services" in compose_start_body
    assert "-Arguments $composeArguments" in compose_start_body
    assert ") + $Services -WorkingDirectory" not in compose_start_body
    assert "-Services $script:FluxComposeServices" in target_start_body
    assert "-Services $Snapshot.SourceRunningServices" in rollback_body


def test_default_preflight_is_read_only_for_valid_fixture(preflight_fixture):
    settings_before = preflight_fixture["docker_settings_path"].read_bytes()
    vhdx_before = preflight_fixture["docker_vhdx_path"].read_bytes()
    result = _run_preflight(preflight_fixture)

    assert result.returncode == 0, result.stderr
    report = json.loads(result.stdout)
    assert report["mode"] == "preflight"
    assert report["mutations_performed"] is False
    assert report["docker_data_root"] == str(preflight_fixture["docker_data_root"])
    assert report["docker_vhdx_path"] == str(preflight_fixture["docker_vhdx_path"])
    assert report["docker_settings_path"] == str(preflight_fixture["docker_settings_path"])
    assert set(report["source_compose_services"]) == set(EXPECTED_COMPOSE_SERVICES)
    assert report["runtime_state"] in {"stopped", "running", "unknown"}
    assert "compose_state" in report
    assert not preflight_fixture["destination_root"].exists()
    assert preflight_fixture["docker_settings_path"].read_bytes() == settings_before
    assert preflight_fixture["docker_vhdx_path"].read_bytes() == vhdx_before


def test_default_preflight_rejects_missing_source_without_destination_mutation(preflight_fixture):
    missing_source = preflight_fixture["source_root"].parent / "missing-source"
    result = _run_preflight(preflight_fixture, source_root=missing_source)

    assert result.returncode != 0
    assert "Source install root does not exist" in (result.stdout + result.stderr)
    assert not preflight_fixture["destination_root"].exists()


def test_default_preflight_rejects_nonempty_destination_without_mutation(preflight_fixture):
    destination_root = preflight_fixture["destination_root"]
    destination_root.mkdir()
    marker = destination_root / "retained.txt"
    marker.write_text("retain\n", encoding="utf-8")

    result = _run_preflight(preflight_fixture)

    assert result.returncode != 0
    assert "DestinationRoot must be new or empty" in (result.stdout + result.stderr)
    assert marker.read_text(encoding="utf-8") == "retain\n"


def test_default_preflight_rejects_revision_mismatch_without_destination_mutation(preflight_fixture):
    version_path = preflight_fixture["source_root"] / "app" / "VERSION"
    version_path.write_text("different\n", encoding="utf-8")

    result = _run_preflight(preflight_fixture)

    assert result.returncode != 0
    assert "Source runtime revision does not match source code revision" in (result.stdout + result.stderr)
    assert not preflight_fixture["destination_root"].exists()


def test_default_preflight_rejects_missing_docker_data_root_without_destination_mutation(preflight_fixture):
    missing_docker_data_root = preflight_fixture["docker_data_root"].parent / "missing-wsl"
    result = _run_preflight(preflight_fixture, docker_data_root=missing_docker_data_root)

    assert result.returncode != 0
    assert "Docker data root does not exist" in (result.stdout + result.stderr)
    assert not preflight_fixture["destination_root"].exists()


def test_default_preflight_rejects_empty_docker_data_root_without_destination_mutation(preflight_fixture):
    preflight_fixture["docker_vhdx_path"].unlink()

    result = _run_preflight(preflight_fixture)

    assert result.returncode != 0
    assert "Docker data root does not contain a supported VHDX" in (result.stdout + result.stderr)
    assert not preflight_fixture["destination_root"].exists()


def test_default_preflight_rejects_inactive_docker_settings_without_destination_mutation(preflight_fixture):
    inactive_root = preflight_fixture["docker_data_root"].parent / "inactive-wsl"
    preflight_fixture["docker_settings_path"].write_text(
        json.dumps({"CustomWslDistroDir": str(inactive_root)}),
        encoding="utf-8",
    )

    result = _run_preflight(preflight_fixture)

    assert result.returncode != 0
    assert "CustomWslDistroDir does not match Docker data root" in (result.stdout + result.stderr)
    assert not preflight_fixture["destination_root"].exists()


def test_default_preflight_rejects_missing_source_compose_service_without_destination_mutation(preflight_fixture):
    compose_path = preflight_fixture["source_root"] / "app" / "docker-compose.yml"
    compose_path.write_text(_compose_definition(EXPECTED_COMPOSE_SERVICES[:-1]), encoding="utf-8")

    result = _run_preflight(preflight_fixture)

    assert result.returncode != 0
    assert "Source Compose configuration is missing required services" in (result.stdout + result.stderr)
    assert not preflight_fixture["destination_root"].exists()


def test_default_preflight_requires_explicit_clean_source_checkout(preflight_fixture):
    result = _run_preflight(preflight_fixture, source_code_root=None)

    assert result.returncode != 0
    assert "Specify -SourceCodeRoot" in (result.stdout + result.stderr)
    assert not preflight_fixture["destination_root"].exists()


def test_default_preflight_rejects_dirty_source_checkout_without_destination_mutation(preflight_fixture):
    (preflight_fixture["code_root"] / "dirty.txt").write_text("dirty\n", encoding="utf-8")

    result = _run_preflight(preflight_fixture)

    assert result.returncode != 0
    assert "Source code checkout must be clean" in (result.stdout + result.stderr)
    assert not preflight_fixture["destination_root"].exists()
