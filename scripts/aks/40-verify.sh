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
warm_running=$(kubectl get pods -n "${NAMESPACE}" -l app=agentweaver-sandbox --field-selector=status.phase=Running --no-headers 2>/dev/null | wc -l | tr -d ' ')

[[ "${api_running}" -ge 1 ]] && ok "API pod(s) running (${api_running})" || fail "No API pods in Running state"
[[ "${fe_running}" -ge 1 ]] && ok "Frontend pod(s) running (${fe_running})" || fail "No Frontend pods in Running state"
[[ "${mcp_running}" -ge 1 ]] && ok "MCP pod(s) running (${mcp_running})" || fail "No MCP pods in Running state"
[[ "${warm_running}" -ge 1 ]] && ok "WarmPool sandbox pod(s) running (${warm_running})" || fail "No WarmPool sandbox pods in Running state"

echo ""
echo "--- Gateway status ---"
kubectl get gateway agentweaver-gateway -n "${NAMESPACE}" -o wide 2>/dev/null || true
echo ""

programmed=$(kubectl get gateway agentweaver-gateway -n "${NAMESPACE}" -o jsonpath='{.status.conditions[?(@.type=="Programmed")].status}' 2>/dev/null || echo "")
gateway_ip=$(kubectl get gateway agentweaver-gateway -n "${NAMESPACE}" -o jsonpath='{.status.addresses[0].value}' 2>/dev/null || echo "")

[[ "${programmed}" == "True" ]] && ok "Gateway Programmed=True" || fail "Gateway not yet Programmed (status=${programmed})"
[[ -n "${gateway_ip}" ]] && ok "Gateway address: ${gateway_ip}" || fail "Gateway has no address yet"

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
  echo "--- HTTP smoke tests ---"
  fe_status=$(curl -sSo /dev/null -w "%{http_code}" --max-time 10 "https://${HOST}/" 2>/dev/null || echo "000")
  api_status=$(curl -sSo /dev/null -w "%{http_code}" --max-time 10 "https://${HOST}/api/health" 2>/dev/null || echo "000")
  mcp_status=$(curl -sSo /dev/null -w "%{http_code}" --max-time 10 "https://${HOST}/mcp/health" 2>/dev/null || echo "000")
  [[ "${fe_status}" == "200" ]] && ok "Frontend / → HTTP ${fe_status}" || fail "Frontend / → HTTP ${fe_status} (expected 200)"
  [[ "${api_status}" == "200" ]] && ok "API /api/health → HTTP ${api_status}" || fail "API /api/health → HTTP ${api_status} (expected 200)"
  [[ "${mcp_status}" == "200" ]] && ok "MCP /mcp/health → HTTP ${mcp_status}" || fail "MCP /mcp/health → HTTP ${mcp_status} (expected 200)"
fi

echo ""
echo "--- SecretProviderClass sync ---"
for spc in agentweaver-secrets; do
  if kubectl get secretproviderclass "${spc}" -n "${NAMESPACE}" >/dev/null 2>&1; then
    ok "SecretProviderClass ${spc} exists"
  else
    fail "SecretProviderClass ${spc} missing"
  fi
done
spc_status_count=$(kubectl get secretproviderclasspodstatus -n "${NAMESPACE}" --no-headers 2>/dev/null | wc -l | tr -d ' ' || echo "0")
[[ "${spc_status_count}" -ge 1 ]] && ok "SecretProviderClassPodStatus objects present (${spc_status_count})" || fail "No SecretProviderClassPodStatus objects found"

echo ""
echo "--- API RBAC ---"
if kubectl get role agentweaver-api-sandbox -n "${NAMESPACE}" >/dev/null 2>&1 && kubectl get rolebinding agentweaver-api-sandbox -n "${NAMESPACE}" >/dev/null 2>&1; then
  ok "API sandbox Role and RoleBinding exist"
else
  fail "API sandbox Role/RoleBinding missing"
fi
if kubectl auth can-i create sandboxclaims.extensions.agents.x-k8s.io --as="system:serviceaccount:${NAMESPACE}:agentweaver-api" -n "${NAMESPACE}" >/dev/null 2>&1 && \
   kubectl auth can-i create pods/exec --as="system:serviceaccount:${NAMESPACE}:agentweaver-api" -n "${NAMESPACE}" >/dev/null 2>&1; then
  ok "API ServiceAccount can create SandboxClaims and pods/exec"
else
  fail "API ServiceAccount lacks required sandbox permissions"
fi

echo ""
echo "--- Sandbox CRDs/resources ---"
if kubectl get runtimeclass kata-vm-isolation &>/dev/null; then ok "kata-vm-isolation RuntimeClass present"; else fail "kata-vm-isolation RuntimeClass missing"; fi
if kubectl get sandboxtemplate agentweaver-sandbox -n "${NAMESPACE}" >/dev/null 2>&1; then ok "SandboxTemplate agentweaver-sandbox exists"; else fail "SandboxTemplate agentweaver-sandbox missing"; fi
if kubectl get sandboxwarmpool agentweaver-sandbox -n "${NAMESPACE}" >/dev/null 2>&1; then ok "SandboxWarmPool agentweaver-sandbox exists"; else fail "SandboxWarmPool agentweaver-sandbox missing"; fi

echo ""
echo "==================================================="
echo " VERIFICATION SUMMARY: ${PASS} passed, ${FAIL} failed"
echo "==================================================="
[[ "${FAIL}" -eq 0 ]] && echo " ALL CHECKS PASSED" || echo " SOME CHECKS FAILED — see output above"
echo ""

[[ "${FAIL}" -eq 0 ]]
