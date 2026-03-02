# ADX Ethereum ETL

Extracts Ethereum blockchain data from an Erigon archive node and loads it into Azure Data Explorer (ADX).

## Architecture

```
Erigon (RPC:8545)
    │ batch JSON-RPC (32 concurrent)
    ▼
cryo (Rust binary)  ─── 500-1,500 blocks/sec
    │ Parquet + Snappy compression
    ▼
Azure Blob Storage (ethereum-etl container)
    │ az storage blob upload-batch
    ▼
ADX Queued Ingestion (from blob URLs)
    │ Kusto Python SDK
    ▼
Azure Data Explorer (7 tables)
```

## Files

| File | Description |
|------|-------------|
| `Dockerfile` | Multi-stage build: Rust (cryo) + Python (orchestrator + Azure CLI) |
| `adx_etl.py` | Python orchestrator — cryo extraction → blob upload → ADX ingestion |
| `adx-schema.kql` | ADX table definitions, ingestion mappings, policies |
| `requirements.txt` | Python dependencies (Kusto SDK, pyarrow, azure-identity) |

## Docker Image

The image is built by `build-deploy.sh` and pushed to ACR alongside the app images:

```bash
# Build all images (API + Web + ETL) in parallel
./build-deploy.sh --build

# Or build ETL only
docker buildx build --platform linux/amd64 --provenance=false \
  --tag ${ACR_LOGIN_SERVER}/adx-etl:latest \
  --file src/etl/Dockerfile --push .

# Local testing (ARM64)
docker build -f src/etl/Dockerfile -t adx-etl:local .
docker run --rm --entrypoint cryo adx-etl:local --version
```

Build takes ~4 min. Rust builder compiles cryo from source; Python stage installs Kusto SDK + Azure CLI.

## Deployment (Flux GitOps)

The CronJob is deployed via Flux from `clusters/etherwurst/apps/adx-etl.yaml`:

- **Image**: `${ACR_LOGIN_SERVER}/adx-etl:latest` (substituted by Flux from cluster-config)
- **Schedule**: `0 2 * * *` (daily at 02:00 UTC)
- **Auth**: Azure Workload Identity via `adx-etl-sa` ServiceAccount
- **Namespace**: `ethereum`

Image tags are updated by `build-deploy.sh --deploy` which commits the new tag to git, triggering Flux reconciliation.

## Configuration (Environment Variables)

| Variable | Default | Description |
|----------|---------|-------------|
| `ERIGON_RPC_URL` | `http://erigon:8545` | Erigon JSON-RPC endpoint |
| `ADX_CLUSTER_URI` | (required) | ADX cluster URI |
| `ADX_DATABASE` | `ethereum` | ADX database name |
| `STORAGE_ACCOUNT_NAME` | (required) | Azure Storage account for staging |
| `STORAGE_CONTAINER` | `ethereum-etl` | Blob container name |
| `AZURE_RESOURCE_GROUP` | (required) | Resource group (for ADX start/stop) |
| `MAX_BLOCKS` | `100000` | Max blocks per run |
| `CRYO_CONCURRENCY` | `32` | cryo parallel requests |
| `CRYO_CHUNK_SIZE` | `10000` | Blocks per cryo output file |
| `CRYO_RPS` | `100` | cryo requests per second |
| `STOP_ADX_AFTER` | `false` | Stop ADX cluster after ETL completes |
| `FIRST_RUN_LOOKBACK` | `10000` | Blocks to backfill on first run |

## ADX Schema

Apply schema to the ADX cluster:

```bash
# Via Azure CLI
az kusto script create --cluster-name <ADX> --database-name ethereum \
  --resource-group <RG> --name setup-schema \
  --script-content "$(cat src/etl/adx-schema.kql)"

# Or paste into the ADX web explorer at https://<cluster>.kusto.windows.net
```

Tables: `Blocks`, `Transactions`, `Logs`, `TokenTransfers`, `Traces`, `Contracts`, `EtlProgress`

See [docs/14-adx-ethereum-analytics.md](../../docs/14-adx-ethereum-analytics.md) for the full design doc.

## Cost

- **ADX Dev/Test SKU**: ~$0.12/hr, auto-stops when idle
- **Daily ETL run**: ~2 hours = $0.24/day = **$7.20/month**
- **Blob storage**: ~$20-50/month for compressed Parquet
- **Total**: ~$30-60/month for full Ethereum analytics
