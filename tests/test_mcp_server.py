from pathlib import Path

from flux_llm_kb import mcp_server


def test_mcp_exposes_claim_and_graph_tools():
    source = Path(mcp_server.__file__).read_text(encoding="utf-8")

    assert '@mcp.tool(name="kb.claim_upsert")' in source
    assert '@mcp.tool(name="kb.claim_transition")' in source
    assert '@mcp.tool(name="kb.graph_traverse")' in source
