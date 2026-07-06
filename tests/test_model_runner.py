from __future__ import annotations

import base64
from io import BytesIO
import threading
import sys
from types import ModuleType, SimpleNamespace
from urllib.error import HTTPError

import pytest

from flux_llm_kb import model_runner


def test_model_runner_client_timeout_allows_cold_model_start(monkeypatch):
    monkeypatch.delenv("FLUX_KB_MODEL_RUNNER_TIMEOUT_SECONDS", raising=False)
    assert model_runner.ModelRunnerClient().timeout_seconds == 600

    monkeypatch.setenv("FLUX_KB_MODEL_RUNNER_TIMEOUT_SECONDS", "900")
    assert model_runner.ModelRunnerClient().timeout_seconds == 900


def test_model_runner_http_503_string_detail_is_retryable_busy(monkeypatch):
    def fake_urlopen(*_args, **_kwargs):
        raise HTTPError(
            url="http://model-runner:8790/v1/rerank",
            code=503,
            msg="Service Unavailable",
            hdrs={},
            fp=BytesIO(b'{"detail":"HTTP Error 503: Service Unavailable"}'),
        )

    monkeypatch.setattr(model_runner, "urlopen", fake_urlopen)

    with pytest.raises(model_runner.ModelRunnerBusy) as exc_info:
        model_runner._post_json_to_base_url("http://model-runner:8790", "/v1/rerank", {"passages": []}, 1)

    assert exc_info.value.retry_after_seconds == 1.0


def test_model_runner_transport_timeout_is_retryable_busy(monkeypatch):
    def fake_urlopen(*_args, **_kwargs):
        raise TimeoutError("timed out")

    monkeypatch.setattr(model_runner, "urlopen", fake_urlopen)

    with pytest.raises(model_runner.ModelRunnerBusy) as exc_info:
        model_runner._post_json_to_base_url("http://model-runner:8790", "/v1/embeddings", {"texts": ["hello"]}, 60)

    assert exc_info.value.retry_after_seconds == 1.0


def test_model_runner_http_400_structured_ocr_input_error_is_terminal(monkeypatch):
    body = (
        b'{"detail":{"code":"ocr.invalid_image_input",'
        b'"message":"OCR image payload is not a readable image",'
        b'"retryable":false,'
        b'"metadata":{"suffix":".png","byte_count":10}}}'
    )

    def fake_urlopen(*_args, **_kwargs):
        raise HTTPError(
            url="http://model-runner:8790/v1/ocr/image",
            code=400,
            msg="Bad Request",
            hdrs={},
            fp=BytesIO(body),
        )

    monkeypatch.setattr(model_runner, "urlopen", fake_urlopen)

    with pytest.raises(model_runner.ModelRunnerError) as exc_info:
        model_runner._post_json_to_base_url("http://model-runner:8790", "/v1/ocr/image", {}, 1)

    assert exc_info.value.__class__.__name__ == "OcrInvalidInputError"
    assert "ocr.invalid_image_input" in str(exc_info.value)
    assert getattr(exc_info.value, "retryable", True) is False


def test_model_runner_client_rerank_uses_request_timeout_for_http_wait(monkeypatch):
    captured = {}

    class FakeResponse:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def read(self):
            return b'{"ok": true, "scores": [0.5]}'

    def fake_urlopen(request, timeout):
        captured["timeout"] = timeout
        captured["body"] = request.data
        return FakeResponse()

    monkeypatch.setattr(model_runner, "urlopen", fake_urlopen)
    client = model_runner.ModelRunnerClient("http://model-runner:8790", timeout_seconds=600)

    scores = client.rerank(
        "rank",
        ["passage"],
        model=model_runner.DEFAULT_RERANKER_MODEL,
        quantization=model_runner.DEFAULT_RERANKER_QUANTIZATION,
        timeout_seconds=3.5,
    )

    assert scores == [0.5]
    assert captured["timeout"] == 3.5
    assert b'"timeout_seconds": 3.5' in captured["body"]


def test_model_runner_client_uses_catalog_base_url_when_env_is_absent(monkeypatch):
    from flux_llm_kb import settings

    class FakeSettingsService:
        def resolve(self, key):
            assert key == "model_runner.base_url"
            return SimpleNamespace(raw_value="http://configured-model-runner:8790")

    monkeypatch.delenv("FLUX_KB_MODEL_RUNNER_BASE_URL", raising=False)
    monkeypatch.setattr(settings, "SettingsService", FakeSettingsService)

    assert model_runner.ModelRunnerClient().base_url == "http://configured-model-runner:8790"
    monkeypatch.setenv("FLUX_KB_MODEL_RUNNER_BASE_URL", "http://env-model-runner:8790")
    assert model_runner.ModelRunnerClient().base_url == "http://env-model-runner:8790"
    assert model_runner.ModelRunnerClient("http://explicit-model-runner:8790").base_url == "http://explicit-model-runner:8790"


def test_download_models_retries_hf_snapshots_and_skips_embedding_onnx(tmp_path, monkeypatch):
    calls: list[dict[str, object]] = []
    qwen_attempts = 0

    def fake_snapshot_download(**kwargs):
        nonlocal qwen_attempts
        calls.append(kwargs)
        if kwargs["repo_id"] == model_runner.DEFAULT_RERANKER_AWQ_MODEL:
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
        model_runner.DEFAULT_RERANKER_AWQ_MODEL,
    ]
    assert [call["repo_id"] for call in calls].count(model_runner.DEFAULT_RERANKER_AWQ_MODEL) == 2
    assert model_runner.DEFAULT_RERANKER_MODEL not in [call["repo_id"] for call in calls]
    snowflake_call = calls[0]
    assert snowflake_call["repo_id"] == model_runner.DEFAULT_EMBEDDING_MODEL
    assert snowflake_call["cache_dir"] == str(tmp_path / "models" / "huggingface" / "hub")
    assert snowflake_call["local_files_only"] is True
    assert snowflake_call["max_workers"] == 2
    assert snowflake_call["ignore_patterns"] == ["onnx/*", "*.onnx"]
    assert {call["cache_dir"] for call in calls} == {str(tmp_path / "models" / "huggingface" / "hub")}


def test_download_paddle_models_uses_persistent_hf_and_paddle_caches(tmp_path, monkeypatch):
    calls: list[dict[str, object]] = []

    def fake_snapshot_download(**kwargs):
        calls.append(kwargs)
        if kwargs.get("local_files_only") is True:
            return str(tmp_path / "cached-paddleocr-vl")
        raise AssertionError("warmup should have reused the existing PaddleOCR-VL HF cache")

    monkeypatch.setitem(sys.modules, "huggingface_hub", SimpleNamespace(snapshot_download=fake_snapshot_download))
    monkeypatch.setattr(model_runner, "_load_paddleocr", lambda _model: object())
    monkeypatch.setattr(model_runner, "_load_paddleocr_vl", lambda _model: object())
    monkeypatch.setenv("PADDLEOCR_HOME", "/root/.paddleocr")
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", "/root/.paddleocr/paddlex")

    result = model_runner.download_paddle_models(str(tmp_path / "models"))

    assert result["ok"] is True
    assert [item["repo_id"] for item in result["downloads"]] == [
        "PaddlePaddle/PaddleOCR-VL",
        "paddleocr-runtime",
        "paddleocr-vl-runtime",
    ]
    assert calls == [
        {
            "repo_id": "PaddlePaddle/PaddleOCR-VL",
            "cache_dir": str(tmp_path / "models" / "huggingface" / "hub"),
            "local_files_only": True,
            "max_workers": 2,
        }
    ]


def test_legacy_int4_awq_uses_awq_loader_not_bitsandbytes(monkeypatch):
    calls: list[dict[str, object]] = []

    class ExplodingBitsAndBytesConfig:
        def __init__(self, **_kwargs):
            raise AssertionError("AWQ must not use BitsAndBytesConfig")

    class FakeTokenizer:
        @classmethod
        def from_pretrained(cls, model, **kwargs):
            calls.append({"loader": "tokenizer", "model": model, "kwargs": kwargs})
            return cls()

        def encode(self, _text, add_special_tokens=False):
            return [1, 2]

        def convert_tokens_to_ids(self, token):
            return {"no": 10, "yes": 11}[token]

    class FakeModel:
        device = "cuda:0"

        @classmethod
        def from_pretrained(cls, model, **kwargs):
            calls.append({"loader": "model", "model": model, "kwargs": kwargs})
            return cls()

        def eval(self):
            calls.append({"loader": "eval"})
            return self

    monkeypatch.setitem(sys.modules, "compressed_tensors", SimpleNamespace())
    monkeypatch.setitem(sys.modules, "torch", SimpleNamespace(float16="float16"))
    monkeypatch.setitem(
        sys.modules,
        "transformers",
        SimpleNamespace(
            AutoTokenizer=FakeTokenizer,
            AutoModelForCausalLM=FakeModel,
            BitsAndBytesConfig=ExplodingBitsAndBytesConfig,
        ),
    )
    model_runner._RERANKER_MODELS.clear()

    reranker = model_runner._load_reranker_model(
        model_runner.DEFAULT_RERANKER_MODEL,
        "int4_awq",
    )

    assert reranker.profile.requested_quantization == "int4_awq"
    assert reranker.profile.quantization == "awq_int4"
    assert reranker.profile.backend == "compressed_tensors_awq"
    assert reranker.profile.load_model == model_runner.DEFAULT_RERANKER_AWQ_MODEL
    assert [call["model"] for call in calls if call.get("loader") in {"tokenizer", "model"}] == [
        model_runner.DEFAULT_RERANKER_AWQ_MODEL,
        model_runner.DEFAULT_RERANKER_AWQ_MODEL,
    ]


def test_qwen_reranker_uses_cross_encoder_with_nf4_quantization(monkeypatch):
    created: list[dict[str, object]] = []

    class FakeBitsAndBytesConfig:
        def __init__(self, **kwargs):
            self.kwargs = kwargs

    class FakeCrossEncoder:
        def __init__(self, model, **kwargs):
            created.append({"model": model, "kwargs": kwargs})

        def predict(self, pairs):
            self.pairs = list(pairs)
            return [8.0, -4.0]

    monkeypatch.setitem(sys.modules, "torch", SimpleNamespace(float16="float16"))
    monkeypatch.setitem(sys.modules, "transformers", SimpleNamespace(BitsAndBytesConfig=FakeBitsAndBytesConfig))
    monkeypatch.setitem(sys.modules, "sentence_transformers", SimpleNamespace(CrossEncoder=FakeCrossEncoder))
    monkeypatch.setenv("FLUX_KB_RETRIEVAL_MAX_RERANK_PASSAGE_TOKENS", "512")
    model_runner._RERANKER_MODELS.clear()

    scores = model_runner._rerank_with_transformers(
        "hybrid retrieval",
        ["Vespa combines BM25 and dense search.", "OCR extracts page text."],
        model=model_runner.DEFAULT_RERANKER_MODEL,
        quantization="nf4_4bit",
    )

    assert scores == [8.0, -4.0]
    assert created[0]["model"] == model_runner.DEFAULT_RERANKER_MODEL
    kwargs = created[0]["kwargs"]
    assert kwargs["trust_remote_code"] is True
    assert kwargs["max_length"] == 512
    model_kwargs = kwargs["model_kwargs"]
    assert model_kwargs["device_map"] == "auto"
    quantization_config = model_kwargs["quantization_config"]
    assert quantization_config.kwargs["load_in_4bit"] is True
    assert quantization_config.kwargs["bnb_4bit_compute_dtype"] == "float16"
    assert quantization_config.kwargs["bnb_4bit_quant_type"] == "nf4"


def test_qwen_reranker_fp16_uses_distinct_cross_encoder_backend(monkeypatch):
    created: list[dict[str, object]] = []

    class FakeCrossEncoder:
        def __init__(self, model, **kwargs):
            created.append({"model": model, "kwargs": kwargs})

        def predict(self, _pairs):
            return [3.0]

    monkeypatch.setitem(
        sys.modules,
        "torch",
        SimpleNamespace(float16="float16", cuda=SimpleNamespace(is_available=lambda: True)),
    )
    monkeypatch.setitem(sys.modules, "sentence_transformers", SimpleNamespace(CrossEncoder=FakeCrossEncoder))
    model_runner._RERANKER_MODELS.clear()

    scores = model_runner._rerank_with_transformers(
        "hybrid retrieval",
        ["Vespa combines BM25 and dense search."],
        model=model_runner.DEFAULT_RERANKER_MODEL,
        quantization="fp16",
    )

    assert scores == [3.0]
    model_kwargs = created[0]["kwargs"]["model_kwargs"]
    assert model_kwargs["device_map"] == "auto"
    assert model_kwargs["torch_dtype"] == "float16"
    assert "quantization_config" not in model_kwargs


def test_paddleocr_loader_disables_optional_preprocessors(monkeypatch):
    created: list[dict[str, object]] = []

    class FakePaddleOCR:
        def __init__(self, **kwargs):
            created.append(kwargs)

    monkeypatch.setitem(sys.modules, "paddleocr", SimpleNamespace(PaddleOCR=FakePaddleOCR))
    monkeypatch.setitem(
        sys.modules,
        "paddle",
        SimpleNamespace(
            device=SimpleNamespace(
                is_compiled_with_cuda=lambda: True,
                cuda=SimpleNamespace(device_count=lambda: 1),
            )
        ),
    )
    model_runner._PADDLE_OCR_MODELS.clear()

    model_runner._load_paddleocr("PP-OCRv5")

    assert created == [
        {
            "lang": "en",
            "ocr_version": "PP-OCRv5",
            "use_doc_orientation_classify": False,
            "use_doc_unwarping": False,
            "use_textline_orientation": False,
            "enable_mkldnn": False,
            "device": "gpu:0",
        }
    ]


def test_paddleocr_loader_configures_onnxruntime_before_importing_paddleocr(monkeypatch):
    events: list[str] = []

    class FakePaddleOCR:
        def __init__(self, **_kwargs):
            events.append("paddleocr-init")

    class FakePaddleOCRModule(ModuleType):
        def __getattr__(self, name):
            if name == "PaddleOCR":
                events.append("import-paddleocr")
                return FakePaddleOCR
            raise AttributeError(name)

    monkeypatch.setattr(model_runner, "configure_onnxruntime_logging", lambda: events.append("configure-ort"), raising=False)
    monkeypatch.setitem(sys.modules, "paddleocr", FakePaddleOCRModule("paddleocr"))
    monkeypatch.setitem(
        sys.modules,
        "paddle",
        SimpleNamespace(
            device=SimpleNamespace(
                is_compiled_with_cuda=lambda: False,
                cuda=SimpleNamespace(device_count=lambda: 0),
            )
        ),
    )
    model_runner._PADDLE_OCR_MODELS.clear()

    model_runner._load_paddleocr("PP-OCRv5")

    assert events[:2] == ["configure-ort", "import-paddleocr"]


def test_paddlex_model_source_defaults_to_bos():
    assert model_runner.os.environ["PADDLE_PDX_MODEL_SOURCE"] == "bos"


def test_paddleocr_text_extracts_rec_texts_from_v3_results():
    result = [
        {
            "rec_texts": ["OCR SMOKE 123"],
            "rec_scores": [0.98],
        }
    ]

    assert model_runner._paddleocr_text(result) == "OCR SMOKE 123"


def test_paddleocr_text_extracts_paddlex_vl_content():
    result = [
        {
            "parsing_res_list": [
                {
                    "label": "paragraph_title",
                    "content": "PaddleOCR-VL text",
                }
            ]
        }
    ]

    assert model_runner._paddleocr_text(result) == "PaddleOCR-VL text"


def test_paddleocr_vl_document_uses_paddlex_pipeline(monkeypatch):
    created: list[dict[str, object]] = []

    class FakePipeline:
        def predict(self, path):
            assert path == "/tmp/doc.png"
            return [{"parsing_res_list": [{"content": "VL document text"}]}]

    def fake_create_pipeline(**kwargs):
        created.append(kwargs)
        return FakePipeline()

    monkeypatch.setitem(sys.modules, "paddlex", SimpleNamespace(create_pipeline=fake_create_pipeline))
    monkeypatch.setitem(
        sys.modules,
        "paddle",
        SimpleNamespace(
            device=SimpleNamespace(
                is_compiled_with_cuda=lambda: False,
                cuda=SimpleNamespace(device_count=lambda: 0),
            )
        ),
    )
    model_runner._PADDLE_OCR_VL_MODELS.clear()

    text = model_runner._ocr_document_with_paddle("/tmp/doc.png", model="PaddleOCR-VL")

    assert text == "VL document text"
    assert created == [
        {
            "pipeline": "PaddleOCR-VL",
            "device": "cpu",
            "use_doc_orientation_classify": False,
            "use_doc_unwarping": False,
        }
    ]


def test_paddleocr_vl_loader_configures_onnxruntime_before_importing_paddlex(monkeypatch):
    events: list[str] = []

    class FakePipeline:
        pass

    def fake_create_pipeline(**_kwargs):
        events.append("create-pipeline")
        return FakePipeline()

    class FakePaddlexModule(ModuleType):
        def __getattr__(self, name):
            if name == "create_pipeline":
                events.append("import-paddlex")
                return fake_create_pipeline
            raise AttributeError(name)

    monkeypatch.setattr(model_runner, "configure_onnxruntime_logging", lambda: events.append("configure-ort"), raising=False)
    monkeypatch.setitem(sys.modules, "paddlex", FakePaddlexModule("paddlex"))
    monkeypatch.setitem(
        sys.modules,
        "paddle",
        SimpleNamespace(
            device=SimpleNamespace(
                is_compiled_with_cuda=lambda: False,
                cuda=SimpleNamespace(device_count=lambda: 0),
            )
        ),
    )
    model_runner._PADDLE_OCR_VL_MODELS.clear()

    model_runner._load_paddleocr_vl("PaddleOCR-VL")

    assert events[:2] == ["configure-ort", "import-paddlex"]


def test_paddleocr_loader_sets_paddle_runtime_device_before_constructor(monkeypatch):
    events: list[tuple[str, str]] = []

    class FakePaddleOCR:
        def __init__(self, **kwargs):
            events.append(("construct", str(kwargs["device"])))

    def set_device(device):
        events.append(("set_device", str(device)))

    monkeypatch.setitem(sys.modules, "paddleocr", SimpleNamespace(PaddleOCR=FakePaddleOCR))
    monkeypatch.setitem(
        sys.modules,
        "paddle",
        SimpleNamespace(
            device=SimpleNamespace(
                is_compiled_with_cuda=lambda: True,
                cuda=SimpleNamespace(device_count=lambda: 1),
                set_device=set_device,
            )
        ),
    )
    model_runner._PADDLE_OCR_MODELS.clear()
    model_runner._PADDLE_OCR_FACTORY_IDS.clear()

    model_runner._load_paddleocr("PP-OCRv5")

    assert events[:2] == [("set_device", "gpu:0"), ("construct", "gpu:0")]


def test_paddleocr_vl_loader_sets_paddle_runtime_device_before_create_pipeline(monkeypatch):
    events: list[tuple[str, str]] = []

    class FakePipeline:
        pass

    def fake_create_pipeline(**kwargs):
        events.append(("create_pipeline", str(kwargs["device"])))
        return FakePipeline()

    def set_device(device):
        events.append(("set_device", str(device)))

    monkeypatch.setitem(sys.modules, "paddlex", SimpleNamespace(create_pipeline=fake_create_pipeline))
    monkeypatch.setitem(
        sys.modules,
        "paddle",
        SimpleNamespace(
            device=SimpleNamespace(
                is_compiled_with_cuda=lambda: True,
                cuda=SimpleNamespace(device_count=lambda: 1),
                set_device=set_device,
            )
        ),
    )
    model_runner._PADDLE_OCR_VL_MODELS.clear()
    model_runner._PADDLE_OCR_VL_FACTORY_IDS.clear()

    model_runner._load_paddleocr_vl("PaddleOCR-VL")

    assert events[:2] == [("set_device", "gpu:0"), ("create_pipeline", "gpu:0")]


def test_paddleocr_vl_document_uses_gpu_when_paddle_cuda_is_available(monkeypatch):
    created: list[dict[str, object]] = []

    class FakePipeline:
        def predict(self, _path):
            return [{"parsing_res_list": [{"content": "VL document text"}]}]

    def fake_create_pipeline(**kwargs):
        created.append(kwargs)
        return FakePipeline()

    monkeypatch.setitem(sys.modules, "paddlex", SimpleNamespace(create_pipeline=fake_create_pipeline))
    monkeypatch.setitem(
        sys.modules,
        "paddle",
        SimpleNamespace(
            device=SimpleNamespace(
                is_compiled_with_cuda=lambda: True,
                cuda=SimpleNamespace(device_count=lambda: 1),
            )
        ),
    )
    model_runner._PADDLE_OCR_VL_MODELS.clear()

    text = model_runner._ocr_document_with_paddle("/tmp/doc.png", model="PaddleOCR-VL")

    assert text == "VL document text"
    assert created[0]["device"] == "gpu:0"


def test_model_runner_health_reports_paddle_cuda_status(monkeypatch):
    monkeypatch.setitem(
        sys.modules,
        "torch",
        SimpleNamespace(cuda=SimpleNamespace(is_available=lambda: True)),
    )
    monkeypatch.setitem(
        sys.modules,
        "paddle",
        SimpleNamespace(
            device=SimpleNamespace(
                is_compiled_with_cuda=lambda: True,
                cuda=SimpleNamespace(device_count=lambda: 1),
            )
        ),
    )

    payload = model_runner.health_payload()

    assert payload["cuda_available"] is True
    assert payload["paddle_cuda_available"] is True
    assert payload["paddle_device"] == "gpu:0"
    assert payload["reranker_quantization"] == "awq_int4"
    assert payload["reranker_requested_quantization"] == "awq_int4"
    assert payload["reranker_quantization_backend"] == "compressed_tensors_awq"
    assert payload["reranker_model"] == model_runner.DEFAULT_RERANKER_MODEL
    assert payload["reranker_load_model"] == model_runner.DEFAULT_RERANKER_AWQ_MODEL
    assert payload["reranker_awq_model"] == model_runner.DEFAULT_RERANKER_AWQ_MODEL


def test_model_runner_app_start_clears_stale_component_residency(monkeypatch):
    events: list[str] = []

    class FakeScheduler:
        def reset_component_residency(self, component):
            events.append(component)

        def status(self):
            return {"enabled": True, "mode": "test"}

    monkeypatch.setenv("FLUX_KB_MODEL_RUNNER_ROLE", "model-runner")
    monkeypatch.setattr(model_runner, "get_gpu_scheduler", lambda: FakeScheduler())

    model_runner.create_app()

    assert events == ["model-runner"]


def test_paddle_runner_app_start_clears_stale_component_residency(monkeypatch):
    events: list[str] = []

    class FakeScheduler:
        def reset_component_residency(self, component):
            events.append(component)

        def status(self):
            return {"enabled": True, "mode": "test"}

    monkeypatch.setenv("FLUX_KB_MODEL_RUNNER_ROLE", "paddle-runner")
    monkeypatch.setattr(model_runner, "get_gpu_scheduler", lambda: FakeScheduler())

    model_runner.create_app()

    assert events == ["paddle-runner"]


def test_rerank_endpoint_reports_canonical_quantization_metadata(monkeypatch):
    app = model_runner.create_app()
    from fastapi.testclient import TestClient

    calls: list[dict[str, object]] = []

    def fake_rerank(*_args, **kwargs):
        calls.append(kwargs)
        return [0.25]

    monkeypatch.setattr(model_runner, "_rerank_with_transformers", fake_rerank)

    response = TestClient(app).post(
        "/v1/rerank",
        json={
            "model": model_runner.DEFAULT_RERANKER_MODEL,
            "quantization": "awq",
            "awq_model": "example/Qwen3-Reranker-4B-AWQ",
            "query": "hybrid retrieval",
            "passages": ["Vespa combines BM25 and dense search."],
        },
    )

    payload = response.json()

    assert payload["ok"] is True
    assert payload["scores"] == [0.25]
    assert payload["model"] == model_runner.DEFAULT_RERANKER_MODEL
    assert payload["quantization"] == "awq_int4"
    assert payload["requested_quantization"] == "awq"
    assert payload["quantization_backend"] == "compressed_tensors_awq"
    assert payload["load_model"] == "example/Qwen3-Reranker-4B-AWQ"
    assert payload["reranker_awq_model"] == "example/Qwen3-Reranker-4B-AWQ"
    assert payload["reranker_load_model"] == "example/Qwen3-Reranker-4B-AWQ"
    assert payload["reranker_quantization"] == "awq_int4"
    assert payload["reranker_requested_quantization"] == "awq"
    assert payload["reranker_quantization_backend"] == "compressed_tensors_awq"
    assert calls[0]["awq_model"] == "example/Qwen3-Reranker-4B-AWQ"


def test_model_runner_proxies_ocr_to_paddle_runner(monkeypatch):
    calls: list[dict[str, object]] = []

    def fake_post_json(base_url, path, payload, timeout_seconds):
        calls.append(
            {
                "base_url": base_url,
                "path": path,
                "payload": payload,
                "timeout_seconds": timeout_seconds,
            }
        )
        return {"ok": True, "model": payload["model"], "text": "remote OCR"}

    monkeypatch.setenv("FLUX_KB_PADDLE_RUNNER_BASE_URL", "http://paddle-runner:8791")
    monkeypatch.setattr(model_runner, "_post_json_to_base_url", fake_post_json)

    result = model_runner._ocr_image_with_paddle("/tmp/unshared.png", model="PP-OCRv5")

    assert result == "remote OCR"
    assert calls == [
        {
            "base_url": "http://paddle-runner:8791",
            "path": "/v1/ocr/image",
            "payload": {"path": "/tmp/unshared.png", "model": "PP-OCRv5"},
            "timeout_seconds": 600,
        }
    ]


def test_ocr_endpoint_accepts_inline_file_content(tmp_path):
    payload = {
        "filename": "page.png",
        "content_b64": base64.b64encode(b"image-bytes").decode("ascii"),
    }
    captured: dict[str, object] = {}

    def fake_consumer(path):
        captured["path"] = path
        return "OCR text"

    text = model_runner._with_ocr_input_path(payload, fake_consumer)

    assert text == "OCR text"
    input_path = captured["path"]
    assert input_path.name.endswith(".png")
    assert not input_path.exists()


@pytest.mark.parametrize("content_b64", ["", "not-valid-base64!!"])
def test_ocr_image_endpoint_rejects_empty_or_malformed_inline_content(monkeypatch, content_b64):
    from fastapi.testclient import TestClient

    def fail_ocr(*_args, **_kwargs):
        raise AssertionError("invalid inline image content must not reach PaddleOCR")

    monkeypatch.delenv("FLUX_KB_PADDLE_RUNNER_BASE_URL", raising=False)
    monkeypatch.setattr(model_runner, "_ocr_image_with_paddle", fail_ocr)

    response = TestClient(model_runner.create_app(), raise_server_exceptions=False).post(
        "/v1/ocr/image",
        json={"filename": "page.png", "content_b64": content_b64, "model": "PP-OCRv5"},
    )

    assert response.status_code == 400
    detail = response.json()["detail"]
    assert detail["code"] == "ocr.invalid_image_input"
    assert detail["retryable"] is False
    assert detail["metadata"]["suffix"] == ".png"


def test_ocr_image_endpoint_rejects_fake_png_bytes_before_paddle(monkeypatch):
    from fastapi.testclient import TestClient

    def fail_ocr(*_args, **_kwargs):
        raise AssertionError("undecodable image bytes must not reach PaddleOCR")

    monkeypatch.delenv("FLUX_KB_PADDLE_RUNNER_BASE_URL", raising=False)
    monkeypatch.setattr(model_runner, "_ocr_image_with_paddle", fail_ocr)

    response = TestClient(model_runner.create_app(), raise_server_exceptions=False).post(
        "/v1/ocr/image",
        json={
            "filename": "page.png",
            "content_b64": base64.b64encode(b"not really a png").decode("ascii"),
            "model": "PP-OCRv5",
        },
    )

    assert response.status_code == 400
    detail = response.json()["detail"]
    assert detail["code"] == "ocr.invalid_image_input"
    assert detail["message"] == "OCR image payload is not a readable image"
    assert detail["metadata"] == {"suffix": ".png", "byte_count": 16}


def test_ocr_image_endpoint_allows_valid_inline_png_to_reach_paddle(monkeypatch):
    from pathlib import Path

    from fastapi.testclient import TestClient
    from PIL import Image

    buffer = BytesIO()
    Image.new("RGB", (1, 1), "white").save(buffer, format="PNG")
    png_bytes = buffer.getvalue()
    calls: list[str] = []

    def fake_ocr(path, **kwargs):
        input_path = Path(path)
        assert input_path.exists()
        assert input_path.suffix == ".png"
        calls.append(str(kwargs["model"]))
        return "OCR text"

    monkeypatch.delenv("FLUX_KB_PADDLE_RUNNER_BASE_URL", raising=False)
    monkeypatch.setattr(model_runner, "_ocr_image_with_paddle", fake_ocr)

    response = TestClient(model_runner.create_app()).post(
        "/v1/ocr/image",
        json={
            "filename": "page.png",
            "content_b64": base64.b64encode(png_bytes).decode("ascii"),
            "model": "PP-OCRv5",
        },
    )

    assert response.status_code == 200
    assert response.json()["text"] == "OCR text"
    assert calls == ["PP-OCRv5"]


def test_model_runner_client_can_send_ocr_file_bytes(tmp_path, monkeypatch):
    image = tmp_path / "page.png"
    image.write_bytes(b"image-bytes")
    calls: list[dict[str, object]] = []

    class FakeClient(model_runner.ModelRunnerClient):
        def _post_json(self, path, payload):
            calls.append({"path": path, "payload": payload})
            return {"ok": True, "text": "OCR text"}

    client = FakeClient(base_url="http://model-runner:8790")

    result = client.ocr_file(image, model="PaddleOCR-VL", document=True)

    assert result["text"] == "OCR text"
    assert calls[0]["path"] == "/v1/ocr/document"
    payload = calls[0]["payload"]
    assert payload["model"] == "PaddleOCR-VL"
    assert payload["filename"] == "page.png"
    assert base64.b64decode(payload["content_b64"]) == b"image-bytes"


def test_model_runner_client_forwards_ocr_timeout(tmp_path):
    image = tmp_path / "page.png"
    image.write_bytes(b"image-bytes")
    calls: list[dict[str, object]] = []

    class FakeClient(model_runner.ModelRunnerClient):
        def _post_json(self, path, payload, **kwargs):
            calls.append({"path": path, "payload": payload, "kwargs": kwargs})
            return {"ok": True, "text": "OCR text"}

    client = FakeClient(base_url="http://model-runner:8790")

    result = client.ocr_file(image, model="PP-OCRv5", timeout_seconds=0.5)

    assert result["text"] == "OCR text"
    assert calls[0]["payload"]["timeout_seconds"] == 0.5
    assert calls[0]["kwargs"] == {"timeout_seconds": 0.5}


def test_model_runner_client_forwards_awq_model_for_reranking():
    calls: list[dict[str, object]] = []

    class FakeClient(model_runner.ModelRunnerClient):
        def _post_json(self, path, payload, **_kwargs):
            calls.append({"path": path, "payload": payload})
            return {"ok": True, "scores": [0.9]}

    client = FakeClient(base_url="http://model-runner:8790")

    scores = client.rerank(
        "hybrid retrieval",
        ["Vespa combines BM25 and dense search."],
        model=model_runner.DEFAULT_RERANKER_MODEL,
        quantization="awq_int4",
        awq_model="example/Qwen3-Reranker-4B-AWQ",
    )

    assert scores == [0.9]
    assert calls[0]["path"] == "/v1/rerank"
    payload = calls[0]["payload"]
    assert payload["model"] == model_runner.DEFAULT_RERANKER_MODEL
    assert payload["quantization"] == "awq_int4"
    assert payload["awq_model"] == "example/Qwen3-Reranker-4B-AWQ"


def test_model_runner_client_forwards_embedding_scheduler_timeout():
    calls: list[dict[str, object]] = []

    class FakeClient(model_runner.ModelRunnerClient):
        def _post_json(self, path, payload):
            calls.append({"path": path, "payload": payload})
            return {"ok": True, "vectors": [[0.1, 0.2]]}

    client = FakeClient(base_url="http://model-runner:8790")

    vectors = client.embed(["query"], model="Snowflake/test", dimensions=2, timeout_seconds=5)

    assert vectors == [[0.1, 0.2]]
    assert calls == [
        {
            "path": "/v1/embeddings",
            "payload": {"model": "Snowflake/test", "dimensions": 2, "texts": ["query"], "timeout_seconds": 5.0},
        }
    ]


def test_model_runner_client_forwards_rerank_scheduler_timeout():
    calls: list[dict[str, object]] = []

    class FakeClient(model_runner.ModelRunnerClient):
        def _post_json(self, path, payload, **kwargs):
            calls.append({"path": path, "payload": payload, "timeout_seconds": kwargs.get("timeout_seconds")})
            return {"ok": True, "scores": [0.9]}

    client = FakeClient(base_url="http://model-runner:8790")

    scores = client.rerank(
        "hybrid retrieval",
        ["Vespa combines BM25 and dense search."],
        model=model_runner.DEFAULT_RERANKER_MODEL,
        quantization="awq_int4",
        awq_model="example/Qwen3-Reranker-4B-AWQ",
        timeout_seconds=5,
    )

    assert scores == [0.9]
    assert calls[0]["payload"]["timeout_seconds"] == 5.0
    assert calls[0]["timeout_seconds"] == 5


def test_model_runner_client_records_safe_embedding_activity(monkeypatch):
    records: list[dict[str, object]] = []

    class FakeRecorder:
        def __init__(self, **kwargs):
            records.append(kwargs)

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    monkeypatch.setattr(model_runner, "record_model_activity", lambda **kwargs: FakeRecorder(**kwargs), raising=False)
    monkeypatch.setattr(
        model_runner,
        "_post_json_to_base_url",
        lambda *_args, **_kwargs: {"ok": True, "vectors": [[0.1, 0.2]]},
    )

    vectors = model_runner.ModelRunnerClient("http://model-runner:8790").embed(
        ["private prompt text"],
        model="Snowflake/test",
        dimensions=2,
    )

    assert vectors == [[0.1, 0.2]]
    assert records == [
        {
            "service": "model-runner",
            "endpoint": "/v1/embeddings",
            "action": "embedding",
            "activity_class": "retrieval",
            "model": "Snowflake/test",
            "metadata": {"input_count": 1, "dimensions": 2},
        }
    ]
    assert "private prompt text" not in str(records)


def test_model_runner_client_records_safe_rerank_activity(monkeypatch):
    records: list[dict[str, object]] = []

    class FakeRecorder:
        def __init__(self, **kwargs):
            records.append(kwargs)

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    monkeypatch.setattr(model_runner, "record_model_activity", lambda **kwargs: FakeRecorder(**kwargs), raising=False)
    monkeypatch.setattr(
        model_runner,
        "_post_json_to_base_url",
        lambda *_args, **_kwargs: {"ok": True, "scores": [0.9]},
    )

    scores = model_runner.ModelRunnerClient("http://model-runner:8790").rerank(
        "private query",
        ["private passage"],
        model="Qwen/test",
        quantization="awq_int4",
    )

    assert scores == [0.9]
    assert records == [
        {
            "service": "model-runner",
            "endpoint": "/v1/rerank",
            "action": "rerank",
            "activity_class": "retrieval",
            "model": "Qwen/test",
            "metadata": {"passage_count": 1, "quantization": "awq_int4"},
        }
    ]
    assert "private query" not in str(records)
    assert "private passage" not in str(records)


def test_model_runner_client_records_safe_ocr_activity(monkeypatch, tmp_path):
    records: list[dict[str, object]] = []
    image = tmp_path / "scan.png"
    image.write_bytes(b"private image bytes")

    class FakeRecorder:
        def __init__(self, **kwargs):
            records.append(kwargs)

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    monkeypatch.setattr(model_runner, "record_model_activity", lambda **kwargs: FakeRecorder(**kwargs), raising=False)
    monkeypatch.setattr(
        model_runner,
        "_post_json_to_base_url",
        lambda *_args, **_kwargs: {"ok": True, "text": "remote OCR"},
    )

    payload = model_runner.ModelRunnerClient("http://model-runner:8790").ocr_file(
        image,
        model="PaddleOCR-VL",
        document=True,
    )

    assert payload["text"] == "remote OCR"
    assert records == [
        {
            "service": "model-runner",
            "endpoint": "/v1/ocr/document",
            "action": "ocr_document",
            "activity_class": "vision_ocr",
            "model": "PaddleOCR-VL",
            "metadata": {"document": True},
        }
    ]
    assert "private image bytes" not in str(records)
    assert str(image) not in str(records)


def test_paddle_proxy_records_safe_ocr_activity(monkeypatch):
    records: list[dict[str, object]] = []

    class FakeRecorder:
        def __init__(self, **kwargs):
            records.append(kwargs)

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    def fake_post_json(base_url, path, payload, timeout_seconds):
        return {"ok": True, "model": payload["model"], "text": "remote OCR"}

    monkeypatch.setenv("FLUX_KB_PADDLE_RUNNER_BASE_URL", "http://paddle-runner:8791")
    monkeypatch.setattr(model_runner, "record_model_activity", lambda **kwargs: FakeRecorder(**kwargs), raising=False)
    monkeypatch.setattr(model_runner, "_post_json_to_base_url", fake_post_json)

    result = model_runner._proxy_paddle_request(
        "/v1/ocr/image",
        {"path": "E:/Private/report.pdf", "content_b64": "private-bytes", "model": "PP-OCRv5"},
    )

    assert result["text"] == "remote OCR"
    assert records == [
        {
            "service": "paddle-runner",
            "endpoint": "/v1/ocr/image",
            "action": "ocr_image",
            "activity_class": "vision_ocr",
            "model": "PP-OCRv5",
            "metadata": {"document": False},
        }
    ]
    assert "E:/Private" not in str(records)
    assert "private-bytes" not in str(records)


def test_direct_local_paddleocr_image_records_safe_activity(monkeypatch):
    records: list[dict[str, object]] = []

    class FakeRecorder:
        def __init__(self, **kwargs):
            records.append(kwargs)

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    class FakeLease:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    class FakeScheduler:
        def acquire(self, profile):
            assert profile.task_type == "ocr_image"
            assert profile.model_id == "PP-OCRv5"
            return FakeLease()

        def record_model_residency(self, _residency):
            return None

    class FakePaddleOCR:
        def predict(self, path):
            assert path == "/tmp/private-image.png"
            return [{"rec_text": "direct OCR"}]

    monkeypatch.delenv("FLUX_KB_PADDLE_RUNNER_BASE_URL", raising=False)
    monkeypatch.delenv("FLUX_KB_MODEL_RUNNER_ROLE", raising=False)
    monkeypatch.setattr(model_runner, "record_model_activity", lambda **kwargs: FakeRecorder(**kwargs), raising=False)
    monkeypatch.setattr(model_runner, "get_gpu_scheduler", lambda: FakeScheduler())
    monkeypatch.setattr(model_runner, "_load_paddleocr", lambda _model: FakePaddleOCR())

    text = model_runner._ocr_image_with_paddle("/tmp/private-image.png", model="PP-OCRv5")

    assert text == "direct OCR"
    assert records == [
        {
            "service": "model-runner",
            "endpoint": "/v1/ocr/image",
            "action": "ocr_image",
            "activity_class": "vision_ocr",
            "model": "PP-OCRv5",
            "metadata": {"document": False},
        }
    ]
    assert "private-image" not in str(records)


def test_direct_local_paddleocr_vl_document_records_safe_activity(monkeypatch):
    records: list[dict[str, object]] = []

    class FakeRecorder:
        def __init__(self, **kwargs):
            records.append(kwargs)

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    class FakeLease:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    class FakeScheduler:
        def acquire(self, profile):
            assert profile.task_type == "ocr_document"
            assert profile.model_id == "PaddleOCR-VL"
            return FakeLease()

        def record_model_residency(self, _residency):
            return None

    class FakePipeline:
        def predict(self, path):
            assert path == "/tmp/private-document.png"
            return [{"res": {"content": "document OCR"}}]

    monkeypatch.delenv("FLUX_KB_PADDLE_RUNNER_BASE_URL", raising=False)
    monkeypatch.delenv("FLUX_KB_MODEL_RUNNER_ROLE", raising=False)
    monkeypatch.setattr(model_runner, "record_model_activity", lambda **kwargs: FakeRecorder(**kwargs), raising=False)
    monkeypatch.setattr(model_runner, "get_gpu_scheduler", lambda: FakeScheduler())
    monkeypatch.setattr(model_runner, "_load_paddleocr_vl", lambda _model: FakePipeline())

    text = model_runner._ocr_document_with_paddle("/tmp/private-document.png", model="PaddleOCR-VL")

    assert text == "document OCR"
    assert records == [
        {
            "service": "model-runner",
            "endpoint": "/v1/ocr/document",
            "action": "ocr_document",
            "activity_class": "vision_ocr",
            "model": "PaddleOCR-VL",
            "metadata": {"document": True},
        }
    ]
    assert "private-document" not in str(records)


def test_paddle_runner_health_records_safe_activity(monkeypatch):
    records: list[dict[str, object]] = []

    class FakeRecorder:
        def __init__(self, **kwargs):
            records.append(kwargs)

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    class FakeResponse:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

        def read(self):
            return b'{"ok": true, "paddle_device": "gpu:0"}'

    monkeypatch.setenv("FLUX_KB_PADDLE_RUNNER_BASE_URL", "http://paddle-runner:8791")
    monkeypatch.setattr(model_runner, "record_model_activity", lambda **kwargs: FakeRecorder(**kwargs), raising=False)
    monkeypatch.setattr(model_runner, "urlopen", lambda *_args, **_kwargs: FakeResponse())

    payload = model_runner._paddle_runner_health()

    assert payload == {"ok": True, "paddle_device": "gpu:0"}
    assert records == [
        {
            "service": "paddle-runner",
            "endpoint": "/health",
            "action": "health",
            "activity_class": "control_plane",
        }
    ]


def test_model_runner_health_does_not_probe_paddle_runner(monkeypatch):
    calls: list[str] = []

    def fail_paddle_health():
        calls.append("paddle")
        raise AssertionError("basic model-runner health must not fan out to paddle-runner")

    monkeypatch.setenv("FLUX_KB_PADDLE_RUNNER_BASE_URL", "http://paddle-runner:8791")
    monkeypatch.setattr(model_runner, "_paddle_runner_health", fail_paddle_health)
    monkeypatch.setattr(model_runner, "_paddle_cuda_status", lambda: (True, 1))
    monkeypatch.setattr(model_runner, "_paddle_device", lambda: "gpu:0")

    payload = model_runner.health_payload(role="model-runner")

    assert payload["ok"] is True
    assert "paddle_runner" not in payload
    assert calls == []


def test_model_runner_livez_does_not_run_deep_health_probes(monkeypatch):
    from fastapi.testclient import TestClient

    class FakeScheduler:
        def reset_component_residency(self, _component):
            return None

        def status(self):
            raise AssertionError("/livez must not query GPU scheduler status")

    monkeypatch.setattr(model_runner, "get_gpu_scheduler", lambda: FakeScheduler())
    monkeypatch.setattr(
        model_runner,
        "_paddle_cuda_status",
        lambda: (_ for _ in ()).throw(AssertionError("/livez must not import/probe Paddle")),
    )
    monkeypatch.setattr(
        model_runner,
        "_paddle_device",
        lambda: (_ for _ in ()).throw(AssertionError("/livez must not resolve Paddle device")),
    )

    response = TestClient(model_runner.create_app()).get("/livez")

    assert response.status_code == 200
    assert response.json() == {"ok": True, "service": "model-runner"}


def test_model_runner_readiness_reports_paddle_runner_status(monkeypatch):
    from fastapi.testclient import TestClient

    calls: list[str] = []

    def fake_paddle_health():
        calls.append("paddle")
        return {"ok": True, "service": "paddle-runner", "paddle_device": "gpu:0"}

    monkeypatch.setenv("FLUX_KB_PADDLE_RUNNER_BASE_URL", "http://paddle-runner:8791")
    monkeypatch.setattr(model_runner, "_paddle_runner_health", fake_paddle_health)
    monkeypatch.setattr(model_runner, "_paddle_cuda_status", lambda: (True, 1))
    monkeypatch.setattr(model_runner, "_paddle_device", lambda: "gpu:0")

    response = TestClient(model_runner.create_app()).get("/readiness")

    assert response.status_code == 200
    payload = response.json()
    assert payload["ok"] is True
    assert payload["service"] == "model-runner"
    assert payload["dependencies"]["paddle_runner"]["ok"] is True
    assert payload["dependencies"]["paddle_runner"]["paddle_device"] == "gpu:0"
    assert calls == ["paddle"]


def test_model_runner_client_health_records_safe_activity(monkeypatch):
    records: list[dict[str, object]] = []

    class FakeRecorder:
        def __init__(self, **kwargs):
            records.append(kwargs)

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    class FakeResponse:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

        def read(self):
            return b'{"ok": true}'

    monkeypatch.setattr(model_runner, "record_model_activity", lambda **kwargs: FakeRecorder(**kwargs), raising=False)
    monkeypatch.setattr(model_runner, "urlopen", lambda *_args, **_kwargs: FakeResponse())

    payload = model_runner.ModelRunnerClient("http://model-runner:8790").health()

    assert payload == {"ok": True}
    assert records == [
        {
            "service": "model-runner",
            "endpoint": "/health",
            "action": "health",
            "activity_class": "control_plane",
        }
    ]


def test_embeddings_endpoint_accepts_openai_input_and_rejects_invalid_payload(monkeypatch):
    from fastapi.testclient import TestClient

    calls: list[list[str]] = []

    def fake_embed(texts, *, model, dimensions, timeout_seconds=None):
        assert timeout_seconds is None
        calls.append(list(texts))
        return [[0.1, 0.2] for _text in texts]

    monkeypatch.setattr(model_runner, "_embed_with_sentence_transformers", fake_embed)
    client = TestClient(model_runner.create_app())

    string_response = client.post("/v1/embeddings", json={"input": "hello", "dimensions": 2})
    list_response = client.post("/v1/embeddings", json={"input": ["one", "two"], "dimensions": 2})
    invalid_response = client.post("/v1/embeddings", json={"input": {"not": "valid"}, "dimensions": 2})
    missing_response = client.post("/v1/embeddings", json={"model": "Snowflake/test"})

    assert string_response.status_code == 200
    assert string_response.json()["vectors"] == [[0.1, 0.2]]
    assert list_response.status_code == 200
    assert list_response.json()["vectors"] == [[0.1, 0.2], [0.1, 0.2]]
    assert invalid_response.status_code == 400
    assert missing_response.status_code == 400
    assert calls == [["hello"], ["one", "two"]]


def test_embeddings_endpoint_forwards_scheduler_timeout(monkeypatch):
    from fastapi.testclient import TestClient

    calls: list[dict[str, object]] = []

    def fake_embed(texts, *, model, dimensions, timeout_seconds=None):
        calls.append(
            {
                "texts": list(texts),
                "model": model,
                "dimensions": dimensions,
                "timeout_seconds": timeout_seconds,
            }
        )
        return [[0.1, 0.2]]

    monkeypatch.setattr(model_runner, "_embed_with_sentence_transformers", fake_embed)

    response = TestClient(model_runner.create_app()).post(
        "/v1/embeddings",
        json={"input": "hello", "dimensions": 2, "timeout_seconds": 5},
    )

    assert response.status_code == 200
    assert calls == [
        {
            "texts": ["hello"],
            "model": model_runner.DEFAULT_EMBEDDING_MODEL,
            "dimensions": 2,
            "timeout_seconds": 5.0,
        }
    ]


def test_rerank_endpoint_forwards_scheduler_timeout(monkeypatch):
    from fastapi.testclient import TestClient

    calls: list[dict[str, object]] = []

    def fake_rerank(*_args, **kwargs):
        calls.append(kwargs)
        return [0.25]

    monkeypatch.setattr(model_runner, "_rerank_with_transformers", fake_rerank)

    response = TestClient(model_runner.create_app()).post(
        "/v1/rerank",
        json={
            "query": "hello",
            "passages": ["world"],
            "quantization": model_runner.DEFAULT_RERANKER_QUANTIZATION,
            "timeout_seconds": 5,
        },
    )

    assert response.status_code == 200
    assert calls[0]["timeout_seconds"] == 5.0


def test_embedding_encode_is_serialised_inside_model_runner(monkeypatch):
    class FakeVectors:
        def __init__(self, count: int) -> None:
            self.count = count

        def tolist(self):
            return [[1.0, 2.0] for _ in range(self.count)]

    class FakeEncoder:
        def __init__(self) -> None:
            self.active = 0
            self.max_active = 0
            self.lock = threading.Lock()

        def encode(self, texts, **_kwargs):
            with self.lock:
                self.active += 1
                self.max_active = max(self.max_active, self.active)
            try:
                barrier.wait(timeout=1.0)
            except threading.BrokenBarrierError:
                pass
            finally:
                with self.lock:
                    self.active -= 1
            return FakeVectors(len(texts))

    barrier = threading.Barrier(2)
    encoder = FakeEncoder()
    monkeypatch.setattr(model_runner, "_load_embedding_model", lambda _model: encoder)

    def run_encode():
        return model_runner._embed_with_sentence_transformers(["a"], model="Snowflake/test", dimensions=2)

    first = threading.Thread(target=run_encode)
    second = threading.Thread(target=run_encode)
    first.start()
    second.start()
    first.join(timeout=2.0)
    second.join(timeout=2.0)

    assert encoder.max_active == 1


def test_cached_embedding_refreshes_residency_before_requesting_lease(monkeypatch):
    events: list[tuple[str, object]] = []

    class FakeVectors:
        def tolist(self):
            return [[1.0, 2.0]]

    class FakeEncoder:
        def encode(self, texts, **_kwargs):
            assert texts == ["cached"]
            events.append(("encode", len(texts)))
            return FakeVectors()

    class FakeLease:
        def __enter__(self):
            events.append(("lease-enter", None))
            return self

        def __exit__(self, *_args):
            events.append(("lease-exit", None))
            return False

    class FakeScheduler:
        def record_model_residency(self, residency):
            events.append(("resident", (residency.task_type, residency.model_id, residency.resident)))

        def acquire(self, profile):
            events.append(("acquire", (profile.model_id, profile.exclusive, profile.share_group)))
            return FakeLease()

    model_runner._EMBEDDING_MODELS.clear()
    model_runner._EMBEDDING_MODELS["Snowflake/cached"] = FakeEncoder()
    monkeypatch.setattr(model_runner, "get_gpu_scheduler", lambda: FakeScheduler())

    vectors = model_runner._embed_with_sentence_transformers(["cached"], model="Snowflake/cached", dimensions=2)

    assert vectors == [[1.0, 2.0]]
    assert events[:2] == [
        ("resident", ("embedding", "Snowflake/cached", True)),
        ("acquire", ("Snowflake/cached", False, "embedding")),
    ]


def test_model_runner_busy_response_is_structured_and_retryable(monkeypatch):
    from fastapi.testclient import TestClient

    from flux_llm_kb.gpu_scheduler import GpuLeaseTimeout

    def busy(*_args, **_kwargs):
        raise GpuLeaseTimeout("GPU scheduler timed out", retry_after_seconds=3.0)

    monkeypatch.setattr(model_runner, "_embed_with_sentence_transformers", busy)

    response = TestClient(model_runner.create_app()).post("/v1/embeddings", json={"input": "hello", "dimensions": 2})

    assert response.status_code == 429
    assert response.json()["detail"]["code"] == "gpu.scheduler_busy"
    assert response.json()["detail"]["retryable"] is True
    assert response.json()["detail"]["retry_after_seconds"] == 3.0


@pytest.mark.parametrize(
    ("endpoint", "payload", "target_name"),
    [
        ("/v1/embeddings", {"input": "hello", "dimensions": 2}, "_embed_with_sentence_transformers"),
        (
            "/v1/rerank",
            {"query": "hello", "passages": ["world"], "quantization": model_runner.DEFAULT_RERANKER_QUANTIZATION},
            "_rerank_with_transformers",
        ),
        ("/v1/ocr/image", {"path": "/tmp/private-image.png", "model": "PP-OCRv5"}, "_ocr_image_with_paddle"),
        ("/v1/ocr/document", {"path": "/tmp/private-document.png", "model": "PaddleOCR-VL"}, "_ocr_document_with_paddle"),
    ],
)
def test_model_runner_gpu_lease_rejection_responses_are_retryable(monkeypatch, endpoint, payload, target_name):
    from fastapi.testclient import TestClient

    from flux_llm_kb.gpu_scheduler import GpuLeaseRejected

    def rejected(*_args, **_kwargs):
        raise GpuLeaseRejected("vram_budget_exceeded")

    monkeypatch.delenv("FLUX_KB_PADDLE_RUNNER_BASE_URL", raising=False)
    monkeypatch.setattr(model_runner, target_name, rejected)

    response = TestClient(model_runner.create_app(), raise_server_exceptions=False).post(endpoint, json=payload)

    assert response.status_code == 429
    assert response.json()["detail"] == {
        "code": "gpu.scheduler_busy",
        "message": "vram_budget_exceeded",
        "retryable": True,
        "retry_after_seconds": 1.0,
    }


@pytest.mark.parametrize(
    ("endpoint", "payload"),
    [
        ("/v1/ocr/image", {"path": "/tmp/private-image.png", "model": "PP-OCRv5"}),
        ("/v1/ocr/document", {"path": "/tmp/private-document.png", "model": "PaddleOCR-VL"}),
    ],
)
def test_model_runner_proxied_ocr_busy_responses_are_structured(monkeypatch, endpoint, payload):
    from fastapi.testclient import TestClient

    def busy_proxy(*_args, **_kwargs):
        raise model_runner.ModelRunnerBusy("vram_budget_exceeded", retry_after_seconds=4.0)

    monkeypatch.setenv("FLUX_KB_PADDLE_RUNNER_BASE_URL", "http://paddle-runner:8791")
    monkeypatch.setattr(model_runner, "_proxy_paddle_request", busy_proxy)

    response = TestClient(model_runner.create_app(), raise_server_exceptions=False).post(endpoint, json=payload)

    assert response.status_code == 429
    assert response.json()["detail"] == {
        "code": "gpu.scheduler_busy",
        "message": "vram_budget_exceeded",
        "retryable": True,
        "retry_after_seconds": 4.0,
    }


def test_gpu_unload_endpoint_removes_exact_embedding_model_and_preserves_unrelated(monkeypatch):
    from fastapi.testclient import TestClient

    records: list[object] = []

    class FakeScheduler:
        def record_model_residency(self, residency):
            records.append(residency)

        def status(self):
            return {"enabled": True, "mode": "test"}

    model_runner._EMBEDDING_MODELS.clear()
    model_runner._EMBEDDING_MODELS["Snowflake/remove"] = object()
    model_runner._EMBEDDING_MODELS["Snowflake/keep"] = object()
    monkeypatch.setattr(model_runner, "get_gpu_scheduler", lambda: FakeScheduler())

    response = TestClient(model_runner.create_app()).post(
        "/v1/gpu/unload",
        json={"task_type": "embedding", "model_id": "Snowflake/remove"},
    )
    repeat = TestClient(model_runner.create_app()).post(
        "/v1/gpu/unload",
        json={"task_type": "embedding", "model_id": "Snowflake/remove"},
    )

    assert response.status_code == 200
    assert response.json()["unloaded"] is True
    assert repeat.status_code == 200
    assert repeat.json()["unloaded"] is False
    assert "Snowflake/remove" not in model_runner._EMBEDDING_MODELS
    assert "Snowflake/keep" in model_runner._EMBEDDING_MODELS
    assert records[-1].resident is False
    assert records[-1].task_type == "embedding"
    assert records[-1].model_id == "Snowflake/remove"


def test_gpu_unload_endpoint_removes_exact_rerank_and_ocr_models(monkeypatch):
    from fastapi.testclient import TestClient

    records: list[object] = []

    class FakeScheduler:
        def record_model_residency(self, residency):
            records.append(residency)

        def status(self):
            return {"enabled": True, "mode": "test"}

    model_runner._RERANKER_MODELS.clear()
    model_runner._PADDLE_OCR_MODELS.clear()
    model_runner._PADDLE_OCR_VL_MODELS.clear()
    model_runner._PADDLE_OCR_FACTORY_IDS.clear()
    model_runner._PADDLE_OCR_VL_FACTORY_IDS.clear()
    model_runner._RERANKER_MODELS[("Qwen/Qwen3-Reranker-4B", "awq_int4", "drawais/remove")] = object()
    model_runner._RERANKER_MODELS[("Qwen/Qwen3-Reranker-4B", "awq_int4", "drawais/keep")] = object()
    model_runner._PADDLE_OCR_MODELS["PP-OCRv5"] = object()
    model_runner._PADDLE_OCR_MODELS["PP-OCRv4"] = object()
    model_runner._PADDLE_OCR_FACTORY_IDS["PP-OCRv5"] = 1
    model_runner._PADDLE_OCR_FACTORY_IDS["PP-OCRv4"] = 2
    model_runner._PADDLE_OCR_VL_MODELS["PaddleOCR-VL"] = object()
    model_runner._PADDLE_OCR_VL_MODELS["PaddleOCR-VL-keep"] = object()
    model_runner._PADDLE_OCR_VL_FACTORY_IDS["PaddleOCR-VL"] = 3
    model_runner._PADDLE_OCR_VL_FACTORY_IDS["PaddleOCR-VL-keep"] = 4
    monkeypatch.setattr(model_runner, "get_gpu_scheduler", lambda: FakeScheduler())

    client = TestClient(model_runner.create_app())
    rerank_response = client.post("/v1/gpu/unload", json={"task_type": "rerank", "model_id": "drawais/remove"})
    ocr_response = client.post("/v1/gpu/unload", json={"task_type": "ocr_image", "model_id": "PP-OCRv5"})
    vl_response = client.post("/v1/gpu/unload", json={"task_type": "ocr_document", "model_id": "PaddleOCR-VL"})

    assert rerank_response.status_code == 200
    assert ocr_response.status_code == 200
    assert vl_response.status_code == 200
    assert rerank_response.json()["unloaded"] is True
    assert ocr_response.json()["unloaded"] is True
    assert vl_response.json()["unloaded"] is True
    assert ("Qwen/Qwen3-Reranker-4B", "awq_int4", "drawais/remove") not in model_runner._RERANKER_MODELS
    assert ("Qwen/Qwen3-Reranker-4B", "awq_int4", "drawais/keep") in model_runner._RERANKER_MODELS
    assert "PP-OCRv5" not in model_runner._PADDLE_OCR_MODELS
    assert "PP-OCRv4" in model_runner._PADDLE_OCR_MODELS
    assert "PaddleOCR-VL" not in model_runner._PADDLE_OCR_VL_MODELS
    assert "PaddleOCR-VL-keep" in model_runner._PADDLE_OCR_VL_MODELS
    assert [(record.task_type, record.model_id, record.resident) for record in records[-3:]] == [
        ("rerank", "drawais/remove", False),
        ("ocr_image", "PP-OCRv5", False),
        ("ocr_document", "PaddleOCR-VL", False),
    ]
