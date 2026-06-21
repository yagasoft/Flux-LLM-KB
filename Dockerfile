FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1
ENV PIP_ROOT_USER_ACTION=ignore

WORKDIR /app

COPY pyproject.toml README.md ./
COPY src ./src
COPY plugins ./plugins

RUN python -m pip install --no-cache-dir --upgrade pip \
    && python -m pip install --no-cache-dir ".[api,corpus]"

EXPOSE 8765

CMD ["python", "-m", "uvicorn", "flux_llm_kb.rest_api:create_app", "--factory", "--host", "0.0.0.0", "--port", "8765"]
