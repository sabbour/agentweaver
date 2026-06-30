#!/usr/bin/env bash
# c3-scale-zero.sh -- Scale API replicas to zero for maintenance/migration.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${SCRIPT_DIR}/00-variables.sh"

echo ""
echo "=== Scale Agentweaver API to zero ==="
echo "  Namespace: ${NAMESPACE}"
echo ""

kubectl scale deployment/agentweaver-api \
  --namespace "${NAMESPACE}" \
  --replicas=0

kubectl rollout status deployment/agentweaver-api \
  --namespace "${NAMESPACE}" \
  --timeout=120s

echo ""
echo "[OK] agentweaver-api scaled to 0."
echo "     Restore with: bash scripts/aks/c4-flip-postgres.sh"
