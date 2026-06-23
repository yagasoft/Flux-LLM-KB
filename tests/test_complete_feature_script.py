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
    cleanup_index = script.index("git worktree remove")

    assert deploy_index < probe_index < cleanup_index


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
