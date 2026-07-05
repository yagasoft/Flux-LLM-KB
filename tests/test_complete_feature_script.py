from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_complete_feature_script_is_fail_fast_and_structured():
    script_path = ROOT / "scripts" / "dev" / "complete-feature.ps1"
    assert script_path.exists()

    script = script_path.read_text(encoding="utf-8")

    assert ".agents\\run-logs" in script
    assert "ConvertTo-Json" in script
    assert "Invoke-FeatureStep" in script
    assert "throw" in script
    assert "git reset --hard" not in script
    assert "push --force" not in script
    assert "git worktree remove" in script
    assert "git branch -D" in script
    assert "scripts\\deploy\\update-flux.ps1" in script
    assert "http://127.0.0.1:8765/dashboard" in script
    assert "http://127.0.0.1:8765/api/dashboard/health" in script
    assert "codex mcp-readiness --json" in script
    assert 'Invoke-FeatureStep -Name "probe-mcp-readiness"' in script
    assert "Dashboard health reported blocked required checks" in script
    assert "database.checks" in script


def test_complete_feature_script_orders_cleanup_after_deploy_probe():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    deploy_index = script.index("scripts\\deploy\\update-flux.ps1")
    probe_index = script.index("http://127.0.0.1:8765/api/dashboard/health")
    mcp_probe_index = script.index('Invoke-FeatureStep -Name "probe-mcp-readiness"')
    reclaim_index = script.index('Invoke-FeatureStep -Name "post-deploy-outlook-spool-reclaim"')
    repair_index = script.index('Invoke-FeatureStep -Name "repair-python-editable-install"')
    cleanup_index = script.index('Invoke-FeatureStep -Name "cleanup-worktree"')

    assert deploy_index < probe_index < mcp_probe_index < reclaim_index < repair_index < cleanup_index


def test_complete_feature_script_retries_transient_mcp_transport_closure():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    assert "$McpReadinessProbeAttempts = 3" in script
    assert "[System.IO.Path]::GetTempFileName()" in script
    assert "1> $stdoutPath 2> $stderrPath" in script
    assert "Get-Content -Raw -LiteralPath $stdoutPath" in script
    assert "Get-Content -Raw -LiteralPath $stderrPath" in script
    assert "Remove-Item -LiteralPath $stdoutPath, $stderrPath" in script
    assert "2>&1" not in script
    assert "transport_closed" in script
    assert "temporary_unavailable" in script
    assert "Start-Sleep -Seconds 5" in script
    assert "MCP readiness failed with transient status" in script


def test_status_script_reports_blocked_required_dashboard_health():
    script = (ROOT / "scripts" / "deploy" / "status-flux.ps1").read_text(encoding="utf-8")

    assert "Dashboard health reported blocked required checks" in script
    assert "database.checks" in script
    assert "required -ne $false" in script
    assert "codex mcp-readiness --json" in script
    assert "Flux MCP readiness failed" in script


def test_complete_feature_script_has_optional_post_deploy_outlook_spool_reclaim():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")
    reclaim_block_start = script.index("$reclaimCommand =")
    reclaim_block_end = script.index('Invoke-FeatureStep -Name "post-deploy-outlook-spool-reclaim"', reclaim_block_start)
    reclaim_block = script[reclaim_block_start:reclaim_block_end]

    assert "[string]$PostDeployReclaimOutlookProfile" in script
    assert "docker exec flux-llm-kb-api python -m flux_llm_kb.cli mail spool-dedupe --profile" in script
    assert "--apply --purge --json" in script
    assert 'Invoke-FeatureStep -Name "post-deploy-outlook-spool-reclaim"' in script
    assert '$env:PYTHONPATH = (Join-Path (Get-Location) "src")' not in reclaim_block


def test_complete_feature_script_runs_npm_install_before_tests_by_default():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    online_step = 'Invoke-FeatureStep -Name "dashboard-install" -Cwd $FeatureWorktree -Command $DashboardNpmInstallCommand'
    test_step = (
        'Invoke-FeatureStep -Name "dashboard-test" '
        "-Cwd $FeatureWorktree -Command 'Push-Location dashboard; try { node node_modules/vitest/dist/cli.js run } finally { Pop-Location }'"
    )
    build_step = (
        'Invoke-FeatureStep -Name "dashboard-build" '
        "-Cwd $FeatureWorktree -Command 'Push-Location dashboard; try { node node_modules/vite/bin/vite.js build } finally { Pop-Location }'"
    )

    assert "[switch]$AllowNpmInstall" not in script
    assert "[switch]$RefreshNpmDependencies" not in script
    assert "[string]$NpmCachePath" in script
    assert "$DashboardCacheCheckCommand" not in script
    assert online_step in script
    assert script.index(online_step) < script.index(test_step) < script.index(build_step)
    assert test_step in script
    assert build_step in script
    assert 'npm --prefix dashboard ci --include=dev --cache "$NpmCachePath" --prefer-offline' in script


def test_complete_feature_script_removes_pip_online_mode_and_deploys_pip_offline():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    assert "[switch]$AllowPipDownloads" not in script
    assert "[switch]$RefreshPipDependencies" not in script
    assert "AllowPipDownloads" not in script
    assert "RefreshPipDependencies" not in script
    assert "FLUX_KB_ALLOW_PIP_DOWNLOADS" not in script
    assert "PipOffline:$false" not in script
    assert "--cache \"$NpmCachePath\"" in script
    assert "$pipOffline" not in script
    assert "$deployPipOfflineValue" not in script
    assert "$deployCommand = \".\\scripts\\deploy\\update-flux.ps1 -GpuMode on -SkipDashboardBuild -PipOffline:`$true -DockerBaseMode $DockerBaseMode\"" in script
    assert "Closeout runs pip offline only." in script


def test_complete_feature_script_runs_pytest_with_xdist_by_default_and_serial_escape_hatch():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    assert '[string]$PytestWorkers = "auto"' in script
    assert "[int]$PytestStepTimeoutSeconds = 1200" in script
    assert "$pytestCommand = " in script
    assert 'python -m pytest -n $PytestWorkers --dist loadfile' in script
    assert "if ([string]::IsNullOrWhiteSpace($PytestWorkers) -or $PytestWorkers -eq \"0\")" in script
    assert "python -m pytest'" in script
    assert 'Invoke-FeatureStep -Name "pytest" -Cwd $FeatureWorktree -Command $pytestCommand -TimeoutSeconds $PytestStepTimeoutSeconds' in script


def test_complete_feature_script_records_step_timings_in_json_summary():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    assert "started_at" in script
    assert "finished_at" in script
    assert "duration_seconds" in script
    assert "Complete-FeatureStepRecord" in script
    assert "[DateTime]::UtcNow.ToString(\"o\")" in script
    assert "[Math]::Round(" in script


def test_dev_dependencies_include_pytest_xdist_for_parallel_closeout():
    pyproject = (ROOT / "pyproject.toml").read_text(encoding="utf-8")

    assert '"pytest-xdist>=3.6"' in pyproject


def test_complete_feature_script_repairs_shared_editable_install_before_worktree_cleanup():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    assert 'Invoke-FeatureStep -Name "repair-python-editable-install"' in script
    assert "python -m pip show flux-llm-kb" in script
    assert "Editable project location:" in script
    assert "Test-Path -LiteralPath $editableLocation" in script
    assert "Test-UnderPath -Path $editableLocation -Root $FeatureWorktree" in script
    assert "Python editable install repair requires the persistent pip wheelhouse" in script
    assert "$env:FLUX_KB_REPAIR_PIP_WHEELHOUSE_PATH" in script
    assert "--no-index" in script
    assert "--find-links" in script
    assert 'python -m pip install --no-index --find-links "$PipWheelhousePath" --cache-dir "$PipCachePath" -e "$MainRoot[dev]"' in script


def test_complete_feature_script_is_safe_to_rerun_after_empty_squash_merge():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    assert 'Invoke-FeatureStep -Name "main-commit"' in script
    assert "No staged changes to commit." in script
    assert "git commit -m '$CommitMessage'" in script


def test_complete_feature_script_bounds_step_processes_without_powershell_stream_redirection():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    assert "[int]$StepTimeoutSeconds" in script
    assert "[int]$DeployStepTimeoutSeconds = 1800" in script
    assert "[System.Diagnostics.ProcessStartInfo]" in script
    assert "UseShellExecute = $false" in script
    assert "CreateNoWindow = $true" in script
    assert "RedirectStandardOutput = $true" in script
    assert "RedirectStandardError = $true" in script
    assert "ReadToEndAsync()" in script
    assert "$effectiveTimeoutSeconds = if ($TimeoutSeconds -gt 0)" in script
    assert "WaitForExit($effectiveTimeoutSeconds * 1000)" in script
    assert "ExitCode" in script
    assert "Stop-FeatureProcessTree" in script
    assert "ProcessStreamReader_CliXmlError" in script
    assert "Start-Process" not in script
    assert "*> $logPath" not in script


def test_complete_feature_script_propagates_nested_native_exit_codes():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    assert "function New-FeatureStepScript" in script
    assert "$stepScript = New-FeatureStepScript -Command $Command" in script
    assert "$global:LASTEXITCODE -is [int]" in script
    assert "$global:LASTEXITCODE -ne 0" in script
    assert "exit `$global:LASTEXITCODE" in script
    assert "exit 1" in script


def test_complete_feature_script_uses_longer_timeout_for_production_deploy():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    assert '[ValidateSet("auto", "local", "python")]' in script
    assert '[string]$DockerBaseMode = "auto"' in script
    assert "$deployCommand = \".\\scripts\\deploy\\update-flux.ps1 -GpuMode on -SkipDashboardBuild -PipOffline:`$true -DockerBaseMode $DockerBaseMode\"" in script
    assert "If deploy pip dependencies are missing from cache, rerun this closeout with -AllowPipDownloads only." not in script
    assert "Invoke-FeatureStep -Name \"deploy-production\" -Cwd $MainRoot -Command $deployCommand -TimeoutSeconds $DeployStepTimeoutSeconds" in script


def test_complete_feature_script_verifies_origin_main_with_scalar_hashes():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    assert "$headSha = (git rev-parse HEAD).Trim()" in script
    assert "$originSha = (git rev-parse origin/main).Trim()" in script
    assert "$headSha -ne $originSha" in script
    assert "HEAD $headSha differs from origin/main $originSha" in script


def test_complete_feature_script_releases_worktree_cwd_and_checks_cleanup_failures():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    assert "Set-Location $MainRoot" in script
    assert "$CleanupWorktreeCommand" in script
    assert "FLUX_KB_CLEANUP_MAIN_ROOT" in script
    assert "FLUX_KB_CLEANUP_FEATURE_WORKTREE" in script
    assert "FLUX_KB_CLEANUP_BRANCH" in script
    assert 'git worktree remove "$FeatureWorktree"' in script
    assert 'if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }' in script
    assert 'Invoke-FeatureStep -Name "cleanup-worktree" -Cwd $MainRoot -Command $CleanupWorktreeCommand' in script


def test_complete_feature_script_tolerates_empty_unregistered_cleanup_residue():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    assert "Test-WorktreeRegistered" in script
    assert "Test-DirectoryEmpty" in script
    assert "worktree remove left an empty directory" in script
    assert "$removeExit = $LASTEXITCODE" in script


def test_dev_flux_kb_wrapper_is_worktree_safe():
    wrapper_path = ROOT / "scripts" / "dev" / "flux-kb.ps1"
    assert wrapper_path.exists()

    wrapper = wrapper_path.read_text(encoding="utf-8")
    assert "ValueFromRemainingArguments" in wrapper
    assert "FLUX_KB_DEV_PYTHON" in wrapper
    assert "FLUX_KB_PYTHON" in wrapper
    assert "PYTHONPATH" in wrapper
    assert "-m flux_llm_kb.cli" in wrapper
    assert "pip install" not in wrapper


def test_setup_docs_describe_worktree_safe_flux_cli_wrapper():
    setup = (ROOT / "docs" / "setup.md").read_text(encoding="utf-8")

    assert ".\\scripts\\dev\\flux-kb.ps1 lint" in setup
    assert "worktree-safe" in setup
    assert "Do not run `python -m pip install -e .` inside temporary worktrees" in setup
    assert "D:\\FluxLLMKB\\app\\.venv" in setup


def test_setup_docs_describe_npm_online_and_pip_offline_feature_closeout():
    setup = (ROOT / "docs" / "setup.md").read_text(encoding="utf-8")
    setup_words = " ".join(setup.split())

    assert "Feature closeout through `scripts/dev/complete-feature.ps1` runs npm install by default" in setup_words
    assert "npm --prefix dashboard ci --include=dev --cache" in setup
    assert "`-AllowNpmInstall`" not in setup
    assert "`-RefreshNpmDependencies`" not in setup
    assert "`-AllowPipDownloads`" not in setup
    assert "`-RefreshPipDependencies`" not in setup
    assert "PipOffline:$false" not in setup
    assert "closeout script never enables pip downloads" in setup_words
    assert "`-DockerBaseMode python`" in setup
    assert "mount options is too" in setup
