#!/usr/bin/env bash
# 15-setup-identity.sh -- Create managed identity, Key Vault, and workload identity
#                         federation for the agentweaver-api pod.
#
# Run from the repo root after 10-create-cluster.sh:
#   bash scripts/aks/15-setup-identity.sh
#
# Prerequisites: az login, source scripts/aks/00-variables.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${SCRIPT_DIR}/00-variables.sh"

echo ""
echo "=== Step 1: Create user-assigned managed identity ==="
az identity create \
  --name agentweaver-api-identity \
  --resource-group "${RESOURCE_GROUP}" \
  --location "${LOCATION}"

IDENTITY_CLIENT_ID=$(az identity show \
  --name agentweaver-api-identity \
  --resource-group "${RESOURCE_GROUP}" \
  --query clientId -o tsv)

IDENTITY_OBJECT_ID=$(az identity show \
  --name agentweaver-api-identity \
  --resource-group "${RESOURCE_GROUP}" \
  --query principalId -o tsv)

echo "  Identity client ID:  ${IDENTITY_CLIENT_ID}"
echo "  Identity object ID:  ${IDENTITY_OBJECT_ID}"

echo ""
echo "=== Step 2: Create Key Vault ==="
az keyvault create \
  --name "${KEYVAULT_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --location "${LOCATION}" \
  --enable-rbac-authorization

KEYVAULT_ID=$(az keyvault show --name "${KEYVAULT_NAME}" --query id -o tsv)
echo "  Key Vault ID: ${KEYVAULT_ID}"

echo ""
echo "=== Step 3: Store GitHub Copilot token ==="
# Replace <placeholder> with the actual token before running, or pass via env:
#   export GITHUB_COPILOT_TOKEN=ghp_...
#   bash scripts/aks/15-setup-identity.sh
az keyvault secret set \
  --vault-name "${KEYVAULT_NAME}" \
  --name github-copilot-token \
  --value "${GITHUB_COPILOT_TOKEN:-<placeholder>}"

echo ""
echo "=== Step 4: Grant 'Key Vault Secrets User' role to managed identity ==="
az role assignment create \
  --role "Key Vault Secrets User" \
  --assignee-object-id "${IDENTITY_OBJECT_ID}" \
  --assignee-principal-type ServicePrincipal \
  --scope "${KEYVAULT_ID}"

echo ""
echo "=== Step 5: Enable OIDC issuer + workload identity on cluster ==="
az aks update \
  --name "${CLUSTER_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --enable-oidc-issuer \
  --enable-workload-identity

OIDC_ISSUER=$(az aks show \
  --name "${CLUSTER_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --query oidcIssuerProfile.issuerUrl -o tsv)

echo "  OIDC issuer: ${OIDC_ISSUER}"

echo ""
echo "=== Step 6: Create federated credential ==="
az identity federated-credential create \
  --name agentweaver-api-fedcred \
  --identity-name agentweaver-api-identity \
  --resource-group "${RESOURCE_GROUP}" \
  --issuer "${OIDC_ISSUER}" \
  --subject "system:serviceaccount:${NAMESPACE}:agentweaver-api" \
  --audience api://AzureADTokenExchange

echo ""
echo "=== Step 7: Install Secrets Store CSI driver add-on ==="
az aks enable-addons \
  --name "${CLUSTER_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --addons azure-keyvault-secrets-provider

echo ""
echo "=== Summary ==="
echo "  IDENTITY_CLIENT_ID=${IDENTITY_CLIENT_ID}"
echo "  KEYVAULT_NAME=${KEYVAULT_NAME}"
echo "  TENANT_ID=${TENANT_ID}"
echo ""
echo "Apply k8s manifests with these values substituted:"
echo "  IDENTITY_CLIENT_ID=${IDENTITY_CLIENT_ID} \\"
echo "  KEYVAULT_NAME=${KEYVAULT_NAME} \\"
echo "  TENANT_ID=${TENANT_ID} \\"
echo "  envsubst < k8s/serviceaccount-api.yaml | kubectl apply -f -"
echo "  envsubst < k8s/secret-provider-class.yaml | kubectl apply -f -"
echo "  envsubst < k8s/api-deployment.yaml | kubectl apply -f -"
