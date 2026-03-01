#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DEPLOYMENT_NAME="health-model-$(date +%Y%m%d%H%M%S)"
LOCATION="${AZURE_LOCATION:-swedencentral}"
RG_NAME="${1:-rg-health-model}"

echo "═══ Checking preview feature availability..."
HM_TYPES=$(az provider show -n Microsoft.Monitor --query "resourceTypes[?contains(resourceType, 'healthmodels')].resourceType" -o tsv 2>/dev/null)
if [ -z "$HM_TYPES" ]; then
  echo "  ⚠ accounts/healthmodels resource type not available in this subscription."
  echo "  The Health Models feature is a gated preview requiring Microsoft approval."
  echo "  Register the preview flag and wait for approval:"
  echo "    az feature register --namespace Microsoft.Monitor --name preview"
  echo "    az provider register -n Microsoft.Monitor"
  echo "  Check status:"
  echo "    az feature show --namespace Microsoft.Monitor --name preview --query properties.state -o tsv"
  exit 1
fi
echo "  ✓ Health models resource type available"

echo "═══ Validating Bicep..."
az bicep build --file "$SCRIPT_DIR/main.bicep" --stdout > /dev/null
echo "  ✓ Bicep valid"

echo "═══ Running what-if..."
az deployment sub what-if \
  --location "$LOCATION" \
  --template-file "$SCRIPT_DIR/main.bicep" \
  --parameters location="$LOCATION" resourceGroupName="$RG_NAME" \
  --no-prompt

echo ""
read -rp "Proceed with deployment? [y/N] " confirm
[[ "$confirm" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 0; }

echo "═══ Deploying health model..."
az deployment sub create \
  --name "$DEPLOYMENT_NAME" \
  --location "$LOCATION" \
  --template-file "$SCRIPT_DIR/main.bicep" \
  --parameters location="$LOCATION" resourceGroupName="$RG_NAME"

echo "═══ Verifying resources..."
MONITOR_NAME=$(az deployment sub show --name "$DEPLOYMENT_NAME" --query 'properties.outputs.monitorAccountName.value' -o tsv)
HM_NAME=$(az deployment sub show --name "$DEPLOYMENT_NAME" --query 'properties.outputs.healthModelName.value' -o tsv)

echo "  Monitor account: $MONITOR_NAME"
echo "  Health model:    $HM_NAME"

az resource show \
  --resource-group "$RG_NAME" \
  --resource-type "Microsoft.Monitor/accounts/healthmodels" \
  --name "$MONITOR_NAME/$HM_NAME" \
  --query '{name:name, location:location, identity:identity.type, provisioningState:properties.provisioningState}' \
  -o table 2>/dev/null && echo "  ✓ Health model exists" || echo "  ⚠ Could not verify (preview API)"

echo "═══ Done! 🎉"
