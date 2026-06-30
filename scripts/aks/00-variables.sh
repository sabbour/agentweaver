#!/usr/bin/env bash
# 00-variables.sh -- Shared environment variables for all Agentweaver AKS scripts.

# -- Azure resource parameters ------------------------------------------------
RESOURCE_GROUP="${RESOURCE_GROUP:-agentweaver-rg}"
CLUSTER_NAME="${CLUSTER_NAME:-agentweaver-aks-2}"
ACR_NAME="${ACR_NAME:-agentweaverregistry}"
LOCATION="${LOCATION:-westus2}"

# -- Key Vault + workload identity parameters ---------------------------------
KEYVAULT_NAME="${KEYVAULT_NAME:-agentweaver-kv}"
AGENTHOST_KEYVAULT_URI="${AGENTHOST_KEYVAULT_URI:-https://${KEYVAULT_NAME}.vault.azure.net/}"
TENANT_ID="${TENANT_ID:-}"
IDENTITY_CLIENT_ID="${IDENTITY_CLIENT_ID:-}"

# -- Kubernetes parameters ----------------------------------------------------
NAMESPACE="${NAMESPACE:-agentweaver}"
KATA_POOL_NAME="${KATA_POOL_NAME:-katapool}"
APP_POOL_NAME="${APP_POOL_NAME:-apppool}"

# -- Image parameters ---------------------------------------------------------
if [[ -z "${IMAGE_TAG:-}" ]]; then
  if command -v git >/dev/null 2>&1; then
    VARIABLES_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    REPO_ROOT="$(cd "${VARIABLES_DIR}/../.." && pwd)"
    IMAGE_TAG="$(git -C "${REPO_ROOT}" rev-parse --short HEAD 2>/dev/null || true)"
  fi
  if [[ -z "${IMAGE_TAG:-}" ]]; then
    echo "ERROR: IMAGE_TAG is not set and git rev-parse failed." >&2
    return 1 2>/dev/null || exit 1
  fi
fi
ACR_LOGIN_SERVER="${ACR_NAME}.azurecr.io"

# AgentHost (pod-per-run) image tag. Defaults to the unified IMAGE_TAG so the
# agentweaver-agent-host image/template/warmpool track the same build unless
# explicitly overridden.
: "${AGENTHOST_IMAGE_TAG:=${IMAGE_TAG}}"

# -- Derived values (do not override) -----------------------------------------
export RESOURCE_GROUP
export CLUSTER_NAME
export ACR_NAME
export LOCATION
export NAMESPACE
export KATA_POOL_NAME
export APP_POOL_NAME
export IMAGE_TAG
export AGENTHOST_IMAGE_TAG
export ACR_LOGIN_SERVER
export KEYVAULT_NAME
export AGENTHOST_KEYVAULT_URI
export TENANT_ID
export IDENTITY_CLIENT_ID

# -- Display summary ----------------------------------------------------------
echo "=== Agentweaver AKS variables ==="
echo "  Resource Group:  ${RESOURCE_GROUP}"
echo "  Cluster:         ${CLUSTER_NAME}"
echo "  ACR:             ${ACR_LOGIN_SERVER}"
echo "  Location:        ${LOCATION}"
echo "  Namespace:       ${NAMESPACE}"
echo "  Kata pool:       ${KATA_POOL_NAME}"
echo "  App pool:        ${APP_POOL_NAME}"
echo "  Image tag:       ${IMAGE_TAG}"
echo "  Key Vault:       ${KEYVAULT_NAME}"
echo "  AgentHost KV:    ${AGENTHOST_KEYVAULT_URI}"
echo "  Tenant ID:       ${TENANT_ID:-<not set>}"
echo "  Identity client: ${IDENTITY_CLIENT_ID:-<not set>}"
