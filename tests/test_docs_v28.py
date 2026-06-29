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
    assert "configured local loopback or docker" in combined
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


def test_v28_docs_describe_indexer_reliability_and_benchmark_history():
    roadmap = (ROOT / "docs" / "roadmap.md").read_text(encoding="utf-8").lower()
    architecture = (ROOT / "docs" / "architecture.md").read_text(encoding="utf-8").lower()
    setup = (ROOT / "docs" / "setup.md").read_text(encoding="utf-8").lower()
    integrations = (ROOT / "docs" / "integrations.md").read_text(encoding="utf-8").lower()
    combined = "\n".join([roadmap, architecture, setup, integrations])

    assert "watcher.backend" in combined
    assert "flux_kb_watcher_backend" in combined
    assert "flux-kb crawl watch probe" in combined
    assert "kb.watch_probe" in combined
    assert "acceleration_benchmark_runs" in architecture
    assert "crawl_path_manifests" in architecture
    assert "watcher_events" in architecture
    assert "worker-family backpressure" in combined
    assert "manifest_skipped_unchanged" in combined
    assert "hash parallelism" in combined
    assert "scan" in combined
    assert "soak" in combined
    assert "watcher" in combined
    assert "--passes" in combined
    assert "--scenario reliability" in combined
    assert "--scenario host_cloud" in combined
    assert "--scenario cache_readiness" in combined
    assert "--scenario tuning" in combined
    assert "recommendations.candidates" in combined
    assert "diagnostics[]" in combined
    assert "--compare-label" in combined
    assert "compare_label" in combined
    assert "settings_mutated: false" in combined
    assert "metadata only" in combined
    assert "raw text" in combined
    assert "private watched roots" in combined


def test_roadmap_tables_and_queue_have_plain_english_purpose():
    roadmap = (ROOT / "docs" / "roadmap.md").read_text(encoding="utf-8")
    table_headers = [line for line in roadmap.splitlines() if line.startswith("| ") and " | " in line]
    roadmap_table_headers = [
        line
        for line in table_headers
        if (
            ("Version" in line and "Summary" in line)
            or ("Piece" in line and "Roadmap Intent" in line)
        )
    ]

    assert roadmap_table_headers
    assert all("Plain-English Purpose" in header for header in roadmap_table_headers)

    queue_section = roadmap.split("## Queued Work In Roadmap Order", 1)[1].split("## Update Rules", 1)[0]
    queued_items = [line for line in queue_section.splitlines() if line.startswith("1.") or line.startswith("2.") or line.startswith("3.") or line.startswith("4.") or line.startswith("5.") or line.startswith("6.")]
    assert queued_items
    assert all("Plain-English purpose:" in item for item in queued_items)

    future_slice = roadmap.split("### Future Slice: Code-Aware Corpus Indexing", 1)[1].split("## V4:", 1)[0]
    assert "Plain-English overview:" in future_slice
    assert "Plain-English purpose:" in future_slice


def test_docs_describe_p0_retrieval_benchmark_queue_policy_and_interfaces():
    roadmap = (ROOT / "docs" / "roadmap.md").read_text(encoding="utf-8").lower()
    architecture = (ROOT / "docs" / "architecture.md").read_text(encoding="utf-8").lower()
    integrations = (ROOT / "docs" / "integrations.md").read_text(encoding="utf-8").lower()
    combined = "\n".join([roadmap, architecture, integrations])

    assert "retrieval_benchmark_runs" in architecture
    assert "flux-kb retrieval benchmark run" in integrations
    assert "kb.retrieval_benchmark_run" in integrations
    assert "post /api/retrieval/benchmarks/run" in integrations
    assert "brief dilution" in combined
    assert "precision@3" in combined
    assert "settings_mutated: false" in combined
    assert "queue policy" in roadmap
    assert "p0 to pn" in roadmap
    assert "do not force blocked p0 items" in roadmap
    queue_section = roadmap.split("## queued work in roadmap order", 1)[1].split("## update rules", 1)[0]
    assert "retrieval calibration" in queue_section
    assert "confidence-band" in queue_section
    assert "semantic duplicate calibration candidates" in queue_section
    assert "priority: p0. plain-english purpose: prove indexer and filesystem reliability" in queue_section
    assert "blocked until retrieval benchmark/live feedback" in queue_section
