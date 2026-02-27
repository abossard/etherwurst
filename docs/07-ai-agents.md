# 06 — AI Investigation Agents

## The Vision

> *"Here are 3 addresses and a scenario: someone may be laundering money through a DeFi protocol. Investigate and produce a report."*

The AI agent should:
1. Look up all addresses in Blockscout / Erigon
2. Trace transaction histories and fund flows
3. Query the analytics layer for patterns
4. Decode contract interactions
5. Build a graph of related addresses
6. Produce a structured investigation report

---

## Architecture

```
User → "Investigate addresses 0xA, 0xB, 0xC for wash trading"
  │
  ▼
┌─────────────────────────────────────────────────┐
│  AI Agent (LLM with tool-use / function calling) │
│                                                   │
│  Tools available:                                 │
│  ├── blockscout_api(address, action)             │
│  ├── erigon_rpc(method, params)                  │
│  ├── databricks_sql(query)                       │
│  ├── trace_transaction(tx_hash)                  │
│  ├── decode_contract_call(input_data)            │
│  ├── get_token_transfers(address)                │
│  ├── get_address_labels(address)                 │
│  └── build_flow_graph(addresses, depth)          │
└─────────────────────────────────────────────────┘
  │
  ▼
Investigation Report (Markdown / PDF)
  ├── Executive Summary
  ├── Address Profiles
  ├── Transaction Timeline
  ├── Fund Flow Diagram
  ├── Anomalies Detected
  └── Confidence Assessment
```

---

## Tool Definitions for the Agent

### 1. Blockscout API Tools

```python
def get_address_info(address: str) -> dict:
    """Get address balance, transaction count, token holdings"""
    resp = requests.get(f"{BLOCKSCOUT_URL}/api?module=account&action=balance&address={address}")
    return resp.json()

def get_address_transactions(address: str, page: int = 1) -> list:
    """Get paginated transaction list for an address"""
    resp = requests.get(
        f"{BLOCKSCOUT_URL}/api?module=account&action=txlist"
        f"&address={address}&page={page}&offset=100&sort=desc"
    )
    return resp.json()["result"]

def get_token_transfers(address: str, token: str = None) -> list:
    """Get ERC-20 token transfers for an address"""
    params = f"module=account&action=tokentx&address={address}"
    if token:
        params += f"&contractaddress={token}"
    resp = requests.get(f"{BLOCKSCOUT_URL}/api?{params}")
    return resp.json()["result"]

def get_internal_transactions(address: str) -> list:
    """Get internal (trace-level) transactions"""
    resp = requests.get(
        f"{BLOCKSCOUT_URL}/api?module=account&action=txlistinternal&address={address}"
    )
    return resp.json()["result"]

def get_contract_abi(address: str) -> dict:
    """Get verified contract ABI for decoding"""
    resp = requests.get(
        f"{BLOCKSCOUT_URL}/api?module=contract&action=getabi&address={address}"
    )
    return resp.json()
```

### 2. Erigon RPC Tools (for deep analysis)

```python
from web3 import Web3

w3 = Web3(Web3.HTTPProvider("http://erigon:8545"))

def trace_transaction(tx_hash: str) -> dict:
    """Get full call trace of a transaction (internal calls, value transfers)"""
    return w3.provider.make_request("trace_transaction", [tx_hash])

def trace_call(to: str, data: str, block: str = "latest") -> dict:
    """Simulate a contract call and get trace"""
    return w3.provider.make_request("trace_call", [
        {"to": to, "data": data}, ["trace"], block
    ])

def debug_trace_transaction(tx_hash: str) -> dict:
    """Get EVM-level execution trace (opcodes, stack, memory)"""
    return w3.provider.make_request("debug_traceTransaction", [
        tx_hash, {"tracer": "callTracer"}
    ])

def get_code(address: str) -> str:
    """Get contract bytecode"""
    return w3.eth.get_code(address).hex()

def get_storage_at(address: str, slot: int) -> str:
    """Read contract storage directly"""
    return w3.eth.get_storage_at(address, slot).hex()
```

### 3. Databricks SQL Tools (for analytics)

```python
from databricks import sql

def query_analytics(sql_query: str) -> list:
    """Run analytical SQL query against indexed blockchain data"""
    connection = sql.connect(
        server_hostname=DATABRICKS_HOST,
        http_path=DATABRICKS_HTTP_PATH,
        access_token=DATABRICKS_TOKEN
    )
    cursor = connection.cursor()
    cursor.execute(sql_query)
    columns = [desc[0] for desc in cursor.description]
    rows = cursor.fetchall()
    return [dict(zip(columns, row)) for row in rows]

# Example queries the agent can construct:

def find_related_addresses(address: str, depth: int = 2) -> list:
    """Find all addresses that transacted with target (multi-hop)"""
    return query_analytics(f"""
        WITH RECURSIVE hops AS (
            SELECT DISTINCT
                CASE WHEN from_address = '{address}' THEN to_address ELSE from_address END as related,
                1 as hop
            FROM ethereum.transactions
            WHERE from_address = '{address}' OR to_address = '{address}'
            UNION ALL
            SELECT DISTINCT
                CASE WHEN t.from_address = h.related THEN t.to_address ELSE t.from_address END,
                h.hop + 1
            FROM ethereum.transactions t
            JOIN hops h ON t.from_address = h.related OR t.to_address = h.related
            WHERE h.hop < {depth}
        )
        SELECT related, MIN(hop) as min_hops, COUNT(*) as tx_count
        FROM hops GROUP BY related ORDER BY tx_count DESC LIMIT 100
    """)

def get_fund_flow_summary(address: str) -> dict:
    """Summarize inflows and outflows for an address"""
    return query_analytics(f"""
        SELECT
            'outflow' as direction,
            to_address as counterparty,
            COUNT(*) as tx_count,
            SUM(CAST(value AS DECIMAL(38,0))) / 1e18 as total_eth
        FROM ethereum.transactions
        WHERE from_address = '{address}' AND value > 0
        GROUP BY to_address
        UNION ALL
        SELECT
            'inflow',
            from_address,
            COUNT(*),
            SUM(CAST(value AS DECIMAL(38,0))) / 1e18
        FROM ethereum.transactions
        WHERE to_address = '{address}' AND value > 0
        GROUP BY from_address
        ORDER BY total_eth DESC
    """)
```

---

## Agent Framework Options

### Option A: LLM with Function Calling (Recommended to start)

Use any LLM that supports tool-use (OpenAI, Claude, Azure OpenAI):

```python
tools = [
    {"name": "get_address_info", "description": "Look up address balance and tx count", ...},
    {"name": "get_address_transactions", "description": "Get transaction history for address", ...},
    {"name": "trace_transaction", "description": "Get internal call trace of a transaction", ...},
    {"name": "query_analytics", "description": "Run SQL query on indexed blockchain data", ...},
    {"name": "find_related_addresses", "description": "Find addresses connected to target", ...},
    {"name": "get_fund_flow_summary", "description": "Summarize ETH inflows/outflows", ...},
]

system_prompt = """You are an Ethereum blockchain investigator.
Given addresses and a scenario, you systematically investigate by:
1. Looking up each address (balance, code, labels)
2. Analyzing transaction patterns
3. Tracing fund flows between addresses
4. Identifying anomalies or suspicious patterns
5. Producing a structured report

Always start by checking if addresses are EOAs or contracts.
Use trace APIs for complex transactions.
Use analytics queries for aggregate patterns."""
```

### Option B: Forta Network — Existing Detection Bots

Forta is a **decentralized network of detection bots** that monitor blockchain transactions in real-time.

- **Repo**: https://github.com/forta-network/forta-node
- Pre-built detection bots for common attack patterns
- Can run your own detection bots
- Alert on: flash loans, rug pulls, phishing, governance attacks, etc.

Useful as a **complement** to your custom AI agents — subscribe to Forta alerts for real-time monitoring while your agents do deep investigation.

### Option C: Slither — Smart Contract Analysis

For analyzing smart contract code (not transactions):

- **Repo**: https://github.com/crytic/slither
- Static analysis framework for Solidity
- Detects vulnerabilities, generates call graphs
- Your AI agent can use it to analyze contracts involved in suspicious transactions

```bash
# Analyze a contract from Etherscan/Blockscout verified source
slither 0xContractAddress --etherscan-apikey YOUR_KEY

# Or analyze local source
slither contracts/SuspiciousContract.sol
```

---

## Investigation Workflow

```
1. INPUT
   ├── Target addresses: [0xA, 0xB, 0xC]
   ├── Scenario: "Possible wash trading between these wallets"
   └── Time range: "Last 90 days"

2. RECONNAISSANCE (Agent Step 1)
   ├── For each address:
   │   ├── Is it EOA or contract?
   │   ├── Balance (ETH + tokens)
   │   ├── First/last transaction timestamps
   │   ├── Total transaction count
   │   └── Known labels (from Blockscout tags)
   └── Output: Address Profile Table

3. TRANSACTION ANALYSIS (Agent Step 2)
   ├── Pull all transactions between the addresses
   ├── Build a directed graph of fund flows
   ├── Calculate:
   │   ├── Total volume between each pair
   │   ├── Average time between transfers
   │   ├── Circular flow detection (A→B→C→A)
   │   └── Gas usage patterns
   └── Output: Transaction Flow Graph

4. PATTERN DETECTION (Agent Step 3)
   ├── Query analytics DB for:
   │   ├── Timing patterns (regular intervals?)
   │   ├── Value patterns (round numbers? similar amounts?)
   │   ├── Cross-reference with DEX trades
   │   ├── Token approval patterns
   │   └── Contract interaction patterns
   └── Output: Anomaly List

5. DEEP TRACE (Agent Step 4)
   ├── For suspicious transactions:
   │   ├── trace_transaction → full internal call tree
   │   ├── Decode contract calls (ABI from Blockscout)
   │   └── Identify intermediary protocols/contracts
   └── Output: Detailed Transaction Breakdowns

6. REPORT GENERATION (Agent Step 5)
   └── Structured markdown report with:
       ├── Executive Summary
       ├── Risk Score (Low/Medium/High/Critical)
       ├── Address Profiles
       ├── Fund Flow Diagram (Mermaid)
       ├── Timeline of Key Events
       ├── Anomalies Found
       ├── Supporting Evidence
       └── Recommendations
```

---

## Example Report Output (Generated by Agent)

```markdown
# Investigation Report: Wash Trading Analysis
**Date**: 2026-02-27
**Addresses**: 0xA..., 0xB..., 0xC...
**Risk Level**: 🔴 HIGH

## Executive Summary
Analysis of 3 addresses over 90 days reveals circular fund flows
totaling 450 ETH across 127 transactions. Addresses show coordinated
timing patterns (median 12-minute intervals) and consistent transfer
amounts (5 ETH ± 0.01).

## Fund Flow Diagram
​```mermaid
graph LR
    A[0xA...3f2] -->|150 ETH / 42 txs| B[0xB...7a1]
    B -->|148 ETH / 41 txs| C[0xC...9e3]
    C -->|147 ETH / 44 txs| A
​```

## Anomalies Detected
1. **Circular transfers**: 95% of funds flow A→B→C→A
2. **Regular timing**: 87% of transfers occur within 10-15 min intervals
3. **Consistent amounts**: Standard deviation of transfer amounts = 0.008 ETH
4. **No interaction with other addresses**: These 3 addresses only transact with each other
5. **Funded from same source**: All initially funded from Tornado Cash

## Confidence: 92%
```

---

## Existing Projects for Inspiration

| Project | What It Does | Link |
|---------|--------------|------|
| **Forta** | Decentralized threat detection network | https://github.com/forta-network/forta-node |
| **Slither** | Smart contract static analysis | https://github.com/crytic/slither |
| **Chainalysis IAPI** | Commercial investigation API (inspiration) | https://www.chainalysis.com |
| **Nansen** | Wallet labels + smart money tracking | https://nansen.ai |
| **Arkham** | Intelligence platform, entity labels | https://www.arkhamintelligence.com |
| **Dune Analytics** | SQL-based blockchain analytics | https://dune.com |
| **LlamaFolio** | Open-source portfolio tracker | https://github.com/llamafolio/llamafolio-api |
