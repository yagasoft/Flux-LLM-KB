# syntax=docker/dockerfile:1.7

FROM python:3.12-slim

ARG APT_DEBIAN_MIRROR_URL=""
ARG APT_SECURITY_MIRROR_URL=""

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1
ENV PIP_ROOT_USER_ACTION=ignore

WORKDIR /app

RUN if [ -n "$APT_DEBIAN_MIRROR_URL" ]; then \
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
        libarchive-tools \
        libemail-address-perl \
        libemail-outlook-message-perl \
        libimage-exiftool-perl \
        libreoffice \
        lz4 \
        pandoc \
        p7zip-full \
        poppler-utils \
        pst-utils \
        rpm2cpio \
        tesseract-ocr \
        tesseract-ocr-eng \
        unar \
        wv \
        zstd \
    && rm -rf /var/lib/apt/lists/*

ARG PIP_INDEX_URL=""
ARG PIP_DEFAULT_TIMEOUT=30
ARG PIP_RETRIES=2

COPY pyproject.toml ./

RUN python - <<'PY' > /tmp/requirements-docker.txt
import tomllib
from pathlib import Path

config = tomllib.loads(Path("pyproject.toml").read_text(encoding="utf-8"))
requirements = list(config["project"]["dependencies"])
optional = config["project"].get("optional-dependencies", {})
for extra in ("api", "corpus", "processors", "gpu"):
    requirements.extend(optional.get(extra, []))
Path("/tmp/requirements-docker.txt").write_text(
    "\n".join(requirements) + "\n",
    encoding="utf-8",
)
PY

RUN --mount=type=cache,target=/root/.cache/pip \
    python -m pip install --timeout "$PIP_DEFAULT_TIMEOUT" --retries "$PIP_RETRIES" --upgrade pip \
    && if [ -n "$PIP_INDEX_URL" ]; then \
        python -m pip install --timeout "$PIP_DEFAULT_TIMEOUT" --retries "$PIP_RETRIES" --index-url "$PIP_INDEX_URL" -r /tmp/requirements-docker.txt; \
    else \
        python -m pip install --timeout "$PIP_DEFAULT_TIMEOUT" --retries "$PIP_RETRIES" -r /tmp/requirements-docker.txt; \
    fi

COPY src ./src
COPY plugins ./plugins
COPY README.md ./

RUN python -m pip install --no-deps .

EXPOSE 8765

CMD ["python", "-m", "uvicorn", "flux_llm_kb.rest_api:create_app", "--factory", "--host", "0.0.0.0", "--port", "8765"]
