# 07 — Architecture & Roadmap

## End-to-End Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                          Azure Kubernetes Service                            │
│                                                                              │
│  ┌──────────────────────┐  ┌──────────────────┐  ┌───────────────────────┐  │
│  │      Erigon           │  │    Lighthouse     │  │     Otterscan         │  │
│  │  (Execution Layer)    │  │  (Consensus Layer) │  │  (Explorer UI)        │  │
│  │                       │  │                    │  │                       │  │
│  │  APIs: eth, trace,    │◄─┤  Engine API        │  │  Direct RPC to        │  │
│  │  debug, ots, erigon   │  │                    │  │  Erigon ots_* API     │  │
│  │                       │  │  Premium SSD 300GB │  │                       │  │
│  │  Ultra SSD 500GB (hot)│  └──────────────────┘  │  No database needed    │  │
│  │  Premium SSD 1.2TB    │                         └───────────────────────┘  │
│  │  (cold history)       │                                                    │
│  └──────────┬────────────┘                                                    │
│             │                                                                 │
│  ┌──────────┼────────────────────────────────────────────────────────────┐    │
│  │          │              Indexing & ETL Layer                           │    │
│  │          │                                                            │    │
│  │  ┌───────▼────────┐  ┌────────────────┐  ┌────────────────────────┐  │    │
│  │  │  cryo (bulk)    │  │ ethereum-etl   │  │  Blockscout            │  │    │
│  │  │                 │  │ (streaming)    │  │  (indexer + API + UI)  │  │    │
│  │  │  → Parquet      │  │ → PostgreSQL   │  │  → PostgreSQL          │  │    │
│  │  │  → Azure Blob   │  │   (real-time)  │  │  → Etherscan API       │  │    │
│  │  └────────┬────────┘  └───────┬────────┘  └──────────┬─────────────┘  │    │
│  │           │                    │                       │                │    │
│  └───────────┼────────────────────┼───────────────────────┼────────────────┘    │
│              │                    │                       │                     │
└──────────────┼────────────────────┼───────────────────────┼─────────────────────┘
               │                    │                       │
               ▼                    ▼                       ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         Analytics Layer                                       │
│                                                                               │
│  ┌─────────────────────────────────────────┐  ┌───────────────────────────┐  │
│  │         Azure Databricks                 │  │  Blockscout PostgreSQL    │  │
│  │                                          │  │                           │  │
│  │  Delta Lake tables:                      │  │  Etherscan-compatible     │  │
│  │  ├── ethereum.blocks                     │  │  REST API for AI agents   │  │
│  │  ├── ethereum.transactions               │  │                           │  │
│  │  ├── ethereum.logs                       │  └───────────────────────────┘  │
│  │  ├── ethereum.traces                     │                                 │
│  │  └── ethereum.token_transfers            │                                 │
│  │                                          │                                 │
│  │  + ML/AI runtime (MLflow, LLMs)          │                                 │
│  └─────────────────────────────────────────┘                                 │
└──────────────────────────────────────────────────────────┬────────────────────┘
                                                           │
                                                           ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                        AI Investigation Agent                                 │
│                                                                               │
│  LLM (Azure OpenAI / Claude) with function-calling tools:                    │
│  ├── blockscout_api()    → Address lookups, tx history, contract ABIs        │
│  ├── erigon_rpc()        → trace_transaction, debug_traceTransaction         │
│  ├── databricks_sql()    → Complex analytics queries across all history      │
│  ├── decode_contract()   → ABI decoding of contract interactions             │
│  └── build_flow_graph()  → Directed graph of fund flows                      │
│                                                                               │
│  Input:  Scenario + addresses + time range                                   │
│  Output: Investigation report (Markdown/PDF)                                 │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## Implementation Roadmap

### Phase 1: Ethereum Node on AKS
**Goal**: Running archive node with Otterscan for quick lookups

- [ ] Create AKS cluster with Ultra SSD support
- [ ] Deploy Erigon (archive mode) via ethpandaops Helm chart
- [ ] Deploy Lighthouse (CL) via Helm chart
- [ ] Create Ultra SSD + Premium SSD storage classes
- [ ] Wait for full sync (~8 hours with Erigon3)
- [ ] Deploy Otterscan UI pointing at Erigon
- [ ] Verify: can look up any address/transaction/block

**Milestone**: Interactive Etherscan-like explorer running on your own infra

### Phase 2: Block Explorer API
**Goal**: Etherscan-compatible REST API for programmatic access

- [ ] Deploy Blockscout on AKS (with PostgreSQL)
- [ ] Configure Blockscout to index from Erigon
- [ ] Wait for Blockscout indexing to complete
- [ ] Test Etherscan-compatible API endpoints
- [ ] Set up API key management

**Milestone**: Can call `?module=account&action=txlist&address=0x...` and get results

### Phase 3: Bulk Data Export & Analytics
**Goal**: All historical data queryable in Databricks

- [ ] Deploy cryo as a CronJob on AKS
- [ ] Export full history to Parquet (blocks, txs, logs, traces)
- [ ] Create Azure Blob Storage (ADLS Gen2)
- [ ] Upload Parquet to Blob Storage
- [ ] Create Databricks workspace
- [ ] Create Delta Lake tables from Parquet
- [ ] Set up incremental update pipeline (daily cryo + ethereum-etl streaming)
- [ ] Optimize tables (Z-ORDER on key columns)

**Milestone**: Can run complex SQL queries across all Ethereum history in seconds

### Phase 4: AI Investigation Agent
**Goal**: Give it addresses + scenario → get investigation report

- [ ] Define tool functions (Blockscout API, Erigon RPC, Databricks SQL)
- [ ] Build agent scaffold with LLM function-calling
- [ ] Implement investigation workflow (recon → analysis → traces → report)
- [ ] Add fund-flow graph generation (Mermaid diagrams)
- [ ] Add contract ABI decoding
- [ ] Build report template (Markdown)
- [ ] Test with known investigation scenarios
- [ ] Deploy agent as API service on AKS

**Milestone**: Working investigation agent that produces reports

### Phase 5: Continuous Monitoring
**Goal**: Real-time alerts and ongoing surveillance

- [ ] Set up ethereum-etl streaming to PostgreSQL/ClickHouse
- [ ] Integrate Forta detection bots for known attack patterns
- [ ] Build custom monitoring agents for specific addresses/patterns
- [ ] Dashboard for ongoing investigations
- [ ] Alert pipeline (webhook → agent → report)

---

## Key Technology Choices Summary

| Layer | Choice | Why |
|-------|--------|-----|
| EL Client | **Erigon** | Smallest archive (1.6TB), fastest sync, native Otterscan API |
| CL Client | **Lighthouse** | Rust, reliable, good with Erigon |
| Explorer UI | **Otterscan** | Zero infra, direct Erigon queries |
| Explorer API | **Blockscout** | Etherscan-compatible REST API |
| Helm Charts | **ethpandaops** | Official EF DevOps, all clients |
| Bulk ETL | **cryo** | Fastest, Parquet output, by Paradigm |
| Stream ETL | **ethereum-etl** | Mature, PostgreSQL/BigQuery support |
| Analytics | **Azure Databricks** | Cheap storage, auto-scaling, ML built-in |
| AI Agent | **LLM + function-calling** | Flexible, can query all layers |
| Monitoring | **Forta** | Decentralized, pre-built detection bots |
| K8s Storage | **Ultra SSD** (hot) + **Premium SSD** (cold) | Cost-optimized for Erigon's split storage |
