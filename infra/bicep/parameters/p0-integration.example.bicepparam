using '../main.bicep'

// Example parameter values for the dedicated v1 P0 integration environment.
// These are placeholders: no real subscription, tenant, or Entra object ID
// is checked in. The coordinator supplies real values out-of-band when an
// authorized target is confirmed; scripts/azure/deploy.mjs refuses to run
// without an explicit, non-placeholder namePrefix and target resource group.
param location = 'eastus2'
param namePrefix = 'aw-v1-p0'
param tenantId = '00000000-0000-0000-0000-000000000000'
param postgresEntraAdminObjectId = '00000000-0000-0000-0000-000000000000'
param postgresEntraAdminPrincipalName = 'agentweaver-v1-p0-admins'
param appRoutingDnsZoneResourceIds = []
param owner = 'agentweaver-v1-platform'
param costCenter = 'unassigned'
param sourceSha = '0000000000000000000000000000000000000000'
param sourceHash = '0000000000000000000000000000000000000000000000000000000000000000'
