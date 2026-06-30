#!/usr/bin/env bash
# c4-flip-postgres.sh -- Re-apply the current API deployment after maintenance.
#
# The live AKS manifest is already Postgres-backed. This helper keeps the old
# operator checkpoint name but no longer hardcodes a registry, image tag, host,
# tenant, or checkout path.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
source "${SCRIPT_DIR}/00-variables.sh"

missing=()
[[ -z "${IDENTITY_CLIENT_ID:-}" ]] && missing+=("IDENTITY_CLIENT_ID")
[[ -z "${KEYVAULT_NAME:-}" ]] && missing+=("KEYVAULT_NAME")
[[ -z "${TENANT_ID:-}" ]] && missing+=("TENANT_ID")
if [[ ${#missing[@]} -gt 0 ]]; then
  echo "ERROR: The following required variables are not set:" >&2
  for v in "${missing[@]}"; do echo "  ${v}" >&2; done
  exit 1
fi

if [[ -z "${HOST:-}" ]]; then
  DOMAIN=$(kubectl get defaultdomaincertificate cert \
    --namespace "${NAMESPACE}" \
    --output jsonpath='{.status.domain}' 2>/dev/null || true)
  if [[ -z "${DOMAIN}" ]]; then
    echo "ERROR: HOST is not set and DefaultDomainCertificate status.domain is unavailable." >&2
    exit 1
  fi
  export HOST="agentweaver.${DOMAIN#\*.}"
fi

export ACR_LOGIN_SERVER IMAGE_TAG IDENTITY_CLIENT_ID KEYVAULT_NAME TENANT_ID HOST

echo ""
echo "=== Re-apply Agentweaver API deployment ==="
echo "  Namespace: ${NAMESPACE}"
echo "  Image:     ${ACR_LOGIN_SERVER}/agentweaver-api:${IMAGE_TAG}"
echo "  Host:      ${HOST}"
echo ""

envsubst '${HOST} ${ACR_LOGIN_SERVER} ${IMAGE_TAG} ${IDENTITY_CLIENT_ID} ${KEYVAULT_NAME} ${TENANT_ID}' \
  < "${REPO_ROOT}/k8s/api-deployment.yaml" | kubectl apply -f -

kubectl rollout status deployment/agentweaver-api \
  --namespace "${NAMESPACE}" \
  --timeout=180s

echo ""
echo "[OK] api-deployment.yaml applied with the current Postgres-backed manifest."
