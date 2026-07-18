# 30-deploy.ps1 -- Deploy Agentweaver to AKS.
# Keep in sync with 30-deploy.sh (bash equivalent).

[CmdletBinding()]
param()

$ScriptDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..\..")).Path
. (Join-Path $ScriptDir "00-variables.ps1")

$RenderedDir = Join-Path $ScriptDir ".rendered"

# PowerShell equivalent of bash's `trap 'rm -rf "${RENDERED_DIR}"' EXIT`.
try {

function Invoke-ApplyRendered {
  param([string]$Fname)
  kubectl apply -f (Join-Path $RenderedDir $Fname)
  if ($LASTEXITCODE -ne 0) { throw "kubectl apply failed for $Fname (exit $LASTEXITCODE)" }
  Write-Host "  [applied] $Fname"
}

# Minimal envsubst equivalent: only substitutes the explicit whitelist of
# ${VAR} references passed in $Vars, leaving any other ${...} text (e.g. shell
# syntax inside embedded scripts in the YAML) untouched -- matching envsubst's
# behavior when invoked with an explicit variable list (`envsubst '$A $B ...'`).
function Invoke-EnvSubstWhitelist {
  param([string]$Content, [string[]]$Vars)
  $result = $Content
  foreach ($varName in $Vars) {
    $value = [System.Environment]::GetEnvironmentVariable($varName)
    if ($null -eq $value) { $value = "" }
    $result = $result.Replace("`${$varName}", $value)
  }
  return $result
}

Write-Host ""
Write-Host "=== Agentweaver AKS deployment ==="
Write-Host "  kubectl context: $(kubectl config current-context)"
Write-Host "  Namespace:       $($env:NAMESPACE)"
Write-Host "  ACR:             $($env:ACR_LOGIN_SERVER)"
Write-Host "  Image tag:       $($env:IMAGE_TAG)"
Write-Host ""

# NOTE: bash's 30-deploy.sh requires `envsubst` (from gettext) to render the
# k8s/*.yaml templates. This port never shells out to envsubst -- it uses
# Invoke-EnvSubstWhitelist above instead, so there is no equivalent
# "envsubst not found" prerequisite check here; gettext is not required on
# Windows/PowerShell workflows.

$missing = @()
if (-not $env:IDENTITY_CLIENT_ID) { $missing += "IDENTITY_CLIENT_ID" }
if (-not $env:KEYVAULT_NAME) { $missing += "KEYVAULT_NAME" }
if (-not $env:TENANT_ID) { $missing += "TENANT_ID" }
if ($missing.Count -gt 0) {
  Write-Host "ERROR: The following required variables are not set:" -ForegroundColor Red
  foreach ($v in $missing) { Write-Host "  $v" -ForegroundColor Red }
  exit 1
}

# Derive compound variables from primitives so templates can reference them directly.
if (-not $env:AGENTHOST_KEYVAULT_URI) {
  $env:AGENTHOST_KEYVAULT_URI = "https://$($env:KEYVAULT_NAME).vault.azure.net/"
}

Write-Host "Applying namespace..."
kubectl apply -f (Join-Path $RepoRoot "k8s\namespace.yaml")
if ($LASTEXITCODE -ne 0) { throw "kubectl apply failed for namespace.yaml (exit $LASTEXITCODE)" }

# Provision monitoring if not already done
az monitor app-insights component show --app agentweaver-insights -g $env:RESOURCE_GROUP *> $null
if ($LASTEXITCODE -ne 0) {
  Write-Host ""
  Write-Host "Provisioning monitoring (Application Insights + Managed Prometheus)..."
  & (Join-Path $ScriptDir "15-provision-monitoring.ps1")
  if ($LASTEXITCODE -ne 0) { throw "15-provision-monitoring.ps1 failed (exit $LASTEXITCODE)" }
}

$env:APPINSIGHTS_WORKSPACE_ID = (az monitor log-analytics workspace show `
    --resource-group $env:RESOURCE_GROUP `
    --workspace-name agentweaver-logs `
    --query customerId `
    --output tsv 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $env:APPINSIGHTS_WORKSPACE_ID) { $env:APPINSIGHTS_WORKSPACE_ID = "" }

Write-Host ""
Write-Host "Checking DefaultDomainCertificate 'cert' in namespace '$($env:NAMESPACE)'..."
kubectl get defaultdomaincertificate cert --namespace $env:NAMESPACE *> $null
if ($LASTEXITCODE -eq 0) {
  Write-Host "  [OK] DefaultDomainCertificate 'cert' already exists."
} else {
@"
apiVersion: approuting.kubernetes.azure.com/v1alpha1
kind: DefaultDomainCertificate
metadata:
  name: cert
  namespace: $($env:NAMESPACE)
spec:
  target:
    secret: agentweaver-tls
"@ | kubectl apply -f -
  if ($LASTEXITCODE -ne 0) { throw "kubectl apply failed for inline DefaultDomainCertificate (exit $LASTEXITCODE)" }
}

kubectl wait `
  --for=condition=Available `
  defaultdomaincertificate/cert `
  --namespace $env:NAMESPACE `
  --timeout=300s
if ($LASTEXITCODE -ne 0) { throw "kubectl wait for defaultdomaincertificate/cert failed (exit $LASTEXITCODE)" }

$Domain = (kubectl get defaultdomaincertificate cert `
  --namespace $env:NAMESPACE `
  --output jsonpath='{.status.domain}')
if ($LASTEXITCODE -ne 0) { throw "kubectl get defaultdomaincertificate cert failed (exit $LASTEXITCODE)" }
$env:HOST = "agentweaver.$($Domain -replace '^\*\.', '')"

Write-Host "  Managed domain: $Domain"
Write-Host "  Ingress host:   $($env:HOST)"

# --- Preview gateway cert path (CONFIRMED 2026-06-28 by live spike) ---
# AKS App Routing DefaultDomainCertificate does NOT support nested wildcards.
# The DDC CRD has no spec.hostname field -- it always issues *.{zone} regardless
# of object name or target secret name.  status.domain is always *.{zone}.
# Spike evidence: cert-preview-spike DDC issued CN=*.6a3de4fe60529400010f3fba.
# westus2.staging.aksapp.io (not *.preview.{zone}); secret SAN confirmed same.
#
# Therefore we always take the single-label fallback path and reuse agentweaver-tls.
# Preview hostnames: {token}-preview.{zone}  (e.g. swift-falcon-amber-k7m2...-preview.6a3de4fe...aksapp.io)
# If AKS adds nested DDC support in the future, restore the probe below and update
# PREVIEW_TLS_SECRET to a new cert-preview DDC secret + add the cert-preview DDC object.
Write-Host ""
Write-Host "Setting preview gateway hostname (single-label fallback -- AKS nested DDC not supported)..."
$Zone = ($Domain -replace '^\*\.', '')
$ZoneSuffix = $Zone
$PreviewTlsSecret = "agentweaver-tls"

$env:PREVIEW_HOSTNAME = "*.$ZoneSuffix"
$env:PREVIEW_TLS_SECRET = $PreviewTlsSecret
$env:SANDBOX_PREVIEW_ENABLED = "true"
$env:SANDBOX_PREVIEW_ZONE_SUFFIX = $ZoneSuffix

Write-Host "  Preview hostname:    $($env:PREVIEW_HOSTNAME)"
Write-Host "  Preview TLS secret:  $($env:PREVIEW_TLS_SECRET)"
Write-Host "  ZoneSuffix (API):    $ZoneSuffix"

Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $RenderedDir
New-Item -ItemType Directory -Force -Path $RenderedDir | Out-Null

Write-Host ""
Write-Host "Rendering manifests..."
$SubstVars = @(
  "HOST", "ACR_LOGIN_SERVER", "IMAGE_TAG", "AGENTHOST_IMAGE_TAG", "IDENTITY_CLIENT_ID",
  "KEYVAULT_NAME", "AGENTHOST_KEYVAULT_URI", "TENANT_ID", "PREVIEW_HOSTNAME",
  "PREVIEW_TLS_SECRET", "SANDBOX_PREVIEW_ENABLED", "SANDBOX_PREVIEW_ZONE_SUFFIX",
  "APPINSIGHTS_WORKSPACE_ID"
)
Get-ChildItem -Path (Join-Path $RepoRoot "k8s") -Filter "*.yaml" | ForEach-Object {
  $fname = $_.Name
  $content = Get-Content -Raw -Path $_.FullName
  $rendered = Invoke-EnvSubstWhitelist -Content $content -Vars $SubstVars
  Set-Content -Path (Join-Path $RenderedDir $fname) -Value $rendered -NoNewline
  Write-Host "  rendered: $fname"
}

Write-Host ""
Write-Host "Applying identity, secrets, RBAC, quotas, and PVCs..."
Invoke-ApplyRendered "serviceaccount-api.yaml"
Invoke-ApplyRendered "serviceaccount-agenthost.yaml"
Invoke-ApplyRendered "secret-provider-class.yaml"
Write-Host "  [note] secret-provider-class.yaml is static only: agentweaver-user-tokens contains ghtok-installation; per-run user-token SPCs are created/deleted by the API at AgentHost launch/release."
Invoke-ApplyRendered "rbac-api.yaml"
Invoke-ApplyRendered "quota.yaml"
Invoke-ApplyRendered "storageclass-workspace.yaml"
Invoke-ApplyRendered "pvc-data.yaml"
Invoke-ApplyRendered "pvc-workspace.yaml"

Write-Host ""
Write-Host "Applying network policies and egress allowlists..."
Invoke-ApplyRendered "networkpolicy-default-deny.yaml"
Invoke-ApplyRendered "networkpolicy-mcp.yaml"
Invoke-ApplyRendered "networkpolicy-sandbox.yaml"
# H2 (spec-018): A2A ingress -- worker->agenthost on port 8088 only; no egress change.
Invoke-ApplyRendered "networkpolicy-agenthost.yaml"
Invoke-ApplyRendered "networkpolicy-agenthost-api-egress.yaml"
Invoke-ApplyRendered "networkpolicy-agenthost-egress.yaml"
Invoke-ApplyRendered "cilium-network-policy-sandbox.yaml"
Invoke-ApplyRendered "serviceentry-telemetry.yaml"
# spec-018 P2: Allow API pods to reach PostgreSQL Flexible Server on port 5432.
Invoke-ApplyRendered "networkpolicy-postgres-egress.yaml"
# spec-018 P3: Worker-tier egress policies (DNS, HTTPS, internal, Postgres, OTEL).
Invoke-ApplyRendered "networkpolicy-worker.yaml"

Write-Host ""
Write-Host "Applying services, gateway, and routes..."
# H1 (spec-018): generate A2A mTLS certs (idempotent -- skips if secrets exist).
Write-Host "Ensuring A2A mTLS certificates are present (H1)..."
& (Join-Path $ScriptDir "gen-a2a-mtls-certs.ps1")
if ($LASTEXITCODE -ne 0) { throw "gen-a2a-mtls-certs.ps1 failed (exit $LASTEXITCODE)" }
# H4/H3 (spec-018): AgentHost Kestrel + card-authz config.
Invoke-ApplyRendered "configmap-agenthost.yaml"
Invoke-ApplyRendered "api-service.yaml"
Invoke-ApplyRendered "frontend-service.yaml"
Invoke-ApplyRendered "mcp-service.yaml"
Invoke-ApplyRendered "gateway.yaml"
Invoke-ApplyRendered "gateway-preview.yaml"
Invoke-ApplyRendered "httproute-api.yaml"
Invoke-ApplyRendered "httproute-frontend.yaml"
Invoke-ApplyRendered "mcp-httproute.yaml"

Write-Host ""
Write-Host "Applying AgentHost sandbox template and warm pool (if CRD is available)..."
$hasSandboxCrd = (kubectl api-resources --api-group=extensions.agents.x-k8s.io 2>$null | Select-String -Pattern "SandboxTemplate" -Quiet)
if ($hasSandboxCrd) {
  # spec-018 pod-per-run: AgentHost SandboxTemplate + warm pool. The template MUST be
  # applied before the warm pool (the pool's sandboxTemplateRef points at it), and the
  # warm pool MUST exist before AgentHost claims (which bind via spec.warmPoolRef.name).
  Invoke-ApplyRendered "sandbox-template-agenthost.yaml"
  Invoke-ApplyRendered "sandbox-warmpool-agenthost.yaml"
} else {
  Write-Host "  [SKIP] agent-sandbox CRD not installed -- AgentHost sandbox template skipped."
}

Write-Host ""
Write-Host "Waiting for gateway/agentweaver-gateway to become Programmed (up to 3 min)..."
kubectl wait `
  --for=condition=Programmed `
  gateway/agentweaver-gateway `
  --namespace $env:NAMESPACE `
  --timeout=180s
if ($LASTEXITCODE -ne 0) { throw "kubectl wait for gateway/agentweaver-gateway failed (exit $LASTEXITCODE)" }

Write-Host "Waiting for gateway/agentweaver-preview-gateway to become Programmed (up to 3 min)..."
kubectl wait `
  --for=condition=Programmed `
  gateway/agentweaver-preview-gateway `
  --namespace $env:NAMESPACE `
  --timeout=180s
if ($LASTEXITCODE -ne 0) { throw "kubectl wait for gateway/agentweaver-preview-gateway failed (exit $LASTEXITCODE)" }

$GatewayIp = (kubectl get gateway agentweaver-gateway `
  --namespace $env:NAMESPACE `
  --output jsonpath='{.status.addresses[0].value}')
if ($LASTEXITCODE -ne 0) { throw "kubectl get gateway agentweaver-gateway failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "Applying deployments after workload identity prerequisites are ready..."
Invoke-ApplyRendered "api-deployment.yaml"
Invoke-ApplyRendered "frontend-deployment.yaml"
Invoke-ApplyRendered "mcp-deployment.yaml"

Write-Host ""
# spec-018 P3: Worker Deployment + autoscaling.
# ORDERING NOTE: Apply worker AFTER api-deployment so the agentweaver-api service
# (Agentweaver__ApiBaseUrl target) is already present. The worker init container
# runs EF migrations against Postgres; it will restart until Tank's Postgres migration
# set is merged + the image is rebuilt. This is safe -- web tier (api-deployment) keeps
# serving on SQLite in the meantime.
Write-Host "Applying worker deployment and HPA (spec-018 P3)..."
Invoke-ApplyRendered "worker-deployment.yaml"
Invoke-ApplyRendered "worker-hpa.yaml"

Write-Host ""
Write-Host "Waiting for API deployment rollout..."
kubectl rollout status deployment/agentweaver-api --namespace $env:NAMESPACE --timeout=180s
if ($LASTEXITCODE -ne 0) { throw "API deployment rollout failed (exit $LASTEXITCODE)" }

Write-Host "Waiting for Frontend deployment rollout..."
kubectl rollout status deployment/agentweaver-frontend --namespace $env:NAMESPACE --timeout=120s
if ($LASTEXITCODE -ne 0) { throw "Frontend deployment rollout failed (exit $LASTEXITCODE)" }

Write-Host "Waiting for MCP deployment rollout..."
kubectl rollout status deployment/agentweaver-mcp --namespace $env:NAMESPACE --timeout=120s
if ($LASTEXITCODE -ne 0) { throw "MCP deployment rollout failed (exit $LASTEXITCODE)" }

Write-Host "Waiting for Worker deployment rollout..."
# Worker init container runs EF Postgres migrations -- allow extra time.
# If Tank's Postgres migration set is not yet merged, this will timeout but is non-fatal
# (web tier is healthy; worker will come up once migrations are applied).
kubectl rollout status deployment/agentweaver-worker --namespace $env:NAMESPACE --timeout=300s
if ($LASTEXITCODE -ne 0) {
  Write-Host "  WARNING: Worker rollout did not complete within 300s. Check: kubectl logs -n $($env:NAMESPACE) -l app=agentweaver-worker --all-containers"
}

Write-Host ""
Write-Host "Verifying image provenance (post-deploy safety net for #251)..."
# Confirms the images now actually running in the cluster are provably built
# from ${env:IMAGE_TAG}'s source (no stale retag-forward drift). This must run
# after rollout so pods have already settled on their final image/digest.
#
# BUGFIX: this used to default VERIFY_GIT_REF to IMAGE_TAG (e.g. "v0.9.63").
# IMAGE_TAG is derived from the VERSION file, not from an actual git tag --
# Get-ReleaseRefForTag in _image-functions.ps1/20-build-push-images.sh explicitly
# notes that releases since v0.9.36 deliberately stopped creating a matching git
# tag. Passing a non-existent tag straight to `git rev-parse --verify <ref>^{commit}`
# inside 25-verify-image-provenance.ps1 fails with "fatal: Needed a single
# revision" -- this was reproduced live at the end of a real deploy. HEAD is
# the commit actually checked out for this deploy (the one IMAGE_TAG's VERSION
# bump belongs to), so default to HEAD instead, exactly like
# 25-verify-image-provenance.ps1's own standalone default.
if (-not $env:VERIFY_GIT_REF) { $env:VERIFY_GIT_REF = "HEAD" }
& (Join-Path $ScriptDir "25-verify-image-provenance.ps1")
if ($LASTEXITCODE -ne 0) {
  Write-Host ""
  Write-Host "ERROR: Image provenance verification FAILED -- see output above." -ForegroundColor Red
  Write-Host "  A running pod's image does not provably match $($env:IMAGE_TAG)'s source, or watched" -ForegroundColor Red
  Write-Host "  paths drifted since the image was built. This is the exact #251 stale-retag failure" -ForegroundColor Red
  Write-Host "  mode -- treating the deploy as FAILED rather than silently succeeding." -ForegroundColor Red
  Write-Host "  Re-run scripts/aks/20-build-push-images.ps1 with -ForceRebuild for the affected" -ForegroundColor Red
  Write-Host "  image(s), then redeploy." -ForegroundColor Red
  exit 1
}
Write-Host "  [OK] All running images verified against source ($($env:IMAGE_TAG))."

Write-Host ""
Write-Host "==================================================="
Write-Host " DEPLOYMENT COMPLETE"
Write-Host "==================================================="
Write-Host ""
Write-Host "  Frontend URL:        https://$($env:HOST)/"
Write-Host "  API URL:             https://$($env:HOST)/api/"
Write-Host "  MCP URL:             https://$($env:HOST)/mcp/"
Write-Host "  Gateway IP:          $GatewayIp"
Write-Host ""
Write-Host "  Preview gateway:     $($env:PREVIEW_HOSTNAME) (TLS: $($env:PREVIEW_TLS_SECRET))"
Write-Host "  Preview zone suffix: $ZoneSuffix"
Write-Host "  Sandbox__Preview__Enabled:          true"
Write-Host "  Sandbox__Preview__ZoneSuffix:       $ZoneSuffix"
Write-Host "  Sandbox__Preview__GatewayName:      agentweaver-preview-gateway"
Write-Host "  Sandbox__Preview__GatewayNamespace: agentweaver"
Write-Host ""
Write-Host "  Next step:"
Write-Host "    .\scripts\aks\40-verify.ps1"
Write-Host ""
Write-Host "  To check status:"
Write-Host "    kubectl get gateway,httproute,pod,svc -n $($env:NAMESPACE)"

} finally {
  Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $RenderedDir
}
