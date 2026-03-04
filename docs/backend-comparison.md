# Backend Comparison — Erigon vs ClickHouse vs ADX

> Test run: 2026-03-04. Each backend was tested against all 8 example addresses/transactions.

## Results

| Example | Input | Erigon | ClickHouse | ADX |
|---|---|---|---|---|
| 🦊 Sample Wallet | `0x60d0da…B57C` | ✅ 12 txs, risk 69, likelyScam | ✅ 0 txs¹, risk 0, clean | ✅ 0 txs¹, risk 0, clean |
| 📋 Sample Tx (1st ERC-20) | `0x5c504e…9e4b` | ✅ 0 txs², risk 0, clean | ✅ 0 txs², risk 0, clean | ✅ 0 txs², risk 0, clean |
| 🐋 Whale (5.5k ETH) | `0xa9ac43…4573` | ⏱️ timeout (4 events) | ✅ 330 txs, risk 100, confirmedScam | ✅ 0 txs¹, risk 0, clean |
| 🔁 High-volume sender | `0x9696f5…6976` | ⏱️ timeout (2 events) | ✅ 519 txs, risk 100, confirmedScam | ✅ 0 txs¹, risk 0, clean |
| 💸 5546 ETH transfer | `0x9f7f9a…cfb5` | ✅ 1 tx, risk 0, clean | ✅ 1 tx, risk 0, clean | ✅ 1 tx, risk 0, clean |
| ⛏️ Ethermine pool (2017) | `0x52bc44…e3b5` | ⏱️ timeout (2 events) | ✅ 0 txs¹, risk 0, clean | ✅ 666 txs, risk 0, clean |
| 🏦 Poloniex (2017) | `0x32be34…2d88` | ⏱️ timeout (2 events) | ✅ 0 txs¹, risk 0, clean | ✅ 666 txs, risk 0, clean |
| 🔥 327k ETH tx | `0x603629…bf16` | ✅ 1 tx, risk 0, clean | ✅ 1 tx, risk 0, clean | ✅ 1 tx, risk 0, clean |

### Legend

- ✅ = completed successfully
- ⏱️ = timed out after 120s (too many sequential RPC calls)
- ¹ Address/tx not in this backend's block range — returns 0 txs (expected)
- ² Transaction hash analysis returns the single tx detail, not wallet history

## Coverage Summary

| Backend | Blocks Covered | Wallets Returning Data | Tx Hashes Returning Data | Timeouts |
|---|---|---|---|---|
| **Erigon** | Full chain (live node) | 1/5 (slow for high-tx wallets) | 3/3 | 4/8 |
| **ClickHouse** | ~24,561,444–24,562,444 (recent) | 2/5 | 3/3 | 0/8 |
| **ADX** | ~3,720,000–3,791,999 (2017 era) | 2/5 | 3/3 | 0/8 |

## Key Takeaways

1. **ClickHouse is the fastest backend** — handles high-tx wallets (330–519 txs) instantly, while Erigon times out on the same addresses due to sequential RPC calls.
2. **Each backend covers different blocks** — ClickHouse has recent blocks, ADX has 2017-era blocks. Zero wallet overlap between them.
3. **Erigon has full chain coverage** but is impractical for wallets with many transactions (sequential `ots_searchTransactionsBefore` + `eth_getTransactionReceipt` per tx).
4. **Transaction hash lookups work across all backends** — single-tx lookups are fast regardless of backend.
5. **Default should be ClickHouse** for the best user experience (now configured).
