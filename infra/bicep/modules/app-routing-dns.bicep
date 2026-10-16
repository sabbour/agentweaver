@description('Exact existing public or private DNS zone resource ID.')
param zoneResourceId string

@description('Whether the zone is a private DNS zone.')
param zoneIsPrivate bool

@description('Exact AKS cluster resource ID used to name the role assignment.')
param aksClusterId string

@description('AKS-generated Application Routing identity object ID.')
param appRoutingIdentityObjectId string

var dnsZoneContributorRoleId = 'befefa01-2a29-4197-83a8-272ff33ce314'
var privateDnsZoneContributorRoleId = 'b12aa53e-6015-4669-85d0-8515ebb3ae7f'
var zoneName = last(split(zoneResourceId, '/'))

resource publicZone 'Microsoft.Network/dnsZones@2018-05-01' existing = {
  name: zoneName
}

resource privateZone 'Microsoft.Network/privateDnsZones@2020-06-01' existing = {
  name: zoneName
}

resource publicDnsRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!zoneIsPrivate) {
  name: guid(zoneResourceId, aksClusterId, dnsZoneContributorRoleId)
  scope: publicZone
  properties: {
    principalId: appRoutingIdentityObjectId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', dnsZoneContributorRoleId)
  }
}

resource privateDnsRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (zoneIsPrivate) {
  name: guid(zoneResourceId, aksClusterId, privateDnsZoneContributorRoleId)
  scope: privateZone
  properties: {
    principalId: appRoutingIdentityObjectId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', privateDnsZoneContributorRoleId)
  }
}
