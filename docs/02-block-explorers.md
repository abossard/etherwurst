# 02 — Block Explorers & Etherscan-like APIs

## The Goal

Host a **self-hosted Etherscan equivalent** — fast address lookups, transaction details, decoded contract calls, token transfers, internal transactions, and a REST/GraphQL API for programmatic access.

---

## Option Comparison

| | **Blockscout** | **Otterscan** | **Ponder** |
|---|---|---|---|
| **Type** | Full block explorer (UI + API + indexer) | Lightweight explorer UI | Indexing framework + API |
| **Repo** | [blockscout/blockscout](https://github.com/blockscout/blockscout) | [otterscan/otterscan](https://github.com/otterscan/otterscan) | [ponder-sh/ponder](https://github.com/ponder-sh/ponder) |
| **Language** | Elixir/Phoenix | React/TypeScript | TypeScript |
| **Database** | PostgreSQL | None (queries node directly) | PostgreSQL |
| **EL Client** | Any (Geth, Reth, Erigon, etc.) | **Erigon only** (uses custom `ots_*` API) | Any (via RPC) |
| **Deployment** | Docker Compose / K8s | Docker / Static files | Docker / Node.js |
| **Features** | Full Etherscan clone: verified contracts, token tracker, API, GraphQL | Fast lookups, address history, decoded txs | Custom indexed data + GraphQL API |
| **API** | REST API (Etherscan-compatible!) + GraphQL | None (UI only, but Erigon has `ots_*` RPC) | GraphQL |
| **Infra Overhead** | High (PostgreSQL, Redis, many microservices) | **Minimal** (just the React app) | Medium (PostgreSQL) |
| **License** | GPL-3.0 | MIT | MIT |

---

## ⭐ Recommended: Erigon + Otterscan + Blockscout

Use **both** for different purposes:

### Otterscan — For Fast Interactive Lookups

Otterscan is a React SPA that talks **directly to Erigon's `ots_*` API**. No database, no indexer, no backend. Just the Erigon node + a static web app.

**Why it's perfect for quick lookups:**
- Zero additional infrastructure
- Sub-second response times (queries archive node directly)
- Address page with full transaction history
- Decoded contract interactions (using 4byte directory + Sourcify)
- Internal transaction tracing
- Token transfers

```bash
# Run Otterscan (just a static web app pointing at your Erigon node)
docker run -d --name otterscan \
  -p 5100:80 \
  -e ERIGON_URL=http://your-erigon:8545 \
  otterscan/otterscan:latest
```

**Otterscan RPC methods provided by Erigon** (`ots_*` namespace):
- `ots_getApiLevel` — API version check
- `ots_getInternalOperations` — internal transactions for a tx hash
- `ots_hasCode` — check if address is a contract
- `ots_getTransactionError` — revert reason
- `ots_traceTransaction` — detailed transaction trace
- `ots_searchTransactionsBefore/After` — paginated tx history for an address
- `ots_getBlockDetails` — enriched block info
- `ots_getContractCreator` — who deployed a contract
- And many more...

📖 Docs: https://docs.otterscan.io/

### Blockscout — For Full Etherscan API Compatibility

When you need the **REST API** (Etherscan-compatible endpoints), verified contract interaction, token tracking, and a polished public-facing UI, add Blockscout.

**Key features:**
- **Etherscan-compatible REST API** — drop-in replacement for tools that use Etherscan API
- Smart contract verification + read/write UI
- Token tracker (ERC-20, ERC-721, ERC-1155)
- Address tags and labels
- GraphQL API for complex queries
- Kubernetes deployment support

```bash
# Blockscout docker-compose (simplified)
git clone https://github.com/blockscout/blockscout
cd blockscout/docker-compose
docker-compose up -d
```

Blockscout has official Kubernetes deployment docs:
https://docs.blockscout.com/for-developers/deployment/kubernetes-deployment

**Blockscout architecture:**
```
Erigon/Reth/Geth → Blockscout Indexer → PostgreSQL → Blockscout API + UI
                                              ↓
                                         Redis (cache)
```

---

## The Etherscan-Compatible API

Blockscout provides these Etherscan-compatible API modules:

| Module | Endpoints |
|--------|-----------|
| `account` | Balance, tx list, token transfers, internal txs |
| `contract` | ABI, source code, verification |
| `transaction` | Tx status, receipt status |
| `block` | Block reward, countdown |
| `token` | Token info, holder list |
| `logs` | Event logs by address/topic |
| `stats` | Token supply, ETH price |

**Example API calls:**
```bash
# Get transactions for an address
curl "https://your-blockscout/api?module=account&action=txlist&address=0x..."

# Get contract ABI
curl "https://your-blockscout/api?module=contract&action=getabi&address=0x..."

# Get token transfers
curl "https://your-blockscout/api?module=account&action=tokentx&address=0x..."
```

These are the same endpoints your AI agents will use later!

---

## Other Notable Projects

### The Graph (graph-node)
- **Repo**: https://github.com/graphprotocol/graph-node
- Decentralized indexing protocol using "Subgraphs"
- Write custom indexers in AssemblyScript
- GraphQL API auto-generated from your schema
- Good for indexing specific contracts/protocols, overkill for general explorer
- Requires: PostgreSQL + IPFS + Ethereum node

### Envio HyperIndex
- **Repo**: https://github.com/enviodev/hyperindex
- Ultra-fast multichain indexer (25,000+ events/sec)
- TypeScript/JavaScript/ReScript
- Auto-generates indexers from contract addresses
- GraphQL API
- Good for custom event indexing alongside Blockscout

---

## Summary: What to Deploy

```
Erigon (archive node)
  ├── Otterscan UI (direct RPC, zero infra overhead)
  │    └── For: developers, quick interactive lookups
  │
  ├── Blockscout (indexed, PostgreSQL-backed)
  │    └── For: public API, Etherscan-compatible REST, AI agent tools
  │
  └── RPC endpoint (eth/trace/debug APIs)
       └── For: cryo/ETL bulk data export, custom indexers
```
