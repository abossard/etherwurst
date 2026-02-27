#!/usr/bin/env bash
#
# Etherwurst Cluster Setup Script
#
# Installs and configures the Flux Operator, applies infrastructure,
# and bootstraps the full GitOps pipeline on an AKS cluster.
#
# Usage:
#   ./setup.sh              # Full install/update
#   ./setup.sh --dry-run    # Validate only, change nothing
#   ./setup.sh --status     # Show current state
#   ./setup.sh --teardown   # Remove Flux and all managed resources
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CLUSTERS_DIR="${SCRIPT_DIR}/clusters/etherwurst"
FLUX_NAMESPACE="flux-system"
DRY_RUN=false
STATUS_ONLY=false
TEARDOWN=false

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

log()  { echo -e "${BLUE}[INFO]${NC} $*"; }
ok()   { echo -e "${GREEN}[OK]${NC}   $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()  { echo -e "${RED}[ERR]${NC}  $*"; }

# Parse args
for arg in "$@"; do
  case "$arg" in
    --dry-run)   DRY_RUN=true ;;
    --status)    STATUS_ONLY=true ;;
    --teardown)  TEARDOWN=true ;;
    --help|-h)
      echo "Usage: $0 [--dry-run|--status|--teardown]"
      exit 0
      ;;
    *) err "Unknown argument: $arg"; exit 1 ;;
  esac
done

# ─── Preflight Checks ───────────────────────────────────────────────
preflight() {
  log "Running preflight checks..."

  local missing=()
  for cmd in kubectl helm flux kustomize; do
    if ! command -v "$cmd" &>/dev/null; then
      missing+=("$cmd")
    fi
  done
  if [ ${#missing[@]} -ne 0 ]; then
    err "Missing required tools: ${missing[*]}"
    err "Install with: brew install kubectl helm fluxcd/tap/flux kustomize"
    exit 1
  fi
  ok "All tools available (kubectl, helm, flux, kustomize)"

  if ! kubectl cluster-info &>/dev/null; then
    err "Cannot connect to Kubernetes cluster. Check your kubeconfig."
    exit 1
  fi
  local ctx
  ctx=$(kubectl config current-context)
  ok "Connected to cluster: ${ctx}"

  local server_version
  server_version=$(kubectl version -o json 2>/dev/null | python3 -c "import sys,json; v=json.load(sys.stdin)['serverVersion']; print(f\"{v['major']}.{v['minor']}\")" 2>/dev/null || echo "unknown")
  log "  Kubernetes server version: ${server_version}"

  local node_count
  node_count=$(kubectl get nodes --no-headers 2>/dev/null | wc -l | tr -d ' ')
  log "  Nodes: ${node_count}"
}

# ─── Validate YAMLs ─────────────────────────────────────────────────
validate() {
  log "Validating manifests..."

  # 1. YAML syntax
  local yaml_errors=0
  for f in $(find "${CLUSTERS_DIR}" -name '*.yaml' | sort); do
    if ! python3 -c "import yaml; list(yaml.safe_load_all(open('$f')))" 2>/dev/null; then
      err "Invalid YAML: ${f}"
      yaml_errors=$((yaml_errors + 1))
    fi
  done
  if [ "$yaml_errors" -gt 0 ]; then
    err "${yaml_errors} YAML syntax error(s). Fix before proceeding."
    exit 1
  fi
  ok "All YAML files parse correctly"

  # 2. Kustomize build
  for layer in infrastructure apps monitoring; do
    local layer_dir="${CLUSTERS_DIR}/${layer}"
    if [ -d "$layer_dir" ]; then
      if ! kustomize build "$layer_dir" > /dev/null 2>&1; then
        err "Kustomize build failed for ${layer}"
        kustomize build "$layer_dir" 2>&1
        exit 1
      fi
      ok "Kustomize build: ${layer}"
    fi
  done

  # 3. Server-side dry-run for native resources
  local native_files=(
    "${CLUSTERS_DIR}/infrastructure/namespaces.yaml"
    "${CLUSTERS_DIR}/infrastructure/storage-classes.yaml"
  )
  for f in "${native_files[@]}"; do
    if [ -f "$f" ]; then
      if ! kubectl apply --dry-run=server -f "$f" &>/dev/null; then
        err "Server dry-run failed: ${f}"
        kubectl apply --dry-run=server -f "$f" 2>&1
        exit 1
      fi
      ok "Server dry-run: $(basename "$f")"
    fi
  done
}

# ─── Show Status ─────────────────────────────────────────────────────
status() {
  log "Cluster state:"
  echo ""

  # Flux Operator
  if kubectl get deployment flux-operator -n "${FLUX_NAMESPACE}" &>/dev/null; then
    ok "Flux Operator: installed"
    kubectl get deployment flux-operator -n "${FLUX_NAMESPACE}" --no-headers 2>/dev/null | sed 's/^/     /'
  else
    warn "Flux Operator: not installed"
  fi
  echo ""

  # Flux controllers
  if kubectl get fluxinstances -n "${FLUX_NAMESPACE}" &>/dev/null 2>&1; then
    ok "FluxInstance:"
    kubectl get fluxinstances -n "${FLUX_NAMESPACE}" --no-headers 2>/dev/null | sed 's/^/     /'
    echo ""
    log "Flux controllers:"
    kubectl get deployments -n "${FLUX_NAMESPACE}" --no-headers 2>/dev/null | sed 's/^/     /'
  else
    warn "Flux CRDs: not installed"
  fi
  echo ""

  # Namespaces
  log "Etherwurst namespaces:"
  for ns in ethereum blockscout monitoring; do
    if kubectl get ns "$ns" &>/dev/null 2>&1; then
      ok "  ${ns}: exists"
    else
      warn "  ${ns}: not found"
    fi
  done
  echo ""

  # Storage classes
  log "Custom storage classes:"
  for sc in premium-ssd-v2 premium-ssd-retain; do
    if kubectl get sc "$sc" &>/dev/null 2>&1; then
      ok "  ${sc}: exists"
    else
      warn "  ${sc}: not found"
    fi
  done
  echo ""

  # HelmReleases
  if kubectl get helmreleases -A &>/dev/null 2>&1; then
    log "HelmReleases:"
    kubectl get helmreleases -A --no-headers 2>/dev/null | sed 's/^/     /' || warn "  none found"
  fi
  echo ""

  # Workloads
  for ns in ethereum blockscout monitoring; do
    if kubectl get ns "$ns" &>/dev/null 2>&1; then
      local pods
      pods=$(kubectl get pods -n "$ns" --no-headers 2>/dev/null | wc -l | tr -d ' ')
      log "Pods in ${ns}: ${pods}"
      kubectl get pods -n "$ns" --no-headers 2>/dev/null | sed 's/^/     /' || true
    fi
  done
}

# ─── Install / Update ────────────────────────────────────────────────
install_flux_operator() {
  log "Installing/updating Flux Operator..."

  if $DRY_RUN; then
    helm template flux-operator oci://ghcr.io/controlplaneio-fluxcd/charts/flux-operator \
      --namespace "${FLUX_NAMESPACE}" > /dev/null 2>&1
    ok "Flux Operator Helm template: valid (dry-run)"
    return
  fi

  # Create namespace if needed
  kubectl create namespace "${FLUX_NAMESPACE}" --dry-run=client -o yaml | kubectl apply -f -

  helm upgrade --install flux-operator \
    oci://ghcr.io/controlplaneio-fluxcd/charts/flux-operator \
    --namespace "${FLUX_NAMESPACE}" \
    --wait \
    --timeout 5m

  # Wait for operator to be ready
  kubectl rollout status deployment/flux-operator -n "${FLUX_NAMESPACE}" --timeout=120s
  ok "Flux Operator is ready"
}

apply_flux_instance() {
  local instance_file="${CLUSTERS_DIR}/flux-system/flux-instance.yaml"

  if [ ! -f "$instance_file" ]; then
    err "FluxInstance manifest not found at ${instance_file}"
    exit 1
  fi

  log "Applying FluxInstance..."

  if $DRY_RUN; then
    kubectl apply --dry-run=server -f "$instance_file" 2>&1
    return
  fi

  kubectl apply -f "$instance_file"

  log "Waiting for Flux controllers to be ready..."
  sleep 10

  # Wait for core controllers
  for ctrl in source-controller kustomize-controller helm-controller notification-controller; do
    if kubectl get deployment "$ctrl" -n "${FLUX_NAMESPACE}" &>/dev/null 2>&1; then
      kubectl rollout status deployment/"$ctrl" -n "${FLUX_NAMESPACE}" --timeout=120s
      ok "  ${ctrl}: ready"
    else
      warn "  ${ctrl}: not found yet (may still be deploying)"
    fi
  done
}

apply_infrastructure() {
  log "Applying infrastructure (namespaces, storage classes, helm repos)..."

  if $DRY_RUN; then
    kustomize build "${CLUSTERS_DIR}/infrastructure" | kubectl apply --dry-run=server -f - 2>&1
    return
  fi

  # Apply native resources first (namespaces, storage classes)
  kubectl apply -f "${CLUSTERS_DIR}/infrastructure/namespaces.yaml"
  kubectl apply -f "${CLUSTERS_DIR}/infrastructure/storage-classes.yaml"
  ok "Namespaces and StorageClasses applied"

  # Apply Flux resources (HelmRepositories) — needs Flux CRDs
  if kubectl get crd helmrepositories.source.toolkit.fluxcd.io &>/dev/null 2>&1; then
    kubectl apply -f "${CLUSTERS_DIR}/infrastructure/helm-repositories.yaml"
    ok "HelmRepositories applied"
  else
    warn "Flux CRDs not yet available — HelmRepositories will be applied by Flux once it syncs from Git"
  fi
}

apply_apps() {
  log "Applying app HelmReleases..."

  if ! kubectl get crd helmreleases.helm.toolkit.fluxcd.io &>/dev/null 2>&1; then
    warn "Flux HelmRelease CRD not available — apps will be applied by Flux once it syncs from Git"
    return
  fi

  if $DRY_RUN; then
    kustomize build "${CLUSTERS_DIR}/apps" | kubectl apply --dry-run=server -f - 2>&1
    return
  fi

  kustomize build "${CLUSTERS_DIR}/apps" | kubectl apply -f -
  ok "App HelmReleases applied"
}

apply_monitoring() {
  log "Applying monitoring HelmReleases..."

  if ! kubectl get crd helmreleases.helm.toolkit.fluxcd.io &>/dev/null 2>&1; then
    warn "Flux HelmRelease CRD not available — monitoring will be applied by Flux once it syncs from Git"
    return
  fi

  if $DRY_RUN; then
    kustomize build "${CLUSTERS_DIR}/monitoring" | kubectl apply --dry-run=server -f - 2>&1
    return
  fi

  kustomize build "${CLUSTERS_DIR}/monitoring" | kubectl apply -f -
  ok "Monitoring HelmReleases applied"
}

# ─── Teardown ─────────────────────────────────────────────────────────
teardown() {
  warn "This will remove Flux Operator and all managed resources."
  echo ""
  read -r -p "Are you sure? (type 'yes' to confirm): " confirm
  if [ "$confirm" != "yes" ]; then
    log "Aborted."
    exit 0
  fi

  log "Removing HelmReleases..."
  kubectl delete helmreleases --all -A --ignore-not-found 2>/dev/null || true

  log "Removing FluxInstance..."
  kubectl delete fluxinstances --all -n "${FLUX_NAMESPACE}" --ignore-not-found 2>/dev/null || true
  sleep 5

  log "Removing Flux Operator..."
  helm uninstall flux-operator -n "${FLUX_NAMESPACE}" --ignore-not-found 2>/dev/null || true

  log "Removing HelmRepositories..."
  kubectl delete helmrepositories --all -n "${FLUX_NAMESPACE}" --ignore-not-found 2>/dev/null || true

  log "Removing custom StorageClasses..."
  kubectl delete sc premium-ssd-v2 premium-ssd-retain --ignore-not-found 2>/dev/null || true

  log "Removing namespaces..."
  kubectl delete ns ethereum blockscout monitoring --ignore-not-found 2>/dev/null || true

  ok "Teardown complete. The ${FLUX_NAMESPACE} namespace was preserved."
}

# ─── Main ─────────────────────────────────────────────────────────────
main() {
  echo ""
  echo "🌭 Etherwurst Cluster Setup"
  echo "═══════════════════════════"
  echo ""

  preflight

  if $STATUS_ONLY; then
    echo ""
    status
    exit 0
  fi

  if $TEARDOWN; then
    teardown
    exit 0
  fi

  echo ""
  validate

  if $DRY_RUN; then
    echo ""
    log "Dry-run mode — testing Flux Operator install..."
    install_flux_operator
    echo ""
    ok "All validations passed. Run without --dry-run to apply."
    exit 0
  fi

  echo ""
  install_flux_operator
  echo ""
  apply_flux_instance
  echo ""
  apply_infrastructure
  echo ""
  apply_apps
  echo ""
  apply_monitoring

  echo ""
  echo "═══════════════════════════"
  ok "Setup complete!"
  echo ""
  log "Next steps:"
  log "  1. Check status:    ./setup.sh --status"
  log "  2. Flux Web UI:     kubectl -n flux-system port-forward svc/flux-operator 9080:9080"
  log "  3. Otterscan:       kubectl -n ethereum port-forward svc/otterscan 5100:80"
  log "  4. Blockscout:      kubectl -n blockscout port-forward svc/blockscout 4000:4000"
  log "  5. Grafana:         kubectl -n monitoring port-forward svc/prometheus-grafana 3000:80"
  echo ""
  log "Erigon will take several hours to sync. Monitor with:"
  log "  kubectl -n ethereum logs -f deployment/erigon --tail=100"
  echo ""
}

main "$@"
