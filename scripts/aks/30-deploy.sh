#!/usr/bin/env bash
# 30-deploy.sh -- Deploy Agentweaver to AKS.
#
# Applies the k8s/ manifests after substituting placeholder tokens:
#   ${HOST}              --> <managed-domain host>  (derived from DefaultDomainCertificate)
#   ${ACR_LOGIN_SERVER}  --> <ACR_NAME>.azurecr.io
#   ${IMAGE_TAG}         --> image tag
#   ${IDENTITY_CLIENT_ID} --> workload identity client ID (from 15-setup-identity.sh)
#   ${KEYVAULT_NAME}     --> Key Vault name
#   ${TENANT_ID}         --> Azure AD tenant ID
#
# envsubst replaces ONLY those tokens; all other $ references in manifests
# (e.g. Kubernetes env var refs) are left untouched.
#
# Sandbox manifests (sandbox-claim-template.yaml, sandbox-template.yaml,
# sandbox-warmpool.yaml) are skipped by default because they require the
# sandbox CRD to be pre-installed and contain literal {runId} placeholders
# that envsubst cannot process. Pass --with-sandbox to include them.
#
# MCP secrets require: MCP_API_KEY, MCP_AUTH_API_KEY, MCP_AUTH_USER
# Export these before running:
#   export MCP_API_KEY=...
#   export MCP_AUTH_API_KEY=...
#   export MCP_AUTH_USER=...
#
# Requires: kubectl (pointed at the AKS cluster), envsubst (apt install gettext /
#           brew install gettext), Azure CLI.
# Run from the REPO ROOT.
#
# Usage:
#   source scripts/aks/00-variables.sh
#   bash scripts/aks/30-deploy.sh [--with-sandbox]

set -euo pipefail

# -- Parse flags ---------------------------------------------------------------
WITH_SANDBOX=0
for arg in "$@"; do
  [[ "${arg}" == "--with-sandbox" ]] && WITH_SANDBOX=1
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
# shellcheck source=00-variables.sh
source "${SCRIPT_DIR}/00-variables.sh"

# Rendered manifests land here; cleaned up on exit
RENDERED_DIR="${SCRIPT_DIR}/.rendered"
trap 'rm -rf "${RENDERED_DIR}"' EXIT

echo ""
echo "=== Agentweaver AKS deployment ==="
echo "  kubectl context: $(kubectl config current-context)"
echo "  Namespace:       ${NAMESPACE}"
echo "  ACR:             ${ACR_LOGIN_SERVER}"
echo "  Image tag:       ${IMAGE_TAG}"
echo ""

# -- Pre-flight ----------------------------------------------------------------
if ! command -v envsubst &>/dev/null; then
  echo "ERROR: envsubst not found."
  echo "Install via: apt install gettext  or  brew install gettext"
  exit 1
fi

# Validate variables required for manifest substitution and secrets
missing=()
[[ -z "${IDENTITY_CLIENT_ID:-}" ]] && missing+=("IDENTITY_CLIENT_ID")
[[ -z "${KEYVAULT_NAME:-}" ]]      && missing+=("KEYVAULT_NAME")
[[ -z "${TENANT_ID:-}" ]]          && missing+=("TENANT_ID")
[[ -z "${MCP_API_KEY:-}" ]]        && missing+=("MCP_API_KEY")
[[ -z "${MCP_AUTH_API_KEY:-}" ]]   && missing+=("MCP_AUTH_API_KEY")
[[ -z "${MCP_AUTH_USER:-}" ]]      && missing+=("MCP_AUTH_USER")
if [[ ${#missing[@]} -gt 0 ]]; then
  echo "ERROR: The following required variables are not set:"
  for v in "${missing[@]}"; do echo "  $v"; done
  exit 1
fi

# -- Step 1: Namespace --------------------------------------------------------
echo "Applying namespace..."
kubectl apply -f "${REPO_ROOT}/k8s/namespace.yaml"

# -- Step 2: DefaultDomainCertificate -----------------------------------------
# Apply the DefaultDomainCertificate resource so the managed TLS cert is issued.
# This resource is cluster-scoped and not in the k8s/ dir — it must exist for
# the gateway to get a valid cert. Wait for it to become Available.
echo ""
echo "Checking DefaultDomainCertificate 'cert' in namespace '${NAMESPACE}'..."
if kubectl get defaultdomaincertificate cert --namespace "${NAMESPACE}" &>/dev/null; then
  echo "  [OK] DefaultDomainCertificate 'cert' already exists."
else
  echo "  [INFO] DefaultDomainCertificate not found. Creating..."
  cat <<EOF | kubectl apply -f -
apiVersion: approuting.kubernetes.azure.com/v1alpha1
kind: DefaultDomainCertificate
metadata:
  name: cert
  namespace: ${NAMESPACE}
spec: {}
EOF
fi

echo "Waiting for DefaultDomainCertificate 'cert' to become Available (up to 5 min)..."
kubectl wait \
  --for=condition=Available \
  defaultdomaincertificate/cert \
  --namespace "${NAMESPACE}" \
  --timeout=300s

# -- Step 3: Derive managed domain host ---------------------------------------
DOMAIN=$(kubectl get defaultdomaincertificate cert \
  --namespace "${NAMESPACE}" \
  --output jsonpath='{.status.domain}')

# Strip leading '*.' wildcard: *.foo.azureaksapps.io --> foo.azureaksapps.io
# then prepend 'agentweaver.' for the per-service FQDN.
export HOST="agentweaver.${DOMAIN#\*.}"

echo "  Managed domain: ${DOMAIN}"
echo "  Ingress host:   ${HOST}"

# -- Step 4: API service account (for workload identity in Wave 2) ------------
echo ""
echo "Applying service account..."
kubectl apply -f - <<EOF
apiVersion: v1
kind: ServiceAccount
metadata:
  name: agentweaver-api
  namespace: ${NAMESPACE}
  labels:
    app.kubernetes.io/part-of: agentweaver
EOF

# -- Step 5: Render manifests via envsubst ------------------------------------
rm -rf "${RENDERED_DIR}"
mkdir -p "${RENDERED_DIR}"

echo ""
echo "Rendering manifests (substituting HOST, ACR_LOGIN_SERVER, IMAGE_TAG, IDENTITY_CLIENT_ID, KEYVAULT_NAME, TENANT_ID)..."

for yaml_file in "${REPO_ROOT}"/k8s/*.yaml; do
  fname="$(basename "${yaml_file}")"
  # Skip sandbox-specific files unless --with-sandbox was passed; they require
  # the sandbox CRD to be pre-installed and contain literal {runId} placeholders
  # that cannot be substituted by envsubst.
  if [[ "${WITH_SANDBOX}" == "0" ]] && \
     [[ "${fname}" == "sandbox-claim-template.yaml" || \
        "${fname}" == "sandbox-template.yaml" || \
        "${fname}" == "sandbox-warmpool.yaml" ]]; then
    echo "  skipped (sandbox, needs CRD): ${fname}"
    continue
  fi
  envsubst '${HOST} ${ACR_LOGIN_SERVER} ${IMAGE_TAG} ${IDENTITY_CLIENT_ID} ${KEYVAULT_NAME} ${TENANT_ID}' \
    < "${yaml_file}" > "${RENDERED_DIR}/${fname}"
  echo "  rendered: ${fname}"
done

# -- Step 6: Apply PVCs before deployment -------------------------------------
# PVCs must be created before pods start so volume mounts succeed on first
# scheduling. Applied from the rendered dir so namespace substitution is done.
echo ""
echo "Applying PersistentVolumeClaims..."
kubectl apply -f "${RENDERED_DIR}/pvc-data.yaml"
echo "  [applied] pvc-data.yaml"
kubectl apply -f "${RENDERED_DIR}/pvc-workspace.yaml"
echo "  [applied] pvc-workspace.yaml"

# -- Step 6b: Create MCP secrets (idempotent) ---------------------------------
# agentweaver-mcp-secrets is required by mcp-deployment.yaml.
# Set MCP_API_KEY, MCP_AUTH_API_KEY, MCP_AUTH_USER before running this script.
echo ""
echo "Applying MCP secrets..."
kubectl create secret generic agentweaver-mcp-secrets \
  --namespace "${NAMESPACE}" \
  --from-literal=api-key="${MCP_API_KEY}" \
  --from-literal=auth-api-key="${MCP_AUTH_API_KEY}" \
  --from-literal=auth-user="${MCP_AUTH_USER}" \
  --dry-run=client -o yaml | kubectl apply -f -
echo "  [applied] agentweaver-mcp-secrets"

# -- Step 7: Apply remaining rendered manifests --------------------------------
echo ""
echo "Applying manifests..."
for rendered_file in "${RENDERED_DIR}"/*.yaml; do
  fname="$(basename "${rendered_file}")"
  # Skip namespace (already applied above) and PVCs (applied in Step 6)
  [[ "${fname}" == "namespace.yaml" ]] && continue
  [[ "${fname}" == pvc-*.yaml ]] && continue
  kubectl apply -f "${rendered_file}"
  echo "  [applied] ${fname}"
done

# -- Step 8: Wait for Gateway -------------------------------------------------
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

# -- Step 9: Wait for rollouts ------------------------------------------------
echo ""
echo "Waiting for API deployment rollout..."
kubectl rollout status deployment/agentweaver-api \
  --namespace "${NAMESPACE}" \
  --timeout=120s

echo "Waiting for Frontend deployment rollout..."
kubectl rollout status deployment/agentweaver-frontend \
  --namespace "${NAMESPACE}" \
  --timeout=120s

echo "Waiting for MCP deployment rollout..."
kubectl rollout status deployment/agentweaver-mcp \
  --namespace "${NAMESPACE}" \
  --timeout=120s

# -- Final output -------------------------------------------------------------
echo ""
echo "==================================================="
echo " DEPLOYMENT COMPLETE"
echo "==================================================="
echo ""
echo "  Frontend URL: https://${HOST}/"
echo "  API URL:      https://${HOST}/api/"
echo "  Gateway IP:   ${GATEWAY_IP}"
echo ""
echo "  Next step:"
echo "    bash scripts/aks/40-verify.sh"
echo ""
echo "  To check status:"
echo "    kubectl get gateway,httproute,pod,svc -n ${NAMESPACE}"
