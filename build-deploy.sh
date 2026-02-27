#!/usr/bin/env bash
#
# Build & Deploy HazMeBeenScammed via GitOps
#
# Default: local Docker buildx (parallel, linux/amd64) → push to ACR → update manifest → git push
# Fallback: --acr-build uses ACR Tasks (no local Docker needed)
#
# Usage:
#   ./build-deploy.sh                # Full: buildx + push tag to git
#   ./build-deploy.sh --acr-build    # Full: ACR Tasks instead of local Docker
#   ./build-deploy.sh --build        # Build & push images only
#   ./build-deploy.sh --deploy       # Update manifest tag + git push only
#   ./build-deploy.sh --status       # Show deployment status
#
# Private config:
#   .private/gateway.yaml  Gateway + HTTPRoute with hostname (gitignored)
#                          Applied once via: kubectl apply -f .private/gateway.yaml
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="${SCRIPT_DIR}/src"
MANIFEST="${SCRIPT_DIR}/clusters/etherwurst/apps/hazmebeenscammed.yaml"

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
USE_ACR_BUILD=false

for arg in "$@"; do
  case "$arg" in
    --build)      BUILD_ONLY=true ;;
    --deploy)     DEPLOY_ONLY=true ;;
    --status)     STATUS_ONLY=true ;;
    --acr-build)  USE_ACR_BUILD=true ;;
    --help|-h)
      echo "Usage: $0 [--build|--deploy|--status|--acr-build]"
      echo ""
      echo "  (default)    Local Docker buildx → push to ACR (fast, parallel)"
      echo "  --acr-build  Use ACR Tasks instead of local Docker (slower, no Docker needed)"
      echo "  --build      Build & push images only (skip git push)"
      echo "  --deploy     Update manifest tag + git push only (skip build)"
      echo "  --status     Show deployment status"
      echo ""
      echo "Environment variables:"
      echo "  IMAGE_TAG  Override image tag (default: git short SHA)"
      echo ""
      echo "First-time setup:"
      echo "  kubectl apply -f .private/gateway.yaml  # apply Gateway with hostname"
      exit 0
      ;;
    *) err "Unknown argument: $arg"; exit 1 ;;
  esac
done

# ─── Preflight ────────────────────────────────────────────────────────
preflight() {
  log "Preflight checks..."
  local missing=()
  for cmd in az kubectl git; do
    command -v "$cmd" &>/dev/null || missing+=("$cmd")
  done
  if [ ${#missing[@]} -ne 0 ]; then
    err "Missing tools: ${missing[*]}"
    exit 1
  fi

  if ! $USE_ACR_BUILD; then
    if ! command -v docker &>/dev/null; then
      warn "Docker not found — falling back to ACR Tasks"
      USE_ACR_BUILD=true
    elif ! timeout 5 docker info &>/dev/null 2>&1; then
      warn "Docker daemon not running — falling back to ACR Tasks"
      USE_ACR_BUILD=true
    fi
  fi

  ok "Tools available"
  if $USE_ACR_BUILD; then
    log "Build mode: ACR Tasks (cloud)"
  else
    log "Build mode: Docker buildx (local, linux/amd64)"
  fi
  log "Image tag: ${IMAGE_TAG}"
}

# ─── ACR Login (for local Docker push) ───────────────────────────────
acr_login() {
  log "Logging into ACR '${ACR_LOGIN_SERVER}'..."
  az acr login --name "$ACR_NAME" --output none
  ok "ACR login successful"
}

# ─── Ensure ACR + AKS pull access ────────────────────────────────────
ensure_acr() {
  log "Ensuring ACR '${ACR_NAME}' exists..."
  if az acr show -n "$ACR_NAME" &>/dev/null 2>&1; then
    ok "ACR '${ACR_NAME}' exists (${ACR_LOGIN_SERVER})"
  else
    log "Creating ACR '${ACR_NAME}'..."
    az acr create \
      --resource-group "$RESOURCE_GROUP" \
      --name "$ACR_NAME" \
      --sku Basic \
      --location "$LOCATION" \
      --output none
    ok "ACR '${ACR_NAME}' created"
  fi

  log "Ensuring AKS → ACR pull access..."
  az aks update \
    --resource-group "$RESOURCE_GROUP" \
    --name "$AKS_NAME" \
    --attach-acr "$ACR_NAME" \
    --output none 2>/dev/null || warn "ACR attach skipped (may already be attached)"
  ok "AKS → ACR pull access OK"
}

# ─── Build: Local Docker buildx (fast, parallel) ─────────────────────
build_local() {
  acr_login

  log "Building API + Web images in parallel (linux/amd64)..."
  local start_time=$SECONDS
  local api_log web_log
  api_log=$(mktemp)
  web_log=$(mktemp)

  # Build both images in parallel using buildx
  (
    docker buildx build \
      --platform linux/amd64 \
      --provenance=false \
      --tag "${API_IMAGE}:${IMAGE_TAG}" \
      --tag "${API_IMAGE}:latest" \
      --file "${SRC_DIR}/HazMeBeenScammed.Api/Dockerfile" \
      --push \
      "${SRC_DIR}" \
      > "$api_log" 2>&1 \
    && echo "API_OK" >> "$api_log"
  ) &
  local api_pid=$!

  (
    docker buildx build \
      --platform linux/amd64 \
      --provenance=false \
      --tag "${WEB_IMAGE}:${IMAGE_TAG}" \
      --tag "${WEB_IMAGE}:latest" \
      --file "${SRC_DIR}/HazMeBeenScammed.Web/Dockerfile" \
      --push \
      "${SRC_DIR}" \
      > "$web_log" 2>&1 \
    && echo "WEB_OK" >> "$web_log"
  ) &
  local web_pid=$!

  # Wait for both
  local failed=false
  if ! wait "$api_pid"; then
    err "API build failed:"
    cat "$api_log"
    failed=true
  fi
  if ! wait "$web_pid"; then
    err "Web build failed:"
    cat "$web_log"
    failed=true
  fi

  local elapsed=$(( SECONDS - start_time ))

  if $failed; then
    rm -f "$api_log" "$web_log"
    err "Build failed after ${elapsed}s"
    exit 1
  fi

  rm -f "$api_log" "$web_log"
  ok "Both images built & pushed in ${elapsed}s"
  ok "  ${API_IMAGE}:${IMAGE_TAG}"
  ok "  ${WEB_IMAGE}:${IMAGE_TAG}"
}

# ─── Build: ACR Tasks (cloud fallback) ───────────────────────────────
build_acr() {
  log "Building API image via ACR Tasks..."
  az acr build \
    --registry "$ACR_NAME" \
    --image "hazmebeenscammed-api:${IMAGE_TAG}" \
    --image "hazmebeenscammed-api:latest" \
    --file "${SRC_DIR}/HazMeBeenScammed.Api/Dockerfile" \
    "${SRC_DIR}"
  ok "API image: ${API_IMAGE}:${IMAGE_TAG}"

  log "Building Web image via ACR Tasks..."
  az acr build \
    --registry "$ACR_NAME" \
    --image "hazmebeenscammed-web:${IMAGE_TAG}" \
    --image "hazmebeenscammed-web:latest" \
    --file "${SRC_DIR}/HazMeBeenScammed.Web/Dockerfile" \
    "${SRC_DIR}"
  ok "Web image: ${WEB_IMAGE}:${IMAGE_TAG}"
}

# ─── Update manifest + git push (Flux deploys) ───────────────────────
update_and_push() {
  log "Updating image tags in manifest to ${IMAGE_TAG}..."

  # Replace image tags (matches the comment marker pattern)
  sed -i.bak -E \
    "s|(hazmebeenscammed-api:)[^ ]+( #)|\1${IMAGE_TAG}\2|g; \
     s|(hazmebeenscammed-web:)[^ ]+( #)|\1${IMAGE_TAG}\2|g" \
    "$MANIFEST"
  rm -f "${MANIFEST}.bak"

  if git -C "$SCRIPT_DIR" diff --quiet "$MANIFEST"; then
    ok "Manifest already at ${IMAGE_TAG} — nothing to push"
    return
  fi

  log "Committing and pushing..."
  git -C "$SCRIPT_DIR" add "$MANIFEST"
  git -C "$SCRIPT_DIR" commit -m "deploy: update images to ${IMAGE_TAG}

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
  git -C "$SCRIPT_DIR" push
  ok "Pushed to git — Flux will reconcile automatically"
}

# ─── Status ───────────────────────────────────────────────────────────
show_status() {
  log "HazMeBeenScammed deployment status:"
  echo ""
  kubectl get deployment,svc,pods -n "$NAMESPACE" -l app.kubernetes.io/part-of=hazmebeenscammed 2>/dev/null | sed 's/^/  /' || warn "Nothing deployed yet"
  echo ""

  log "Current image tags in cluster:"
  for deploy in hazmebeenscammed-api hazmebeenscammed-web; do
    local img
    img=$(kubectl get deploy "$deploy" -n "$NAMESPACE" -o jsonpath='{.spec.template.spec.containers[0].image}' 2>/dev/null || echo "not found")
    echo "  ${deploy}: ${img}"
  done
  echo ""

  log "Current tag in manifest:"
  grep 'hazmebeenscammed-' "$MANIFEST" | grep image | sed 's/^/  /'
  echo ""

  log "Gateway:"
  kubectl get gateway,httproute -n "$NAMESPACE" 2>/dev/null | sed 's/^/  /' || warn "No gateway configured"
  echo ""

  log "Flux status:"
  kubectl get kustomizations -n flux-system 2>/dev/null | sed 's/^/  /' || warn "Flux not installed"
}

# ─── Main ─────────────────────────────────────────────────────────────
main() {
  echo ""
  echo "🔍 HazMeBeenScammed — Build & Deploy (GitOps)"
  echo "═══════════════════════════════════════════════"
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
    if $USE_ACR_BUILD; then
      build_acr
    else
      build_local
    fi
  fi

  if ! $BUILD_ONLY; then
    echo ""
    update_and_push
  fi

  echo ""
  ok "Done! 🎉"
}

main "$@"
