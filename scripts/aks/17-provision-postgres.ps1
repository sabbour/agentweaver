# 17-provision-postgres.ps1 -- PowerShell port of 17-provision-postgres.sh — keep in sync.
#
# Provisions Azure Database for PostgreSQL Flexible Server with private VNet
# integration, its private DNS configuration, and the application database.

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

function Get-EnvironmentOrDefault {
  param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Default
  )

  $value = [Environment]::GetEnvironmentVariable($Name)
  if (-not $value) {
    $value = $Default
    [Environment]::SetEnvironmentVariable($Name, $value, "Process")
  }
  return $value
}

function New-PostgresAdminPassword {
  do {
    $bytes = New-Object byte[] 48
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
      $random.GetBytes($bytes)
    } finally {
      $random.Dispose()
    }
    $value = ([Convert]::ToBase64String($bytes) -replace "[+=/]", "")
  } while ($value.Length -lt 48)
  return $value.Substring(0, 48)
}

$pgServerName = Get-EnvironmentOrDefault -Name "PG_SERVER_NAME" -Default "agentweaver-pg"
$pgDatabaseName = Get-EnvironmentOrDefault -Name "PG_DB_NAME" -Default "agentweaver"
$pgAdminUser = Get-EnvironmentOrDefault -Name "PG_ADMIN_USER" -Default "pgadmin"
$pgVersion = Get-EnvironmentOrDefault -Name "PG_VERSION" -Default "16"
$pgSku = Get-EnvironmentOrDefault -Name "PG_SKU" -Default "Standard_D2ds_v4"
$pgStorageGb = Get-EnvironmentOrDefault -Name "PG_STORAGE_GB" -Default "32"
$pgHaMode = Get-EnvironmentOrDefault -Name "PG_HA_MODE" -Default "ZoneRedundant"
$pgBackupDays = Get-EnvironmentOrDefault -Name "PG_BACKUP_DAYS" -Default "7"
$pgSubnetName = Get-EnvironmentOrDefault -Name "PG_SUBNET_NAME" -Default "aks-postgres"
$pgSubnetPrefix = Get-EnvironmentOrDefault -Name "PG_SUBNET_PREFIX" -Default "10.225.0.0/28"
$pgDnsZone = Get-EnvironmentOrDefault -Name "PG_DNS_ZONE" -Default "privatelink.postgres.database.azure.com"
$pgDnsLinkName = Get-EnvironmentOrDefault -Name "PG_DNS_LINK_NAME" -Default "agentweaver-pg-dns-link"

$aksMcResourceGroup = $env:AKS_MC_RG
if (-not $aksMcResourceGroup) {
  $aksMcResourceGroup = (az aks show `
    --resource-group $env:RESOURCE_GROUP `
    --name $env:CLUSTER_NAME `
    --query nodeResourceGroup `
    --output tsv 2>$null | Out-String).Trim()
}
if (-not $aksMcResourceGroup) {
  throw "ERROR: AKS_MC_RG is not set and could not be detected from cluster '$($env:CLUSTER_NAME)'."
}
$env:AKS_MC_RG = $aksMcResourceGroup

$aksVnetName = $env:AKS_VNET_NAME
if (-not $aksVnetName) {
  $aksVnetName = (az network vnet list `
    --resource-group $aksMcResourceGroup `
    --query "[0].name" `
    --output tsv 2>$null | Out-String).Trim()
}
if (-not $aksVnetName) {
  throw "ERROR: AKS_VNET_NAME is not set and no VNet was found in node resource group '$aksMcResourceGroup'."
}
$env:AKS_VNET_NAME = $aksVnetName

$subscriptionId = (az account show --query id --output tsv | Out-String).Trim()
Assert-LastExitCode "Reading Azure subscription ID"
$aksVnetId = "/subscriptions/$subscriptionId/resourceGroups/$aksMcResourceGroup/providers/Microsoft.Network/virtualNetworks/$aksVnetName"
$pgDnsZoneId = "/subscriptions/$subscriptionId/resourceGroups/$($env:RESOURCE_GROUP)/providers/Microsoft.Network/privateDnsZones/$pgDnsZone"
$pgFqdn = "$pgServerName.postgres.database.azure.com"

Write-Host ""
Write-Host "=== Agentweaver PostgreSQL Flexible Server provisioning ==="
Write-Host "  Resource Group:  $($env:RESOURCE_GROUP)"
Write-Host "  Location:        $($env:LOCATION)"
Write-Host "  Server name:     $pgServerName"
Write-Host "  FQDN:            $pgFqdn"
Write-Host "  Database:        $pgDatabaseName"
Write-Host "  Version:         $pgVersion"
Write-Host "  SKU:             $pgSku"
Write-Host "  Storage (GB):    $pgStorageGb"
Write-Host "  HA mode:         $pgHaMode"
Write-Host "  Backup days:     $pgBackupDays"
Write-Host "  Subnet:          $pgSubnetName ($pgSubnetPrefix)"
Write-Host "  DNS zone:        $pgDnsZone"
Write-Host "  AKS VNet:        $aksVnetName in $aksMcResourceGroup"
Write-Host "  K8s namespace:   $($env:NAMESPACE)"
Write-Host ""

Write-Host "Ensuring Kubernetes namespace '$($env:NAMESPACE)' exists for the Postgres secret..."
$namespaceYaml = kubectl create namespace $env:NAMESPACE --dry-run=client -o yaml
Assert-LastExitCode "Rendering Kubernetes namespace '$($env:NAMESPACE)'"
$namespaceYaml | kubectl apply -f -
Assert-LastExitCode "Applying Kubernetes namespace '$($env:NAMESPACE)'"
Write-Host ""

Write-Host "Step 1: Ensuring delegated subnet '$pgSubnetName'..."
$existingSubnet = (az network vnet subnet show `
  --resource-group $aksMcResourceGroup `
  --vnet-name $aksVnetName `
  --name $pgSubnetName `
  --query id `
  --output tsv 2>$null | Out-String).Trim()
if ($LASTEXITCODE -eq 0 -and $existingSubnet) {
  Write-Host "  [SKIP] Subnet '$pgSubnetName' already exists."
  $pgSubnetId = $existingSubnet
} else {
  Write-Host "  Creating subnet '$pgSubnetName' ($pgSubnetPrefix)..."
  $pgSubnetId = (az network vnet subnet create `
    --resource-group $aksMcResourceGroup `
    --vnet-name $aksVnetName `
    --name $pgSubnetName `
    --address-prefixes $pgSubnetPrefix `
    --delegations Microsoft.DBforPostgreSQL/flexibleServers `
    --query id `
    --output tsv | Out-String).Trim()
  Assert-LastExitCode "Creating PostgreSQL delegated subnet"
  Write-Host "  [OK] Subnet created: $pgSubnetId"
}

Write-Host ""
Write-Host "Step 2: Ensuring Private DNS zone '$pgDnsZone'..."
$existingZone = (az network private-dns zone show `
  --resource-group $env:RESOURCE_GROUP `
  --name $pgDnsZone `
  --query id `
  --output tsv 2>$null | Out-String).Trim()
if ($LASTEXITCODE -eq 0 -and $existingZone) {
  Write-Host "  [SKIP] DNS zone '$pgDnsZone' already exists."
} else {
  Write-Host "  Creating Private DNS zone '$pgDnsZone'..."
  az network private-dns zone create `
    --resource-group $env:RESOURCE_GROUP `
    --name $pgDnsZone `
    --output none
  Assert-LastExitCode "Creating PostgreSQL private DNS zone"
  Write-Host "  [OK] DNS zone created."
}

Write-Host ""
Write-Host "Step 3: Ensuring VNet DNS link '$pgDnsLinkName'..."
$existingLink = (az network private-dns link vnet show `
  --resource-group $env:RESOURCE_GROUP `
  --zone-name $pgDnsZone `
  --name $pgDnsLinkName `
  --query id `
  --output tsv 2>$null | Out-String).Trim()
if ($LASTEXITCODE -eq 0 -and $existingLink) {
  Write-Host "  [SKIP] VNet link '$pgDnsLinkName' already exists."
} else {
  Write-Host "  Linking DNS zone to VNet '$aksVnetName'..."
  az network private-dns link vnet create `
    --resource-group $env:RESOURCE_GROUP `
    --zone-name $pgDnsZone `
    --name $pgDnsLinkName `
    --virtual-network $aksVnetId `
    --registration-enabled false `
    --output none
  Assert-LastExitCode "Creating PostgreSQL private DNS VNet link"
  Write-Host "  [OK] VNet link created."
}

Write-Host ""
Write-Host "Step 4: Ensuring Flexible Server '$pgServerName'..."
$existingServer = (az postgres flexible-server show `
  --resource-group $env:RESOURCE_GROUP `
  --name $pgServerName `
  --query state `
  --output tsv 2>$null | Out-String).Trim()
if ($LASTEXITCODE -eq 0 -and $existingServer) {
  Write-Host "  [SKIP] Server '$pgServerName' already exists (state: $existingServer)."
} else {
  Write-Host "  Generating admin password (not echoed; will be stored in K8s secret)..."
  $pgAdminPassword = New-PostgresAdminPassword
  Write-Host "  Creating Flexible Server '$pgServerName' — this takes ~5 minutes..."

  $zonalFlags = @()
  if ($pgHaMode -ne "Disabled") {
    $zonalFlags = @("--zonal-resiliency", "Enabled")
  }

  $createOutput = (& az postgres flexible-server create `
    --resource-group $env:RESOURCE_GROUP `
    --name $pgServerName `
    --location $env:LOCATION `
    --admin-user $pgAdminUser `
    --admin-password $pgAdminPassword `
    --version $pgVersion `
    --sku-name $pgSku `
    --tier GeneralPurpose `
    --storage-size $pgStorageGb `
    @zonalFlags `
    --backup-retention $pgBackupDays `
    --subnet $pgSubnetId `
    --private-dns-zone $pgDnsZoneId `
    --yes `
    --output none 2>&1 | Out-String)
  $createExitCode = $LASTEXITCODE
  $createOutput -split [Environment]::NewLine | Where-Object { $_ -notmatch "(?i)password" } | ForEach-Object { Write-Host $_ }
  if ($createExitCode -ne 0) {
    throw "Creating PostgreSQL Flexible Server failed (exit $createExitCode)."
  }
  Write-Host "  [OK] Server created."

  Write-Host "  Storing credentials in K8s secret 'agentweaver-postgres'..."
  $pgConnectionString = "Host=$pgFqdn;Port=5432;Database=$pgDatabaseName;Username=$pgAdminUser;Password=$pgAdminPassword;Ssl Mode=Require;Trust Server Certificate=false"
  try {
    $secretYaml = kubectl create secret generic agentweaver-postgres `
      --namespace $env:NAMESPACE `
      "--from-literal=host=$pgFqdn" `
      --from-literal=port=5432 `
      "--from-literal=database=$pgDatabaseName" `
      "--from-literal=username=$pgAdminUser" `
      "--from-literal=password=$pgAdminPassword" `
      "--from-literal=connectionstring=$pgConnectionString" `
      --save-config `
      --dry-run=client `
      -o yaml
    Assert-LastExitCode "Rendering PostgreSQL Kubernetes secret"
    $secretYaml | kubectl apply -f -
    Assert-LastExitCode "Applying PostgreSQL Kubernetes secret"
  } finally {
    Remove-Variable pgAdminPassword -ErrorAction SilentlyContinue
    Remove-Variable pgConnectionString -ErrorAction SilentlyContinue
  }

  Write-Host "  [OK] K8s secret 'agentweaver-postgres' created/updated."
  Write-Host "       Admin password is stored in: secret/agentweaver-postgres, key 'password'"
  Write-Host "       Connection string in:         secret/agentweaver-postgres, key 'connectionstring'"
}

Write-Host ""
Write-Host "Step 5: Ensuring database '$pgDatabaseName'..."
$existingDatabase = (az postgres flexible-server db show `
  --resource-group $env:RESOURCE_GROUP `
  --server-name $pgServerName `
  --name $pgDatabaseName `
  --query name `
  --output tsv 2>$null | Out-String).Trim()
if ($LASTEXITCODE -eq 0 -and $existingDatabase) {
  Write-Host "  [SKIP] Database '$pgDatabaseName' already exists."
} else {
  Write-Host "  Creating database '$pgDatabaseName'..."
  az postgres flexible-server db create `
    --resource-group $env:RESOURCE_GROUP `
    --server-name $pgServerName `
    --name $pgDatabaseName `
    --output none
  Assert-LastExitCode "Creating PostgreSQL database '$pgDatabaseName'"
  Write-Host "  [OK] Database '$pgDatabaseName' created."
}

Write-Host ""
Write-Host "Step 6: Verifying private DNS A record for '$pgServerName'..."
$privateIp = (az postgres flexible-server show `
  --resource-group $env:RESOURCE_GROUP `
  --name $pgServerName `
  --query network.delegatedSubnetResourceId `
  --output tsv 2>$null | Out-String).Trim()
$existingARecord = (az network private-dns record-set a show `
  --resource-group $env:RESOURCE_GROUP `
  --zone-name $pgDnsZone `
  --name $pgServerName `
  --query name `
  --output tsv 2>$null | Out-String).Trim()
if ($LASTEXITCODE -eq 0 -and $existingARecord) {
  Write-Host "  [SKIP] A record '$pgServerName' already exists in $pgDnsZone."
} else {
  $dnsRecords = az network private-dns record-set a list `
    --resource-group $env:RESOURCE_GROUP `
    --zone-name $pgDnsZone `
    --output json 2>$null
  if ($LASTEXITCODE -eq 0) {
    $privateIp = @(
      $dnsRecords |
        ConvertFrom-Json |
        Where-Object { $_.name -ne "@" } |
        ForEach-Object { $_.aRecords | Select-Object -First 1 } |
        Select-Object -ExpandProperty ipv4Address -First 1
    )[0]
  } else {
    $privateIp = ""
  }
  if ($privateIp) {
    Write-Host "  Adding A record '$pgServerName' → $privateIp..."
    az network private-dns record-set a add-record `
      --resource-group $env:RESOURCE_GROUP `
      --zone-name $pgDnsZone `
      --record-set-name $pgServerName `
      --ipv4-address $privateIp `
      --output none
    Assert-LastExitCode "Adding PostgreSQL private DNS A record"
    Write-Host "  [OK] A record added: $pgServerName.$pgDnsZone → $privateIp"
  } else {
    Write-Host "  WARNING: Could not determine private IP. Azure normally creates this record when"
    Write-Host "           --private-dns-zone is set; verify $pgServerName.$pgDnsZone manually."
  }
}

Write-Host ""
Write-Host "Step 7: Verifying server state..."
$serverState = (az postgres flexible-server show `
  --resource-group $env:RESOURCE_GROUP `
  --name $pgServerName `
  --query state `
  --output tsv 2>$null | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { $serverState = "unknown" }
Write-Host "  Server state: $serverState"
if ($serverState -ne "Ready") {
  Write-Host "  WARNING: Server is not in Ready state. It may still be provisioning."
}

$existingSecret = (kubectl get secret agentweaver-postgres `
  --namespace $env:NAMESPACE `
  --ignore-not-found `
  --output jsonpath='{.metadata.name}' 2>$null | Out-String).Trim()
if (-not $existingSecret) {
  Write-Host ""
  Write-Host "WARNING: K8s secret 'agentweaver-postgres' was not found."
  Write-Host "  The server already existed — retrieve the password from Key Vault or Azure Portal"
  Write-Host "  to create secret/agentweaver-postgres with host, port, database, username,"
  Write-Host "  password, and connectionstring keys."
}

Write-Host ""
Write-Host "==================================================="
Write-Host " POSTGRES PROVISIONING COMPLETE"
Write-Host "==================================================="
Write-Host ""
Write-Host "  Server:          $pgServerName"
Write-Host "  FQDN:            $pgFqdn"
Write-Host "  Database:        $pgDatabaseName"
Write-Host "  SKU:             $pgSku (GeneralPurpose)"
Write-Host "  HA:              $pgHaMode"
Write-Host "  Networking:      Private (VNet integration, no public endpoint)"
Write-Host "  App FQDN:        $pgFqdn (private resolution is via $pgDnsZone)"
Write-Host "  Subnet:          $pgSubnetName ($pgSubnetPrefix) in $aksVnetName"
Write-Host "  DNS zone:        $pgDnsZone → linked to $aksVnetName"
Write-Host ""
Write-Host "  K8s secret:      secret/agentweaver-postgres (namespace: $($env:NAMESPACE))"
Write-Host "  Connection key:  connectionstring (used by ConnectionStrings:MemoryDb and ConnectionStrings:Postgres)"
