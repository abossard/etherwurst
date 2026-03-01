#!/usr/bin/env bash
set -euo pipefail

RG_NAME="${1:-rg-health-model}"

echo "═══ Deleting resource group '$RG_NAME' and all its resources..."
read -rp "Are you sure? [y/N] " confirm
[[ "$confirm" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 0; }

az group delete --name "$RG_NAME" --yes --no-wait
echo "  Deletion started (--no-wait). Monitor with:"
echo "    az group show -n $RG_NAME --query properties.provisioningState -o tsv"
echo "═══ Done."
