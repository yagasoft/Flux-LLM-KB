from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DASHBOARD_SRC = ROOT / "dashboard" / "src"
DASHBOARD_VITE_CONFIG = ROOT / "dashboard" / "vite.config.ts"


def test_dashboard_app_tests_are_split_for_vitest_file_parallelism():
    test_files = sorted(path.name for path in DASHBOARD_SRC.glob("App.*.test.tsx"))

    assert "App.test.tsx" not in test_files
    assert len(test_files) >= 4
    assert "App.overview-performance.test.tsx" in test_files
    assert "App.review-jobs.test.tsx" in test_files
    assert "App.mail-corpus-settings.test.tsx" in test_files
    assert "App.retrieval-details.test.tsx" in test_files


def test_dashboard_app_tests_share_a_single_harness():
    harness = DASHBOARD_SRC / "test" / "appHarness.ts"
    assert harness.exists()

    source = harness.read_text(encoding="utf-8")
    assert "setupDashboardTest" in source
    assert "deferredResponse" in source
    assert 'vi.stubGlobal("fetch"' in source


def test_dashboard_vitest_uses_bounded_fork_workers():
    source = DASHBOARD_VITE_CONFIG.read_text(encoding="utf-8")

    assert 'pool: "forks"' in source
    assert "poolOptions" in source
    assert "forks" in source
    assert "minForks: 1" in source
    assert "maxForks: 4" in source
