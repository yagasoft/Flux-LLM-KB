from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_v28_docs_describe_local_asr_model_path_and_cache_policy():
    roadmap = (ROOT / "docs" / "roadmap.md").read_text(encoding="utf-8").lower()
    architecture = (ROOT / "docs" / "architecture.md").read_text(encoding="utf-8").lower()
    coverage = (ROOT / "docs" / "file-type-coverage.md").read_text(encoding="utf-8").lower()
    setup = (ROOT / "docs" / "setup.md").read_text(encoding="utf-8").lower()
    combined = "\n".join([roadmap, architecture, coverage, setup])

    assert "asr" in roadmap
    assert "faster-whisper" in combined
    assert "acceleration.asr.model_path" in combined
    assert "asr cache" in combined
    assert "remote model download" in combined
    assert "cloud transcription" in combined
