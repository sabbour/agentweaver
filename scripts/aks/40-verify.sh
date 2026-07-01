#!/usr/bin/env bash
# 40-verify.sh -- Verify the Agentweaver AKS deployment is healthy.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${SCRIPT_DIR}/00-variables.sh"

PASS=0
FAIL=0

ok()   { echo "  [OK]   $*"; (( PASS++ )) || true; }
fail() { echo "  [FAIL] $*"; (( FAIL++ )) || true; }
info() { echo "  [INFO] $*"; }

echo ""
echo "=== Agentweaver AKS deployment verification ==="
echo "  Namespace: ${NAMESPACE}"
echo ""

echo "--- Pod status ---"
kubectl get pods -n "${NAMESPACE}" -o wide
echo ""

api_running=$(kubectl get pods -n "${NAMESPACE}" -l app=agentweaver-api --field-selector=status.phase=Running --no-headers 2>/dev/null | wc -l | tr -d ' ')
fe_running=$(kubectl get pods -n "${NAMESPACE}" -l app=agentweaver-frontend --field-selector=status.phase=Running --no-headers 2>/dev/null | wc -l | tr -d ' ')
mcp_running=$(kubectl get pods -n "${NAMESPACE}" -l app=agentweaver-mcp --field-selector=status.phase=Running --no-headers 2>/dev/null | wc -l | tr -d ' ')
worker_running=$(kubectl get pods -n "${NAMESPACE}" -l app=agentweaver-worker --field-selector=status.phase=Running --no-headers 2>/dev/null | wc -l | tr -d ' ')
agenthost_warm_running=$(kubectl get pods -n "${NAMESPACE}" -l app.kubernetes.io/component=agent-host --field-selector=status.phase=Running --no-headers 2>/dev/null | wc -l | tr -d ' ')

[[ "${api_running}" -ge 1 ]] && ok "API pod(s) running (${api_running})" || fail "No API pods in Running state"
[[ "${fe_running}" -ge 1 ]] && ok "Frontend pod(s) running (${fe_running})" || fail "No Frontend pods in Running state"
[[ "${mcp_running}" -ge 1 ]] && ok "MCP pod(s) running (${mcp_running})" || fail "No MCP pods in Running state"
[[ "${worker_running}" -ge 1 ]] && ok "Worker pod(s) running (${worker_running})" || fail "No Worker pods in Running state"
[[ "${agenthost_warm_running}" -ge 1 ]] && ok "AgentHost warm-pool pod(s) running (${agenthost_warm_running})" || fail "No AgentHost warm-pool pods in Running state"

echo ""
echo "--- Gateway status ---"
kubectl get gateway agentweaver-gateway -n "${NAMESPACE}" -o wide 2>/dev/null || true
kubectl get gateway agentweaver-preview-gateway -n "${NAMESPACE}" -o wide 2>/dev/null || true
echo ""

programmed=$(kubectl get gateway agentweaver-gateway -n "${NAMESPACE}" -o jsonpath='{.status.conditions[?(@.type=="Programmed")].status}' 2>/dev/null || echo "")
gateway_ip=$(kubectl get gateway agentweaver-gateway -n "${NAMESPACE}" -o jsonpath='{.status.addresses[0].value}' 2>/dev/null || echo "")
preview_programmed=$(kubectl get gateway agentweaver-preview-gateway -n "${NAMESPACE}" -o jsonpath='{.status.conditions[?(@.type=="Programmed")].status}' 2>/dev/null || echo "")

[[ "${programmed}" == "True" ]] && ok "Gateway Programmed=True" || fail "Gateway not yet Programmed (status=${programmed})"
[[ -n "${gateway_ip}" ]] && ok "Gateway address: ${gateway_ip}" || fail "Gateway has no address yet"
[[ "${preview_programmed}" == "True" ]] && ok "Preview Gateway Programmed=True" || fail "Preview Gateway not yet Programmed (status=${preview_programmed})"

echo ""
echo "--- HTTPRoute status ---"
kubectl get httproute -n "${NAMESPACE}" -o wide 2>/dev/null || true
echo ""

for route in agentweaver-api-route agentweaver-frontend-route agentweaver-mcp-route; do
  accepted=$(kubectl get httproute "${route}" -n "${NAMESPACE}" -o jsonpath='{.status.parents[0].conditions[?(@.type=="Accepted")].status}' 2>/dev/null || echo "")
  resolved=$(kubectl get httproute "${route}" -n "${NAMESPACE}" -o jsonpath='{.status.parents[0].conditions[?(@.type=="ResolvedRefs")].status}' 2>/dev/null || echo "")
  [[ "${accepted}" == "True" && "${resolved}" == "True" ]] && ok "HTTPRoute ${route}: Accepted=True, ResolvedRefs=True" || fail "HTTPRoute ${route}: Accepted=${accepted}, ResolvedRefs=${resolved}"
done

echo ""
DOMAIN=$(kubectl get defaultdomaincertificate cert --namespace "${NAMESPACE}" --output jsonpath='{.status.domain}' 2>/dev/null || echo "")
if [[ -n "${DOMAIN}" ]]; then
  HOST="agentweaver.${DOMAIN#\*.}"
  info "Ingress host: ${HOST}"
else
  info "Could not derive HOST from DefaultDomainCertificate — skipping HTTP checks"
  HOST=""
fi

if [[ -n "${HOST}" ]]; then
  echo ""
  echo "--- Authenticated feature validation ---"
  unauth_projects_status=$(curl -sSo /dev/null -w "%{http_code}" --max-time 10 "https://${HOST}/api/projects" 2>/dev/null || echo "000")
  [[ "${unauth_projects_status}" == "401" ]] && ok "Unauthenticated /api/projects rejected → HTTP ${unauth_projects_status}" || fail "Unauthenticated /api/projects → HTTP ${unauth_projects_status} (expected 401)"

  validation_token="${AGENTWEAVER_VALIDATION_TOKEN:-${GH_TOKEN:-}}"
  if [[ -z "${validation_token}" ]]; then
    info "Set AGENTWEAVER_VALIDATION_TOKEN or GH_TOKEN to validate signed-in identity plus project memory/decision APIs"
  else
    auth_status=$(curl -sSo /dev/null -w "%{http_code}" --max-time 10 -H "Authorization: Bearer ${validation_token}" "https://${HOST}/api/auth/github" 2>/dev/null || echo "000")
    projects_status=$(curl -sSo /dev/null -w "%{http_code}" --max-time 10 -H "Authorization: Bearer ${validation_token}" "https://${HOST}/api/projects" 2>/dev/null || echo "000")
    [[ "${auth_status}" == "200" ]] && ok "Authenticated /api/auth/github → HTTP ${auth_status}" || fail "Authenticated /api/auth/github → HTTP ${auth_status} (expected 200)"
    [[ "${projects_status}" == "200" ]] && ok "Authenticated /api/projects → HTTP ${projects_status}" || fail "Authenticated /api/projects → HTTP ${projects_status} (expected 200)"

    if command -v jq >/dev/null 2>&1; then
      projects_json=$(curl -sf --max-time 10 -H "Authorization: Bearer ${validation_token}" "https://${HOST}/api/projects" 2>/dev/null || echo "[]")
      project_id=$(printf '%s' "${projects_json}" | jq -r 'if type=="array" then .[0].id // .[0].projectId // empty else .projects[0].id // .projects[0].projectId // .items[0].id // .items[0].projectId // empty end' 2>/dev/null || echo "")
      if [[ -n "${project_id}" ]]; then
        for path in "/api/projects/${project_id}/memory" "/api/projects/${project_id}/decisions/inbox" "/api/projects/${project_id}/decisions"; do
          status=$(curl -sSo /dev/null -w "%{http_code}" --max-time 10 -H "Authorization: Bearer ${validation_token}" "https://${HOST}${path}" 2>/dev/null || echo "000")
          [[ "${status}" == "200" ]] && ok "Authenticated ${path} → HTTP ${status}" || fail "Authenticated ${path} → HTTP ${status} (expected 200)"
        done
      else
        info "Authenticated account has no project id to validate memory/decision APIs"
      fi
    else
      info "Install jq to validate project-scoped memory and decision APIs from the first authenticated project"
    fi
  fi
fi

echo ""
echo "--- SecretProviderClass sync ---"
for spc in agentweaver-secrets agentweaver-user-tokens; do
  if kubectl get secretproviderclass "${spc}" -n "${NAMESPACE}" >/dev/null 2>&1; then
    ok "SecretProviderClass ${spc} exists"
  else
    fail "SecretProviderClass ${spc} missing"
  fi
done
spc_status_count=$(kubectl get secretproviderclasspodstatus -n "${NAMESPACE}" --no-headers 2>/dev/null | wc -l | tr -d ' ' || echo "0")
[[ "${spc_status_count}" -ge 1 ]] && ok "SecretProviderClassPodStatus objects present (${spc_status_count})" || fail "No SecretProviderClassPodStatus objects found"
info "agentweaver-user-tokens is installation-only; run-scoped agentweaver-user-token-* SPCs appear only while AgentHost pods are running"

echo ""
echo "--- API RBAC ---"
if kubectl get role agentweaver-api-sandbox -n "${NAMESPACE}" >/dev/null 2>&1 && kubectl get rolebinding agentweaver-api-sandbox -n "${NAMESPACE}" >/dev/null 2>&1; then
  ok "API sandbox Role and RoleBinding exist"
else
  fail "API sandbox Role/RoleBinding missing"
fi
if kubectl auth can-i create sandboxclaims.extensions.agents.x-k8s.io --as="system:serviceaccount:${NAMESPACE}:agentweaver-api" -n "${NAMESPACE}" >/dev/null 2>&1 && \
   kubectl auth can-i create sandboxtemplates.extensions.agents.x-k8s.io --as="system:serviceaccount:${NAMESPACE}:agentweaver-api" -n "${NAMESPACE}" >/dev/null 2>&1 && \
   kubectl auth can-i create sandboxwarmpools.extensions.agents.x-k8s.io --as="system:serviceaccount:${NAMESPACE}:agentweaver-api" -n "${NAMESPACE}" >/dev/null 2>&1 && \
   kubectl auth can-i create secretproviderclasses.secrets-store.csi.x-k8s.io --as="system:serviceaccount:${NAMESPACE}:agentweaver-api" -n "${NAMESPACE}" >/dev/null 2>&1 && \
   kubectl auth can-i create pods/exec --as="system:serviceaccount:${NAMESPACE}:agentweaver-api" -n "${NAMESPACE}" >/dev/null 2>&1; then
  ok "API ServiceAccount can create SandboxClaims, run-scoped templates/pools/SPCs, and pods/exec"
else
  fail "API ServiceAccount lacks required sandbox or run-scoped SPC permissions"
fi

echo ""
echo "--- Sandbox CRDs/resources ---"
if kubectl get runtimeclass kata-vm-isolation &>/dev/null; then ok "kata-vm-isolation RuntimeClass present"; else fail "kata-vm-isolation RuntimeClass missing"; fi
if kubectl get sandboxtemplate agentweaver-agent-host -n "${NAMESPACE}" >/dev/null 2>&1; then ok "SandboxTemplate agentweaver-agent-host exists"; else fail "SandboxTemplate agentweaver-agent-host missing"; fi
if kubectl get sandboxwarmpool agentweaver-agent-host -n "${NAMESPACE}" >/dev/null 2>&1; then ok "SandboxWarmPool agentweaver-agent-host exists"; else fail "SandboxWarmPool agentweaver-agent-host missing"; fi
if kubectl get sandboxtemplate agentweaver-sandbox -n "${NAMESPACE}" >/dev/null 2>&1 || \
   kubectl get sandboxwarmpool agentweaver-sandbox -n "${NAMESPACE}" >/dev/null 2>&1; then
  fail "Legacy agentweaver-sandbox template/warm pool still exists; remove it before verifying"
else
  ok "Legacy agentweaver-sandbox template/warm pool absent"
fi

echo ""
echo "--- Storage ---"
if kubectl get storageclass azurefile-csi-premium-uid1000 >/dev/null 2>&1; then ok "Workspace StorageClass exists"; else fail "Workspace StorageClass missing"; fi
if kubectl get pvc agentweaver-workspace -n "${NAMESPACE}" >/dev/null 2>&1; then ok "Workspace PVC exists"; else fail "Workspace PVC missing"; fi

echo ""
echo "==================================================="
echo " VERIFICATION SUMMARY: ${PASS} passed, ${FAIL} failed"
echo "==================================================="
[[ "${FAIL}" -eq 0 ]] && echo " ALL CHECKS PASSED" || echo " SOME CHECKS FAILED — see output above"
echo ""

[[ "${FAIL}" -eq 0 ]]
