# 16-provision-oauth-signing-key.ps1 -- PowerShell port of 16-provision-oauth-signing-key.sh — keep in sync.
#
# Provisions the MCP OAuth signing key and internal API key as Key Vault
# secrets. Existing non-empty secrets are preserved.

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

function New-RsaPrivateKeyPem {
  $rsa = [System.Security.Cryptography.RSA]::Create(2048)
  try {
    if (-not ($rsa.PSObject.Methods.Name -contains "ExportPkcs8PrivateKey")) {
      throw "Generating a PKCS#8 PEM key requires PowerShell 7+ or a .NET runtime that supports RSA.ExportPkcs8PrivateKey()."
    }

    $der = $rsa.ExportPkcs8PrivateKey()
    $base64 = [Convert]::ToBase64String($der)
    $lines = for ($offset = 0; $offset -lt $base64.Length; $offset += 64) {
      $base64.Substring($offset, [Math]::Min(64, $base64.Length - $offset))
    }
    return (@("-----BEGIN PRIVATE KEY-----") + $lines + @("-----END PRIVATE KEY-----", "")) -join [Environment]::NewLine
  } finally {
    $rsa.Dispose()
  }
}

function New-RandomHex {
  param([Parameter(Mandatory)][int]$ByteCount)
  $bytes = New-Object byte[] $ByteCount
  $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
  try {
    $random.GetBytes($bytes)
  } finally {
    $random.Dispose()
  }
  return -join ($bytes | ForEach-Object { $_.ToString("x2") })
}

$secretName = "mcp-oauth-signing-key"
Write-Host ""
Write-Host "=== MCP OAuth signing key provisioning ==="
Write-Host "  Key Vault:   $($env:KEYVAULT_NAME)"
Write-Host "  Secret name: $secretName"
Write-Host ""

$existingValue = (az keyvault secret show `
  --vault-name $env:KEYVAULT_NAME `
  --name $secretName `
  --query value `
  --output tsv 2>$null | Out-String).Trim()
if ($LASTEXITCODE -eq 0 -and $existingValue) {
  Write-Host "  [SKIP] Secret '$secretName' already exists in Key Vault '$($env:KEYVAULT_NAME)'."
  Write-Host "         To rotate, delete the secret version and re-run this script."
} else {
  Write-Host "  Generating RSA-2048 private key..."
  $scratchDir = if ($env:AGENTWEAVER_TMP_DIR) { $env:AGENTWEAVER_TMP_DIR } else { Join-Path $RepoRoot ".agentweaver\tmp" }
  New-Item -ItemType Directory -Force -Path $scratchDir | Out-Null
  $keyFile = Join-Path $scratchDir "mcp-oauth-signing-key-$PID.pem"

  try {
    [IO.File]::WriteAllText($keyFile, (New-RsaPrivateKeyPem), [Text.Encoding]::ASCII)
    Write-Host "  Storing private key as Key Vault secret '$secretName'..."
    az keyvault secret set `
      --vault-name $env:KEYVAULT_NAME `
      --name $secretName `
      --file $keyFile `
      --content-type application/x-pem-file `
      --output none
    Assert-LastExitCode "Storing OAuth signing key"
    Write-Host ""
    Write-Host "  [OK] Secret '$secretName' created successfully."
  } finally {
    Remove-Item -Force -ErrorAction SilentlyContinue $keyFile
  }
}

$apiKeySecretName = "mcp-api-key"
Write-Host ""
Write-Host "=== Internal API key provisioning ==="
Write-Host "  Key Vault:   $($env:KEYVAULT_NAME)"
Write-Host "  Secret name: $apiKeySecretName"
Write-Host ""

$existingApiKey = (az keyvault secret show `
  --vault-name $env:KEYVAULT_NAME `
  --name $apiKeySecretName `
  --query value `
  --output tsv 2>$null | Out-String).Trim()
if ($LASTEXITCODE -eq 0 -and $existingApiKey) {
  Write-Host "  [SKIP] Secret '$apiKeySecretName' already exists in Key Vault '$($env:KEYVAULT_NAME)'."
} else {
  Write-Host "  Generating 32-byte random hex key..."
  $generatedApiKey = New-RandomHex -ByteCount 32
  az keyvault secret set `
    --vault-name $env:KEYVAULT_NAME `
    --name $apiKeySecretName `
    --value $generatedApiKey `
    --content-type text/plain `
    --output none
  Assert-LastExitCode "Storing internal API key"
  Write-Host "  [OK] Secret '$apiKeySecretName' created successfully."
}

Write-Host ""
Write-Host "  Next steps:"
Write-Host "    1. Run scripts/aks/30-deploy.ps1 to deploy the updated manifests."
Write-Host "    2. Verify: kubectl get secret agentweaver-secrets -n agentweaver -o jsonpath='{.data.mcp-oauth-signing-key}'"
Write-Host "       Expected PEM header: -----BEGIN PRIVATE KEY-----"
Write-Host ""
Write-Host "  Key rotation:"
Write-Host "    Delete the current version in Key Vault and re-run this script."
Write-Host "    The CSI driver polls every 2 min (rotation-poll-interval) and will"
Write-Host "    pick up the new version automatically; no pod restart is required"
Write-Host "    as long as the app re-reads Auth__OAuth__SigningKey on each token mint."
