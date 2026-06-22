import json

from flux_llm_kb import database
from flux_llm_kb import service
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


def test_service_search_formats_mail_manifest_results(monkeypatch):
    manifest = {
        "subject": "YsTrader alert: shared market data unavailable",
        "sender": "YsTrader <alerts@example.com>",
        "recipients": ["me@example.com"],
        "received_at": "Mon, 22 Jun 2026 19:19:26 +0000",
        "source_folder": "FluxCapture",
        "attachment_count": 1,
    }
    monkeypatch.setattr(database, "search_episodes", lambda query, limit=5: [])
    monkeypatch.setattr(
        database,
        "search_corpus_chunks",
        lambda query, limit=20: [
            {
                "id": "chunk-1",
                "title": "manifest.json",
                "summary": json.dumps(manifest),
                "score": 0.032,
                "streams": ["corpus_lexical"],
                "raw_scores": {"corpus_lexical": 0.24},
                "source_path": "export-1/manifest.json",
                "duplicate_count": 0,
                "trust_rank": 450,
            }
        ],
    )

    results = KnowledgeService().search("ystrader", limit=5)

    assert results[0]["title"] == "Mail: YsTrader alert: shared market data unavailable"
    assert "From YsTrader" in results[0]["summary"]
    assert "FluxCapture" in results[0]["summary"]
    assert "1 attachment" in results[0]["summary"]
    assert results[0]["excerpt"] == results[0]["summary"]


def test_service_search_collapses_mail_spool_siblings(monkeypatch):
    manifest = {
        "export_id": "export-1",
        "profile_name": "gmail-capture",
        "subject": "Customer RFP",
        "sender": "Sender <sender@example.com>",
        "recipients": ["me@example.com"],
        "source_folder": "FluxCapture",
        "attachment_count": 1,
    }
    monkeypatch.setattr(database, "search_episodes", lambda query, limit=5: [])
    monkeypatch.setattr(
        database,
        "search_corpus_chunks",
        lambda query, limit=20: [
            {
                "id": "chunk-body",
                "asset_id": "asset-body",
                "title": "body.txt",
                "summary": "Please review the Customer RFP",
                "score": 0.060,
                "streams": ["corpus_lexical"],
                "raw_scores": {"corpus_lexical": 0.4},
                "source_path": "export-1/body.txt",
                "duplicate_count": 0,
                "trust_rank": 450,
            },
            {
                "id": "chunk-manifest",
                "asset_id": "asset-manifest",
                "title": "manifest.json",
                "summary": json.dumps(manifest),
                "score": 0.050,
                "streams": ["corpus_lexical"],
                "raw_scores": {"corpus_lexical": 0.3},
                "source_path": "export-1/manifest.json",
                "duplicate_count": 0,
                "trust_rank": 450,
            },
            {
                "id": "chunk-attachment",
                "asset_id": "asset-attachment",
                "title": "rfp.pdf",
                "summary": "Customer RFP attachment",
                "score": 0.040,
                "streams": ["corpus_fuzzy"],
                "raw_scores": {"corpus_fuzzy": 0.2},
                "source_path": "export-1/attachments/rfp.pdf",
                "duplicate_count": 0,
                "trust_rank": 450,
            },
        ],
    )

    results = KnowledgeService().search("customer rfp", limit=5)

    assert len(results) == 1
    assert results[0]["logical_kind"] == "mail"
    assert results[0]["title"] == "Mail: Customer RFP"
    assert results[0]["source_path"] == "export-1/manifest.json"
    assert results[0]["related_evidence_count"] == 2
    assert results[0]["detail_ref"] == {"kind": "corpus_chunk", "id": "chunk-manifest"}


def test_service_brief_uses_configured_token_budget(monkeypatch):
    observed = {}

    class FakeSetting:
        raw_value = 321

    class FakeSettingsService:
        def resolve(self, key):
            assert key == "retrieval.token_budget"
            return FakeSetting()

    monkeypatch.setattr(service, "SettingsService", FakeSettingsService)
    monkeypatch.setattr(KnowledgeService, "search", lambda self, query, limit=10: [])
    monkeypatch.setattr(
        service,
        "pack_context",
        lambda candidates, token_budget: (observed.setdefault("token_budget", token_budget), "brief")[1],
    )

    assert KnowledgeService().brief("anything") == "brief"
    assert observed["token_budget"] == 321
