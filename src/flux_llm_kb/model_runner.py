from __future__ import annotations

import argparse
import json
import os
import time
from typing import Any, Iterable
from urllib.error import URLError
from urllib.parse import urljoin
from urllib.request import Request, urlopen


DEFAULT_MODEL_RUNNER_BASE_URL = "http://127.0.0.1:8790"
DEFAULT_EMBEDDING_MODEL = "Snowflake/snowflake-arctic-embed-l-v2.0"
DEFAULT_RERANKER_MODEL = "Qwen/Qwen3-Reranker-4B"
DEFAULT_RERANKER_QUANTIZATION = "int4_awq"
DEFAULT_OCR_SIMPLE_MODEL = "PP-OCRv5"
DEFAULT_OCR_DOCUMENT_MODEL = "PaddleOCR-VL"
DEFAULT_MODEL_RUNNER_TIMEOUT_SECONDS = 600
_EMBEDDING_MODELS: dict[str, Any] = {}
_RERANKER_MODELS: dict[tuple[str, str], tuple[Any, Any]] = {}
_PADDLE_OCR_MODELS: dict[str, Any] = {}


class ModelRunnerError(RuntimeError):
    pass


class ModelRunnerClient:
    def __init__(self, base_url: str | None = None, *, timeout_seconds: int | None = None) -> None:
        self.base_url = (base_url or os.environ.get("FLUX_KB_MODEL_RUNNER_BASE_URL") or DEFAULT_MODEL_RUNNER_BASE_URL).rstrip("/")
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

    def health(self) -> dict[str, Any]:
        try:
            with urlopen(f"{self.base_url}/health", timeout=min(self.timeout_seconds, 10)) as response:
                return json.loads(response.read().decode("utf-8"))
        except Exception as exc:  # pragma: no cover - environment-specific
            raise ModelRunnerError(str(exc)) from exc

    def _post_json(self, path: str, payload: dict[str, Any]) -> dict[str, Any]:
        body = json.dumps(payload).encode("utf-8")
        request = Request(
            urljoin(f"{self.base_url}/", path.lstrip("/")),
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urlopen(request, timeout=self.timeout_seconds) as response:
                raw = response.read().decode("utf-8")
        except URLError as exc:  # pragma: no cover - network-specific
            raise ModelRunnerError(str(exc)) from exc
        except Exception as exc:  # pragma: no cover - network-specific
            raise ModelRunnerError(str(exc)) from exc
        try:
            payload = json.loads(raw)
        except json.JSONDecodeError as exc:
            raise ModelRunnerError("model-runner returned invalid JSON") from exc
        if not isinstance(payload, dict):
            raise ModelRunnerError("model-runner returned a non-object payload")
        if payload.get("ok") is False:
            raise ModelRunnerError(str(payload.get("message") or "model-runner request failed"))
        return payload


class ModelRunnerRerankScorer:
    def __init__(self, client: ModelRunnerClient | None = None) -> None:
        self.client = client or ModelRunnerClient()

    def score(self, query: str, passages: list[str], *, model: str, quantization: str) -> list[float]:
        return self.client.rerank(query, passages, model=model, quantization=quantization)


def health_payload() -> dict[str, Any]:
    cuda_available = False
    try:
        import torch

        cuda_available = bool(torch.cuda.is_available())
    except Exception:
        cuda_available = False
    return {
        "ok": True,
        "service": "model-runner",
        "cuda_required": True,
        "cuda_available": cuda_available,
        "embedding_model": os.environ.get("FLUX_KB_RETRIEVAL_EMBEDDING_MODEL", DEFAULT_EMBEDDING_MODEL),
        "reranker_model": os.environ.get("FLUX_KB_RETRIEVAL_RERANKER_MODEL", DEFAULT_RERANKER_MODEL),
        "reranker_quantization": os.environ.get("FLUX_KB_RETRIEVAL_RERANKER_QUANTIZATION", DEFAULT_RERANKER_QUANTIZATION),
        "ocr_engine": os.environ.get("FLUX_KB_OCR_ENGINE", "paddleocr"),
        "ocr_simple_model": os.environ.get("FLUX_KB_OCR_SIMPLE_MODEL", DEFAULT_OCR_SIMPLE_MODEL),
        "ocr_document_model": os.environ.get("FLUX_KB_OCR_DOCUMENT_MODEL", DEFAULT_OCR_DOCUMENT_MODEL),
        "models_dir": os.environ.get("FLUX_KB_MODEL_RUNNER_MODELS_DIR", "/models"),
    }


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


def _load_reranker_model(model: str, quantization: str) -> tuple[Any, Any]:
    cache_key = (model, quantization)
    if cache_key not in _RERANKER_MODELS:
        try:
            import torch
            from transformers import AutoModelForSequenceClassification, AutoTokenizer
        except ImportError as exc:
            raise ModelRunnerError("transformers and torch are required for Qwen reranking") from exc
        tokenizer = AutoTokenizer.from_pretrained(model, trust_remote_code=True)
        model_kwargs: dict[str, Any] = {
            "trust_remote_code": True,
            "device_map": "auto",
        }
        if quantization in {"int4_awq", "int4", "4bit"}:
            try:
                from transformers import BitsAndBytesConfig
            except ImportError as exc:
                raise ModelRunnerError("bitsandbytes-compatible transformers is required for 4-bit Qwen reranking") from exc
            model_kwargs["quantization_config"] = BitsAndBytesConfig(
                load_in_4bit=True,
                bnb_4bit_compute_dtype=torch.float16,
                bnb_4bit_quant_type="nf4",
                bnb_4bit_use_double_quant=True,
            )
        elif torch.cuda.is_available():
            model_kwargs["torch_dtype"] = torch.float16
        reranker = AutoModelForSequenceClassification.from_pretrained(model, **model_kwargs)
        reranker.eval()
        _RERANKER_MODELS[cache_key] = (tokenizer, reranker)
    return _RERANKER_MODELS[cache_key]


def _rerank_with_transformers(query: str, passages: list[str], *, model: str, quantization: str) -> list[float]:
    if not passages:
        return []
    try:
        import torch
    except ImportError as exc:
        raise ModelRunnerError("torch is required for Qwen reranking") from exc
    tokenizer, reranker = _load_reranker_model(model, quantization)
    encoded = tokenizer(
        [query] * len(passages),
        passages,
        padding=True,
        truncation=True,
        max_length=int(os.environ.get("FLUX_KB_RETRIEVAL_MAX_RERANK_PASSAGE_TOKENS", "1536")),
        return_tensors="pt",
    )
    device = next(reranker.parameters()).device
    encoded = {key: value.to(device) for key, value in encoded.items()}
    with torch.no_grad():
        output = reranker(**encoded)
    logits = output.logits
    if logits.ndim == 2 and logits.shape[-1] > 1:
        scores = logits[:, -1]
    else:
        scores = logits.reshape(-1)
    return [float(score) for score in scores.detach().cpu().tolist()]


def _load_paddleocr(model: str) -> Any:
    if model not in _PADDLE_OCR_MODELS:
        try:
            from paddleocr import PaddleOCR
        except ImportError as exc:
            raise ModelRunnerError("paddleocr is required for OCR") from exc
        kwargs: dict[str, Any] = {"lang": "en"}
        if model:
            kwargs["ocr_version"] = model
        _PADDLE_OCR_MODELS[model] = PaddleOCR(**kwargs)
    return _PADDLE_OCR_MODELS[model]


def _paddleocr_text(value: Any) -> str:
    parts: list[str] = []

    def visit(item: Any) -> None:
        if isinstance(item, str):
            text = item.strip()
            if text:
                parts.append(text)
            return
        if isinstance(item, dict):
            for key in ("text", "rec_text", "content"):
                if key in item:
                    visit(item[key])
            for key in ("res", "data", "results"):
                if key in item:
                    visit(item[key])
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
    ocr = _load_paddleocr(model)
    if hasattr(ocr, "predict"):
        result = ocr.predict(path)
    else:
        result = ocr.ocr(path, cls=True)
    return _paddleocr_text(result)


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
    os.makedirs(hf_home, exist_ok=True)
    results: list[dict[str, Any]] = []
    try:
        from huggingface_hub import snapshot_download
    except ImportError as exc:
        raise ModelRunnerError("huggingface-hub is required to pre-download model-runner models") from exc
    embedding_model = os.environ.get("FLUX_KB_RETRIEVAL_EMBEDDING_MODEL", DEFAULT_EMBEDDING_MODEL)
    for repo_id, ignore_patterns in (
        (embedding_model, ["onnx/*", "*.onnx"]),
        (os.environ.get("FLUX_KB_RETRIEVAL_RERANKER_MODEL", DEFAULT_RERANKER_MODEL), None),
        (os.environ.get("FLUX_KB_OCR_DOCUMENT_MODEL_REPO", "PaddlePaddle/PaddleOCR-VL"), None),
    ):
        path = _download_snapshot_with_retries(
            snapshot_download,
            repo_id=repo_id,
            cache_dir=hf_home,
            ignore_patterns=ignore_patterns,
        )
        results.append({"repo_id": repo_id, "path": path})
    try:
        _load_paddleocr(os.environ.get("FLUX_KB_OCR_SIMPLE_MODEL", DEFAULT_OCR_SIMPLE_MODEL))
        results.append({"repo_id": "paddleocr-runtime", "path": os.environ.get("PADDLEOCR_HOME", "")})
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
        return {"ok": True, "model": model, "text": _ocr_image_with_paddle(path, model=model)}

    @app.post("/v1/ocr/document")
    def ocr_document(payload: dict[str, Any]) -> dict[str, Any]:
        path = str(payload.get("path") or "")
        model = str(payload.get("model") or DEFAULT_OCR_DOCUMENT_MODEL)
        return {"ok": True, "model": model, "text": _ocr_image_with_paddle(path, model=model)}

    return app


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m flux_llm_kb.model_runner")
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("health")
    serve = subparsers.add_parser("serve")
    serve.add_argument("--host", default="127.0.0.1")
    serve.add_argument("--port", type=int, default=8790)
    download = subparsers.add_parser("download-models")
    download.add_argument("--models-dir", default="/models")
    args = parser.parse_args(argv)

    if args.command == "health":
        print(json.dumps(health_payload(), indent=2, sort_keys=True))
        return 0
    if args.command == "download-models":
        print(json.dumps(download_models(args.models_dir), indent=2))
        return 0
    if args.command == "serve":
        import uvicorn

        uvicorn.run(create_app(), host=args.host, port=args.port)
        return 0
    raise ValueError(args.command)


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
