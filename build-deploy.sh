#!/usr/bin/env bash
#
# Build & Deploy HazMeBeenScammed to AKS
#
# Creates an ACR (or uses existing), builds the .NET projects,
# pushes images, attaches ACR to AKS, and deploys to the cluster.
#
# Usage:
#   ./build-deploy.sh              # Full build + deploy
#   ./build-deploy.sh --build      # Build & push images only
#   ./build-deploy.sh --deploy     # Deploy to cluster only (images must exist)
#   ./build-deploy.sh --status     # Show deployment status
#
# Environment variables (NOT stored in repo):
#   IMAGE_TAG            Override image tag (default: git short SHA)
#
# Private config:
#   .private/gateway.yaml  Gateway + HTTPRoute with hostname (gitignored)
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="${SCRIPT_DIR}/src"

# ─── Configuration ────────────────────────────────────────────────────
RESOURCE_GROUP="anbo-aks-demo"
AKS_NAME="aks-demo-light"
ACR_NAME="k8sdemoanbo"
LOCATION="swedencentral"
NAMESPACE="ethereum"
IMAGE_TAG="${IMAGE_TAG:-$(git -C "$SCRIPT_DIR" rev-parse --short HEAD 2>/dev/null || echo latest)}"

# Derived
ACR_LOGIN_SERVER="${ACR_NAME}.azurecr.io"
API_IMAGE="${ACR_LOGIN_SERVER}/hazmebeenscammed-api"
WEB_IMAGE="${ACR_LOGIN_SERVER}/hazmebeenscammed-web"

# Colors
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; BLUE='\033[0;34m'; NC='\033[0m'
log()  { echo -e "${BLUE}[INFO]${NC} $*"; }
ok()   { echo -e "${GREEN}[OK]${NC}   $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()  { echo -e "${RED}[ERR]${NC}  $*"; }

BUILD_ONLY=false
DEPLOY_ONLY=false
STATUS_ONLY=false

for arg in "$@"; do
  case "$arg" in
    --build)   BUILD_ONLY=true ;;
    --deploy)  DEPLOY_ONLY=true ;;
    --status)  STATUS_ONLY=true ;;
    --help|-h)
      echo "Usage: $0 [--build|--deploy|--status]"
      echo ""
      echo "  --build   Build & push images only"
      echo "  --deploy  Deploy to cluster only"
      echo "  --status  Show deployment status"
      echo ""
      echo "Environment variables:"
      echo "  IMAGE_TAG  Override image tag (default: git short SHA)"
      echo ""
      echo "Private config:"
      echo "  .private/gateway.yaml  Gateway + HTTPRoute with hostname (gitignored)"
      exit 0
      ;;
    *) err "Unknown argument: $arg"; exit 1 ;;
  esac
done

# ─── Preflight ────────────────────────────────────────────────────────
preflight() {
  log "Preflight checks..."
  local missing=()
  for cmd in az kubectl; do
    command -v "$cmd" &>/dev/null || missing+=("$cmd")
  done
  if [ ${#missing[@]} -ne 0 ]; then
    err "Missing tools: ${missing[*]}"
    exit 1
  fi
  ok "Tools available (az, kubectl)"
  log "Image tag: ${IMAGE_TAG}"
}

# ─── ACR Setup ────────────────────────────────────────────────────────
ensure_acr() {
  log "Ensuring ACR '${ACR_NAME}' exists..."

  if az acr show -n "$ACR_NAME" &>/dev/null 2>&1; then
    ok "ACR '${ACR_NAME}' already exists (${ACR_LOGIN_SERVER})"
  else
    log "Creating ACR '${ACR_NAME}' in ${RESOURCE_GROUP}..."
    az acr create \
      --resource-group "$RESOURCE_GROUP" \
      --name "$ACR_NAME" \
      --sku Basic \
      --location "$LOCATION" \
      --output none
    ok "ACR '${ACR_NAME}' created"
  fi

  # Attach ACR to AKS (idempotent — grants AcrPull to kubelet identity)
  log "Attaching ACR to AKS cluster..."
  az aks update \
    --resource-group "$RESOURCE_GROUP" \
    --name "$AKS_NAME" \
    --attach-acr "$ACR_NAME" \
    --output none 2>/dev/null || warn "ACR attach returned non-zero (may already be attached)"
  ok "AKS → ACR pull access configured"
}

# ─── Build & Push ─────────────────────────────────────────────────────
# Uses ACR Tasks to build in the cloud — no local Docker required.
build_and_push() {
  log "Building API image via ACR Tasks..."
  az acr build \
    --registry "$ACR_NAME" \
    --image "hazmebeenscammed-api:${IMAGE_TAG}" \
    --image "hazmebeenscammed-api:latest" \
    --file "${SRC_DIR}/HazMeBeenScammed.Api/Dockerfile" \
    "${SRC_DIR}"
  ok "API image built & pushed: ${API_IMAGE}:${IMAGE_TAG}"

  log "Building Web image via ACR Tasks..."
  az acr build \
    --registry "$ACR_NAME" \
    --image "hazmebeenscammed-web:${IMAGE_TAG}" \
    --image "hazmebeenscammed-web:latest" \
    --file "${SRC_DIR}/HazMeBeenScammed.Web/Dockerfile" \
    "${SRC_DIR}"
  ok "Web image built & pushed: ${WEB_IMAGE}:${IMAGE_TAG}"
}

# ─── Deploy ───────────────────────────────────────────────────────────
deploy() {
  local manifest="${SCRIPT_DIR}/clusters/etherwurst/apps/hazmebeenscammed.yaml"

  if [ ! -f "$manifest" ]; then
    err "Deployment manifest not found: ${manifest}"
    exit 1
  fi

  log "Deploying HazMeBeenScammed to namespace '${NAMESPACE}'..."

  # Apply workloads (Deployments + Services only, no Gateway/HTTPRoute)
  sed "s|__IMAGE_TAG__|${IMAGE_TAG}|g" "$manifest" | kubectl apply -f -

  # Apply Gateway + HTTPRoute from .private/ if present (never committed to repo)
  local gw_manifest="${SCRIPT_DIR}/.private/gateway.yaml"
  if [ -f "$gw_manifest" ]; then
    log "Applying Gateway + HTTPRoute from .private/gateway.yaml..."
    kubectl apply -f "$gw_manifest"
    ok "Gateway + routes applied"
  else
    warn "No .private/gateway.yaml found — skipping Gateway/HTTPRoute"
    warn "Create .private/gateway.yaml with your hostname to enable ingress"
  fi

  log "Waiting for rollout..."
  kubectl rollout status deployment/hazmebeenscammed-api -n "$NAMESPACE" --timeout=120s
  kubectl rollout status deployment/hazmebeenscammed-web -n "$NAMESPACE" --timeout=120s
  ok "Deployment complete!"

  echo ""
  log "Services:"
  kubectl get svc -n "$NAMESPACE" -l app.kubernetes.io/part-of=hazmebeenscammed --no-headers 2>/dev/null | sed 's/^/     /'
  echo ""
  log "Access via port-forward:"
  log "  API:  kubectl -n ${NAMESPACE} port-forward svc/hazmebeenscammed-api 5249:80"
  log "  Web:  kubectl -n ${NAMESPACE} port-forward svc/hazmebeenscammed-web 5174:80"
}

# ─── Status ───────────────────────────────────────────────────────────
show_status() {
  log "HazMeBeenScammed deployment status:"
  echo ""
  kubectl get deployment,svc,pods -n "$NAMESPACE" -l app.kubernetes.io/part-of=hazmebeenscammed 2>/dev/null | sed 's/^/  /' || warn "Nothing deployed yet"
  echo ""

  log "Gateway:"
  kubectl get gateway,httproute -n "$NAMESPACE" 2>/dev/null | sed 's/^/  /' || warn "No gateway configured"
  echo ""

  log "ACR images:"
  az acr repository list --name "$ACR_NAME" -o tsv 2>/dev/null | grep hazme | sed 's/^/  /' || warn "No images found"
}

# ─── Main ─────────────────────────────────────────────────────────────
main() {
  echo ""
  echo "🔍 HazMeBeenScammed — Build & Deploy"
  echo "═════════════════════════════════════"
  echo ""

  preflight

  if $STATUS_ONLY; then
    show_status
    exit 0
  fi

  if ! $DEPLOY_ONLY; then
    echo ""
    ensure_acr
    echo ""
    build_and_push
  fi

  if ! $BUILD_ONLY; then
    echo ""
    deploy
  fi

  echo ""
  ok "Done! 🎉"
}

main "$@"
