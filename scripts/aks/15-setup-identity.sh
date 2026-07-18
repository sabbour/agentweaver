#!/usr/bin/env bash
# 15-setup-identity.sh -- Create managed identity, Key Vault, workload identity, and secrets.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${SCRIPT_DIR}/00-variables.sh"

if [[ -z "${TENANT_ID:-}" ]]; then
  TENANT_ID="$(az account show --query tenantId --output tsv)"
  export TENANT_ID
fi

if [[ -t 0 && -t 1 ]]; then
  if [[ -z "${GITHUB_CLIENT_ID:-}" ]]; then
    read -r -p "GitHub OAuth client ID: " GITHUB_CLIENT_ID || true
  fi
  if [[ -z "${GITHUB_CLIENT_SECRET:-}" ]]; then
    read -r -s -p "GitHub OAuth client secret: " GITHUB_CLIENT_SECRET || true
    echo
  fi
fi

missing=()
[[ -z "${GITHUB_CLIENT_ID:-}" ]] && missing+=("GITHUB_CLIENT_ID")
[[ -z "${GITHUB_CLIENT_SECRET:-}" ]] && missing+=("GITHUB_CLIENT_SECRET")
if [[ ${#missing[@]} -gt 0 ]]; then
  echo "ERROR: GitHub OAuth credentials are missing." >&2
  if [[ ! -t 0 || ! -t 1 ]]; then
    echo "       This non-interactive session cannot prompt. Set these variables:" >&2
  else
    echo "       Prompted values were empty. Set these variables:" >&2
  fi
  for v in "${missing[@]}"; do echo "  ${v}" >&2; done
  exit 1
fi
export GITHUB_CLIENT_ID GITHUB_CLIENT_SECRET

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
if ! az keyvault show --name "${KEYVAULT_NAME}" --resource-group "${RESOURCE_GROUP}" &>/dev/null; then
  az keyvault create \
    --name "${KEYVAULT_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --location "${LOCATION}" \
    --enable-rbac-authorization
else
  echo "  [OK] Key Vault '${KEYVAULT_NAME}' already exists."
fi

KEYVAULT_ID=$(az keyvault show --name "${KEYVAULT_NAME}" --query id -o tsv)
echo "  Key Vault ID: ${KEYVAULT_ID}"

echo ""
echo "=== Step 2b: Grant provisioning caller data-plane secret access ==="
# The vault is created with --enable-rbac-authorization, so control-plane
# Contributor/Owner does NOT grant setSecret. Grant the interactive caller running
# this script 'Key Vault Secrets Officer' at the vault scope so Step 3 can write
# secrets on a freshly-created vault regardless of ambient group membership.
# Idempotent. See issue #234.
CALLER_OID="$(az ad signed-in-user show --query id -o tsv 2>/dev/null || true)"
CALLER_PTYPE="User"
if [[ -z "${CALLER_OID}" ]]; then
  # Service-principal / managed-identity login: resolve from the account context.
  CALLER_APPID="$(az account show --query user.name -o tsv 2>/dev/null || true)"
  if [[ -n "${CALLER_APPID}" ]]; then
    CALLER_OID="$(az ad sp show --id "${CALLER_APPID}" --query id -o tsv 2>/dev/null || true)"
    CALLER_PTYPE="ServicePrincipal"
  fi
fi
if [[ -n "${CALLER_OID}" ]]; then
  echo "  Granting 'Key Vault Secrets Officer' to caller ${CALLER_OID} (${CALLER_PTYPE})..."
  az role assignment create \
    --role "Key Vault Secrets Officer" \
    --assignee-object-id "${CALLER_OID}" \
    --assignee-principal-type "${CALLER_PTYPE}" \
    --scope "${KEYVAULT_ID}" \
    2>&1 | grep -v "already exists" || true
else
  echo "  [WARN] Could not resolve caller object ID; relying on ambient Key Vault permissions."
fi

echo ""
echo "=== Step 3: Store required secrets in Key Vault ==="
# Bounded retry: tolerate Forbidden/ForbiddenByRbac while the Step 2b role
# assignment propagates (RBAC data-plane is eventually consistent, ~30-120s).
set_secret_with_retry() {
  local name="$1" value="$2" attempt=1 max=12
  while true; do
    if az keyvault secret set --vault-name "${KEYVAULT_NAME}" --name "${name}" --value "${value}" --output none 2>/tmp/kv-set-err; then
      return 0
    fi
    if grep -qiE "Forbidden|ForbiddenByRbac|not authorized" /tmp/kv-set-err && [[ ${attempt} -lt ${max} ]]; then
      echo "  [retry ${attempt}/${max}] RBAC role for '${name}' still propagating; waiting 15s..."
      sleep 15
      attempt=$((attempt + 1))
      continue
    fi
    cat /tmp/kv-set-err >&2
    return 1
  done
}
set_secret_with_retry github-client-id "${GITHUB_CLIENT_ID}"
set_secret_with_retry github-client-secret "${GITHUB_CLIENT_SECRET}"

echo ""
echo "=== Step 4: Grant Key Vault roles to managed identity ==="
# 'Key Vault Secrets User' — read access for CSI driver secret mounts.
az role assignment create \
  --role "Key Vault Secrets User" \
  --assignee-object-id "${IDENTITY_OBJECT_ID}" \
  --assignee-principal-type ServicePrincipal \
  --scope "${KEYVAULT_ID}" \
  2>&1 | grep -v "already exists" || true

# 'Key Vault Secrets Officer' — write access for the GitHub token store
# (SetAsync / DeleteAsync via Azure SDK + workload identity, spec 006).
az role assignment create \
  --role "Key Vault Secrets Officer" \
  --assignee-object-id "${IDENTITY_OBJECT_ID}" \
  --assignee-principal-type ServicePrincipal \
  --scope "${KEYVAULT_ID}" \
  2>&1 | grep -v "already exists" || true

echo ""
echo "=== Step 5: Enable OIDC issuer + workload identity on cluster ==="
OIDC_ENABLED=$(az aks show --name "${CLUSTER_NAME}" --resource-group "${RESOURCE_GROUP}" \
  --query 'oidcIssuerProfile.enabled' -o tsv 2>/dev/null || echo "false")
WI_ENABLED=$(az aks show --name "${CLUSTER_NAME}" --resource-group "${RESOURCE_GROUP}" \
  --query 'securityProfile.workloadIdentity.enabled' -o tsv 2>/dev/null || echo "false")
if [[ "${OIDC_ENABLED}" == "true" && "${WI_ENABLED}" == "true" ]]; then
  echo "  [SKIP] OIDC issuer and workload identity already enabled."
else
  az aks update \
    --name "${CLUSTER_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --enable-oidc-issuer \
    --enable-workload-identity
fi

OIDC_ISSUER=$(az aks show \
  --name "${CLUSTER_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --query oidcIssuerProfile.issuerUrl -o tsv)

echo "  OIDC issuer: ${OIDC_ISSUER}"

echo ""
echo "=== Step 6: Create federated credential ==="
if ! az identity federated-credential show \
    --name agentweaver-api-fedcred \
    --identity-name agentweaver-api-identity \
    --resource-group "${RESOURCE_GROUP}" &>/dev/null; then
  az identity federated-credential create \
    --name agentweaver-api-fedcred \
    --identity-name agentweaver-api-identity \
    --resource-group "${RESOURCE_GROUP}" \
    --issuer "${OIDC_ISSUER}" \
    --subject "system:serviceaccount:${NAMESPACE}:agentweaver-api" \
    --audience api://AzureADTokenExchange
else
  echo "  [OK] Federated credential already exists."
fi

echo ""
echo "=== Step 7: Create federated credential for agent-host ==="
if ! az identity federated-credential show \
    --name agentweaver-agenthost-fedcred \
    --identity-name agentweaver-api-identity \
    --resource-group "${RESOURCE_GROUP}" &>/dev/null; then
  az identity federated-credential create \
    --name agentweaver-agenthost-fedcred \
    --identity-name agentweaver-api-identity \
    --resource-group "${RESOURCE_GROUP}" \
    --issuer "${OIDC_ISSUER}" \
    --subject "system:serviceaccount:${NAMESPACE}:agentweaver-agent-host" \
    --audience api://AzureADTokenExchange
else
  echo "  [OK] Agent-host federated credential already exists."
fi

echo ""
echo ""
echo "=== Summary ==="
echo "  IDENTITY_CLIENT_ID=${IDENTITY_CLIENT_ID}"
echo "  KEYVAULT_NAME=${KEYVAULT_NAME}"
echo "  TENANT_ID=${TENANT_ID}"
echo ""
echo "Two federated credentials are now configured on agentweaver-api-identity:"
echo "  agentweaver-api-fedcred      → system:serviceaccount:${NAMESPACE}:agentweaver-api"
echo "  agentweaver-agenthost-fedcred → system:serviceaccount:${NAMESPACE}:agentweaver-agent-host"
echo ""
echo "NOTE: Run scripts/aks/16-provision-oauth-signing-key.sh before the first deploy"
echo "      to provision the mcp-oauth-signing-key secret in Key Vault."
echo ""
echo "Apply k8s manifests with these values substituted:"
echo "  IDENTITY_CLIENT_ID=${IDENTITY_CLIENT_ID} KEYVAULT_NAME=${KEYVAULT_NAME} TENANT_ID=${TENANT_ID} bash scripts/aks/30-deploy.sh"
