#!/usr/bin/env bash
set -euo pipefail

# One-time same-region AKS Azure Disk PVC migration helper for Erigon.
# This script is intentionally conservative:
# - default mode is plan (read-only)
# - execute mode performs cutover operations
# - observe mode prints health checks continuously

MODE="plan"
WATCH_SECONDS=20
ASSUME_YES=false

# Required environment variables in execute mode.
SRC_CONTEXT="${SRC_CONTEXT:-}"
TGT_CONTEXT="${TGT_CONTEXT:-}"
SRC_AKS_RG="${SRC_AKS_RG:-}"
SRC_AKS_NAME="${SRC_AKS_NAME:-}"
NS="${NS:-ethereum}"
STATEFULSET="${STATEFULSET:-erigon}"
PVC_NAME="${PVC_NAME:-}"
TARGET_NODE_RG="${TARGET_NODE_RG:-}"
DISK_ID="${DISK_ID:-}"
DISK_NAME="${DISK_NAME:-}"
TGT_AKS_RG="${TGT_AKS_RG:-}"
TGT_AKS_NAME="${TGT_AKS_NAME:-}"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; BLUE='\033[0;34m'; NC='\033[0m'
log()  { echo -e "${BLUE}[INFO]${NC} $*"; }
ok()   { echo -e "${GREEN}[OK]${NC}   $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()  { echo -e "${RED}[ERR]${NC}  $*"; }

usage() {
  cat <<'EOF'
Usage:
  ./migrate-erigon-pvc-once.sh --mode plan [flags]
  ./migrate-erigon-pvc-once.sh --mode execute [flags]
  ./migrate-erigon-pvc-once.sh --mode observe [flags]

Optional flags:
  --source-context <name>
  --target-context <name>
  --source-aks-rg <rg>
  --source-aks-name <name>
  --target-aks-rg <rg>
  --target-aks-name <name>
  --target-node-rg <rg>      If omitted, derived from target AKS RG/name
  --namespace <ns>           Default: ethereum
  --statefulset <name>       Default: erigon
  --pvc <name>
  --disk-id <resource-id>
  --disk-name <name>
  --watch-seconds <n>    Observe interval (default: 20)
  --yes                  Skip execute confirmation prompt

Environment variable fallback (same names):
  SRC_CONTEXT, TGT_CONTEXT
  SRC_AKS_RG, SRC_AKS_NAME
  TGT_AKS_RG, TGT_AKS_NAME
  NS (default: ethereum), STATEFULSET (default: erigon)
  PVC_NAME
  TARGET_NODE_RG
  DISK_ID (optional if derivable from PVC), DISK_NAME (optional)

Notes:
  - Same-region migration only.
  - Script assumes Azure Disk CSI PVC.
  - Default mode is read-only plan.
EOF
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --mode)
        MODE="$2"; shift 2 ;;
      --source-context)
        SRC_CONTEXT="$2"; shift 2 ;;
      --target-context)
        TGT_CONTEXT="$2"; shift 2 ;;
      --source-aks-rg)
        SRC_AKS_RG="$2"; shift 2 ;;
      --source-aks-name)
        SRC_AKS_NAME="$2"; shift 2 ;;
      --target-aks-rg)
        TGT_AKS_RG="$2"; shift 2 ;;
      --target-aks-name)
        TGT_AKS_NAME="$2"; shift 2 ;;
      --target-node-rg)
        TARGET_NODE_RG="$2"; shift 2 ;;
      --namespace)
        NS="$2"; shift 2 ;;
      --statefulset)
        STATEFULSET="$2"; shift 2 ;;
      --pvc)
        PVC_NAME="$2"; shift 2 ;;
      --disk-id)
        DISK_ID="$2"; shift 2 ;;
      --disk-name)
        DISK_NAME="$2"; shift 2 ;;
      --watch-seconds)
        WATCH_SECONDS="$2"; shift 2 ;;
      --yes)
        ASSUME_YES=true; shift ;;
      --help|-h)
        usage; exit 0 ;;
      *)
        err "Unknown argument: $1"
        usage
        exit 1 ;;
    esac
  done
}

need_cmd() {
  command -v "$1" >/dev/null 2>&1 || { err "Missing command: $1"; exit 1; }
}

preflight() {
  need_cmd kubectl
  need_cmd az
  if command -v flux >/dev/null 2>&1; then
    ok "flux CLI detected"
  else
    warn "flux CLI not found; script will still work if you suspend/resume via kubectl path"
  fi
}

require_execute_vars() {
  local missing=()
  for v in SRC_CONTEXT TGT_CONTEXT SRC_AKS_RG SRC_AKS_NAME PVC_NAME; do
    [[ -n "${!v:-}" ]] || missing+=("$v")
  done
  if [[ -z "$TARGET_NODE_RG" && ( -z "$TGT_AKS_RG" || -z "$TGT_AKS_NAME" ) ]]; then
    missing+=("TARGET_NODE_RG (or TGT_AKS_RG + TGT_AKS_NAME)")
  fi
  if [[ ${#missing[@]} -ne 0 ]]; then
    err "Missing required inputs: ${missing[*]}"
    exit 1
  fi
}

derive_target_node_rg_if_needed() {
  if [[ -n "$TARGET_NODE_RG" ]]; then
    return
  fi
  log "Deriving target node resource group from target AKS"
  TARGET_NODE_RG="$(az aks show -g "$TGT_AKS_RG" -n "$TGT_AKS_NAME" --query nodeResourceGroup -o tsv)"
  [[ -n "$TARGET_NODE_RG" ]] || { err "Unable to derive target node resource group"; exit 1; }
  ok "TARGET_NODE_RG=$TARGET_NODE_RG"
}

discover_disk_from_pvc() {
  log "Discovering disk from PVC ${NS}/${PVC_NAME} in source context"
  kubectl config use-context "$SRC_CONTEXT" >/dev/null
  local pv
  pv="$(kubectl -n "$NS" get pvc "$PVC_NAME" -o jsonpath='{.spec.volumeName}')"
  if [[ -z "$pv" ]]; then
    err "PVC ${NS}/${PVC_NAME} has no bound PV"
    exit 1
  fi
  if [[ -z "$DISK_ID" ]]; then
    DISK_ID="$(kubectl get pv "$pv" -o jsonpath='{.spec.csi.volumeHandle}')"
  fi
  if [[ -z "$DISK_NAME" ]]; then
    DISK_NAME="${DISK_ID##*/}"
  fi
  ok "PV=$pv"
  ok "DISK_ID=$DISK_ID"
}

print_plan() {
  log "Plan summary"
  echo "- Source context: ${SRC_CONTEXT:-<unset>}"
  echo "- Target context: ${TGT_CONTEXT:-<unset>}"
  echo "- Namespace/StatefulSet: ${NS}/${STATEFULSET}"
  echo "- PVC: ${PVC_NAME:-<unset>}"
  echo "- Target node RG: ${TARGET_NODE_RG:-<unset>}"
  echo "- Disk ID: ${DISK_ID:-<unset>}"
  echo "- Disk Name: ${DISK_NAME:-<unset>}"
  echo
  echo "Cutover sequence:"
  echo "1) Snapshot for rollback"
  echo "2) Suspend Flux and scale StatefulSet to 0"
  echo "3) Wait for disk to become Unattached"
  echo "4) Move disk to target node resource group"
  echo "5) Create static PV/PVC in target"
  echo "6) Start workload and validate"
}

confirm_execute() {
  warn "Execute mode will stop source Erigon and move disk resources."
  $ASSUME_YES && { warn "--yes set, skipping confirmation"; return; }
  read -r -p "Type 'move-now' to continue: " answer
  [[ "$answer" == "move-now" ]] || { log "Aborted."; exit 0; }
}

snapshot_for_rollback() {
  local disk_rg
  disk_rg="$(echo "$DISK_ID" | awk -F/ '{print $5}')"
  local snap="${DISK_NAME}-pre-migration-$(date +%Y%m%d%H%M)"
  log "Creating rollback snapshot: $snap"
  az snapshot create --name "$snap" --resource-group "$disk_rg" --source "$DISK_ID" >/dev/null
  ok "Snapshot created: $snap"
}

suspend_and_detach() {
  log "Suspending source workload"
  kubectl config use-context "$SRC_CONTEXT" >/dev/null
  if command -v flux >/dev/null 2>&1; then
    flux suspend helmrelease erigon -n "$NS" || true
  fi
  kubectl -n "$NS" scale statefulset "$STATEFULSET" --replicas=0

  log "Waiting for disk to become Unattached"
  local state=""
  for _ in $(seq 1 120); do
    state="$(az disk show --ids "$DISK_ID" --query diskState -o tsv)"
    [[ "$state" == "Unattached" ]] && break
    sleep 10
  done
  [[ "$state" == "Unattached" ]] || { err "Disk state is '$state', expected Unattached"; exit 1; }
  ok "Disk is Unattached"
}

move_disk() {
  log "Moving disk to target node RG: $TARGET_NODE_RG"
  az resource move --destination-group "$TARGET_NODE_RG" --ids "$DISK_ID" >/dev/null
  ok "Disk move command completed"
}

emit_target_manifests() {
  local out_dir="${PWD}/.private/migration"
  mkdir -p "$out_dir"

  cat > "$out_dir/pv-erigon-migrated.yaml" <<EOF
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
    volumeHandle: ${DISK_ID}
    fsType: ext4
EOF

  cat > "$out_dir/pvc-erigon-migrated.yaml" <<EOF
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: erigon-data-migrated
  namespace: ${NS}
spec:
  accessModes:
    - ReadWriteOnce
  resources:
    requests:
      storage: 2Ti
  volumeName: pv-erigon-migrated
  storageClassName: managed-csi-premium
EOF

  ok "Wrote manifests in $out_dir"
  echo "Apply on target after workload claim mapping is updated:"
  echo "  kubectl config use-context $TGT_CONTEXT"
  echo "  kubectl apply -f $out_dir/pv-erigon-migrated.yaml"
  echo "  kubectl apply -f $out_dir/pvc-erigon-migrated.yaml"
}

observe_once() {
  kubectl config use-context "$TGT_CONTEXT" >/dev/null 2>&1 || true
  echo "== Kubernetes =="
  kubectl -n "$NS" get pods -o wide 2>/dev/null || true
  kubectl -n "$NS" get pvc 2>/dev/null || true
  kubectl -n "$NS" get events --sort-by=.lastTimestamp 2>/dev/null | tail -n 20 || true
  echo
  echo "== Azure Disk =="
  [[ -n "$DISK_ID" ]] && az disk show --ids "$DISK_ID" --query '{state:diskState,managedBy:managedBy,zone:zones,sizeGB:diskSizeGb}' -o table || true
}

observe_loop() {
  log "Starting observe loop (Ctrl+C to stop)"
  while true; do
    clear
    date
    observe_once
    sleep "$WATCH_SECONDS"
  done
}

execute_cutover() {
  require_execute_vars
  derive_target_node_rg_if_needed
  discover_disk_from_pvc
  print_plan
  confirm_execute
  snapshot_for_rollback
  suspend_and_detach
  move_disk
  emit_target_manifests
  ok "Cutover script finished. Next: apply PV/PVC on target and point StatefulSet claim to migrated PVC."
}

main() {
  parse_args "$@"
  preflight
  case "$MODE" in
    plan)
      [[ -n "$SRC_CONTEXT" && -n "$PVC_NAME" ]] && discover_disk_from_pvc || true
      print_plan
      ;;
    execute)
      execute_cutover
      ;;
    observe)
      if [[ -n "$SRC_CONTEXT" && -n "$PVC_NAME" && -z "$DISK_ID" ]]; then
        discover_disk_from_pvc
      fi
      observe_loop
      ;;
    *)
      err "Invalid mode: $MODE"
      usage
      exit 1
      ;;
  esac
}

main "$@"
