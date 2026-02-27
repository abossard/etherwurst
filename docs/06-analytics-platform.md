# 05 — Analytics Platform: Querying Blockchain Data at Scale

## The Problem

Once you've exported blockchain data (via cryo, ethereum-etl, or Blockscout), you need a way to:
- Run complex SQL queries across billions of rows (all Ethereum transactions)
- Join on-chain data with off-chain data (labels, prices, protocol metadata)
- Build dashboards and visualizations
- Feed data into ML/AI pipelines

---

## Option Comparison

| | **Azure Databricks** | **ClickHouse** | **PostgreSQL + TimescaleDB** |
|---|---|---|---|
| **Type** | Managed Spark + Delta Lake | Column-store OLAP DB | Row-store + time-series extension |
| **Query Speed** | Fast on large datasets | Extremely fast for aggregations | Good for small-medium datasets |
| **Storage** | Azure Blob (cheap, infinite) | Local SSD (fast but costly) | Local SSD |
| **Scale** | Petabytes, auto-scaling | Terabytes | Hundreds of GB |
| **ML/AI Built-in** | ✅ MLflow, Spark ML, LLM support | ❌ | ❌ |
| **Cost** | Pay per compute-hour | Self-managed on AKS | Self-managed on AKS |
| **Best For** | Full analytics + ML platform | Fast dashboards, Grafana | Blockscout backend, simple queries |

---

## ⭐ Recommended: Azure Databricks

For this project, **Databricks is the clear winner** because:

1. **Storage is dirt cheap** — Delta Lake on Azure Blob Storage costs ~$0.02/GB/month. 1TB of Parquet = ~$20/month
2. **Compute scales to zero** — Only pay when running queries or jobs
3. **Native ML/AI support** — Train models, run LLMs, build agent pipelines directly on your data
4. **Spark SQL** — Query billions of rows with standard SQL
5. **Unity Catalog** — Govern and share data securely
6. **Notebook interface** — Great for exploratory analysis

### Setup: Databricks + cryo Pipeline

```
cryo (on AKS) → Parquet → Azure Blob Storage → Databricks (Delta Lake)
```

#### Step 1: Create Azure Databricks Workspace

```bash
# Create Databricks workspace
az databricks workspace create \
  --resource-group etherwurst-rg \
  --name etherwurst-databricks \
  --location westeurope \
  --sku standard

# Create storage account for Delta Lake
az storage account create \
  --name etherwurstdata \
  --resource-group etherwurst-rg \
  --location westeurope \
  --sku Standard_LRS \
  --kind StorageV2 \
  --hns true  # Enable hierarchical namespace (ADLS Gen2)

# Create container for blockchain data
az storage container create \
  --name ethereum \
  --account-name etherwurstdata
```

#### Step 2: Upload cryo Data to Blob Storage

```bash
# Run cryo export (on AKS or locally)
cryo blocks txs logs traces \
  -b 0:latest \
  -o /tmp/ethereum-export/ \
  --requests-per-second 100

# Upload to Azure Blob Storage
az storage blob upload-batch \
  --destination ethereum \
  --source /tmp/ethereum-export/ \
  --account-name etherwurstdata \
  --pattern "*.parquet"
```

#### Step 3: Create Delta Tables in Databricks

```sql
-- In a Databricks notebook:

-- Create external tables from Parquet files
CREATE TABLE IF NOT EXISTS ethereum.blocks
USING DELTA
AS SELECT * FROM parquet.`abfss://ethereum@etherwurstdata.dfs.core.windows.net/blocks/`;

CREATE TABLE IF NOT EXISTS ethereum.transactions
USING DELTA
AS SELECT * FROM parquet.`abfss://ethereum@etherwurstdata.dfs.core.windows.net/transactions/`;

CREATE TABLE IF NOT EXISTS ethereum.logs
USING DELTA
AS SELECT * FROM parquet.`abfss://ethereum@etherwurstdata.dfs.core.windows.net/logs/`;

CREATE TABLE IF NOT EXISTS ethereum.traces
USING DELTA
AS SELECT * FROM parquet.`abfss://ethereum@etherwurstdata.dfs.core.windows.net/traces/`;

-- Optimize for query performance
OPTIMIZE ethereum.transactions ZORDER BY (from_address, to_address, block_number);
OPTIMIZE ethereum.logs ZORDER BY (address, topic0, block_number);
```

#### Step 4: Example Analytical Queries

```sql
-- Top 20 most active addresses in the last 7 days
SELECT from_address, COUNT(*) as tx_count, SUM(value) as total_value
FROM ethereum.transactions
WHERE block_number > (SELECT MAX(block_number) - 50400 FROM ethereum.blocks)  -- ~7 days
GROUP BY from_address
ORDER BY tx_count DESC
LIMIT 20;

-- All ERC-20 transfers for a specific address
SELECT
  t.block_number,
  t.transaction_hash,
  t.address as token_contract,
  CONCAT('0x', SUBSTR(t.topic1, 27)) as from_address,
  CONCAT('0x', SUBSTR(t.topic2, 27)) as to_address,
  CONV(SUBSTR(t.data, 3, 64), 16, 10) as amount
FROM ethereum.logs t
WHERE t.topic0 = '0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef'
  AND (t.topic1 LIKE '%YOUR_ADDRESS%' OR t.topic2 LIKE '%YOUR_ADDRESS%');

-- Contract deployment analysis
SELECT
  DATE_TRUNC('day', FROM_UNIXTIME(b.timestamp)) as day,
  COUNT(*) as contracts_deployed
FROM ethereum.traces tr
JOIN ethereum.blocks b ON tr.block_number = b.number
WHERE tr.call_type = 'create'
GROUP BY 1
ORDER BY 1 DESC;

-- Find all internal ETH transfers > 100 ETH
SELECT
  block_number,
  transaction_hash,
  from_address,
  to_address,
  CAST(value AS DECIMAL(38,0)) / 1e18 as eth_value
FROM ethereum.traces
WHERE value > 100000000000000000000  -- 100 ETH in wei
  AND call_type = 'call'
ORDER BY eth_value DESC
LIMIT 100;
```

---

## ClickHouse as an Alternative

If you want **sub-second dashboard queries** or want to avoid Databricks costs, ClickHouse is excellent for OLAP on blockchain data. The [Xatu](https://github.com/ethpandaops/xatu) project by ethpandaops uses ClickHouse for Ethereum network analytics.

### Deploy ClickHouse on AKS

```yaml
# clickhouse-values.yaml (using Altinity operator)
apiVersion: clickhouse.altinity.com/v1
kind: ClickHouseInstallation
metadata:
  name: ethereum-analytics
spec:
  configuration:
    clusters:
      - name: main
        layout:
          shardsCount: 1
          replicasCount: 1
    settings:
      max_memory_usage: "32000000000"  # 32GB
  defaults:
    templates:
      dataVolumeClaimTemplate: data-volume
  templates:
    volumeClaimTemplates:
      - name: data-volume
        spec:
          storageClassName: premium-ssd
          accessModes: [ReadWriteOnce]
          resources:
            requests:
              storage: 2Ti
```

### ClickHouse Schema for Ethereum

```sql
CREATE TABLE ethereum.transactions (
    block_number UInt64,
    block_timestamp DateTime,
    transaction_hash String,
    from_address String,
    to_address Nullable(String),
    value UInt256,
    gas_used UInt64,
    gas_price UInt64,
    input String,
    nonce UInt64,
    transaction_index UInt32
) ENGINE = MergeTree()
ORDER BY (block_number, transaction_index)
PARTITION BY toYYYYMM(block_timestamp);

-- ClickHouse is incredibly fast for queries like:
SELECT from_address, count() as cnt
FROM ethereum.transactions
WHERE block_timestamp > now() - INTERVAL 7 DAY
GROUP BY from_address
ORDER BY cnt DESC
LIMIT 20;
-- Returns in < 1 second across billions of rows
```

---

## Integration with AI Agents

The analytics layer is what your AI agents will query. They need:

1. **Databricks SQL Warehouse endpoint** — for complex analytical queries
2. **Blockscout API** — for address lookups and transaction details
3. **Erigon RPC** — for real-time data and trace queries

```python
# Example: AI agent querying Databricks
from databricks import sql

connection = sql.connect(
    server_hostname="etherwurst-databricks.azuredatabricks.net",
    http_path="/sql/1.0/warehouses/abc123",
    access_token="your-token"
)

cursor = connection.cursor()
cursor.execute("""
    SELECT from_address, to_address, value, block_number
    FROM ethereum.transactions
    WHERE from_address = '0x...' OR to_address = '0x...'
    ORDER BY block_number DESC
    LIMIT 1000
""")
results = cursor.fetchall()
```

---

## Cost Comparison (Monthly, ~1TB blockchain data)

| Solution | Storage | Compute | Total |
|----------|---------|---------|-------|
| **Databricks** (serverless SQL) | ~$20 (Blob) | ~$100-300 (on-demand) | **~$150-350** |
| **ClickHouse** (on AKS) | ~$200 (Premium SSD 2TB) | ~$400 (D8ds_v5) | **~$600** |
| **PostgreSQL** (on AKS) | ~$200 (Premium SSD 2TB) | ~$400 (D8ds_v5) | **~$600** |

Databricks wins on cost because storage is on cheap Blob and compute scales to zero.
