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

WORKSPACE_RESOURCE_ID="$(az monitor log-analytics workspace show \
    --resource-group "${RESOURCE_GROUP}" \
    --workspace-name agentweaver-logs \
    --query id \
    --output tsv)"
APPINSIGHTS_WORKSPACE_ID="$(az monitor log-analytics workspace show \
    --resource-group "${RESOURCE_GROUP}" \
    --workspace-name agentweaver-logs \
    --query customerId \
    --output tsv)"
export APPINSIGHTS_WORKSPACE_ID

# ── 2. Grant workload identity read access to Log Analytics ───────────────────
echo "Granting Log Analytics Reader on 'agentweaver-logs' to workload identity..."
if [[ -z "${IDENTITY_CLIENT_ID:-}" ]]; then
  echo "  [WARN] IDENTITY_CLIENT_ID is not set; skipping workspace role assignment."
else
  IDENTITY_OBJECT_ID="$(az identity list \
      --resource-group "${RESOURCE_GROUP}" \
      --query "[?clientId=='${IDENTITY_CLIENT_ID}'].principalId | [0]" \
      --output tsv)"
  if [[ -z "${IDENTITY_OBJECT_ID}" ]]; then
    echo "  [WARN] No managed identity found for IDENTITY_CLIENT_ID='${IDENTITY_CLIENT_ID}'; skipping workspace role assignment."
  else
    EXISTING_ASSIGNMENTS="$(az role assignment list \
        --assignee-object-id "${IDENTITY_OBJECT_ID}" \
        --scope "${WORKSPACE_RESOURCE_ID}" \
        --role "Log Analytics Reader" \
        --query 'length(@)' \
        --output tsv)"
    if [[ "${EXISTING_ASSIGNMENTS}" == "0" ]]; then
      az role assignment create \
        --role "Log Analytics Reader" \
        --assignee-object-id "${IDENTITY_OBJECT_ID}" \
        --assignee-principal-type ServicePrincipal \
        --scope "${WORKSPACE_RESOURCE_ID}" \
        --output none
      echo "  [granted] Log Analytics Reader on workspace."
    else
      echo "  [OK] Log Analytics Reader already assigned."
    fi
  fi
fi

# ── 3. Application Insights (workspace-based, required for Agents view) ────────
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

# ── 4. Store connection string in Key Vault ────────────────────────────────────
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

# ── 5. Enable AKS Managed Prometheus ──────────────────────────────────────────
echo "Enabling AKS Managed Prometheus on cluster '${CLUSTER_NAME}'..."
az aks update \
    --resource-group "${RESOURCE_GROUP}" \
    --name "${CLUSTER_NAME}" \
    --enable-azure-monitor-metrics
echo "  [enabled] AKS Managed Prometheus."

echo ""
echo "=== Monitoring provisioning complete ==="
echo "  Application Insights connection string stored as 'appinsights-connection-string' in Key Vault."
echo "  Log Analytics workspace customerId: ${APPINSIGHTS_WORKSPACE_ID}"
echo "  AKS Managed Prometheus enabled on cluster '${CLUSTER_NAME}'."
echo ""
