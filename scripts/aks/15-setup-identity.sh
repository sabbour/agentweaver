#!/usr/bin/env bash
# 15-setup-identity.sh -- Create managed identity, Key Vault, workload identity, and secrets.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${SCRIPT_DIR}/00-variables.sh"

missing=()
[[ -z "${MCP_API_KEY:-}" ]] && missing+=("MCP_API_KEY")
[[ -z "${MCP_AUTH_API_KEY:-}" ]] && missing+=("MCP_AUTH_API_KEY")
[[ -z "${MCP_AUTH_USER:-}" ]] && missing+=("MCP_AUTH_USER")
[[ -z "${GITHUB_CLIENT_ID:-}" ]] && missing+=("GITHUB_CLIENT_ID")
[[ -z "${GITHUB_CLIENT_SECRET:-}" ]] && missing+=("GITHUB_CLIENT_SECRET")
if [[ ${#missing[@]} -gt 0 ]]; then
  echo "ERROR: refusing to write placeholder Key Vault secrets. Set these variables:" >&2
  for v in "${missing[@]}"; do echo "  ${v}" >&2; done
  exit 1
fi

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
echo "=== Step 3: Store required secrets in Key Vault ==="
az keyvault secret set --vault-name "${KEYVAULT_NAME}" --name mcp-api-key --value "${MCP_API_KEY}" --output none
az keyvault secret set --vault-name "${KEYVAULT_NAME}" --name mcp-auth-api-key --value "${MCP_AUTH_API_KEY}" --output none
az keyvault secret set --vault-name "${KEYVAULT_NAME}" --name mcp-auth-user --value "${MCP_AUTH_USER}" --output none
az keyvault secret set --vault-name "${KEYVAULT_NAME}" --name github-client-id --value "${GITHUB_CLIENT_ID}" --output none
az keyvault secret set --vault-name "${KEYVAULT_NAME}" --name github-client-secret --value "${GITHUB_CLIENT_SECRET}" --output none

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
echo ""
echo "=== Summary ==="
echo "  IDENTITY_CLIENT_ID=${IDENTITY_CLIENT_ID}"
echo "  KEYVAULT_NAME=${KEYVAULT_NAME}"
echo "  TENANT_ID=${TENANT_ID}"
echo ""
echo "Apply k8s manifests with these values substituted:"
echo "  IDENTITY_CLIENT_ID=${IDENTITY_CLIENT_ID} KEYVAULT_NAME=${KEYVAULT_NAME} TENANT_ID=${TENANT_ID} bash scripts/aks/30-deploy.sh"
