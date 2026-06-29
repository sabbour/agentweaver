#!/usr/bin/env bash
# 17-provision-postgres.sh
#
# Provisions Azure Database for PostgreSQL Flexible Server for Agentweaver.
#
# Networking: Private access via VNet integration (recommended by platform-deployment.md §1.1).
# A dedicated /28 delegated subnet is created in the AKS VNet; the server gets a private IP
# only — no public endpoint.  Resolution is via the privatelink.postgres.database.azure.com
# Private DNS zone linked to the AKS VNet.
#
# Auth: password stored in Kubernetes Secret `agentweaver-postgres` (namespace agentweaver).
# Passwordless Entra auth via workload identity is the recommended long-term path
# (platform-deployment.md §1.2) — see TODO at end of script for promotion steps.
#
# HARDENING NOTE: This script uses password auth as a bring-up fallback.
# Follow-up work: set the Entra admin on the server, create a DB role mapped to the UAMI
# principal (`agentweaver-api-identity`) with least-priv grants, and replace the
# K8s secret with a workload-identity token credential.  No stored password, no rotation.
#
# Usage:
#   bash scripts/aks/17-provision-postgres.sh
#
# Optional overrides (all have defaults from 00-variables.sh or this file):
#   RESOURCE_GROUP         User resource group (default: agentweaver-rg)
#   PG_SERVER_NAME         Flexible Server name  (default: agentweaver-pg)
#   PG_DB_NAME             Application database  (default: agentweaver)
#   PG_ADMIN_USER          Admin login           (default: pgadmin)
#   PG_VERSION             PostgreSQL major ver  (default: 16)
#   PG_SKU                 Compute SKU           (default: Standard_D2ds_v4)
#   PG_STORAGE_GB          Storage GB            (default: 32)
#   PG_HA_MODE             ZoneRedundant|SameZone|Disabled (default: ZoneRedundant)
#   PG_BACKUP_DAYS         PITR retention days   (default: 7)
#   PG_SUBNET_PREFIX       CIDR for new subnet   (default: 10.225.0.0/28)
#   PG_SUBNET_NAME         Subnet name           (default: aks-postgres)
#   PG_DNS_ZONE            Private DNS zone name (default: privatelink.postgres.database.azure.com)
#   PG_DNS_LINK_NAME       VNet link name        (default: agentweaver-pg-dns-link)
#   NAMESPACE              K8s namespace         (default: agentweaver)
#   AKS_VNET_NAME          AKS VNet name         (default: aks-vnet-88700483)
#   AKS_MC_RG              Node resource group   (default: MC_agentweaver-rg_agentweaver-aks-2_westus2)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${SCRIPT_DIR}/00-variables.sh"

# ---------------------------------------------------------------------------
# Postgres-specific variables
# ---------------------------------------------------------------------------
PG_SERVER_NAME="${PG_SERVER_NAME:-agentweaver-pg}"
PG_DB_NAME="${PG_DB_NAME:-agentweaver}"
PG_ADMIN_USER="${PG_ADMIN_USER:-pgadmin}"
PG_VERSION="${PG_VERSION:-16}"
PG_SKU="${PG_SKU:-Standard_D2ds_v4}"
PG_STORAGE_GB="${PG_STORAGE_GB:-32}"
PG_HA_MODE="${PG_HA_MODE:-ZoneRedundant}"
PG_BACKUP_DAYS="${PG_BACKUP_DAYS:-7}"

PG_SUBNET_NAME="${PG_SUBNET_NAME:-aks-postgres}"
PG_SUBNET_PREFIX="${PG_SUBNET_PREFIX:-10.225.0.0/28}"
PG_DNS_ZONE="${PG_DNS_ZONE:-privatelink.postgres.database.azure.com}"
PG_DNS_LINK_NAME="${PG_DNS_LINK_NAME:-agentweaver-pg-dns-link}"

AKS_VNET_NAME="${AKS_VNET_NAME:-aks-vnet-88700483}"
AKS_MC_RG="${AKS_MC_RG:-MC_agentweaver-rg_agentweaver-aks-2_westus2}"

SUBSCRIPTION_ID="$(az account show --query id --output tsv)"
AKS_VNET_ID="/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${AKS_MC_RG}/providers/Microsoft.Network/virtualNetworks/${AKS_VNET_NAME}"
# Full resource ID required when DNS zone is in a different RG from the server (cross-RG safe).
PG_DNS_ZONE_ID="/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.Network/privateDnsZones/${PG_DNS_ZONE}"
PG_FQDN="${PG_SERVER_NAME}.postgres.database.azure.com"

echo ""
echo "=== Agentweaver PostgreSQL Flexible Server provisioning ==="
echo "  Resource Group:  ${RESOURCE_GROUP}"
echo "  Location:        ${LOCATION}"
echo "  Server name:     ${PG_SERVER_NAME}"
echo "  FQDN:            ${PG_FQDN}"
echo "  Database:        ${PG_DB_NAME}"
echo "  Version:         ${PG_VERSION}"
echo "  SKU:             ${PG_SKU}"
echo "  Storage (GB):    ${PG_STORAGE_GB}"
echo "  HA mode:         ${PG_HA_MODE}"
echo "  Subnet:          ${PG_SUBNET_NAME} (${PG_SUBNET_PREFIX})"
echo "  DNS zone:        ${PG_DNS_ZONE}"
echo "  AKS VNet:        ${AKS_VNET_NAME} in ${AKS_MC_RG}"
echo "  K8s namespace:   ${NAMESPACE}"
echo ""

# ---------------------------------------------------------------------------
# 1. Delegated subnet for Flexible Server VNet injection
# ---------------------------------------------------------------------------
echo "Step 1: Ensuring delegated subnet '${PG_SUBNET_NAME}'..."

existing_subnet=$(az network vnet subnet show \
  --resource-group "${AKS_MC_RG}" \
  --vnet-name "${AKS_VNET_NAME}" \
  --name "${PG_SUBNET_NAME}" \
  --query "id" \
  --output tsv 2>/dev/null || true)

if [[ -n "${existing_subnet}" ]]; then
  echo "  [SKIP] Subnet '${PG_SUBNET_NAME}' already exists."
  PG_SUBNET_ID="${existing_subnet}"
else
  echo "  Creating subnet '${PG_SUBNET_NAME}' (${PG_SUBNET_PREFIX})..."
  PG_SUBNET_ID="$(az network vnet subnet create \
    --resource-group "${AKS_MC_RG}" \
    --vnet-name "${AKS_VNET_NAME}" \
    --name "${PG_SUBNET_NAME}" \
    --address-prefixes "${PG_SUBNET_PREFIX}" \
    --delegations Microsoft.DBforPostgreSQL/flexibleServers \
    --query "id" \
    --output tsv)"
  echo "  [OK] Subnet created: ${PG_SUBNET_ID}"
fi

# ---------------------------------------------------------------------------
# 2. Private DNS zone
# ---------------------------------------------------------------------------
echo ""
echo "Step 2: Ensuring Private DNS zone '${PG_DNS_ZONE}'..."

existing_zone=$(az network private-dns zone show \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${PG_DNS_ZONE}" \
  --query "id" \
  --output tsv 2>/dev/null || true)

if [[ -n "${existing_zone}" ]]; then
  echo "  [SKIP] DNS zone '${PG_DNS_ZONE}' already exists."
else
  echo "  Creating Private DNS zone '${PG_DNS_ZONE}'..."
  az network private-dns zone create \
    --resource-group "${RESOURCE_GROUP}" \
    --name "${PG_DNS_ZONE}" \
    --output none
  echo "  [OK] DNS zone created."
fi

# ---------------------------------------------------------------------------
# 3. VNet link (DNS zone → AKS VNet)
# ---------------------------------------------------------------------------
echo ""
echo "Step 3: Ensuring VNet DNS link '${PG_DNS_LINK_NAME}'..."

existing_link=$(az network private-dns link vnet show \
  --resource-group "${RESOURCE_GROUP}" \
  --zone-name "${PG_DNS_ZONE}" \
  --name "${PG_DNS_LINK_NAME}" \
  --query "id" \
  --output tsv 2>/dev/null || true)

if [[ -n "${existing_link}" ]]; then
  echo "  [SKIP] VNet link '${PG_DNS_LINK_NAME}' already exists."
else
  echo "  Linking DNS zone to VNet '${AKS_VNET_NAME}'..."
  az network private-dns link vnet create \
    --resource-group "${RESOURCE_GROUP}" \
    --zone-name "${PG_DNS_ZONE}" \
    --name "${PG_DNS_LINK_NAME}" \
    --virtual-network "${AKS_VNET_ID}" \
    --registration-enabled false \
    --output none
  echo "  [OK] VNet link created."
fi

# ---------------------------------------------------------------------------
# 4. Flexible Server
# ---------------------------------------------------------------------------
echo ""
echo "Step 4: Ensuring Flexible Server '${PG_SERVER_NAME}'..."

existing_server=$(az postgres flexible-server show \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${PG_SERVER_NAME}" \
  --query "state" \
  --output tsv 2>/dev/null || true)

if [[ -n "${existing_server}" ]]; then
  echo "  [SKIP] Server '${PG_SERVER_NAME}' already exists (state: ${existing_server})."
else
  echo "  Generating admin password (not echoed; will be stored in K8s secret)..."
  # Use openssl to generate a strong random password (no special shell chars)
  PG_ADMIN_PASSWORD="$(openssl rand -base64 48 | tr -d '+=/' | head -c 48)"

  echo "  Creating Flexible Server '${PG_SERVER_NAME}' — this takes ~5 minutes..."
  # --zonal-resiliency requires AZ capacity; skip when HA is Disabled (staging).
  ZONAL_FLAGS=()
  if [[ "${PG_HA_MODE}" != "Disabled" ]]; then
    ZONAL_FLAGS+=(--zonal-resiliency Enabled)
  fi
  az postgres flexible-server create \
    --resource-group "${RESOURCE_GROUP}" \
    --name "${PG_SERVER_NAME}" \
    --location "${LOCATION}" \
    --admin-user "${PG_ADMIN_USER}" \
    --admin-password "${PG_ADMIN_PASSWORD}" \
    --version "${PG_VERSION}" \
    --sku-name "${PG_SKU}" \
    --tier "GeneralPurpose" \
    --storage-size "${PG_STORAGE_GB}" \
    "${ZONAL_FLAGS[@]}" \
    --backup-retention "${PG_BACKUP_DAYS}" \
    --subnet "${PG_SUBNET_ID}" \
    --private-dns-zone "${PG_DNS_ZONE_ID}" \
    --yes \
    --output none 2>&1 | grep -v -i "password" 
  echo "  [OK] Server created."

  # Store admin password directly into K8s secret — never written to disk or stdout
  echo "  Storing credentials in K8s secret 'agentweaver-postgres'..."
  PG_CONNECTION_STRING="Host=${PG_FQDN};Port=5432;Database=${PG_DB_NAME};Username=${PG_ADMIN_USER};Password=${PG_ADMIN_PASSWORD};Ssl Mode=Require;Trust Server Certificate=false"

  kubectl create secret generic agentweaver-postgres \
    --namespace "${NAMESPACE}" \
    --from-literal=host="${PG_FQDN}" \
    --from-literal=port="5432" \
    --from-literal=database="${PG_DB_NAME}" \
    --from-literal=username="${PG_ADMIN_USER}" \
    --from-literal=password="${PG_ADMIN_PASSWORD}" \
    --from-literal=connectionstring="${PG_CONNECTION_STRING}" \
    --save-config \
    --dry-run=client \
    -o yaml | kubectl apply -f -

  # Unset password from environment — stored only in K8s secret
  unset PG_ADMIN_PASSWORD PG_CONNECTION_STRING

  echo "  [OK] K8s secret 'agentweaver-postgres' created/updated."
  echo "       Admin password is stored in: secret/agentweaver-postgres, key 'password'"
  echo "       Connection string in:         secret/agentweaver-postgres, key 'connectionstring'"
fi

# ---------------------------------------------------------------------------
# 5. Application database
# ---------------------------------------------------------------------------
echo ""
echo "Step 5: Ensuring database '${PG_DB_NAME}'..."

existing_db=$(az postgres flexible-server db show \
  --resource-group "${RESOURCE_GROUP}" \
  --server-name "${PG_SERVER_NAME}" \
  --name "${PG_DB_NAME}" \
  --query "name" \
  --output tsv 2>/dev/null || true)

if [[ -n "${existing_db}" ]]; then
  echo "  [SKIP] Database '${PG_DB_NAME}' already exists."
else
  echo "  Creating database '${PG_DB_NAME}'..."
  az postgres flexible-server db create \
    --resource-group "${RESOURCE_GROUP}" \
    --server-name "${PG_SERVER_NAME}" \
    --name "${PG_DB_NAME}" \
    --output none
  echo "  [OK] Database '${PG_DB_NAME}' created."
fi

# ---------------------------------------------------------------------------
# 6. Add private DNS A record for the server
# ---------------------------------------------------------------------------
echo ""
echo "Step 6: Ensuring private DNS A record for '${PG_SERVER_NAME}'..."

# Get the server's private IP from the delegated subnet
PRIVATE_IP=$(az postgres flexible-server show \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${PG_SERVER_NAME}" \
  --query "network.delegatedSubnetResourceId" \
  --output tsv 2>/dev/null || true)

# Add an A record in the private DNS zone so pods resolve via FQDN
existing_a=$(az network private-dns record-set a show \
  --resource-group "${RESOURCE_GROUP}" \
  --zone-name "${PG_DNS_ZONE}" \
  --name "${PG_SERVER_NAME}" \
  --query "name" --output tsv 2>/dev/null || true)

if [[ -n "${existing_a}" ]]; then
  echo "  [SKIP] A record '${PG_SERVER_NAME}' already exists in ${PG_DNS_ZONE}."
else
  # Retrieve the private IP from the DNS zone A records auto-created by Azure
  PRIVATE_IP=$(az network private-dns record-set a list \
    --resource-group "${RESOURCE_GROUP}" \
    --zone-name "${PG_DNS_ZONE}" \
    --query "[?name!='@'].aRecords[0].ipv4Address | [0]" \
    --output tsv 2>/dev/null || true)
  if [[ -n "${PRIVATE_IP}" ]]; then
    echo "  Adding A record '${PG_SERVER_NAME}' → ${PRIVATE_IP}..."
    az network private-dns record-set a add-record \
      --resource-group "${RESOURCE_GROUP}" \
      --zone-name "${PG_DNS_ZONE}" \
      --record-set-name "${PG_SERVER_NAME}" \
      --ipv4-address "${PRIVATE_IP}" \
      --output none
    echo "  [OK] A record added: ${PG_SERVER_NAME}.${PG_DNS_ZONE} → ${PRIVATE_IP}"
  else
    echo "  WARNING: Could not determine private IP. Add A record manually once server is Ready."
  fi
fi

# ---------------------------------------------------------------------------
# 7. Verify server is Ready
# ---------------------------------------------------------------------------
echo ""
echo "Step 7: Verifying server state..."

server_state=$(az postgres flexible-server show \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${PG_SERVER_NAME}" \
  --query "state" \
  --output tsv 2>/dev/null || echo "unknown")

echo "  Server state: ${server_state}"
if [[ "${server_state}" != "Ready" ]]; then
  echo "  WARNING: Server is not in Ready state. It may still be provisioning."
fi

# ---------------------------------------------------------------------------
# 8. K8s secret refresh (if server already existed, ensure secret is current)
# ---------------------------------------------------------------------------
existing_secret=$(kubectl get secret agentweaver-postgres \
  --namespace "${NAMESPACE}" \
  --ignore-not-found \
  --output jsonpath='{.metadata.name}' 2>/dev/null || true)

if [[ -z "${existing_secret}" ]]; then
  echo ""
  echo "WARNING: K8s secret 'agentweaver-postgres' was not found."
  echo "  The server already existed — if you need to (re)create the secret,"
  echo "  retrieve the password from Key Vault or Azure Portal and run:"
  echo "    kubectl create secret generic agentweaver-postgres \\"
  echo "      --namespace ${NAMESPACE} \\"
  echo "      --from-literal=host=${PG_FQDN} \\"
  echo "      --from-literal=port=5432 \\"
  echo "      --from-literal=database=${PG_DB_NAME} \\"
  echo "      --from-literal=username=${PG_ADMIN_USER} \\"
  echo "      --from-literal=password=<password> \\"
  echo "      --from-literal=connectionstring=\"Host=${PG_FQDN};Port=5432;Database=${PG_DB_NAME};Username=${PG_ADMIN_USER};Password=<password>;Ssl Mode=Require\""
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
echo ""
echo "==================================================="
echo " POSTGRES PROVISIONING COMPLETE"
echo "==================================================="
echo ""
echo "  Server:          ${PG_SERVER_NAME}"
echo "  FQDN:            ${PG_FQDN}"
echo "  Database:        ${PG_DB_NAME}"
echo "  SKU:             ${PG_SKU} (GeneralPurpose)"
echo "  HA:              ${PG_HA_MODE}"
echo "  Networking:      Private (VNet integration, no public endpoint)"
echo "  Subnet:          ${PG_SUBNET_NAME} (${PG_SUBNET_PREFIX}) in ${AKS_VNET_NAME}"
echo "  DNS zone:        ${PG_DNS_ZONE} → linked to ${AKS_VNET_NAME}"
echo ""
echo "  K8s secret:      secret/agentweaver-postgres (namespace: ${NAMESPACE})"
echo "  Connection key:  ConnectionStrings:MemoryDb (current code) / ConnectionStrings:Postgres (Tank's rename)"
echo ""
echo "  Next steps:"
echo "    1. Run scripts/aks/30-deploy.sh to apply the NetworkPolicy egress allowance."
echo "    2. Tank: set Database__Provider=Postgres + ConnectionStrings__MemoryDb to switch the app."
echo "    3. Follow-up (HARDENING): promote to passwordless Entra auth:"
echo "       - az postgres flexible-server ad-admin set ... (set UAMI as Entra admin)"
echo "       - Grant DB role to agentweaver-api-identity principal (pgaadauth)"
echo "       - Remove password from K8s secret; configure Npgsql PeriodicPasswordProvider"
echo "         to fetch Entra access tokens (audience: https://ossrdbms-aad.database.windows.net)"
echo ""
