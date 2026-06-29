# syntax=docker/dockerfile:1.7

FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1
ENV PIP_ROOT_USER_ACTION=ignore

WORKDIR /app

RUN apt-get update \
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
    python -m pip install --upgrade pip \
    && python -m pip install -r /tmp/requirements-docker.txt

COPY src ./src
COPY plugins ./plugins
COPY README.md ./

RUN python -m pip install --no-deps .

EXPOSE 8765

CMD ["python", "-m", "uvicorn", "flux_llm_kb.rest_api:create_app", "--factory", "--host", "0.0.0.0", "--port", "8765"]
