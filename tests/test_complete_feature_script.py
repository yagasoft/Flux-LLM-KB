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
    assert "Dashboard health reported blocked required checks" in script
    assert "database.checks" in script


def test_complete_feature_script_orders_cleanup_after_deploy_probe():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    deploy_index = script.index("scripts\\deploy\\update-flux.ps1")
    probe_index = script.index("http://127.0.0.1:8765/api/dashboard/health")
    reclaim_index = script.index('Invoke-FeatureStep -Name "post-deploy-outlook-spool-reclaim"')
    repair_index = script.index('Invoke-FeatureStep -Name "repair-python-editable-install"')
    cleanup_index = script.index('Invoke-FeatureStep -Name "cleanup-worktree"')

    assert deploy_index < probe_index < reclaim_index < repair_index < cleanup_index


def test_status_script_reports_blocked_required_dashboard_health():
    script = (ROOT / "scripts" / "deploy" / "status-flux.ps1").read_text(encoding="utf-8")

    assert "Dashboard health reported blocked required checks" in script
    assert "database.checks" in script
    assert "required -ne $false" in script


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


def test_complete_feature_script_installs_dashboard_dependencies_before_tests():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    install_step = (
        'Invoke-FeatureStep -Name "dashboard-install" '
        "-Cwd $FeatureWorktree -Command 'npm --prefix dashboard ci --include=dev'"
    )
    test_step = (
        'Invoke-FeatureStep -Name "dashboard-test" '
        "-Cwd $FeatureWorktree -Command 'Push-Location dashboard; try { node node_modules/vitest/vitest.mjs run } finally { Pop-Location }'"
    )
    build_step = (
        'Invoke-FeatureStep -Name "dashboard-build" '
        "-Cwd $FeatureWorktree -Command 'Push-Location dashboard; try { node node_modules/vite/bin/vite.js build } finally { Pop-Location }'"
    )

    assert install_step in script
    assert test_step in script
    assert build_step in script
    assert script.index(install_step) < script.index(test_step) < script.index(build_step)


def test_complete_feature_script_repairs_shared_editable_install_before_worktree_cleanup():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    assert 'Invoke-FeatureStep -Name "repair-python-editable-install"' in script
    assert "python -m pip show flux-llm-kb" in script
    assert "Editable project location:" in script
    assert "Test-Path -LiteralPath $editableLocation" in script
    assert "Test-UnderPath -Path $editableLocation -Root $FeatureWorktree" in script
    assert 'python -m pip install -e "$MainRoot[dev]"' in script


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


def test_complete_feature_script_uses_longer_timeout_for_production_deploy():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    deploy_step = (
        'Invoke-FeatureStep -Name "deploy-production" '
        "-Cwd $MainRoot -Command '.\\scripts\\deploy\\update-flux.ps1 -GpuMode on -SkipDashboardBuild' "
        "-TimeoutSeconds $DeployStepTimeoutSeconds"
    )

    assert deploy_step in script


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
