# 40-verify.ps1 -- PowerShell port of 40-verify.sh — keep in sync.
#
# Verifies the Agentweaver AKS deployment, its ingress, identity resources,
# sandbox resources, and storage prerequisites.

[CmdletBinding()]
param()

$ScriptDir = $PSScriptRoot
. (Join-Path $ScriptDir "00-variables.ps1")

$script:Pass = 0
$script:Fail = 0

function Write-Ok {
  param([Parameter(Mandatory)][string]$Message)
  Write-Host "  [OK]   $Message"
  $script:Pass++
}

function Write-Fail {
  param([Parameter(Mandatory)][string]$Message)
  Write-Host "  [FAIL] $Message"
  $script:Fail++
}

function Write-Info {
  param([Parameter(Mandatory)][string]$Message)
  Write-Host "  [INFO] $Message"
}

function Get-RunningPodCount {
  param([Parameter(Mandatory)][string]$Selector)
  $pods = kubectl get pods `
    --namespace $env:NAMESPACE `
    --selector $Selector `
    --field-selector status.phase=Running `
    --no-headers 2>$null
  if ($LASTEXITCODE -ne 0) { return 0 }
  return @($pods | Where-Object { $_ -and $_.Trim() }).Count
}

function Test-Kubectl {
  param([Parameter(Mandatory)][string[]]$Arguments)
  & kubectl @Arguments *> $null
  return $LASTEXITCODE -eq 0
}

function Get-HttpStatus {
  param(
    [Parameter(Mandatory)][string]$Uri,
    [string]$BearerToken
  )

  $client = [System.Net.Http.HttpClient]::new()
  $client.Timeout = [TimeSpan]::FromSeconds(10)
  try {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Uri)
    if ($BearerToken) {
      $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $BearerToken)
    }
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    try {
      return [int]$response.StatusCode
    } finally {
      $response.Dispose()
    }
  } catch {
    return "000"
  } finally {
    $client.Dispose()
  }
}

function Get-HttpJson {
  param(
    [Parameter(Mandatory)][string]$Uri,
    [Parameter(Mandatory)][string]$BearerToken
  )

  $client = [System.Net.Http.HttpClient]::new()
  $client.Timeout = [TimeSpan]::FromSeconds(10)
  try {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Uri)
    $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $BearerToken)
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    try {
      if (-not $response.IsSuccessStatusCode) { return "[]" }
      return $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    } finally {
      $response.Dispose()
    }
  } catch {
    return "[]"
  } finally {
    $client.Dispose()
  }
}

Write-Host ""
Write-Host "=== Agentweaver AKS deployment verification ==="
Write-Host "  Namespace: $($env:NAMESPACE)"
Write-Host ""

Write-Host "--- Pod status ---"
kubectl get pods --namespace $env:NAMESPACE -o wide
Write-Host ""

$apiRunning = Get-RunningPodCount -Selector "app=agentweaver-api"
$frontendRunning = Get-RunningPodCount -Selector "app=agentweaver-frontend"
$mcpRunning = Get-RunningPodCount -Selector "app=agentweaver-mcp"
$workerRunning = Get-RunningPodCount -Selector "app=agentweaver-worker"
$agentHostWarmRunning = Get-RunningPodCount -Selector "app.kubernetes.io/component=agent-host"

if ($apiRunning -ge 1) { Write-Ok "API pod(s) running ($apiRunning)" } else { Write-Fail "No API pods in Running state" }
if ($frontendRunning -ge 1) { Write-Ok "Frontend pod(s) running ($frontendRunning)" } else { Write-Fail "No Frontend pods in Running state" }
if ($mcpRunning -ge 1) { Write-Ok "MCP pod(s) running ($mcpRunning)" } else { Write-Fail "No MCP pods in Running state" }
if ($workerRunning -ge 1) { Write-Ok "Worker pod(s) running ($workerRunning)" } else { Write-Fail "No Worker pods in Running state" }
if ($agentHostWarmRunning -ge 1) { Write-Ok "AgentHost warm-pool pod(s) running ($agentHostWarmRunning)" } else { Write-Fail "No AgentHost warm-pool pods in Running state" }

Write-Host ""
Write-Host "--- Gateway status ---"
kubectl get gateway agentweaver-gateway --namespace $env:NAMESPACE -o wide 2>$null
kubectl get gateway agentweaver-preview-gateway --namespace $env:NAMESPACE -o wide 2>$null
Write-Host ""

$programmed = (kubectl get gateway agentweaver-gateway `
  --namespace $env:NAMESPACE `
  --output "jsonpath={.status.conditions[?(@.type==`"Programmed`")].status}" 2>$null | Out-String).Trim()
$gatewayIp = (kubectl get gateway agentweaver-gateway `
  --namespace $env:NAMESPACE `
  --output "jsonpath={.status.addresses[0].value}" 2>$null | Out-String).Trim()
$previewProgrammed = (kubectl get gateway agentweaver-preview-gateway `
  --namespace $env:NAMESPACE `
  --output "jsonpath={.status.conditions[?(@.type==`"Programmed`")].status}" 2>$null | Out-String).Trim()

if ($programmed -eq "True") { Write-Ok "Gateway Programmed=True" } else { Write-Fail "Gateway not yet Programmed (status=$programmed)" }
if ($gatewayIp) { Write-Ok "Gateway address: $gatewayIp" } else { Write-Fail "Gateway has no address yet" }
if ($previewProgrammed -eq "True") { Write-Ok "Preview Gateway Programmed=True" } else { Write-Fail "Preview Gateway not yet Programmed (status=$previewProgrammed)" }

Write-Host ""
Write-Host "--- HTTPRoute status ---"
kubectl get httproute --namespace $env:NAMESPACE -o wide 2>$null
Write-Host ""

foreach ($route in @("agentweaver-api-route", "agentweaver-frontend-route", "agentweaver-mcp-route")) {
  $accepted = (kubectl get httproute $route `
    --namespace $env:NAMESPACE `
    --output "jsonpath={.status.parents[0].conditions[?(@.type==`"Accepted`")].status}" 2>$null | Out-String).Trim()
  $resolved = (kubectl get httproute $route `
    --namespace $env:NAMESPACE `
    --output "jsonpath={.status.parents[0].conditions[?(@.type==`"ResolvedRefs`")].status}" 2>$null | Out-String).Trim()
  if ($accepted -eq "True" -and $resolved -eq "True") {
    Write-Ok "HTTPRoute ${route}: Accepted=True, ResolvedRefs=True"
  } else {
    Write-Fail "HTTPRoute ${route}: Accepted=$accepted, ResolvedRefs=$resolved"
  }
}

Write-Host ""
$domain = (kubectl get defaultdomaincertificate cert `
  --namespace $env:NAMESPACE `
  --output "jsonpath={.status.domain}" 2>$null | Out-String).Trim()
if ($domain) {
  $ingressHost = "agentweaver." + ($domain -replace "^\*\.", "")
  Write-Info "Ingress host: $ingressHost"
} else {
  Write-Info "Could not derive HOST from DefaultDomainCertificate — skipping HTTP checks"
  $ingressHost = ""
}

if ($ingressHost) {
  Write-Host ""
  Write-Host "--- Authenticated feature validation ---"
  $unauthenticatedProjectsStatus = Get-HttpStatus -Uri "https://$ingressHost/api/projects"
  if ($unauthenticatedProjectsStatus -eq 401) {
    Write-Ok "Unauthenticated /api/projects rejected → HTTP $unauthenticatedProjectsStatus"
  } else {
    Write-Fail "Unauthenticated /api/projects → HTTP $unauthenticatedProjectsStatus (expected 401)"
  }

  $validationToken = if ($env:AGENTWEAVER_VALIDATION_TOKEN) { $env:AGENTWEAVER_VALIDATION_TOKEN } else { $env:GH_TOKEN }
  if (-not $validationToken) {
    Write-Info "Set AGENTWEAVER_VALIDATION_TOKEN or GH_TOKEN to validate signed-in identity plus project memory/decision APIs"
  } else {
    $authStatus = Get-HttpStatus -Uri "https://$ingressHost/api/auth/github" -BearerToken $validationToken
    $projectsStatus = Get-HttpStatus -Uri "https://$ingressHost/api/projects" -BearerToken $validationToken
    if ($authStatus -eq 200) { Write-Ok "Authenticated /api/auth/github → HTTP $authStatus" } else { Write-Fail "Authenticated /api/auth/github → HTTP $authStatus (expected 200)" }
    if ($projectsStatus -eq 200) { Write-Ok "Authenticated /api/projects → HTTP $projectsStatus" } else { Write-Fail "Authenticated /api/projects → HTTP $projectsStatus (expected 200)" }

    try {
      $projects = Get-HttpJson -Uri "https://$ingressHost/api/projects" -BearerToken $validationToken | ConvertFrom-Json
      if ($projects -is [Array]) {
        $project = $projects | Select-Object -First 1
      } elseif ($projects.projects) {
        $project = $projects.projects | Select-Object -First 1
      } elseif ($projects.items) {
        $project = $projects.items | Select-Object -First 1
      }
      $projectId = if ($project.id) { $project.id } else { $project.projectId }
    } catch {
      $projectId = ""
    }

    if ($projectId) {
      foreach ($path in @(
        "/api/projects/$projectId/memory",
        "/api/projects/$projectId/decisions/inbox",
        "/api/projects/$projectId/decisions"
      )) {
        $status = Get-HttpStatus -Uri "https://$ingressHost$path" -BearerToken $validationToken
        if ($status -eq 200) {
          Write-Ok "Authenticated $path → HTTP $status"
        } else {
          Write-Fail "Authenticated $path → HTTP $status (expected 200)"
        }
      }
    } else {
      Write-Info "Authenticated account has no project id to validate memory/decision APIs"
    }
  }
}

Write-Host ""
Write-Host "--- SecretProviderClass sync ---"
foreach ($spc in @("agentweaver-secrets", "agentweaver-user-tokens")) {
  if (Test-Kubectl -Arguments @("get", "secretproviderclass", $spc, "--namespace", $env:NAMESPACE)) {
    Write-Ok "SecretProviderClass $spc exists"
  } else {
    Write-Fail "SecretProviderClass $spc missing"
  }
}
$spcStatuses = kubectl get secretproviderclasspodstatus --namespace $env:NAMESPACE --no-headers 2>$null
$spcStatusCount = if ($LASTEXITCODE -eq 0) { @($spcStatuses | Where-Object { $_ -and $_.Trim() }).Count } else { 0 }
if ($spcStatusCount -ge 1) { Write-Ok "SecretProviderClassPodStatus objects present ($spcStatusCount)" } else { Write-Fail "No SecretProviderClassPodStatus objects found" }
Write-Info "agentweaver-user-tokens is installation-only; run-scoped agentweaver-user-token-* SPCs appear only while AgentHost pods are running"

Write-Host ""
Write-Host "--- API RBAC ---"
if ((Test-Kubectl -Arguments @("get", "role", "agentweaver-api-sandbox", "--namespace", $env:NAMESPACE)) -and
    (Test-Kubectl -Arguments @("get", "rolebinding", "agentweaver-api-sandbox", "--namespace", $env:NAMESPACE))) {
  Write-Ok "API sandbox Role and RoleBinding exist"
} else {
  Write-Fail "API sandbox Role/RoleBinding missing"
}

$apiServiceAccount = "system:serviceaccount:$($env:NAMESPACE):agentweaver-api"
$canCreate = $true
foreach ($resource in @(
  "sandboxclaims.extensions.agents.x-k8s.io",
  "sandboxtemplates.extensions.agents.x-k8s.io",
  "sandboxwarmpools.extensions.agents.x-k8s.io",
  "secretproviderclasses.secrets-store.csi.x-k8s.io",
  "pods/exec"
)) {
  if (-not (Test-Kubectl -Arguments @("auth", "can-i", "create", $resource, "--as=$apiServiceAccount", "--namespace", $env:NAMESPACE))) {
    $canCreate = $false
  }
}
if ($canCreate) {
  Write-Ok "API ServiceAccount can create SandboxClaims, run-scoped templates/pools/SPCs, and pods/exec"
} else {
  Write-Fail "API ServiceAccount lacks required sandbox or run-scoped SPC permissions"
}

Write-Host ""
Write-Host "--- Sandbox CRDs/resources ---"
if (Test-Kubectl -Arguments @("get", "runtimeclass", "kata-vm-isolation")) { Write-Ok "kata-vm-isolation RuntimeClass present" } else { Write-Fail "kata-vm-isolation RuntimeClass missing" }
if (Test-Kubectl -Arguments @("get", "sandboxtemplate", "agentweaver-agent-host", "--namespace", $env:NAMESPACE)) { Write-Ok "SandboxTemplate agentweaver-agent-host exists" } else { Write-Fail "SandboxTemplate agentweaver-agent-host missing" }
if (Test-Kubectl -Arguments @("get", "sandboxwarmpool", "agentweaver-agent-host", "--namespace", $env:NAMESPACE)) { Write-Ok "SandboxWarmPool agentweaver-agent-host exists" } else { Write-Fail "SandboxWarmPool agentweaver-agent-host missing" }

$legacyTemplate = Test-Kubectl -Arguments @("get", "sandboxtemplate", "agentweaver-sandbox", "--namespace", $env:NAMESPACE)
$legacyWarmPool = Test-Kubectl -Arguments @("get", "sandboxwarmpool", "agentweaver-sandbox", "--namespace", $env:NAMESPACE)
if ($legacyTemplate -or $legacyWarmPool) {
  Write-Fail "Legacy agentweaver-sandbox template/warm pool still exists; remove it before verifying"
} else {
  Write-Ok "Legacy agentweaver-sandbox template/warm pool absent"
}

Write-Host ""
Write-Host "--- Storage ---"
if (Test-Kubectl -Arguments @("get", "storageclass", "azurefile-csi-premium-uid1000")) { Write-Ok "Workspace StorageClass exists" } else { Write-Fail "Workspace StorageClass missing" }
if (Test-Kubectl -Arguments @("get", "pvc", "agentweaver-workspace", "--namespace", $env:NAMESPACE)) { Write-Ok "Workspace PVC exists" } else { Write-Fail "Workspace PVC missing" }

Write-Host ""
Write-Host "==================================================="
Write-Host " VERIFICATION SUMMARY: $($script:Pass) passed, $($script:Fail) failed"
Write-Host "==================================================="
if ($script:Fail -eq 0) {
  Write-Host " ALL CHECKS PASSED"
} else {
  Write-Host " SOME CHECKS FAILED — see output above"
}
Write-Host ""

if ($script:Fail -ne 0) { exit 1 }
