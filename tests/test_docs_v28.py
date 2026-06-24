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


def test_v28_docs_describe_local_visual_media_enrichment_policy():
    roadmap = (ROOT / "docs" / "roadmap.md").read_text(encoding="utf-8").lower()
    architecture = (ROOT / "docs" / "architecture.md").read_text(encoding="utf-8").lower()
    coverage = (ROOT / "docs" / "file-type-coverage.md").read_text(encoding="utf-8").lower()
    combined = "\n".join([roadmap, architecture, coverage])

    assert "acceleration.vision.enabled" in combined
    assert "vision cache" in combined
    assert "decorative-image" in combined
    assert "scene-transition" in combined
    assert "thumbnail cache" in combined
    assert "loopback" in combined
    assert "configured loopback local inference" in combined
    assert "ollama-compatible" in combined
    assert "gemma-class" in combined
    assert "loopback ollama" not in combined
    assert "ollama only" not in combined
    assert "only through loopback ollama" not in combined


def test_v28_docs_describe_publication_and_embedded_sidecar_extraction():
    roadmap = (ROOT / "docs" / "roadmap.md").read_text(encoding="utf-8").lower()
    architecture = (ROOT / "docs" / "architecture.md").read_text(encoding="utf-8").lower()
    coverage = (ROOT / "docs" / "file-type-coverage.md").read_text(encoding="utf-8").lower()
    combined = "\n".join([roadmap, architecture, coverage])

    assert "epub" in combined
    assert "fb2" in combined
    assert "ebook-convert" in combined
    assert "comic archive" in combined
    assert "embedded media sidecar" in combined
