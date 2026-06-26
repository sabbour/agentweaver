#!/usr/bin/env bash
# 16-provision-oauth-signing-key.sh
#
# Provisions the MCP OAuth 2.1 signing key in Azure Key Vault.
#
# The private key is stored as a PEM-encoded RSA-2048 secret named
# "mcp-oauth-signing-key".  The API pod reads this secret via the
# secrets-store CSI driver (SecretProviderClass: agentweaver-secrets)
# and exposes it as the env var Auth__OAuth__SigningKey.
#
# This script must be run ONCE before the first deploy that includes the
# OAuth Authorization Server endpoints.  Re-running is safe: it skips
# creation if the secret already exists and is non-empty.
#
# NOTE: Run this script manually with sufficient Azure CLI credentials
#       (az login as an identity with Key Vault Secrets Officer or
#       Contributor on the vault).  It is intentionally NOT called from
#       30-deploy.sh because key provisioning is a one-time operator
#       action, not a routine deployment step.
#
# Usage:
#   bash scripts/aks/16-provision-oauth-signing-key.sh
#
# Optional overrides (all have defaults from 00-variables.sh):
#   KEYVAULT_NAME   Key Vault name (default: agentweaver-kv)
#   RESOURCE_GROUP  Resource group   (default: agentweaver-rg)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${SCRIPT_DIR}/00-variables.sh"

SECRET_NAME="mcp-oauth-signing-key"

echo ""
echo "=== MCP OAuth signing key provisioning ==="
echo "  Key Vault:   ${KEYVAULT_NAME}"
echo "  Secret name: ${SECRET_NAME}"
echo ""

# Check whether the secret already exists and has a non-empty value.
existing_value=$(az keyvault secret show \
  --vault-name "${KEYVAULT_NAME}" \
  --name "${SECRET_NAME}" \
  --query "value" \
  --output tsv 2>/dev/null || true)

if [[ -n "${existing_value}" ]]; then
  echo "  [SKIP] Secret '${SECRET_NAME}' already exists in Key Vault '${KEYVAULT_NAME}'."
  echo "         To rotate, delete the secret version and re-run this script."
  exit 0
fi

echo "  Generating RSA-2048 private key..."

# Generate a PKCS#8 PEM-encoded RSA-2048 private key in a temp file that
# lives only in memory (tmpfs via /dev/shm when available, otherwise /tmp).
TMP_KEY_FILE="$(mktemp)"
trap 'rm -f "${TMP_KEY_FILE}"' EXIT

openssl genpkey \
  -algorithm RSA \
  -pkeyopt rsa_keygen_bits:2048 \
  -outform PEM \
  -out "${TMP_KEY_FILE}" 2>/dev/null

echo "  Storing private key as Key Vault secret '${SECRET_NAME}'..."

az keyvault secret set \
  --vault-name "${KEYVAULT_NAME}" \
  --name "${SECRET_NAME}" \
  --file "${TMP_KEY_FILE}" \
  --content-type "application/x-pem-file" \
  --output none

echo ""
echo "  [OK] Secret '${SECRET_NAME}' created successfully."
echo ""
echo "  Next steps:"
echo "    1. Run scripts/aks/30-deploy.sh to deploy the updated manifests."
echo "    2. Verify: kubectl get secret agentweaver-secrets -n agentweaver -o jsonpath='{.data.mcp-oauth-signing-key}' | base64 -d | head -1"
echo "       Expected: -----BEGIN PRIVATE KEY-----"
echo ""
echo "  Key rotation:"
echo "    Delete the current version in Key Vault and re-run this script."
echo "    The CSI driver polls every 2 min (rotation-poll-interval) and will"
echo "    pick up the new version automatically; no pod restart is required"
echo "    as long as the app re-reads Auth__OAuth__SigningKey on each token mint."
