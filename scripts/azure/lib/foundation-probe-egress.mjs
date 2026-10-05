import { isIPv4 } from 'node:net';

const logsHost = 'api.loganalytics.io';
const logsAliases = ['api.monitor.azure.com', 'api.privatelink.monitor.azure.com'];
const sameId = (actual, expected) => typeof actual === 'string' && actual.toLowerCase() === expected.toLowerCase();

export function foundationProbeEgress(config, { endpoint, nic, record, ingestionHost }) {
  const prefix = `/subscriptions/${config.subscriptionId}/resourceGroups/aw-v1-p0/providers/`;
  const endpointId = `${prefix}Microsoft.Network/privateEndpoints/aw-v1-p0-ampls-pe`;
  const subnetId = `${prefix}Microsoft.Network/virtualNetworks/aw-v1-p0-vnet/subnets/private-endpoints`;
  const connections = endpoint?.privateLinkServiceConnections;
  if (config.resourceGroup !== 'aw-v1-p0' || config.clusterName !== 'aw-v1-p0-aks' ||
      !sameId(endpoint?.id, endpointId) || connections?.length !== 1 ||
      !sameId(connections[0].privateLinkServiceId, `${prefix}Microsoft.Insights/privateLinkScopes/aw-v1-p0-ampls`) ||
      connections[0].privateLinkServiceConnectionState?.status !== 'Approved' ||
      connections[0].groupIds?.length !== 1 || connections[0].groupIds[0] !== 'azuremonitor' ||
      endpoint.networkInterfaces?.length !== 1 ||
      !sameId(endpoint.networkInterfaces[0].id, nic?.id) ||
      !sameId(nic?.privateEndpoint?.id, endpointId)) {
    throw new Error('Probe Logs egress requires the exact approved AMPLS endpoint and reciprocal NIC.');
  }
  const members = nic.ipConfigurations?.filter(value =>
    value.privateLinkConnectionProperties?.requiredMemberName === 'api');
  const member = members?.[0];
  const address = member?.privateIPAddress;
  const bytes = typeof address === 'string' ? address.split('.').map(Number) : [];
  const privateAddress = isIPv4(address ?? '') && (bytes[0] === 10 || bytes[0] === 192 && bytes[1] === 168 ||
    bytes[0] === 172 && bytes[1] >= 16 && bytes[1] <= 31);
  if (members?.length !== 1 || member.privateLinkConnectionProperties.groupId !== 'azuremonitor' ||
      member.privateLinkConnectionProperties.fqdns?.length !== 1 ||
      member.privateLinkConnectionProperties.fqdns[0] !== logsAliases[0] ||
      !sameId(member.subnet?.id, subnetId) || !privateAddress ||
      !sameId(record?.id, `${prefix}Microsoft.Network/privateDnsZones/privatelink.monitor.azure.com/A/api`) ||
      record.fqdn !== `${logsAliases[1]}.` ||
      record.aRecords?.length !== 1 || record.aRecords[0].ipv4Address !== address ||
      !/^[a-z0-9]+(?:-[a-z0-9]+)*\.in\.applicationinsights\.azure\.com$/.test(ingestionHost ?? '')) {
    throw new Error('Probe Logs egress requires an exact private API member, matching DNS record and ingestion host.');
  }
  const baseDns = ['login.microsoftonline.com', 'aw-v1-p0-kv.vault.azure.net',
    'awv1p0blob.blob.core.windows.net', ingestionHost, 'aw-v1-p0-pg.postgres.database.azure.com'];
  const ports = port => [{ ports: [{ port, protocol: 'TCP' }] }];
  const baseline = {
    endpointSelector: { matchLabels: { 'agentweaver.io/probe': 'foundation-probe' } },
    egress: [
      { toEndpoints: [{ matchLabels: { 'k8s:io.kubernetes.pod.namespace': 'kube-system', 'k8s:k8s-app': 'kube-dns' } }],
        toPorts: [{ ports: [{ port: '53', protocol: 'UDP' }, { port: '53', protocol: 'TCP' }],
          rules: { dns: baseDns.map(matchName => ({ matchName })) } }] },
      { toFQDNs: ['login.microsoftonline.com', 'aw-v1-p0-kv.vault.azure.net'].map(matchName => ({ matchName })),
        toPorts: ports('443') },
      { toFQDNs: ['awv1p0blob.blob.core.windows.net', ingestionHost].map(matchName => ({ matchName })),
        toPorts: ports('443') },
      { toFQDNs: [{ matchName: 'aw-v1-p0-pg.postgres.database.azure.com' }], toPorts: ports('5432') },
    ],
  };
  const spec = structuredClone(baseline);
  spec.egress[0].toPorts[0].rules.dns.push(...[logsHost, ...logsAliases].map(matchName => ({ matchName })));
  spec.egress.push({ toCIDR: [`${address}/32`], toPorts: ports('443') });
  return { baseline, spec, logsHost, privateAddress: address, endpointId, nicId: nic.id, recordId: record.id };
}
