#!/usr/bin/env bash
# 00-variables.sh -- Shared environment variables for all Agentweaver AKS scripts.
#
# Source this file at the start of each script, or export the variables manually
# before running any script in this directory:
#
#   source scripts/aks/00-variables.sh
#
# All values can be overridden by setting the corresponding environment variable
# before sourcing this file, e.g.:
#   export CLUSTER_NAME=my-cluster
#   source scripts/aks/00-variables.sh

# -- Azure resource parameters ------------------------------------------------
RESOURCE_GROUP="${RESOURCE_GROUP:-agentweaver-rg}"
CLUSTER_NAME="${CLUSTER_NAME:-agentweaver-aks}"
# ACR names are globally unique and alphanumeric-only (5-50 chars).
ACR_NAME="${ACR_NAME:-agentweaverregistry}"
LOCATION="${LOCATION:-eastus}"

# -- Key Vault + workload identity parameters ---------------------------------
# Key Vault name must be globally unique (3-24 chars, alphanumeric and hyphens).
KEYVAULT_NAME="${KEYVAULT_NAME:-agentweaver-kv}"
# Azure AD tenant ID — fill in or export before sourcing:
#   export TENANT_ID=$(az account show --query tenantId -o tsv)
TENANT_ID="${TENANT_ID:-}"
# Derived after running 15-setup-identity.sh; can be overridden manually:
#   export IDENTITY_CLIENT_ID=$(az identity show --name agentweaver-api-identity \
#     --resource-group $RESOURCE_GROUP --query clientId -o tsv)
IDENTITY_CLIENT_ID="${IDENTITY_CLIENT_ID:-}"

# -- Kubernetes parameters ----------------------------------------------------
NAMESPACE="${NAMESPACE:-agentweaver}"

# -- Image parameters ---------------------------------------------------------
# Override IMAGE_TAG to deploy a specific image version.
IMAGE_TAG="${IMAGE_TAG:-latest}"
ACR_LOGIN_SERVER="${ACR_NAME}.azurecr.io"

# -- Derived values (do not override) -----------------------------------------
export RESOURCE_GROUP
export CLUSTER_NAME
export ACR_NAME
export LOCATION
export NAMESPACE
export IMAGE_TAG
export ACR_LOGIN_SERVER
export KEYVAULT_NAME
export TENANT_ID
export IDENTITY_CLIENT_ID

# -- Display summary ----------------------------------------------------------
echo "=== Agentweaver AKS variables ==="
echo "  Resource Group:  ${RESOURCE_GROUP}"
echo "  Cluster:         ${CLUSTER_NAME}"
echo "  ACR:             ${ACR_LOGIN_SERVER}"
echo "  Location:        ${LOCATION}"
echo "  Namespace:       ${NAMESPACE}"
echo "  Image tag:       ${IMAGE_TAG}"
