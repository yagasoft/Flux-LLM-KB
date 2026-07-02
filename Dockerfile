# syntax=docker/dockerfile:1.7

ARG FLUX_KB_DOCKER_BASE_IMAGE=python:3.12-slim
FROM ${FLUX_KB_DOCKER_BASE_IMAGE} AS runtime-deps

ARG FLUX_KB_SKIP_SYSTEM_PACKAGES=false
ARG APT_DEBIAN_MIRROR_URL=""
ARG APT_SECURITY_MIRROR_URL=""

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1
ENV PIP_ROOT_USER_ACTION=ignore
ENV LD_LIBRARY_PATH=/usr/local/lib/python3.12/site-packages/nvidia/cuda_runtime/lib:/usr/local/lib/python3.12/site-packages/nvidia/cublas/lib:/usr/local/lib/python3.12/site-packages/nvidia/cudnn/lib:/usr/local/lib/python3.12/site-packages/nvidia/nccl/lib:/usr/local/lib/python3.12/site-packages/nvidia/cufft/lib:/usr/local/lib/python3.12/site-packages/nvidia/curand/lib:/usr/local/lib/python3.12/site-packages/nvidia/cusolver/lib:/usr/local/lib/python3.12/site-packages/nvidia/cusparse/lib
ENV FLUX_KB_PADDLE_PYTHON=/opt/flux-paddle/bin/python
ENV FLUX_KB_PADDLE_LD_LIBRARY_PATH=/opt/flux-paddle/lib/python3.12/site-packages/nvidia/cuda_runtime/lib:/opt/flux-paddle/lib/python3.12/site-packages/nvidia/cublas/lib:/opt/flux-paddle/lib/python3.12/site-packages/nvidia/cudnn/lib:/opt/flux-paddle/lib/python3.12/site-packages/nvidia/nccl/lib:/opt/flux-paddle/lib/python3.12/site-packages/nvidia/cufft/lib:/opt/flux-paddle/lib/python3.12/site-packages/nvidia/curand/lib:/opt/flux-paddle/lib/python3.12/site-packages/nvidia/cusolver/lib:/opt/flux-paddle/lib/python3.12/site-packages/nvidia/cusparse/lib

WORKDIR /app

RUN --mount=type=cache,id=flux-llm-kb-apt-cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,id=flux-llm-kb-apt-lists,target=/var/lib/apt/lists,sharing=locked \
    if [ "$FLUX_KB_SKIP_SYSTEM_PACKAGES" = "true" ]; then \
        echo "Skipping system package installation; reusing packages from Docker base image."; \
    else \
        if [ -n "$APT_DEBIAN_MIRROR_URL" ]; then \
            sed -i "s|URIs: http://deb.debian.org/debian$|URIs: $APT_DEBIAN_MIRROR_URL|g" /etc/apt/sources.list.d/debian.sources; \
        fi \
        && if [ -n "$APT_SECURITY_MIRROR_URL" ]; then \
            sed -i "s|URIs: http://deb.debian.org/debian-security$|URIs: $APT_SECURITY_MIRROR_URL|g" /etc/apt/sources.list.d/debian.sources; \
        fi \
        && apt-get update \
        && apt-get install -y --no-install-recommends \
            antiword \
            binutils \
            calibre \
            catdoc \
            cpio \
            ffmpeg \
            g++ \
            gcc \
            libarchive-tools \
            libemail-address-perl \
            libemail-outlook-message-perl \
            libimage-exiftool-perl \
            librsvg2-bin \
            libreoffice \
            lz4 \
            pandoc \
            p7zip-full \
            poppler-utils \
            pst-utils \
            rpm2cpio \
            unar \
            wv \
            zstd; \
    fi

ARG PIP_INDEX_URL=""
ARG PADDLE_GPU_INDEX_URL="https://www.paddlepaddle.org.cn/packages/stable/cu126/"
ARG PYTORCH_GPU_INDEX_URL="https://download.pytorch.org/whl/cu126"
ARG PIP_OFFLINE=false
ARG PIP_DEFAULT_TIMEOUT=30
ARG PIP_RETRIES=2

COPY pyproject.toml ./

RUN python - <<'PY'
import tomllib
from pathlib import Path

config = tomllib.loads(Path("pyproject.toml").read_text(encoding="utf-8"))
optional = config["project"].get("optional-dependencies", {})

def write_requirements(path: str, extras: tuple[str, ...]) -> None:
    requirements = list(config["project"]["dependencies"])
    for extra in extras:
        requirements.extend(optional.get(extra, []))
    Path(path).write_text("\n".join(requirements) + "\n", encoding="utf-8")

write_requirements("/tmp/requirements-docker.txt", ("api", "corpus", "mcp", "processors", "asr_gpu"))
write_requirements("/tmp/requirements-paddle.txt", ("api", "ocr_paddle"))
PY

RUN --mount=type=cache,id=flux-llm-kb-pip-wheelhouse,target=/opt/flux-wheelhouse,sharing=locked \
    set -eu; \
    export PIP_CACHE_DIR=/opt/flux-wheelhouse/.pip-cache; \
    python -m pip --version; \
    python -m venv /opt/flux-paddle; \
    /opt/flux-paddle/bin/python -m pip --version; \
    mkdir -p /opt/flux-wheelhouse "$PIP_CACHE_DIR"; \
    pip_extra_index_args=""; \
    if [ -n "$PADDLE_GPU_INDEX_URL" ]; then pip_extra_index_args="$pip_extra_index_args --extra-index-url $PADDLE_GPU_INDEX_URL"; fi; \
    if [ -n "$PYTORCH_GPU_INDEX_URL" ]; then pip_extra_index_args="$pip_extra_index_args --extra-index-url $PYTORCH_GPU_INDEX_URL"; fi; \
    if [ -n "$PIP_INDEX_URL" ]; then \
        pip_index_args="--index-url $PIP_INDEX_URL"; \
    else \
        pip_index_args=""; \
    fi; \
    download_requirements() { \
        python_bin="$1"; \
        requirements="$2"; \
        if "$python_bin" -m pip download --only-binary=:all: --no-index --find-links /opt/flux-wheelhouse --dest /opt/flux-wheelhouse -r "$requirements"; then \
            return 0; \
        fi; \
        if [ "$PIP_OFFLINE" = "true" ]; then \
            return 1; \
        fi; \
        "$python_bin" -m pip download --only-binary=:all: --timeout "$PIP_DEFAULT_TIMEOUT" --retries "$PIP_RETRIES" --find-links /opt/flux-wheelhouse $pip_index_args $pip_extra_index_args --dest /opt/flux-wheelhouse -r "$requirements"; \
    }; \
    download_requirements python /tmp/requirements-docker.txt; \
    python -m pip install --no-index --find-links /opt/flux-wheelhouse -r /tmp/requirements-docker.txt; \
    download_requirements /opt/flux-paddle/bin/python /tmp/requirements-paddle.txt; \
    /opt/flux-paddle/bin/python -m pip install --no-index --find-links /opt/flux-wheelhouse -r /tmp/requirements-paddle.txt

FROM runtime-deps AS runtime

ARG FLUX_KB_IMAGE_REVISION=""
ARG FLUX_KB_IMAGE_SOURCE=""
ARG FLUX_KB_IMAGE_CREATED=""
ARG FLUX_KB_IMAGE_VERSION=""

LABEL org.opencontainers.image.revision=$FLUX_KB_IMAGE_REVISION \
      org.opencontainers.image.source=$FLUX_KB_IMAGE_SOURCE \
      org.opencontainers.image.created=$FLUX_KB_IMAGE_CREATED \
      org.opencontainers.image.version=$FLUX_KB_IMAGE_VERSION

COPY src ./src
COPY plugins ./plugins
COPY README.md ./

RUN python -m pip install --no-deps . \
    && /opt/flux-paddle/bin/python -m pip install --no-deps .

EXPOSE 8765

CMD ["python", "-m", "uvicorn", "flux_llm_kb.rest_api:create_app", "--factory", "--host", "0.0.0.0", "--port", "8765"]
