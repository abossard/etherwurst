#!/usr/bin/env bash
set -euo pipefail

# Etherwurst Port-Forward Manager
# Usage: ./portforward.sh [start|stop|status]

PIDS_FILE="/tmp/etherwurst-portforwards.pids"

SERVICES=(
  "ethereum:otterscan:5100:80:Otterscan UI"
  "blockscout:blockscout:4000:4000:Blockscout UI"
  "monitoring:kube-prometheus-stack-grafana:3000:80:Grafana (admin/prom-operator)"
  "monitoring:prometheus-prometheus:9090:9090:Prometheus"
  "flux-system:flux-operator:9080:9080:Flux UI"
  "ethereum:erigon:8545:8545:Erigon JSON-RPC"
  "kube-system:hubble-ui:12000:80:Hubble UI (Cilium)"
  "goldilocks:goldilocks-dashboard:8080:80:Goldilocks VPA"
)

start_forwards() {
  stop_forwards 2>/dev/null || true
  echo "🚀 Starting port-forwards..."
  echo "" > "$PIDS_FILE"

  for entry in "${SERVICES[@]}"; do
    IFS=: read -r ns svc local_port remote_port label <<< "$entry"
    kubectl port-forward -n "$ns" "svc/$svc" "$local_port:$remote_port" &>/dev/null &
    pid=$!
    echo "$pid:$ns:$svc:$local_port:$label" >> "$PIDS_FILE"
  done

  sleep 3
  echo ""
  echo "╔══════════════════════════════════════════════════════════╗"
  echo "║  Etherwurst Services                                    ║"
  echo "╠══════════════════════════════════════════════════════════╣"
  for entry in "${SERVICES[@]}"; do
    IFS=: read -r ns svc local_port remote_port label <<< "$entry"
    code=$(curl -s -o /dev/null -w "%{http_code}" -m 2 "http://localhost:$local_port" 2>/dev/null || echo "000")
    if [ "$code" != "000" ]; then
      printf "║  ✅ %-20s http://localhost:%-5s     ║\n" "$label" "$local_port"
    else
      printf "║  ❌ %-20s http://localhost:%-5s     ║\n" "$label" "$local_port"
    fi
  done
  echo "╚══════════════════════════════════════════════════════════╝"
  echo ""
  echo "Run './portforward.sh stop' to terminate all forwards."
}

stop_forwards() {
  if [ ! -f "$PIDS_FILE" ]; then
    echo "No active port-forwards found."
    return
  fi
  echo "🛑 Stopping port-forwards..."
  while IFS= read -r line; do
    [ -z "$line" ] && continue
    pid=$(echo "$line" | cut -d: -f1)
    if kill -0 "$pid" 2>/dev/null; then
      kill "$pid" 2>/dev/null || true
    fi
  done < "$PIDS_FILE"
  rm -f "$PIDS_FILE"
  echo "All port-forwards stopped."
}

show_status() {
  if [ ! -f "$PIDS_FILE" ]; then
    echo "No active port-forwards. Run './portforward.sh start'"
    return
  fi
  echo ""
  echo "╔══════════════════════════════════════════════════════════╗"
  echo "║  Port-Forward Status                                    ║"
  echo "╠══════════════════════════════════════════════════════════╣"
  while IFS= read -r line; do
    [ -z "$line" ] && continue
    pid=$(echo "$line" | cut -d: -f1)
    port=$(echo "$line" | cut -d: -f4)
    label=$(echo "$line" | cut -d: -f5)
    if kill -0 "$pid" 2>/dev/null; then
      code=$(curl -s -o /dev/null -w "%{http_code}" -m 2 "http://localhost:$port" 2>/dev/null || echo "000")
      if [ "$code" != "000" ]; then
        printf "║  ✅ %-20s http://localhost:%-5s     ║\n" "$label" "$port"
      else
        printf "║  ⚠️  %-20s http://localhost:%-5s     ║\n" "$label" "$port"
      fi
    else
      printf "║  ❌ %-20s (process dead)              ║\n" "$label"
    fi
  done < "$PIDS_FILE"
  echo "╚══════════════════════════════════════════════════════════╝"
}

case "${1:-start}" in
  start) start_forwards ;;
  stop)  stop_forwards ;;
  status) show_status ;;
  *)
    echo "Usage: $0 [start|stop|status]"
    exit 1
    ;;
esac
