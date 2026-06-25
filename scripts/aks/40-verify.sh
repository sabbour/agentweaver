#!/usr/bin/env bash
# 40-verify.sh -- Verify the Agentweaver AKS deployment is healthy.
#
# Checks:
#   1. All pods are Running
#   2. Gateway is Programmed with a public address
#   3. HTTPRoutes are Accepted and ResolvedRefs
#   4. Frontend (/) returns HTTP 200
#   5. API health endpoint returns HTTP 200
#
# Requires: kubectl, curl.
# Run from the REPO ROOT.
#
# Usage:
#   source scripts/aks/00-variables.sh
#   bash scripts/aks/40-verify.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=00-variables.sh
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

# -- 1: Pod status ------------------------------------------------------------
echo "--- Pod status ---"
kubectl get pods -n "${NAMESPACE}" -o wide
echo ""

api_running=$(kubectl get pods -n "${NAMESPACE}" \
  -l app=agentweaver-api \
  --field-selector=status.phase=Running \
  --no-headers 2>/dev/null | wc -l | tr -d ' ')

fe_running=$(kubectl get pods -n "${NAMESPACE}" \
  -l app=agentweaver-frontend \
  --field-selector=status.phase=Running \
  --no-headers 2>/dev/null | wc -l | tr -d ' ')

[[ "${api_running}" -ge 1 ]] && ok "API pod(s) running (${api_running})" \
                              || fail "No API pods in Running state"
[[ "${fe_running}" -ge 1 ]]  && ok "Frontend pod(s) running (${fe_running})" \
                              || fail "No Frontend pods in Running state"

# -- 2: Gateway status --------------------------------------------------------
echo ""
echo "--- Gateway status ---"
kubectl get gateway agentweaver-gateway -n "${NAMESPACE}" -o wide 2>/dev/null || true
echo ""

programmed=$(kubectl get gateway agentweaver-gateway -n "${NAMESPACE}" \
  -o jsonpath='{.status.conditions[?(@.type=="Programmed")].status}' 2>/dev/null || echo "")
gateway_ip=$(kubectl get gateway agentweaver-gateway -n "${NAMESPACE}" \
  -o jsonpath='{.status.addresses[0].value}' 2>/dev/null || echo "")

[[ "${programmed}" == "True" ]] && ok "Gateway Programmed=True" \
                                || fail "Gateway not yet Programmed (status=${programmed})"
[[ -n "${gateway_ip}" ]] && ok "Gateway address: ${gateway_ip}" \
                          || fail "Gateway has no address yet"

# -- 3: HTTPRoute status ------------------------------------------------------
echo ""
echo "--- HTTPRoute status ---"
kubectl get httproute -n "${NAMESPACE}" -o wide 2>/dev/null || true
echo ""

for route in agentweaver-api-route agentweaver-frontend-route; do
  accepted=$(kubectl get httproute "${route}" -n "${NAMESPACE}" \
    -o jsonpath='{.status.parents[0].conditions[?(@.type=="Accepted")].status}' 2>/dev/null || echo "")
  resolved=$(kubectl get httproute "${route}" -n "${NAMESPACE}" \
    -o jsonpath='{.status.parents[0].conditions[?(@.type=="ResolvedRefs")].status}' 2>/dev/null || echo "")

  [[ "${accepted}" == "True" && "${resolved}" == "True" ]] \
    && ok "HTTPRoute ${route}: Accepted=True, ResolvedRefs=True" \
    || fail "HTTPRoute ${route}: Accepted=${accepted}, ResolvedRefs=${resolved}"
done

# -- 4: Derive host -----------------------------------------------------------
echo ""
DOMAIN=$(kubectl get defaultdomaincertificate cert \
  --namespace "${NAMESPACE}" \
  --output jsonpath='{.status.domain}' 2>/dev/null || echo "")

if [[ -n "${DOMAIN}" ]]; then
  HOST="agentweaver.${DOMAIN#\*.}"
  info "Ingress host: ${HOST}"
else
  info "Could not derive HOST from DefaultDomainCertificate — skipping HTTP checks"
  HOST=""
fi

# -- 5: HTTP smoke tests ------------------------------------------------------
if [[ -n "${HOST}" ]]; then
  echo ""
  echo "--- HTTP smoke tests ---"

  fe_status=$(curl -sSo /dev/null -w "%{http_code}" --max-time 10 \
    "https://${HOST}/" 2>/dev/null || echo "000")
  api_status=$(curl -sSo /dev/null -w "%{http_code}" --max-time 10 \
    "https://${HOST}/health" 2>/dev/null || echo "000")

  [[ "${fe_status}" == "200" ]] \
    && ok "Frontend / → HTTP ${fe_status}" \
    || fail "Frontend / → HTTP ${fe_status} (expected 200)"

  [[ "${api_status}" == "200" ]] \
    && ok "API /health → HTTP ${api_status}" \
    || fail "API /health → HTTP ${api_status} (expected 200)"
fi

# -- 6: RuntimeClass ----------------------------------------------------------
echo ""
echo "--- RuntimeClass check ---"
if kubectl get runtimeclass kata-vm-isolation &>/dev/null; then
  ok "kata-vm-isolation RuntimeClass present"
else
  fail "kata-vm-isolation RuntimeClass missing — Kata pod sandboxing unavailable"
fi

# -- Summary ------------------------------------------------------------------
echo ""
echo "==================================================="
echo " VERIFICATION SUMMARY: ${PASS} passed, ${FAIL} failed"
echo "==================================================="

[[ "${FAIL}" -eq 0 ]] && echo " ALL CHECKS PASSED" || echo " SOME CHECKS FAILED — see output above"
echo ""

[[ "${FAIL}" -eq 0 ]]
