from __future__ import annotations

import sys
from types import SimpleNamespace

from flux_llm_kb import model_runner


def test_download_models_retries_hf_snapshots_and_skips_embedding_onnx(tmp_path, monkeypatch):
    calls: list[dict[str, object]] = []
    qwen_attempts = 0

    def fake_snapshot_download(**kwargs):
        nonlocal qwen_attempts
        calls.append(kwargs)
        if kwargs["repo_id"] == model_runner.DEFAULT_RERANKER_MODEL:
            qwen_attempts += 1
            if qwen_attempts == 1:
                raise OSError("connection broken: incomplete read")
        return str(tmp_path / str(kwargs["repo_id"]).replace("/", "--"))

    monkeypatch.setitem(sys.modules, "huggingface_hub", SimpleNamespace(snapshot_download=fake_snapshot_download))
    monkeypatch.setattr(model_runner, "_load_paddleocr", lambda _model: object())
    monkeypatch.setenv("FLUX_KB_MODEL_RUNNER_DOWNLOAD_RETRIES", "3")
    monkeypatch.setenv("FLUX_KB_MODEL_RUNNER_DOWNLOAD_RETRY_SECONDS", "0")

    result = model_runner.download_models(str(tmp_path / "models"))

    assert result["ok"] is True
    assert [item["repo_id"] for item in result["downloads"]] == [
        model_runner.DEFAULT_EMBEDDING_MODEL,
        model_runner.DEFAULT_RERANKER_MODEL,
        "PaddlePaddle/PaddleOCR-VL",
        "paddleocr-runtime",
    ]
    assert [call["repo_id"] for call in calls].count(model_runner.DEFAULT_RERANKER_MODEL) == 2
    snowflake_call = calls[0]
    assert snowflake_call["repo_id"] == model_runner.DEFAULT_EMBEDDING_MODEL
    assert snowflake_call["max_workers"] == 2
    assert snowflake_call["ignore_patterns"] == ["onnx/*", "*.onnx"]
