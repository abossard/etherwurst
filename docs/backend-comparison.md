# Backend Comparison — Erigon vs ClickHouse

## Current Production Configuration

The API supports multiple backends via `AdapterRegistry`. The active production setup is:

| Backend | Status | Role |
|---|---|---|
| **ClickHouse** | ✅ **Default** (`DefaultBackend=clickhouse`) | Primary analytics backend — fast aggregation queries |
| **Erigon** | ✅ Active | Live-node fallback for real-time/full-chain lookups |
| **Blockscout** | ✅ Available | Registered when configured; used for block explorer data |
| **Fake** | — | Test-only fallback when no real backends are configured |

## Historical Test Results

> Test run: 2026-03-04. Each backend was tested against all 8 example addresses/transactions.

| Example | Input | Erigon | ClickHouse |
|---|---|---|---|
| 🦊 Sample Wallet | `0x60d0da…B57C` | ✅ 12 txs, risk 69, likelyScam | ✅ 0 txs¹, risk 0, clean |
| 📋 Sample Tx (1st ERC-20) | `0x5c504e…9e4b` | ✅ 0 txs², risk 0, clean | ✅ 0 txs², risk 0, clean |
| 🐋 Whale (5.5k ETH) | `0xa9ac43…4573` | ⏱️ timeout (4 events) | ✅ 330 txs, risk 100, confirmedScam |
| 🔁 High-volume sender | `0x9696f5…6976` | ⏱️ timeout (2 events) | ✅ 519 txs, risk 100, confirmedScam |
| 💸 5546 ETH transfer | `0x9f7f9a…cfb5` | ✅ 1 tx, risk 0, clean | ✅ 1 tx, risk 0, clean |
| ⛏️ Ethermine pool (2017) | `0x52bc44…e3b5` | ⏱️ timeout (2 events) | ✅ 0 txs¹, risk 0, clean |
| 🏦 Poloniex (2017) | `0x32be34…2d88` | ⏱️ timeout (2 events) | ✅ 0 txs¹, risk 0, clean |
| 🔥 327k ETH tx | `0x603629…bf16` | ✅ 1 tx, risk 0, clean | ✅ 1 tx, risk 0, clean |

### Legend

- ✅ = completed successfully
- ⏱️ = timed out after 120s (too many sequential RPC calls)
- ¹ Address/tx not in this backend's block range — returns 0 txs (expected)
- ² Transaction hash analysis returns the single tx detail, not wallet history

## Coverage Summary

| Backend | Status | Blocks Covered | Wallets Returning Data | Tx Hashes Returning Data | Timeouts |
|---|---|---|---|---|---|
| **ClickHouse** | Production default | ~24,561,444–24,562,444 (recent) | 2/5 | 3/3 | 0/8 |
| **Erigon** | Active fallback | Full chain (live node) | 1/5 (slow for high-tx wallets) | 3/3 | 4/8 |

## Key Takeaways

1. **ClickHouse is the production default** — handles high-tx wallets (330–519 txs) instantly, while Erigon times out on the same addresses due to sequential RPC calls.
2. **Erigon has full chain coverage** but is impractical for wallets with many transactions (sequential `ots_searchTransactionsBefore` + `eth_getTransactionReceipt` per tx). It serves as a live-node fallback.
3. **Transaction hash lookups work across all backends** — single-tx lookups are fast regardless of backend.
