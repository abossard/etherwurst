# 04 — Azure Developer CLI (azd) Integration

The **Azure Developer CLI** (`azd`) is a developer-first command-line tool that automates the full lifecycle of deploying Azure infrastructure and applications with a single workflow. As of early 2026 (v1.23+), `azd` has first-class support for AKS, Helm charts, and post-deploy automation — making it an excellent fit for the Etherwurst stack.

---

## What `azd` Can Do for Etherwurst

| Capability | Built-in? | How |
|---|---|---|
| Provision Azure infrastructure (AKS, storage, networking) | ✅ Yes | `azd provision` via Bicep or Terraform |
| Deploy Helm charts to AKS | ✅ Yes | `k8s.helm` section in `azure.yaml` |
| Add Helm repositories | ✅ Yes | `k8s.helm.repositories` |
| Apply Kubernetes manifests | ✅ Yes | `k8s.deploymentPath` (default: `manifests/`) |
| Kustomize support | ✅ Yes | `k8s.kustomize` section |
| Port forwarding | ⚙️ Via hooks | `postdeploy` hook running `kubectl port-forward` |
| Open browser after deploy | ⚙️ Via hooks | `postup` hook using `open`/`xdg-open`/`start` |
| End-to-end: infra + deploy in one command | ✅ Yes | `azd up` |
| CI/CD pipeline generation | ✅ Yes | `azd pipeline config` |

---

## Key Commands

```bash
# Full cycle: provision infrastructure + deploy all services
azd up

# Only provision infrastructure (AKS cluster, storage, etc.)
azd provision

# Only deploy services (Helm charts + k8s manifests)
azd deploy

# Tear down everything
azd down

# Preview infrastructure changes before applying
azd provision --preview

# Show deployed endpoints
azd show
```

---

## `azure.yaml` Structure for Etherwurst

`azd` is configured through an `azure.yaml` file at the project root. Here is how to configure it for the Etherwurst Ethereum node stack:

```yaml
# azure.yaml
name: etherwurst
metadata:
  template: etherwurst@0.1.0

infra:
  provider: bicep
  path: infra

services:
  # Erigon execution layer + Lighthouse consensus layer
  ethereum-node:
    host: aks
    k8s:
      namespace: ethereum
      deploymentPath: helm/ethereum-node
      deployment:
        name: ethereum-erigon
      service:
        name: ethereum-erigon
      helm:
        repositories:
          - name: ethereum-helm-charts
            url: https://ethpandaops.github.io/ethereum-helm-charts
        releases:
          - name: ethereum
            chart: ethereum-helm-charts/ethereum-node
            namespace: ethereum
            values: values-erigon.yaml

  # Blockscout block explorer
  blockscout:
    host: aks
    k8s:
      namespace: blockscout
      deployment:
        name: blockscout
      service:
        name: blockscout
      helm:
        releases:
          - name: blockscout
            chart: ethereum-helm-charts/blockscout
            namespace: blockscout
            values: values-blockscout.yaml

  # Prometheus + Grafana monitoring
  monitoring:
    host: aks
    k8s:
      namespace: monitoring
      helm:
        repositories:
          - name: prometheus-community
            url: https://prometheus-community.github.io/helm-charts
        releases:
          - name: kube-prometheus-stack
            chart: prometheus-community/kube-prometheus-stack
            namespace: monitoring
            values: values-monitoring.yaml
```

---

## Helm Chart Support in Detail

`azd` natively manages the full Helm lifecycle:

1. **Repository management**: `azd` runs `helm repo add` and `helm repo update` for each configured repository before deploying
2. **Install or upgrade**: `azd` runs `helm upgrade --install` for each release, making deployments idempotent
3. **Namespace creation**: `azd` creates the target namespace if it does not exist
4. **Values files**: The `values` property points to a relative path from the service directory

```yaml
k8s:
  helm:
    repositories:
      - name: ethereum-helm-charts
        url: https://ethpandaops.github.io/ethereum-helm-charts
    releases:
      - name: ethereum
        chart: ethereum-helm-charts/ethereum-node
        version: "1.0.0"          # Pin to a specific version
        namespace: ethereum
        values: helm/values-erigon.yaml
```

---

## Port Forwarding via Hooks

`azd` does not have a built-in `port-forward` command, but its **lifecycle hooks** system makes this straightforward to automate.

Hooks run shell scripts at specific points in the `azd` workflow (before/after provision, deploy, up, down, etc.).

```yaml
# In azure.yaml, at the project level
hooks:
  postup:
    shell: sh
    run: scripts/port-forward.sh
    interactive: true
```

```bash
# scripts/port-forward.sh
#!/bin/bash
set -e

echo "Getting AKS credentials..."
az aks get-credentials \
  --resource-group "${AZURE_RESOURCE_GROUP}" \
  --name "${AZURE_AKS_CLUSTER_NAME}" \
  --overwrite-existing

echo "Starting port forwards..."
# Erigon RPC
kubectl port-forward svc/ethereum-erigon 8545:8545 -n ethereum &
PF_ERIGON=$!

# Otterscan UI
kubectl port-forward svc/otterscan 5100:80 -n ethereum &
PF_OTTERSCAN=$!

# Blockscout UI
kubectl port-forward svc/blockscout 4000:4000 -n blockscout &
PF_BLOCKSCOUT=$!

# Grafana dashboard
kubectl port-forward svc/kube-prometheus-stack-grafana 3000:80 -n monitoring &
PF_GRAFANA=$!

echo ""
echo "Port forwards active:"
echo "  Erigon RPC:   http://localhost:8545"
echo "  Otterscan:    http://localhost:5100"
echo "  Blockscout:   http://localhost:4000"
echo "  Grafana:      http://localhost:3000"
echo ""
echo "Press Ctrl+C to stop all port forwards"

# Wait for any background job to exit
wait $PF_ERIGON $PF_OTTERSCAN $PF_BLOCKSCOUT $PF_GRAFANA
```

> **Note**: Set `interactive: true` on the hook so the script can hold the terminal open for long-running port forwards.

---

## Opening Browsers via Hooks

After a successful `azd up`, you can automatically open the relevant dashboards in the browser using a `postup` hook.

The hook uses platform-specific browser launch commands:
- **macOS**: `open <url>`
- **Linux**: `xdg-open <url>`
- **Windows**: `start <url>`

```yaml
# In azure.yaml
hooks:
  postup:
    - shell: sh
      run: scripts/open-browser.sh
      posix:
        run: scripts/open-browser.sh
      windows:
        shell: pwsh
        run: scripts/open-browser.ps1
```

```bash
# scripts/open-browser.sh
#!/bin/bash

# Detect OS for browser open command
if [[ "$OSTYPE" == "darwin"* ]]; then
  OPEN_CMD="open"
elif command -v xdg-open &>/dev/null; then
  OPEN_CMD="xdg-open"
else
  echo "Cannot open browser automatically on this platform."
  exit 0
fi

echo "Opening Etherwurst dashboards..."

# Small delay for port forwards to be ready
sleep 2

$OPEN_CMD "http://localhost:5100"   # Otterscan
$OPEN_CMD "http://localhost:4000"   # Blockscout
$OPEN_CMD "http://localhost:3000"   # Grafana
```

```powershell
# scripts/open-browser.ps1
Start-Sleep -Seconds 2
Start-Process "http://localhost:5100"   # Otterscan
Start-Process "http://localhost:4000"   # Blockscout
Start-Process "http://localhost:3000"   # Grafana
```

---

## Layered Provisioning (`infra.layers`)

Added in azd v1.19, **layered provisioning** solves the chicken-and-egg problem where some infrastructure (like K8s workloads) depends on other infrastructure (the AKS cluster) being provisioned first.

```yaml
# azure.yaml
infra:
  provider: bicep
  path: infra
  layers:
    - path: infra/cluster        # Layer 1: provision AKS cluster + storage
      module: main
    - path: infra/k8s-bootstrap  # Layer 2: install storage classes, RBAC (depends on layer 1)
      module: main
```

This allows you to:
1. Provision the AKS cluster and Azure resources first
2. Then apply K8s-specific resources (StorageClasses, namespaces, RBAC) that require the cluster to exist
3. Finally deploy Helm charts and application workloads

---

## Complete `azd up` Workflow for Etherwurst

When you run `azd up`, the following happens in order:

```
azd up
 │
 ├─ [1] azd provision (infra layers)
 │       ├── Layer 1: Create resource group, AKS cluster, Ultra SSD disks
 │       └── Layer 2: Apply StorageClasses, namespaces (if using layers)
 │
 ├─ [2] azd deploy (for each service in parallel where possible)
 │       ├── ethereum-node: helm repo add + helm upgrade --install ethereum-helm-charts/ethereum-node
 │       ├── blockscout:    helm upgrade --install ethereum-helm-charts/blockscout
 │       └── monitoring:    helm upgrade --install prometheus-community/kube-prometheus-stack
 │
 └─ [3] hooks (postup)
         ├── Start kubectl port-forward for local access
         └── Open Otterscan, Blockscout, Grafana in browser
```

---

## Environment Variables

`azd` automatically exports infrastructure outputs as environment variables. You can reference them in Helm values files or hooks:

```bash
# Available after azd provision:
AZURE_RESOURCE_GROUP          # e.g. "etherwurst-rg"
AZURE_LOCATION                # e.g. "westeurope"
AZURE_SUBSCRIPTION_ID         # Azure subscription ID
AZURE_AKS_CLUSTER_NAME        # AKS cluster name from Bicep output
AZURE_CONTAINER_REGISTRY_ENDPOINT  # ACR endpoint if provisioned
```

Use these in your Bicep outputs:

```bicep
// infra/main.bicep
output AZURE_AKS_CLUSTER_NAME string = aks.outputs.clusterName
output AZURE_RESOURCE_GROUP string = resourceGroup().name
```

---

## Supported `azd` Version

The features described here require:

```yaml
# azure.yaml
requiredVersions:
  azd: ">= 1.19.0"
```

| Feature | Min. Version |
|---|---|
| AKS + Helm support | 1.6.0 |
| Kustomize support | 1.6.0 |
| Layered provisioning | 1.19.0 |
| Service dependencies (`uses`) | 1.20.0 |
| Multiple hooks per event | 1.10.0 |
| `--subscription` / `--location` on `azd up` | 1.23.6 |

---

## Limitations

| Feature | Status |
|---|---|
| Native `kubectl port-forward` subcommand | ❌ Not built-in — use hooks |
| Automatic browser launch | ❌ Not built-in — use hooks |
| Otterscan/Blockscout endpoint detection | ❌ Not automatic for AKS services — use `azd show` after deploy |
| Helm chart dependency resolution (`helm dep update`) | ❌ Run as `predeploy` hook |
| `helm test` integration | ❌ Run as `postdeploy` hook |

---

## References

- `azd` docs: https://aka.ms/azd
- AKS deployment docs: https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/aks-support-overview
- `azure.yaml` schema: https://raw.githubusercontent.com/Azure/azure-dev/main/schemas/v1.0/azure.yaml.json
- `azd` GitHub releases & changelog: https://github.com/Azure/azure-dev/releases
- AKS + Helm template example: https://github.com/Azure-Samples/todo-nodejs-mongo-aks
