# Etherwurst Cluster — Workload Analysis Report

> Generated: 2026-03-02 | Cluster: AKS `etherwurst`

## Cluster Overview

| Node | Pool | Instance Type | vCPU | Memory | CPU% | Mem% |
|------|------|---------------|------|--------|------|------|
| aks-default-cfc7h | default | Standard_D4a_v4 | 4 | 16 GiB | 5% | 44% |
| aks-default-gqk6l | default | Standard_D2ls_v5 | 2 | 4 GiB | 43% | 79% |
| aks-default-pb9ls | default | Standard_D2ls_v5 | 2 | 4 GiB | 8% | 93% |
| aks-ethereum-slzcr | ethereum | Standard_E4as_v5 | 4 | 32 GiB | 25% | 85% |
| aks-system-… | system | Standard_B2s | 2 | 4 GiB | 62% | 59% |

**Key observations:** The default pool nodes `gqk6l` (79% mem) and `pb9ls` (93% mem) are under memory pressure. The ethereum-dedicated node is appropriately sized for heavy workloads but at 85% memory. The system node is CPU-heavy at 62%.

---

## 1. Application Workloads (User-Managed)

### 1.1 Erigon (Execution Client)

| Property | Value |
|----------|-------|
| **Kind** | StatefulSet (Helm) |
| **Namespace** | ethereum |
| **Image** | erigontech/erigon:v3.3.8 |
| **Node** | aks-ethereum-slzcr (dedicated, taint-tolerant) |
| **Storage** | 2 TiB managed-csi-premium |

| | CPU Request | CPU Limit | Mem Request | Mem Limit |
|---|---|---|---|---|
| **Configured** | 2 | — | 16 GiB | — |
| **Actual Usage** | 356m | — | 17.6 GiB | — |

**Probes:**
| Probe | Type | Port | InitialDelay | Period | Timeout |
|-------|------|------|-------------|--------|---------|
| Liveness | tcpSocket | metrics | 60s | 120s | 1s |
| Readiness | tcpSocket | http-rpc | 10s | 10s | 1s |
| Startup | — | — | — | — | — |

**Assessment:**
- **Resources:** ⚠️ Memory usage (17.6 GiB) **exceeds** the 16 GiB request with no limit set. This is possible because there's no memory limit, so the pod borrows from the node's available memory. This is risky — the OOM killer could terminate Erigon if the node gets pressured. The CPU request of 2 cores is generous but actual usage averages ~356m, meaning the request is oversized for scheduling but provides burst headroom. No CPU limit is set, which is the **recommended pattern** for Ethereum nodes that have bursty workloads during sync/reorg.
- **Probes:** TCP-only probes are appropriate for Erigon. The 60s initialDelay + 120s period on liveness is very lenient — correct because Erigon can take minutes to become responsive after startup or heavy chain operations. No startup probe is a gap: during initial sync (which can take days), the liveness probe could restart the pod. **Recommendation: Add a startup probe with a high failureThreshold (e.g., 2880 × 30s = 24h).**
- **Why configured this way:** Erigon is the most resource-intensive workload. Archive mode (`--prune.mode=archive`) demands massive storage and memory. Dedicating it to a memory-optimized node (E4as_v5, 32 GiB) with taints/affinity ensures no other pods compete. No limits allow it to use all available node resources.

---

### 1.2 Lighthouse (Consensus Client)

| Property | Value |
|----------|-------|
| **Kind** | StatefulSet (Helm) |
| **Namespace** | ethereum |
| **Image** | sigp/lighthouse:v8.1.1 |
| **Node** | aks-ethereum-slzcr (dedicated, co-located with Erigon) |
| **Storage** | 50 GiB managed-csi-premium |

| | CPU Request | CPU Limit | Mem Request | Mem Limit |
|---|---|---|---|---|
| **Configured** | 500m | — | 4 GiB | — |
| **Actual Usage** | 504m | — | 5.6 GiB | — |

**Probes:**
| Probe | Type | Port | InitialDelay | Period | Timeout |
|-------|------|------|-------------|--------|---------|
| Liveness | tcpSocket | http-api | 60s | 120s | 1s |
| Readiness | tcpSocket | http-api | 10s | 10s | 1s |
| Startup | — | — | — | — | — |

**Assessment:**
- **Resources:** ⚠️ Both CPU (504m vs 500m request) and memory (5.6 GiB vs 4 GiB request) exceed their requests. The absence of limits means it can burst, which is fine on the dedicated ethereum node. However, the memory request is clearly undersized — Lighthouse routinely needs 5–8 GiB for mainnet. **Recommendation: Increase memory request to 8 GiB** to better reflect actual usage and protect scheduling.
- **Probes:** Same pattern as Erigon — lenient TCP probes. Appropriate for a consensus client. Same gap: no startup probe for initial sync. Checkpoint sync mitigates this (sync from `beaconstate.ethstaker.cc` is fast), so the 60s initialDelay is usually sufficient.
- **Why configured this way:** Lighthouse runs alongside Erigon for low-latency consensus ↔ execution communication. Modest requests let it share the ethereum node. No limits allow burst during attestation/proposal duties. Checkpoint sync reduces startup time dramatically.

---

### 1.3 Blockscout (Block Explorer)

#### 1.3.1 Blockscout Server

| Property | Value |
|----------|-------|
| **Kind** | StatefulSet (Helm) |
| **Namespace** | blockscout |
| **Image** | blockscout/blockscout:5.1.5 |
| **Node** | aks-default-gqk6l |

| | CPU Request | CPU Limit | Mem Request | Mem Limit |
|---|---|---|---|---|
| **Configured** | 500m | 2 | 1 GiB | 4 GiB |
| **Actual Usage** | 546m | — | 276 MiB | — |

**Probes:**
| Probe | Type | Port | InitialDelay | Period | Timeout |
|-------|------|------|-------------|--------|---------|
| Liveness | tcpSocket | http | 60s | 120s | 1s |
| Readiness | tcpSocket | http | 10s | 10s | 1s |
| Startup | — | — | — | — | — |

**Assessment:**
- **Resources:** ✅ Well-configured with both requests and limits. CPU usage (546m) slightly exceeds the 500m request but stays well within the 2-core limit. Memory usage (276 MiB) is far below the 1 GiB request — the request could be reduced to 512 MiB. The 4 GiB memory limit provides headroom for indexing spikes when processing many blocks.
- **Probes:** TCP probes are safe but suboptimal. Blockscout exposes an HTTP health endpoint — using `httpGet /api/v2/health` would be more accurate (checks DB connectivity and indexer status, not just port liveness). The lenient timing mirrors Helm chart defaults.
- **Why configured this way:** Blockscout indexing is CPU-bursty (processing blockchain data) but memory-light during steady state. Limits prevent it from starving co-located workloads on the small D2ls_v5 node.

#### 1.3.2 Blockscout PostgreSQL

| | CPU Request | CPU Limit | Mem Request | Mem Limit |
|---|---|---|---|---|
| **Configured** | 250m | — | 256 MiB | — |
| **Actual Usage** | 10m | — | 280 MiB | — |

**Probes:** Liveness + Readiness via `exec` (pg_isready).

**Assessment:**
- **Resources:** ⚠️ Memory usage (280 MiB) slightly exceeds the 256 MiB request. No limits means it can grow. CPU is heavily over-provisioned. **Recommendation: Increase memory request to 512 MiB** and add a memory limit (e.g., 2 GiB) to prevent runaway queries from affecting the node. This is on `gqk6l` which is already at 79% memory.
- **Probes:** ✅ `exec pg_isready` is the gold standard for PostgreSQL health checks.

---

### 1.4 ClickHouse (Analytics DB)

| Property | Value |
|----------|-------|
| **Kind** | ClickHouseInstallation (Operator CRD) |
| **Namespace** | ethereum |
| **Image** | clickhouse/clickhouse-server:24.12 |
| **Storage** | 500 GiB managed-csi |

| | CPU Request | CPU Limit | Mem Request | Mem Limit |
|---|---|---|---|---|
| **Configured** | 2 | — | 8 GiB | — |
| **Actual Usage** | 51m | — | 1.1 GiB | — |

**Probes (operator-managed):**
| Probe | Type | Path | InitialDelay | Period | Timeout |
|-------|------|------|-------------|--------|---------|
| Liveness | httpGet | /ping | 60s | 3s | 1s |
| Readiness | httpGet | /ping | 10s | 3s | 1s |
| Startup | — | — | — | — | — |

**Assessment:**
- **Resources:** ⚠️ Massively over-provisioned. Actual usage is 51m CPU / 1.1 GiB — a fraction of the 2 CPU / 8 GiB request. The server-level `max_server_memory_usage_to_ram_ratio: 0.9` caps memory at ~14.4 GiB of whatever the pod sees. The generous request guarantees scheduling on a node with capacity, and ClickHouse will use available memory for caches under query load. Current low usage reflects light ETL activity. **Recommendation: If cost is a concern, reduce requests to 1 CPU / 4 GiB. Keep no limits to allow burst during heavy analytical queries.**
- **Probes:** ✅ `/ping` is the standard ClickHouse health endpoint. The 3s period is aggressive (appropriate for a database that should always respond). `failureThreshold: 10` on liveness gives a 30s window before restart — good for brief GC pauses.
- **Why configured this way:** ClickHouse is designed to use all available memory for query caching. High requests reserve node capacity. No limits let it burst. Profile-level memory caps (`max_memory_usage: 5 GiB`) prevent individual queries from OOM-killing the server.

---

### 1.5 Otterscan (Blockchain Explorer UI)

| Property | Value |
|----------|-------|
| **Kind** | Deployment |
| **Namespace** | ethereum |
| **Image** | otterscan/otterscan:v2.11.0 |

| | CPU Request | CPU Limit | Mem Request | Mem Limit |
|---|---|---|---|---|
| **Configured** | 100m | 500m | 128 MiB | 256 MiB |
| **Actual Usage** | 1m | — | 3 MiB | — |

**Probes:**
| Probe | Type | Path | InitialDelay | Period |
|-------|------|------|-------------|--------|
| Liveness | httpGet | / | 10s | 10s (default) |
| Readiness | httpGet | / | 5s | 10s (default) |
| Startup | — | — | — | — |

**Assessment:**
- **Resources:** ✅ Extremely lightweight static frontend — 1m CPU and 3 MiB memory. Requests and limits are conservatively set but appropriate for a containerized SPA. The 128 MiB request is generous for an nginx-served frontend but reasonable to avoid being the first eviction target.
- **Probes:** ✅ HTTP GET `/` is perfect for a static frontend. Short initialDelay values (5s/10s) match the fast startup of nginx.
- **Why configured this way:** Otterscan is a pure client-side React app served by nginx. It delegates all blockchain queries to Erigon via the browser. Tiny footprint is expected. Limits prevent a misbehaving nginx from consuming resources.

---

### 1.6 HazMeBeenScammed (Custom App)

#### 1.6.1 API Backend

| Property | Value |
|----------|-------|
| **Kind** | Deployment |
| **Namespace** | ethereum |
| **Image** | acrhazscamr3is7.azurecr.io/hazmebeenscammed-api:11588ff |

| | CPU Request | CPU Limit | Mem Request | Mem Limit |
|---|---|---|---|---|
| **Configured** | 100m | 500m | 128 MiB | 512 MiB |
| **Actual Usage** | 1m | — | 83 MiB | — |

**Probes:**
| Probe | Type | Path | InitialDelay | Period |
|-------|------|------|-------------|--------|
| Liveness | httpGet | /alive | 10s | 30s |
| Readiness | httpGet | /health | 10s | 10s |

**Assessment:**
- **Resources:** ✅ Well-tuned. ASP.NET Core app using 83 MiB idle is typical. The 512 MiB memory limit accommodates traffic spikes and in-memory caching. CPU limit of 500m is fine for an API that mostly proxies to Erigon/Blockscout.
- **Probes:** ✅ Excellent probe design. Separate liveness (`/alive` — simple "am I running?") and readiness (`/health` — dependency checks) endpoints is best practice. The 30s liveness period avoids unnecessary restarts during brief Erigon RPC timeouts, while 10s readiness ensures fast traffic routing changes.
- **Why configured this way:** Lightweight .NET API with external dependencies. Conservative resource bounds prevent runaway requests from affecting the shared node. The image tag (`11588ff`) is managed by Flux image automation.

#### 1.6.2 Web Frontend

| | CPU Request | CPU Limit | Mem Request | Mem Limit |
|---|---|---|---|---|
| **Configured** | 100m | 500m | 128 MiB | 512 MiB |
| **Actual Usage** | 2m | — | 150 MiB | — |

**Probes:** Same as API (`/alive` liveness, `/health` readiness).

**Assessment:**
- **Resources:** ✅ Blazor server-side app using 150 MiB is typical (SignalR circuits consume memory per connection). The 512 MiB limit is appropriate for moderate concurrent users. If traffic grows, this may need scaling.
- **Probes:** ✅ Same excellent pattern as the API.
- **Why configured this way:** Blazor Server maintains WebSocket connections per user. Memory usage scales with concurrent connections. Limits match the API for consistency.

---

### 1.7 Hubble UI (Cilium Network Observability)

| Property | Value |
|----------|-------|
| **Kind** | Deployment |
| **Namespace** | kube-system |
| **Containers** | frontend + backend |

| Container | CPU Req | CPU Lim | Mem Req | Mem Lim | Actual CPU | Actual Mem |
|-----------|---------|---------|---------|---------|------------|------------|
| frontend | 50m | — | 64 MiB | 256 MiB | ~1m | ~10 MiB |
| backend | 50m | — | 64 MiB | 256 MiB | ~1m | ~9 MiB |

**Probes:** ⚠️ **None configured on either container.**

**Assessment:**
- **Resources:** ✅ Lightweight observability UI. Usage is far below requests. No CPU limit is a deliberate choice — allows burst when rendering large network graphs.
- **Probes:** ❌ **Missing probes is a gap.** If the frontend nginx crashes or the backend loses gRPC connectivity to hubble-relay, Kubernetes won't detect it. **Recommendation: Add liveness/readiness httpGet probes — frontend on port 8081 path `/`, backend on port 8090 with a gRPC or TCP check.**
- **Why configured this way:** Manually deployed (not via Helm chart that would include default probes). The custom manifest omitted probes — likely an oversight.

---

### 1.8 Ethereum ETL (ClickHouse Pipeline)

| Property | Value |
|----------|-------|
| **Kind** | CronJob (*/10 * * * *) + one-shot Job |
| **Namespace** | ethereum |
| **Image** | python:3.12-slim |

| | CPU Request | CPU Limit | Mem Request | Mem Limit |
|---|---|---|---|---|
| **Configured** | 100m | 500m | 256 MiB | 512 MiB |
| **Actual Usage** | — (batch, not running) | — | — | — |

**Probes:** None (expected — Jobs don't benefit from probes).

**Assessment:**
- **Resources:** ✅ Appropriate for a Python ETL script making HTTP calls to Erigon and ClickHouse. The 512 MiB limit prevents memory leaks in long-running batches. CPU limit of 500m is fine — the bottleneck is RPC I/O, not compute.
- **Why configured this way:** Short-lived batch processing. `backoffLimit: 2` + `restartPolicy: OnFailure` handles transient Erigon/ClickHouse connectivity issues. `concurrencyPolicy: Forbid` prevents overlapping runs. `ttlSecondsAfterFinished: 3600` cleans up completed pods.

---

### 1.9 ADX ETL (Azure Data Explorer Pipeline)

| Property | Value |
|----------|-------|
| **Kind** | CronJob (daily 02:00 UTC) |
| **Namespace** | ethereum |
| **Image** | python:3.12-slim |

| | CPU Request | CPU Limit | Mem Request | Mem Limit |
|---|---|---|---|---|
| **Configured** | 250m | 1 | 512 MiB | 2 GiB |
| **Actual Usage** | — (batch, scheduled daily) | — | — | — |

**Probes:** None (expected — CronJob).

**Assessment:**
- **Resources:** ✅ Higher than ethereum-etl because ADX ETL uses batch RPC (10 parallel JSON-RPC calls), Kusto SDK (heavier Python dependencies), and writes temp files for ingestion. The 2 GiB limit accommodates large batch JSON payloads in memory.
- **Why configured this way:** Heavier workload than the ClickHouse ETL — it installs Azure SDKs at startup (`pip install`) and processes 10,000 blocks per run (vs 500 for ethereum-etl). The daily schedule aligns with ADX auto-stop to minimize cost.

---

### 1.10 Ethereum Metrics Exporter

| Property | Value |
|----------|-------|
| **Kind** | Deployment (Helm) |
| **Namespace** | monitoring |
| **Image** | samcm/ethereum-metrics-exporter:latest |

| | CPU Request | CPU Limit | Mem Request | Mem Limit |
|---|---|---|---|---|
| **Configured** | 50m | 200m | 64 MiB | 128 MiB |
| **Actual Usage** | 1m | — | 9 MiB | — |

**Probes (Helm chart defaults):**
| Probe | Type | Port |
|-------|------|------|
| Liveness | tcpSocket | metrics |
| Readiness | tcpSocket | metrics |

**Assessment:**
- **Resources:** ✅ Perfectly sized for a Prometheus exporter. Tiny footprint. Limits prevent runaway metric collection from affecting the cluster.
- **Probes:** ✅ TCP probe on the metrics port verifies the exporter is serving. Simple and effective.
- **Why configured this way:** Exporters are fire-and-forget. They scrape Erigon + Lighthouse HTTP APIs and expose Prometheus metrics. Minimal resources needed.

---

## 2. Infrastructure Workloads

### 2.1 NGINX Gateway Fabric

| | CPU Request | CPU Limit | Mem Request | Mem Limit | Actual |
|---|---|---|---|---|---|
| nginx-gateway (control) | — | — | — | — | 5m / 59 MiB |
| nginx (data plane) | — | — | — | — | 1m / 46 MiB |

**Probes:** Readiness only (httpGet) on both. No liveness or startup.

**Assessment:**
- ⚠️ **No resource requests or limits** on either container. This means the pods are BestEffort QoS — they will be **first to be evicted** under node pressure. For an ingress gateway handling production traffic, this is risky. **Recommendation: Set requests (100m CPU, 128 MiB mem) and limits (500m CPU, 512 MiB mem).**
- ⚠️ Missing liveness probes could leave zombie gateway processes running.

### 2.2 Cert-Manager

| Container | CPU Req | CPU Lim | Mem Req | Mem Lim | Actual | Probes |
|-----------|---------|---------|---------|---------|--------|--------|
| controller | — | — | — | — | 1m / 30 MiB | liveness:httpGet |
| cainjector | — | — | — | — | 1m / 45 MiB | NONE |
| webhook | — | — | — | — | 1m / 15 MiB | liveness+readiness:httpGet |

**Assessment:**
- ⚠️ **No resource requests/limits** — BestEffort QoS. Standard Helm chart defaults. While cert-manager is lightweight, the cainjector having no probes at all means certificate injection failures would go undetected. Low risk given the small footprint, but not production best practice.

### 2.3 ClickHouse Operator

| Container | CPU Req | CPU Lim | Mem Req | Mem Lim | Actual | Probes |
|-----------|---------|---------|---------|---------|--------|--------|
| operator | — | — | — | — | <1m / ~20 MiB | NONE |
| metrics-exporter | — | — | — | — | <1m / ~11 MiB | NONE |

**Assessment:**
- ⚠️ No resources, no probes. Operator default Helm values. Since it only reconciles ClickHouseInstallation CRDs (low activity), the actual impact is minimal. But BestEffort QoS means it could be evicted, which would prevent ClickHouse healing.

---

## 3. System Workloads (AKS-Managed)

These are managed by AKS and generally well-configured with appropriate probes and resources. Highlights:

| Workload | CPU Req/Lim | Mem Req/Lim | Probes | Notes |
|----------|------------|------------|--------|-------|
| **Flux controllers** (helm, kustomize, source, notification) | 100m / 1–2 | 64–128 MiB / 1 GiB | ✅ liveness+readiness | Well-configured by Flux operator |
| **Gatekeeper** (audit + 2× controller) | 100m / 2 | 256 MiB / 2–3 GiB | ✅ liveness+readiness | High limits for OPA policy evaluation |
| **Cilium** (5× DaemonSet) | — / — | — / — | ✅ liveness+readiness+startup | No resource constraints by design (critical network path) |
| **CoreDNS** (2× replicas) | 100m / 3 | 70 MiB / 500 MiB | ✅ liveness+readiness | CPU limit of 3 is very generous |
| **Azure CNS** (5× DaemonSet) | 40m / 40m | 250 MiB / 250 MiB | ✅ all three probes | Guaranteed QoS (req == lim) |
| **ama-logs** (5× DaemonSet) | 75m / 1 | 325 MiB / 1 GiB | liveness only | Large memory for log aggregation |
| **ama-metrics** (2× Deployment) | 150m / 7 | 500 MiB / 14 GiB | liveness only | Extremely high limits for Prometheus scraping |
| **Metrics Server** (2× replicas) | 153m / 153m | 120 MiB / 120 MiB | ✅ liveness+readiness | Guaranteed QoS |

---

## 4. Summary & Recommendations

### Probe Coverage

| Rating | Workloads |
|--------|-----------|
| ✅ Excellent | HazMeBeenScammed API/Web (separate /alive + /health), ClickHouse, Otterscan, Azure CNS |
| ✅ Good | Erigon, Lighthouse, Blockscout, Eth-Metrics, Flux controllers, Gatekeeper, CoreDNS |
| ⚠️ Missing startup | Erigon, Lighthouse (risk during initial sync) |
| ❌ No probes | Hubble UI, ClickHouse Operator, Cert-Manager cainjector, NGINX Gateway |
| N/A | ETL Jobs/CronJobs (probes not applicable) |

### Resource Configuration

| Rating | Workloads |
|--------|-----------|
| ✅ Well-sized | HazMeBeenScammed API/Web, Otterscan, Ethereum ETL, ADX ETL, Eth-Metrics |
| ⚠️ Under-requested | Erigon (mem 16→17.6 GiB), Lighthouse (mem 4→5.6 GiB), Blockscout PostgreSQL (256→280 MiB) |
| ⚠️ Over-requested | ClickHouse (2 CPU/8 GiB requested, uses 51m/1.1 GiB) |
| ❌ No resources | NGINX Gateway, Cert-Manager, ClickHouse Operator (BestEffort QoS) |

### Top Recommendations

1. **🔴 Critical: Erigon memory request** — Increase from 16 GiB to 20 GiB. Current usage exceeds the request, which means the scheduler doesn't account for actual needs. Risk of OOM kill under node pressure.

2. **🔴 Critical: NGINX Gateway resources** — Add resource requests/limits. As the ingress gateway, being BestEffort means production traffic could be disrupted by pod eviction.

3. **🟠 Important: Add startup probes to Erigon & Lighthouse** — These can take hours to sync. A startup probe with `failureThreshold: 2880, periodSeconds: 30` (24h window) prevents the liveness probe from killing them during sync.

4. **🟠 Important: Lighthouse memory request** — Increase from 4 GiB to 8 GiB to reflect actual 5.6 GiB usage with headroom for epoch processing spikes.

5. **🟡 Moderate: Hubble UI probes** — Add liveness/readiness probes to detect frontend/backend failures.

6. **🟡 Moderate: ClickHouse right-sizing** — Consider reducing requests to 1 CPU / 4 GiB if the analytics workload remains light.

7. **🟡 Moderate: Blockscout probes** — Upgrade from TCP to HTTP health checks (`/api/v2/health`) for deeper health validation.

8. **🟢 Low: Node memory pressure** — `aks-default-pb9ls` at 93% memory is dangerously close to eviction thresholds. Consider scaling the default nodepool to D4ls_v5 or adding another node.

---

## 5. Evidence from Live Cluster Events & Logs

The following findings are drawn from `kubectl get events`, pod restart counts, node conditions, and scheduling history observed at the time of analysis.

### 5.1 🔴 Spot Preemption Cascade (Happening NOW)

**Event timeline (19:01–19:02 UTC):**
```
19:01:30  WARNING  VMEventScheduled  aks-default-cfc7h  "Preempt Scheduled: Mon, 02 Mar 2026 19:01:45 GMT"
19:01:46  WARNING  PreemptScheduled  aks-default-cfc7h  (Azure IMDS confirmed spot eviction)
19:02:34  NORMAL   NodeNotReady      aks-default-cfc7h  "Kubelet stopped posting node status"
19:02:35  WARNING  NodeNotReady      (20+ pods affected simultaneously)
```

**Impact:** Node `aks-default-cfc7h` (Standard_D4a_v4, 4 vCPU / 16 GiB) — the **largest default pool node** — was **spot-preempted** and is now gone. This node hosted:
- `hazmebeenscammed-api`, `hazmebeenscammed-web` (production app)
- `otterscan`, `chi-ethereum-analytics` (ClickHouse)
- `clickhouse-operator`, `eth-metrics`
- Multiple Flux controllers, gatekeeper, cert-manager pods
- Cilium, CSI drivers, ama-logs/metrics agents

All 20+ pods received `NodeNotReady` warnings simultaneously. Karpenter is managing replacement, but another node `aks-default-48r78` was also removed earlier (18:55 — "Disrupting Node: Empty").

**Root cause:** All default pool nodes are **spot instances** (`SpotToSpotConsolidation is disabled`). Spot preemption on the biggest node causes massive pod rescheduling. The surviving nodes (`gqk6l` at 79% mem, `pb9ls` at 93% mem) may not have capacity for all evicted pods.

**Connection to resources/probes:** Workloads without resource requests (NGINX Gateway, cert-manager, ClickHouse operator) are BestEffort QoS and **will be evicted first** during node pressure. Workloads with proper requests (HazMeBeenScammed, Otterscan) get priority in rescheduling.

### 5.2 🔴 System Node CPU Saturation (Persistent)

**Evidence:**
```
CPUPressureIGDiagnostics  aks-system-25846890-vmss000000
  "PSI CPU some avg300: 97.42%, load: 33.37 (threshold: 1.8)"
  "CPU idle: 0.00%"
```

This event fires repeatedly every 10 minutes. The Standard_B2s (2 vCPU) system node runs at **97% CPU pressure** with a load average of 30+ (on a 2-core machine). `kubectl top` confirms 62% CPU utilization.

**Cascading effects observed:**
- `ama-metrics-node-l9ld2` (prometheus-collector): **42 restarts**, currently in `CrashLoopBackOff`. The collector can't keep up with metric scraping on a CPU-starved node.
- `azure-cns-r8sz9`: **85 restarts**. The CNS networking daemon keeps failing health probes under CPU starvation → liveness kills it → it restarts → cycle repeats.
- `coredns-autoscaler`: **46 restarts** (exit code 2). Liveness probe timeouts due to CPU starvation: `"Liveness probe failed: Get .../last-poll: context deadline exceeded"`.
- `konnectivity-agent-autoscaler`: **33 restarts** for the same reason.
- CoreDNS: Readiness probe failures causing `CoreDNSUnreachable` and `VnetDNSUnreachable` events — DNS resolution is intermittently failing cluster-wide.

**Assessment:** The B2s system node is fundamentally undersized for the AKS system workloads running on it. The monitoring agents alone (`ama-logs` + `ama-metrics-node`) request 125m CPU but their limits allow bursting to 1.2 cores. Combined with CNS, Cilium, CSI drivers, CoreDNS, konnectivity, and cloud-node-manager, the 2-vCPU budget is overwhelmed.

### 5.3 🔴 Ethereum Node I/O Pressure

**Evidence:**
```
CPUPressureIGDiagnostics  aks-ethereum-slzcr
  "iowait: 38.36% (threshold: 20%)"
  "PSI IO some avg300: 36.02"
```

The ethereum-dedicated node consistently shows **20–38% iowait**. This is Erigon's archive mode doing heavy disk I/O on the 2 TiB premium SSD. While the CPU itself isn't overloaded (idle 44%), I/O wait makes everything slower.

**Connection to resources:** Erigon using 17.6 GiB memory (over its 16 GiB request) is partly a symptom — it's trying to cache more data in RAM to compensate for slow disk reads. The `--db.pagesize=16KB` flag is an attempt to optimize I/O, but the fundamental bottleneck is disk throughput.

### 5.4 🟠 FailedScheduling — Resource Exhaustion

**Evidence (multiple pods):**
```
FailedScheduling  adx-etl-manual-1772476100-vk2z2
  "0/6 nodes: 1 Insufficient cpu, 2 untolerated taints, 4 Insufficient memory"

FailedScheduling  chi-ethereum-analytics-analytics-0-0-0
  "0/4 nodes: 1 untolerated taint, 3 Insufficient cpu, 3 Insufficient memory"

FailedScheduling  hazmebeenscammed-api-cf6c4f9b6-8s7vm
  "0/4 nodes: 1 Insufficient cpu, 1 untolerated taint, 3 Insufficient memory"
```

Multiple pods — including the production HazMeBeenScammed app and ClickHouse — experienced `FailedScheduling` at some point because no node had enough allocatable resources. Karpenter eventually provisioned new nodes, but there was a **scheduling gap**.

**Connection to resources:** The ClickHouse request of 2 CPU + 8 GiB makes it particularly hard to schedule. The ADX ETL job (250m CPU + 512 MiB) couldn't fit either, blocked by 4 nodes with insufficient memory. This validates the recommendation to right-size ClickHouse requests.

### 5.5 🟠 OOMKilled — ama-logs Agent

**Evidence:**
```
Pod: ama-logs-qsf4x (node aks-default-gqk6l)
Last State: Terminated
  Reason:   OOMKilled
  Message:  "mdsd is not running"
  Exit Code: 143
```

The Azure Monitor log agent was OOM-killed on `gqk6l` (the 4 GiB D2ls_v5 node at 79% memory). Its memory limit is 1 GiB, but the `mdsd` daemon within the container exceeded that. This node also runs Blockscout + PostgreSQL, which together consume ~556 MiB — leaving very little headroom.

**Connection to resources:** This is a direct consequence of running too many workloads on a small node. The ama-logs agent's 1 GiB limit is appropriate in isolation, but when combined with Blockscout's 1 GiB request + PostgreSQL's 256 MiB request + other DaemonSets, the 4 GiB node is overcommitted.

### 5.6 🟡 Probe-Triggered Restarts (CPU Starvation)

**Evidence from restart counts:**

| Pod | Restarts | Node | Root Cause |
|-----|----------|------|------------|
| azure-cns-r8sz9 | 85 | system (B2s) | Liveness probe timeout → kill → restart loop |
| coredns-autoscaler | 46 | system (B2s) | Same pattern — CPU starvation |
| ama-metrics-node-l9ld2 | 42 | system (B2s) | CrashLoopBackOff, 71+ restarts on `top` |
| konnectivity-agent-autoscaler | 33 | system (B2s) | Liveness timeout |
| ama-logs-bzv2w | 15 | system (B2s) | Liveness exec timeout after 15s |
| cilium-operator | 8 | system (B2s) | Liveness timeout, exit code 137 (SIGKILL) |
| gatekeeper-controller | 7 | default | Exit code 1 during node churn |

Almost all high-restart pods are on the **system node**. The pattern is consistent: CPU starvation → probes can't respond in time → liveness kills the container → restart → same problem. The probes are working as designed, but they're detecting a node-level problem (insufficient CPU), not an application-level problem.

**Key insight:** Exit code 137 (SIGKILL) on cilium-operator confirms the kernel OOM killer or Kubernetes is forcefully terminating containers. Exit code 143 (SIGTERM) on ama-logs confirms liveness probe-triggered graceful shutdown.

### 5.7 🟡 ADX ETL Job Failures

**Evidence:**
```
BackOff  adx-etl-manual-1772476100-vk2z2  "Back-off restarting failed container install-cryo"
BackOff  adx-etl-manual-run-dts8q          "Back-off restarting failed container etl"
BackOff  adx-etl-run2-pfc7l               "Back-off restarting failed container etl"
BackoffLimitExceeded  adx-etl-manual-1772476100  "Job has reached the specified backoff limit"
```

Three separate ADX ETL manual runs failed. The first had an init container `install-cryo` that failed (likely a missing binary or network issue). The subsequent runs failed at the `etl` container itself. Combined with the `FailedScheduling` event above, these jobs struggled to even get scheduled, then failed when they did.

### 5.8 Summary: Events Corroborating Resource Recommendations

| Recommendation | Supporting Evidence |
|---------------|-------------------|
| Increase Erigon memory request (16→20 GiB) | Currently using 17.6 GiB, I/O pressure at 38% iowait shows it needs more memory for caching |
| Upgrade system node from B2s | 85 restarts (azure-cns), 46 (coredns-autoscaler), 42 (ama-metrics), 97% CPU pressure, cascading DNS failures |
| Add NGINX Gateway resources | BestEffort QoS → first to be evicted during spot preemption (19:02:35 NodeNotReady cascade) |
| Right-size ClickHouse requests | FailedScheduling events: "3 Insufficient cpu, 3 Insufficient memory" — high requests blocked scheduling |
| Add startup probes to Erigon/Lighthouse | No restarts observed (stable once running), but during the spot preemption cascade, these StatefulSets on the ethereum node were unaffected — proving the dedicated node strategy works |
| Node capacity planning | 2 spot preemptions within 7 minutes (48r78 at 18:55, cfc7h at 19:02) with FailedScheduling on multiple pods |
