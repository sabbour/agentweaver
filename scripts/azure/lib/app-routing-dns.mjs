const subscriptionIdPattern = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const resourceGroupPattern = /^[a-z0-9_.()-]{1,90}$/i;
const dnsLabelPattern = /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/i;

export function parseAppRoutingDnsZoneResourceId(resourceId) {
  const match = typeof resourceId === 'string' && resourceId.match(
    /^\/subscriptions\/([^/]+)\/resourceGroups\/([^/]+)\/providers\/Microsoft\.Network\/(dnsZones|privateDnsZones)\/([^/]+)$/i,
  );
  if (!match) throw new Error('App Routing DNS zone ID must identify one full Microsoft.Network DNS zone resource.');

  const [, subscriptionId, resourceGroup, resourceType, zoneName] = match;
  const labels = zoneName.split('.');
  if (!subscriptionIdPattern.test(subscriptionId) || !resourceGroupPattern.test(resourceGroup) ||
      zoneName.length > 253 || labels.length < 2 || labels.some(label => !dnsLabelPattern.test(label)) ||
      zoneName.toLowerCase().startsWith('privatelink.')) {
    throw new Error('App Routing DNS zone ID has an invalid subscription, resource group, or application domain.');
  }

  const zoneIsPrivate = resourceType.toLowerCase() === 'privatednszones';
  return {
    resourceId,
    subscriptionId,
    resourceGroup,
    zoneName,
    zoneIsPrivate,
    roleDefinitionId: zoneIsPrivate
      ? 'b12aa53e-6015-4669-85d0-8515ebb3ae7f'
      : 'befefa01-2a29-4197-83a8-272ff33ce314',
  };
}

export function validateAppRoutingDnsZoneResourceIds(resourceIds, subscriptionId) {
  if (resourceIds === undefined) return [];
  if (!Array.isArray(resourceIds)) throw new Error('App Routing DNS zone IDs must be an array.');
  if (resourceIds.length > 5) throw new Error('App Routing accepts no more than five custom DNS zones.');
  if (resourceIds.length === 0) return [];
  if (!subscriptionIdPattern.test(subscriptionId ?? '')) {
    throw new Error('Custom App Routing DNS zones require the exact authorized subscription ID.');
  }

  const zones = resourceIds.map(parseAppRoutingDnsZoneResourceId);
  const seen = new Set();
  const resourceGroups = new Map();
  for (const zone of zones) {
    if (zone.subscriptionId.toLowerCase() !== subscriptionId.toLowerCase()) {
      throw new Error('App Routing DNS zones must use the exact authorized subscription.');
    }
    const normalizedId = zone.resourceId.toLowerCase();
    if (seen.has(normalizedId)) throw new Error('App Routing DNS zone IDs must be unique.');
    seen.add(normalizedId);

    const kind = zone.zoneIsPrivate ? 'private' : 'public';
    const group = zone.resourceGroup.toLowerCase();
    const previousGroup = resourceGroups.get(kind);
    if (previousGroup && previousGroup !== group) {
      throw new Error(`All ${kind} App Routing DNS zones must use one resource group.`);
    }
    resourceGroups.set(kind, group);
  }
  return zones.map(zone => zone.resourceId);
}
