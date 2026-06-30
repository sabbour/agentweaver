#!/usr/bin/env bash
# scripts/aks/gen-a2a-mtls-certs.sh
#
# Generate workload-bound mTLS certificates for the A2A transport (spec-018 H1).
#
# Creates three K8s Secrets in the agentweaver namespace:
#   agentweaver-a2a-ca          — CA certificate (used for cross-validation)
#   agentweaver-a2a-server-tls  — AgentHost server cert + key (mounted in sandbox pod)
#   agentweaver-a2a-client-tls  — Worker client cert + key (mounted in API/worker pod)
#
# mTLS model:
#   - The sandbox AgentHost presents its server cert (CN=agentweaver-agenthost).
#   - The worker presents its client cert (CN=agentweaver-worker).
#   - Both certs are signed by the same internal CA.
#   - Server validates client cert against CA + CN.
#   - Client validates server cert against CA + CN (hostname check skipped;
#     pods are addressed by IP — SPIRE recommended for full SVID identity, deferred).
#
# SPIRE deferral note:
#   This script uses a Kubernetes-Secret-backed self-signed CA as a practical
#   substitute for SPIFFE/SPIRE workload identity.  The key security properties
#   (mutual authentication, per-workload distinct certs, CA-pinned trust) are
#   preserved.  When SPIRE is provisioned on the cluster, these secrets should
#   be replaced with SPIFFE SVIDs issued by the SPIRE server, and this script
#   retired.  See spec-018 H1 / platform-deployment.md §4.
#
# Usage:
#   source scripts/aks/00-variables.sh  # sets NAMESPACE, KEYVAULT_NAME, etc.
#   bash scripts/aks/gen-a2a-mtls-certs.sh
#
# Idempotency: existing secrets are NOT overwritten unless --force is passed.
#   bash scripts/aks/gen-a2a-mtls-certs.sh --force
#
# Requirements: openssl, kubectl (with an active cluster context).
# Optional: AGENTWEAVER_TMP_DIR for repo-local scratch files.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
source "${SCRIPT_DIR}/00-variables.sh"

FORCE=false
for arg in "$@"; do [[ "$arg" == "--force" ]] && FORCE=true; done

NAMESPACE="${NAMESPACE:-agentweaver}"
SCRATCH_ROOT="${AGENTWEAVER_TMP_DIR:-${REPO_ROOT}/.agentweaver/tmp}"
mkdir -p "${SCRATCH_ROOT}"
chmod 700 "${SCRATCH_ROOT}" 2>/dev/null || true
WORKDIR="${SCRATCH_ROOT}/a2a-mtls-${$}"
rm -rf "${WORKDIR}"
mkdir -p "${WORKDIR}"
trap 'rm -rf "${WORKDIR}"' EXIT

echo ""
echo "=== A2A mTLS certificate generation (spec-018 H1) ==="
echo "  Namespace:  ${NAMESPACE}"
echo "  Work dir:   ${WORKDIR}"
echo "  Force regen: ${FORCE}"
echo ""

# ── helpers ──────────────────────────────────────────────────────────────────

secret_exists() {
  kubectl get secret "$1" --namespace "${NAMESPACE}" &>/dev/null
}

# ── check idempotency ─────────────────────────────────────────────────────────

if ! "${FORCE}"; then
  existing=()
  secret_exists agentweaver-a2a-ca           && existing+=(agentweaver-a2a-ca)
  secret_exists agentweaver-a2a-server-tls   && existing+=(agentweaver-a2a-server-tls)
  secret_exists agentweaver-a2a-client-tls   && existing+=(agentweaver-a2a-client-tls)

  if [[ ${#existing[@]} -eq 3 ]]; then
    echo "[OK] All three A2A mTLS secrets already exist — skipping generation."
    echo "     Pass --force to regenerate."
    exit 0
  elif [[ ${#existing[@]} -gt 0 ]]; then
    echo "WARNING: Partial secrets found: ${existing[*]}"
    echo "         Run with --force to regenerate all three consistently."
    exit 1
  fi
fi

# ── validate tools ────────────────────────────────────────────────────────────

for tool in openssl kubectl; do
  if ! command -v "${tool}" &>/dev/null; then
    echo "ERROR: ${tool} not found on PATH."
    exit 1
  fi
done

# ── 1. Generate CA ────────────────────────────────────────────────────────────

echo "Generating internal A2A CA..."
openssl genrsa -out "${WORKDIR}/ca.key" 4096

openssl req -new -x509 \
  -key "${WORKDIR}/ca.key" \
  -out "${WORKDIR}/ca.crt" \
  -days 730 \
  -subj "/CN=agentweaver-a2a-ca/O=agentweaver" \
  -addext "keyUsage=critical,keyCertSign,cRLSign" \
  -addext "basicConstraints=critical,CA:TRUE"

echo "  CA certificate generated."

# ── 2. Generate server cert (AgentHost / sandbox pod) ─────────────────────────

echo "Generating AgentHost server certificate..."
openssl genrsa -out "${WORKDIR}/server.key" 2048

openssl req -new \
  -key "${WORKDIR}/server.key" \
  -out "${WORKDIR}/server.csr" \
  -subj "/CN=agentweaver-agenthost/O=agentweaver"

cat > "${WORKDIR}/server-ext.cnf" <<EOF
[req_ext]
keyUsage = critical, digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth
subjectAltName = @alt_names

[alt_names]
DNS.1 = agentweaver-agenthost
DNS.2 = agentweaver-agent-host.agentweaver.svc.cluster.local
EOF

openssl x509 -req \
  -in "${WORKDIR}/server.csr" \
  -CA "${WORKDIR}/ca.crt" \
  -CAkey "${WORKDIR}/ca.key" \
  -CAcreateserial \
  -out "${WORKDIR}/server.crt" \
  -days 365 \
  -extensions req_ext \
  -extfile "${WORKDIR}/server-ext.cnf"

echo "  Server certificate generated."

# ── 3. Generate client cert (worker / API pod) ────────────────────────────────

echo "Generating worker client certificate..."
openssl genrsa -out "${WORKDIR}/client.key" 2048

openssl req -new \
  -key "${WORKDIR}/client.key" \
  -out "${WORKDIR}/client.csr" \
  -subj "/CN=agentweaver-worker/O=agentweaver"

cat > "${WORKDIR}/client-ext.cnf" <<EOF
[req_ext]
keyUsage = critical, digitalSignature
extendedKeyUsage = clientAuth
EOF

openssl x509 -req \
  -in "${WORKDIR}/client.csr" \
  -CA "${WORKDIR}/ca.crt" \
  -CAkey "${WORKDIR}/ca.key" \
  -CAcreateserial \
  -out "${WORKDIR}/client.crt" \
  -days 365 \
  -extensions req_ext \
  -extfile "${WORKDIR}/client-ext.cnf"

echo "  Client certificate generated."

# ── 4. Apply secrets ──────────────────────────────────────────────────────────

apply_secret() {
  local name="$1"
  if "${FORCE}" && secret_exists "${name}"; then
    kubectl delete secret "${name}" --namespace "${NAMESPACE}"
  fi
}

echo ""
echo "Applying K8s Secrets..."

apply_secret agentweaver-a2a-ca
cat > "${WORKDIR}/secret-a2a-ca.yaml" <<YAML
apiVersion: v1
kind: Secret
metadata:
  name: agentweaver-a2a-ca
  namespace: ${NAMESPACE}
  labels:
    app.kubernetes.io/part-of: agentweaver
type: Opaque
data:
  ca.crt: $(base64 < "${WORKDIR}/ca.crt" | tr -d '\n')
YAML
kubectl apply -f "${WORKDIR}/secret-a2a-ca.yaml"
echo "  [applied] agentweaver-a2a-ca"

apply_secret agentweaver-a2a-server-tls
# Build a single Secret manifest that includes tls.crt, tls.key, and ca.crt so
# the sandbox pod has all three in one volume mount — no kubectl patch required.
cat > "${WORKDIR}/secret-a2a-server-tls.yaml" <<YAML
apiVersion: v1
kind: Secret
metadata:
  name: agentweaver-a2a-server-tls
  namespace: ${NAMESPACE}
  labels:
    app.kubernetes.io/part-of: agentweaver
type: kubernetes.io/tls
data:
  tls.crt: $(base64 < "${WORKDIR}/server.crt" | tr -d '\n')
  tls.key: $(base64 < "${WORKDIR}/server.key" | tr -d '\n')
  ca.crt:  $(base64 < "${WORKDIR}/ca.crt"     | tr -d '\n')
YAML
kubectl apply -f "${WORKDIR}/secret-a2a-server-tls.yaml"
echo "  [applied] agentweaver-a2a-server-tls (tls.crt + tls.key + ca.crt)"

apply_secret agentweaver-a2a-client-tls
cat > "${WORKDIR}/secret-a2a-client-tls.yaml" <<YAML
apiVersion: v1
kind: Secret
metadata:
  name: agentweaver-a2a-client-tls
  namespace: ${NAMESPACE}
  labels:
    app.kubernetes.io/part-of: agentweaver
type: kubernetes.io/tls
data:
  tls.crt: $(base64 < "${WORKDIR}/client.crt" | tr -d '\n')
  tls.key: $(base64 < "${WORKDIR}/client.key" | tr -d '\n')
  ca.crt:  $(base64 < "${WORKDIR}/ca.crt"     | tr -d '\n')
YAML
kubectl apply -f "${WORKDIR}/secret-a2a-client-tls.yaml"
echo "  [applied] agentweaver-a2a-client-tls (tls.crt + tls.key + ca.crt)"

echo ""
echo "=== A2A mTLS certificate generation complete ==="
echo ""
echo "  Secret agentweaver-a2a-server-tls  → mounted in sandbox pod at /mnt/a2a-tls/"
echo "  Secret agentweaver-a2a-client-tls  → mounted in api/worker pod at /mnt/a2a-client-tls/"
echo "  CA cert (ca.crt) included in both mounts for mutual validation."
echo ""
echo "  REMINDER: Rotate certs before expiry (365 days) by re-running with --force."
echo "  FOLLOW-UP: Replace with SPIFFE/SPIRE SVIDs when SPIRE is provisioned on the cluster."
