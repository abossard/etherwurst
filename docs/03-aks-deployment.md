# 03 — AKS Deployment & Storage

## Azure Storage Options for Ethereum Nodes

Ethereum nodes are **extremely I/O intensive**. The disk is always the bottleneck. Azure offers several storage tiers:

| Disk Type | Max IOPS | Max Throughput | Latency | Use Case |
|-----------|----------|----------------|---------|----------|
| **Ultra Disk** | 400,000 | 10,000 MB/s | Sub-ms | ⭐ Erigon hot state (domain + idx) |
| **Premium SSD v2** | 80,000 | 1,200 MB/s | Sub-ms | Good alternative, more flexible pricing |
| **Premium SSD** | 20,000 | 900 MB/s | Low | CL client, Blockscout PostgreSQL |
| **Standard SSD** | 6,000 | 750 MB/s | Medium | Erigon cold history (if budget-constrained) |

### Key Insight: Erigon's Split Storage

Erigon3 uniquely supports **putting history on a cheaper disk** while keeping hot state on fast NVMe. This saves significant cost on Azure:

```
Ultra SSD (500 GB, 50k IOPS)     ← chaindata/ + snapshots/domain/ + snapshots/idx/
Premium SSD (1.2 TB)              ← snapshots/history/ + snapshots/accessor/
```

Estimated monthly cost (West Europe):
- Ultra SSD 500GB @ 50k IOPS: ~$250/month
- Premium SSD 1.2TB: ~$150/month
- **Total storage: ~$400/month** (vs ~$2,000+/month for 12TB Geth archive on Ultra SSD)

---

## AKS Cluster Setup

### Create Resource Group and Cluster

```bash
# Create resource group
az group create --name etherwurst-rg --location westeurope

# Create AKS cluster with Ultra SSD support
az aks create \
  --resource-group etherwurst-rg \
  --name etherwurst-aks \
  --node-count 2 \
  --node-vm-size Standard_E8ds_v5 \
  --enable-ultra-ssd \
  --zones 1 \
  --network-plugin azure \
  --network-policy azure \
  --enable-managed-identity \
  --generate-ssh-keys \
  --tier standard

# Add dedicated node pool for Ethereum node (big VM, Ultra SSD)
az aks nodepool add \
  --resource-group etherwurst-rg \
  --cluster-name etherwurst-aks \
  --name ethpool \
  --node-count 1 \
  --node-vm-size Standard_E16ds_v5 \
  --enable-ultra-ssd \
  --zones 1 \
  --labels workload=ethereum \
  --node-taints ethereum=true:NoSchedule \
  --max-pods 30
```

**VM Size Recommendations:**

| VM | vCPUs | RAM | Use |
|----|-------|-----|-----|
| `Standard_E16ds_v5` | 16 | 128 GB | Erigon archive node |
| `Standard_E8ds_v5` | 8 | 64 GB | Reth or Geth full node |
| `Standard_D8ds_v5` | 8 | 32 GB | Blockscout, PostgreSQL |
| `Standard_D4ds_v5` | 4 | 16 GB | Otterscan, monitoring |

### Storage Classes

```yaml
# ultra-ssd-storage-class.yaml
apiVersion: storage.k8s.io/v1
kind: StorageClass
metadata:
  name: ultra-ssd
provisioner: disk.csi.azure.com
parameters:
  skuName: UltraSSD_LRS
  cachingMode: None          # Ultra SSD only supports None
  DiskIOPSReadWrite: "50000"
  DiskMBpsReadWrite: "1000"
reclaimPolicy: Retain        # Don't delete 2TB of synced data!
volumeBindingMode: WaitForFirstConsumer
allowVolumeExpansion: true
---
# premium-ssd-storage-class.yaml
apiVersion: storage.k8s.io/v1
kind: StorageClass
metadata:
  name: premium-ssd
provisioner: disk.csi.azure.com
parameters:
  skuName: Premium_LRS
  cachingMode: ReadOnly
reclaimPolicy: Retain
volumeBindingMode: WaitForFirstConsumer
allowVolumeExpansion: true
```

```bash
kubectl apply -f ultra-ssd-storage-class.yaml
kubectl apply -f premium-ssd-storage-class.yaml
```

---

## Helm Deployment

### ethpandaops Ethereum Helm Charts

The **ethpandaops/ethereum-helm-charts** repository is the de facto standard for deploying Ethereum clients on Kubernetes. Maintained by the Ethereum Foundation's DevOps team.

- **Repo**: https://github.com/ethpandaops/ethereum-helm-charts
- **Artifact Hub**: https://artifacthub.io/packages/search?repo=ethereum-helm-charts

```bash
helm repo add ethereum-helm-charts https://ethpandaops.github.io/ethereum-helm-charts
helm repo update
```

#### Available Charts

| Chart | Description |
|-------|-------------|
| `ethereum-node` | Umbrella: deploys EL + CL together |
| `geth` | Go Ethereum |
| `reth` | Paradigm's Rust Ethereum |
| `erigon` | Erigon |
| `nethermind` | Nethermind (.NET) |
| `lighthouse` | Lighthouse CL (Rust) |
| `prysm` | Prysm CL (Go) |
| `teku` | Teku CL (Java) |
| `blockscout` | Block explorer |
| `ethereum-metrics-exporter` | Prometheus metrics |

### Deploy Erigon + Lighthouse

```yaml
# values-erigon.yaml
global:
  main:
    network: mainnet

erigon:
  enabled: true
  image:
    repository: thorax/erigon
    tag: latest
  resources:
    requests:
      cpu: "8"
      memory: "32Gi"
    limits:
      cpu: "16"
      memory: "64Gi"
  persistence:
    enabled: true
    storageClassName: ultra-ssd
    size: 2Ti
    accessModes:
      - ReadWriteOnce
  extraArgs:
    - --prune.mode=archive
    - --http
    - --http.addr=0.0.0.0
    - --http.api=eth,net,web3,trace,debug,txpool,erigon,ots
    - --http.vhosts=*
    - --http.corsdomain=*
    - --ws
    - --ws.compression
    - --metrics
    - --metrics.addr=0.0.0.0
  nodeSelector:
    workload: ethereum
  tolerations:
    - key: ethereum
      operator: Equal
      value: "true"
      effect: NoSchedule

lighthouse:
  enabled: true
  resources:
    requests:
      cpu: "2"
      memory: "8Gi"
    limits:
      cpu: "4"
      memory: "16Gi"
  persistence:
    enabled: true
    storageClassName: premium-ssd
    size: 300Gi
```

```bash
# Deploy the full Ethereum node
helm install ethereum ethereum-helm-charts/ethereum-node \
  -f values-erigon.yaml \
  --namespace ethereum \
  --create-namespace

# Deploy Otterscan (just a frontend)
kubectl create deployment otterscan \
  --image=otterscan/otterscan:latest \
  --namespace=ethereum \
  -- env ERIGON_URL=http://ethereum-erigon:8545

kubectl expose deployment otterscan \
  --port=80 --target-port=80 \
  --namespace=ethereum
```

### Deploy Blockscout

Blockscout has its own Helm chart and Kubernetes deployment guide:

```bash
# Option 1: Use ethpandaops chart
helm install blockscout ethereum-helm-charts/blockscout \
  --namespace blockscout \
  --create-namespace \
  --set config.ETHEREUM_JSONRPC_HTTP_URL=http://ethereum-erigon.ethereum:8545 \
  --set config.ETHEREUM_JSONRPC_TRACE_URL=http://ethereum-erigon.ethereum:8545 \
  --set config.ETHEREUM_JSONRPC_WS_URL=ws://ethereum-erigon.ethereum:8546

# Option 2: Use Blockscout's official deployment
# See: https://docs.blockscout.com/for-developers/deployment/kubernetes-deployment
```

---

## Monitoring Stack

```bash
# Prometheus metrics exporter for Ethereum nodes
helm install eth-metrics ethereum-helm-charts/ethereum-metrics-exporter \
  --namespace monitoring \
  --set config.executionUrl=http://ethereum-erigon.ethereum:8545 \
  --set config.consensusUrl=http://ethereum-lighthouse.ethereum:5052
```

Pair with Grafana dashboards for:
- Sync status and block height
- Peer count
- RPC request latency
- Disk usage and IOPS
- Memory and CPU utilization

---

## Estimated Monthly Costs (West Europe)

| Component | VM / Disk | Cost/month |
|-----------|-----------|------------|
| Erigon node | E16ds_v5 (16 vCPU, 128GB) | ~$800 |
| Erigon hot storage | Ultra SSD 500GB @ 50k IOPS | ~$250 |
| Erigon cold storage | Premium SSD 1.2TB | ~$150 |
| CL storage | Premium SSD 300GB | ~$40 |
| Blockscout/services | D8ds_v5 (8 vCPU, 32GB) | ~$400 |
| PostgreSQL (Blockscout) | Premium SSD 500GB | ~$65 |
| AKS control plane | Standard tier | ~$75 |
| **Total** | | **~$1,780/month** |

> Compare with running a Geth archive node: 12TB Ultra SSD alone would cost ~$5,000+/month. Erigon's small archive size is a massive cost advantage.
