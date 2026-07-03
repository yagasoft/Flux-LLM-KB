# syntax=docker/dockerfile:1.7

ARG OLLAMA_BASE_IMAGE=ollama/ollama:latest
FROM ${OLLAMA_BASE_IMAGE}

ARG FLUX_KB_IMAGE_REVISION=""
ARG FLUX_KB_IMAGE_SOURCE=""
ARG FLUX_KB_IMAGE_CREATED=""
ARG FLUX_KB_IMAGE_VERSION=""
ARG FLUX_KB_OLLAMA_RUNTIME_FINGERPRINT=""

LABEL org.opencontainers.image.revision=${FLUX_KB_IMAGE_REVISION} \
      org.opencontainers.image.source=${FLUX_KB_IMAGE_SOURCE} \
      org.opencontainers.image.created=${FLUX_KB_IMAGE_CREATED} \
      org.opencontainers.image.version=${FLUX_KB_IMAGE_VERSION} \
      org.flux_llm_kb.ollama.runtime_fingerprint=${FLUX_KB_OLLAMA_RUNTIME_FINGERPRINT}

ENV DEBIAN_FRONTEND=noninteractive

RUN --mount=type=cache,id=flux-ollama-apt-cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,id=flux-ollama-apt-lists,target=/var/lib/apt/lists,sharing=locked \
    apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && command -v ffmpeg \
    && command -v ffprobe \
    && rm -rf /var/lib/apt/lists/*
