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
  VARIABLES_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  REPO_ROOT="$(cd "${VARIABLES_DIR}/../.." && pwd)"
  if command -v git >/dev/null 2>&1; then
    IMAGE_TAG="$(git -C "${REPO_ROOT}" rev-parse --short HEAD 2>/dev/null || true)"
  fi
  # Fallback: read VERSION file when not in a git context (e.g. inside a container)
  if [[ -z "${IMAGE_TAG:-}" && -f "${REPO_ROOT}/VERSION" ]]; then
    IMAGE_TAG="v$(cat "${REPO_ROOT}/VERSION" | tr -d '[:space:]')"
  fi
  if [[ -z "${IMAGE_TAG:-}" ]]; then
    echo "ERROR: IMAGE_TAG is not set, git rev-parse failed, and no VERSION file found." >&2
    return 1 2>/dev/null || exit 1
  fi
fi

# Validate tag: must not be 'latest', and must be either a git short SHA
# (7-12 hex chars) or a semver tag prefixed with 'v' (e.g. v1.2.3).
_validate_image_tag() {
  local tag="$1"
  local name="$2"
  if [[ "${tag}" == "latest" || "${tag}" == "latest-release" ]]; then
    echo "ERROR: ${name} must be immutable; do not use '${tag}'." >&2
    return 1
  fi
  # Accept: short SHA (hex, 7-40 chars) OR vMAJOR.MINOR.PATCH[-prerelease][+build]
  if [[ "${tag}" =~ ^[0-9a-f]{7,40}$ ]] || [[ "${tag}" =~ ^v[0-9]+\.[0-9]+\.[0-9] ]]; then
    return 0
  fi
  echo "ERROR: ${name}='${tag}' is not a valid tag (expected git SHA or vX.Y.Z semver)." >&2
  return 1
}
_validate_image_tag "${IMAGE_TAG}" "IMAGE_TAG" || { return 1 2>/dev/null || exit 1; }
if [[ -n "${AGENTHOST_IMAGE_TAG:-}" ]]; then
  _validate_image_tag "${AGENTHOST_IMAGE_TAG}" "AGENTHOST_IMAGE_TAG" || { return 1 2>/dev/null || exit 1; }
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
echo "  AgentHost tag:   ${AGENTHOST_IMAGE_TAG}"
echo "  Key Vault:       ${KEYVAULT_NAME}"
echo "  AgentHost KV:    ${AGENTHOST_KEYVAULT_URI}"
echo "  Tenant ID:       ${TENANT_ID:-<not set>}"
echo "  Identity client: ${IDENTITY_CLIENT_ID:-<not set>}"
