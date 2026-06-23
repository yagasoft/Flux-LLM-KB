from pathlib import Path

from flux_llm_kb import mcp_server


def test_mcp_exposes_claim_and_graph_tools():
    source = Path(mcp_server.__file__).read_text(encoding="utf-8")

    assert "def search(query: str, limit: int = 5, cwd: str | None = None, root_name: str | None = None, scope_mode: str = \"local_first\")" in source
    assert "def brief(query: str, token_budget: int = 1200, cwd: str | None = None, root_name: str | None = None, scope_mode: str = \"local_first\")" in source
    assert "scope_mode=scope_mode" in source
    assert "workspace_boosted" in source
    assert "query mid-turn" in source
    assert "expanded kb.search" in source
    assert '@mcp.tool(name="kb.claim_upsert")' in source
    assert '@mcp.tool(name="kb.claim_transition")' in source
    assert '@mcp.tool(name="kb.graph_traverse")' in source
    assert '@mcp.tool(name="kb.capture_review")' in source
    assert '@mcp.tool(name="kb.capture_review_decide")' in source
    assert "def capture_review_decide(job_id: str, decision: str, rationale: str)" in source
    assert "review_capture_job(" in source
