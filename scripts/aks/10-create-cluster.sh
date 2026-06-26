#!/usr/bin/env bash
# 10-create-cluster.sh -- Provision ACR + AKS cluster for Agentweaver.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${SCRIPT_DIR}/00-variables.sh"

SANDBOX_CONTROLLER_VERSION="${SANDBOX_CONTROLLER_VERSION:-v0.4.6}"
SANDBOX_CONTROLLER_MANIFEST_URL="${SANDBOX_CONTROLLER_MANIFEST_URL:-https://github.com/kubernetes-sigs/agent-sandbox/releases/download/${SANDBOX_CONTROLLER_VERSION}/release.yaml}"

echo ""
echo "=== Agentweaver AKS cluster provisioning ==="
echo ""

echo "Installing/upgrading aks-preview extension..."
az extension add --upgrade --name aks-preview

echo "Creating resource group '${RESOURCE_GROUP}' in ${LOCATION}..."
az group create \
  --name "${RESOURCE_GROUP}" \
  --location "${LOCATION}" \
  --output table

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

echo ""
echo "Fetching kubeconfig..."
az aks get-credentials \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${CLUSTER_NAME}" \
  --overwrite-existing

echo ""
echo "Installing agent-sandbox CRDs/controller (${SANDBOX_CONTROLLER_VERSION})..."
kubectl apply -f "${SANDBOX_CONTROLLER_MANIFEST_URL}"
kubectl wait --for=condition=Established crd/sandboxclaims.extensions.agents.x-k8s.io --timeout=180s
kubectl wait --for=condition=Established crd/sandboxtemplates.extensions.agents.x-k8s.io --timeout=180s
kubectl wait --for=condition=Established crd/sandboxwarmpools.extensions.agents.x-k8s.io --timeout=180s

echo ""
echo "--- Node status ---"
kubectl get nodes -o wide

echo ""
echo "--- RuntimeClass check ---"
kubectl get runtimeclass
echo ""
echo "Verify 'kata-vm-isolation' is listed above."

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
