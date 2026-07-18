# 15-provision-monitoring.ps1 -- PowerShell port of 15-provision-monitoring.sh — keep in sync.
#
# Provisions Application Insights, its Log Analytics workspace, and AKS Managed
# Prometheus.

[CmdletBinding()]
param()

$ScriptDir = $PSScriptRoot
. (Join-Path $ScriptDir "00-variables.ps1")

function Assert-LastExitCode {
  param([Parameter(Mandatory)][string]$Operation)
  if ($LASTEXITCODE -ne 0) {
    throw "$Operation failed (exit $LASTEXITCODE)."
  }
}

Write-Host ""
Write-Host "=== Provision Monitoring ==="
Write-Host "  Resource Group: $($env:RESOURCE_GROUP)"
Write-Host "  Location:       $($env:LOCATION)"
Write-Host "  Key Vault:      $($env:KEYVAULT_NAME)"
Write-Host "  Cluster:        $($env:CLUSTER_NAME)"
Write-Host ""

Write-Host "Ensuring Log Analytics workspace 'agentweaver-logs'..."
az monitor log-analytics workspace show `
  --resource-group $env:RESOURCE_GROUP `
  --workspace-name agentweaver-logs *> $null
if ($LASTEXITCODE -eq 0) {
  Write-Host "  [OK] Log Analytics workspace already exists."
} else {
  az monitor log-analytics workspace create `
    --resource-group $env:RESOURCE_GROUP `
    --workspace-name agentweaver-logs `
    --location $env:LOCATION
  Assert-LastExitCode "Creating Log Analytics workspace"
  Write-Host "  [created] Log Analytics workspace."
}

$workspaceResourceId = (az monitor log-analytics workspace show `
  --resource-group $env:RESOURCE_GROUP `
  --workspace-name agentweaver-logs `
  --query id `
  --output tsv | Out-String).Trim()
Assert-LastExitCode "Reading Log Analytics workspace resource ID"

$env:APPINSIGHTS_WORKSPACE_ID = (az monitor log-analytics workspace show `
  --resource-group $env:RESOURCE_GROUP `
  --workspace-name agentweaver-logs `
  --query customerId `
  --output tsv | Out-String).Trim()
Assert-LastExitCode "Reading Log Analytics workspace ID"

Write-Host "Granting Log Analytics Reader on 'agentweaver-logs' to workload identity..."
if (-not $env:IDENTITY_CLIENT_ID) {
  Write-Host "  [WARN] IDENTITY_CLIENT_ID is not set; skipping workspace role assignment."
} else {
  $identityObjectId = (az identity list `
    --resource-group $env:RESOURCE_GROUP `
    --query "[?clientId=='$($env:IDENTITY_CLIENT_ID)'].principalId | [0]" `
    --output tsv | Out-String).Trim()
  Assert-LastExitCode "Resolving workload identity"

  if (-not $identityObjectId) {
    Write-Host "  [WARN] No managed identity found for IDENTITY_CLIENT_ID='$($env:IDENTITY_CLIENT_ID)'; skipping workspace role assignment."
  } else {
    $assignmentJson = az role assignment list `
      --assignee-object-id $identityObjectId `
      --scope $workspaceResourceId `
      --role "Log Analytics Reader" `
      --output json
    Assert-LastExitCode "Checking Log Analytics Reader assignment"
    $existingAssignments = @($assignmentJson | ConvertFrom-Json).Count

    if ($existingAssignments -eq 0) {
      az role assignment create `
        --role "Log Analytics Reader" `
        --assignee-object-id $identityObjectId `
        --assignee-principal-type ServicePrincipal `
        --scope $workspaceResourceId `
        --output none
      Assert-LastExitCode "Granting Log Analytics Reader"
      Write-Host "  [granted] Log Analytics Reader on workspace."
    } else {
      Write-Host "  [OK] Log Analytics Reader already assigned."
    }
  }
}

Write-Host "Ensuring Application Insights 'agentweaver-insights'..."
az monitor app-insights component show `
  --app agentweaver-insights `
  --resource-group $env:RESOURCE_GROUP *> $null
if ($LASTEXITCODE -eq 0) {
  Write-Host "  [OK] Application Insights already exists."
} else {
  az monitor app-insights component create `
    --resource-group $env:RESOURCE_GROUP `
    --app agentweaver-insights `
    --location $env:LOCATION `
    --kind web `
    --workspace agentweaver-logs
  Assert-LastExitCode "Creating Application Insights"
  Write-Host "  [created] Application Insights."
}

Write-Host "Storing AppInsights connection string in Key Vault..."
$connectionString = (az monitor app-insights component show `
  --app agentweaver-insights `
  --resource-group $env:RESOURCE_GROUP `
  --query connectionString `
  --output tsv | Out-String).Trim()
Assert-LastExitCode "Reading Application Insights connection string"

az keyvault secret set `
  --vault-name $env:KEYVAULT_NAME `
  --name appinsights-connection-string `
  --value $connectionString `
  --output none
Assert-LastExitCode "Storing AppInsights connection string"
Write-Host "  [stored] appinsights-connection-string in Key Vault."

Write-Host "Enabling AKS Managed Prometheus on cluster '$($env:CLUSTER_NAME)'..."
az aks update `
  --resource-group $env:RESOURCE_GROUP `
  --name $env:CLUSTER_NAME `
  --enable-azure-monitor-metrics
Assert-LastExitCode "Enabling AKS Managed Prometheus"
Write-Host "  [enabled] AKS Managed Prometheus."

Write-Host ""
Write-Host "=== Monitoring provisioning complete ==="
Write-Host "  Application Insights connection string stored as 'appinsights-connection-string' in Key Vault."
Write-Host "  Log Analytics workspace customerId: $($env:APPINSIGHTS_WORKSPACE_ID)"
Write-Host "  AKS Managed Prometheus enabled on cluster '$($env:CLUSTER_NAME)'."
