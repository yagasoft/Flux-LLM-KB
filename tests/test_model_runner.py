from __future__ import annotations

import base64
import sys
from types import ModuleType, SimpleNamespace

from flux_llm_kb import model_runner


def test_model_runner_client_timeout_allows_cold_model_start(monkeypatch):
    monkeypatch.delenv("FLUX_KB_MODEL_RUNNER_TIMEOUT_SECONDS", raising=False)
    assert model_runner.ModelRunnerClient().timeout_seconds == 600

    monkeypatch.setenv("FLUX_KB_MODEL_RUNNER_TIMEOUT_SECONDS", "900")
    assert model_runner.ModelRunnerClient().timeout_seconds == 900


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
    ]
    assert [call["repo_id"] for call in calls].count(model_runner.DEFAULT_RERANKER_MODEL) == 2
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


def test_qwen_reranker_uses_cross_encoder_with_4bit_quantization(monkeypatch):
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
        quantization="int4_awq",
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
