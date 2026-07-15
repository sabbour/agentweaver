# 00-variables.ps1 -- Shared environment variables for all Agentweaver AKS scripts.
# Keep in sync with 00-variables.sh (bash equivalent, sourced with
# `source scripts/aks/00-variables.sh` from Git Bash/WSL workflows).
#
# Usage (dot-source so variables persist in the caller's session):
#   . .\scripts\aks\00-variables.ps1
#
# Every variable below is set BOTH as a PowerShell variable ($Foo) and as a
# process-level environment variable ($env:FOO), so this script behaves the
# same whether it's dot-sourced interactively or consumed by the other .ps1
# scripts in this directory (which read $env:FOO to match the bash scripts'
# `export FOO` convention).

# -- Azure resource parameters ------------------------------------------------
if (-not $env:RESOURCE_GROUP) { $env:RESOURCE_GROUP = "agentweaver-rg" }
if (-not $env:CLUSTER_NAME)   { $env:CLUSTER_NAME = "agentweaver-aks-2" }
if (-not $env:ACR_NAME)       { $env:ACR_NAME = "agentweaverregistry" }
if (-not $env:LOCATION)       { $env:LOCATION = "westus2" }

# -- Key Vault + workload identity parameters ---------------------------------
if (-not $env:KEYVAULT_NAME) { $env:KEYVAULT_NAME = "agentweaver-kv" }
if (-not $env:AGENTHOST_KEYVAULT_URI) {
  $env:AGENTHOST_KEYVAULT_URI = "https://$($env:KEYVAULT_NAME).vault.azure.net/"
}

# Resolve the current Azure AD tenant live from the logged-in az context if not
# already set. This used to be done ad-hoc by hand before each deploy; automating
# it here means 30-deploy.ps1 always has a correct TENANT_ID for the workload
# identity federation annotations without anyone remembering to export it first.
if (-not $env:TENANT_ID) {
  if (Get-Command az -ErrorAction SilentlyContinue) {
    $tenantId = (az account show --query tenantId --output tsv 2>$null)
    if ($LASTEXITCODE -eq 0 -and $tenantId) { $env:TENANT_ID = $tenantId.Trim() }
  }
}
if (-not $env:TENANT_ID) { $env:TENANT_ID = "" }

# Resolve the workload identity's client ID live from the resource group if not
# already set. NOTE: the real identity name is 'agentweaver-api-identity' (not
# 'agentweaver-identity' -- that was a naming mismatch discovered during manual
# deploys; keep this name in sync with the actual `az identity` resource).
if (-not $env:IDENTITY_CLIENT_ID) {
  if (Get-Command az -ErrorAction SilentlyContinue) {
    $identityClientId = (az identity list `
      --resource-group $env:RESOURCE_GROUP `
      --query "[?name=='agentweaver-api-identity'].clientId | [0]" `
      --output tsv 2>$null)
    if ($LASTEXITCODE -eq 0 -and $identityClientId) { $env:IDENTITY_CLIENT_ID = $identityClientId.Trim() }
  }
}
if (-not $env:IDENTITY_CLIENT_ID) { $env:IDENTITY_CLIENT_ID = "" }

if (-not $env:APPINSIGHTS_WORKSPACE_ID) {
  if (Get-Command az -ErrorAction SilentlyContinue) {
    $workspaceId = (az monitor log-analytics workspace show `
      --resource-group $env:RESOURCE_GROUP `
      --workspace-name agentweaver-logs `
      --query customerId `
      --output tsv 2>$null)
    if ($LASTEXITCODE -eq 0 -and $workspaceId) { $env:APPINSIGHTS_WORKSPACE_ID = $workspaceId.Trim() }
  }
  if (-not $env:APPINSIGHTS_WORKSPACE_ID) { $env:APPINSIGHTS_WORKSPACE_ID = "" }
}

# -- Kubernetes parameters ----------------------------------------------------
if (-not $env:NAMESPACE)      { $env:NAMESPACE = "agentweaver" }
if (-not $env:KATA_POOL_NAME) { $env:KATA_POOL_NAME = "katapool" }
if (-not $env:APP_POOL_NAME)  { $env:APP_POOL_NAME = "apppool" }

# -- Image parameters ---------------------------------------------------------
# Validate tag: must not be 'latest', and must be either a git short SHA
# (7-40 hex chars) or a semver tag prefixed with 'v' (e.g. v1.2.3).
function Test-AgentweaverImageTag {
  param(
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$Name
  )
  if ($Tag -eq "latest" -or $Tag -eq "latest-release") {
    Write-Error "ERROR: $Name must be immutable; do not use '$Tag'."
    return $false
  }
  # Accept: short SHA (hex, 7-40 chars) OR vMAJOR.MINOR.PATCH[-prerelease][+build]
  if ($Tag -match '^[0-9a-f]{7,40}$' -or $Tag -match '^v[0-9]+\.[0-9]+\.[0-9]') {
    return $true
  }
  Write-Error "ERROR: $Name='$Tag' is not a valid tag (expected git SHA or vX.Y.Z semver)."
  return $false
}

if (-not $env:IMAGE_TAG) {
  $VariablesDir = $PSScriptRoot
  $RepoRoot = (Resolve-Path (Join-Path $VariablesDir "..\..")).Path
  $imageTag = $null
  # Prefer VERSION file — it reflects the intentional release semver
  $versionFile = Join-Path $RepoRoot "VERSION"
  if (Test-Path $versionFile) {
    $imageTag = "v" + ((Get-Content $versionFile -Raw) -replace '\s', '')
  }
  # Fallback: git short SHA when no VERSION file exists
  if (-not $imageTag -and (Get-Command git -ErrorAction SilentlyContinue)) {
    $shortSha = (git -C $RepoRoot rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $shortSha) { $imageTag = $shortSha.Trim() }
  }
  if (-not $imageTag) {
    Write-Error "ERROR: IMAGE_TAG is not set and no VERSION file or git context found."
    return
  }
  $env:IMAGE_TAG = $imageTag
}

if (-not (Test-AgentweaverImageTag -Tag $env:IMAGE_TAG -Name "IMAGE_TAG")) { return }
if ($env:AGENTHOST_IMAGE_TAG) {
  if (-not (Test-AgentweaverImageTag -Tag $env:AGENTHOST_IMAGE_TAG -Name "AGENTHOST_IMAGE_TAG")) { return }
}
$env:ACR_LOGIN_SERVER = "$($env:ACR_NAME).azurecr.io"

# AgentHost (pod-per-run) image tag. Defaults to the unified IMAGE_TAG so the
# agentweaver-agent-host image/template/warmpool track the same build unless
# explicitly overridden.
if (-not $env:AGENTHOST_IMAGE_TAG) { $env:AGENTHOST_IMAGE_TAG = $env:IMAGE_TAG }

# -- PowerShell-variable mirrors (do not override) ----------------------------
# These make the values convenient to reference as $Foo in addition to $env:FOO,
# matching how the bash scripts export shell variables that are also visible
# as environment variables to child processes.
$RESOURCE_GROUP            = $env:RESOURCE_GROUP
$CLUSTER_NAME              = $env:CLUSTER_NAME
$ACR_NAME                  = $env:ACR_NAME
$LOCATION                  = $env:LOCATION
$NAMESPACE                 = $env:NAMESPACE
$KATA_POOL_NAME            = $env:KATA_POOL_NAME
$APP_POOL_NAME             = $env:APP_POOL_NAME
$IMAGE_TAG                 = $env:IMAGE_TAG
$AGENTHOST_IMAGE_TAG       = $env:AGENTHOST_IMAGE_TAG
$ACR_LOGIN_SERVER          = $env:ACR_LOGIN_SERVER
$KEYVAULT_NAME             = $env:KEYVAULT_NAME
$AGENTHOST_KEYVAULT_URI    = $env:AGENTHOST_KEYVAULT_URI
$TENANT_ID                 = $env:TENANT_ID
$IDENTITY_CLIENT_ID        = $env:IDENTITY_CLIENT_ID
$APPINSIGHTS_WORKSPACE_ID  = $env:APPINSIGHTS_WORKSPACE_ID

# -- Display summary ----------------------------------------------------------
Write-Host ""
Write-Host "=== Agentweaver AKS variables ==="
Write-Host "  Resource Group:  $($env:RESOURCE_GROUP)"
Write-Host "  Cluster:         $($env:CLUSTER_NAME)"
Write-Host "  ACR:             $($env:ACR_LOGIN_SERVER)"
Write-Host "  Location:        $($env:LOCATION)"
Write-Host "  Namespace:       $($env:NAMESPACE)"
Write-Host "  Kata pool:       $($env:KATA_POOL_NAME)"
Write-Host "  App pool:        $($env:APP_POOL_NAME)"
Write-Host "  Image tag:       $($env:IMAGE_TAG)"
Write-Host "  AgentHost tag:   $($env:AGENTHOST_IMAGE_TAG)"
Write-Host "  Key Vault:       $($env:KEYVAULT_NAME)"
Write-Host "  AgentHost KV:    $($env:AGENTHOST_KEYVAULT_URI)"
Write-Host "  Tenant ID:       $(if ($env:TENANT_ID) { $env:TENANT_ID } else { '<not set>' })"
Write-Host "  Identity client: $(if ($env:IDENTITY_CLIENT_ID) { $env:IDENTITY_CLIENT_ID } else { '<not set>' })"
Write-Host "  AppInsights workspace: $(if ($env:APPINSIGHTS_WORKSPACE_ID) { $env:APPINSIGHTS_WORKSPACE_ID } else { '<not set>' })"
