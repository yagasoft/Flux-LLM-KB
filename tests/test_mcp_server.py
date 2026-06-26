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
    assert '@mcp.tool(name="kb.capture_review_ingest")' in source
    assert '@mcp.tool(name="kb.retention_policies")' in source
    assert '@mcp.tool(name="kb.retention_quality")' in source
    assert '@mcp.tool(name="kb.semantic_duplicates_refresh")' in source
    assert '@mcp.tool(name="kb.semantic_duplicates_list")' in source
    assert '@mcp.tool(name="kb.acceleration_status")' in source
    assert '@mcp.tool(name="kb.watch_probe")' in source
    assert '@mcp.tool(name="kb.worker_status")' in source
    assert '@mcp.tool(name="kb.crawl_backfill")' in source
    assert '@mcp.tool(name="kb.benchmark_run")' in source
    assert '@mcp.tool(name="kb.benchmark_history")' in source
    assert '@mcp.tool(name="kb.indexer_reliability_status")' in source
    assert '@mcp.tool(name="kb.indexer_reliability_run")' in source
    assert '@mcp.tool(name="kb.operator_evidence")' in source
    assert '@mcp.tool(name="kb.indexer_root_reliability")' in source
    assert '@mcp.tool(name="kb.indexer_reliability_roots")' in source
    assert '@mcp.tool(name="kb.code_status")' in source
    assert '@mcp.tool(name="kb.code_search")' in source
    assert '@mcp.tool(name="kb.code_symbol_lookup")' in source
    assert '@mcp.tool(name="kb.code_feedback_record")' in source
    assert '@mcp.tool(name="kb.code_feedback_summary")' in source
    assert '@mcp.tool(name="kb.operational_diagnostics")' in source
    assert '@mcp.tool(name="kb.diagnostics_remediate")' in source
    assert '@mcp.tool(name="kb.retrieval_benchmark_run")' in source
    assert '@mcp.tool(name="kb.retrieval_benchmark_history")' in source
    assert '@mcp.tool(name="kb.governance_run")' in source
    assert '@mcp.tool(name="kb.governance_actions")' in source
    assert '@mcp.tool(name="kb.governance_apply")' in source
    assert '@mcp.tool(name="kb.governance_recover")' in source
    assert '@mcp.tool(name="kb.governance_digest")' in source
    assert '@mcp.tool(name="kb.governance_policy")' in source
    assert '@mcp.tool(name="kb.embeddings_status")' in source
    assert '@mcp.tool(name="kb.embeddings_enqueue")' in source
    assert '@mcp.tool(name="kb.embeddings_backfill")' in source
    assert 'def capture_review(status: str = "pending_review", limit: int = 50)' in source
    assert "def capture_review_decide(job_id: str, decision: str, rationale: str)" in source
    assert "def capture_review_ingest(job_id: str | None = None, limit: int = 25, dry_run: bool = False)" in source
    assert "def retention_quality(limit: int = 25)" in source
    assert 'def semantic_duplicates_refresh(memory_class: str = "all", root_name: str | None = None, threshold: float | None = None, limit: int = 1000)' in source
    assert "def semantic_duplicates_list(memory_class: str | None = None, root_name: str | None = None, limit: int = 50)" in source
    assert "def acceleration_status()" in source
    assert "def watch_probe(timeout_seconds: float = 2.0)" in source
    assert "def worker_status(family: str = \"all\")" in source
    assert "def crawl_backfill(kind: str = \"all\", limit: int = 10, workers: int = 1, root_name: str | None = None, family: str | None = None)" in source
    assert 'def benchmark_run(fixture: str = "all", files: int = 10, mode: str = "scan", passes: int = 1, label: str | None = None, compare_label: str | None = None, workers: int = 1, family: str = "all", scope: str = "synthetic", root_name: str | None = None, path: str | None = None, max_files: int | None = None, deployment_label: str | None = None, scenario: str = "standard", include_model_probe: bool = False)' in source
    assert "def benchmark_history(fixture: str | None = None, mode: str | None = None, label: str | None = None, warm_state: str | None = None, scope_type: str | None = None, deployment_label: str | None = None, scenario: str | None = None, scope_hash: str | None = None, freshness_hours: int | None = None, limit: int = 20)" in source
    assert "def indexer_reliability_status(root_name: str | None = None, path: str | None = None, label: str | None = None, deployment_label: str | None = None, compare_label: str | None = None, freshness_hours: int = 336, limit: int = 100)" in source
    assert "def indexer_reliability_run(scope: str = \"synthetic\", root_name: str | None = None, path: str | None = None, label: str | None = None, deployment_label: str | None = None, compare_label: str | None = None, max_files: int = 1000, passes: int = 2, include_cache_readiness: bool = False, include_tuning: bool = True, evidence_level: str = \"standard\")" in source
    assert "def operator_evidence(label: str | None = None, deployment_label: str | None = None, compare_label: str | None = None, freshness_hours: int = 336, limit: int = 100)" in source
    assert "def indexer_root_reliability(root_name: str)" in source
    assert "def indexer_reliability_roots(include_disabled: bool = False, freshness_hours: int = 336, limit: int = 100)" in source
    assert "def code_status(root_name: str | None = None)" in source
    assert "def code_search(query: str, root_name: str | None = None, language: str | None = None, symbol_kind: str | None = None, relationship: str | None = None, path_glob: str | None = None, include_generated: bool = False, limit: int = 20)" in source
    assert "def code_symbol_lookup(symbol: str, root_name: str | None = None, language: str | None = None, include_references: bool = True, limit: int = 20)" in source
    assert "def code_feedback_record(query: str, root_name: str | None = None, result_count: int = 0, surface: str = \"mcp\", miss_category: str = \"other\", expected_symbol: str | None = None, path: str | None = None)" in source
    assert "def code_feedback_summary(root_name: str | None = None, limit: int = 20)" in source
    assert "def operational_diagnostics(section: str = \"all\", limit: int = 25, root_name: str | None = None, status: str | None = None, family: str | None = None, since_hours: int | None = None, include_details: bool = False)" in source
    assert "def diagnostics_remediate(action: str, target_type: str, target_id: str | None = None, root_name: str | None = None, family: str | None = None, reason: str = \"operator diagnostic remediation\")" in source
    assert 'def retrieval_benchmark_run(suite: str = "standard", label: str | None = None, compare_label: str | None = None, limit_per_query: int = 5, token_budget: int | None = None, persist: bool = True)' in source
    assert 'def retrieval_benchmark_history(suite: str | None = None, label: str | None = None, limit: int = 20)' in source
    assert 'def governance_run(mode: str = "shadow", limit: int = 25)' in source
    assert 'def governance_actions(status: str = "proposed", limit: int = 50)' in source
    assert "def governance_apply(action_id: str, rationale: str, confirm: bool = False)" in source
    assert "def governance_recover(action_id: str, rationale: str, confirm: bool = False)" in source
    assert "def governance_digest()" in source
    assert "def governance_policy()" in source
    assert "confidence bands, calibration candidates, and metric deltas" in source
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
