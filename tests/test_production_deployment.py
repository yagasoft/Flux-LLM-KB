from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def _script(name: str) -> str:
    return (ROOT / "scripts" / "deploy" / name).read_text(encoding="utf-8")


def test_production_deploy_scripts_exist_and_use_d_drive_install_root():
    install = _script("install-flux.ps1")
    update = _script("update-flux.ps1")
    start = _script("start-flux.ps1")
    stop = _script("stop-flux.ps1")
    status = _script("status-flux.ps1")

    assert 'D:\\FluxLLMKB' in install
    assert "D:\\FluxLLMKB" in update
    assert "docker compose" in start
    assert "docker compose" in stop
    assert "FluxKB Host Agent" in install
    assert "FluxKB Outlook Host" in install
    assert "Register-ScheduledTask" in install
    assert "-Hidden" in install
    assert "pythonw.exe" in install
    assert "run-host-agent.pyw" in install
    assert "run-outlook-host.pyw" in install
    assert "Remove-FluxLegacyConsoleLaunchers" in install
    assert "run-host-agent.ps1" in install
    assert "run-outlook-host.ps1" in install
    assert '-Execute "pwsh.exe"' not in install
    assert "Resolve-FluxPythonExe" in install
    assert "python\\python.exe" in install
    assert "Invoke-FluxMigration" in install
    assert "-m flux_llm_kb.cli migrate" in install
    assert "Invoke-FluxCodexPluginInstall" in install
    assert "-m flux_llm_kb.cli codex install-plugin" in install
    assert '"$SourceRoot[api,corpus,mail,mcp]"' in install
    assert 'Join-Path $appRoot "plugins"' in install
    assert "E:\\LLM KB" not in install
    assert "private\\runtime" not in install
    assert "127.0.0.1:${ApiPort}:8765" in install
    assert "restart: unless-stopped" in install
    assert "Flux production status" in status
    assert "docker compose down" not in install
    assert "--volumes" not in install
    assert "docker volume rm" not in install
    for protected_path in (
        "$InstallRoot",
        "$privateRoot",
        "$dataRoot",
        "$logsRoot",
        "$runtimeRoot",
        "$backupRoot",
        'Join-Path $dataRoot "postgres"',
    ):
        assert f"Remove-Item -LiteralPath {protected_path}" not in install


def test_production_update_uses_prebuilt_images_not_repo_context_compose_build():
    update = _script("update-flux.ps1")

    assert "docker build" in update
    assert "flux-llm-kb-api:" in update
    assert '"up", "-d", "--no-build"' in update
    assert '"postgres", "api", "worker"' in update
    assert "FLUX_KB_IMAGE_TAG" in update
    assert "private\\flux.env" in update
    assert 'Join-Path $appRoot "plugins"' in update
    assert "Resolve-FluxPythonExe" in update
    assert "RecreateVenv" in update
    assert "Invoke-FluxMigration" in update
    assert "-m flux_llm_kb.cli migrate" in update
    assert "Invoke-FluxCodexPluginInstall" in update
    assert "-m flux_llm_kb.cli codex install-plugin" in update
    assert '"$SourceRoot[api,corpus,mail,mcp]"' in update
    assert "Register-FluxTask" in update
    assert "Wait-FluxTaskStopped" in update
    assert "Wait-FluxTcpClosed" in update
    assert "Port = $HostAgentPort" in update
    assert "pythonw.exe" in update
    assert "run-host-agent.pyw" in update
    assert "run-outlook-host.pyw" in update
    assert "Remove-FluxLegacyConsoleLaunchers" in update
    assert "run-host-agent.ps1" in update
    assert "run-outlook-host.ps1" in update
    assert '-Execute "pwsh.exe"' not in update
    assert "[int]$HostAgentPort = 8799" in update
    assert "[int]$PostgresPort = 5432" in update
    assert "Write-FluxHostScripts -AppRoot $appRoot -InstallRoot $InstallRoot -HostAgentPort $HostAgentPort -PostgresPort $PostgresPort" in update
    assert "build:" not in _embedded_compose_template(update)


def test_production_update_bounds_compose_up_and_recovers_created_services():
    update = _script("update-flux.ps1")

    assert "[int]$DockerComposeTimeoutSeconds" in update
    assert "Invoke-FluxDockerComposeUp" in update
    assert "WaitForExit($TimeoutSeconds * 1000)" in update
    assert "Stop-FluxProcessTree" in update
    assert "Start-FluxCreatedContainers" in update
    assert "docker inspect" in update
    assert "{{.State.Status}}" in update
    assert "docker start" in update
    assert "flux-llm-kb-api" in update
    assert "flux-llm-kb-worker" in update


def test_docs_describe_production_runtime_boundary():
    setup = (ROOT / "docs" / "setup.md").read_text(encoding="utf-8")
    architecture = (ROOT / "docs" / "architecture.md").read_text(encoding="utf-8")

    assert "D:\\FluxLLMKB" in setup
    assert "production" in setup.lower()
    assert "startup reconciliation" in architecture.lower()
    assert "periodic reconciliation" in architecture.lower()
    assert "repository remains source code only" in setup.lower()


def _embedded_compose_template(script: str) -> str:
    marker = "@'"
    start = script.find(marker)
    if start == -1:
        return ""
    end = script.find("'@", start + len(marker))
    return script[start:end]
