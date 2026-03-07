# Data Schema — Transaction Analytics Platform

> **Purpose:** Technology-neutral reference for the data model used by this analytics platform.
> Originally designed for Ethereum blockchain analysis, but the schema is generalizable
> to any system that produces **accounts, transfers, event logs, and deployable programs**
> (e.g., actor frameworks, payment networks, supply-chain ledgers).

---

## 1. Storage Layer — Raw Data Tables

These tables hold the ingested raw data. Column names use the original Ethereum naming;
the **Neutral Concept** column maps each to a domain-agnostic equivalent.

### 1.1 Blocks (Epochs / Time Slots)

A sequential, immutable record of a processing epoch.

| Column            | Type           | Nullable | Neutral Concept                         |
|-------------------|----------------|----------|-----------------------------------------|
| block_number      | uint64         | no       | **Epoch sequence number**               |
| block_hash        | string (hex)   | no       | Epoch content hash / unique ID          |
| timestamp         | uint32 (unix)  | no       | Wall-clock time of the epoch            |
| author            | string (hex)   | no       | Producer / validator of the epoch       |
| gas_used          | uint64         | no       | Computational work consumed in epoch    |
| extra_data        | string (hex)   | no       | Producer-supplied metadata              |
| base_fee_per_gas  | uint64         | yes      | Epoch-level fee rate                    |
| chain_id          | uint64         | no       | Network / environment identifier        |

**Primary ordering:** `block_number`

### 1.2 Transactions (Transfers / Operations)

Every state-changing operation submitted by an account.

| Column                    | Type           | Nullable | Neutral Concept                                |
|---------------------------|----------------|----------|------------------------------------------------|
| block_number              | uint64         | no       | Parent epoch                                   |
| transaction_index         | uint64         | no       | Position within the epoch                      |
| transaction_hash          | string (hex)   | no       | **Unique operation ID**                        |
| nonce                     | uint64         | no       | Sender's sequence counter (replay protection)  |
| from_address              | string (hex)   | no       | **Sender account**                             |
| to_address                | string (hex)   | yes      | **Recipient account** (null = program deploy)  |
| value_binary              | string (hex)   | yes      | Native value transferred (binary)              |
| value_string              | string         | yes      | Native value transferred (decimal string)      |
| value_f64                 | float64        | yes      | Native value transferred (lossy float)         |
| input                     | string (hex)   | no       | Payload / call data                            |
| gas_limit                 | uint64         | no       | Max computational budget                       |
| gas_used                  | uint64         | no       | Actual computational cost                      |
| gas_price                 | uint64         | yes      | Fee per unit of computation                    |
| transaction_type          | uint32         | yes      | Operation variant (legacy, EIP-1559, etc.)     |
| max_priority_fee_per_gas  | uint64         | yes      | Tip offered to epoch producer                  |
| max_fee_per_gas           | uint64         | yes      | Max total fee per computation unit             |
| success                   | uint8 / bool   | no       | Whether the operation succeeded                |
| n_input_bytes             | uint32         | no       | Payload size (bytes)                           |
| n_input_zero_bytes        | uint32         | no       | Zero-byte count in payload                     |
| n_input_nonzero_bytes     | uint32         | no       | Non-zero-byte count in payload                 |
| chain_id                  | uint64         | no       | Network / environment identifier               |

**Primary ordering:** `(block_number, transaction_index)`

### 1.3 Logs (Events / Signals)

Structured events emitted during operation execution.

| Column            | Type           | Nullable | Neutral Concept                           |
|-------------------|----------------|----------|-------------------------------------------|
| block_number      | uint64         | no       | Parent epoch                              |
| transaction_index | uint32         | no       | Parent operation index                    |
| log_index         | uint32         | no       | Event position within epoch               |
| transaction_hash  | string (hex)   | no       | Parent operation ID                       |
| address           | string (hex)   | no       | **Emitter** (program address)             |
| topic0            | string (hex)   | yes      | Event type signature hash                 |
| topic1            | string (hex)   | yes      | Indexed parameter 1                       |
| topic2            | string (hex)   | yes      | Indexed parameter 2                       |
| topic3            | string (hex)   | yes      | Indexed parameter 3                       |
| data              | string (hex)   | no       | Non-indexed event payload                 |
| n_data_bytes      | uint32         | no       | Payload size (bytes)                      |
| chain_id          | uint64         | no       | Network / environment identifier          |

**Primary ordering:** `(block_number, log_index)`

### 1.4 Contracts (Programs / Deployed Code)

Records of program deployments.

| Column           | Type           | Nullable | Neutral Concept                          |
|------------------|----------------|----------|------------------------------------------|
| block_number     | uint64         | no       | Epoch in which the program was deployed  |
| create_index     | uint64         | no       | Deploy order within the epoch            |
| transaction_hash | string (hex)   | no       | Operation that created the program       |
| contract_address | string (hex)   | no       | **Program address / ID**                 |
| deployer         | string (hex)   | no       | Account that initiated the deploy        |
| factory          | string (hex)   | no       | Intermediate creator (if factory pattern)|
| init_code        | string (hex)   | no       | Initialization payload                   |
| code             | string (hex)   | no       | Deployed program bytecode                |
| init_code_hash   | string (hex)   | no       | Hash of initialization payload           |
| code_hash        | string (hex)   | no       | Hash of deployed bytecode                |
| chain_id         | uint64         | no       | Network / environment identifier         |

**Primary ordering:** `(block_number, create_index)`

### 1.5 ETL Progress (Sync Checkpoint)

Tracks the last ingested epoch per dataset for incremental loading.

| Column     | Type      | Neutral Concept                |
|------------|-----------|--------------------------------|
| dataset    | string    | Name of the data feed          |
| last_block | uint64    | Last epoch successfully synced |
| updated_at | datetime  | Timestamp of last sync         |

---

## 2. Domain Layer — Application Models

These are the **use-case-oriented** types consumed by the application logic.
They are independent of any storage backend.

### 2.1 Core Value Objects

| Type              | Fields                  | Validation                           |
|-------------------|-------------------------|--------------------------------------|
| `AccountAddress`  | Value (string)          | Starts with `0x`, length 42          |
| `OperationHash`   | Value (string)          | Starts with `0x`, length 66          |
| `AnalysisRequest` | Input (string)          | Auto-detects address vs. hash        |

### 2.2 Operation Data

```
TransactionInfo
├── Hash              : string        — Unique operation ID
├── From              : string        — Sender account
├── To                : string        — Recipient account
├── ValueEth          : decimal       — Native value in display units
├── TokenSymbol       : string        — Token symbol (if token transfer)
├── TokenAmount       : decimal       — Token quantity (if token transfer)
├── IsContractInteraction : bool      — Whether a program was invoked
├── ContractName      : string?       — Human-readable program name
├── Timestamp         : DateTimeOffset
├── Status            : string        — "success" or "reverted"
└── InputData         : string?       — Raw call payload
```

### 2.3 Operation Detail (Operation + Event Logs)

```
TransactionDetail
├── Transaction       : TransactionInfo
└── Logs[]            : TransactionLogInfo
    ├── Address       : string        — Emitter program
    ├── Topics[]      : string[]      — Indexed event parameters
    └── Data          : string        — Event payload
```

### 2.4 Program Assessment

```
ContractAssessment
├── Address                       : string
├── Name                          : string?
├── IsVerified                    : bool
├── IsProxy                       : bool    — Upgradeable / delegating
├── ProxyImplementation           : string? — Target address if proxy
├── HasSuspiciouslyShortBytecode  : bool
├── BytecodeLength                : int
└── AbiFragment                   : string? — Partial interface definition
```

### 2.5 Risk Analysis

```
ScamAnalysisResult
├── AnalysisId    : string
├── Input         : string
├── InputType     : WalletAddress | TransactionHash
├── Verdict       : Clean | Suspicious | LikelyScam | ConfirmedScam
├── RiskScore     : int (0–100)
├── Summary       : string
├── Transactions  : TransactionInfo[]
├── Indicators    : ScamIndicator[]
│   ├── Type       : enum (20 indicator types, see below)
│   ├── Description: string
│   ├── Severity   : Info | Warning | High | Critical
│   ├── Confidence : Low | Medium | High | Verified
│   └── Evidence   : string[]?
└── AnalyzedAt    : DateTimeOffset

Indicator Types:
  SuspiciousContract, DrainerPattern, FlashLoanAttack, HoneypotToken,
  FakeApproval, RapidTokenDump, ZeroValueTransfer, PhishingContract,
  UnverifiedContract, HighGasFee, SandwichAttack, CleanTransaction,
  CounterpartyConcentration, WalletAgeAnomaly, FailedTransactionSpike,
  ProxyUpgradeabilityRisk, ApprovalDrainPattern, EventLogAnomaly,
  MaliciousBytecodeSimilarity, BurstFanoutPattern
```

### 2.6 Account Graph (Flow Visualization)

```
WalletGraphQuery
├── Root          : AccountAddress   — Starting account
├── Depth         : int (1–10)       — Traversal depth
├── Direction     : Outgoing | Incoming | Both
├── MinValueEth   : decimal          — Minimum edge value filter
├── MaxNodes      : int (10–5000)
├── MaxEdges      : int (10–10000)
└── LookbackDays  : int (1–3650)

WalletGraphResult
├── Root, Depth, Direction, NodeCount, EdgeCount
├── Nodes[]
│   ├── Address, Label, IsSeed, IsContract
│   ├── InboundCount, OutboundCount
│   └── TotalInEth, TotalOutEth
└── Edges[]
    ├── Id, From, To
    ├── TotalValueEth, TransactionCount
    ├── FirstSeen, LastSeen
    └── DominantToken
```

---

## 3. Port Interfaces (Data Access Contracts)

These define the **required capabilities** of any storage backend.
Implement these to swap in a different data stack.

### 3.1 IBlockchainAnalyticsPort (Data Retrieval)

| Method                     | Input                        | Output (async stream)      |
|----------------------------|------------------------------|----------------------------|
| `GetWalletActivityAsync`   | List of account addresses    | `WalletTransaction` stream |
| `GetTransactionDetailsAsync`| List of operation hashes    | `TransactionDetail` stream |
| `AssessContractsAsync`     | List of program addresses    | `ContractAssessment` stream|

**Design principles:**
- **Batch-first**: accepts lists, returns async streams
- **Tag-based**: `WalletTransaction` tags each result with the querying account
- **Fault-tolerant**: missing items are silently skipped
- **Single-item extensions**: convenience wrappers for single-address/hash queries

### 3.2 IScamAnalysisPort (Analysis Orchestration)

| Method         | Input             | Output (async stream)          |
|----------------|-------------------|--------------------------------|
| `AnalyzeAsync` | `AnalysisRequest` | `AnalysisProgressEvent` stream |

Emits progress events through stages: `Started → FetchingTransactions → AnalyzingContracts → DetectingPatterns → ComputingScore → Completed`.

Optional deep analysis stages: `DeepAnalysis → DeepAnalysisComplete` with per-counterparty risk events.

### 3.3 IWalletGraphPort (Graph Traversal)

| Method           | Input              | Output                 |
|------------------|--------------------|------------------------|
| `BuildGraphAsync`| `WalletGraphQuery` | `WalletGraphResult`    |

BFS traversal of account-to-account flows, bounded by depth/node/edge limits.

---

## 4. ETL Pipeline

The ETL process extracts raw data from a source node and loads it into the analytics store.

```
Source Node (JSON-RPC) → Extractor (cryo/Rust) → Parquet files → Analytics Store
```

**Sync strategy:**
- Checkpoint-based: reads `etl_progress.last_block` per dataset
- First run: starts from `max(chain_head - 1000, 0)`
- Incremental: processes batches of 50–500 epochs per run
- Scheduled: CronJob every 10 minutes for continuous sync
- Deduplication: storage engine merges on primary key (idempotent re-ingestion)

**Datasets extracted:** `blocks`, `transactions`, `logs`, `contracts`

---

## 5. Mapping to Alternative Data Stacks

| Ethereum Concept     | Actor Model Equivalent     | Payment Network         | Supply Chain               |
|----------------------|----------------------------|-------------------------|----------------------------|
| Block                | Epoch / Mailbox batch      | Settlement window       | Batch / shipment lot       |
| Transaction          | Message                    | Payment instruction     | Transfer order             |
| from_address         | Sender actor               | Payer                   | Shipping origin            |
| to_address           | Receiver actor             | Payee                   | Shipping destination       |
| value                | Payload / credits          | Amount                  | Quantity                   |
| Log / Event          | Side-effect / notification | Status event            | Tracking event             |
| Contract             | Deployed behavior / policy | Payment rule            | Business logic module      |
| gas                  | Compute cost / quota       | Processing fee          | Handling cost              |
| nonce                | Message sequence number    | Idempotency key         | Order sequence number      |
| chain_id             | System / cluster ID        | Network ID              | Tenant / channel ID        |
