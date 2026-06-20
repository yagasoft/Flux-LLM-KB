import os
from uuid import uuid4

import pytest

from flux_llm_kb.database import forget_episode, insert_episode, run_migrations, search_episodes


TEST_DATABASE_URL = os.environ.get("FLUX_KB_TEST_DATABASE_URL")

if not TEST_DATABASE_URL:
    pytest.skip("FLUX_KB_TEST_DATABASE_URL is not set", allow_module_level=True)


def test_postgres_hybrid_search_smoke():
    run_migrations(TEST_DATABASE_URL)
    marker = f"pgvector-smoke-{uuid4()}"
    episode_id = insert_episode(
        title=f"Hybrid retrieval {marker}",
        summary="PostgreSQL full text, pg_trgm fuzzy search, and pgvector ranking are enabled.",
        url=TEST_DATABASE_URL,
    )

    results = search_episodes(marker, limit=5, url=TEST_DATABASE_URL)

    assert any(result["id"] == episode_id for result in results)
    assert any("vector" in result["streams"] for result in results)
    assert forget_episode(episode_id, url=TEST_DATABASE_URL) is True
