#!/usr/bin/env bash
# 30-deploy.sh -- Deploy Agentweaver to AKS.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
source "${SCRIPT_DIR}/00-variables.sh"

RENDERED_DIR="${SCRIPT_DIR}/.rendered"
trap 'rm -rf "${RENDERED_DIR}"' EXIT

apply_rendered() {
  local fname="$1"
  kubectl apply -f "${RENDERED_DIR}/${fname}"
  echo "  [applied] ${fname}"
}

echo ""
echo "=== Agentweaver AKS deployment ==="
echo "  kubectl context: $(kubectl config current-context)"
echo "  Namespace:       ${NAMESPACE}"
echo "  ACR:             ${ACR_LOGIN_SERVER}"
echo "  Image tag:       ${IMAGE_TAG}"
echo ""

if ! command -v envsubst &>/dev/null; then
  echo "ERROR: envsubst not found. Install via: apt install gettext  or  brew install gettext"
  exit 1
fi

missing=()
[[ -z "${IDENTITY_CLIENT_ID:-}" ]] && missing+=("IDENTITY_CLIENT_ID")
[[ -z "${KEYVAULT_NAME:-}" ]] && missing+=("KEYVAULT_NAME")
[[ -z "${TENANT_ID:-}" ]] && missing+=("TENANT_ID")
if [[ ${#missing[@]} -gt 0 ]]; then
  echo "ERROR: The following required variables are not set:"
  for v in "${missing[@]}"; do echo "  $v"; done
  exit 1
fi

echo "Applying namespace..."
kubectl apply -f "${REPO_ROOT}/k8s/namespace.yaml"

echo ""
echo "Checking DefaultDomainCertificate 'cert' in namespace '${NAMESPACE}'..."
if kubectl get defaultdomaincertificate cert --namespace "${NAMESPACE}" &>/dev/null; then
  echo "  [OK] DefaultDomainCertificate 'cert' already exists."
else
  cat <<EOF | kubectl apply -f -
apiVersion: approuting.kubernetes.azure.com/v1alpha1
kind: DefaultDomainCertificate
metadata:
  name: cert
  namespace: ${NAMESPACE}
spec: {}
EOF
fi

kubectl wait \
  --for=condition=Available \
  defaultdomaincertificate/cert \
  --namespace "${NAMESPACE}" \
  --timeout=300s

DOMAIN=$(kubectl get defaultdomaincertificate cert \
  --namespace "${NAMESPACE}" \
  --output jsonpath='{.status.domain}')
export HOST="agentweaver.${DOMAIN#\*.}"

echo "  Managed domain: ${DOMAIN}"
echo "  Ingress host:   ${HOST}"

rm -rf "${RENDERED_DIR}"
mkdir -p "${RENDERED_DIR}"

echo ""
echo "Rendering manifests..."
for yaml_file in "${REPO_ROOT}"/k8s/*.yaml; do
  fname="$(basename "${yaml_file}")"
  envsubst '${HOST} ${ACR_LOGIN_SERVER} ${IMAGE_TAG} ${IDENTITY_CLIENT_ID} ${KEYVAULT_NAME} ${TENANT_ID}' \
    < "${yaml_file}" > "${RENDERED_DIR}/${fname}"
  echo "  rendered: ${fname}"
done

echo ""
echo "Applying identity, secrets, RBAC, quotas, and PVCs..."
apply_rendered serviceaccount-api.yaml
kubectl wait \
  --for=jsonpath='{.metadata.annotations.azure\.workload\.identity/client-id}'="${IDENTITY_CLIENT_ID}" \
  serviceaccount/agentweaver-api \
  --namespace "${NAMESPACE}" \
  --timeout=60s
apply_rendered secret-provider-class.yaml
apply_rendered secretprovider-mcp.yaml
apply_rendered rbac-api.yaml
apply_rendered quota.yaml
apply_rendered pvc-data.yaml
apply_rendered pvc-workspace.yaml

echo ""
echo "Applying network policies and egress allowlists..."
apply_rendered networkpolicy-default-deny.yaml
apply_rendered networkpolicy-mcp.yaml
apply_rendered networkpolicy-sandbox.yaml
apply_rendered cilium-network-policy-sandbox.yaml
apply_rendered serviceentry-telemetry.yaml

echo ""
echo "Applying services, gateway, routes, and backup jobs..."
apply_rendered api-service.yaml
apply_rendered frontend-service.yaml
apply_rendered mcp-service.yaml
apply_rendered gateway.yaml
apply_rendered httproute-api.yaml
apply_rendered httproute-frontend.yaml
apply_rendered mcp-httproute.yaml
apply_rendered backup-cronjob.yaml

echo ""
echo "Applying sandbox template and warm pool (required for production)..."
apply_rendered sandbox-template.yaml
apply_rendered sandbox-warmpool.yaml

echo ""
echo "Waiting for gateway/agentweaver-gateway to become Programmed (up to 3 min)..."
kubectl wait \
  --for=condition=Programmed \
  gateway/agentweaver-gateway \
  --namespace "${NAMESPACE}" \
  --timeout=180s

GATEWAY_IP=$(kubectl get gateway agentweaver-gateway \
  --namespace "${NAMESPACE}" \
  --output jsonpath='{.status.addresses[0].value}')

echo ""
echo "Applying deployments after workload identity prerequisites are ready..."
apply_rendered api-deployment.yaml
apply_rendered frontend-deployment.yaml
apply_rendered mcp-deployment.yaml

echo ""
echo "Waiting for API deployment rollout..."
kubectl rollout status deployment/agentweaver-api --namespace "${NAMESPACE}" --timeout=180s

echo "Waiting for Frontend deployment rollout..."
kubectl rollout status deployment/agentweaver-frontend --namespace "${NAMESPACE}" --timeout=120s

echo "Waiting for MCP deployment rollout..."
kubectl rollout status deployment/agentweaver-mcp --namespace "${NAMESPACE}" --timeout=120s

echo ""
echo "==================================================="
echo " DEPLOYMENT COMPLETE"
echo "==================================================="
echo ""
echo "  Frontend URL: https://${HOST}/"
echo "  API URL:      https://${HOST}/api/"
echo "  MCP URL:      https://${HOST}/mcp/"
echo "  Gateway IP:   ${GATEWAY_IP}"
echo ""
echo "  Next step:"
echo "    bash scripts/aks/40-verify.sh"
echo ""
echo "  To check status:"
echo "    kubectl get gateway,httproute,pod,svc -n ${NAMESPACE}"
