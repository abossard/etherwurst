# 13 - One-Time AKS Erigon PVC Migration (Same Region)

This runbook is for a one-time move of Erigon data disk (multi-TB) from one AKS cluster to another in the same Azure region, using Azure-managed operations only.

Companion helper script: `migrate-erigon-pvc-once.sh`

## Outcome

- Move an existing Azure Disk-backed PVC to a new AKS cluster.
- Avoid copying data through local machines.
- Keep downtime limited to cutover window only.

## Scope and assumptions

- Source and target AKS clusters are in the same Azure region.
- Erigon data PVC is Azure Disk CSI (`disk.csi.azure.com`).
- Migration is one-time, not continuous replication.
- You accept a short write-stop cutover window.

## Strategy

- Stop Erigon writes on source cluster.
- Confirm managed disk is `Unattached`.
- Move managed disk resource to target cluster node RG/subscription as needed.
- Bind disk as static PV/PVC on target cluster.
- Start Erigon and validate chain state.

Reference: `https://learn.microsoft.com/en-us/azure/aks/csi-disk-move-subscriptions`

## Pre-cutover checklist

- Snapshot exists for rollback.
- Source and target AKS versions are compatible.
- Target cluster has node pool/VM SKU able to attach the disk.
- Zone constraints are known (`disk.zone` and node zone).
- You can access both clusters and Azure subscription with CLI.

## Variables

Set these before execution:

```bash
export SRC_AKS_RG="<source-aks-rg>"
export SRC_AKS_NAME="<source-aks-name>"
export TGT_AKS_RG="<target-aks-rg>"
export TGT_AKS_NAME="<target-aks-name>"

export NS="ethereum"
export PVC_NAME="<erigon-pvc-name>"

export DISK_NAME="<managed-disk-name>"
export DISK_ID="<managed-disk-resource-id>"

export TARGET_NODE_RG="<target-aks-node-resource-group>"
```

Optional helper usage:

```bash
# Read-only plan
./migrate-erigon-pvc-once.sh --mode plan \
  --source-context <source-context> \
  --target-context <target-context> \
  --pvc <erigon-pvc-name>

# Perform cutover actions (snapshot + stop + move + manifest emit)
./migrate-erigon-pvc-once.sh --mode execute \
  --source-context <source-context> \
  --target-context <target-context> \
  --source-aks-rg <source-aks-rg> \
  --source-aks-name <source-aks-name> \
  --target-aks-rg <target-aks-rg> \
  --target-aks-name <target-aks-name> \
  --namespace ethereum \
  --statefulset erigon \
  --pvc <erigon-pvc-name>

# Or provide target node RG directly
./migrate-erigon-pvc-once.sh --mode execute \
  --source-context <source-context> \
  --target-context <target-context> \
  --source-aks-rg <source-aks-rg> \
  --source-aks-name <source-aks-name> \
  --target-node-rg <target-node-resource-group> \
  --pvc <erigon-pvc-name>

# Live observation loop
./migrate-erigon-pvc-once.sh --mode observe \
  --target-context <target-context> \
  --namespace ethereum \
  --watch-seconds 20
```

Concrete examples from current environment (same region `swedencentral`):

- Source context/cluster: `aks-hazscam-c3npe`
- Source RG: `rg-hazscam-dev`
- Target context/cluster (example): `aks-demo-light`
- Target RG: `anbo-aks-demo`

```bash
# 1) Plan with concrete source/target (replace PVC before running)
./migrate-erigon-pvc-once.sh --mode plan \
  --source-context aks-hazscam-c3npe \
  --target-context aks-demo-light \
  --source-aks-rg rg-hazscam-dev \
  --source-aks-name aks-hazscam-c3npe \
  --target-aks-rg anbo-aks-demo \
  --target-aks-name aks-demo-light \
  --namespace ethereum \
  --statefulset erigon \
  --pvc <replace-with-erigon-pvc-name>

# 2) Execute (interactive confirmation)
./migrate-erigon-pvc-once.sh --mode execute \
  --source-context aks-hazscam-c3npe \
  --target-context aks-demo-light \
  --source-aks-rg rg-hazscam-dev \
  --source-aks-name aks-hazscam-c3npe \
  --target-aks-rg anbo-aks-demo \
  --target-aks-name aks-demo-light \
  --namespace ethereum \
  --statefulset erigon \
  --pvc <replace-with-erigon-pvc-name>

# 3) Observe target during/after cutover
./migrate-erigon-pvc-once.sh --mode observe \
  --source-context aks-hazscam-c3npe \
  --target-context aks-demo-light \
  --namespace ethereum \
  --watch-seconds 20
```

If target will be `anbo-aks-demo` instead, only replace:

- `--target-context anbo-aks-demo`
- `--target-aks-name anbo-aks-demo`

## Execution steps

### 1) Identify source disk from PVC

```bash
kubectl config use-context <source-context>
kubectl -n "$NS" get pvc "$PVC_NAME" -o wide
kubectl -n "$NS" get pv
```

Capture `volumeHandle` (disk resource ID) from the bound PV:

```bash
kubectl get pv <pv-name> -o jsonpath='{.spec.csi.volumeHandle}{"\n"}'
```

### 2) Create rollback snapshot before cutover

```bash
az snapshot create \
  --name "${DISK_NAME}-pre-migration-$(date +%Y%m%d%H%M)" \
  --resource-group "$(echo "$DISK_ID" | awk -F/ '{print $5}')" \
  --source "$DISK_ID"
```

### 3) Stop Erigon writes and detach disk

For HelmRelease-based deployment, suspend reconciliation to avoid immediate reattach loops:

```bash
kubectl config use-context <source-context>
flux suspend helmrelease erigon -n ethereum
kubectl -n ethereum scale statefulset erigon --replicas=0
kubectl -n ethereum get pods -w
```

Wait until disk state becomes `Unattached`:

```bash
az disk show --ids "$DISK_ID" --query diskState -o tsv
```

### 4) Move disk to target AKS node resource group

If source and target are same subscription, move RG-to-RG. If different subscription, include `--destination-subscription-id`.

```bash
SRC_NODE_RG=$(az aks show -g "$SRC_AKS_RG" -n "$SRC_AKS_NAME" --query nodeResourceGroup -o tsv)
az resource move \
  --destination-group "$TARGET_NODE_RG" \
  --ids "$DISK_ID"
```

Validate move:

```bash
az disk list -g "$TARGET_NODE_RG" --query "[?id=='$DISK_ID' || name=='$DISK_NAME'].{name:name,id:id,diskState:diskState,zone:zones}" -o table
```

### 5) Create static PV/PVC on target cluster

```bash
kubectl config use-context <target-context>
```

Create `pv-erigon-migrated.yaml`:

```yaml
apiVersion: v1
kind: PersistentVolume
metadata:
  name: pv-erigon-migrated
spec:
  capacity:
    storage: 2Ti
  accessModes:
    - ReadWriteOnce
  persistentVolumeReclaimPolicy: Retain
  storageClassName: managed-csi-premium
  csi:
    driver: disk.csi.azure.com
    volumeHandle: <MOVED_DISK_RESOURCE_ID>
    fsType: ext4
```

Create `pvc-erigon-migrated.yaml`:

```yaml
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: erigon-data-migrated
  namespace: ethereum
spec:
  accessModes:
    - ReadWriteOnce
  resources:
    requests:
      storage: 2Ti
  volumeName: pv-erigon-migrated
  storageClassName: managed-csi-premium
```

Apply and verify binding:

```bash
kubectl apply -f pv-erigon-migrated.yaml
kubectl apply -f pvc-erigon-migrated.yaml
kubectl -n ethereum get pvc erigon-data-migrated
kubectl get pv pv-erigon-migrated
```

### 6) Point Erigon workload to migrated PVC and start

- Update Helm values to reference the migrated claim if your chart supports `existingClaim`.
- If not supported, deploy a one-time manifest patch for StatefulSet volume claim mapping.
- Resume Flux reconciliation only after manifests are correct.

```bash
flux resume helmrelease erigon -n ethereum
kubectl -n ethereum rollout status statefulset/erigon --timeout=30m
```

## Observe and validate

## Kubernetes signals

```bash
kubectl -n ethereum get pods -o wide
kubectl -n ethereum get events --sort-by=.lastTimestamp | tail -n 40
kubectl -n ethereum describe pod erigon-0
kubectl -n ethereum logs statefulset/erigon --tail=200
```

Watch for:

- PVC is `Bound`.
- No `FailedAttachVolume`, `MountVolume` errors.
- Pod reaches `Ready` without crash loops.

## Azure disk signals

```bash
az disk show --ids "$DISK_ID" --query "{state:diskState,managedBy:managedBy,zone:zones,sku:sku.name,sizeGB:diskSizeGb}" -o json
```

Watch for:

- `diskState` becomes `Attached` on target node.
- `managedBy` points to target VMSS instance.

## Erigon app signals

```bash
kubectl -n ethereum port-forward svc/erigon 8545:8545
curl -s -X POST http://127.0.0.1:8545 \
  -H 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":1}'
```

Watch for:

- RPC responds.
- Block height catches up and continues to advance.
- Peer count and sync state are healthy via your Grafana/Prometheus dashboards.

## Rollback

- Keep source HelmRelease suspended until target is validated.
- If target fails, recreate disk from pre-migration snapshot and rebind on source.
- Resume source workload only after PVC attach is confirmed.

## Common gotchas

- Disk zone and node zone mismatch blocks scheduling/attach.
- StatefulSet recreates claim unexpectedly if chart values are not pinned.
- Flux reconciles old values and reverts manual changes if not committed.
- Existing resources in target namespace conflict with restore/mount names.

## Final go/no-go checklist

- Pre-cutover snapshot taken.
- Source pod scaled down and disk `Unattached`.
- Disk move completed and visible in target node RG.
- Target PV/PVC `Bound`.
- Erigon pod `Ready` and RPC healthy.
- Chain height progressing.
