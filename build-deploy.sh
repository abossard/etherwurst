#!/usr/bin/env bash
#
# Build & Deploy HazMeBeenScammed via GitOps
#
# 1. Builds docker images (local buildx or ACR Tasks fallback)
# 2. Pushes to ACR
# 3. Updates image tag in manifest
# 4. Commits + pushes to git (Flux reconciles)
#
# Prerequisites: run ./setup.sh first to ensure ACR, AKS, Flux are configured.
#
# Usage:
#   ./build-deploy.sh              # Full: build + push + git update
#   ./build-deploy.sh --build      # Build & push images only
#   ./build-deploy.sh --deploy     # Update manifest + git push only
#   ./build-deploy.sh --status     # Show deployment status
#   ./build-deploy.sh --acr-build  # Force ACR Tasks instead of local Docker
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="${SCRIPT_DIR}/src"
MANIFEST="${SCRIPT_DIR}/clusters/etherwurst/apps/hazmebeenscammed.yaml"

ACR_NAME="k8sdemoanbo"
ACR_LOGIN_SERVER="${ACR_NAME}.azurecr.io"
API_IMAGE="${ACR_LOGIN_SERVER}/hazmebeenscammed-api"
WEB_IMAGE="${ACR_LOGIN_SERVER}/hazmebeenscammed-web"
NAMESPACE="ethereum"
IMAGE_TAG="${IMAGE_TAG:-$(git -C "$SCRIPT_DIR" rev-parse --short HEAD 2>/dev/null || echo latest)}"

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
      sed -n '2,17p' "$0" | sed 's/^# \?//'
      exit 0
      ;;
    *) err "Unknown argument: $arg"; exit 1 ;;
  esac
done

# ─── Build: Local Docker buildx (fast, parallel) ─────────────────────
build_local() {
  log "Logging into ACR..."
  az acr login --name "$ACR_NAME" --output none
  log "Building API + Web in parallel (linux/amd64)..."
  local start_time=$SECONDS api_log web_log
  api_log=$(mktemp); web_log=$(mktemp)

  ( docker buildx build --platform linux/amd64 --provenance=false \
      --tag "${API_IMAGE}:${IMAGE_TAG}" --tag "${API_IMAGE}:latest" \
      --file "${SRC_DIR}/HazMeBeenScammed.Api/Dockerfile" --push "${SRC_DIR}" \
      > "$api_log" 2>&1 && echo "OK" >> "$api_log" ) &
  local api_pid=$!

  ( docker buildx build --platform linux/amd64 --provenance=false \
      --tag "${WEB_IMAGE}:${IMAGE_TAG}" --tag "${WEB_IMAGE}:latest" \
      --file "${SRC_DIR}/HazMeBeenScammed.Web/Dockerfile" --push "${SRC_DIR}" \
      > "$web_log" 2>&1 && echo "OK" >> "$web_log" ) &
  local web_pid=$!

  local failed=false
  wait "$api_pid" || { err "API build failed:"; cat "$api_log"; failed=true; }
  wait "$web_pid" || { err "Web build failed:"; cat "$web_log"; failed=true; }
  rm -f "$api_log" "$web_log"

  local elapsed=$(( SECONDS - start_time ))
  $failed && { err "Build failed after ${elapsed}s"; exit 1; }
  ok "Built & pushed in ${elapsed}s: ${API_IMAGE}:${IMAGE_TAG}, ${WEB_IMAGE}:${IMAGE_TAG}"
}

# ─── Build: ACR Tasks (cloud fallback) ───────────────────────────────
build_acr() {
  log "Building API via ACR Tasks..."
  az acr build --registry "$ACR_NAME" \
    --image "hazmebeenscammed-api:${IMAGE_TAG}" --image "hazmebeenscammed-api:latest" \
    --file "${SRC_DIR}/HazMeBeenScammed.Api/Dockerfile" "${SRC_DIR}"
  ok "API: ${API_IMAGE}:${IMAGE_TAG}"

  log "Building Web via ACR Tasks..."
  az acr build --registry "$ACR_NAME" \
    --image "hazmebeenscammed-web:${IMAGE_TAG}" --image "hazmebeenscammed-web:latest" \
    --file "${SRC_DIR}/HazMeBeenScammed.Web/Dockerfile" "${SRC_DIR}"
  ok "Web: ${WEB_IMAGE}:${IMAGE_TAG}"
}

# ─── Build dispatch ──────────────────────────────────────────────────
build() {
  if ! $USE_ACR_BUILD && command -v docker &>/dev/null && timeout 5 docker info &>/dev/null 2>&1; then
    build_local
  else
    $USE_ACR_BUILD || warn "Docker not available — using ACR Tasks"
    build_acr
  fi
}

# ─── Update manifest + git push ──────────────────────────────────────
deploy() {
  log "Updating manifest to ${IMAGE_TAG}..."
  sed -i.bak -E \
    "s|(hazmebeenscammed-api:)[^ ]+( #)|\1${IMAGE_TAG}\2|g; \
     s|(hazmebeenscammed-web:)[^ ]+( #)|\1${IMAGE_TAG}\2|g" "$MANIFEST"
  rm -f "${MANIFEST}.bak"

  if git -C "$SCRIPT_DIR" diff --quiet "$MANIFEST"; then
    ok "Manifest already at ${IMAGE_TAG}"
    return
  fi

  git -C "$SCRIPT_DIR" add "$MANIFEST"
  git -C "$SCRIPT_DIR" commit -m "deploy: update images to ${IMAGE_TAG}

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
  git -C "$SCRIPT_DIR" push
  ok "Pushed — Flux will reconcile"
}

# ─── Status ──────────────────────────────────────────────────────────
show_status() {
  log "Deployment status:"
  kubectl get pods -n "$NAMESPACE" -l app.kubernetes.io/part-of=hazmebeenscammed 2>/dev/null | sed 's/^/  /'
  echo ""
  log "Images in cluster:"
  for d in hazmebeenscammed-api hazmebeenscammed-web; do
    echo "  ${d}: $(kubectl get deploy "$d" -n "$NAMESPACE" -o jsonpath='{.spec.template.spec.containers[0].image}' 2>/dev/null || echo 'not found')"
  done
  echo ""
  log "Manifest:"
  grep 'hazmebeenscammed-' "$MANIFEST" | grep image | sed 's/^/  /'
  echo ""
  log "Flux:"
  kubectl get kustomizations -n flux-system 2>/dev/null | sed 's/^/  /'
}

# ─── Main ────────────────────────────────────────────────────────────
echo ""
echo "🔍 HazMeBeenScammed — Build & Deploy"
echo "═════════════════════════════════════"
log "Tag: ${IMAGE_TAG}"
echo ""

if $STATUS_ONLY; then show_status; exit 0; fi
if ! $DEPLOY_ONLY; then build; echo ""; fi
if ! $BUILD_ONLY; then deploy; fi
echo ""
ok "Done! 🎉"
