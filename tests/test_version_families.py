from flux_llm_kb.service import KnowledgeService
from flux_llm_kb.versioning import document_family_key


def test_document_family_key_removes_common_version_noise():
    assert document_family_key("RFP Response v1.docx") == document_family_key("RFP Response v2 final.docx")
    assert document_family_key("Customer Proposal 2026-06-01.pdf") == document_family_key("Customer Proposal 2026-06-18.pdf")
    assert document_family_key("Architecture copy (2).md") == document_family_key("Architecture.md")


def test_service_search_suppresses_same_document_version_siblings(monkeypatch):
    from flux_llm_kb import database

    monkeypatch.setattr(database, "search_episodes", lambda query, limit=5, **_kwargs: [])
    monkeypatch.setattr(
        database,
        "search_corpus_chunks",
        lambda query, limit=5, **_kwargs: [
            {
                "id": "chunk-v1",
                "title": "RFP Response",
                "summary": "older response",
                "score": 0.91,
                "source_path": "client/RFP Response v1.docx",
                "duplicate_count": 0,
                "trust_rank": 500,
            },
            {
                "id": "chunk-v2",
                "title": "RFP Response",
                "summary": "newer response",
                "score": 0.89,
                "source_path": "client/RFP Response v2 final.docx",
                "duplicate_count": 0,
                "trust_rank": 900,
            },
            {
                "id": "chunk-other",
                "title": "Delivery Plan",
                "summary": "different doc",
                "score": 0.80,
                "source_path": "client/Delivery Plan.docx",
                "duplicate_count": 0,
                "trust_rank": 500,
            },
        ],
    )

    results = KnowledgeService().search("rfp response", limit=5)

    assert [item["id"] for item in results] == ["chunk-v2", "chunk-other"]
    assert results[0]["version_family"]["suppressed_count"] == 1
    assert results[0]["version_family"]["canonical_source_path"] == "client/RFP Response v2 final.docx"
