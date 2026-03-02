# ═══════════════════════════════════════════════════════════════════════════════
# Ethereum ADX ETL — Optimized Docker image
#
# Multi-stage build:
#   Stage 1: Rust (latest) — compiles cryo from git main
#   Stage 2: Python (latest) — slim runtime with Kusto SDK + az CLI
#
# Build (multi-arch):
#   docker buildx build --platform linux/amd64,linux/arm64 -t adx-etl .
# ═══════════════════════════════════════════════════════════════════════════════

# ── Stage 1: Build cryo from source ──────────────────────────────────────────
FROM rust:slim-bookworm AS cryo-builder

RUN apt-get update && \
    apt-get install -y --no-install-recommends pkg-config libssl-dev git && \
    rm -rf /var/lib/apt/lists/*

RUN cargo install cryo_cli --git https://github.com/paradigmxyz/cryo.git && \
    strip /usr/local/cargo/bin/cryo

# ── Stage 2: Python runtime with all dependencies ────────────────────────────
FROM python:3-slim-bookworm

RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        ca-certificates curl libssl3 && \
    rm -rf /var/lib/apt/lists/*

COPY --from=cryo-builder /usr/local/cargo/bin/cryo /usr/local/bin/cryo

COPY src/etl/requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir -U -r /tmp/requirements.txt && \
    rm /tmp/requirements.txt

RUN pip install --no-cache-dir -U azure-cli && \
    az --version | head -1

WORKDIR /app
COPY src/etl/adx_etl.py /app/adx_etl.py

RUN cryo --version && \
    python3 -c "from azure.kusto.ingest import QueuedIngestClient; print('kusto-ingest OK')" && \
    python3 -c "from azure.kusto.data import DataFormat; print('kusto-data OK')" && \
    python3 -c "from azure.identity import DefaultAzureCredential; print('azure-identity OK')" && \
    python3 -c "import pyarrow; print(f'pyarrow {pyarrow.__version__} OK')" && \
    python3 -c "import requests; print(f'requests {requests.__version__} OK')"

ENTRYPOINT ["python3", "/app/adx_etl.py"]
