// Dedicated Log Analytics workspace and Azure Monitor exporter target for
// the P0 OpenTelemetry/Azure Monitor foundation. This module only creates
// the workspace and (optional) Application Insights resource; it does not
// configure any service's exporter connection string.
//
// Query access is private-link only (publicNetworkAccessForQuery:
// 'Disabled'), so this module also provisions an Azure Monitor Private
// Link Scope (AMPLS), a private endpoint for it on the dedicated subnet,
// and all five documented private DNS zones. Open ingestion permits public
// access but does not override private DNS or provide a public fallback.
@description('Azure region for the workspace.')
param location string

@description('Dedicated naming prefix, e.g. aw-v1-p0.')
param namePrefix string

@description('Resource tags applied to the workspace and app insights resource.')
param tags object

@description('Log retention in days.')
param retentionInDays int = 30

@description('Subnet resource ID the Azure Monitor private endpoint is attached to.')
param privateEndpointsSubnetId string

@description('Resource ID of the privatelink.monitor.azure.com private DNS zone, linked to the dedicated VNet by the caller.')
param monitorPrivateDnsZoneId string

@description('Resource ID of the privatelink.oms.opinsights.azure.com private DNS zone, linked to the dedicated VNet by the caller.')
param omsPrivateDnsZoneId string

@description('Resource ID of the privatelink.ods.opinsights.azure.com private DNS zone, linked to the dedicated VNet by the caller.')
param odsPrivateDnsZoneId string

param agentSvcPrivateDnsZoneId string
param blobPrivateDnsZoneId string

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-law'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Disabled'
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-appi'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Disabled'
  }
}

resource ampls 'Microsoft.Insights/privateLinkScopes@2021-07-01-preview' = {
  name: '${namePrefix}-ampls'
  location: 'global'
  tags: tags
  properties: {
    accessModeSettings: {
      queryAccessMode: 'PrivateOnly'
      ingestionAccessMode: 'Open'
    }
  }
}

resource amplsWorkspaceScope 'Microsoft.Insights/privateLinkScopes/scopedResources@2021-07-01-preview' = {
  parent: ampls
  name: '${namePrefix}-law-scope'
  properties: {
    linkedResourceId: workspace.id
  }
}

resource amplsAppInsightsScope 'Microsoft.Insights/privateLinkScopes/scopedResources@2021-07-01-preview' = {
  parent: ampls
  name: '${namePrefix}-appi-scope'
  properties: {
    linkedResourceId: appInsights.id
  }
}

resource privateEndpoint 'Microsoft.Network/privateEndpoints@2023-09-01' = {
  name: '${namePrefix}-ampls-pe'
  location: location
  tags: tags
  dependsOn: [
    amplsWorkspaceScope
    amplsAppInsightsScope
  ]
  properties: {
    subnet: {
      id: privateEndpointsSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${namePrefix}-ampls-pls'
        properties: {
          privateLinkServiceId: ampls.id
          groupIds: [
            'azuremonitor'
          ]
        }
      }
    ]
  }
}

resource privateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-09-01' = {
  parent: privateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'monitor'
        properties: {
          privateDnsZoneId: monitorPrivateDnsZoneId
        }
      }
      {
        name: 'oms'
        properties: {
          privateDnsZoneId: omsPrivateDnsZoneId
        }
      }
      {
        name: 'ods'
        properties: {
          privateDnsZoneId: odsPrivateDnsZoneId
        }
      }
      {
        name: 'agentsvc'
        properties: {
          privateDnsZoneId: agentSvcPrivateDnsZoneId
        }
      }
      {
        name: 'blob'
        properties: {
          privateDnsZoneId: blobPrivateDnsZoneId
        }
      }
    ]
  }
}

output workspaceId string = workspace.id
output workspaceCustomerId string = workspace.properties.customerId
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output appInsightsId string = appInsights.id
