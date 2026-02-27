#!/usr/bin/env bash
set -euo pipefail

# Etherwurst Sync Status Monitor
# Usage: ./sync-status.sh [--watch]

show_status() {
  echo "╔══════════════════════════════════════════════════════════════╗"
  echo "║  Etherwurst Sync Status                                     ║"
  echo "╠══════════════════════════════════════════════════════════════╣"

  # Erigon sync
  echo "║                                                              ║"
  echo "║  📦 Erigon (Execution Layer)                                 ║"
  erigon_status=$(kubectl get pod erigon-0 -n ethereum -o jsonpath='{.status.phase}' 2>/dev/null || echo "NotFound")
  if [ "$erigon_status" = "Running" ]; then
    sync_line=$(kubectl logs erigon-0 -n ethereum --tail=20 2>/dev/null | grep -E "\[1/6 OtterSync\] Downloading|Sync finished|OtterSync.*done" | tail -1)
    if [ -n "$sync_line" ]; then
      progress=$(echo "$sync_line" | sed -n 's/.*progress="\([^"]*\)".*/\1/p')
      [ -z "$progress" ] && progress="unknown"
      printf "║     Status: ⏳ Syncing — %s\n" "$progress"
    else
      stage=$(kubectl logs erigon-0 -n ethereum --tail=5 2>/dev/null | sed -n 's/.*\(\[[0-9]*\/[0-9]* [A-Za-z]*\]\).*/\1/p' | tail -1)
      [ -z "$stage" ] && stage="starting"
      printf "║     Status: ⏳ %s\n" "$stage"
    fi
  else
    printf "║     Status: ❌ %s\n" "$erigon_status"
  fi

  # Lighthouse sync
  echo "║                                                              ║"
  echo "║  🔥 Lighthouse (Consensus Layer)                             ║"
  lh_status=$(kubectl get pod lighthouse-0 -n ethereum -o jsonpath='{.status.phase}' 2>/dev/null || echo "NotFound")
  lh_restarts=$(kubectl get pod lighthouse-0 -n ethereum -o jsonpath='{.status.containerStatuses[0].restartCount}' 2>/dev/null || echo "?")
  lh_reason=$(kubectl get pod lighthouse-0 -n ethereum -o jsonpath='{.status.containerStatuses[0].lastState.terminated.reason}' 2>/dev/null || echo "")
  if [ "$lh_status" = "Running" ]; then
    lh_line=$(kubectl logs lighthouse-0 -n ethereum --tail=10 2>/dev/null | grep -iE "slot|sync|peer|head" | tail -1)
    printf "║     Status: ✅ Running (restarts: %s)\n" "$lh_restarts"
    [ -n "$lh_line" ] && printf "║     Latest: %s\n" "$(echo "$lh_line" | cut -c1-60)"
  else
    printf "║     Status: ❌ %s (restarts: %s, last: %s)\n" "$lh_status" "$lh_restarts" "$lh_reason"
  fi

  # Blockscout
  echo "║                                                              ║"
  echo "║  🔍 Blockscout (Block Explorer)                              ║"
  bs_status=$(kubectl get pod blockscout-0 -n blockscout -o jsonpath='{.status.phase}' 2>/dev/null || echo "NotFound")
  bs_pg=$(kubectl get pod blockscout-postgresql-0 -n blockscout -o jsonpath='{.status.phase}' 2>/dev/null || echo "NotFound")
  printf "║     App: %s | PostgreSQL: %s\n" "$bs_status" "$bs_pg"

  # Otterscan
  echo "║                                                              ║"
  echo "║  🦦 Otterscan (Explorer UI)                                  ║"
  ot_ready=$(kubectl get pods -n ethereum -l app.kubernetes.io/name=otterscan -o jsonpath='{.items[0].status.containerStatuses[0].ready}' 2>/dev/null || echo "false")
  printf "║     Ready: %s\n" "$ot_ready"

  # Monitoring
  echo "║                                                              ║"
  echo "║  📊 Monitoring                                               ║"
  mon_total=$(kubectl get pods -n monitoring --no-headers 2>/dev/null | wc -l | tr -d ' ')
  mon_ready=$(kubectl get pods -n monitoring --no-headers 2>/dev/null | grep -c "Running" || echo "0")
  printf "║     Pods: %s/%s running\n" "$mon_ready" "$mon_total"

  # HelmReleases
  echo "║                                                              ║"
  echo "║  🔄 Flux HelmReleases                                       ║"
  kubectl get helmrelease -A --no-headers 2>/dev/null | while read -r ns name age ready status; do
    if [ "$ready" = "True" ]; then
      printf "║     ✅ %-30s Ready\n" "$name"
    else
      printf "║     ❌ %-30s %s\n" "$name" "$ready"
    fi
  done

  echo "║                                                              ║"
  echo "╚══════════════════════════════════════════════════════════════╝"
  echo ""
  echo "Commands:"
  echo "  kubectl logs -f erigon-0 -n ethereum          # Watch Erigon sync"
  echo "  kubectl logs -f lighthouse-0 -n ethereum      # Watch Lighthouse sync"
  echo "  kubectl logs -f blockscout-0 -n blockscout    # Watch Blockscout indexing"
  echo "  kubectl top pods -n ethereum                  # Resource usage"
  echo "  ./portforward.sh start                        # Open all UIs"
}

if [ "${1:-}" = "--watch" ]; then
  while true; do
    clear
    show_status
    echo ""
    echo "(Refreshing every 15s — Ctrl+C to stop)"
    sleep 15
  done
else
  show_status
fi
