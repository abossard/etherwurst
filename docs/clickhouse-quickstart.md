# ClickHouse Quick Start — Ethereum Analytics

## Connect

**Port-forward** (after `./portforward.sh start`):

```
http://localhost:8123/play
```

Or from inside the cluster:

```
http://clickhouse-ethereum-analytics:8123/play
```

## What's in the Database

| Table | Rows | Block Range | Description |
|---|---|---|---|
| `blocks` | 1.6M | 2,284,000 → 4,215,999 | Block headers (timestamp, author, gas, base fee) |
| `transactions` | 27M | 2,294,000 → 4,215,999 | All transactions (from/to, value, gas, input data) |
| `logs` | 10.9M | 2,294,004 → 4,215,999 | Event logs (topics, data) |
| `contracts` | 75.7K | 3,900,001 → 4,213,999 | Deployed contracts (deployer, code, init_code) |
| `etl_progress` | — | — | Sync cursor for the ETL pipeline |

**Time range:** Sep 18, 2016 → Aug 29, 2017 (early Ethereum, pre-Byzantium fork)

**Sync:** The ETL CronJob runs every 10 minutes, ingesting new blocks from Erigon.

---

## Essential Queries

### 1. Overview — How much data do I have?

```sql
SELECT
  formatReadableQuantity(count()) AS total_txs,
  min(block_number) AS from_block,
  max(block_number) AS to_block,
  formatReadableQuantity(countDistinct(from_address)) AS unique_senders,
  formatReadableQuantity(countDistinct(to_address)) AS unique_receivers
FROM transactions
```

### 2. Top 20 most active wallets (by transaction count)

```sql
SELECT
  from_address AS wallet,
  count() AS tx_count,
  round(sum(value_f64) / 1e18, 4) AS total_eth_sent,
  min(block_number) AS first_block,
  max(block_number) AS last_block
FROM transactions
GROUP BY from_address
ORDER BY tx_count DESC
LIMIT 20
```

### 3. Daily transaction volume

```sql
SELECT
  toDate(toDateTime(b.timestamp)) AS day,
  count() AS tx_count,
  round(sum(t.value_f64) / 1e18, 2) AS eth_volume,
  round(avg(t.gas_used), 0) AS avg_gas_used
FROM transactions t
JOIN blocks b ON t.block_number = b.block_number
GROUP BY day
ORDER BY day
```

### 4. Gas price trends over time

```sql
SELECT
  toDate(toDateTime(b.timestamp)) AS day,
  round(avg(t.gas_price) / 1e9, 2) AS avg_gwei,
  round(median(t.gas_price) / 1e9, 2) AS median_gwei,
  round(max(t.gas_price) / 1e9, 2) AS max_gwei
FROM transactions t
JOIN blocks b ON t.block_number = b.block_number
WHERE t.gas_price IS NOT NULL
GROUP BY day
ORDER BY day
```

### 5. Biggest ETH transfers

```sql
SELECT
  transaction_hash,
  from_address,
  to_address,
  round(value_f64 / 1e18, 4) AS eth_amount,
  block_number
FROM transactions
WHERE value_f64 > 0
ORDER BY value_f64 DESC
LIMIT 20
```

### 6. Contract deployments per day

```sql
SELECT
  toDate(toDateTime(b.timestamp)) AS day,
  count() AS contracts_deployed,
  countDistinct(deployer) AS unique_deployers
FROM contracts c
JOIN blocks b ON c.block_number = b.block_number
GROUP BY day
ORDER BY day
```

### 7. Most prolific contract deployers

```sql
SELECT
  deployer,
  count() AS contracts_deployed,
  min(block_number) AS first_deploy,
  max(block_number) AS last_deploy
FROM contracts
GROUP BY deployer
ORDER BY contracts_deployed DESC
LIMIT 20
```

### 8. ERC-20 token transfers (Transfer events)

```sql
-- topic0 = keccak256("Transfer(address,address,uint256)")
SELECT
  address AS token_contract,
  count() AS transfer_count
FROM logs
WHERE topic0 = '0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef'
GROUP BY address
ORDER BY transfer_count DESC
LIMIT 20
```

### 9. Wallet activity for a specific address

```sql
-- Replace the address below with the one you want to investigate
SELECT
  block_number,
  transaction_hash,
  CASE WHEN from_address = '0x...' THEN 'OUT' ELSE 'IN' END AS direction,
  from_address,
  to_address,
  round(value_f64 / 1e18, 6) AS eth_amount,
  gas_used,
  success
FROM transactions
WHERE from_address = '0x...' OR to_address = '0x...'
ORDER BY block_number DESC
LIMIT 100
```

### 10. Failed transactions analysis

```sql
SELECT
  toDate(toDateTime(b.timestamp)) AS day,
  countIf(success = 0) AS failed,
  countIf(success = 1) AS succeeded,
  round(countIf(success = 0) * 100.0 / count(), 2) AS fail_pct
FROM transactions t
JOIN blocks b ON t.block_number = b.block_number
GROUP BY day
ORDER BY day
```

### 11. Block utilization (gas used vs gas limit patterns)

```sql
SELECT
  toDate(toDateTime(timestamp)) AS day,
  round(avg(gas_used), 0) AS avg_gas_per_block,
  round(max(gas_used), 0) AS max_gas_per_block,
  count() AS blocks_per_day
FROM blocks
GROUP BY day
ORDER BY day
```

### 12. Smart contract interaction patterns (by input selector)

```sql
-- First 4 bytes of input = function selector
SELECT
  substring(input, 1, 10) AS selector,
  count() AS call_count,
  countDistinct(to_address) AS contracts_called,
  countDistinct(from_address) AS unique_callers
FROM transactions
WHERE length(input) >= 10 AND to_address IS NOT NULL
GROUP BY selector
ORDER BY call_count DESC
LIMIT 20
```

### 13. Miner/validator block rewards

```sql
SELECT
  author AS miner,
  count() AS blocks_mined,
  round(count() * 100.0 / (SELECT count() FROM blocks), 2) AS pct_of_blocks
FROM blocks
GROUP BY miner
ORDER BY blocks_mined DESC
LIMIT 20
```

### 14. Event-rich contracts (most log-emitting)

```sql
SELECT
  address AS contract,
  count() AS event_count,
  countDistinct(topic0) AS unique_events
FROM logs
GROUP BY address
ORDER BY event_count DESC
LIMIT 20
```

### 15. Database health check

```sql
SELECT
  table,
  formatReadableSize(sum(bytes_on_disk)) AS disk_size,
  formatReadableQuantity(sum(rows)) AS total_rows,
  count() AS parts
FROM system.parts
WHERE database = 'default' AND active
GROUP BY table
ORDER BY sum(bytes_on_disk) DESC
```

---

## Tips

- **ClickHouse uses 0-based hex** — addresses and hashes are lowercase with `0x` prefix
- **ETH values** are stored as `Float64` in Wei (`value_f64`); divide by `1e18` for ETH
- **Gas prices** are in Wei; divide by `1e9` for Gwei
- **Timestamps** in `blocks` are Unix epoch (`UInt32`); wrap with `toDateTime(timestamp)`
- **`/play` UI** supports CSV/JSON/Parquet export — click the format dropdown next to Run
- **Large queries** — use `LIMIT` and `FORMAT` clauses to avoid overwhelming the browser
