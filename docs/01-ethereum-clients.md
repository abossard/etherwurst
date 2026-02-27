# 01 — Ethereum Clients Compared

## The Big Three for Analytics Workloads

Since The Merge, every Ethereum node requires **two clients**:
- **Execution Layer (EL)**: processes transactions and state
- **Consensus Layer (CL)**: handles proof-of-stake consensus

For our use case (Etherscan-like API + analytics), the EL client choice matters most.

---

## Client Comparison

| | **Geth** | **Reth** | **Erigon** |
|---|---|---|---|
| **Language** | Go | Rust | Go |
| **Repo** | [ethereum/go-ethereum](https://github.com/ethereum/go-ethereum) | [paradigmxyz/reth](https://github.com/paradigmxyz/reth) | [erigontech/erigon](https://github.com/erigontech/erigon) |
| **Maturity** | Most mature, reference implementation | Production-ready since June 2024, audited | Very mature, battle-tested |
| **Archive Size** | ~12 TB+ | ~2.2 TB (full archive) | ~1.6 TB (archive, Erigon3) |
| **Full Node Size** | ~500 GB (snap sync) | ~1.2 TB (pruned) | ~1.1 TB (full node) |
| **Sync Time** | Days (snap sync) | Hours to days | ~4-8 hours (Erigon3!) |
| **trace/debug APIs** | Yes (needs `--gcmode archive`) | Yes, built-in | Yes, native, very fast |
| **Otterscan support** | No (needs separate indexer) | No | **Yes — built-in Otterscan API** |
| **Blockscout support** | Yes | Yes | Yes |
| **Key Strength** | Ecosystem standard | Fastest RPC performance, modular | Smallest archive, fastest sync, Otterscan-native |
| **Docker Image** | `ethereum/client-go` | `ghcr.io/paradigmxyz/reth` | `thorax/erigon` |

---

## ⭐ Recommendation: Erigon

**For an Etherscan-like explorer with fast lookups, Erigon is the best choice because:**

1. **Smallest archive node** — 1.6 TB vs 12 TB+ for Geth. Saves massively on Azure Ultra SSD costs
2. **Fastest sync** — Full archive in ~8 hours (Erigon3)
3. **Native Otterscan API** — Built-in Etherscan-like API without extra indexing infrastructure
4. **History on cheap disk** — Erigon3 lets you put historical data on a cheaper disk while keeping hot state on NVMe
5. **Trace/debug APIs** are first-class, not an afterthought

### Erigon3 Datadir Breakdown (Ethereum Mainnet Archive)

```
chaindata/          15 GB    → fast SSD
snapshots/
  ├── accessor/    120 GB
  ├── domain/      300 GB   → fast SSD (latest state)
  ├── history/     280 GB   → can be on cheaper storage
  ├── idx/         430 GB   → fast SSD for lookups
  └── total       ~1.6 TB
```

### Erigon Key Configuration

```bash
erigon \
  --datadir=/data/erigon \
  --chain=mainnet \
  --prune.mode=archive \          # Keep all history
  --http \
  --http.addr=0.0.0.0 \
  --http.port=8545 \
  --http.api=eth,net,web3,trace,debug,txpool,erigon,ots \  # 'ots' = Otterscan API!
  --http.vhosts='*' \
  --ws \
  --private.api.addr=0.0.0.0:9090 \
  --metrics \
  --metrics.addr=0.0.0.0
```

The `ots` API namespace enables the **Otterscan JSON-RPC extensions** — custom, high-performance endpoints for block explorer functionality.

### Erigon System Requirements

| Component | Requirement |
|-----------|-------------|
| CPU | 8+ cores |
| RAM | 32 GB+ (recommended 64 GB) |
| Disk (hot) | 500 GB NVMe/Ultra SSD (domain + idx + chaindata) |
| Disk (cold) | 1.2 TB SSD (history + accessor) |
| Bandwidth | 25+ Mbps |

---

## Reth as Alternative

If you need **maximum RPC throughput** (e.g., serving many concurrent API requests) or want to build custom indexing pipelines, **Reth** is excellent:

- Built as a library — you can embed it in custom tooling
- Paradigm's team actively optimizes for MEV/RPC/indexing use cases
- Fastest raw RPC query performance
- But: no built-in block explorer API (need Blockscout on top)

```bash
reth node \
  --datadir /data/reth \
  --http --http.addr 0.0.0.0 \
  --http.api eth,net,web3,txpool,trace,debug \
  --ws --ws.addr 0.0.0.0 \
  --full
```

---

## Consensus Layer Client

For CL, any client works. Recommendations:
- **Lighthouse** (Rust, by Sigma Prime) — most popular with Reth
- **Prysm** (Go) — most popular overall
- **Teku** (Java) — good for enterprise

All are available as Helm charts from ethpandaops.

---

## References

- Geth hardware requirements: https://geth.ethereum.org/docs/getting-started/hardware-requirements
- Reth docs: https://reth.rs
- Erigon repo + docs: https://github.com/erigontech/erigon
- Ethereum node guide: https://ethereum.org/en/developers/docs/nodes-and-clients/run-a-node/
- SSD compatibility list: https://gist.github.com/yorickdowne/f3a3e79a573bf35767cd002cc977b038
