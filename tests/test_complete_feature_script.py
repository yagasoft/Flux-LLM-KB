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


def test_complete_feature_script_orders_cleanup_after_deploy_probe():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    deploy_index = script.index("scripts\\deploy\\update-flux.ps1")
    probe_index = script.index("http://127.0.0.1:8765/api/dashboard/health")
    repair_index = script.index('Invoke-FeatureStep -Name "repair-python-editable-install"')
    cleanup_index = script.index("git worktree remove")

    assert deploy_index < probe_index < repair_index < cleanup_index


def test_complete_feature_script_installs_dashboard_dependencies_before_tests():
    script = (ROOT / "scripts" / "dev" / "complete-feature.ps1").read_text(encoding="utf-8")

    install_step = (
        'Invoke-FeatureStep -Name "dashboard-install" '
        "-Cwd $FeatureWorktree -Command 'npm --prefix dashboard ci'"
    )
    test_step = (
        'Invoke-FeatureStep -Name "dashboard-test" '
        "-Cwd $FeatureWorktree -Command 'npm --prefix dashboard test'"
    )
    build_step = (
        'Invoke-FeatureStep -Name "dashboard-build" '
        "-Cwd $FeatureWorktree -Command 'npm --prefix dashboard run build'"
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
