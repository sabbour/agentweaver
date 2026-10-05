import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bootstrapFoundationProbeInputs } from '../lib/foundation-probe-inputs.mjs';
import { foundationProbeEgress } from '../lib/foundation-probe-egress.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const principal = '22222222-2222-2222-2222-222222222222';
const config = { resourceGroup: 'aw-v1-p0', clusterName: 'aw-v1-p0-aks',
  subscriptionId: id, tenantId: id, repoRoot: process.cwd() };
const prefix = `/subscriptions/${id}/resourceGroups/aw-v1-p0/providers/`;
const issuer = `https://eastus2euap.oic.prod-aks.azure.com/${id}/cluster/`;
const connectionString = `InstrumentationKey=${principal};IngestionEndpoint=https://eastus-1.in.applicationinsights.azure.com/`;
const ok = stdout => ({ status: 0, stdout: stdout ?? '', stderr: '' });
const endpointId = `${prefix}Microsoft.Network/privateEndpoints/aw-v1-p0-ampls-pe`;
const nicId = `${prefix}Microsoft.Network/networkInterfaces/aw-v1-p0-ampls-pe.nic.owned`;
const route = () => ({
  endpoint: { id: endpointId, networkInterfaces: [{ id: nicId }],
    privateLinkServiceConnections: [{ privateLinkServiceId: `${prefix}Microsoft.Insights/privateLinkScopes/aw-v1-p0-ampls`,
      groupIds: ['azuremonitor'], privateLinkServiceConnectionState: { status: 'Approved' } }] },
  nic: { id: nicId, privateEndpoint: { id: endpointId }, ipConfigurations: [{
    privateIPAddress: '10.90.33.11',
    subnet: { id: `${prefix}Microsoft.Network/virtualNetworks/aw-v1-p0-vnet/subnets/private-endpoints` },
    privateLinkConnectionProperties: { requiredMemberName: 'api', groupId: 'azuremonitor', fqdns: ['api.monitor.azure.com'] },
  }] },
  record: { id: `${prefix}Microsoft.Network/privateDnsZones/privatelink.monitor.azure.com/A/api`,
    fqdn: 'api.privatelink.monitor.azure.com.', aRecords: [{ ipv4Address: '10.90.33.11' }] },
  ingestionHost: 'eastus-1.in.applicationinsights.azure.com',
});

function dependencies({ failWrite = false, wrongFic = false, failPolicyWrite = false, wrongPolicyReadback = false } = {}) {
  const calls = [];
  const azureOutput = [];
  const objects = new Map();
  return { calls, azureOutput, objects,
    withKubeconfig: (config, action) => action(['--kubeconfig', 'owned']),
    execAz(args, options) {
      let value;
      if (args[0] === 'account') value = { id, tenantId: id, state: 'Enabled' };
      if (args[0] === 'aks') value = { oidcIssuerProfile: { issuerUrl: issuer } };
      if (args[0] === 'identity' && args[1] === 'show') value = {
        id: `${prefix}Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-foundation-probe`,
        clientId: id, principalId: principal,
      };
      if (args[0] === 'identity' && args[1] === 'federated-credential') value = {
        issuer, subject: `system:serviceaccount:agentweaver-v1-p0:${wrongFic ? 'foreign' : 'foundation-probe'}`,
        audiences: ['api://AzureADTokenExchange'],
      };
      if (args[0] === 'monitor') value = {
        id: `${prefix}Microsoft.Insights/components/aw-v1-p0-appi`, connectionString,
      };
      if (args[0] === 'network') value = args[1] === 'private-endpoint' ? route().endpoint :
        args[1] === 'nic' ? route().nic : route().record;
      const projected = JSON.stringify(options.projectJson(value));
      azureOutput.push(projected);
      return ok(projected);
    },
    execKubectl(args, options) {
      calls.push({ args, options });
      if (args.includes('get')) {
        const name = args[args.indexOf('--field-selector') + 1].split('=')[1];
        const object = objects.get(name);
        return ok(JSON.stringify(options.projectJson({ items: object ? [object] : [] })));
      }
      assert.ok(args.includes('create') || args.includes('replace'));
      if (options.input.startsWith('{')) {
        const object = JSON.parse(options.input);
        if (object.kind === 'CiliumNetworkPolicy') {
          if (failPolicyWrite) return { status: 1, stderr: 'resource version conflict', stdout: '' };
          object.metadata.uid ??= 'policy-uid';
          object.metadata.resourceVersion = '2';
          if (wrongPolicyReadback) object.spec.egress.at(-1).toCIDR = ['52.1.2.3/32'];
          objects.set(object.metadata.name, object);
          return ok();
        }
        assert.deepEqual(Object.keys(object.data), ['APPLICATIONINSIGHTS_CONNECTION_STRING']);
        if (failWrite) return { status: 1, stdout: connectionString, stderr: connectionString };
        object.metadata.uid = 'secret-uid';
        objects.set(object.metadata.name, object);
      } else {
        assert.ok(!options.input.includes('CHANGEME-'));
        objects.set('foundation-probe', { kind: 'ServiceAccount', metadata: {
          name: 'foundation-probe', namespace: 'agentweaver-v1-p0', uid: 'sa-uid',
          annotations: { 'azure.workload.identity/client-id': id, 'azure.workload.identity/tenant-id': id },
        } });
      }
      return ok();
    },
  };
}

test('Probe install derives existing federation and Monitor configuration without surfaced private values', () => {
  const deps = dependencies();
  const receipt = bootstrapFoundationProbeInputs(config, deps);
  assert.equal(receipt.serviceAccount.created, true);
  assert.equal(receipt.monitor.created, true);
  assert.equal(receipt.runtimeVerified, false);
  assert.ok(!JSON.stringify(receipt).includes(connectionString));
  assert.ok(!deps.azureOutput.join().includes(connectionString));
  const writes = deps.calls.filter(call => call.args.includes('create')).length;
  assert.equal(bootstrapFoundationProbeInputs(config, deps).monitor.created, false);
  assert.equal(deps.calls.filter(call => call.args.includes('create')).length, writes);
  assert.ok(!deps.calls.some(call => call.args.includes('apply') || call.args.includes('patch') || call.args.includes('delete')));
});

test('Probe Logs route permits only owned private API /32 and exact DNS names', () => {
  const egress = foundationProbeEgress(config, route());
  const rule = egress.spec.egress.at(-1);
  assert.deepEqual(rule, { toCIDR: ['10.90.33.11/32'],
    toPorts: [{ ports: [{ port: '443', protocol: 'TCP' }] }] });
  const names = egress.spec.egress[0].toPorts[0].rules.dns.map(value => value.matchName);
  assert.deepEqual(names.slice(-3), ['api.loganalytics.io', 'api.monitor.azure.com', 'api.privatelink.monitor.azure.com']);
  assert.ok(!JSON.stringify(egress.spec).includes('matchPattern'));
  assert.ok(!egress.spec.egress.some(value => value.toFQDNs?.some(host => host.matchName === 'api.loganalytics.io')));
});

test('Probe Logs route rejects wrong host, public IP and foreign or inconsistent ownership', () => {
  for (const change of [
    value => { value.nic.ipConfigurations[0].privateLinkConnectionProperties.fqdns = ['foreign.monitor.azure.com']; },
    value => { value.nic.ipConfigurations[0].privateIPAddress = value.record.aRecords[0].ipv4Address = '52.1.2.3'; },
    value => { value.nic.privateEndpoint.id = `${endpointId}-foreign`; },
    value => { value.endpoint.privateLinkServiceConnections[0].privateLinkServiceConnectionState.status = 'Pending'; },
    value => { value.record.aRecords[0].ipv4Address = '10.90.33.12'; },
    value => { value.record.fqdn = 'foreign.privatelink.monitor.azure.com.'; },
    value => { value.ingestionHost = 'foreign.example'; },
  ]) {
    const value = route();
    change(value);
    assert.throws(() => foundationProbeEgress(config, value), /exact/);
  }
});

test('Probe installer replaces only exact baseline policy with resource-version guard and preserves identity', () => {
  const deps = dependencies();
  deps.objects.set('foundation-probe-egress', { apiVersion: 'cilium.io/v2', kind: 'CiliumNetworkPolicy',
    metadata: { name: 'foundation-probe-egress', namespace: 'agentweaver-v1-p0',
      uid: 'existing-policy', resourceVersion: '1' }, spec: foundationProbeEgress(config, route()).baseline });
  const receipt = bootstrapFoundationProbeInputs(config, deps);
  assert.equal(receipt.egress.changed, true);
  const replacement = JSON.parse(deps.calls.find(call => call.args.includes('replace')).options.input);
  assert.equal(replacement.metadata.uid, 'existing-policy');
  assert.equal(replacement.metadata.resourceVersion, '1');
  assert.equal(bootstrapFoundationProbeInputs(config, deps).egress.changed, false);
  deps.objects.get('foundation-probe-egress').spec.egress.push({ toCIDR: ['0.0.0.0/0'] });
  const count = deps.calls.filter(call => call.args.includes('replace')).length;
  assert.throws(() => bootstrapFoundationProbeInputs(config, deps), /refusing replacement/);
  assert.equal(deps.calls.filter(call => call.args.includes('replace')).length, count);
});

test('Probe installer fails explicitly on policy conflict or changed readback without writing private material', () => {
  for (const [option, expected] of [
    ['failPolicyWrite', /policy write failed: resource version conflict/],
    ['wrongPolicyReadback', /policy readback differs/],
  ]) {
    const deps = dependencies({ [option]: true });
    assert.throws(() => bootstrapFoundationProbeInputs(config, deps), expected);
    assert.ok(!deps.objects.has('foundation-probe-monitor'));
    assert.ok(!deps.objects.has('foundation-probe'));
  }
});

test('Probe install rejects changed federation or existing Monitor material without replacement', () => {
  const wrong = dependencies({ wrongFic: true });
  assert.throws(() => bootstrapFoundationProbeInputs(config, wrong), /exact native federation/);
  assert.equal(wrong.calls.length, 0);
  const deps = dependencies();
  bootstrapFoundationProbeInputs(config, deps);
  deps.objects.get('foundation-probe-monitor').data.APPLICATIONINSIGHTS_CONNECTION_STRING = Buffer.from('different').toString('base64');
  assert.throws(() => bootstrapFoundationProbeInputs(config, deps), /refusing replacement/);
});

test('Probe private write failures omit even echoed native secret bytes', () => {
  assert.throws(() => bootstrapFoundationProbeInputs(config, dependencies({ failWrite: true })), error =>
    /private output omitted/.test(error.message) && !error.message.includes('InstrumentationKey'));
});
