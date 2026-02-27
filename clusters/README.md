# Etherwurst Fleet Repository
#
# This directory follows the Flux GitOps structure.
# The Flux Operator syncs from clusters/etherwurst/ and reconciles
# infrastructure first, then apps, then monitoring.
#
# Structure:
#   clusters/etherwurst/
#   ├── flux-system/        # Flux Operator managed (FluxInstance)
#   ├── infrastructure/     # Namespaces, storage classes, RBAC
#   ├── apps/               # Erigon, Lighthouse, Otterscan, Blockscout, cryo
#   └── monitoring/         # Prometheus, Grafana, metrics exporters
