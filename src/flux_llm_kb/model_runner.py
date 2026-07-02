from __future__ import annotations

import argparse
import base64
import json
import os
import tempfile
import time
from typing import Any, Iterable
from urllib.error import URLError
from urllib.parse import urljoin
from urllib.request import Request, urlopen
from pathlib import Path


DEFAULT_MODEL_RUNNER_BASE_URL = "http://127.0.0.1:8790"
DEFAULT_EMBEDDING_MODEL = "Snowflake/snowflake-arctic-embed-l-v2.0"
DEFAULT_RERANKER_MODEL = "Qwen/Qwen3-Reranker-4B"
DEFAULT_RERANKER_QUANTIZATION = "int4_awq"
DEFAULT_OCR_SIMPLE_MODEL = "PP-OCRv5"
DEFAULT_OCR_DOCUMENT_MODEL = "PaddleOCR-VL"
DEFAULT_MODEL_RUNNER_TIMEOUT_SECONDS = 600
DEFAULT_PADDLEX_CACHE_HOME = "/root/.paddleocr/paddlex"
os.environ.setdefault("PADDLE_PDX_CACHE_HOME", DEFAULT_PADDLEX_CACHE_HOME)
os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")
os.environ.setdefault("PADDLE_PDX_MODEL_SOURCE", "bos")
_EMBEDDING_MODELS: dict[str, Any] = {}
_RERANKER_MODELS: dict[tuple[str, str], Any] = {}
_PADDLE_OCR_MODELS: dict[str, Any] = {}
_PADDLE_OCR_VL_MODELS: dict[str, Any] = {}


class ModelRunnerError(RuntimeError):
    pass


class ModelRunnerClient:
    def __init__(self, base_url: str | None = None, *, timeout_seconds: int | None = None) -> None:
        self.base_url = _resolve_model_runner_base_url(base_url).rstrip("/")
        resolved_timeout = timeout_seconds
        if resolved_timeout is None:
            resolved_timeout = int(os.environ.get("FLUX_KB_MODEL_RUNNER_TIMEOUT_SECONDS") or DEFAULT_MODEL_RUNNER_TIMEOUT_SECONDS)
        self.timeout_seconds = max(1, int(resolved_timeout))

    def embed(self, texts: Iterable[str], *, model: str, dimensions: int) -> list[list[float]]:
        payload = self._post_json(
            "/v1/embeddings",
            {"model": model, "dimensions": int(dimensions), "texts": list(texts)},
        )
        vectors = payload.get("vectors")
        if not isinstance(vectors, list):
            raise ModelRunnerError("model-runner embedding response did not include vectors")
        return [[float(value) for value in vector] for vector in vectors]

    def rerank(self, query: str, passages: Iterable[str], *, model: str, quantization: str) -> list[float]:
        payload = self._post_json(
            "/v1/rerank",
            {
                "model": model,
                "quantization": quantization,
                "query": query,
                "passages": list(passages),
            },
        )
        scores = payload.get("scores")
        if not isinstance(scores, list):
            raise ModelRunnerError("model-runner rerank response did not include scores")
        return [float(score) for score in scores]

    def ocr_image(self, path: str, *, model: str) -> dict[str, Any]:
        return self._post_json("/v1/ocr/image", {"path": path, "model": model})

    def ocr_document(self, path: str, *, pages: list[int] | None, model: str) -> dict[str, Any]:
        return self._post_json("/v1/ocr/document", {"path": path, "pages": pages or [], "model": model})

    def ocr_file(self, path: str | Path, *, model: str, document: bool = False) -> dict[str, Any]:
        file_path = Path(path)
        payload = {
            "filename": file_path.name,
            "content_b64": base64.b64encode(file_path.read_bytes()).decode("ascii"),
            "model": model,
        }
        endpoint = "/v1/ocr/document" if document else "/v1/ocr/image"
        return self._post_json(endpoint, payload)

    def health(self) -> dict[str, Any]:
        try:
            with urlopen(f"{self.base_url}/health", timeout=min(self.timeout_seconds, 10)) as response:
                return json.loads(response.read().decode("utf-8"))
        except Exception as exc:  # pragma: no cover - environment-specific
            raise ModelRunnerError(str(exc)) from exc

    def _post_json(self, path: str, payload: dict[str, Any]) -> dict[str, Any]:
        return _post_json_to_base_url(self.base_url, path, payload, self.timeout_seconds)


def _resolve_model_runner_base_url(explicit_base_url: str | None = None) -> str:
    if explicit_base_url:
        return explicit_base_url
    env_base_url = os.environ.get("FLUX_KB_MODEL_RUNNER_BASE_URL")
    if env_base_url:
        return env_base_url
    try:
        from .settings import SettingsService

        configured = SettingsService().resolve("model_runner.base_url").raw_value
    except Exception:
        configured = None
    if configured:
        return str(configured)
    return DEFAULT_MODEL_RUNNER_BASE_URL


class ModelRunnerRerankScorer:
    def __init__(self, client: ModelRunnerClient | None = None) -> None:
        self.client = client or ModelRunnerClient()

    def score(self, query: str, passages: list[str], *, model: str, quantization: str) -> list[float]:
        return self.client.rerank(query, passages, model=model, quantization=quantization)


def _post_json_to_base_url(base_url: str, path: str, payload: dict[str, Any], timeout_seconds: int) -> dict[str, Any]:
    body = json.dumps(payload).encode("utf-8")
    request = Request(
        urljoin(f"{base_url.rstrip('/')}/", path.lstrip("/")),
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urlopen(request, timeout=timeout_seconds) as response:
            raw = response.read().decode("utf-8")
    except URLError as exc:  # pragma: no cover - network-specific
        raise ModelRunnerError(str(exc)) from exc
    except Exception as exc:  # pragma: no cover - network-specific
        raise ModelRunnerError(str(exc)) from exc
    try:
        response_payload = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ModelRunnerError("model-runner returned invalid JSON") from exc
    if not isinstance(response_payload, dict):
        raise ModelRunnerError("model-runner returned a non-object payload")
    if response_payload.get("ok") is False:
        raise ModelRunnerError(str(response_payload.get("message") or "model-runner request failed"))
    return response_payload


def _paddle_runner_base_url() -> str:
    return (os.environ.get("FLUX_KB_PADDLE_RUNNER_BASE_URL") or "").rstrip("/")


def _paddle_runner_timeout_seconds() -> int:
    return _env_positive_int("FLUX_KB_MODEL_RUNNER_TIMEOUT_SECONDS", DEFAULT_MODEL_RUNNER_TIMEOUT_SECONDS)


def _proxy_paddle_request(path: str, payload: dict[str, Any]) -> dict[str, Any]:
    base_url = _paddle_runner_base_url()
    if not base_url:
        raise ModelRunnerError("Paddle runner base URL is not configured")
    return _post_json_to_base_url(base_url, path, payload, _paddle_runner_timeout_seconds())


def _paddle_runner_health() -> dict[str, Any] | None:
    base_url = _paddle_runner_base_url()
    if not base_url:
        return None
    try:
        with urlopen(f"{base_url}/health", timeout=min(_paddle_runner_timeout_seconds(), 10)) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except Exception as exc:  # pragma: no cover - deployment-specific
        return {"ok": False, "message": str(exc)}
    return payload if isinstance(payload, dict) else {"ok": False, "message": "Paddle runner returned invalid health payload"}


def health_payload(role: str | None = None) -> dict[str, Any]:
    resolved_role = role or os.environ.get("FLUX_KB_MODEL_RUNNER_ROLE", "model-runner")
    cuda_available = False
    if resolved_role != "paddle-runner":
        try:
            import torch

            cuda_available = bool(torch.cuda.is_available())
        except Exception:
            cuda_available = False
    paddle_runner = None if resolved_role == "paddle-runner" else _paddle_runner_health()
    if paddle_runner is not None and paddle_runner.get("ok") is True:
        paddle_cuda_available = bool(paddle_runner.get("paddle_cuda_available"))
        paddle_cuda_device_count = int(paddle_runner.get("paddle_cuda_device_count") or 0)
        paddle_device = str(paddle_runner.get("paddle_device") or "cpu")
    else:
        paddle_cuda_available, paddle_cuda_device_count = _paddle_cuda_status()
        paddle_device = _paddle_device()
    payload = {
        "ok": True,
        "service": resolved_role,
        "cuda_required": True,
        "cuda_available": cuda_available,
        "paddle_cuda_available": paddle_cuda_available,
        "paddle_cuda_device_count": paddle_cuda_device_count,
        "paddle_device": paddle_device,
        "embedding_model": os.environ.get("FLUX_KB_RETRIEVAL_EMBEDDING_MODEL", DEFAULT_EMBEDDING_MODEL),
        "reranker_model": os.environ.get("FLUX_KB_RETRIEVAL_RERANKER_MODEL", DEFAULT_RERANKER_MODEL),
        "reranker_quantization": os.environ.get("FLUX_KB_RETRIEVAL_RERANKER_QUANTIZATION", DEFAULT_RERANKER_QUANTIZATION),
        "ocr_engine": os.environ.get("FLUX_KB_OCR_ENGINE", "paddleocr"),
        "ocr_simple_model": os.environ.get("FLUX_KB_OCR_SIMPLE_MODEL", DEFAULT_OCR_SIMPLE_MODEL),
        "ocr_document_model": os.environ.get("FLUX_KB_OCR_DOCUMENT_MODEL", DEFAULT_OCR_DOCUMENT_MODEL),
        "models_dir": os.environ.get("FLUX_KB_MODEL_RUNNER_MODELS_DIR", "/models"),
    }
    if resolved_role != "paddle-runner" and _paddle_runner_base_url():
        payload["paddle_runner_base_url"] = _paddle_runner_base_url()
        payload["paddle_runner"] = paddle_runner
    return payload


def _load_embedding_model(model: str) -> Any:
    if model not in _EMBEDDING_MODELS:
        try:
            from sentence_transformers import SentenceTransformer
        except ImportError as exc:
            raise ModelRunnerError("sentence-transformers is required for Snowflake embeddings") from exc
        try:
            import torch

            device = "cuda" if torch.cuda.is_available() else "cpu"
        except Exception:
            device = "cpu"
        try:
            _EMBEDDING_MODELS[model] = SentenceTransformer(model, device=device, trust_remote_code=True)
        except TypeError:
            _EMBEDDING_MODELS[model] = SentenceTransformer(model, device=device)
    return _EMBEDDING_MODELS[model]


def _embed_with_sentence_transformers(texts: list[str], *, model: str, dimensions: int) -> list[list[float]]:
    encoder = _load_embedding_model(model)
    vectors = encoder.encode(texts, normalize_embeddings=True, convert_to_numpy=True)
    result = [[float(value) for value in vector] for vector in vectors.tolist()]
    for vector in result:
        if len(vector) != dimensions:
            raise ModelRunnerError(f"embedding dimension mismatch: expected {dimensions}, got {len(vector)}")
    return result


def _load_reranker_model(model: str, quantization: str) -> Any:
    cache_key = (model, quantization)
    if cache_key not in _RERANKER_MODELS:
        try:
            import torch
            from sentence_transformers import CrossEncoder
        except ImportError as exc:
            raise ModelRunnerError("sentence-transformers and torch are required for Qwen reranking") from exc
        model_kwargs: dict[str, Any] = {
            "trust_remote_code": True,
        }
        cross_encoder_model_kwargs: dict[str, Any] = {"device_map": "auto"}
        if quantization in {"int4_awq", "int4", "4bit"}:
            try:
                from transformers import BitsAndBytesConfig
            except ImportError as exc:
                raise ModelRunnerError("bitsandbytes-compatible transformers is required for 4-bit Qwen reranking") from exc
            cross_encoder_model_kwargs["quantization_config"] = BitsAndBytesConfig(
                load_in_4bit=True,
                bnb_4bit_compute_dtype=torch.float16,
                bnb_4bit_quant_type="nf4",
                bnb_4bit_use_double_quant=True,
            )
        elif getattr(torch, "cuda", None) is not None and torch.cuda.is_available():
            cross_encoder_model_kwargs["torch_dtype"] = torch.float16
        model_kwargs["model_kwargs"] = cross_encoder_model_kwargs
        model_kwargs["max_length"] = int(os.environ.get("FLUX_KB_RETRIEVAL_MAX_RERANK_PASSAGE_TOKENS", "1536"))
        _RERANKER_MODELS[cache_key] = CrossEncoder(model, **model_kwargs)
    return _RERANKER_MODELS[cache_key]


def _rerank_with_transformers(query: str, passages: list[str], *, model: str, quantization: str) -> list[float]:
    if not passages:
        return []
    reranker = _load_reranker_model(model, quantization)
    scores = reranker.predict([(query, passage) for passage in passages])
    return [float(score) for score in scores]


def _load_paddleocr(model: str) -> Any:
    if model not in _PADDLE_OCR_MODELS:
        try:
            from paddleocr import PaddleOCR
        except ImportError as exc:
            raise ModelRunnerError("paddleocr is required for OCR") from exc
        kwargs: dict[str, Any] = {
            "lang": "en",
            "use_doc_orientation_classify": False,
            "use_doc_unwarping": False,
            "use_textline_orientation": False,
            "enable_mkldnn": False,
            "device": _paddle_device(),
        }
        if model:
            kwargs["ocr_version"] = model
        _PADDLE_OCR_MODELS[model] = PaddleOCR(**kwargs)
    return _PADDLE_OCR_MODELS[model]


def _paddle_cuda_status() -> tuple[bool, int]:
    try:
        import paddle

        if not paddle.device.is_compiled_with_cuda():
            return False, 0
        device_count = int(paddle.device.cuda.device_count())
        return device_count > 0, device_count
    except Exception:
        return False, 0


def _paddle_device() -> str:
    cuda_available, _device_count = _paddle_cuda_status()
    return "gpu:0" if cuda_available else "cpu"


def _load_paddleocr_vl(model: str) -> Any:
    if model not in _PADDLE_OCR_VL_MODELS:
        try:
            from paddlex import create_pipeline
        except ImportError as exc:
            raise ModelRunnerError("paddlex[ocr] is required for PaddleOCR-VL document OCR") from exc
        _PADDLE_OCR_VL_MODELS[model] = create_pipeline(
            pipeline=model,
            device=_paddle_device(),
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
        )
    return _PADDLE_OCR_VL_MODELS[model]


def _paddleocr_text(value: Any) -> str:
    parts: list[str] = []

    def visit(item: Any) -> None:
        if isinstance(item, str):
            text = item.strip()
            if text:
                parts.append(text)
            return
        if isinstance(item, dict):
            for key in ("text", "rec_text", "rec_texts", "content"):
                if key in item:
                    visit(item[key])
            for key in ("res", "data", "results", "parsing_res_list"):
                if key in item:
                    visit(item[key])
            return
        if hasattr(item, "to_dict"):
            visit(item.to_dict())
            return
        if hasattr(item, "items"):
            try:
                visit(dict(item.items()))
                return
            except Exception:
                pass
        content = getattr(item, "content", None)
        if content is not None:
            visit(content)
            return
        if isinstance(item, (list, tuple)):
            if len(item) >= 2 and isinstance(item[1], (list, tuple)) and item[1] and isinstance(item[1][0], str):
                visit(item[1][0])
                return
            for child in item:
                visit(child)

    visit(value)
    return "\n".join(parts)


def _ocr_image_with_paddle(path: str, *, model: str) -> str:
    if _paddle_runner_base_url():
        payload = _proxy_paddle_request("/v1/ocr/image", {"path": str(path), "model": model})
        return str(payload.get("text") or "")
    ocr = _load_paddleocr(model)
    if hasattr(ocr, "predict"):
        result = ocr.predict(str(path))
    else:
        result = ocr.ocr(str(path), cls=True)
    return _paddleocr_text(result)


def _ocr_document_with_paddle(path: str, *, model: str) -> str:
    if _paddle_runner_base_url():
        payload = _proxy_paddle_request("/v1/ocr/document", {"path": str(path), "model": model})
        return str(payload.get("text") or "")
    if model.startswith("PaddleOCR-VL"):
        pipeline = _load_paddleocr_vl(model)
        return _paddleocr_text(list(pipeline.predict(str(path))))
    return _ocr_image_with_paddle(path, model=model)


def _with_ocr_input_path(payload: dict[str, Any], consumer: Any) -> str:
    content_b64 = payload.get("content_b64")
    if isinstance(content_b64, str) and content_b64.strip():
        filename = str(payload.get("filename") or "ocr-input.bin")
        suffix = Path(filename).suffix or ".bin"
        fd, temp_name = tempfile.mkstemp(prefix="flux-kb-ocr-", suffix=suffix)
        os.close(fd)
        temp_path = Path(temp_name)
        try:
            temp_path.write_bytes(base64.b64decode(content_b64))
            return str(consumer(temp_path))
        finally:
            try:
                temp_path.unlink()
            except FileNotFoundError:
                pass
    path = str(payload.get("path") or "")
    return str(consumer(Path(path)))


def _env_positive_int(name: str, default: int) -> int:
    try:
        return max(1, int(os.environ.get(name, str(default))))
    except (TypeError, ValueError):
        return default


def _env_non_negative_float(name: str, default: float) -> float:
    try:
        return max(0.0, float(os.environ.get(name, str(default))))
    except (TypeError, ValueError):
        return default


def _download_snapshot_with_retries(
    snapshot_download: Any,
    *,
    repo_id: str,
    cache_dir: str,
    ignore_patterns: list[str] | None = None,
) -> str:
    attempts = _env_positive_int("FLUX_KB_MODEL_RUNNER_DOWNLOAD_RETRIES", 5)
    retry_seconds = _env_non_negative_float("FLUX_KB_MODEL_RUNNER_DOWNLOAD_RETRY_SECONDS", 10.0)
    max_workers = _env_positive_int("FLUX_KB_MODEL_RUNNER_DOWNLOAD_MAX_WORKERS", 2)
    try:
        kwargs: dict[str, Any] = {
            "repo_id": repo_id,
            "cache_dir": cache_dir,
            "local_files_only": True,
            "max_workers": max_workers,
        }
        if ignore_patterns:
            kwargs["ignore_patterns"] = ignore_patterns
        return str(snapshot_download(**kwargs))
    except Exception:
        pass
    last_error: Exception | None = None
    for attempt in range(1, attempts + 1):
        try:
            kwargs: dict[str, Any] = {
                "repo_id": repo_id,
                "cache_dir": cache_dir,
                "local_files_only": False,
                "max_workers": max_workers,
            }
            if ignore_patterns:
                kwargs["ignore_patterns"] = ignore_patterns
            return str(snapshot_download(**kwargs))
        except Exception as exc:
            last_error = exc
            if attempt >= attempts:
                break
            print(f"Retrying Hugging Face snapshot download for {repo_id} after {exc.__class__.__name__}: {exc}", flush=True)
            if retry_seconds > 0:
                time.sleep(retry_seconds)
    raise ModelRunnerError(f"failed to download Hugging Face snapshot for {repo_id} after {attempts} attempts: {last_error}") from last_error


def download_models(models_dir: str) -> dict[str, Any]:
    models_root = os.path.abspath(models_dir)
    hf_home = os.environ.get("HF_HOME") or os.path.join(models_root, "huggingface")
    hf_hub_cache = os.environ.get("HF_HUB_CACHE") or os.path.join(hf_home, "hub")
    os.makedirs(hf_home, exist_ok=True)
    os.makedirs(hf_hub_cache, exist_ok=True)
    results: list[dict[str, Any]] = []
    try:
        from huggingface_hub import snapshot_download
    except ImportError as exc:
        raise ModelRunnerError("huggingface-hub is required to pre-download model-runner models") from exc
    embedding_model = os.environ.get("FLUX_KB_RETRIEVAL_EMBEDDING_MODEL", DEFAULT_EMBEDDING_MODEL)
    for repo_id, ignore_patterns in (
        (embedding_model, ["onnx/*", "*.onnx"]),
        (os.environ.get("FLUX_KB_RETRIEVAL_RERANKER_MODEL", DEFAULT_RERANKER_MODEL), None),
    ):
        path = _download_snapshot_with_retries(
            snapshot_download,
            repo_id=repo_id,
            cache_dir=hf_hub_cache,
            ignore_patterns=ignore_patterns,
        )
        results.append({"repo_id": repo_id, "path": path})
    return {"ok": True, "models_dir": models_root, "downloads": results}


def download_paddle_models(models_dir: str) -> dict[str, Any]:
    models_root = os.path.abspath(models_dir)
    hf_home = os.environ.get("HF_HOME") or os.path.join(models_root, "huggingface")
    hf_hub_cache = os.environ.get("HF_HUB_CACHE") or os.path.join(hf_home, "hub")
    os.makedirs(models_root, exist_ok=True)
    os.makedirs(hf_home, exist_ok=True)
    os.makedirs(hf_hub_cache, exist_ok=True)
    results: list[dict[str, Any]] = []
    try:
        from huggingface_hub import snapshot_download
    except ImportError as exc:
        raise ModelRunnerError("huggingface-hub is required to pre-download PaddleOCR-VL models") from exc
    repo_id = os.environ.get("FLUX_KB_OCR_DOCUMENT_MODEL_REPO", "PaddlePaddle/PaddleOCR-VL")
    path = _download_snapshot_with_retries(snapshot_download, repo_id=repo_id, cache_dir=hf_hub_cache)
    results.append({"repo_id": repo_id, "path": path})
    try:
        _load_paddleocr(os.environ.get("FLUX_KB_OCR_SIMPLE_MODEL", DEFAULT_OCR_SIMPLE_MODEL))
        results.append({"repo_id": "paddleocr-runtime", "path": os.environ.get("PADDLEOCR_HOME", "")})
        _load_paddleocr_vl(os.environ.get("FLUX_KB_OCR_DOCUMENT_MODEL", DEFAULT_OCR_DOCUMENT_MODEL))
        results.append({"repo_id": "paddleocr-vl-runtime", "path": os.environ.get("PADDLE_PDX_CACHE_HOME", "")})
    except Exception as exc:
        results.append({"repo_id": "paddleocr-runtime", "error": str(exc)})
        raise
    return {"ok": True, "models_dir": models_root, "downloads": results}


def create_app():
    try:
        from fastapi import FastAPI
    except ImportError as exc:  # pragma: no cover - deployment-only
        raise RuntimeError("fastapi is required to serve the model-runner") from exc

    app = FastAPI(title="Flux model-runner")

    @app.get("/health")
    def health() -> dict[str, Any]:
        return health_payload()

    @app.post("/v1/embeddings")
    def embeddings(payload: dict[str, Any]) -> dict[str, Any]:
        model = str(payload.get("model") or DEFAULT_EMBEDDING_MODEL)
        dimensions = int(payload.get("dimensions") or 1024)
        texts = payload.get("texts") if isinstance(payload.get("texts"), list) else []
        return {
            "ok": True,
            "model": model,
            "dimensions": dimensions,
            "vectors": _embed_with_sentence_transformers([str(text or "") for text in texts], model=model, dimensions=dimensions),
        }

    @app.post("/v1/rerank")
    def rerank(payload: dict[str, Any]) -> dict[str, Any]:
        passages = payload.get("passages") if isinstance(payload.get("passages"), list) else []
        model = str(payload.get("model") or DEFAULT_RERANKER_MODEL)
        quantization = str(payload.get("quantization") or DEFAULT_RERANKER_QUANTIZATION)
        return {"ok": True, "model": model, "quantization": quantization, "scores": _rerank_with_transformers(str(payload.get("query") or ""), [str(passage or "") for passage in passages], model=model, quantization=quantization)}

    @app.post("/v1/ocr/image")
    def ocr_image(payload: dict[str, Any]) -> dict[str, Any]:
        path = str(payload.get("path") or "")
        model = str(payload.get("model") or DEFAULT_OCR_SIMPLE_MODEL)
        if _paddle_runner_base_url():
            return _proxy_paddle_request("/v1/ocr/image", {**payload, "model": model})
        return {"ok": True, "model": model, "text": _with_ocr_input_path(payload, lambda input_path: _ocr_image_with_paddle(str(input_path or path), model=model))}

    @app.post("/v1/ocr/document")
    def ocr_document(payload: dict[str, Any]) -> dict[str, Any]:
        path = str(payload.get("path") or "")
        model = str(payload.get("model") or DEFAULT_OCR_DOCUMENT_MODEL)
        if _paddle_runner_base_url():
            return _proxy_paddle_request("/v1/ocr/document", {**payload, "model": model})
        return {"ok": True, "model": model, "text": _with_ocr_input_path(payload, lambda input_path: _ocr_document_with_paddle(str(input_path or path), model=model))}

    return app


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m flux_llm_kb.model_runner")
    subparsers = parser.add_subparsers(dest="command", required=True)
    health = subparsers.add_parser("health")
    health.add_argument("--role", choices=["model-runner", "paddle-runner"], default=None)
    serve = subparsers.add_parser("serve")
    serve.add_argument("--host", default="127.0.0.1")
    serve.add_argument("--port", type=int, default=8790)
    serve_paddle = subparsers.add_parser("serve-paddle")
    serve_paddle.add_argument("--host", default="127.0.0.1")
    serve_paddle.add_argument("--port", type=int, default=8791)
    download = subparsers.add_parser("download-models")
    download.add_argument("--models-dir", default="/models")
    download_paddle = subparsers.add_parser("download-paddle-models")
    download_paddle.add_argument("--models-dir", default="/models")
    args = parser.parse_args(argv)

    if args.command == "health":
        print(json.dumps(health_payload(args.role), indent=2, sort_keys=True))
        return 0
    if args.command == "download-models":
        print(json.dumps(download_models(args.models_dir), indent=2))
        return 0
    if args.command == "download-paddle-models":
        os.environ["FLUX_KB_MODEL_RUNNER_ROLE"] = "paddle-runner"
        os.environ.pop("FLUX_KB_PADDLE_RUNNER_BASE_URL", None)
        print(json.dumps(download_paddle_models(args.models_dir), indent=2))
        return 0
    if args.command == "serve":
        import uvicorn

        uvicorn.run(create_app(), host=args.host, port=args.port)
        return 0
    if args.command == "serve-paddle":
        import uvicorn

        os.environ["FLUX_KB_MODEL_RUNNER_ROLE"] = "paddle-runner"
        os.environ.pop("FLUX_KB_PADDLE_RUNNER_BASE_URL", None)
        uvicorn.run(create_app(), host=args.host, port=args.port)
        return 0
    raise ValueError(args.command)


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
