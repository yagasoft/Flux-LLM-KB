import ast
from pathlib import Path

from flux_llm_kb import mcp_server


def _mcp_tool_functions(source: str) -> list[ast.FunctionDef]:
    tree = ast.parse(source)
    return [
        node
        for node in ast.walk(tree)
        if isinstance(node, ast.FunctionDef)
        and any(
            isinstance(decorator, ast.Call)
            and isinstance(decorator.func, ast.Attribute)
            and decorator.func.attr == "tool"
            for decorator in node.decorator_list
        )
    ]


def _mcp_tool_names(source: str) -> list[str]:
    names: list[str] = []
    for node in _mcp_tool_functions(source):
        for decorator in node.decorator_list:
            if not (
                isinstance(decorator, ast.Call)
                and isinstance(decorator.func, ast.Attribute)
                and decorator.func.attr == "tool"
            ):
                continue
            for keyword in decorator.keywords:
                if keyword.arg == "name" and isinstance(keyword.value, ast.Constant):
                    names.append(str(keyword.value.value))
    return sorted(names)


def test_mcp_exposes_claim_and_graph_tools():
    source = Path(mcp_server.__file__).read_text(encoding="utf-8")

    assert "def search(query: str, limit: int = 5, cwd: str | None = None, root_name: str | None = None, scope_mode: str = \"local_first\", filters: dict | None = None)" in source
    assert "def explain(query: str, limit: int = 5, token_budget: int = 1200, cwd: str | None = None, root_name: str | None = None, scope_mode: str = \"local_first\", filters: dict | None = None)" in source
    assert "def brief(query: str, token_budget: int = 1200, cwd: str | None = None, root_name: str | None = None, scope_mode: str = \"local_first\", filters: dict | None = None)" in source
    assert "def remember(title: str, body: str, cwd: str | None = None, root_name: str | None = None)" in source
    assert "def finalize_turn(title: str, summary: str, cwd: str | None = None, root_name: str | None = None)" in source
    assert "Finalize the current agent turn by storing a redacted durable summary." in source
    assert "Use kb.remember for concise durable atomic saves" in source
    assert "do not wait for turn finalization" in source
    assert "Finalize with kb.finalize_turn at turn end" in source
    assert "avoid duplicating every prior kb.remember item" in source
    assert "service.remember(title, summary, metadata={\"source\": \"finalize_turn\"}, cwd=cwd, root_name=root_name)" in source
    assert "scope_mode=scope_mode" in source
    assert "filters=filters" in source
    assert "workspace_boosted" in source
    assert "query mid-turn" in source
    assert "expanded kb.search" in source
    assert '@mcp.tool(name="kb.explain")' in source
    assert '@mcp.tool(name="kb.claim_upsert")' in source
    assert '@mcp.tool(name="kb.claim_transition")' in source
    assert '@mcp.tool(name="kb.graph_traverse")' in source
    assert '@mcp.tool(name="kb.capture_review")' in source
    assert '@mcp.tool(name="kb.capture_review_decide")' in source
    assert '@mcp.tool(name="kb.retention_policies")' in source
    assert '@mcp.tool(name="kb.retention_quality")' in source
    assert '@mcp.tool(name="kb.semantic_duplicates_refresh")' in source
    assert '@mcp.tool(name="kb.semantic_duplicates_list")' in source
    assert '@mcp.tool(name="kb.acceleration_status")' in source
    assert '@mcp.tool(name="kb.watch_probe")' in source
    assert '@mcp.tool(name="kb.worker_status")' in source
    assert '@mcp.tool(name="kb.benchmark_run")' in source
    assert '@mcp.tool(name="kb.benchmark_history")' in source
    assert '@mcp.tool(name="kb.retrieval_benchmark_run")' in source
    assert '@mcp.tool(name="kb.retrieval_benchmark_history")' in source
    assert '@mcp.tool(name="kb.embeddings_status")' in source
    assert '@mcp.tool(name="kb.embeddings_enqueue")' in source
    assert '@mcp.tool(name="kb.embeddings_backfill")' in source
    assert "def capture_review_decide(job_id: str, decision: str, rationale: str)" in source
    assert "def retention_quality(limit: int = 25)" in source
    assert 'def semantic_duplicates_refresh(memory_class: str = "all", root_name: str | None = None, threshold: float | None = None, limit: int = 1000)' in source
    assert "def semantic_duplicates_list(memory_class: str | None = None, root_name: str | None = None, limit: int = 50)" in source
    assert "def acceleration_status()" in source
    assert "def watch_probe(timeout_seconds: float = 2.0)" in source
    assert "def worker_status(family: str = \"all\")" in source
    assert 'def benchmark_run(fixture: str = "all", files: int = 10, mode: str = "scan", passes: int = 1, label: str | None = None, compare_label: str | None = None, workers: int = 1, family: str = "all", scope: str = "synthetic", root_name: str | None = None, path: str | None = None, max_files: int | None = None, deployment_label: str | None = None, scenario: str = "standard", include_model_probe: bool = False)' in source
    assert "def benchmark_history(fixture: str | None = None, mode: str | None = None, label: str | None = None, warm_state: str | None = None, scope_type: str | None = None, deployment_label: str | None = None, limit: int = 20)" in source
    assert 'def retrieval_benchmark_run(suite: str = "standard", label: str | None = None, compare_label: str | None = None, limit_per_query: int = 5, token_budget: int | None = None, persist: bool = True)' in source
    assert 'def retrieval_benchmark_history(suite: str | None = None, label: str | None = None, limit: int = 20)' in source
    assert "review_capture_job(" in source


def test_all_mcp_tools_have_discoverable_docstrings():
    source = Path(mcp_server.__file__).read_text(encoding="utf-8")
    tool_functions = _mcp_tool_functions(source)

    missing = [node.name for node in tool_functions if not ast.get_docstring(node)]

    assert missing == []


def test_integrations_docs_list_every_mcp_tool():
    source = Path(mcp_server.__file__).read_text(encoding="utf-8")
    repo_root = Path(mcp_server.__file__).resolve().parents[2]
    docs = (repo_root / "docs" / "integrations.md").read_text(encoding="utf-8")

    missing = [tool_name for tool_name in _mcp_tool_names(source) if f"`{tool_name}`" not in docs]

    assert missing == []
