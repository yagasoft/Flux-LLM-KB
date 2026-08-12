from __future__ import annotations

import pytest


@pytest.mark.filterwarnings(
    "ignore:Using `httpx` with `starlette.testclient` is deprecated:starlette.exceptions.StarletteDeprecationWarning"
)
def test_legacy_rest_allows_direct_loopback_but_rejects_remote_and_forwarded_requests(monkeypatch):
    from fastapi.testclient import TestClient
    from flux_llm_kb.rest_api import create_app

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())
    app = create_app()

    assert TestClient(app, client=("127.0.0.1", 50100)).get("/api/health").status_code == 200
    assert TestClient(app, client=("192.0.2.20", 50100)).get("/api/health").status_code == 403
    assert (
        TestClient(app, client=("127.0.0.1", 50100))
        .get("/api/health", headers={"Forwarded": "for=192.0.2.20"})
        .status_code
        == 403
    )


def test_legacy_local_disclosure_returns_clean_detail_and_withholds_the_synthetic_secret():
    from flux_llm_kb.local_visibility import LocalDisclosureKind, evaluate_local_disclosure

    clean = evaluate_local_disclosure("namespace Flux;", LocalDisclosureKind.RETAINED_DETAIL)
    secret = evaluate_local_disclosure("secret-content-sentinel", LocalDisclosureKind.RETAINED_DETAIL)

    assert clean.value == "namespace Flux;"
    assert clean.withheld is False
    assert clean.reason_code is None
    assert secret.value is None
    assert secret.withheld is True
    assert secret.reason_code == "secret-content-withheld"


@pytest.mark.parametrize(
    "synthetic_value",
    [
        "-----BEGIN RSA PRIVATE KEY-----\nsynthetic-key-material\n-----END RSA PRIVATE KEY-----",
        "-----BEGIN EC PRIVATE KEY-----\nsynthetic-key-material\n-----END EC PRIVATE KEY-----",
        "-----BEGIN OPENSSH PRIVATE KEY-----\nsynthetic-key-material\n-----END OPENSSH PRIVATE KEY-----",
        "-----BEGIN ENCRYPTED PRIVATE KEY-----\nsynthetic-key-material\n-----END ENCRYPTED PRIVATE KEY-----",
        "-----BEGIN PGP PRIVATE KEY BLOCK-----\nsynthetic-key-material\n-----END PGP PRIVATE KEY BLOCK-----",
        "postgresql://synthetic-user:synthetic-password@127.0.0.1/synthetic",
        "https://synthetic-user:synthetic-password@localhost/synthetic",
    ],
)
def test_legacy_local_disclosure_withholds_private_key_envelopes_and_credential_uris(synthetic_value):
    from flux_llm_kb.local_visibility import LocalDisclosureKind, evaluate_local_disclosure

    disclosure = evaluate_local_disclosure(synthetic_value, LocalDisclosureKind.CODE_EXCERPT)

    assert disclosure.value is None
    assert disclosure.withheld is True
    assert disclosure.reason_code == "secret-content-withheld"


def test_legacy_mcp_starts_only_the_stdio_transport(monkeypatch):
    from flux_llm_kb import mcp_server

    calls: list[dict] = []

    class FakeMcp:
        def run(self, **kwargs):
            calls.append(kwargs)

    monkeypatch.setattr(mcp_server, "create_server", FakeMcp)

    mcp_server.main()

    assert calls == [{"transport": "stdio"}]
