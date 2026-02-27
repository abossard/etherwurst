# 04 — Indexing & ETL: Getting Blockchain Data Into Queryable Form

## Why You Need an Indexing Layer

The raw Ethereum RPC API is powerful but **not optimized for analytics queries**. For example:
- "Show me all ERC-20 transfers for address X in the last 30 days" → requires scanning millions of blocks
- "Find all contracts that called function Y" → impossible without trace indexing
- "Aggregate daily DEX volume" → need decoded event logs in a database

**You need an ETL/indexing layer** between your node and your analytics queries.

---

## Tool Comparison

| Tool | Type | Speed | Output | Best For |
|------|------|-------|--------|----------|
| **cryo** ❄️ | Bulk export CLI | Very fast | Parquet/CSV/JSON | One-time bulk export to data lake |
| **ethereum-etl** | Bulk export CLI | Fast | CSV/JSON/BigQuery | BigQuery/PostgreSQL pipelines |
| **Ponder** | Indexing framework | Fast | PostgreSQL + GraphQL | Custom app-specific indexing |
| **HyperIndex (Envio)** | Indexing framework | 25k+ events/s | PostgreSQL + GraphQL | Fast multichain indexing |
| **The Graph (graph-node)** | Indexing protocol | Medium | PostgreSQL + GraphQL | Subgraph ecosystem |
| **Subsquid** | ETL toolkit | Fast | Any DB | Flexible TypeScript ETL |
| **Blockscout indexer** | Built into Blockscout | Medium | PostgreSQL | Comes free with Blockscout |

---

## ⭐ Recommended Stack

### Tier 1: cryo (Paradigm) — Bulk Data Export

**The fastest way to get historical blockchain data into Parquet files** for Databricks/Spark analysis.

- **Repo**: https://github.com/paradigmxyz/cryo
- **Language**: Rust (also available as Python package)
- **Output**: Parquet, CSV, JSON, or Python DataFrame
- **Data**: Blocks, transactions, logs, traces, state diffs, contracts

```bash
# Install cryo
cargo install cryo_cli
# Or via Python
pip install cryo

# Export all blocks from 18M to 19M as Parquet
export ETH_RPC_URL=http://your-erigon:8545
cryo blocks -b 18M:19M --requests-per-second 50

# Export all logs (event emissions)
cryo logs -b 18M:19M

# Export ERC-20 transfers specifically
cryo logs -b 18M:19M \
  --topic0 0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef

# Export traces (internal transactions)
cryo traces -b 18M:19M

# Export to a specific directory
cryo blocks txs logs traces -b 0:latest -o /data/ethereum-parquet/
```

**cryo datasets available:**
| Dataset | Description |
|---------|-------------|
| `blocks` | Block headers (number, timestamp, gas, miner) |
| `transactions` | All transactions |
| `logs` | Event logs (decoded topics) |
| `traces` | Internal transactions (call traces) |
| `state_diffs` | State changes per block |
| `contracts` | Contract creation events |
| `native_transfers` | ETH transfers (including internal) |

**Output goes directly to Parquet** → upload to Azure Blob Storage → query in Databricks.

### Tier 2: ethereum-etl — Streaming Pipeline

For **continuous, real-time indexing** alongside the bulk export:

- **Repo**: https://github.com/blockchain-etl/ethereum-etl
- **Language**: Python (also has a faster Rust version: ethereum-etl.rs)
- **Output**: CSV, JSON, PostgreSQL, Pub/Sub, BigQuery

```bash
pip install ethereum-etl[streaming]

# Stream blocks + transactions + logs continuously
ethereumetl stream \
  --start-block 19000000 \
  --provider-uri http://your-erigon:8545 \
  -e block,transaction,log,token_transfer,trace \
  --output postgresql://user:pass@host:5432/ethereum
```

**Schema** (what you get):

| Table | Key Columns |
|-------|-------------|
| `blocks` | number, timestamp, miner, gas_used, tx_count |
| `transactions` | hash, from, to, value, gas_price, input |
| `logs` | tx_hash, address, topics, data |
| `token_transfers` | token_address, from, to, value |
| `traces` | tx_hash, from, to, value, call_type, trace_address |
| `contracts` | address, bytecode, deployer |

There's also an **Airflow DAG** for orchestrating the pipeline:
https://github.com/blockchain-etl/ethereum-etl-airflow

### Tier 3: Ponder — Custom App Indexing

For indexing **specific contracts and events** with TypeScript:

- **Repo**: https://github.com/ponder-sh/ponder
- Inspired by The Graph, but simpler and self-hosted
- Type-safe, hot-reloading development
- GraphQL API auto-generated

```typescript
// ponder.config.ts
import { createConfig } from "ponder";
import { UniswapV3PoolAbi } from "./abis/UniswapV3Pool";

export default createConfig({
  chains: {
    mainnet: { id: 1, rpc: "http://your-erigon:8545" },
  },
  contracts: {
    UniswapV3Pool: {
      abi: UniswapV3PoolAbi,
      chain: "mainnet",
      address: "0x88e6A0c2dDD26FEEb64F039a2c41296FcB3f5640",
      startBlock: 12376729,
    },
  },
});

// src/UniswapV3Pool.ts
ponder.on("UniswapV3Pool:Swap", async ({ event, context }) => {
  await context.db.insert(schema.swap).values({
    hash: event.transaction.hash,
    sender: event.params.sender,
    amount0: event.params.amount0,
    amount1: event.params.amount1,
    sqrtPriceX96: event.params.sqrtPriceX96,
    timestamp: event.block.timestamp,
  });
});
```

---

## Pipeline Architecture

```
Erigon (Archive Node)
    │
    ├── cryo (bulk) ──────→ Parquet files ──→ Azure Blob Storage ──→ Databricks
    │                                                                   ↑
    ├── ethereum-etl (stream) ──→ PostgreSQL (real-time) ──────────────┘
    │
    ├── Blockscout indexer ──→ PostgreSQL (explorer API)
    │
    └── Ponder (custom) ──→ PostgreSQL (app-specific GraphQL)
```

---

## Data Volumes (Ethereum Mainnet, Full History)

| Dataset | Approximate Size (Parquet) |
|---------|---------------------------|
| Blocks | ~5 GB |
| Transactions | ~200 GB |
| Logs (events) | ~300 GB |
| Traces | ~500 GB |
| Token Transfers | ~50 GB |
| **Total** | **~1 TB+** |

This is why Databricks (with Delta Lake on Azure Blob Storage) is ideal — cheap, scalable storage with fast query performance.
