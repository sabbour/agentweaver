# 15-setup-identity.ps1 -- PowerShell port of 15-setup-identity.sh — keep in sync.
#
# Creates the workload identity, Key Vault, federated credentials, and required
# Key Vault role assignments and secrets.

[CmdletBinding()]
param()

$ScriptDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..\..")).Path
. (Join-Path $ScriptDir "00-variables.ps1")

function Assert-LastExitCode {
  param([Parameter(Mandatory)][string]$Operation)
  if ($LASTEXITCODE -ne 0) {
    throw "$Operation failed (exit $LASTEXITCODE)."
  }
}

function Set-KeyVaultSecretWithRetry {
  param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Value
  )

  $attempt = 1
  $maximumAttempts = 12
  while ($true) {
    $output = (& az keyvault secret set `
      --vault-name $env:KEYVAULT_NAME `
      --name $Name `
      --value $Value `
      --output none 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0) { return }

    if ($output -match "Forbidden|ForbiddenByRbac|not authorized" -and $attempt -lt $maximumAttempts) {
      Write-Host "  [retry $attempt/$maximumAttempts] RBAC role for '$Name' still propagating; waiting 15s..."
      Start-Sleep -Seconds 15
      $attempt++
      continue
    }

    throw $output.Trim()
  }
}

function Resolve-GitHubOAuthFromUserSecrets {
  if ($env:GITHUB_CLIENT_ID -and $env:GITHUB_CLIENT_SECRET) { return }
  if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return }

  $apiProject = Join-Path $RepoRoot "apps\Agentweaver.Api"
  if (-not (Test-Path $apiProject)) { return }

  $secrets = & dotnet user-secrets list --project $apiProject 2>$null
  if ($LASTEXITCODE -ne 0) { return }

  foreach ($line in $secrets) {
    if ($line -match '^\s*Auth:GitHub:ClientId\s*=\s*(?<value>.+?)\s*$' -and -not $env:GITHUB_CLIENT_ID) {
      $env:GITHUB_CLIENT_ID = $matches.value
    }
    if ($line -match '^\s*Auth:GitHub:ClientSecret\s*=\s*(?<value>.+?)\s*$' -and -not $env:GITHUB_CLIENT_SECRET) {
      $env:GITHUB_CLIENT_SECRET = $matches.value
    }
  }
}

function Test-InteractiveConsole {
  try {
    return -not [Console]::IsInputRedirected -and -not [Console]::IsOutputRedirected
  } catch {
    return $false
  }
}

if (-not $env:TENANT_ID) {
  $env:TENANT_ID = (az account show --query tenantId --output tsv | Out-String).Trim()
  Assert-LastExitCode "Reading Azure tenant ID"
}

Resolve-GitHubOAuthFromUserSecrets

$canPrompt = Test-InteractiveConsole
if ($canPrompt -and -not $env:GITHUB_CLIENT_ID) {
  $env:GITHUB_CLIENT_ID = Read-Host "GitHub OAuth client ID"
}
if ($canPrompt -and -not $env:GITHUB_CLIENT_SECRET) {
  $secureClientSecret = Read-Host "GitHub OAuth client secret" -AsSecureString
  $clientSecretBstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureClientSecret)
  try {
    $env:GITHUB_CLIENT_SECRET = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($clientSecretBstr)
  } finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($clientSecretBstr)
  }
}

$missing = @()
if (-not $env:GITHUB_CLIENT_ID) { $missing += "GITHUB_CLIENT_ID" }
if (-not $env:GITHUB_CLIENT_SECRET) { $missing += "GITHUB_CLIENT_SECRET" }
if ($missing.Count -gt 0) {
  Write-Host "ERROR: unable to resolve GitHub OAuth secrets from the environment or local user-secrets." -ForegroundColor Red
  if (-not $canPrompt) {
    Write-Host "       This non-interactive session cannot prompt. Set these variables:" -ForegroundColor Red
  } else {
    Write-Host "       Prompted values were empty. Set these variables:" -ForegroundColor Red
  }
  foreach ($variable in $missing) { Write-Host "  $variable" -ForegroundColor Red }
  exit 1
}

Write-Host ""
Write-Host "=== Step 1: Create user-assigned managed identity ==="
az identity create `
  --name agentweaver-api-identity `
  --resource-group $env:RESOURCE_GROUP `
  --location $env:LOCATION
Assert-LastExitCode "Creating managed identity"

$env:IDENTITY_CLIENT_ID = (az identity show `
  --name agentweaver-api-identity `
  --resource-group $env:RESOURCE_GROUP `
  --query clientId `
  --output tsv | Out-String).Trim()
Assert-LastExitCode "Reading managed identity client ID"

$IdentityObjectId = (az identity show `
  --name agentweaver-api-identity `
  --resource-group $env:RESOURCE_GROUP `
  --query principalId `
  --output tsv | Out-String).Trim()
Assert-LastExitCode "Reading managed identity object ID"

Write-Host "  Identity client ID:  $($env:IDENTITY_CLIENT_ID)"
Write-Host "  Identity object ID:  $IdentityObjectId"

Write-Host ""
Write-Host "=== Step 2: Create Key Vault ==="
az keyvault show --name $env:KEYVAULT_NAME --resource-group $env:RESOURCE_GROUP *> $null
if ($LASTEXITCODE -ne 0) {
  az keyvault create `
    --name $env:KEYVAULT_NAME `
    --resource-group $env:RESOURCE_GROUP `
    --location $env:LOCATION `
    --enable-rbac-authorization
  Assert-LastExitCode "Creating Key Vault '$($env:KEYVAULT_NAME)'"
} else {
  Write-Host "  [OK] Key Vault '$($env:KEYVAULT_NAME)' already exists."
}

$KeyVaultId = (az keyvault show --name $env:KEYVAULT_NAME --query id --output tsv | Out-String).Trim()
Assert-LastExitCode "Reading Key Vault ID"
Write-Host "  Key Vault ID: $KeyVaultId"

Write-Host ""
Write-Host "=== Step 2b: Grant provisioning caller data-plane secret access ==="
$callerObjectId = (az ad signed-in-user show --query id --output tsv 2>$null | Out-String).Trim()
$callerPrincipalType = "User"
if (-not $callerObjectId) {
  $callerAppId = (az account show --query user.name --output tsv 2>$null | Out-String).Trim()
  if ($callerAppId) {
    $callerObjectId = (az ad sp show --id $callerAppId --query id --output tsv 2>$null | Out-String).Trim()
    $callerPrincipalType = "ServicePrincipal"
  }
}
if ($callerObjectId) {
  Write-Host "  Granting 'Key Vault Secrets Officer' to caller $callerObjectId ($callerPrincipalType)..."
  $assignmentOutput = (& az role assignment create `
    --role "Key Vault Secrets Officer" `
    --assignee-object-id $callerObjectId `
    --assignee-principal-type $callerPrincipalType `
    --scope $KeyVaultId 2>&1 | Out-String)
  if ($LASTEXITCODE -ne 0 -and $assignmentOutput -notmatch "already exists") {
    Write-Warning $assignmentOutput.Trim()
  }
} else {
  Write-Host "  [WARN] Could not resolve caller object ID; relying on ambient Key Vault permissions."
}

Write-Host ""
Write-Host "=== Step 3: Store required secrets in Key Vault ==="
Set-KeyVaultSecretWithRetry -Name "github-client-id" -Value $env:GITHUB_CLIENT_ID
Set-KeyVaultSecretWithRetry -Name "github-client-secret" -Value $env:GITHUB_CLIENT_SECRET

Write-Host ""
Write-Host "=== Step 4: Grant Key Vault roles to managed identity ==="
foreach ($role in @("Key Vault Secrets User", "Key Vault Secrets Officer")) {
  $assignmentOutput = (& az role assignment create `
    --role $role `
    --assignee-object-id $IdentityObjectId `
    --assignee-principal-type ServicePrincipal `
    --scope $KeyVaultId 2>&1 | Out-String)
  if ($LASTEXITCODE -ne 0 -and $assignmentOutput -notmatch "already exists") {
    Write-Warning $assignmentOutput.Trim()
  }
}

Write-Host ""
Write-Host "=== Step 5: Enable OIDC issuer + workload identity on cluster ==="
$oidcEnabled = (az aks show `
  --name $env:CLUSTER_NAME `
  --resource-group $env:RESOURCE_GROUP `
  --query oidcIssuerProfile.enabled `
  --output tsv 2>$null | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { $oidcEnabled = "false" }
$workloadIdentityEnabled = (az aks show `
  --name $env:CLUSTER_NAME `
  --resource-group $env:RESOURCE_GROUP `
  --query securityProfile.workloadIdentity.enabled `
  --output tsv 2>$null | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { $workloadIdentityEnabled = "false" }

if ($oidcEnabled -eq "true" -and $workloadIdentityEnabled -eq "true") {
  Write-Host "  [SKIP] OIDC issuer and workload identity already enabled."
} else {
  az aks update `
    --name $env:CLUSTER_NAME `
    --resource-group $env:RESOURCE_GROUP `
    --enable-oidc-issuer `
    --enable-workload-identity
  Assert-LastExitCode "Enabling OIDC issuer and workload identity"
}

$oidcIssuer = (az aks show `
  --name $env:CLUSTER_NAME `
  --resource-group $env:RESOURCE_GROUP `
  --query oidcIssuerProfile.issuerUrl `
  --output tsv | Out-String).Trim()
Assert-LastExitCode "Reading OIDC issuer"
Write-Host "  OIDC issuer: $oidcIssuer"

Write-Host ""
Write-Host "=== Step 6: Create federated credential ==="
az identity federated-credential show `
  --name agentweaver-api-fedcred `
  --identity-name agentweaver-api-identity `
  --resource-group $env:RESOURCE_GROUP *> $null
if ($LASTEXITCODE -ne 0) {
  az identity federated-credential create `
    --name agentweaver-api-fedcred `
    --identity-name agentweaver-api-identity `
    --resource-group $env:RESOURCE_GROUP `
    --issuer $oidcIssuer `
    --subject "system:serviceaccount:$($env:NAMESPACE):agentweaver-api" `
    --audience api://AzureADTokenExchange
  Assert-LastExitCode "Creating API federated credential"
} else {
  Write-Host "  [OK] Federated credential already exists."
}

Write-Host ""
Write-Host "=== Step 7: Create federated credential for agent-host ==="
az identity federated-credential show `
  --name agentweaver-agenthost-fedcred `
  --identity-name agentweaver-api-identity `
  --resource-group $env:RESOURCE_GROUP *> $null
if ($LASTEXITCODE -ne 0) {
  az identity federated-credential create `
    --name agentweaver-agenthost-fedcred `
    --identity-name agentweaver-api-identity `
    --resource-group $env:RESOURCE_GROUP `
    --issuer $oidcIssuer `
    --subject "system:serviceaccount:$($env:NAMESPACE):agentweaver-agent-host" `
    --audience api://AzureADTokenExchange
  Assert-LastExitCode "Creating agent-host federated credential"
} else {
  Write-Host "  [OK] Agent-host federated credential already exists."
}

Write-Host ""
Write-Host "=== Summary ==="
Write-Host "  IDENTITY_CLIENT_ID=$($env:IDENTITY_CLIENT_ID)"
Write-Host "  KEYVAULT_NAME=$($env:KEYVAULT_NAME)"
Write-Host "  TENANT_ID=$($env:TENANT_ID)"
Write-Host ""
Write-Host "Two federated credentials are now configured on agentweaver-api-identity:"
Write-Host "  agentweaver-api-fedcred       → system:serviceaccount:$($env:NAMESPACE):agentweaver-api"
Write-Host "  agentweaver-agenthost-fedcred → system:serviceaccount:$($env:NAMESPACE):agentweaver-agent-host"
Write-Host ""
Write-Host "NOTE: Run scripts/aks/16-provision-oauth-signing-key.ps1 before the first deploy."
