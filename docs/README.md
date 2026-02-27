# Etherwurst 🌭 — Ethereum Infrastructure on Azure

A research collection for running a high-performance Ethereum API (like Etherscan), blockchain analytics, and AI-powered investigation agents — all on Azure.

## Documentation Index

| Document | Description |
|----------|-------------|
| [01 - Ethereum Clients Compared](./01-ethereum-clients.md) | Geth vs Reth vs Erigon — which client for which use case |
| [02 - Block Explorers & APIs](./02-block-explorers.md) | Self-hosted Etherscan alternatives: Blockscout, Otterscan, and API layers |
| [03 - AKS Deployment & Storage](./03-aks-deployment.md) | Azure Kubernetes Service setup with Ultra SSDs and Helm charts |
| [04 - Azure Developer CLI (azd)](./04-azd-integration.md) | One-command deploy: infra provisioning + Helm charts + port forwarding + browser automation via `azd` |
| [05 - Indexing & ETL](./05-indexing-etl.md) | Blockchain indexers: Ponder, HyperIndex, The Graph, ethereum-etl, cryo |
| [06 - Analytics Platform](./06-analytics-platform.md) | Databricks, ClickHouse, and analytics databases for on-chain data |
| [07 - AI Investigation Agents](./07-ai-agents.md) | AI agents that take addresses, investigate transactions, produce reports |
| [08 - Architecture & Roadmap](./08-architecture.md) | End-to-end architecture and implementation roadmap |
| [09 - Resources & Links](./09-resources-links.md) | Curated list of every tool, repo, and reference |

## The Goal

> **Given a scenario and some addresses, an AI agent investigates transactions and produces a report.**

To get there, we need these layers:

```
┌─────────────────────────────────────────────────────────────────────┐
│  LAYER 4: AI Investigation Agents                                    │
│  "Describe a scenario, give addresses → get a report"               │
│  LLM + tools that query the API & analytics layer                   │
├─────────────────────────────────────────────────────────────────────┤
│  LAYER 3: Analytics / Indexed Data                                   │
│  Pre-indexed, queryable: token transfers, contract calls,           │
│  address profiles, decoded events — Databricks / ClickHouse        │
├─────────────────────────────────────────────────────────────────────┤
│  LAYER 2: Block Explorer API (Etherscan-like)                        │
│  Blockscout / Otterscan: quick lookups, address pages,              │
│  transaction details, contract verification, REST + GraphQL API     │
├─────────────────────────────────────────────────────────────────────┤
│  LAYER 1: Ethereum Node (Archive)                                    │
│  Erigon or Reth on AKS with Ultra SSD                               │
│  Full archive + trace/debug APIs                                    │
└─────────────────────────────────────────────────────────────────────┘
```

## Quick Decision Guide

| Question | Recommendation |
|----------|---------------|
| Which EL client? | **Erigon** (smallest archive, built-in Otterscan support) or **Reth** (fastest, modular) |
| Which block explorer? | **Blockscout** (full Etherscan replacement) or **Otterscan** (lightweight, Erigon-native) |
| Which indexer? | **Ponder** (TypeScript, simple) or **cryo** (bulk export to parquet) |
| Which analytics DB? | **Azure Databricks** (managed, Delta Lake, ML built-in) |
| Which AI framework? | LLM agent with tool-use (function calling) against your APIs |
