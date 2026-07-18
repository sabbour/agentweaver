# 10-create-cluster.ps1 -- PowerShell port of 10-create-cluster.sh — keep in sync.
#
# Provisions the Agentweaver resource group, ACR, AKS cluster, user node pools,
# and the agent-sandbox controller CRDs.

[CmdletBinding()]
param()

$ScriptDir = $PSScriptRoot
. (Join-Path $ScriptDir "00-variables.ps1")

$SandboxControllerVersion = if ($env:SANDBOX_CONTROLLER_VERSION) { $env:SANDBOX_CONTROLLER_VERSION } else { "v0.5.0" }
$SandboxControllerManifestUrl = if ($env:SANDBOX_CONTROLLER_MANIFEST_URL) {
  $env:SANDBOX_CONTROLLER_MANIFEST_URL
} else {
  "https://github.com/kubernetes-sigs/agent-sandbox/releases/download/$SandboxControllerVersion/manifest.yaml"
}

function Assert-LastExitCode {
  param([Parameter(Mandatory)][string]$Operation)
  if ($LASTEXITCODE -ne 0) {
    throw "$Operation failed (exit $LASTEXITCODE)."
  }
}

Write-Host ""
Write-Host "=== Agentweaver AKS cluster provisioning ==="
Write-Host ""

Write-Host "Installing/upgrading aks-preview extension..."
az extension add --upgrade --name aks-preview
Assert-LastExitCode "Installing aks-preview extension"

if ((az group exists --name $env:RESOURCE_GROUP | Out-String).Trim() -eq "true") {
  Write-Host "  [SKIP] Resource group '$($env:RESOURCE_GROUP)' already exists."
} else {
  Write-Host "Creating resource group '$($env:RESOURCE_GROUP)' in $($env:LOCATION)..."
  az group create --name $env:RESOURCE_GROUP --location $env:LOCATION --output table
  Assert-LastExitCode "Creating resource group '$($env:RESOURCE_GROUP)'"
}

az acr show --name $env:ACR_NAME --resource-group $env:RESOURCE_GROUP *> $null
if ($LASTEXITCODE -eq 0) {
  Write-Host "  [SKIP] ACR '$($env:ACR_NAME)' already exists."
} else {
  Write-Host ""
  Write-Host "Creating ACR '$($env:ACR_NAME)'..."
  az acr create `
    --resource-group $env:RESOURCE_GROUP `
    --name $env:ACR_NAME `
    --sku Standard `
    --admin-enabled false `
    --output table
  Assert-LastExitCode "Creating ACR '$($env:ACR_NAME)'"
}

$AcrId = (az acr show `
  --name $env:ACR_NAME `
  --resource-group $env:RESOURCE_GROUP `
  --query id `
  --output tsv | Out-String).Trim()
Assert-LastExitCode "Reading ACR '$($env:ACR_NAME)'"
Write-Host "  ACR resource ID: $AcrId"

az aks show --name $env:CLUSTER_NAME --resource-group $env:RESOURCE_GROUP *> $null
if ($LASTEXITCODE -eq 0) {
  Write-Host ""
  Write-Host "  [SKIP] AKS cluster '$($env:CLUSTER_NAME)' already exists."
} else {
  Write-Host ""
  Write-Host "Creating AKS cluster '$($env:CLUSTER_NAME)' (~10-15 minutes)..."
  az aks create `
    --resource-group $env:RESOURCE_GROUP `
    --name $env:CLUSTER_NAME `
    --location $env:LOCATION `
    --network-plugin azure `
    --network-plugin-mode overlay `
    --network-dataplane cilium `
    --enable-acns `
    --os-sku AzureLinux `
    --node-vm-size Standard_D4s_v3 `
    --node-count 2 `
    --enable-cluster-autoscaler `
    --min-count 1 `
    --max-count 3 `
    --nodepool-taints CriticalAddonsOnly=true:NoSchedule `
    --enable-app-routing-istio `
    --enable-gateway-api `
    --enable-default-domain `
    --enable-addons azure-keyvault-secrets-provider `
    --enable-oidc-issuer `
    --enable-workload-identity `
    --attach-acr $AcrId `
    --generate-ssh-keys `
    --output table
  Assert-LastExitCode "Creating AKS cluster '$($env:CLUSTER_NAME)'"
}

Write-Host ""
Write-Host "Fetching kubeconfig..."
az aks get-credentials `
  --resource-group $env:RESOURCE_GROUP `
  --name $env:CLUSTER_NAME `
  --overwrite-existing
Assert-LastExitCode "Fetching AKS credentials"

az aks nodepool show `
  --cluster-name $env:CLUSTER_NAME `
  --resource-group $env:RESOURCE_GROUP `
  --name $env:APP_POOL_NAME *> $null
if ($LASTEXITCODE -eq 0) {
  Write-Host "  [SKIP] Node pool '$($env:APP_POOL_NAME)' already exists."
} else {
  Write-Host ""
  Write-Host "Adding app user pool '$($env:APP_POOL_NAME)' (cluster-autoscaler 1–5 nodes)..."
  az aks nodepool add `
    --resource-group $env:RESOURCE_GROUP `
    --cluster-name $env:CLUSTER_NAME `
    --name $env:APP_POOL_NAME `
    --mode User `
    --os-sku AzureLinux `
    --node-vm-size Standard_D4s_v3 `
    --enable-cluster-autoscaler `
    --min-count 1 `
    --max-count 5 `
    --ssh-access disabled `
    --output table
  Assert-LastExitCode "Adding node pool '$($env:APP_POOL_NAME)'"
}

az aks nodepool show `
  --cluster-name $env:CLUSTER_NAME `
  --resource-group $env:RESOURCE_GROUP `
  --name $env:KATA_POOL_NAME *> $null
if ($LASTEXITCODE -eq 0) {
  Write-Host "  [SKIP] Node pool '$($env:KATA_POOL_NAME)' already exists."
} else {
  Write-Host ""
  Write-Host "Adding dedicated Kata user pool '$($env:KATA_POOL_NAME)' (cluster-autoscaler 1–5 nodes)..."
  az aks nodepool add `
    --resource-group $env:RESOURCE_GROUP `
    --cluster-name $env:CLUSTER_NAME `
    --name $env:KATA_POOL_NAME `
    --mode User `
    --os-sku AzureLinux `
    --workload-runtime KataVmIsolation `
    --node-vm-size Standard_D4s_v3 `
    --enable-cluster-autoscaler `
    --min-count 1 `
    --max-count 5 `
    --node-taints sandbox=kata:NoSchedule `
    --labels agentweaver.io/kata=true `
    --ssh-access disabled `
    --output table
  Assert-LastExitCode "Adding node pool '$($env:KATA_POOL_NAME)'"
}

$SandboxExtensionsUrl = "https://github.com/kubernetes-sigs/agent-sandbox/releases/download/$SandboxControllerVersion/extensions.yaml"
kubectl get crd sandboxclaims.extensions.agents.x-k8s.io *> $null
if ($LASTEXITCODE -eq 0) {
  Write-Host "  [SKIP] Agent-sandbox CRDs already installed."
} else {
  Write-Host ""
  Write-Host "Installing agent-sandbox CRDs/controller ($SandboxControllerVersion)..."
  kubectl apply -f $SandboxControllerManifestUrl
  Assert-LastExitCode "Installing agent-sandbox controller"
  kubectl apply -f $SandboxExtensionsUrl
  Assert-LastExitCode "Installing agent-sandbox extensions"
  kubectl wait --for=condition=Established crd/sandboxclaims.extensions.agents.x-k8s.io --timeout=180s
  Assert-LastExitCode "Waiting for SandboxClaim CRD"
  kubectl wait --for=condition=Established crd/sandboxtemplates.extensions.agents.x-k8s.io --timeout=180s
  Assert-LastExitCode "Waiting for SandboxTemplate CRD"
  kubectl wait --for=condition=Established crd/sandboxwarmpools.extensions.agents.x-k8s.io --timeout=180s
  Assert-LastExitCode "Waiting for SandboxWarmPool CRD"
}

Write-Host ""
Write-Host "--- Node status ---"
kubectl get nodes -o wide
Assert-LastExitCode "Listing nodes"

Write-Host ""
Write-Host "--- RuntimeClass check ---"
kubectl get runtimeclass
Assert-LastExitCode "Listing RuntimeClasses"
Write-Host ""
Write-Host "Verify 'kata-vm-isolation' (or 'kata-mshv-vm-isolation') is listed above."

Write-Host ""
Write-Host "==================================================="
Write-Host " CLUSTER READY"
Write-Host "==================================================="
Write-Host ""
Write-Host "  Resource Group: $($env:RESOURCE_GROUP)"
Write-Host "  Cluster:        $($env:CLUSTER_NAME)"
Write-Host "  ACR:            $($env:ACR_LOGIN_SERVER)"
Write-Host ""
Write-Host "  Next step:"
Write-Host "    .\scripts\aks\15-setup-identity.ps1"
Write-Host ""
Write-Host "Export for subsequent steps:"
Write-Host "  `$env:RESOURCE_GROUP='$($env:RESOURCE_GROUP)'"
Write-Host "  `$env:CLUSTER_NAME='$($env:CLUSTER_NAME)'"
Write-Host "  `$env:ACR_NAME='$($env:ACR_NAME)'"
