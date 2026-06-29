#!/usr/bin/env bash
# 10-create-cluster.sh -- Provision ACR + AKS cluster for Agentweaver.
#
# Creates: resource group, ACR, AKS cluster with three node pools:
#
#   nodepool1 (System, AzureLinux, autoscaler 1–3)
#     Taint: CriticalAddonsOnly=true:NoSchedule
#     Only kube-system / critical-addon pods schedule here. App workloads
#     are excluded and land on apppool (no taint).
#
#   apppool (User, AzureLinux, autoscaler 1–5, no taint)
#     Receives all application workloads: api, worker, mcp, frontend, jobs.
#     No tolerations needed in app deployment YAMLs.
#
#   katapool (User, AzureLinux, KataVmIsolation, autoscaler 1–5)
#     Taint: sandbox=kata:NoSchedule
#     Label: agentweaver.io/kata=true
#     Dedicated to sandbox/AgentHost pods via SandboxTemplate tolerations.
#
# NOTE: NAP (--node-provisioning-mode Auto) is intentionally NOT used.
#   NAP and cluster-autoscaler are mutually exclusive; Kata VM isolation
#   (katapool) requires cluster-autoscaler on a fixed user pool.
#
# NOTE: If az aks create fails with "feature not registered", register first:
#   az feature register --namespace Microsoft.ContainerService --name AKSAppRoutingGatewayAPI
#   az feature register --namespace Microsoft.ContainerService --name AKS-KataVMIsolation
#   az provider register --namespace Microsoft.ContainerService
#   # Wait until State == Registered:
#   az feature show --namespace Microsoft.ContainerService --name AKSAppRoutingGatewayAPI -o tsv --query properties.state

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
# System pool: CriticalAddonsOnly taint keeps app workloads off nodepool1.
# App workloads (api, worker, mcp, frontend) land on apppool (added below, no taint).
az aks create \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${CLUSTER_NAME}" \
  --location "${LOCATION}" \
  --network-plugin azure \
  --network-plugin-mode overlay \
  --network-dataplane cilium \
  --enable-acns \
  --os-sku AzureLinux \
  --node-vm-size Standard_D4s_v3 \
  --node-count 2 \
  --enable-cluster-autoscaler \
  --min-count 1 \
  --max-count 3 \
  --node-taints CriticalAddonsOnly=true:NoSchedule \
  --enable-app-routing-istio \
  --enable-gateway-api \
  --enable-default-domain \
  --enable-addons azure-keyvault-secrets-provider \
  --enable-oidc-issuer \
  --enable-workload-identity \
  --attach-acr "${ACR_ID}" \
  --generate-ssh-keys \
  --output table

echo ""
echo "Fetching kubeconfig..."
az aks get-credentials \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${CLUSTER_NAME}" \
  --overwrite-existing

echo ""
echo "Adding app user pool '${APP_POOL_NAME}' (cluster-autoscaler 1–5 nodes)..."
# Receives all app workloads (api, worker, mcp, frontend, jobs).
# No taint: pods schedule here without needing any toleration.
az aks nodepool add \
  --resource-group "${RESOURCE_GROUP}" \
  --cluster-name "${CLUSTER_NAME}" \
  --name "${APP_POOL_NAME}" \
  --mode User \
  --os-sku AzureLinux \
  --node-vm-size Standard_D4s_v3 \
  --enable-cluster-autoscaler \
  --min-count 1 \
  --max-count 5 \
  --ssh-access disabled \
  --output table

echo ""
echo "Adding dedicated Kata user pool '${KATA_POOL_NAME}' (cluster-autoscaler 1–5 nodes)..."
# NAP and cluster-autoscaler are mutually exclusive; this cluster uses cluster-autoscaler.
# katapool is the sole Kata-capable pool; sandbox/AgentHost SandboxTemplate pod specs
# target it via toleration (sandbox=kata:NoSchedule) + preferred nodeAffinity
# (agentweaver.io/kata=true). The system pool (nodepool1) is a standard pool.
az aks nodepool add \
  --resource-group "${RESOURCE_GROUP}" \
  --cluster-name "${CLUSTER_NAME}" \
  --name "${KATA_POOL_NAME}" \
  --mode User \
  --os-sku AzureLinux \
  --workload-runtime KataVmIsolation \
  --node-vm-size Standard_D4s_v3 \
  --enable-cluster-autoscaler \
  --min-count 1 \
  --max-count 5 \
  --node-taints sandbox=kata:NoSchedule \
  --labels agentweaver.io/kata=true \
  --ssh-access disabled \
  --output table

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
echo "Verify 'kata-vm-isolation' (or 'kata-mshv-vm-isolation') is listed above."

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
echo "    bash scripts/aks/15-setup-identity.sh"
echo ""
echo "Export for subsequent steps:"
echo "  export RESOURCE_GROUP=${RESOURCE_GROUP}"
echo "  export CLUSTER_NAME=${CLUSTER_NAME}"
echo "  export ACR_NAME=${ACR_NAME}"
