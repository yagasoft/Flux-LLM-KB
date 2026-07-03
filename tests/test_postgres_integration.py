import os
from uuid import uuid4

import pytest

from flux_llm_kb import database
from flux_llm_kb.database import forget_episode, insert_episode, run_migrations, search_episodes
from flux_llm_kb.embeddings import EmbeddingResult
from flux_llm_kb.service import KnowledgeService


TEST_DATABASE_URL = os.environ.get("FLUX_KB_TEST_DATABASE_URL")

if not TEST_DATABASE_URL:
    pytest.skip("FLUX_KB_TEST_DATABASE_URL is not set", allow_module_level=True)


def test_postgres_hybrid_search_smoke():
    run_migrations(TEST_DATABASE_URL)
    marker = f"postgres-search-smoke-{uuid4()}"
    episode_id = insert_episode(
        title=f"Hybrid retrieval {marker}",
        summary="PostgreSQL full text and pg_trgm fuzzy search are enabled for degraded retrieval.",
        url=TEST_DATABASE_URL,
    )

    results = search_episodes(marker, limit=5, url=TEST_DATABASE_URL)

    assert any(result["id"] == episode_id for result in results)
    assert any({"lexical", "fuzzy"} & set(result["streams"]) for result in results)
    assert forget_episode(episode_id, url=TEST_DATABASE_URL) is True


def test_postgres_corpus_sync_search_and_watch_state(tmp_path, monkeypatch):
    monkeypatch.setenv("FLUX_KB_DATABASE_URL", TEST_DATABASE_URL)
    run_migrations(TEST_DATABASE_URL)
    marker = f"corpus-smoke-{uuid4()}"
    root = tmp_path / "corpus"
    root.mkdir()
    (root / "decision.md").write_text(f"{marker} says dashboard health is unified.", encoding="utf-8")
    name = f"corpus-{uuid4()}"
    database.add_monitored_root(name=name, root_path=root, watch_enabled=True, url=TEST_DATABASE_URL)

    try:
        sync_result = KnowledgeService().sync_corpus(root_name=name)
        results = database.search_corpus_chunks(marker, limit=5, url=TEST_DATABASE_URL)
        status = database.crawl_status(url=TEST_DATABASE_URL)

        assert sync_result["files_seen"] == 1
        assert sync_result["chunks_indexed"] == 1
        assert any(result["source_path"] == "decision.md" for result in results)
        assert status["active_watch_roots"] >= 1
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_targeted_sync_does_not_update_outside_target(tmp_path, monkeypatch):
    monkeypatch.setenv("FLUX_KB_DATABASE_URL", TEST_DATABASE_URL)
    run_migrations(TEST_DATABASE_URL)
    root = tmp_path / "targeted"
    root.mkdir()
    sub = root / "sub"
    sub.mkdir()
    outside_marker = f"outside-{uuid4()}"
    target_marker = f"target-{uuid4()}"
    changed_outside_marker = f"mutatedmarker{uuid4().hex}"
    changed_target_marker = f"changed-target-{uuid4()}"
    (root / "outside.md").write_text(outside_marker, encoding="utf-8")
    (sub / "inside.md").write_text(target_marker, encoding="utf-8")
    name = f"targeted-{uuid4()}"
    database.add_monitored_root(name=name, root_path=root, url=TEST_DATABASE_URL)

    try:
        service = KnowledgeService()
        service.sync_corpus(root_name=name)
        (root / "outside.md").write_text(changed_outside_marker, encoding="utf-8")
        (sub / "inside.md").write_text(changed_target_marker, encoding="utf-8")

        result = service.sync_corpus(path=sub / "inside.md")

        assert result["files_seen"] == 1
        assert database.search_corpus_chunks(changed_outside_marker, limit=5, url=TEST_DATABASE_URL) == []
        assert database.search_corpus_chunks(changed_target_marker, limit=5, url=TEST_DATABASE_URL)
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_duplicate_assets_preserve_paths_but_return_one_canonical_hit(tmp_path, monkeypatch):
    monkeypatch.setenv("FLUX_KB_DATABASE_URL", TEST_DATABASE_URL)
    run_migrations(TEST_DATABASE_URL)
    root = tmp_path / "dupes"
    root.mkdir()
    marker = f"duplicate-{uuid4()}"
    (root / "a.md").write_text(f"{marker} same body", encoding="utf-8")
    (root / "b.md").write_text(f"{marker} same body", encoding="utf-8")
    name = f"dupes-{uuid4()}"
    database.add_monitored_root(name=name, root_path=root, url=TEST_DATABASE_URL)

    try:
        KnowledgeService().sync_corpus(root_name=name)
        results = database.search_corpus_chunks(marker, limit=10, url=TEST_DATABASE_URL)
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    """
                    SELECT count(*), count(*) FILTER (WHERE canonical_asset_id IS NOT NULL)
                    FROM source_assets a
                    JOIN monitored_roots r ON r.id = a.root_id
                    WHERE r.name = %s
                    """,
                    (name,),
                )
                asset_count, duplicate_count = cur.fetchone()

        assert asset_count == 2
        assert duplicate_count == 1
        assert len(results) == 1
        assert results[0]["duplicate_count"] == 1
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_corpus_search_includes_lexical_stream(tmp_path, monkeypatch):
    monkeypatch.setenv("FLUX_KB_DATABASE_URL", TEST_DATABASE_URL)
    run_migrations(TEST_DATABASE_URL)
    root = tmp_path / "lexical"
    root.mkdir()
    marker = f"lexical-{uuid4()}"
    (root / "note.md").write_text(f"{marker} corpus semantic retrieval", encoding="utf-8")
    name = f"lexical-{uuid4()}"
    database.add_monitored_root(name=name, root_path=root, url=TEST_DATABASE_URL)

    try:
        KnowledgeService().sync_corpus(root_name=name)
        results = database.search_corpus_chunks(marker, limit=5, url=TEST_DATABASE_URL)

        assert any("corpus_lexical" in result["streams"] for result in results)
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_semantic_duplicate_refresh_suppresses_corpus_and_episode_results(tmp_path, monkeypatch):
    monkeypatch.setenv("FLUX_KB_DATABASE_URL", TEST_DATABASE_URL)
    run_migrations(TEST_DATABASE_URL)

    class FakeSnowflakeEmbeddingProvider:
        def __init__(self, *, model, dimensions):
            self.model = model
            self.dimensions = dimensions

        def embed_batch(self, inputs):
            return [
                EmbeddingResult(
                    owner_table=item.owner_table,
                    owner_id=item.owner_id,
                    model=item.model,
                    dimensions=item.dimensions,
                    vector=[1.0, 0.0],
                )
                for item in inputs
            ]

    monkeypatch.setattr("flux_llm_kb.embeddings.SnowflakeEmbeddingProvider", FakeSnowflakeEmbeddingProvider)

    marker = f"semantic-dupe-{uuid4()}"
    root = tmp_path / "semantic-dupes"
    root.mkdir()
    (root / "alpha.md").write_text(
        f"{marker} semantic duplicate architecture Vespa retrieval local ranking canonical",
        encoding="utf-8",
    )
    (root / "bravo.md").write_text(
        f"{marker} semantic duplicate architecture Vespa retrieval local ranking duplicate extra",
        encoding="utf-8",
    )
    name = f"semantic-dupes-{uuid4()}"
    database.add_monitored_root(name=name, root_path=root, trust_rank=900, url=TEST_DATABASE_URL)
    episode_a = insert_episode(
        title=f"{marker} operating decision alpha",
        summary="Semantic duplicate memories should be clustered and retrieved once.",
        metadata={"root_name": name, "workspace_key": f"root:{name}"},
        url=TEST_DATABASE_URL,
    )
    episode_b = insert_episode(
        title=f"{marker} operating decision bravo",
        summary="Semantic duplicate memories should be clustered and retrieved once with extra words.",
        metadata={"root_name": name, "workspace_key": f"root:{name}"},
        url=TEST_DATABASE_URL,
    )

    try:
        service = KnowledgeService()
        service.sync_corpus(root_name=name)
        refresh = database.refresh_semantic_duplicate_clusters(
            memory_class="all",
            root_name=name,
            threshold=0.7,
            url=TEST_DATABASE_URL,
        )
        corpus_results = database.search_corpus_chunks(marker, limit=10, root_name=name, url=TEST_DATABASE_URL)
        episode_results = search_episodes(
            marker,
            limit=10,
            workspace_key=f"root:{name}",
            url=TEST_DATABASE_URL,
        )
        explain = service.explain(
            marker,
            root_name=name,
            filters={"include_suppressed": True},
        )

        assert refresh["created_clusters"] >= 2
        assert len(corpus_results) == 1
        assert corpus_results[0]["semantic_duplicate_cluster"]["suppressed_count"] == 1
        assert len(episode_results) == 1
        assert episode_results[0]["semantic_duplicate_cluster"]["suppressed_count"] == 1
        assert explain["suppression"]["semantic_duplicates"]
    finally:
        forget_episode(episode_a, url=TEST_DATABASE_URL)
        forget_episode(episode_b, url=TEST_DATABASE_URL)
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))
                cur.execute("DELETE FROM semantic_duplicate_clusters WHERE root_name = %s", (name,))


def test_postgres_retrieval_benchmark_seeds_searches_persists_and_cleans_up(monkeypatch):
    monkeypatch.setenv("FLUX_KB_DATABASE_URL", TEST_DATABASE_URL)
    run_migrations(TEST_DATABASE_URL)
    label = f"retrieval-benchmark-{uuid4()}"

    result = KnowledgeService().run_retrieval_benchmark(suite="standard", label=label, limit_per_query=5)
    history = database.list_retrieval_benchmark_runs(suite="standard", label=label, url=TEST_DATABASE_URL)
    psycopg = database._load_psycopg()
    with psycopg.connect(TEST_DATABASE_URL) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT count(*) FROM monitored_roots WHERE name LIKE '__retrieval_benchmark_%'")
            synthetic_roots = cur.fetchone()[0]

    assert result["status"] == "completed"
    assert result["query_count"] >= 4
    assert result["metrics"]["top1_accuracy"] >= 0.5
    assert result["metrics"]["brief_dilution"] >= 0.0
    categories = {str(case.get("category") or "") for case in result["case_results"]}
    assert {
        "mail_filter",
        "current_only",
        "semantic_duplicate",
        "semantic_guardrail",
        "code_symbol_miss",
    }.issubset(categories)
    assert result["calibration_summary"]["semantic_thresholds"]
    assert result["recommendations"]["settings_mutated"] is False
    assert result["recommendations"]["candidates"]
    assert history
    assert history[0]["label"] == label
    assert history[0]["calibration_summary"]["confidence_bands"]
    assert "metric_deltas" in history[0]
    assert synthetic_roots == 0


def test_postgres_claim_lifecycle_and_graph_traversal_are_migration_backed():
    run_migrations(TEST_DATABASE_URL)
    marker = f"graph-lifecycle-{uuid4()}"

    try:
        claim = database.upsert_claim(
            subject_type="project",
            subject_name=f"{marker}-flux",
            predicate="uses",
            object_text=f"{marker} PostgreSQL lifecycle graph",
            confidence=0.82,
            url=TEST_DATABASE_URL,
        )
        replacement = database.upsert_claim(
            subject_type="project",
            subject_name=f"{marker}-flux",
            predicate="uses",
            object_text=f"{marker} PostgreSQL plus lifecycle graph scoring",
            confidence=0.9,
            url=TEST_DATABASE_URL,
        )
        contradicted = database.transition_claim(
            claim_id=claim["id"],
            transition="contradict",
            related_claim_id=replacement["id"],
            reason="newer evidence",
            url=TEST_DATABASE_URL,
        )
        database.upsert_entity_relation(
            from_entity_id=claim["subject_entity_id"],
            to_entity_id=replacement["subject_entity_id"],
            relation_type="depends_on",
            confidence=0.7,
            url=TEST_DATABASE_URL,
        )

        traversal = database.traverse_entity_graph(
            entity_id=claim["subject_entity_id"],
            relation_types=["depends_on"],
            max_depth=2,
            url=TEST_DATABASE_URL,
        )
        fetched = database.get_claim(claim["id"], url=TEST_DATABASE_URL)

        assert contradicted["lifecycle_state"] == "contradicted"
        assert fetched["lifecycle"]["audit_events"]
        assert fetched["lifecycle"]["related_claims"][0]["relation_type"] == "contradicts"
        assert traversal["edges"][0]["relation_type"] == "depends_on"
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM entities WHERE name LIKE %s", (f"{marker}%",))


def test_postgres_claim_review_list_counts_and_graph_work_together():
    run_migrations(TEST_DATABASE_URL)
    marker = f"claim-review-{uuid4()}"

    try:
        active = database.upsert_claim(
            subject_type="project",
            subject_name=f"{marker}-flux",
            predicate="uses",
            object_text=f"{marker} active PostgreSQL",
            confidence=0.8,
            url=TEST_DATABASE_URL,
        )
        stale = database.upsert_claim(
            subject_type="project",
            subject_name=f"{marker}-flux",
            predicate="uses",
            object_text=f"{marker} stale PostgreSQL",
            confidence=0.7,
            url=TEST_DATABASE_URL,
        )
        replacement = database.upsert_claim(
            subject_type="system",
            subject_name=f"{marker}-graph",
            predicate="supports",
            object_text=f"{marker} graph review traversal",
            confidence=0.9,
            url=TEST_DATABASE_URL,
        )
        database.transition_claim(
            claim_id=stale["id"],
            transition="deprioritize",
            reason="review queue",
            url=TEST_DATABASE_URL,
        )
        database.upsert_entity_relation(
            from_entity_id=active["subject_entity_id"],
            to_entity_id=replacement["subject_entity_id"],
            relation_type="depends_on",
            confidence=0.7,
            url=TEST_DATABASE_URL,
        )

        claims = database.list_claims(review="needs_review", q=marker, limit=10, url=TEST_DATABASE_URL)
        counts = database.claim_review_counts(url=TEST_DATABASE_URL)
        graph = database.traverse_entity_graph(
            entity_id=active["subject_entity_id"],
            relation_types=["depends_on"],
            direction="both",
            max_depth=2,
            url=TEST_DATABASE_URL,
        )

        assert [claim["id"] for claim in claims] == [stale["id"]]
        assert claims[0]["review_reasons"] == ["stale", "retention:deprioritize"]
        assert counts["needs_review"] >= 1
        assert graph["edges"][0]["relation_type"] == "depends_on"
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM entities WHERE name LIKE %s", (f"{marker}%",))
