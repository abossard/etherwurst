# ClickHouse Ethereum ETL

Extracts Ethereum blockchain data from an Erigon archive node and loads it into ClickHouse via HTTP ingestion. Runs as a **sidecar container** inside the Erigon pod for zero-latency localhost RPC access.

## Architecture

```
┌─── Erigon Pod ──────────────────────────────┐
│                                              │
│  Erigon (RPC on localhost:8545)              │
│      │ batch JSON-RPC (16 concurrent, no     │
│      │ RPS limit) via localhost loopback      │
│      ▼                                       │
│  etl-sidecar container                       │
│      │ cryo extraction → Parquet files       │
│      │ (10K-block batches, /tmp/cryo-extract)│
│      ▼                                       │
│  HTTP POST (INSERT INTO … FORMAT Parquet)    │
│                                              │
└──────────────────────────────────────────────┘
                    │
                    ▼
          ClickHouse (4 tables)
```

The sidecar runs in `SIDECAR_MODE=true` — a continuous loop with 60s sleep between runs. cryo extracts blocks, transactions, and logs as Parquet files, which are posted directly to the ClickHouse HTTP interface. Progress is tracked in the `etl_progress` table for incremental runs.

**Key advantage**: Because the ETL runs in the same pod as Erigon, RPC calls go over `localhost` with zero network latency, enabling maximum extraction throughput.

## Files

| File | Description |
|------|-------------|
| `Dockerfile` | Multi-stage build: Rust (cryo from source) + Python runtime |
| `etl.py` | Python orchestrator — cryo extraction → ClickHouse HTTP ingestion |
| `clickhouse-schema.sql` | ClickHouse table definitions (ReplacingMergeTree) |
| `requirements.txt` | Python dependencies (pyarrow, requests, azure-identity) |

## Docker Image

The image is built by `build-deploy.sh` and pushed to ACR as `etl`:

```bash
# Build all images (API + Web + ETL) in parallel
./build-deploy.sh --build

# Or build ETL only
docker buildx build --platform linux/amd64 --provenance=false \
  --tag ${ACR_LOGIN_SERVER}/etl:latest \
  --file src/etl/Dockerfile --push .

# Local testing (ARM64)
docker build -f src/etl/Dockerfile -t etl:local .
docker run --rm --entrypoint cryo etl:local --version
```

Build takes ~4 min. The Rust stage compiles cryo from source; the Python stage installs the orchestrator dependencies.

## Deployment

The ETL runs as a **sidecar container** (`etl-sidecar`) inside the Erigon pod, defined in [`clusters/etherwurst/apps/erigon.yaml`](../../clusters/etherwurst/apps/erigon.yaml) under `extraContainers`.

- **Image**: `${ACR_LOGIN_SERVER}/etl:latest`
- **Mode**: `SIDECAR_MODE=true` — continuous loop, sleeps 60s between runs
- **RPC**: `http://localhost:8545` (pod-local Erigon, zero network hop)
- **Target**: ClickHouse
- **Datasets**: `blocks,transactions,logs` (contracts excluded)
- **Scale**: 10K blocks/batch, 16 concurrency, unlimited RPS, 25M block lookback
- **Temp storage**: 10Gi `emptyDir` volume mounted at `/tmp/cryo-extract`
- **Identity**: `etl-sa` ServiceAccount with Azure Workload Identity
- **Resources**: requests 500m CPU / 512Mi RAM, limits 4 CPU / 4Gi RAM

Image tags are updated by `build-deploy.sh --deploy` which commits the new tag to git, triggering Flux reconciliation.

## Configuration (Environment Variables)

### Required

| Variable | Description | Production Value |
|----------|-------------|------------------|
| `ERIGON_RPC_URL` | Erigon JSON-RPC endpoint | `http://localhost:8545` |
| `CH_URL` | ClickHouse HTTP endpoint | `http://clickhouse-ethereum-analytics:8123/?database=ethereum` |
| `CH_USER` | ClickHouse username | `etherwurst` |
| `CH_PASSWORD` | ClickHouse password | *(set in manifest)* |

### Sidecar / Tuning

| Variable | Production Value | Description |
|----------|------------------|-------------|
| `SIDECAR_MODE` | `true` | Run in continuous loop instead of one-shot |
| `LOOP_SLEEP_SECS` | `60` | Seconds to sleep between sidecar loop iterations |
| `MAX_BLOCKS` | `25000000` | Max blocks per run |
| `BATCH_SIZE` | `10000` | Blocks per batch |
| `CRYO_CONCURRENCY` | `16` | cryo parallel requests |
| `CRYO_CHUNK_SIZE` | `10000` | Blocks per cryo output file |
| `CRYO_RPS` | `0` | cryo requests per second (`0` = unlimited) |
| `FIRST_RUN_LOOKBACK` | `25000000` | Blocks to backfill on first run |
| `WORKER_ID` | `sidecar` | Identifier for this ETL instance |
| `EXTRACT_DATASETS` | `blocks,transactions,logs` | Datasets to extract (comma-separated) |

### Local Development

```bash
export ERIGON_RPC_URL=http://localhost:8545
export CH_URL=http://localhost:8123
export CH_USER=etherwurst
export CH_PASSWORD=etherwurst-ch-2026
python src/etl/etl.py
```

## ClickHouse Schema

Tables are created automatically by the ETL on first run. Schema is defined in `clickhouse-schema.sql`:

| Table | Engine | Order By |
|-------|--------|----------|
| `blocks` | ReplacingMergeTree | `block_number` |
| `transactions` | ReplacingMergeTree | `(block_number, transaction_index)` |
| `logs` | ReplacingMergeTree | `(block_number, log_index)` |
| `etl_progress` | ReplacingMergeTree | `dataset` |

> **Note:** The `contracts` table exists in the schema but is not extracted in production (`EXTRACT_DATASETS` does not include `contracts`).

## Cost

- **ClickHouse on AKS**: ~$15-30/month (single replica, modest resource requests)
- **Persistent storage**: ~$5-10/month for ClickHouse data volume
- **Total**: ~$20-40/month for Ethereum analytics on AKS
