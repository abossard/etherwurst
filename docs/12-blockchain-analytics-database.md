# Blockchain Analytics Database: Fast Querying Beyond the Node

## The Problem

Ethereum nodes (Erigon, Geth) store blockchain data optimized for consensus and state verification — not for analytics. Querying transactions by address, analyzing token flows, or aggregating gas usage via JSON-RPC is extremely slow for anything beyond simple lookups. To build an investigation platform (like Etherscan, Dune, or our HazMeBeenScammed), we need a **dedicated analytics database** sitting alongside the node.

## TL;DR Recommendation

> **ClickHouse** is the clear winner for self-hosted Ethereum blockchain analytics. It's 10-100x faster than PostgreSQL for analytical queries, handles billions of rows, compresses terabytes to hundreds of GBs, and has a proven ecosystem for blockchain data. Deploy via the **Altinity Kubernetes Operator** on AKS with SSD-backed PVCs.

---

## Database Comparison

| Database | Type | Query Speed (OLAP) | Real-time Ingest | Scalability | Ops Complexity | Best For |
|----------|------|-------------------|-----------------|-------------|----------------|----------|
| **ClickHouse** | Columnar OLAP | ⚡ Extremely fast (10-100x PG) | Good (micro-batch) | High (distributed) | Medium | **Historical analytics, AI pipelines** |
| **Apache Druid** | Columnar OLAP | ⚡ Fast, sub-second | Excellent (streaming) | High (distributed) | High | Real-time dashboards |
| **PostgreSQL** | Row-based RDBMS | Moderate | Moderate | Good (partitioned) | Low | General purpose, Blockscout backend |
| **DuckDB** | Embedded OLAP | ⚡ Fast (single node) | Not designed for it | Single node only | Very Low | Prototyping, local analysis |
| **Apache Pinot** | Columnar OLAP | ⚡ Ultra-low latency | Excellent | High | High | User-facing real-time apps |
| **StarRocks** | MPP SQL | ⚡ Fast, complex joins | Good | High | Medium | BI workloads, complex analytics |
| **Snowflake/BigQuery** | Cloud warehouse | Fast (managed) | Batch-oriented | Unlimited | Very Low | Enterprise, pay-per-query |

### Why ClickHouse Wins for Our Use Case

1. **Speed**: Scans billions of rows per second. Sub-second queries on transaction tables with 2B+ rows
2. **Compression**: Ethereum's ~2TB uncompressed → ~200-400GB compressed on disk
3. **Columnar storage**: Only reads columns needed (e.g., `from_address`, `value` without loading all 30+ fields)
4. **SQL-compatible**: Standard SQL with powerful analytical functions (window functions, arrays, maps)
5. **Self-hosted on K8s**: Altinity operator provides production-grade Kubernetes deployment
6. **Blockchain ecosystem**: Used by Goldsky, EthGas, Migalabs (GotEth), Pinax, and many others
7. **AI/ML friendly**: Easy to export data for AI agent pipelines, supports Parquet import/export
8. **Cost-effective**: Open source, runs on commodity hardware with Azure managed SSDs

---

## Architecture: Erigon → ClickHouse Pipeline

```
┌─────────────┐     JSON-RPC      ┌──────────────┐     Insert      ┌──────────────┐
│   Erigon     │ ─────────────────→│  ETL Worker   │ ──────────────→│  ClickHouse  │
│  (Full Node) │  eth_getBlock*    │  (Cryo/ETL)   │   Batch/HTTP   │  (Analytics) │
│  Port 8545   │  eth_getReceipts  │               │                │              │
└─────────────┘                    └──────────────┘                 └──────┬───────┘
                                                                          │
                                          ┌───────────────────────────────┤
                                          │                               │
                                   ┌──────▼───────┐              ┌───────▼──────┐
                                   │  AI Agents    │              │  Grafana /   │
                                   │  (Investigate)│              │  Dashboards  │
                                   └──────────────┘              └──────────────┘
```

---

## ETL Tools: Getting Data from Erigon to ClickHouse

### Option 1: Cryo (by Paradigm) — ⭐ Recommended

The easiest and fastest tool for extracting Ethereum data into analytics-friendly formats.

- **GitHub**: https://github.com/paradigmxyz/cryo
- **Language**: Rust (fast!)
- **Output**: Parquet, CSV, JSON, or Python DataFrames
- **Datasets**: blocks, transactions, logs, traces, contracts, ERC20 transfers, and 30+ more

```bash
# Install
pip install cryo

# Extract all transactions from blocks 18M-19M into Parquet
cryo transactions -b 18000000:19000000 --rpc http://erigon:8545 -o /data/

# Extract logs (events) for specific contracts
cryo logs -b 18000000:19000000 --contract 0xdAC17F958D2ee523a2206206994597C13D831ec7 --rpc http://erigon:8545

# Then load into ClickHouse
clickhouse-client --query "INSERT INTO transactions FORMAT Parquet" < transactions.parquet
```

**Cryo + ClickHouse pipeline (Cryobase)**:
- https://github.com/LatentSpaceExplorer/cryobase
- Automated ETL: Extract → Transform → Load into ClickHouse

### Option 2: Ethereum ETL (Python)

Mature, well-documented Python toolkit. Slower than Cryo but more flexible.

- **GitHub**: https://github.com/blockchain-etl/ethereum-etl
- **Install**: `pip install ethereum-etl`

```bash
# Export blocks and transactions to CSV
ethereumetl export_blocks_and_transactions \
  --start-block 0 --end-block 500000 \
  --blocks-output blocks.csv --transactions-output transactions.csv \
  --provider-uri http://erigon:8545

# Stream new blocks continuously to PostgreSQL or Pub/Sub
ethereumetl stream --start-block latest \
  --provider-uri http://erigon:8545 \
  --output postgresql+pg8000://user:pass@host/db
```

**Postgres variant**: https://github.com/blockchain-etl/ethereum-etl-postgres

### Option 3: Custom ETL Script (for specific needs)

Use Erigon's efficient `eth_getBlockReceipts` for bulk extraction:

```python
import requests
from clickhouse_driver import Client

ch = Client('clickhouse-server')
rpc_url = 'http://erigon:8545'

def extract_block(block_num):
    # Single call gets ALL receipts for a block (much faster than per-tx)
    resp = requests.post(rpc_url, json={
        "jsonrpc": "2.0", "method": "eth_getBlockReceipts",
        "params": [hex(block_num)], "id": 1
    })
    return resp.json()['result']

# Batch insert into ClickHouse
for block_num in range(18_000_000, 19_000_000):
    receipts = extract_block(block_num)
    rows = [(r['transactionHash'], r['from'], r['to'], int(r['gasUsed'], 16))
            for r in receipts]
    ch.execute('INSERT INTO tx_receipts VALUES', rows)
```

### Option 4: GotEth (Validator Analytics)

If you need **validator/staking analytics** specifically:

- **GitHub**: https://github.com/migalabs/goteth
- **Language**: Go
- **Backend**: ClickHouse
- Indexes validator duties, rewards, balances from both CL and EL
- Powers Migalabs' Ethereum validator dashboards

---

## ClickHouse Schema for Ethereum

### Core Tables

```sql
-- Blocks
CREATE TABLE ethereum.blocks (
    number          UInt64,
    hash            FixedString(66),
    parent_hash     FixedString(66),
    timestamp       DateTime,
    miner           FixedString(42),
    gas_used        UInt64,
    gas_limit       UInt64,
    base_fee        UInt64,
    transaction_count UInt16
) ENGINE = MergeTree()
ORDER BY (timestamp, number)
PARTITION BY toYYYYMM(timestamp);

-- Transactions
CREATE TABLE ethereum.transactions (
    block_number    UInt64,
    tx_index        UInt16,
    hash            FixedString(66),
    from_address    FixedString(42),
    to_address      Nullable(FixedString(42)),
    value           UInt256,
    gas_price       UInt64,
    gas_used        UInt64,
    input_data      String,
    status          UInt8,
    timestamp       DateTime
) ENGINE = MergeTree()
ORDER BY (from_address, timestamp, block_number)
PARTITION BY toYYYYMM(timestamp);

-- ERC20 Token Transfers (from event logs)
CREATE TABLE ethereum.token_transfers (
    block_number    UInt64,
    tx_hash         FixedString(66),
    log_index       UInt16,
    token_address   FixedString(42),
    from_address    FixedString(42),
    to_address      FixedString(42),
    value           UInt256,
    timestamp       DateTime
) ENGINE = MergeTree()
ORDER BY (token_address, from_address, timestamp)
PARTITION BY toYYYYMM(timestamp);

-- Internal Transactions (traces)
CREATE TABLE ethereum.traces (
    block_number    UInt64,
    tx_hash         FixedString(66),
    trace_address   Array(UInt16),
    trace_type      LowCardinality(String),
    from_address    FixedString(42),
    to_address      Nullable(FixedString(42)),
    value           UInt256,
    gas_used        UInt64,
    input           String,
    output          String,
    error           Nullable(String),
    timestamp       DateTime
) ENGINE = MergeTree()
ORDER BY (from_address, timestamp, block_number)
PARTITION BY toYYYYMM(timestamp);
```

### Example Analytical Queries

```sql
-- Find all transactions for an address (sub-second on billions of rows!)
SELECT hash, to_address, value, timestamp
FROM ethereum.transactions
WHERE from_address = '0x742d35Cc6634C0532925a3b844Bc9e7595f2bD73'
ORDER BY timestamp DESC
LIMIT 100;

-- Top 10 gas consumers in the last 24h
SELECT from_address, sum(gas_used * gas_price) as total_gas_cost, count() as tx_count
FROM ethereum.transactions
WHERE timestamp > now() - INTERVAL 24 HOUR
GROUP BY from_address
ORDER BY total_gas_cost DESC
LIMIT 10;

-- Token transfer flow analysis (for scam investigation)
SELECT
    from_address,
    to_address,
    token_address,
    sum(value) as total_transferred,
    count() as transfer_count
FROM ethereum.token_transfers
WHERE from_address = '0xSUSPECT...' OR to_address = '0xSUSPECT...'
GROUP BY from_address, to_address, token_address
ORDER BY total_transferred DESC;

-- Trace money flow through multiple hops
WITH RECURSIVE money_flow AS (
    SELECT to_address as address, value, 1 as hop
    FROM ethereum.transactions
    WHERE from_address = '0xSCAPP...'
    UNION ALL
    SELECT t.to_address, t.value, mf.hop + 1
    FROM ethereum.transactions t
    JOIN money_flow mf ON t.from_address = mf.address
    WHERE mf.hop < 5  -- max 5 hops
)
SELECT address, sum(value) as total_received, max(hop) as max_depth
FROM money_flow
GROUP BY address
ORDER BY total_received DESC;
```

---

## Deploying ClickHouse on AKS

### Option A: Altinity Kubernetes Operator (⭐ Recommended for Production)

The Altinity operator provides CRD-based management of ClickHouse clusters.

```bash
# Add Helm repo
helm repo add altinity https://docs.altinity.com/clickhouse-operator/
helm repo update

# Install operator
helm install clickhouse-operator altinity/altinity-clickhouse-operator \
  --namespace clickhouse --create-namespace
```

Then define a `ClickHouseInstallation` CRD:

```yaml
apiVersion: clickhouse.altinity.com/v1
kind: ClickHouseInstallation
metadata:
  name: ethereum-analytics
  namespace: ethereum
spec:
  configuration:
    clusters:
      - name: analytics
        layout:
          shardsCount: 1      # Start with 1, scale later
          replicasCount: 1    # Add replicas for HA
    users:
      etherwurst/password: "secure-password-here"
      etherwurst/networks/ip: ["10.0.0.0/8"]
  defaults:
    templates:
      podTemplate: clickhouse-pod
      dataVolumeClaimTemplate: data-volume
  templates:
    podTemplates:
      - name: clickhouse-pod
        spec:
          containers:
            - name: clickhouse
              resources:
                requests:
                  cpu: "4"
                  memory: "16Gi"
                limits:
                  cpu: "8"
                  memory: "32Gi"
    volumeClaimTemplates:
      - name: data-volume
        spec:
          accessModes: ["ReadWriteOnce"]
          storageClassName: managed-csi-premium  # Azure Premium SSD
          resources:
            requests:
              storage: 500Gi  # ~500GB for compressed Ethereum data
```

### Option B: Bitnami Helm Chart (Simpler, Less Control)

```bash
helm repo add bitnami https://charts.bitnami.com/bitnami
helm install clickhouse bitnami/clickhouse \
  --namespace ethereum \
  --set persistence.size=500Gi \
  --set persistence.storageClass=managed-csi-premium \
  --set resources.requests.memory=16Gi \
  --set resources.requests.cpu=4
```

### Storage Recommendations for AKS

| Storage Class | IOPS | Throughput | Cost | Use For |
|---------------|------|-----------|------|---------|
| `managed-csi-premium` (Premium SSD v1) | Up to 20K | Up to 900 MB/s | $$ | Good default |
| `managed-csi-premiumv2` (Premium SSD v2) | Up to 80K | Up to 1.2 GB/s | $$$ | Heavy analytics |
| `managed-csi` (Standard SSD) | Up to 6K | Up to 750 MB/s | $ | Budget option |

**Recommendation**: Use `managed-csi-premium` with at least P30 (1TB) for optimal IOPS/throughput ratio.

---

## Integration with Our Stack

### How ClickHouse Fits Into Etherwurst

```
┌────────────────────────────────────────────────────────┐
│                    AKS Cluster                          │
│                                                         │
│  ┌──────────┐  ┌───────────┐  ┌──────────────────────┐ │
│  │ Erigon   │  │Lighthouse │  │   ClickHouse         │ │
│  │ (EL)     │  │ (CL)      │  │   (Analytics DB)     │ │
│  │ Port 8545│  │ Port 5052 │  │   Port 8123/9000     │ │
│  └────┬─────┘  └───────────┘  └──────────┬───────────┘ │
│       │                                   │             │
│       │  ┌────────────┐                   │             │
│       └──│ Cryo ETL   │───────────────────┘             │
│          │ (CronJob)  │                                 │
│          └────────────┘                                 │
│                                                         │
│  ┌──────────────┐  ┌────────────────┐  ┌─────────────┐ │
│  │ Otterscan    │  │ HazMeBeenScam  │  │  AI Agents  │ │
│  │ (Explorer)   │  │ (Investigation)│  │ (Analysis)  │ │
│  │ → Erigon RPC │  │ → ClickHouse   │  │ → ClickHouse│ │
│  └──────────────┘  └────────────────┘  └─────────────┘ │
└────────────────────────────────────────────────────────┘
```

### Migration Path

1. **Phase 1** (Current): Erigon + Lighthouse running, Otterscan for browsing, HazMeBeenScammed using RPC directly
2. **Phase 2** (Next): Deploy ClickHouse, run Cryo to backfill historical data, set up continuous ETL CronJob
3. **Phase 3**: Point HazMeBeenScammed analytics at ClickHouse for fast address lookups and transaction graph analysis
4. **Phase 4**: AI agents query ClickHouse for investigation reports (10-100x faster than RPC calls)

---

## Managed Alternatives (If Self-Hosting is Too Much)

| Service | What It Does | Pricing Model | Best For |
|---------|-------------|---------------|----------|
| **Dune Analytics** | SQL analytics on pre-indexed blockchain data | Free tier + paid plans | Ad-hoc research, dashboards |
| **Allium** | Enterprise real-time blockchain data pipelines | Enterprise pricing | Production data feeds |
| **Goldsky** | Streaming blockchain data to your own DB (ClickHouse!) | Usage-based | Mirror data to your infra |
| **The Graph** | Subgraph-based indexed data (GraphQL) | GRT token staking | dApp-specific queries |
| **Chainstack** | Managed nodes + enhanced APIs | Per-request | If you don't want to run Erigon |
| **Azure Databricks** | Managed Spark + Delta Lake | Compute-based | ML/AI pipelines at scale |

### Goldsky + ClickHouse (Hybrid Approach)

If backfilling historical data is too slow via Cryo, **Goldsky** can stream pre-indexed data directly into your self-hosted ClickHouse:

```
Goldsky (indexed Ethereum data) ──stream──→ Your ClickHouse on AKS
```

- Sub-second latency for new blocks
- No need to extract from your own node for historical data
- Free tier available for development

---

## Key Resources & Links

### Databases
- [ClickHouse Docs](https://clickhouse.com/docs)
- [Altinity Kubernetes Operator](https://github.com/Altinity/helm-charts)
- [Bitnami ClickHouse Helm Chart](https://artifacthub.io/packages/helm/bitnami-aks/clickhouse)
- [ClickHouse Parquet Support](https://clickhouse.com/docs/integrations/data-formats/parquet)

### ETL & Indexing Tools
- [Cryo (Paradigm)](https://github.com/paradigmxyz/cryo) — Fastest Ethereum data extraction
- [Cryobase](https://github.com/LatentSpaceExplorer/cryobase) — Cryo → ClickHouse ETL pipeline
- [Ethereum ETL](https://github.com/blockchain-etl/ethereum-etl) — Mature Python ETL toolkit
- [Ethereum ETL Postgres](https://github.com/blockchain-etl/ethereum-etl-postgres) — Direct PG loading
- [GotEth](https://github.com/migalabs/goteth) — Validator analytics → ClickHouse

### Blockchain Analytics Platforms
- [Dune Analytics](https://dune.com/) — SQL-based blockchain analytics
- [Goldsky](https://goldsky.com/) — Real-time blockchain data streaming
- [Allium](https://www.allium.so/) — Enterprise blockchain data
- [The Graph](https://thegraph.com/) — Decentralized indexing

### Benchmarks & Articles
- [Fastest Database for Analytics (Tinybird 2026)](https://www.tinybird.co/blog/fastest-database-for-analytics)
- [ClickHouse for Blockchain Analytics: Setup and Benchmarks](https://www.veritasprotocol.com/blog/clickhouse-for-blockchain-analytics-setup-and-benchmarks)
- [Leveraging ClickHouse for Real-time Analytics on Blockchain Data](https://chistadata.com/clickhouse-techniques-for-processing-large-blockchain-data/)
- [Goldsky + ClickHouse + Redpanda Architecture](https://clickhouse.com/blog/clickhouse-redpanda-architecture-with-goldsky)
- [ClickHouse on Kubernetes (duyet)](https://blog.duyet.net/2024/03/clickhouse-on-kubernetes/)
- [Erigon RPC Documentation](https://docs.erigon.tech/interacting-with-erigon/interacting-with-erigon)
- [PostgreSQL and Blockchain Data](https://reintech.io/blog/postgresql-and-blockchain-storing-data)

---

## Decision Matrix for Etherwurst

| Requirement | ClickHouse | PostgreSQL | Druid | Managed (Dune) |
|------------|-----------|-----------|-------|----------------|
| Fast address lookup (billions of txs) | ✅ Sub-second | ❌ Seconds-minutes | ✅ Sub-second | ✅ Seconds |
| Transaction graph analysis | ✅ Fast JOINs | ⚠️ Slow at scale | ⚠️ Limited joins | ✅ Pre-indexed |
| Self-hosted on AKS | ✅ Altinity operator | ✅ Easy | ⚠️ Complex | ❌ SaaS only |
| AI agent integration | ✅ SQL + Parquet export | ✅ SQL | ✅ SQL | ⚠️ API limits |
| Operational complexity | Medium | Low | High | None |
| Cost (for full Ethereum) | ~$50-100/mo storage | ~$200/mo storage | ~$150/mo | Free-$400/mo |
| Data freshness | Seconds (with ETL) | Seconds | Sub-second | Minutes-hours |

**Verdict**: Deploy **ClickHouse via Altinity Operator** on our AKS cluster, use **Cryo** for ETL from Erigon, and point our AI agents at ClickHouse for investigations.
