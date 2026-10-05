import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bootstrapFoundationProbeInputs } from '../lib/foundation-probe-inputs.mjs';

const id = '11111111-1111-1111-1111-111111111111';
const principal = '22222222-2222-2222-2222-222222222222';
const config = { resourceGroup: 'aw-v1-p0', clusterName: 'aw-v1-p0-aks',
  subscriptionId: id, tenantId: id, repoRoot: process.cwd() };
const prefix = `/subscriptions/${id}/resourceGroups/aw-v1-p0/providers/`;
const issuer = `https://eastus2euap.oic.prod-aks.azure.com/${id}/cluster/`;
const connectionString = `InstrumentationKey=${principal};IngestionEndpoint=https://eastus-1.in.applicationinsights.azure.com/`;
const ok = stdout => ({ status: 0, stdout: stdout ?? '', stderr: '' });

function dependencies({ failWrite = false, wrongFic = false } = {}) {
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
      assert.ok(args.includes('create'));
      if (options.input.startsWith('{')) {
        const object = JSON.parse(options.input);
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
