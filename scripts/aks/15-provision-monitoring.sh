#!/usr/bin/env bash
# 15-provision-monitoring.sh -- Provision Application Insights + AKS Managed Prometheus.
#
# Idempotent: checks whether agentweaver-insights already exists before creating.
# Sources 00-variables.sh for RESOURCE_GROUP, LOCATION, KV_NAME, CLUSTER_NAME.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${SCRIPT_DIR}/00-variables.sh"

echo ""
echo "=== Provision Monitoring ==="
echo "  Resource Group: ${RESOURCE_GROUP}"
echo "  Location:       ${LOCATION}"
echo "  Key Vault:      ${KEYVAULT_NAME}"
echo "  Cluster:        ${CLUSTER_NAME}"
echo ""

# ── 1. Log Analytics workspace ─────────────────────────────────────────────────
echo "Ensuring Log Analytics workspace 'agentweaver-logs'..."
if az monitor log-analytics workspace show \
    --resource-group "${RESOURCE_GROUP}" \
    --workspace-name agentweaver-logs \
    &>/dev/null; then
  echo "  [OK] Log Analytics workspace already exists."
else
  az monitor log-analytics workspace create \
    --resource-group "${RESOURCE_GROUP}" \
    --workspace-name agentweaver-logs \
    --location "${LOCATION}"
  echo "  [created] Log Analytics workspace."
fi

# ── 2. Application Insights (workspace-based, required for Agents view) ────────
echo "Ensuring Application Insights 'agentweaver-insights'..."
if az monitor app-insights component show \
    --app agentweaver-insights \
    --resource-group "${RESOURCE_GROUP}" \
    &>/dev/null; then
  echo "  [OK] Application Insights already exists."
else
  az monitor app-insights component create \
    --resource-group "${RESOURCE_GROUP}" \
    --app agentweaver-insights \
    --location "${LOCATION}" \
    --kind web \
    --workspace agentweaver-logs
  echo "  [created] Application Insights."
fi

# ── 3. Store connection string in Key Vault ────────────────────────────────────
echo "Storing AppInsights connection string in Key Vault..."
CONN_STR="$(az monitor app-insights component show \
    --app agentweaver-insights \
    --resource-group "${RESOURCE_GROUP}" \
    --query connectionString \
    --output tsv)"

az keyvault secret set \
    --vault-name "${KEYVAULT_NAME}" \
    --name appinsights-connection-string \
    --value "${CONN_STR}" \
    --output none
echo "  [stored] appinsights-connection-string in Key Vault."

# ── 4. Enable AKS Managed Prometheus ──────────────────────────────────────────
echo "Enabling AKS Managed Prometheus on cluster '${CLUSTER_NAME}'..."
az aks update \
    --resource-group "${RESOURCE_GROUP}" \
    --name "${CLUSTER_NAME}" \
    --enable-azure-monitor-metrics
echo "  [enabled] AKS Managed Prometheus."

echo ""
echo "=== Monitoring provisioning complete ==="
echo "  Application Insights connection string stored as 'appinsights-connection-string' in Key Vault."
echo "  AKS Managed Prometheus enabled on cluster '${CLUSTER_NAME}'."
echo ""
