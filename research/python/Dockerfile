FROM python:3.14-slim AS build

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

WORKDIR /app

RUN apt-get update \
    && apt-get install --yes --no-install-recommends build-essential \
    && rm -rf /var/lib/apt/lists/*

COPY pyproject.toml ./
COPY src ./src
COPY configs ./configs

RUN --mount=type=cache,target=/root/.cache/pip \
    python -m pip install --prefix=/install .

FROM python:3.14-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app

RUN apt-get update \
    && apt-get install --yes --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /install /usr/local
COPY configs ./configs
# Keep the Python verification suite available in the runtime image so the
# Dockerized deployment, not only the host uv environment, is testable.
COPY tests ./tests

RUN useradd --create-home --uid 10001 quantdesk \
    && mkdir -p /app/data /app/artifacts /app/logs \
    && chown -R quantdesk:quantdesk /app

USER quantdesk

EXPOSE 8000

CMD ["python", "-m", "quantdesk_research.cli.main", "mcp", "--host", "0.0.0.0", "--port", "8000"]
