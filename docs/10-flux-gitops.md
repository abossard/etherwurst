# 09 — Flux CD: GitOps Cluster Management with the New UI

## What is Flux?

**Flux** is a CNCF **Graduated** GitOps tool that keeps Kubernetes clusters in sync with Git repositories (or OCI registries / S3 buckets). You declare your desired cluster state in Git, and Flux continuously reconciles the cluster to match.

- **Repo**: https://github.com/fluxcd/flux2
- **Docs**: https://fluxcd.io/flux/
- **License**: Apache 2.0
- **Status**: Production-grade, used by major enterprises and cloud providers

---

## The New: Flux Operator + Web UI

The big news is the **Flux Operator** (`controlplaneio-fluxcd/flux-operator`), which replaces the traditional `flux bootstrap` workflow with a **Kubernetes-native operator** and includes a **brand new Web UI** embedded directly in the operator.

- **Repo**: https://github.com/controlplaneio-fluxcd/flux-operator
- **Docs**: https://fluxoperator.dev
- **Web UI**: https://fluxoperator.dev/web-ui/

### What the Flux Operator Adds Over Plain Flux

| Feature | `flux bootstrap` (traditional) | Flux Operator (new) |
|---------|-------------------------------|---------------------|
| Installation | CLI bootstrap into Git | Helm chart + `FluxInstance` CRD |
| Management | Manual CLI / Git edits | Declarative CRD, auto-reconciles |
| Upgrades | Manual `flux bootstrap` re-run | Automatic, operator manages lifecycle |
| Web UI | None (CLI only) | ✅ Built-in, real-time dashboard |
| MCP Server | No | ✅ AI assistant integration |
| ResourceSets | No | ✅ App definitions, PR environments |
| Deployment windows | No | ✅ Time-based delivery |
| Fleet management | Manual per-cluster | Operator-driven, scales to fleets |
| Scaling/sharding | Manual config | Declarative cluster size (`small`/`medium`/`large`) |
| Monitoring | `flux get` CLI | Web UI + Prometheus metrics + `FluxReport` CRD |
| SSO | N/A | ✅ OIDC with Kubernetes RBAC |

---

## The Web UI Features

The Flux Operator Web UI is a **lightweight, mobile-friendly dashboard** embedded directly in the operator (no separate install). It provides:

### Cluster Dashboard
- Real-time status of all Flux controllers
- Reconciliation activity feed
- Stats: Kustomizations, HelmReleases, Sources

### GitOps Pipeline Graph
- **Interactive dependency graph** — visualize source → kustomization → helm release → workload
- Real-time status updates as resources reconcile
- Instantly spot failures in the pipeline

### Resource Dashboards
- Dedicated views for HelmReleases, Kustomizations, Sources
- Revision history, applied values, conditions/errors
- **Actions**: trigger reconcile, suspend, resume (guarded by K8s RBAC)

### Advanced Search & Favorites
- Filter by type, namespace, status, name
- Pin important resources for quick monitoring

### Reconciliation History
- Track changes over time
- See when resources were updated and what changed

### SSO / OIDC
- OpenID Connect integration (Dex, Keycloak, Azure AD, etc.)
- Maps to Kubernetes RBAC — users see only what they're authorized to
- Predefined roles: `flux-web-user` (read-only) and `flux-web-admin` (full control)

### Access

```bash
# Quick access via port-forward
kubectl -n flux-system port-forward svc/flux-operator 9080:9080
# Then open http://localhost:9080

# Production: use Ingress with TLS
# See Helm values below
```

---

## 🤖 Flux MCP Server (AI Integration)

A standout feature: the **Flux MCP Server** connects AI assistants (Claude, Copilot, Gemini) directly to your Kubernetes cluster via the Model Context Protocol.

This means you can ask your AI assistant natural language questions about your cluster:
- *"What's the status of my Ethereum deployment?"*
- *"Compare staging vs production Helm release values"*
- *"Why did the last reconciliation fail?"*
- *"Show me the dependency graph for the ethereum namespace"*

The MCP server:
- Has a **read-only mode** for safe observation
- Masks secrets automatically
- Respects kubeconfig permissions
- Supports Kubernetes impersonation for limited access

**Docs**: https://fluxoperator.dev/mcp-server/

---

## Should You Use Flux for Etherwurst?

### ✅ YES — Strong Recommendation

Here's why Flux Operator is an excellent fit for managing the Etherwurst AKS cluster:

**1. GitOps for Complex Infra**
The Etherwurst stack has many components (Erigon, Lighthouse, Otterscan, Blockscout, cryo CronJobs, monitoring). Managing these imperatively with `helm install` commands is fragile. With Flux:
- All Helm releases are declared in Git
- Changes are reviewed via PR
- Cluster always matches Git state
- Rollbacks = `git revert`

**2. Helm Release Management**
Flux has first-class Helm support. Every component in the stack uses Helm charts:
```yaml
# Example: Erigon HelmRelease managed by Flux
apiVersion: helm.toolkit.fluxcd.io/v2
kind: HelmRelease
metadata:
  name: erigon
  namespace: ethereum
spec:
  interval: 1h
  chart:
    spec:
      chart: erigon
      sourceRef:
        kind: HelmRepository
        name: ethereum-helm-charts
      version: ">=0.1.0"
  values:
    persistence:
      enabled: true
      storageClassName: ultra-ssd
      size: 2Ti
    extraArgs:
      - --prune.mode=archive
      - --http.api=eth,net,web3,trace,debug,txpool,erigon,ots
```

**3. The Web UI Replaces Need for Lens/Rancher**
Instead of deploying a separate Kubernetes dashboard, the Flux Web UI gives you:
- GitOps pipeline visualization
- Deployment status at a glance
- Reconciliation history
- Action triggers (reconcile, suspend, resume)

**4. MCP Server = AI-Assisted Ops**
Since we're already building AI agents, having the Flux MCP Server means those agents can also manage and monitor the infrastructure itself — not just blockchain data.

**5. Azure DevOps / GitHub Integration**
Flux Operator has native support for:
- GitHub (including GitHub App auth)
- Azure DevOps with AKS Workload Identity
- GitLab, Gitea, Forgejo

**6. ResourceSets for Environment Management**
If you later want staging/production environments or PR preview environments for the analytics layer, ResourceSets handle this natively.

---

## Installation for Etherwurst

### Step 1: Install Flux Operator via Helm

```bash
helm install flux-operator oci://ghcr.io/controlplaneio-fluxcd/charts/flux-operator \
  --namespace flux-system \
  --create-namespace \
  --set web.ingress.enabled=true \
  --set web.ingress.className=nginx \
  --set web.ingress.hosts[0].host=flux.etherwurst.example.com \
  --set web.ingress.hosts[0].paths[0].path=/ \
  --set web.ingress.hosts[0].paths[0].pathType=Prefix
```

### Step 2: Create FluxInstance (install Flux controllers)

```yaml
# flux-instance.yaml
apiVersion: fluxcd.controlplane.io/v1
kind: FluxInstance
metadata:
  name: flux
  namespace: flux-system
spec:
  distribution:
    version: "2.x"
    registry: "ghcr.io/fluxcd"
    artifact: "oci://ghcr.io/controlplaneio-fluxcd/flux-operator-manifests"
  components:
    - source-controller
    - kustomize-controller
    - helm-controller
    - notification-controller
  cluster:
    type: kubernetes
    size: medium
    multitenant: false
    networkPolicy: true
  sync:
    kind: GitRepository
    url: "https://github.com/YOUR_ORG/etherwurst-fleet.git"
    ref: "refs/heads/main"
    path: "clusters/etherwurst"
    pullSecret: "flux-system"
```

```bash
kubectl apply -f flux-instance.yaml
```

### Step 3: Create Git Secret

```bash
flux create secret git flux-system \
  --url=https://github.com/YOUR_ORG/etherwurst-fleet.git \
  --username=git \
  --password=$GITHUB_TOKEN
```

### Step 4: Structure Your Fleet Repository

```
etherwurst-fleet/
├── clusters/
│   └── etherwurst/
│       ├── flux-system/          # Auto-managed by Flux
│       ├── sources.yaml          # HelmRepository definitions
│       ├── infrastructure/       # Storage classes, namespaces, RBAC
│       │   ├── kustomization.yaml
│       │   ├── namespaces.yaml
│       │   └── storage-classes.yaml
│       └── apps/                 # Application HelmReleases
│           ├── kustomization.yaml
│           ├── erigon.yaml       # Erigon HelmRelease
│           ├── lighthouse.yaml   # Lighthouse HelmRelease
│           ├── otterscan.yaml    # Otterscan Deployment
│           ├── blockscout.yaml   # Blockscout HelmRelease
│           ├── monitoring.yaml   # Prometheus + Grafana
│           └── cryo-job.yaml     # CronJob for data export
```

### Step 5: Example sources.yaml

```yaml
apiVersion: source.toolkit.fluxcd.io/v1
kind: HelmRepository
metadata:
  name: ethereum-helm-charts
  namespace: flux-system
spec:
  interval: 1h
  url: https://ethpandaops.github.io/ethereum-helm-charts
---
apiVersion: source.toolkit.fluxcd.io/v1
kind: HelmRepository
metadata:
  name: blockscout
  namespace: flux-system
spec:
  interval: 1h
  url: https://blockscout.github.io/helm-charts
```

### Step 6: Verify via Web UI

```bash
# Port-forward to access the UI
kubectl -n flux-system port-forward svc/flux-operator 9080:9080

# Open http://localhost:9080
# You should see:
# - All Flux controllers healthy
# - GitRepository synced
# - HelmReleases reconciling
# - Workloads deploying
```

---

## Flux vs Alternatives

| | **Flux + Operator** | **ArgoCD** | **Helm CLI** | **Terraform** |
|---|---|---|---|---|
| GitOps | ✅ Native | ✅ Native | ❌ Imperative | ⚠️ With state file |
| K8s-native CRDs | ✅ | ✅ | ❌ | ❌ |
| Web UI | ✅ Lightweight, embedded | ✅ Full-featured | ❌ | ❌ |
| Helm support | ✅ First-class | ✅ | ✅ (it IS Helm) | ✅ via provider |
| Multi-tenancy | ✅ | ✅ | ❌ | ❌ |
| AI integration (MCP) | ✅ | ❌ | ❌ | ❌ |
| CNCF status | Graduated | Graduated | Graduated | N/A |
| Resource footprint | Low (~4 controllers) | Higher (server + repo-server + Redis + Dex) | None | None |
| Azure DevOps | ✅ Native | ✅ | N/A | N/A |
| PR environments | ✅ ResourceSets | ✅ ApplicationSets | ❌ | ❌ |

**Why Flux over ArgoCD for this project:**
- Lighter footprint (no Redis, no separate server)
- The new Web UI closes the gap with ArgoCD's UI
- MCP Server is unique to Flux — AI assistants can manage your cluster
- ResourceSets are cleaner than ApplicationSets for our use case
- Better Kustomize integration (Flux was built around it)

---

## Summary

| Aspect | Verdict |
|--------|---------|
| Use Flux for Etherwurst? | **Yes, strongly recommended** |
| Use Flux Operator (not plain bootstrap)? | **Yes — Web UI, auto-upgrades, ResourceSets** |
| Replace a separate K8s dashboard? | **Yes — Flux Web UI covers GitOps monitoring** |
| AI integration for cluster ops? | **Yes — MCP Server is unique and powerful** |
| Maturity risk? | **Low — Flux is CNCF Graduated, Operator is by ControlPlane (Flux maintainers)** |

---

## References

- Flux CD: https://fluxcd.io
- Flux Operator: https://fluxoperator.dev
- Flux Operator Web UI: https://fluxoperator.dev/web-ui/
- Flux MCP Server: https://fluxoperator.dev/mcp-server/
- Flux Operator Helm Chart: `oci://ghcr.io/controlplaneio-fluxcd/charts/flux-operator`
- Flux Operator GitHub: https://github.com/controlplaneio-fluxcd/flux-operator
- ResourceSets: https://fluxoperator.dev/docs/resourcesets/introduction/
- SSO / User Management: https://fluxoperator.dev/docs/web-ui/user-management/
- Ingress Configuration: https://fluxoperator.dev/docs/web-ui/ingress/
- Get Started with Flux: https://fluxcd.io/flux/get-started/
- Azure DevOps + Workload Identity: https://fluxoperator.dev/docs/instance/sync/
