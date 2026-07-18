# gen-a2a-mtls-certs.ps1 -- PowerShell port of gen-a2a-mtls-certs.sh — keep in sync.
#
# Generates the workload-bound A2A mTLS CA, AgentHost server certificate, and
# worker client certificate. Existing complete secret sets are preserved unless
# -Force is supplied.

[CmdletBinding()]
param(
  [switch]$Force
)

$ScriptDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..\..")).Path
. (Join-Path $ScriptDir "00-variables.ps1")

function Assert-LastExitCode {
  param([Parameter(Mandatory)][string]$Operation)
  if ($LASTEXITCODE -ne 0) {
    throw "$Operation failed (exit $LASTEXITCODE)."
  }
}

function Test-SecretExists {
  param([Parameter(Mandatory)][string]$Name)
  kubectl get secret $Name --namespace $env:NAMESPACE *> $null
  return $LASTEXITCODE -eq 0
}

function ConvertTo-Pem {
  param(
    [Parameter(Mandatory)][byte[]]$Bytes,
    [Parameter(Mandatory)][string]$Label
  )

  $base64 = [Convert]::ToBase64String($Bytes)
  $lines = for ($offset = 0; $offset -lt $base64.Length; $offset += 64) {
    $base64.Substring($offset, [Math]::Min(64, $base64.Length - $offset))
  }
  return (@("-----BEGIN $Label-----") + $lines + @("-----END $Label-----", "")) -join [Environment]::NewLine
}

function New-RandomSerialNumber {
  $bytes = New-Object byte[] 16
  $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
  try {
    $random.GetBytes($bytes)
    return $bytes
  } finally {
    $random.Dispose()
  }
}

function Write-TextFile {
  param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$Content
  )
  [IO.File]::WriteAllText($Path, $Content, [Text.Encoding]::ASCII)
}

function Apply-A2ASecret {
  param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Type,
    [Parameter(Mandatory)][string[]]$FileArguments
  )

  if ($Force -and (Test-SecretExists $Name)) {
    kubectl delete secret $Name --namespace $env:NAMESPACE
    Assert-LastExitCode "Deleting A2A secret '$Name'"
  }

  $yaml = & kubectl create secret generic $Name `
    --namespace $env:NAMESPACE `
    "--type=$Type" `
    @FileArguments `
    --dry-run=client `
    -o yaml
  Assert-LastExitCode "Rendering A2A secret '$Name'"
  $yaml | kubectl apply -f -
  Assert-LastExitCode "Applying A2A secret '$Name'"
}

if (-not ([System.Security.Cryptography.RSA]::Create().PSObject.Methods.Name -contains "ExportPkcs8PrivateKey")) {
  throw "Generating A2A mTLS PEM keys requires PowerShell 7+ or a .NET runtime that supports RSA.ExportPkcs8PrivateKey()."
}
if (-not ("System.Security.Cryptography.X509Certificates.CertificateRequest" -as [type])) {
  throw "Generating A2A mTLS certificates requires a .NET runtime that supports CertificateRequest."
}

$namespace = if ($env:NAMESPACE) { $env:NAMESPACE } else { "agentweaver" }
$env:NAMESPACE = $namespace
$scratchRoot = if ($env:AGENTWEAVER_TMP_DIR) { $env:AGENTWEAVER_TMP_DIR } else { Join-Path $RepoRoot ".agentweaver\tmp" }
$workDir = Join-Path $scratchRoot "a2a-mtls-$PID"
New-Item -ItemType Directory -Force -Path $workDir | Out-Null

try {
  Write-Host ""
  Write-Host "=== A2A mTLS certificate generation (spec-018 H1) ==="
  Write-Host "  Namespace:   $namespace"
  Write-Host "  Work dir:    $workDir"
  Write-Host "  Force regen: $Force"
  Write-Host ""

  $existing = @()
  foreach ($name in @("agentweaver-a2a-ca", "agentweaver-a2a-server-tls", "agentweaver-a2a-client-tls")) {
    if (Test-SecretExists $name) { $existing += $name }
  }
  if (-not $Force -and $existing.Count -eq 3) {
    Write-Host "[OK] All three A2A mTLS secrets already exist — skipping generation."
    Write-Host "     Pass -Force to regenerate."
    exit 0
  }
  if (-not $Force -and $existing.Count -gt 0) {
    throw "WARNING: Partial secrets found: $($existing -join ' '). Run with -Force to regenerate all three consistently."
  }

  Write-Host "Generating internal A2A CA..."
  $caKey = [System.Security.Cryptography.RSA]::Create(4096)
  $caRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    "CN=agentweaver-a2a-ca, O=agentweaver",
    $caKey,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
  $caRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
  $caRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign,
    $true))
  $caCertificate = $caRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(730))
  Write-TextFile -Path (Join-Path $workDir "ca.key") -Content (ConvertTo-Pem -Bytes $caKey.ExportPkcs8PrivateKey() -Label "PRIVATE KEY")
  Write-TextFile -Path (Join-Path $workDir "ca.crt") -Content (ConvertTo-Pem -Bytes $caCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert) -Label "CERTIFICATE")
  Write-Host "  CA certificate generated."

  Write-Host "Generating AgentHost server certificate..."
  $serverKey = [System.Security.Cryptography.RSA]::Create(2048)
  $serverRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    "CN=agentweaver-agenthost, O=agentweaver",
    $serverKey,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
  $serverRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment,
    $true))
  $serverEku = [System.Security.Cryptography.OidCollection]::new()
  [void]$serverEku.Add([System.Security.Cryptography.Oid]::new("1.3.6.1.5.5.7.3.1"))
  $serverRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($serverEku, $false))
  $serverSan = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
  $serverSan.AddDnsName("agentweaver-agenthost")
  $serverSan.AddDnsName("agentweaver-agent-host.agentweaver.svc.cluster.local")
  $serverRequest.CertificateExtensions.Add($serverSan.Build())
  $serverCertificate = $serverRequest.Create($caCertificate, [DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(365), (New-RandomSerialNumber))
  Write-TextFile -Path (Join-Path $workDir "server.key") -Content (ConvertTo-Pem -Bytes $serverKey.ExportPkcs8PrivateKey() -Label "PRIVATE KEY")
  Write-TextFile -Path (Join-Path $workDir "server.crt") -Content (ConvertTo-Pem -Bytes $serverCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert) -Label "CERTIFICATE")
  Write-Host "  Server certificate generated."

  Write-Host "Generating worker client certificate..."
  $clientKey = [System.Security.Cryptography.RSA]::Create(2048)
  $clientRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    "CN=agentweaver-worker, O=agentweaver",
    $clientKey,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
  $clientRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
    $true))
  $clientEku = [System.Security.Cryptography.OidCollection]::new()
  [void]$clientEku.Add([System.Security.Cryptography.Oid]::new("1.3.6.1.5.5.7.3.2"))
  $clientRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($clientEku, $false))
  $clientCertificate = $clientRequest.Create($caCertificate, [DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(365), (New-RandomSerialNumber))
  Write-TextFile -Path (Join-Path $workDir "client.key") -Content (ConvertTo-Pem -Bytes $clientKey.ExportPkcs8PrivateKey() -Label "PRIVATE KEY")
  Write-TextFile -Path (Join-Path $workDir "client.crt") -Content (ConvertTo-Pem -Bytes $clientCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert) -Label "CERTIFICATE")
  Write-Host "  Client certificate generated."

  Write-Host ""
  Write-Host "Applying K8s Secrets..."
  Apply-A2ASecret -Name "agentweaver-a2a-ca" -Type "Opaque" -FileArguments @("--from-file=ca.crt=$(Join-Path $workDir 'ca.crt')")
  Write-Host "  [applied] agentweaver-a2a-ca"
  Apply-A2ASecret -Name "agentweaver-a2a-server-tls" -Type "kubernetes.io/tls" -FileArguments @(
    "--from-file=tls.crt=$(Join-Path $workDir 'server.crt')",
    "--from-file=tls.key=$(Join-Path $workDir 'server.key')",
    "--from-file=ca.crt=$(Join-Path $workDir 'ca.crt')"
  )
  Write-Host "  [applied] agentweaver-a2a-server-tls (tls.crt + tls.key + ca.crt)"
  Apply-A2ASecret -Name "agentweaver-a2a-client-tls" -Type "kubernetes.io/tls" -FileArguments @(
    "--from-file=tls.crt=$(Join-Path $workDir 'client.crt')",
    "--from-file=tls.key=$(Join-Path $workDir 'client.key')",
    "--from-file=ca.crt=$(Join-Path $workDir 'ca.crt')"
  )
  Write-Host "  [applied] agentweaver-a2a-client-tls (tls.crt + tls.key + ca.crt)"

  Write-Host ""
  Write-Host "=== A2A mTLS certificate generation complete ==="
  Write-Host ""
  Write-Host "  Secret agentweaver-a2a-server-tls  → mounted in sandbox pod at /mnt/a2a-tls/"
  Write-Host "  Secret agentweaver-a2a-client-tls  → mounted in api/worker pod at /mnt/a2a-client-tls/"
  Write-Host "  CA cert (ca.crt) included in both mounts for mutual validation."
} finally {
  if ($caCertificate) { $caCertificate.Dispose() }
  if ($serverCertificate) { $serverCertificate.Dispose() }
  if ($clientCertificate) { $clientCertificate.Dispose() }
  if ($caKey) { $caKey.Dispose() }
  if ($serverKey) { $serverKey.Dispose() }
  if ($clientKey) { $clientKey.Dispose() }
  Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $workDir
}
