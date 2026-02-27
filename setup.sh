#!/usr/bin/env bash
#
# Etherwurst Cluster Setup & Verify
#
# Idempotent — safe to run many times. Ensures everything is in place:
#   - AKS credentials
#   - ACR exists + AKS pull access
#   - Flux Operator + Web UI
#   - FluxInstance (GitOps sync)
#   - Infrastructure (namespaces, storage, helm repos)
#   - Gateway (from .private/gateway.yaml if present)
#
# Usage:
#   ./setup.sh              # Full verify + setup
#   ./setup.sh --status     # Show current state only
#   ./setup.sh --teardown   # Remove everything
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CLUSTERS_DIR="${SCRIPT_DIR}/clusters/etherwurst"
FLUX_NAMESPACE="flux-system"

# ─── Configuration ────────────────────────────────────────────────────
RESOURCE_GROUP="anbo-aks-demo"
AKS_NAME="aks-demo-light"
ACR_NAME="k8sdemoanbo"
LOCATION="swedencentral"

# Colors
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; BLUE='\033[0;34m'; NC='\033[0m'
log()  { echo -e "${BLUE}[INFO]${NC} $*"; }
ok()   { echo -e "${GREEN}[OK]${NC}   $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()  { echo -e "${RED}[ERR]${NC}  $*"; }

STATUS_ONLY=false
TEARDOWN=false

for arg in "$@"; do
  case "$arg" in
    --status)    STATUS_ONLY=true ;;
    --teardown)  TEARDOWN=true ;;
    --help|-h)   sed -n '2,16p' "$0" | sed 's/^# \?//'; exit 0 ;;
    *) err "Unknown argument: $arg"; exit 1 ;;
  esac
done

# ─── Preflight ───────────────────────────────────────────────────────
preflight() {
  log "Checking tools..."
  local missing=()
  for cmd in kubectl helm az git; do
    command -v "$cmd" &>/dev/null || missing+=("$cmd")
  done
  if [ ${#missing[@]} -ne 0 ]; then
    err "Missing: ${missing[*]}"
    exit 1
  fi
  ok "Tools: kubectl, helm, az, git"

  if ! kubectl cluster-info &>/dev/null; then
    err "Cannot connect to cluster"
    exit 1
  fi
  ok "Cluster: $(kubectl config current-context)"
}

# ─── ACR ─────────────────────────────────────────────────────────────
ensure_acr() {
  log "Checking ACR '${ACR_NAME}'..."
  if az acr show -n "$ACR_NAME" &>/dev/null 2>&1; then
    ok "ACR '${ACR_NAME}' exists"
  else
    log "Creating ACR '${ACR_NAME}'..."
    az acr create --resource-group "$RESOURCE_GROUP" --name "$ACR_NAME" \
      --sku Basic --location "$LOCATION" --output none
    ok "ACR created"
  fi

  log "Ensuring AKS → ACR pull access..."
  az aks update --resource-group "$RESOURCE_GROUP" --name "$AKS_NAME" \
    --attach-acr "$ACR_NAME" --output none 2>/dev/null \
    || warn "ACR attach skipped (may already be attached)"
  ok "AKS → ACR pull access"
}

# ─── Flux Operator + Web UI ─────────────────────────────────────────
ensure_flux_operator() {
  log "Checking Flux Operator..."
  kubectl create namespace "${FLUX_NAMESPACE}" --dry-run=client -o yaml | kubectl apply -f - 2>/dev/null

  helm upgrade --install flux-operator \
    oci://ghcr.io/controlplaneio-fluxcd/charts/flux-operator \
    --namespace "${FLUX_NAMESPACE}" \
    --set webUI.enabled=true \
    --wait --timeout 5m 2>&1 | tail -3
  ok "Flux Operator + Web UI"
}

# ─── FluxInstance (GitOps sync) ──────────────────────────────────────
ensure_flux_instance() {
  local f="${CLUSTERS_DIR}/flux-system/flux-instance.yaml"
  if [ ! -f "$f" ]; then
    err "FluxInstance not found: $f"
    exit 1
  fi

  log "Applying FluxInstance..."
  kubectl apply -f "$f"

  log "Waiting for controllers..."
  sleep 5
  for ctrl in source-controller kustomize-controller helm-controller; do
    kubectl rollout status deployment/"$ctrl" -n "${FLUX_NAMESPACE}" --timeout=120s 2>/dev/null \
      && ok "  ${ctrl}" || warn "  ${ctrl}: not ready yet"
  done
}

# ─── Infrastructure ─────────────────────────────────────────────────
ensure_infrastructure() {
  log "Applying infrastructure..."
  kubectl apply -f "${CLUSTERS_DIR}/infrastructure/namespaces.yaml" 2>/dev/null
  kubectl apply -f "${CLUSTERS_DIR}/infrastructure/storage-classes.yaml" 2>/dev/null
  ok "Namespaces + StorageClasses"

  if kubectl get crd helmrepositories.source.toolkit.fluxcd.io &>/dev/null 2>&1; then
    kubectl apply -f "${CLUSTERS_DIR}/infrastructure/helm-repositories.yaml" 2>/dev/null
    ok "HelmRepositories"
  else
    warn "Flux CRDs not ready — HelmRepositories will sync via GitOps"
  fi
}

# ─── Gateway (.private/) ────────────────────────────────────────────
ensure_gateway() {
  local gw="${SCRIPT_DIR}/.private/gateway.yaml"
  if [ -f "$gw" ]; then
    log "Applying Gateway from .private/gateway.yaml..."
    kubectl apply -f "$gw"
    ok "Gateway + HTTPRoute applied"
  else
    warn "No .private/gateway.yaml — skipping Gateway"
    log "  Create it for HTTPS ingress with your hostname"
  fi
}

# ─── Validate manifests ─────────────────────────────────────────────
validate() {
  log "Validating kustomize builds..."
  for layer in infrastructure apps monitoring; do
    local d="${CLUSTERS_DIR}/${layer}"
    [ -d "$d" ] || continue
    if kustomize build "$d" > /dev/null 2>&1; then
      ok "  ${layer}"
    else
      err "  ${layer} failed:"
      kustomize build "$d" 2>&1 | head -5
    fi
  done
}

# ─── Status ──────────────────────────────────────────────────────────
status() {
  echo ""
  log "═══ Cluster Status ═══"
  echo ""

  # ACR
  if az acr show -n "$ACR_NAME" &>/dev/null 2>&1; then
    ok "ACR: ${ACR_NAME}.azurecr.io"
  else
    warn "ACR: not found"
  fi

  # Flux
  if kubectl get deployment flux-operator -n "${FLUX_NAMESPACE}" &>/dev/null; then
    ok "Flux Operator: running"
    kubectl get fluxinstances,kustomizations -n "${FLUX_NAMESPACE}" --no-headers 2>/dev/null | sed 's/^/     /'
  else
    warn "Flux Operator: not installed"
  fi
  echo ""

  # Namespaces
  for ns in ethereum blockscout monitoring; do
    if kubectl get ns "$ns" &>/dev/null 2>&1; then
      local pods=$(kubectl get pods -n "$ns" --no-headers 2>/dev/null | wc -l | tr -d ' ')
      ok "  ${ns}: ${pods} pods"
    else
      warn "  ${ns}: not found"
    fi
  done
  echo ""

  # HelmReleases
  if kubectl get helmreleases -A --no-headers 2>/dev/null | head -5 | grep -q .; then
    log "HelmReleases:"
    kubectl get helmreleases -A --no-headers 2>/dev/null | sed 's/^/     /'
  fi

  # Gateway
  echo ""
  log "Gateway:"
  kubectl get gateway,httproute -n ethereum --no-headers 2>/dev/null | sed 's/^/     /' || warn "  none"

  echo ""
  log "Port-forwards: ./portforward.sh start"
  log "Flux Web UI:   http://localhost:9080"
}

# ─── Teardown ────────────────────────────────────────────────────────
teardown() {
  warn "This will remove Flux and all managed resources."
  read -r -p "Type 'yes' to confirm: " confirm
  [ "$confirm" = "yes" ] || { log "Aborted."; exit 0; }

  kubectl delete helmreleases --all -A --ignore-not-found 2>/dev/null || true
  kubectl delete fluxinstances --all -n "${FLUX_NAMESPACE}" --ignore-not-found 2>/dev/null || true
  sleep 5
  helm uninstall flux-operator -n "${FLUX_NAMESPACE}" 2>/dev/null || true
  kubectl delete sc premium-ssd-v2 premium-ssd-retain --ignore-not-found 2>/dev/null || true
  kubectl delete ns ethereum blockscout monitoring --ignore-not-found 2>/dev/null || true
  ok "Teardown complete"
}

# ─── Main ────────────────────────────────────────────────────────────
echo ""
echo "🌭 Etherwurst Setup & Verify"
echo "════════════════════════════"
echo ""

preflight

if $STATUS_ONLY; then status; exit 0; fi
if $TEARDOWN; then teardown; exit 0; fi

echo ""
ensure_acr
echo ""
ensure_flux_operator
echo ""
ensure_flux_instance
echo ""
ensure_infrastructure
echo ""
ensure_gateway
echo ""
validate

echo ""
echo "════════════════════════════"
ok "All good! 🎉"
echo ""
log "Build & deploy:  ./build-deploy.sh"
log "Port-forwards:   ./portforward.sh start"
log "Flux Web UI:     http://localhost:9080"
echo ""
