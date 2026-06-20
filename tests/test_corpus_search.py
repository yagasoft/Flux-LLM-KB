from flux_llm_kb import database
from flux_llm_kb.service import KnowledgeService


def test_service_search_includes_corpus_chunks(monkeypatch):
    monkeypatch.setattr(
        database,
        "search_episodes",
        lambda query, limit=5: [
            {
                "id": "episode-1",
                "title": "Prior decision",
                "summary": "Use PostgreSQL for memory.",
                "score": 0.2,
                "streams": ["lexical"],
                "raw_scores": {"lexical": 1.0},
            }
        ],
    )
    monkeypatch.setattr(
        database,
        "search_corpus_chunks",
        lambda query, limit=5: [
            {
                "id": "chunk-1",
                "title": "architecture.md",
                "summary": "Crawler dashboard health is centralized.",
                "score": 0.9,
                "streams": ["corpus_lexical"],
                "raw_scores": {"corpus_lexical": 1.0},
                "source_path": "docs/architecture.md",
            }
        ],
    )

    results = KnowledgeService().search("crawler dashboard", limit=5)

    assert [result["kind"] for result in results] == ["corpus_chunk", "episode"]
    assert results[0]["source_path"] == "docs/architecture.md"
