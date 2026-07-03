from __future__ import annotations

import argparse
import base64
import gc
from dataclasses import dataclass
import json
import os
import tempfile
import threading
import time
from typing import Any, Iterable
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin
from urllib.request import Request, urlopen
from pathlib import Path

from .gpu_scheduler import GpuLeaseRejected, GpuLeaseTimeout, GpuModelResidency, get_gpu_scheduler, task_profile
from .onnxruntime_logging import configure_onnxruntime_logging


DEFAULT_MODEL_RUNNER_BASE_URL = "http://127.0.0.1:8790"
DEFAULT_EMBEDDING_MODEL = "Snowflake/snowflake-arctic-embed-l-v2.0"
DEFAULT_RERANKER_MODEL = "Qwen/Qwen3-Reranker-4B"
DEFAULT_RERANKER_AWQ_MODEL = "drawais/Qwen3-Reranker-4B-AWQ-INT4"
DEFAULT_RERANKER_QUANTIZATION = "awq_int4"
DEFAULT_OCR_SIMPLE_MODEL = "PP-OCRv5"
DEFAULT_OCR_DOCUMENT_MODEL = "PaddleOCR-VL"
DEFAULT_MODEL_RUNNER_TIMEOUT_SECONDS = 600
DEFAULT_PADDLEX_CACHE_HOME = "/root/.paddleocr/paddlex"
os.environ.setdefault("PADDLE_PDX_CACHE_HOME", DEFAULT_PADDLEX_CACHE_HOME)
os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")
os.environ.setdefault("PADDLE_PDX_MODEL_SOURCE", "bos")
_EMBEDDING_MODELS: dict[str, Any] = {}
_RERANKER_MODELS: dict[tuple[str, str, str], Any] = {}
_PADDLE_OCR_MODELS: dict[str, Any] = {}
_PADDLE_OCR_VL_MODELS: dict[str, Any] = {}
_PADDLE_OCR_FACTORY_IDS: dict[str, int] = {}
_PADDLE_OCR_VL_FACTORY_IDS: dict[str, int] = {}
_LOCKS_GUARD = threading.Lock()
_EMBEDDING_MODEL_LOCKS: dict[str, threading.Lock] = {}
_EMBEDDING_ENCODE_LOCKS: dict[str, threading.Lock] = {}
_RERANKER_MODEL_LOCKS: dict[tuple[str, str, str], threading.Lock] = {}
_RERANKER_PREDICT_LOCKS: dict[tuple[str, str, str], threading.Lock] = {}
_PADDLE_OCR_MODEL_LOCKS: dict[str, threading.Lock] = {}
_PADDLE_OCR_VL_MODEL_LOCKS: dict[str, threading.Lock] = {}


class ModelRunnerError(RuntimeError):
    pass


class ModelRunnerBusy(ModelRunnerError):
    def __init__(self, message: str, *, retry_after_seconds: float = 1.0) -> None:
        super().__init__(message)
        self.retry_after_seconds = max(0.1, float(retry_after_seconds))


@dataclass(frozen=True)
class RerankerQuantizationProfile:
    requested_quantization: str
    quantization: str
    backend: str
    model: str
    load_model: str
    awq_model: str


_RERANKER_QUANTIZATION_ALIASES = {
    "int4_awq": "awq_int4",
    "awq": "awq_int4",
    "int4": "nf4_4bit",
    "4bit": "nf4_4bit",
}
_RERANKER_QUANTIZATION_CHOICES = ("awq_int4", "nf4_4bit", "fp16")


def normalize_reranker_quantization(value: Any) -> str:
    requested = str(value or DEFAULT_RERANKER_QUANTIZATION).strip().lower()
    canonical = _RERANKER_QUANTIZATION_ALIASES.get(requested, requested)
    if canonical not in _RERANKER_QUANTIZATION_CHOICES:
        choices = ", ".join((*_RERANKER_QUANTIZATION_CHOICES, *_RERANKER_QUANTIZATION_ALIASES))
        raise ValueError(f"value must be one of: {choices}")
    return canonical


def resolve_reranker_quantization(
    quantization: Any,
    *,
    model: str | None = None,
    awq_model: str | None = None,
) -> RerankerQuantizationProfile:
    requested = str(quantization or DEFAULT_RERANKER_QUANTIZATION).strip() or DEFAULT_RERANKER_QUANTIZATION
    canonical = normalize_reranker_quantization(requested)
    base_model = str(model or DEFAULT_RERANKER_MODEL)
    resolved_awq_model = str(
        awq_model
        or os.environ.get("FLUX_KB_RETRIEVAL_RERANKER_AWQ_MODEL")
        or DEFAULT_RERANKER_AWQ_MODEL
    )
    if canonical == "awq_int4":
        return RerankerQuantizationProfile(
            requested_quantization=requested,
            quantization=canonical,
            backend="compressed_tensors_awq",
            model=base_model,
            load_model=resolved_awq_model,
            awq_model=resolved_awq_model,
        )
    if canonical == "nf4_4bit":
        return RerankerQuantizationProfile(
            requested_quantization=requested,
            quantization=canonical,
            backend="bitsandbytes_nf4",
            model=base_model,
            load_model=base_model,
            awq_model=resolved_awq_model,
        )
    return RerankerQuantizationProfile(
        requested_quantization=requested,
        quantization=canonical,
        backend="torch_fp16",
        model=base_model,
        load_model=base_model,
        awq_model=resolved_awq_model,
    )


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

    def rerank(
        self,
        query: str,
        passages: Iterable[str],
        *,
        model: str,
        quantization: str,
        awq_model: str | None = None,
    ) -> list[float]:
        request_payload = {
            "model": model,
            "quantization": quantization,
            "query": query,
            "passages": list(passages),
        }
        if awq_model:
            request_payload["awq_model"] = awq_model
        payload = self._post_json("/v1/rerank", request_payload)
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

    def score(
        self,
        query: str,
        passages: list[str],
        *,
        model: str,
        quantization: str,
        awq_model: str | None = None,
    ) -> list[float]:
        return self.client.rerank(query, passages, model=model, quantization=quantization, awq_model=awq_model)


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
    except HTTPError as exc:  # pragma: no cover - network-specific
        _raise_model_runner_http_error(exc)
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
        _raise_model_runner_payload_error(response_payload)
        raise ModelRunnerError(str(response_payload.get("message") or "model-runner request failed"))
    return response_payload


def _raise_model_runner_http_error(exc: HTTPError) -> None:
    try:
        raw = exc.read().decode("utf-8")
        payload = json.loads(raw)
    except Exception:
        payload = {}
    if isinstance(payload, dict):
        detail = payload.get("detail")
        if isinstance(detail, dict) and detail.get("code") == "gpu.scheduler_busy":
            raise ModelRunnerBusy(
                str(detail.get("message") or "model-runner GPU scheduler is busy"),
                retry_after_seconds=float(detail.get("retry_after_seconds") or 1.0),
            ) from exc
        if isinstance(detail, str):
            raise ModelRunnerError(detail) from exc
        _raise_model_runner_payload_error(payload)
    raise ModelRunnerError(str(exc)) from exc


def _raise_model_runner_payload_error(payload: dict[str, Any]) -> None:
    detail = payload.get("detail")
    if isinstance(detail, dict) and detail.get("code") == "gpu.scheduler_busy":
        raise ModelRunnerBusy(
            str(detail.get("message") or "model-runner GPU scheduler is busy"),
            retry_after_seconds=float(detail.get("retry_after_seconds") or 1.0),
        )


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
    reranker_model = os.environ.get("FLUX_KB_RETRIEVAL_RERANKER_MODEL", DEFAULT_RERANKER_MODEL)
    reranker_profile = resolve_reranker_quantization(
        os.environ.get("FLUX_KB_RETRIEVAL_RERANKER_QUANTIZATION", DEFAULT_RERANKER_QUANTIZATION),
        model=reranker_model,
    )
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
        "reranker_model": reranker_profile.model,
        "reranker_awq_model": reranker_profile.awq_model,
        "reranker_load_model": reranker_profile.load_model,
        "reranker_quantization": reranker_profile.quantization,
        "reranker_requested_quantization": reranker_profile.requested_quantization,
        "reranker_quantization_backend": reranker_profile.backend,
        "ocr_engine": os.environ.get("FLUX_KB_OCR_ENGINE", "paddleocr"),
        "ocr_simple_model": os.environ.get("FLUX_KB_OCR_SIMPLE_MODEL", DEFAULT_OCR_SIMPLE_MODEL),
        "ocr_document_model": os.environ.get("FLUX_KB_OCR_DOCUMENT_MODEL", DEFAULT_OCR_DOCUMENT_MODEL),
        "models_dir": os.environ.get("FLUX_KB_MODEL_RUNNER_MODELS_DIR", "/models"),
    }
    try:
        payload["gpu_scheduler"] = get_gpu_scheduler().status()
    except Exception as exc:
        payload["gpu_scheduler"] = {"status": "unavailable", "error": str(exc)}
    if resolved_role != "paddle-runner" and _paddle_runner_base_url():
        payload["paddle_runner_base_url"] = _paddle_runner_base_url()
        payload["paddle_runner"] = paddle_runner
    return payload


def _load_embedding_model(model: str) -> Any:
    lock = _named_lock(_EMBEDDING_MODEL_LOCKS, model)
    with lock:
        if model in _EMBEDDING_MODELS:
            return _EMBEDDING_MODELS[model]
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
        _record_resident_model("embedding", model, _EMBEDDING_MODELS[model])
    return _EMBEDDING_MODELS[model]


def _embed_with_sentence_transformers(texts: list[str], *, model: str, dimensions: int) -> list[list[float]]:
    if not texts:
        return []
    if model in _EMBEDDING_MODELS:
        _record_model_residency_state("embedding", model, resident=True)
    profile = task_profile("embedding", model_id=model, component=_scheduler_component(), exclusive=False, share_group="embedding")
    with get_gpu_scheduler().acquire(profile):
        encoder = _load_embedding_model(model)
        with _named_lock(_EMBEDDING_ENCODE_LOCKS, model):
            vectors = encoder.encode(texts, normalize_embeddings=True, convert_to_numpy=True)
    result = [[float(value) for value in vector] for vector in vectors.tolist()]
    for vector in result:
        if len(vector) != dimensions:
            raise ModelRunnerError(f"embedding dimension mismatch: expected {dimensions}, got {len(vector)}")
    return result


def _load_reranker_model(model: str, quantization: str, *, awq_model: str | None = None) -> Any:
    profile = resolve_reranker_quantization(quantization, model=model, awq_model=awq_model)
    cache_key = (profile.model, profile.quantization, profile.load_model)
    lock = _named_lock(_RERANKER_MODEL_LOCKS, cache_key)
    with lock:
        if cache_key in _RERANKER_MODELS:
            return _RERANKER_MODELS[cache_key]
        if profile.quantization == "awq_int4":
            try:
                import compressed_tensors  # noqa: F401
                import torch
                from transformers import AutoModelForCausalLM, AutoTokenizer
            except ImportError as exc:
                raise ModelRunnerError("compressed-tensors, transformers, and torch are required for AWQ Qwen reranking") from exc
            tokenizer = AutoTokenizer.from_pretrained(profile.load_model, trust_remote_code=True, padding_side="left")
            causal_model = AutoModelForCausalLM.from_pretrained(
                profile.load_model,
                device_map="auto",
                trust_remote_code=True,
            ).eval()
            _RERANKER_MODELS[cache_key] = _QwenCausalLMReranker(
                causal_model,
                tokenizer,
                torch_module=torch,
                profile=profile,
                max_length=int(os.environ.get("FLUX_KB_RETRIEVAL_MAX_RERANK_PASSAGE_TOKENS", "1536")),
            )
            _record_resident_model("rerank", profile.load_model, _RERANKER_MODELS[cache_key])
            return _RERANKER_MODELS[cache_key]
        try:
            import torch
            from sentence_transformers import CrossEncoder
        except ImportError as exc:
            raise ModelRunnerError("sentence-transformers and torch are required for Qwen reranking") from exc
        model_kwargs: dict[str, Any] = {
            "trust_remote_code": True,
        }
        cross_encoder_model_kwargs: dict[str, Any] = {"device_map": "auto"}
        if profile.quantization == "nf4_4bit":
            try:
                from transformers import BitsAndBytesConfig
            except ImportError as exc:
                raise ModelRunnerError("bitsandbytes-compatible transformers is required for NF4 Qwen reranking") from exc
            cross_encoder_model_kwargs["quantization_config"] = BitsAndBytesConfig(
                load_in_4bit=True,
                bnb_4bit_compute_dtype=torch.float16,
                bnb_4bit_quant_type="nf4",
                bnb_4bit_use_double_quant=True,
            )
        elif profile.quantization == "fp16":
            cross_encoder_model_kwargs["torch_dtype"] = torch.float16
        model_kwargs["model_kwargs"] = cross_encoder_model_kwargs
        model_kwargs["max_length"] = int(os.environ.get("FLUX_KB_RETRIEVAL_MAX_RERANK_PASSAGE_TOKENS", "1536"))
        reranker = CrossEncoder(profile.load_model, **model_kwargs)
        _attach_reranker_profile(reranker, profile)
        _RERANKER_MODELS[cache_key] = reranker
        _record_resident_model("rerank", profile.load_model, reranker)
    return _RERANKER_MODELS[cache_key]


def _attach_reranker_profile(reranker: Any, profile: RerankerQuantizationProfile) -> None:
    try:
        reranker.profile = profile
        reranker.quantization = profile.quantization
        reranker.requested_quantization = profile.requested_quantization
        reranker.quantization_backend = profile.backend
        reranker.load_model = profile.load_model
    except Exception:
        pass


class _QwenCausalLMReranker:
    def __init__(
        self,
        model: Any,
        tokenizer: Any,
        *,
        torch_module: Any,
        profile: RerankerQuantizationProfile,
        max_length: int,
    ) -> None:
        self.model = model
        self.tokenizer = tokenizer
        self.torch = torch_module
        self.profile = profile
        self.quantization = profile.quantization
        self.requested_quantization = profile.requested_quantization
        self.quantization_backend = profile.backend
        self.load_model = profile.load_model
        self.max_length = max(1, int(max_length))
        self.token_false_id = int(tokenizer.convert_tokens_to_ids("no"))
        self.token_true_id = int(tokenizer.convert_tokens_to_ids("yes"))
        prefix = (
            "<|im_start|>system\n"
            'Judge whether the Document meets the requirements based on the Query and the Instruct provided. Note that the answer can only be "yes" or "no".'
            "<|im_end|>\n<|im_start|>user\n"
        )
        suffix = "<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n"
        self.prefix_tokens = tokenizer.encode(prefix, add_special_tokens=False)
        self.suffix_tokens = tokenizer.encode(suffix, add_special_tokens=False)
        try:
            tokenizer.deprecation_warnings["Asking-to-pad-a-fast-tokenizer"] = True
        except Exception:
            pass

    def predict(self, pairs: Iterable[tuple[str, str]]) -> list[float]:
        pair_list = [(str(query or ""), str(document or "")) for query, document in pairs]
        if not pair_list:
            return []
        inputs = self._process_inputs(pair_list)
        with self.torch.no_grad():
            logits = self.model(**inputs).logits[:, -1, :]
            true_vector = logits[:, self.token_true_id]
            false_vector = logits[:, self.token_false_id]
            scores = self.torch.stack([false_vector, true_vector], dim=1)
            scores = self.torch.nn.functional.log_softmax(scores, dim=1)
            return [float(score) for score in scores[:, 1].exp().tolist()]

    def _process_inputs(self, pairs: list[tuple[str, str]]) -> Any:
        task = "Given a web search query, retrieve relevant passages that answer the query"
        texts = [
            "<Instruct>: {instruction}\n<Query>: {query}\n<Document>: {document}".format(
                instruction=task,
                query=query,
                document=document,
            )
            for query, document in pairs
        ]
        max_pair_length = max(1, self.max_length - len(self.prefix_tokens) - len(self.suffix_tokens))
        inputs = self.tokenizer(
            texts,
            padding=False,
            truncation="longest_first",
            return_attention_mask=False,
            max_length=max_pair_length,
        )
        inputs["input_ids"] = [self.prefix_tokens + input_ids + self.suffix_tokens for input_ids in inputs["input_ids"]]
        inputs = self.tokenizer.pad(inputs, padding=True, return_tensors="pt")
        device = getattr(self.model, "device", None)
        if device is not None and hasattr(inputs, "to"):
            return inputs.to(device)
        if device is not None and isinstance(inputs, dict):
            return {key: value.to(device) if hasattr(value, "to") else value for key, value in inputs.items()}
        return inputs


def _rerank_with_transformers(
    query: str,
    passages: list[str],
    *,
    model: str,
    quantization: str,
    awq_model: str | None = None,
) -> list[float]:
    if not passages:
        return []
    resolved_profile = resolve_reranker_quantization(quantization, model=model, awq_model=awq_model)
    lease_profile = task_profile("rerank", model_id=resolved_profile.load_model, component=_scheduler_component())
    cache_key = (resolved_profile.model, resolved_profile.quantization, resolved_profile.load_model)
    if cache_key in _RERANKER_MODELS:
        _record_model_residency_state("rerank", resolved_profile.load_model, resident=True)
    with get_gpu_scheduler().acquire(lease_profile):
        reranker = _load_reranker_model(model, quantization, awq_model=awq_model)
        with _named_lock(_RERANKER_PREDICT_LOCKS, cache_key):
            scores = reranker.predict([(query, passage) for passage in passages])
    return [float(score) for score in scores]


def _load_paddleocr(model: str) -> Any:
    lock = _named_lock(_PADDLE_OCR_MODEL_LOCKS, model)
    with lock:
        _configure_optional_onnxruntime_logging()
        try:
            from paddleocr import PaddleOCR
        except ImportError as exc:
            raise ModelRunnerError("paddleocr is required for OCR") from exc
        factory_id = id(PaddleOCR)
        if model in _PADDLE_OCR_MODELS and _PADDLE_OCR_FACTORY_IDS.get(model) == factory_id:
            return _PADDLE_OCR_MODELS[model]
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
        _PADDLE_OCR_FACTORY_IDS[model] = factory_id
        _record_resident_model("ocr_image", model, _PADDLE_OCR_MODELS[model])
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


def _configure_optional_onnxruntime_logging() -> None:
    try:
        configure_onnxruntime_logging()
    except ModuleNotFoundError:
        pass


def _load_paddleocr_vl(model: str) -> Any:
    lock = _named_lock(_PADDLE_OCR_VL_MODEL_LOCKS, model)
    with lock:
        _configure_optional_onnxruntime_logging()
        try:
            from paddlex import create_pipeline
        except ImportError as exc:
            raise ModelRunnerError("paddlex[ocr] is required for PaddleOCR-VL document OCR") from exc
        factory_id = id(create_pipeline)
        if model in _PADDLE_OCR_VL_MODELS and _PADDLE_OCR_VL_FACTORY_IDS.get(model) == factory_id:
            return _PADDLE_OCR_VL_MODELS[model]
        _PADDLE_OCR_VL_MODELS[model] = create_pipeline(
            pipeline=model,
            device=_paddle_device(),
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
        )
        _PADDLE_OCR_VL_FACTORY_IDS[model] = factory_id
        _record_resident_model("ocr_document", model, _PADDLE_OCR_VL_MODELS[model])
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
    if model in _PADDLE_OCR_MODELS:
        _record_model_residency_state("ocr_image", model, resident=True)
    profile = task_profile("ocr_image", model_id=model, component=_scheduler_component())
    with get_gpu_scheduler().acquire(profile):
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
        if model in _PADDLE_OCR_VL_MODELS:
            _record_model_residency_state("ocr_document", model, resident=True)
        profile = task_profile("ocr_document", model_id=model, component=_scheduler_component())
        with get_gpu_scheduler().acquire(profile):
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
    reranker_model = os.environ.get("FLUX_KB_RETRIEVAL_RERANKER_MODEL", DEFAULT_RERANKER_MODEL)
    reranker_profile = resolve_reranker_quantization(
        os.environ.get("FLUX_KB_RETRIEVAL_RERANKER_QUANTIZATION", DEFAULT_RERANKER_QUANTIZATION),
        model=reranker_model,
    )
    for repo_id, ignore_patterns in (
        (embedding_model, ["onnx/*", "*.onnx"]),
        (reranker_profile.load_model, None),
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
        from fastapi import FastAPI, HTTPException
    except ImportError as exc:  # pragma: no cover - deployment-only
        raise RuntimeError("fastapi is required to serve the model-runner") from exc

    app = FastAPI(title="Flux model-runner")

    @app.get("/health")
    def health() -> dict[str, Any]:
        return health_payload()

    @app.get("/v1/gpu/status")
    def gpu_status() -> dict[str, Any]:
        return get_gpu_scheduler().status()

    @app.post("/v1/gpu/unload")
    def gpu_unload(payload: dict[str, Any]) -> dict[str, Any]:
        try:
            return _unload_resident_model(
                str(payload.get("task_type") or ""),
                str(payload.get("model_id") or ""),
            )
        except ValueError as exc:
            raise HTTPException(status_code=400, detail=str(exc)) from exc

    @app.post("/v1/embeddings")
    def embeddings(payload: dict[str, Any]) -> dict[str, Any]:
        try:
            model = str(payload.get("model") or DEFAULT_EMBEDDING_MODEL)
            dimensions = int(payload.get("dimensions") or 1024)
            texts = _embedding_texts_from_payload(payload)
            vectors = _embed_with_sentence_transformers(texts, model=model, dimensions=dimensions)
            return {
                "ok": True,
                "model": model,
                "dimensions": dimensions,
                "vectors": vectors,
                "data": [{"object": "embedding", "index": index, "embedding": vector} for index, vector in enumerate(vectors)],
                "object": "list",
            }
        except ValueError as exc:
            raise HTTPException(status_code=400, detail=str(exc)) from exc
        except GpuLeaseTimeout as exc:
            raise HTTPException(status_code=429, detail=_gpu_busy_detail(exc)) from exc
        except GpuLeaseRejected as exc:
            raise HTTPException(status_code=503, detail={"code": "gpu.scheduler_rejected", "message": str(exc), "retryable": False}) from exc

    @app.post("/v1/rerank")
    def rerank(payload: dict[str, Any]) -> dict[str, Any]:
        try:
            passages = payload.get("passages") if isinstance(payload.get("passages"), list) else []
            model = str(payload.get("model") or DEFAULT_RERANKER_MODEL)
            quantization = str(payload.get("quantization") or DEFAULT_RERANKER_QUANTIZATION)
            awq_model = payload.get("awq_model") or payload.get("reranker_awq_model")
            profile = resolve_reranker_quantization(quantization, model=model, awq_model=str(awq_model) if awq_model else None)
            scores = _rerank_with_transformers(
                str(payload.get("query") or ""),
                [str(passage or "") for passage in passages],
                model=model,
                quantization=quantization,
                awq_model=profile.awq_model,
            )
        except GpuLeaseTimeout as exc:
            raise HTTPException(status_code=429, detail=_gpu_busy_detail(exc)) from exc
        except GpuLeaseRejected as exc:
            raise HTTPException(status_code=503, detail={"code": "gpu.scheduler_rejected", "message": str(exc), "retryable": False}) from exc
        return {
            "ok": True,
            "model": profile.model,
            "load_model": profile.load_model,
            "quantization": profile.quantization,
            "requested_quantization": profile.requested_quantization,
            "quantization_backend": profile.backend,
            "reranker_model": profile.model,
            "reranker_awq_model": profile.awq_model,
            "reranker_load_model": profile.load_model,
            "reranker_quantization": profile.quantization,
            "reranker_requested_quantization": profile.requested_quantization,
            "reranker_quantization_backend": profile.backend,
            "scores": scores,
        }

    @app.post("/v1/ocr/image")
    def ocr_image(payload: dict[str, Any]) -> dict[str, Any]:
        path = str(payload.get("path") or "")
        model = str(payload.get("model") or DEFAULT_OCR_SIMPLE_MODEL)
        if _paddle_runner_base_url():
            return _proxy_paddle_request("/v1/ocr/image", {**payload, "model": model})
        try:
            return {"ok": True, "model": model, "text": _with_ocr_input_path(payload, lambda input_path: _ocr_image_with_paddle(str(input_path or path), model=model))}
        except GpuLeaseTimeout as exc:
            raise HTTPException(status_code=429, detail=_gpu_busy_detail(exc)) from exc

    @app.post("/v1/ocr/document")
    def ocr_document(payload: dict[str, Any]) -> dict[str, Any]:
        path = str(payload.get("path") or "")
        model = str(payload.get("model") or DEFAULT_OCR_DOCUMENT_MODEL)
        if _paddle_runner_base_url():
            return _proxy_paddle_request("/v1/ocr/document", {**payload, "model": model})
        try:
            return {"ok": True, "model": model, "text": _with_ocr_input_path(payload, lambda input_path: _ocr_document_with_paddle(str(input_path or path), model=model))}
        except GpuLeaseTimeout as exc:
            raise HTTPException(status_code=429, detail=_gpu_busy_detail(exc)) from exc

    return app


def _embedding_texts_from_payload(payload: dict[str, Any]) -> list[str]:
    if "texts" in payload:
        texts = payload.get("texts")
        if not isinstance(texts, list):
            raise ValueError("embedding texts must be a list of strings")
        if any(not isinstance(text, str) for text in texts):
            raise ValueError("embedding texts must be a list of strings")
        return list(texts)
    if "input" in payload:
        value = payload.get("input")
        if isinstance(value, str):
            return [value]
        if isinstance(value, list) and all(isinstance(text, str) for text in value):
            return list(value)
        raise ValueError("embedding input must be a string or list of strings")
    raise ValueError("embedding request requires texts or input")


def _gpu_busy_detail(exc: GpuLeaseTimeout) -> dict[str, Any]:
    return {
        "code": "gpu.scheduler_busy",
        "message": str(exc),
        "retryable": True,
        "retry_after_seconds": float(exc.retry_after_seconds),
    }


def _scheduler_component() -> str:
    return os.environ.get("FLUX_KB_MODEL_RUNNER_ROLE", "model-runner")


def _named_lock(pool: dict[Any, threading.Lock], key: Any) -> threading.Lock:
    with _LOCKS_GUARD:
        lock = pool.get(key)
        if lock is None:
            lock = threading.Lock()
            pool[key] = lock
        return lock


def _record_resident_model(task_type: str, model_id: str, _model: Any) -> None:
    _record_model_residency_state(task_type, model_id, resident=True)


def _record_model_residency_state(task_type: str, model_id: str, *, resident: bool) -> None:
    try:
        profile = task_profile(task_type, model_id=model_id, component=_scheduler_component())
        get_gpu_scheduler().record_model_residency(
            GpuModelResidency(
                model_id=model_id,
                task_type=task_type,
                estimated_vram_mb=profile.estimated_vram_mb,
                resident=resident,
                last_used_at=time.time(),
                metadata={"component": _scheduler_component()},
            )
        )
    except Exception:
        pass


def _unload_resident_model(task_type: str, model_id: str) -> dict[str, Any]:
    task = str(task_type or "").strip()
    model = str(model_id or "").strip()
    if not task:
        raise ValueError("task_type is required")
    if not model:
        raise ValueError("model_id is required")
    removed: list[Any] = []
    if task == "embedding":
        lock = _named_lock(_EMBEDDING_MODEL_LOCKS, model)
        with lock:
            if model in _EMBEDDING_MODELS:
                removed.append(_EMBEDDING_MODELS.pop(model))
    elif task == "rerank":
        keys = [key for key in list(_RERANKER_MODELS) if key[2] == model]
        for key in keys:
            lock = _named_lock(_RERANKER_MODEL_LOCKS, key)
            with lock:
                if key in _RERANKER_MODELS:
                    removed.append(_RERANKER_MODELS.pop(key))
    elif task == "ocr_image":
        lock = _named_lock(_PADDLE_OCR_MODEL_LOCKS, model)
        with lock:
            if model in _PADDLE_OCR_MODELS:
                removed.append(_PADDLE_OCR_MODELS.pop(model))
            _PADDLE_OCR_FACTORY_IDS.pop(model, None)
    elif task == "ocr_document":
        lock = _named_lock(_PADDLE_OCR_VL_MODEL_LOCKS, model)
        with lock:
            if model in _PADDLE_OCR_VL_MODELS:
                removed.append(_PADDLE_OCR_VL_MODELS.pop(model))
            _PADDLE_OCR_VL_FACTORY_IDS.pop(model, None)
    else:
        raise ValueError(f"unsupported GPU unload task_type: {task}")
    unloaded = bool(removed)
    removed.clear()
    _record_model_residency_state(task, model, resident=False)
    _release_gpu_memory()
    return {"ok": True, "task_type": task, "model_id": model, "unloaded": unloaded, "resident": False}


def _release_gpu_memory() -> None:
    gc.collect()
    try:
        import torch

        if hasattr(torch, "cuda") and torch.cuda.is_available():
            torch.cuda.empty_cache()
            if hasattr(torch.cuda, "ipc_collect"):
                torch.cuda.ipc_collect()
    except Exception:
        pass
    try:
        import paddle

        empty_cache = getattr(getattr(paddle, "device", None), "cuda", None)
        if empty_cache is not None and hasattr(empty_cache, "empty_cache"):
            empty_cache.empty_cache()
    except Exception:
        pass


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
