# 11 - Ethereum Scam Detection Playbook

This guide documents practical scam and malicious-contract detection in layers:

1. Start with simple, low-cost checks.
2. Add stronger checks that are verifiable from on-chain data.
3. Integrate in phases into this repository (`HazMeBeenScammed.Core` + adapters).

## 1. Detection Layers

### Layer A: Simple checks (MVP, cheap, fast)
These are easy to implement from transaction history and basic contract metadata.

| Signal | Why it matters | Data needed | Suggested threshold | Caveats |
|---|---|---|---|---|
| Unverified contract interaction | Many scam flows route through unknown/unverified contracts | `IsContractInteraction`, `ContractName` | Any interaction => warning | Legit new contracts can be unverified |
| Zero-value non-contract transfers | Often used as noisy bait / probing | `ValueEth`, `IsContractInteraction` | Repeated pattern (>3) | Some wallets do test txs |
| Rapid token dump | High-frequency outgoing token activity can indicate drain/rug behavior | timestamps + token transfers | >5 token tx in <10 min | Bots/arbitrage may look similar |
| Approval-like spam to unknown contracts | Common pre-drain setup | zero-value contract calls to unknown contracts | >2 in short window | Power users can interact heavily |
| Counterparty concentration | Scam funnels often use a small set of endpoints | tx graph per wallet | Top 3 counterparties >60% of interactions | Exchange users may be concentrated |
| Wallet age anomaly | Fresh wallets moving large value are riskier | first tx timestamp + value | age <7d and transfer >10 ETH | New legit wallets exist |

Implementation note:
- These checks should stay low severity unless multiple signals co-occur.

### Layer B: On-chain verifiable checks (higher confidence)
These checks should be directly provable from Ethereum state, logs, and traces.

| Signal | Chain verification method | Data source | Cost | Confidence |
|---|---|---|---|---|
| Proxy risk / suspicious upgradeability | Detect EIP-1967/UUPS/beacon/minimal proxy patterns; inspect implementation slot | `eth_getCode`, `eth_getStorageAt` | Low-Med | High |
| ERC20 transfer event inconsistency | Balance/transfer behavior without expected `Transfer` logs | `eth_call`, `eth_getTransactionReceipt` | Med | High |
| Infinite approval + immediate drain | Detect approval then fast outflow from spender | logs + timeline | Med | High |
| Function selector abuse / non-standard behavior | suspicious fallback routing, known drainer selectors | tx input + bytecode analysis | Med-High | Med-High |
| Clone of known malicious bytecode families | exact/fuzzy opcode/bytecode similarity | `eth_getCode` + local signatures | Med | High (exact), Med (fuzzy) |
| Temporal/network laundering patterns | burst fan-out, hub-and-spoke, cyclic flow | tx graph + time windows | High | Med |

Implementation note:
- Prefer deterministic checks over heuristics when possible.
- Keep an evidence trail per signal (tx hash, block, contract, selector, slot value).

## 2. Evidence and Explainability Rules

For each triggered signal, store:

- `signalId`
- `severity`
- `confidence`
- `evidence`:
  - tx hash(es)
  - contract address
  - block/time
  - exact observation (`selector=0x095ea7b3`, `implementation slot set`, etc.)

Why:
- Users need human-readable reasons, not just a score.
- Evidence is required for auditability and tuning false positives.

## 3. Risk Scoring Strategy (Recommended)

Use weighted scoring with confidence.

- `finalScore = heuristicScore * 0.45 + verifiableScore * 0.55`
- Clamp to `0..100`.

Suggested mapping:
- `0-14` => `Clean`
- `15-39` => `Suspicious`
- `40-69` => `LikelyScam`
- `70-100` => `ConfirmedScam`

Guardrails:
- Require at least one high-confidence signal before `ConfirmedScam`.
- For heuristic-only results, cap at `LikelyScam` unless repeated over time.

## 4. Data Sources

### Pure on-chain / self-hosted (preferred)
- Erigon RPC:
  - `eth_getCode`
  - `eth_getStorageAt`
  - `eth_call`
  - `eth_getTransactionReceipt`
  - traces (if enabled)
- Blockscout metadata:
  - verified contract status
  - known contract names

### External intelligence (optional enrichment)
- Forta alerts (near real-time signal)
- OFAC sanctioned addresses (compliance)
- public labels and scam lists (careful with licensing and stale data)

Rule:
- External feeds should enrich, not replace, on-chain verification.

## 5. Integration Plan for This Codebase

Current architecture already supports phased extension:

- Analyzer: `src/HazMeBeenScammed.Core/Services/ScamAnalyzer.cs`
- Port: `src/HazMeBeenScammed.Core/Ports/IBlockchainAnalyticsPort.cs`
- Adapter: `src/HazMeBeenScammed.Api/Adapters/ErigonBlockchainAdapter.cs`
- Domain model: `src/HazMeBeenScammed.Core/Domain/Models.cs`

### Phase 1: Harden MVP heuristics (quick win)

1. Keep current checks and add:
- counterparty concentration
- age anomaly
- failed tx ratio

2. Add evidence details to descriptions in `ScamIndicator` text.

3. Add tests:
- unit tests for each heuristic threshold
- integration tests for expected signal emission

Expected effort:
- Low

### Phase 2: Add verifiable contract checks

1. Extend `IBlockchainAnalyticsPort` with focused methods:
- `Task<string?> GetBytecodeAsync(string address, ...)`
- `Task<string?> GetStorageAtAsync(string address, string slot, ...)`
- `Task<TransactionReceipt?> GetTransactionReceiptAsync(string txHash, ...)`

2. Implement in real adapter first (`ErigonBlockchainAdapter`).

3. Add detectors in Core services (new classes):
- `ProxyRiskDetector`
- `ApprovalDrainerDetector`
- `EventIntegrityDetector`

4. Wire these detectors from `ScamAnalyzer`.

Expected effort:
- Medium

### Phase 3: Signature and family detection

1. Maintain local known-bad selector and bytecode signature sets.
2. Add exact + fuzzy matching scoring.
3. Add "confidence" dimension in scoring logic.

Expected effort:
- Medium-High

### Phase 4: Graph and temporal anomaly scoring

1. Reuse graph traversal workbench data model.
2. Add offline/analyzer-time pattern checks:
- burst fan-out
- cyclic flow
- sink/source concentration

Expected effort:
- High

## 6. Practical Backlog (ordered)

1. Add `confidence` to indicator model (or companion metadata).
2. Add concentration + age + failed-ratio heuristics.
3. Add proxy implementation slot check (EIP-1967).
4. Add approval-drain correlation check.
5. Add event integrity check for token transfers.
6. Add known selector/bytecode signature checks.
7. Add graph-temporal anomaly scoring.

## 7. False Positive Controls

- Use multi-signal gating: no severe verdict from one weak signal.
- Distinguish contracts used by major routers/bridges/exchanges.
- Keep thresholds configurable in `appsettings.json`.
- Track precision/recall against labeled historical cases.

## 8. First Implementation Target (Recommended)

Implement this first bundle:

1. Counterparty concentration (heuristic)
2. Age anomaly (heuristic)
3. Proxy risk slot check (verifiable)
4. Approval + fast drain correlation (verifiable)

Reason:
- Good risk coverage with manageable complexity.
- Mix of simple and high-confidence checks.
- Fits existing architecture cleanly.
