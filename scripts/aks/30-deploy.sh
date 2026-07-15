#!/usr/bin/env bash
# 30-deploy.sh -- Deploy Agentweaver to AKS.
# Keep in sync with 30-deploy.ps1 (PowerShell equivalent).

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

# Derive compound variables from primitives so templates can reference them directly.
AGENTHOST_KEYVAULT_URI="${AGENTHOST_KEYVAULT_URI:-https://${KEYVAULT_NAME}.vault.azure.net/}"
export AGENTHOST_KEYVAULT_URI

echo "Applying namespace..."
kubectl apply -f "${REPO_ROOT}/k8s/namespace.yaml"

# Provision monitoring if not already done
if ! az monitor app-insights component show --app agentweaver-insights -g "${RESOURCE_GROUP}" &>/dev/null; then
    echo ""
    echo "Provisioning monitoring (Application Insights + Managed Prometheus)..."
    bash "$(dirname "$0")/15-provision-monitoring.sh"
fi

APPINSIGHTS_WORKSPACE_ID="$(az monitor log-analytics workspace show \
    --resource-group "${RESOURCE_GROUP}" \
    --workspace-name agentweaver-logs \
    --query customerId \
    --output tsv 2>/dev/null || true)"
export APPINSIGHTS_WORKSPACE_ID

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
spec:
  target:
    secret: agentweaver-tls
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

# --- Preview gateway cert path (CONFIRMED 2026-06-28 by live spike) ---
# AKS App Routing DefaultDomainCertificate does NOT support nested wildcards.
# The DDC CRD has no spec.hostname field — it always issues *.{zone} regardless
# of object name or target secret name.  status.domain is always *.{zone}.
# Spike evidence: cert-preview-spike DDC issued CN=*.6a3de4fe60529400010f3fba.
# westus2.staging.aksapp.io (not *.preview.{zone}); secret SAN confirmed same.
#
# Therefore we always take the single-label fallback path and reuse agentweaver-tls.
# Preview hostnames: {token}-preview.{zone}  (e.g. swift-falcon-amber-k7m2...-preview.6a3de4fe...aksapp.io)
# If AKS adds nested DDC support in the future, restore the probe below and update
# PREVIEW_TLS_SECRET to a new cert-preview DDC secret + add the cert-preview DDC object.
echo ""
echo "Setting preview gateway hostname (single-label fallback — AKS nested DDC not supported)..."
ZONE="${DOMAIN#\*.}"
ZONE_SUFFIX="${ZONE}"
PREVIEW_TLS_SECRET="agentweaver-tls"

export PREVIEW_HOSTNAME="*.${ZONE_SUFFIX}"
export PREVIEW_TLS_SECRET
export SANDBOX_PREVIEW_ENABLED="true"
export SANDBOX_PREVIEW_ZONE_SUFFIX="${ZONE_SUFFIX}"

echo "  Preview hostname:    ${PREVIEW_HOSTNAME}"
echo "  Preview TLS secret:  ${PREVIEW_TLS_SECRET}"
echo "  ZoneSuffix (API):    ${ZONE_SUFFIX}"

rm -rf "${RENDERED_DIR}"
mkdir -p "${RENDERED_DIR}"

echo ""
echo "Rendering manifests..."
for yaml_file in "${REPO_ROOT}"/k8s/*.yaml; do
  fname="$(basename "${yaml_file}")"
  envsubst '${HOST} ${ACR_LOGIN_SERVER} ${IMAGE_TAG} ${AGENTHOST_IMAGE_TAG} ${IDENTITY_CLIENT_ID} ${KEYVAULT_NAME} ${AGENTHOST_KEYVAULT_URI} ${TENANT_ID} ${PREVIEW_HOSTNAME} ${PREVIEW_TLS_SECRET} ${SANDBOX_PREVIEW_ENABLED} ${SANDBOX_PREVIEW_ZONE_SUFFIX} ${APPINSIGHTS_WORKSPACE_ID}' \
    < "${yaml_file}" > "${RENDERED_DIR}/${fname}"
  echo "  rendered: ${fname}"
done

echo ""
echo "Applying identity, secrets, RBAC, quotas, and PVCs..."
apply_rendered serviceaccount-api.yaml
apply_rendered serviceaccount-agenthost.yaml
kubectl wait \
  --for=jsonpath='{.metadata.annotations.azure\.workload\.identity/client-id}'="${IDENTITY_CLIENT_ID}" \
  serviceaccount/agentweaver-api \
  --namespace "${NAMESPACE}" \
  --timeout=60s
apply_rendered secret-provider-class.yaml
echo "  [note] secret-provider-class.yaml is static only: agentweaver-user-tokens contains ghtok-installation; per-run user-token SPCs are created/deleted by the API at AgentHost launch/release."
apply_rendered rbac-api.yaml
apply_rendered quota.yaml
apply_rendered storageclass-workspace.yaml
apply_rendered pvc-data.yaml
apply_rendered pvc-workspace.yaml

echo ""
echo "Applying network policies and egress allowlists..."
apply_rendered networkpolicy-default-deny.yaml
apply_rendered networkpolicy-mcp.yaml
apply_rendered networkpolicy-sandbox.yaml
# H2 (spec-018): A2A ingress — worker→agenthost on port 8088 only; no egress change.
apply_rendered networkpolicy-agenthost.yaml
apply_rendered networkpolicy-agenthost-api-egress.yaml
apply_rendered networkpolicy-agenthost-egress.yaml
apply_rendered cilium-network-policy-sandbox.yaml
apply_rendered serviceentry-telemetry.yaml
# spec-018 P2: Allow API pods to reach PostgreSQL Flexible Server on port 5432.
apply_rendered networkpolicy-postgres-egress.yaml
# spec-018 P3: Worker-tier egress policies (DNS, HTTPS, internal, Postgres, OTEL).
apply_rendered networkpolicy-worker.yaml

echo ""
echo "Applying services, gateway, and routes..."
# H1 (spec-018): generate A2A mTLS certs (idempotent — skips if secrets exist).
echo "Ensuring A2A mTLS certificates are present (H1)..."
bash "${SCRIPT_DIR}/gen-a2a-mtls-certs.sh"
# H4/H3 (spec-018): AgentHost Kestrel + card-authz config.
apply_rendered configmap-agenthost.yaml
apply_rendered api-service.yaml
apply_rendered frontend-service.yaml
apply_rendered mcp-service.yaml
apply_rendered gateway.yaml
apply_rendered gateway-preview.yaml
apply_rendered httproute-api.yaml
apply_rendered httproute-frontend.yaml
apply_rendered mcp-httproute.yaml

echo ""
echo "Applying AgentHost sandbox template and warm pool (if CRD is available)..."
if kubectl api-resources --api-group=extensions.agents.x-k8s.io 2>/dev/null | grep -q SandboxTemplate; then
  # spec-018 pod-per-run: AgentHost SandboxTemplate + warm pool. The template MUST be
  # applied before the warm pool (the pool's sandboxTemplateRef points at it), and the
  # warm pool MUST exist before AgentHost claims (which bind via spec.warmPoolRef.name).
  apply_rendered sandbox-template-agenthost.yaml
  apply_rendered sandbox-warmpool-agenthost.yaml
else
  echo "  [SKIP] agent-sandbox CRD not installed — AgentHost sandbox template skipped."
fi

echo ""
echo "Waiting for gateway/agentweaver-gateway to become Programmed (up to 3 min)..."
kubectl wait \
  --for=condition=Programmed \
  gateway/agentweaver-gateway \
  --namespace "${NAMESPACE}" \
  --timeout=180s

echo "Waiting for gateway/agentweaver-preview-gateway to become Programmed (up to 3 min)..."
kubectl wait \
  --for=condition=Programmed \
  gateway/agentweaver-preview-gateway \
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
# spec-018 P3: Worker Deployment + autoscaling.
# ORDERING NOTE: Apply worker AFTER api-deployment so the agentweaver-api service
# (Agentweaver__ApiBaseUrl target) is already present. The worker init container
# runs EF migrations against Postgres; it will restart until Tank's Postgres migration
# set is merged + the image is rebuilt. This is safe — web tier (api-deployment) keeps
# serving on SQLite in the meantime.
echo "Applying worker deployment and HPA (spec-018 P3)..."
apply_rendered worker-deployment.yaml
apply_rendered worker-hpa.yaml

echo ""
echo "Waiting for API deployment rollout..."
kubectl rollout status deployment/agentweaver-api --namespace "${NAMESPACE}" --timeout=180s

echo "Waiting for Frontend deployment rollout..."
kubectl rollout status deployment/agentweaver-frontend --namespace "${NAMESPACE}" --timeout=120s

echo "Waiting for MCP deployment rollout..."
kubectl rollout status deployment/agentweaver-mcp --namespace "${NAMESPACE}" --timeout=120s

echo "Waiting for Worker deployment rollout..."
# Worker init container runs EF Postgres migrations — allow extra time.
# If Tank's Postgres migration set is not yet merged, this will timeout but is non-fatal
# (web tier is healthy; worker will come up once migrations are applied).
kubectl rollout status deployment/agentweaver-worker --namespace "${NAMESPACE}" --timeout=300s || \
  echo "  WARNING: Worker rollout did not complete within 300s. Check: kubectl logs -n ${NAMESPACE} -l app=agentweaver-worker --all-containers"

echo ""
echo "Verifying image provenance (post-deploy safety net for #251)..."
# Confirms the images now actually running in the cluster are provably built
# from ${IMAGE_TAG}'s source (no stale retag-forward drift). This must run
# after rollout so pods have already settled on their final image/digest.
#
# BUGFIX: this used to default VERIFY_GIT_REF to IMAGE_TAG (e.g. "v0.9.63").
# IMAGE_TAG is derived from the VERSION file, not from an actual git tag --
# release_ref_for_tag() in 20-build-push-images.sh explicitly notes that
# releases since v0.9.36 deliberately stopped creating a matching git tag.
# Passing a non-existent tag straight to `git rev-parse --verify <ref>^{commit}`
# inside 25-verify-image-provenance.sh fails with "fatal: Needed a single
# revision" -- this was reproduced live at the end of a real deploy. HEAD is
# the commit actually checked out for this deploy (the one IMAGE_TAG's VERSION
# bump belongs to), so default to HEAD instead, exactly like
# 25-verify-image-provenance.sh's own standalone default.
if ! VERIFY_GIT_REF="${VERIFY_GIT_REF:-HEAD}" bash "${SCRIPT_DIR}/25-verify-image-provenance.sh"; then
  echo "" >&2
  echo "ERROR: Image provenance verification FAILED -- see output above." >&2
  echo "  A running pod's image does not provably match ${IMAGE_TAG}'s source, or watched" >&2
  echo "  paths drifted since the image was built. This is the exact #251 stale-retag failure" >&2
  echo "  mode -- treating the deploy as FAILED rather than silently succeeding." >&2
  echo "  Re-run scripts/aks/20-build-push-images.sh with FORCE_REBUILD=true for the affected" >&2
  echo "  image(s), then redeploy." >&2
  exit 1
fi
echo "  [OK] All running images verified against source (${IMAGE_TAG})."

echo ""
echo "==================================================="
echo " DEPLOYMENT COMPLETE"
echo "==================================================="
echo ""
echo "  Frontend URL:        https://${HOST}/"
echo "  API URL:             https://${HOST}/api/"
echo "  MCP URL:             https://${HOST}/mcp/"
echo "  Gateway IP:          ${GATEWAY_IP}"
echo ""
echo "  Preview gateway:     ${PREVIEW_HOSTNAME} (TLS: ${PREVIEW_TLS_SECRET})"
echo "  Preview zone suffix: ${ZONE_SUFFIX}"
echo "  Sandbox__Preview__Enabled:          true"
echo "  Sandbox__Preview__ZoneSuffix:       ${ZONE_SUFFIX}"
echo "  Sandbox__Preview__GatewayName:      agentweaver-preview-gateway"
echo "  Sandbox__Preview__GatewayNamespace: agentweaver"
echo ""
echo "  Next step:"
echo "    bash scripts/aks/40-verify.sh"
echo ""
echo "  To check status:"
echo "    kubectl get gateway,httproute,pod,svc -n ${NAMESPACE}"
