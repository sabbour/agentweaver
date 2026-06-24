#!/usr/bin/env bash
# 10-create-cluster.sh -- Provision ACR + AKS cluster for Agentweaver.
#
# Creates: resource group, ACR, AKS cluster with:
#   - App Routing add-on (Istio variant) + Gateway API
#   - Kata VM isolation workload runtime
#   - Azure CNI Overlay networking with Cilium dataplane (NetworkPolicy enforcement)
#   - AzureLinux node OS
#   - ACR attachment (no imagePullSecret required)
#
# Requires: Azure CLI 2.80.0+ (`az upgrade` if older), kubectl.
# Run from the repo root or any directory — no relative paths used.
#
# Usage:
#   source scripts/aks/00-variables.sh
#   bash scripts/aks/10-create-cluster.sh
#
# IMPORTANT: This script provisions live Azure resources and WILL INCUR COSTS.
# Do NOT run in CI without explicit intent.

set -euo pipefail

# Load shared variables (allow running the script directly without pre-sourcing)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=00-variables.sh
source "${SCRIPT_DIR}/00-variables.sh"

echo ""
echo "=== Agentweaver AKS cluster provisioning ==="
echo ""

# -- Step 1: aks-preview extension --------------------------------------------
# --enable-app-routing-istio, --enable-gateway-api, and --enable-default-domain
# require the aks-preview extension. Some flags may also require feature
# registration:
#
#   az feature register --namespace Microsoft.ContainerService \
#       --name AKSAppRoutingGatewayAPI
#   az feature register --namespace Microsoft.ContainerService \
#       --name AKS-KataVMIsolation
#   az provider register --namespace Microsoft.ContainerService
#
#   # Poll until Registered:
#   az feature show --namespace Microsoft.ContainerService \
#       --name AKSAppRoutingGatewayAPI --query properties.state -o tsv
#
# Cilium dataplane is GA for Azure CNI Overlay since AKS 1.28; no feature
# registration required.
#
echo "Installing/upgrading aks-preview extension..."
az extension add --upgrade --name aks-preview

# -- Step 2: Resource group ---------------------------------------------------
echo "Creating resource group '${RESOURCE_GROUP}' in ${LOCATION}..."
az group create \
  --name "${RESOURCE_GROUP}" \
  --location "${LOCATION}" \
  --output table

# -- Step 3: ACR --------------------------------------------------------------
echo ""
echo "Creating ACR '${ACR_NAME}'..."
az acr create \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${ACR_NAME}" \
  --sku Standard \
  --admin-enabled false \
  --output table

ACR_ID=$(az acr show \
  --name "${ACR_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --query id \
  --output tsv)

echo "  ACR resource ID: ${ACR_ID}"

# -- Step 4: AKS cluster ------------------------------------------------------
# Key flags:
#   --enable-app-routing        : Enable Application Routing add-on
#   --enable-app-routing-addon-istio : Use Istio variant (approuting-istio GatewayClass)
#                                  Note: this enables Istio for the GATEWAY only — no
#                                  service mesh sidecars or ambient mode on workload pods.
#   --network-plugin azure      : Required for CNI overlay
#   --network-plugin-mode overlay: Azure CNI with overlay networking (scales better)
#   --network-dataplane cilium  : Cilium replaces kube-proxy + Azure NPM.
#                                  Required for NetworkPolicy enforcement (default-deny,
#                                  egress allow-lists, etc.)
#   --enable-acns               : Advanced Container Networking Services. Required to
#                                  unlock the full Cilium feature set, including
#                                  CiliumNetworkPolicy FQDN-based egress filtering
#                                  (toFQDNs) for sandbox egress control, and advanced
#                                  network observability. Must be combined with
#                                  --network-dataplane cilium.
#   --workload-runtime KataVmIsolation : Exposes kata-vm-isolation RuntimeClass
#   --os-sku AzureLinux         : Required for Kata VM isolation
#   --attach-acr                : Grants AcrPull to the cluster's managed identity
echo ""
echo "Creating AKS cluster '${CLUSTER_NAME}' (~10-15 minutes)..."
az aks create \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${CLUSTER_NAME}" \
  --location "${LOCATION}" \
  --enable-app-routing \
  --enable-app-routing-addon-istio \
  --network-plugin azure \
  --network-plugin-mode overlay \
  --network-dataplane cilium \
  --enable-acns \
  --node-vm-size Standard_D4s_v5 \
  --node-count 2 \
  --attach-acr "${ACR_NAME}" \
  --workload-runtime KataVmIsolation \
  --os-sku AzureLinux \
  --generate-ssh-keys \
  --output table

# -- Step 5: Kubeconfig -------------------------------------------------------
echo ""
echo "Fetching kubeconfig..."
az aks get-credentials \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${CLUSTER_NAME}" \
  --overwrite-existing

# -- Step 6: Verification -----------------------------------------------------
echo ""
echo "--- Node status ---"
kubectl get nodes -o wide

echo ""
echo "--- RuntimeClass check ---"
kubectl get runtimeclass
echo ""
echo "Verify 'kata-vm-isolation' is listed above."
echo "If missing, check that --workload-runtime KataVmIsolation was applied."

echo ""
echo "==================================================="
echo " CLUSTER READY"
echo "==================================================="
echo ""
echo "  Resource Group: ${RESOURCE_GROUP}"
echo "  Cluster:        ${CLUSTER_NAME}"
echo "  ACR:            ${ACR_LOGIN_SERVER}"
echo ""
echo "  Next step:"
echo "    bash scripts/aks/20-build-push-images.sh"
echo ""
echo "Export for subsequent steps:"
echo "  export RESOURCE_GROUP=${RESOURCE_GROUP}"
echo "  export CLUSTER_NAME=${CLUSTER_NAME}"
echo "  export ACR_NAME=${ACR_NAME}"
