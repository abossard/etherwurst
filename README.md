<div align="center">

# 🌭 ETHERWURST

### The Self-Hosted Ethereum Intelligence Platform

**Run your own Etherscan. Index every transaction. Deploy AI agents that investigate the blockchain.**

[![Erigon](https://img.shields.io/badge/Erigon-v3.0.5-blue?logo=ethereum)](https://github.com/erigontech/erigon)
[![Lighthouse](https://img.shields.io/badge/Lighthouse-latest-orange?logo=ethereum)](https://github.com/sigp/lighthouse)
[![Blockscout](https://img.shields.io/badge/Blockscout-v5.1-green?logo=ethereum)](https://github.com/blockscout/blockscout)
[![Otterscan](https://img.shields.io/badge/Otterscan-latest-purple?logo=ethereum)](https://github.com/otterscan/otterscan)
[![Flux](https://img.shields.io/badge/Flux_CD-v2.8-cyan?logo=flux)](https://github.com/fluxcd/flux2)
[![AKS](https://img.shields.io/badge/AKS-Karpenter-0078D4?logo=microsoftazure)](https://github.com/Azure/karpenter-provider-azure)

```
"Give me addresses. Describe a scenario. I'll investigate and report back."
```

</div>

---

## 🔥 What Is This?

Etherwurst is a **production-ready, GitOps-deployed Ethereum infrastructure stack** that turns a Kubernetes cluster into a full blockchain intelligence platform.

It's not just a node. It's a **self-hosted Etherscan + analytics engine + AI investigation lab** — all running on your own infrastructure, syncing the entire Ethereum mainnet archive.

```
          YOU                                  ETHERWURST
┌─────────────────────┐              ┌──────────────────────────┐
│                     │              │                          │
│  "Investigate these │    ───►      │  🤖 AI Agent             │
│   addresses for     │              │    ├── queries Blockscout│
│   wash trading"     │              │    ├── traces via Erigon │
│                     │              │    ├── patterns via cryo │
│                     │    ◄───      │    └── 📊 REPORT         │
│  [PDF Report]       │              │                          │
└─────────────────────┘              └──────────────────────────┘
```

---

## 🧱 The Stack

### Layer 1 — The Archive Node

| Component | What | Why |
|-----------|------|-----|
| [**Erigon**](https://github.com/erigontech/erigon) | Ethereum execution client | **1.6TB archive** (vs 12TB Geth). Built-in `ots_*` API for instant Otterscan queries. Archive mode with full trace/debug support. The fastest sync in the game. |
| [**Lighthouse**](https://github.com/sigp/lighthouse) | Ethereum consensus client | Rust-built, security-hardened with continuous fuzzing. Checkpoint sync gets you running in minutes, not days. |

### Layer 2 — Block Explorers & APIs

| Component | What | Why |
|-----------|------|-----|
| [**Otterscan**](https://github.com/otterscan/otterscan) | Local block explorer | **Zero infrastructure** — runs entirely in your browser, talks directly to Erigon's JSON-RPC. Privacy-first, blazing fast. No databases, no indexers. |
| [**Blockscout**](https://github.com/blockscout/blockscout) | Full Etherscan replacement | REST + GraphQL API, contract verification, token tracking, address pages. **Powers 600+ networks** including Optimism, Gnosis, and Base. Your AI agents talk to this. |

### Layer 3 — Analytics & Indexing (Roadmap)

| Component | What | Why |
|-----------|------|-----|
| [**cryo**](https://github.com/paradigmxyz/cryo) | Bulk blockchain → Parquet | By Paradigm. Extract **40+ datasets** (blocks, txs, logs, traces) to Parquet files. Filter by address, topic, contract. Feed directly into Databricks/Spark. |
| [**Ponder**](https://github.com/ponder-sh/ponder) | TypeScript indexing framework | Full type safety, hot reloading, auto-generated GraphQL. Built-in reorg handling. Index exactly what your agents need. |
| [**ethereum-etl**](https://github.com/blockchain-etl/ethereum-etl) | Streaming ETL pipeline | Battle-tested by Google BigQuery public datasets. Python + Rust implementations. Stream into Kafka, Pub/Sub, or directly to your analytics DB. |
| **Azure Databricks** | Analytics compute | Delta Lake, auto-scaling Spark, SQL analytics. Connect to the Parquet files cryo produces. Run ML models on on-chain data. |

### Layer 4 — AI Investigation Agents (Roadmap)

| Component | What | Why |
|-----------|------|-----|
| LLM + Tool Use | AI agent framework | Agents with function-calling that query Blockscout API, trace transactions via Erigon RPC, and pattern-match via indexed data |
| [**Forta**](https://github.com/forta-network/forta-node) | Real-time threat detection | Community-driven detection bots for hacks, exploits, rug pulls. Run a node and get alerts on suspicious activity. |

---

## ⚡ Infrastructure

Everything is **GitOps-managed** and **auto-scaling**:

| Tool | Role | Superpower |
|------|------|------------|
| [**Flux CD**](https://github.com/fluxcd/flux2) + [**Flux Operator**](https://github.com/controlplaneio-fluxcd/flux-operator) | GitOps deployment | Push to Git → cluster updates. Built-in Web UI for monitoring. MCP Server for AI-assisted ops. |
| [**Karpenter**](https://github.com/Azure/karpenter-provider-azure) | Node autoscaling | Erigon needs 32GB RAM? Karpenter spins up a memory-optimized E-series VM in seconds. Node goes idle? Consolidated and terminated. |
| **Azure Premium SSD** | Storage | Erigon's archive needs fast IOPS. Premium SSD with retain policy so your 2TB of synced data survives pod restarts. |
| [**Prometheus**](https://github.com/prometheus/prometheus) + [**Grafana**](https://github.com/grafana/grafana) | Monitoring | Full observability: Ethereum sync progress, peer counts, RPC latency, node resource usage. |

---

## 🚀 Quick Start

```bash
# 1. Connect to your AKS cluster
az aks get-credentials --resource-group <rg> --name <cluster>

# 2. Deploy everything
./setup.sh

# 3. Open all UIs
./portforward.sh start

# 4. Monitor sync progress
./sync-status.sh --watch
```

### What You Get

| URL | Service |
|-----|---------|
| http://localhost:5100 | **Otterscan** — Block explorer UI |
| http://localhost:4000 | **Blockscout** — Etherscan-compatible API + UI |
| http://localhost:3000 | **Grafana** — Monitoring dashboards (`admin`/`prom-operator`) |
| http://localhost:9090 | **Prometheus** — Metrics & alerting |
| http://localhost:9080 | **Flux UI** — GitOps management |
| http://localhost:8545 | **Erigon RPC** — Raw JSON-RPC endpoint |

---

## 📁 Repository Structure

```
etherwurst/
├── clusters/etherwurst/           # Flux GitOps fleet repo
│   ├── flux-system/               # Flux Operator bootstrap
│   ├── infrastructure/            # Namespaces, storage, Karpenter NodePools
│   ├── apps/                      # Erigon, Lighthouse, Otterscan, Blockscout
│   └── monitoring/                # Prometheus, Grafana, eth-metrics
├── docs/                          # 10 detailed research & architecture docs
│   ├── 01-ethereum-clients.md     # Geth vs Reth vs Erigon deep-dive
│   ├── 02-block-explorers.md      # Blockscout + Otterscan comparison
│   ├── 03-aks-deployment.md       # AKS setup, storage classes, costs
│   ├── 05-indexing-etl.md         # cryo, ethereum-etl, Ponder pipelines
│   ├── 06-analytics-platform.md   # Databricks + ClickHouse setup
│   ├── 07-ai-agents.md            # Agent architecture & investigation workflow
│   ├── 08-architecture.md         # Full architecture diagram & roadmap
│   ├── 09-resources-links.md      # Every tool, repo, and reference
│   └── 10-flux-gitops.md          # Flux Operator research & recommendation
├── setup.sh                       # Bootstrap / update / teardown the stack
├── portforward.sh                 # Start/stop/status all port-forwards
└── sync-status.sh                 # Monitor Ethereum sync progress
```

---

## 🏗️ Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│  AKS Cluster (Karpenter-managed)                                        │
│                                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │  ethereum namespace                     [E-series dedicated VM] │    │
│  │                                                                 │    │
│  │  ┌──────────┐  Engine API  ┌────────────┐                      │    │
│  │  │  Erigon  │◄────────────►│ Lighthouse │                      │    │
│  │  │ (EL)     │  JWT auth    │ (CL)       │                      │    │
│  │  │          │              └────────────┘                      │    │
│  │  │ RPC:8545 │◄──── ┌───────────┐                               │    │
│  │  │ WS:8546  │      │ Otterscan │ (browser-side explorer)       │    │
│  │  │ P2P:303xx│      └───────────┘                               │    │
│  │  └──────────┘                                                   │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│                                                                         │
│  ┌──────────────────────────┐  ┌───────────────────────────────────┐   │
│  │  blockscout namespace    │  │  monitoring namespace             │   │
│  │                          │  │                                   │   │
│  │  ┌────────────┐          │  │  ┌────────────┐ ┌──────────┐    │   │
│  │  │ Blockscout │          │  │  │ Prometheus │ │ Grafana  │    │   │
│  │  │ API + UI   │          │  │  └────────────┘ └──────────┘    │   │
│  │  └────────────┘          │  │  ┌─────────────────────────┐    │   │
│  │  ┌────────────┐          │  │  │ ethereum-metrics-export │    │   │
│  │  │ PostgreSQL │          │  │  └─────────────────────────┘    │   │
│  │  └────────────┘          │  └───────────────────────────────────┘   │
│  └──────────────────────────┘                                          │
│                                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │  flux-system          Flux Operator + Controllers + Web UI      │   │
│  └─────────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## 🎯 The Vision

**Phase 1** ✅ Archive Node — Erigon + Lighthouse syncing Ethereum mainnet  
**Phase 2** ✅ Block Explorers — Otterscan + Blockscout providing Etherscan-like access  
**Phase 3** 🔜 Analytics — cryo → Parquet → Databricks pipeline for bulk analysis  
**Phase 4** 🔜 AI Agents — LLM-powered investigators that trace money flows, detect patterns, and generate reports  

> The end state: **"Here are 5 addresses and a suspicion of wash trading. Investigate and report."**  
> The AI agent traces transactions, identifies patterns across the indexed data, cross-references with known threat signatures from Forta, and produces a structured PDF report.

---

## 🔗 Key References

| Project | Repository | Stars |
|---------|-----------|-------|
| Erigon | [`erigontech/erigon`](https://github.com/erigontech/erigon) | 3.5k+ |
| Lighthouse | [`sigp/lighthouse`](https://github.com/sigp/lighthouse) | 2.9k+ |
| Otterscan | [`otterscan/otterscan`](https://github.com/otterscan/otterscan) | 1.8k+ |
| Blockscout | [`blockscout/blockscout`](https://github.com/blockscout/blockscout) | 3.8k+ |
| Flux CD | [`fluxcd/flux2`](https://github.com/fluxcd/flux2) | 6.5k+ |
| Flux Operator | [`controlplaneio-fluxcd/flux-operator`](https://github.com/controlplaneio-fluxcd/flux-operator) | 400+ |
| Karpenter Azure | [`Azure/karpenter-provider-azure`](https://github.com/Azure/karpenter-provider-azure) | 300+ |
| cryo | [`paradigmxyz/cryo`](https://github.com/paradigmxyz/cryo) | 1.2k+ |
| Ponder | [`ponder-sh/ponder`](https://github.com/ponder-sh/ponder) | 1.5k+ |
| ethereum-etl | [`blockchain-etl/ethereum-etl`](https://github.com/blockchain-etl/ethereum-etl) | 3k+ |
| Forta | [`forta-network/forta-node`](https://github.com/forta-network/forta-node) | 200+ |
| Prometheus | [`prometheus/prometheus`](https://github.com/prometheus/prometheus) | 56k+ |
| Grafana | [`grafana/grafana`](https://github.com/grafana/grafana) | 66k+ |

---

<div align="center">

**Built with 🌭 on Azure Kubernetes Service**

*Etherwurst — because investigating the blockchain should be as easy as ordering a sausage.*

</div>
