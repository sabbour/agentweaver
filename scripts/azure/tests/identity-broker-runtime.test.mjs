import { test } from 'node:test';
import assert from 'node:assert/strict';
import { alignExistingP0BrokerHostname, observeExistingP0BrokerRouting } from '../lib/identity-broker-routing.mjs';
import { assertBrokerRuntimeInputs, bootstrapIdentityBrokerRuntime, brokerAcceptanceConfiguration,
  brokerObjectHash, projectBrokerRuntimeResource } from '../lib/identity-broker-runtime.mjs';

const namespace = 'agentweaver-v1-p0';
const clientId = '11111111-1111-1111-1111-111111111111';
const principalId = '22222222-2222-2222-2222-222222222222';
const config = {
  resourceGroup: 'aw-v1-p0', clusterName: 'aw-v1-p0-aks', subscriptionId: clientId, tenantId: clientId,
  upstreamClientId: clientId, acceptanceRunId: principalId, acceptanceRedirectUri: 'http://127.0.0.1:18641/callback',
  brokerImage: 'ghcr.io/sabbour/agentweaver.identity.broker@sha256:' + 'a'.repeat(64),
  repoRoot: process.cwd(),
};
const zone = '6ac31fa7dd86cf00014876f0.eastus2euap.aksapp.io';
const hostname = `agentweaver.${zone}`;
const legacyHostname = `identity-broker.${zone}`;
const issuer = `https://eastus2euap.oic.prod-aks.azure.com/${clientId}/cluster/`;
const ok = stdout => ({ status: 0, stdout: stdout ?? '', stderr: '' });
function object(kind, spec, status) {
  return { apiVersion: kind === 'DefaultDomainCertificate'
    ? 'approuting.kubernetes.azure.com/v1alpha1' : 'gateway.networking.k8s.io/v1',
  kind, metadata: { name: 'identity-broker', namespace, uid: `${kind}-uid`, resourceVersion: '1',
    generation: 1, labels: { 'agentweaver.io/service': 'identity-broker' } }, spec, status };
}
function routing(host = legacyHostname) {
  return {
    certificate: object('DefaultDomainCertificate', { target: { secret: 'identity-broker-tls' } }, {
      domain: `*.${zone}`, conditions: [{ type: 'Available', status: 'True', observedGeneration: 1 }],
    }),
    gateway: object('Gateway', { gatewayClassName: 'approuting-istio', listeners: [{
      name: 'https', hostname: host, protocol: 'HTTPS', port: 443,
      tls: { mode: 'Terminate', certificateRefs: [{ name: 'identity-broker-tls' }] },
      allowedRoutes: { namespaces: { from: 'Same' } },
    }] }, { conditions: [{ type: 'Programmed', status: 'False', observedGeneration: 1 }] }),
    route: object('HTTPRoute', { hostnames: [host], parentRefs: [{ name: 'identity-broker', sectionName: 'https' }],
      rules: [{ matches: [{ path: { type: 'PathPrefix', value: '/' } }],
        backendRefs: [{ name: 'identity-broker', port: 443 }] }] }),
    backendPolicy: object('BackendTLSPolicy', { targetRefs: [{
      name: 'identity-broker', kind: 'Service', group: '', sectionName: 'https',
    }], validation: { hostname: host, wellKnownCACertificates: 'System' } }),
  };
}
const resourceKeys = {
  defaultdomaincertificate: 'certificate', gateway: 'gateway', httproute: 'route', backendtlspolicy: 'backendPolicy',
};

test('Deployment hashing normalizes only an equal explicit deprecated ServiceAccount alias', () => {
  const desired = { kind: 'Deployment', spec: { strategy: { type: 'Recreate' },
    template: { spec: { serviceAccountName: 'identity-broker', containers: [] } } } };
  const actual = structuredClone(desired);
  actual.spec.template.spec.serviceAccount = 'identity-broker';
  assert.equal(brokerObjectHash(actual), brokerObjectHash(desired));
  for (const alias of ['foreign', '', null]) {
    actual.spec.template.spec.serviceAccount = alias;
    assert.notEqual(brokerObjectHash(actual), brokerObjectHash(desired));
  }
  actual.spec.template.spec.serviceAccount = 'identity-broker';
  delete actual.spec.template.spec.serviceAccountName;
  assert.notEqual(brokerObjectHash(actual), brokerObjectHash(desired));
});

function gatewayRouting(host = hostname) {
  const state = routing(host);
  state.gateway.metadata.namespace = 'agentweaver-v1-gateway';
  state.gateway.spec.listeners[0].allowedRoutes = { namespaces: {
    from: 'Selector', selector: { matchLabels: { 'kubernetes.io/metadata.name': namespace } },
  } };
  state.route.spec.parentRefs[0].namespace = 'agentweaver-v1-gateway';
  return state;
}

test('post-placement alignment reads the exact Gateway namespace and leaves aligned objects unchanged', () => {
  const state = gatewayRouting();
  const calls = [];
  const result = alignExistingP0BrokerHostname(config, {
    withKubeconfig: (_config, action) => action(['--kubeconfig', 'owned']),
    execKubectl(args, options) {
      calls.push(args);
      assert.ok(!args.includes('patch'));
      const resource = args[args.indexOf('get') + 1];
      const expectedNamespace = resource === 'gateway' ? 'agentweaver-v1-gateway' : namespace;
      assert.equal(args[args.indexOf('--namespace') + 1], expectedNamespace);
      return ok(JSON.stringify(options.projectJson(state[resourceKeys[resource]])));
    },
  });
  assert.equal(result.changed, false);
  assert.equal(result.gatewayNamespace, 'agentweaver-v1-gateway');
  assert.equal(result.configurationOnly, true);
  assert.ok(calls.length > 0);
  state.gateway.spec.listeners[0].allowedRoutes.namespaces.selector.matchLabels.foreign = 'allowed';
  assert.throws(() => observeExistingP0BrokerRouting(state), /configuration changed/);
});

test('existing P0 alignment replaces only hostname fields with optimistic tests and keeps programming blocked', () => {
  const state = gatewayRouting(legacyHostname);
  const before = structuredClone(state);
  const patches = [];
  const deps = {
    withKubeconfig: (config, action) => action(['--kubeconfig', 'owned']),
    execKubectl(args, options) {
      const verb = args.includes('patch') ? 'patch' : 'get';
      const resource = args[args.indexOf(verb) + 1];
      const target = state[resourceKeys[resource]];
      if (verb === 'get') return ok(JSON.stringify(options.projectJson(target)));
      const patch = JSON.parse(args[args.indexOf('--patch') + 1]);
      assert.deepEqual(patch.slice(0, 2), [
        { op: 'test', path: '/metadata/uid', value: target.metadata.uid },
        { op: 'test', path: '/metadata/resourceVersion', value: target.metadata.resourceVersion },
      ]);
      patches.push({ resource, patch });
      if (resource === 'gateway') target.spec.listeners[0].hostname = hostname;
      if (resource === 'httproute') target.spec.hostnames = [hostname];
      if (resource === 'backendtlspolicy') target.spec.validation.hostname = hostname;
      return ok();
    },
  };
  const receipt = alignExistingP0BrokerHostname(config, deps);
  assert.equal(receipt.configurationOnly, true);
  assert.equal(receipt.runtimeVerified, false);
  assert.equal(receipt.routingBlocked, true);
  assert.equal(receipt.issuer, `https://${hostname}/`);
  assert.equal(patches.length, 3);
  assert.deepEqual(state.gateway.spec.listeners[0].allowedRoutes, before.gateway.spec.listeners[0].allowedRoutes);
  assert.deepEqual(state.route.spec.rules, before.route.spec.rules);
  assert.deepEqual(state.route.spec.parentRefs, before.route.spec.parentRefs);
  assert.deepEqual(state.backendPolicy.spec.targetRefs, before.backendPolicy.spec.targetRefs);
  assert.equal(alignExistingP0BrokerHostname(config, deps).changed, false);
  assert.equal(patches.length, 3);
});

test('hostname alignment rejects changed ownership, namespace, routes, trust, and backend before writes', () => {
  for (const mutate of [
    state => { state.gateway.metadata.namespace = 'foreign'; },
    state => { state.route.metadata.labels = {}; },
    state => { state.gateway.spec.listeners[0].allowedRoutes.namespaces.from = 'All'; },
    state => { state.route.spec.rules[0].backendRefs[0].name = 'foreign'; },
    state => { state.route.spec.rules[0].filters = [{ type: 'RequestHeaderModifier' }]; },
    state => { state.backendPolicy.spec.validation.subjectAltNames = [{ type: 'Hostname', hostname: 'foreign' }]; },
    state => { state.gateway.spec.listeners[0].hostname = 'foreign.example'; },
  ]) {
    const state = gatewayRouting(legacyHostname);
    mutate(state);
    assert.throws(() => observeExistingP0BrokerRouting(state), /exact|changed|trust/);
  }
});

test('old same-name P0 Gateway parents are rejected before any hostname patch', () => {
  for (const parentNamespace of [undefined, namespace]) {
    const state = routing();
    if (parentNamespace) state.route.spec.parentRefs[0].namespace = parentNamespace;
    const calls = [];
    assert.throws(() => alignExistingP0BrokerHostname(config, {
      withKubeconfig: (_config, action) => action(['--kubeconfig', 'owned']),
      execKubectl(args, options) {
        calls.push(args);
        const resource = args[args.indexOf('get') + 1];
        assert.notEqual(resource, 'gateway');
        return ok(JSON.stringify(options.projectJson(state[resourceKeys[resource]])));
      },
    }), /exact approved Gateway namespace/);
    assert.ok(!calls.some(args => args.includes('patch')));
    assert.throws(() => observeExistingP0BrokerRouting(state), /exact approved Gateway namespace/);
  }
});

test('public acceptance client is explicit run-owned configuration, not production registration or availability', () => {
  const data = brokerAcceptanceConfiguration(config, { issuer: `https://${hostname}/`, identity: { clientId, principalId } });
  assert.equal(data.IdentityBroker__Clients__0__Type, 'Public');
  assert.equal(data.IdentityBroker__Clients__0__ClientId, `identity-acceptance-${principalId}`);
  assert.equal(data.IdentityBroker__Clients__0__RedirectUris__0, config.acceptanceRedirectUri);
  assert.ok(!Object.keys(data).some(key => key.includes('ClientSecret')));
  assert.ok(data.ConnectionStrings__IdentityBroker.endsWith('SSL Mode=VerifyFull'));
  for (const redirect of ['http://public.example:80/callback', 'https://127.0.0.1:123/callback',
    'http://127.0.0.1:123/callback?secret=value']) {
    assert.throws(() => assertBrokerRuntimeInputs({ ...config, acceptanceRedirectUri: redirect }), /loopback callback/);
  }
  const projected = projectBrokerRuntimeResource({ kind: 'ConfigMap', metadata: {}, data: {
    IdentityBroker__ExternalProvider__ClientSecret: 'PRIVATE',
    ConnectionStrings__IdentityBroker: 'Host=server;Password=PRIVATE',
  } });
  assert.ok(!JSON.stringify(projected).includes('PRIVATE'));
});

test('runtime setup creates absent native objects once and never overwrites conflicting state', () => {
  const state = gatewayRouting();
  const created = new Map();
  const calls = [];
  const rendered = [
    { apiVersion: 'v1', kind: 'ServiceAccount', metadata: { name: 'identity-broker', namespace },
      automountServiceAccountToken: true },
    { apiVersion: 'v1', kind: 'Service', metadata: { name: 'identity-broker', namespace }, spec: {
      ports: [{ port: 443, targetPort: 'https' }], type: 'ClusterIP' } },
    { apiVersion: 'cilium.io/v2', kind: 'CiliumNetworkPolicy', metadata: { name: 'identity-broker-egress', namespace },
      spec: { endpointSelector: { matchLabels: { 'agentweaver.io/workload': 'identity-broker' } } } },
    { apiVersion: 'apps/v1', kind: 'Deployment', metadata: { name: 'identity-broker', namespace }, spec: {
      strategy: { type: 'Recreate' }, template: { spec: { containers: [{ name: 'identity-broker', image: config.brokerImage }] } },
    } },
  ];
  const deps = {
    withKubeconfig: (config, action) => action(['--kubeconfig', 'owned']),
    execAz(args, options) {
      let value;
      if (args[0] === 'account') value = { id: clientId, tenantId: clientId, state: 'Enabled' };
      if (args[0] === 'aks') value = { oidcIssuerProfile: { issuerUrl: issuer } };
      if (args[0] === 'identity' && args[1] === 'show') value = { id:
        `/subscriptions/${clientId}/resourceGroups/aw-v1-p0/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-identity-broker`,
      clientId, principalId };
      if (args[0] === 'identity' && args[1] === 'federated-credential') value = {
        issuer, subject: `system:serviceaccount:${namespace}:identity-broker`, audiences: ['api://AzureADTokenExchange'],
      };
      if (args[0] === 'ad') value = { appId: clientId, publicClient: { redirectUris: [`https://${hostname}/signin-oidc`] } };
      return ok(JSON.stringify(options.projectJson(value)));
    },
    execKubectl(args, options) {
      calls.push(args);
      if (args.includes('--dry-run=client')) {
        assert.ok(!options.input.includes('CHANGEME-'));
        const kind = options.input.match(/^kind: (\w+)$/m)?.[1];
        if (kind === 'Deployment') assert.ok(options.input.includes(config.brokerImage));
        return ok(JSON.stringify(options.projectJson(rendered.find(value => value.kind === kind))));
      }
      if (args.includes('get')) {
        const resource = args[args.indexOf('get') + 1];
        const singular = resource.replace(/ies$/, 'y').replace(/s$/, '');
        const field = args[args.indexOf('--field-selector') + 1];
        const name = field.split('=')[1];
        const value = state[resourceKeys[singular]] ?? created.get(`${resource}/${name}`);
        return ok(JSON.stringify(options.projectJson({ items: value ? [value] : [] })));
      }
      assert.ok(args.includes('create'));
      const value = JSON.parse(options.input);
      value.metadata.uid = 'native-uid';
      created.set(`${value.kind}/${value.metadata.name}`, value);
      if (value.kind === 'ConfigMap') created.set(`configmaps/${value.metadata.name}`, value);
      return ok();
    },
  };
  const receipt = bootstrapIdentityBrokerRuntime(config, deps);
  assert.equal(receipt.configMapCreated, true);
  assert.equal(receipt.routingBlocked, true);
  assert.equal(receipt.runtimeVerified, false);
  assert.equal(calls.filter(args => args.includes('--dry-run=client')).length, 4);
  assert.equal(created.size, 6);
  const writes = calls.filter(args => args.includes('create') && !args.includes('--dry-run=client')).length;
  assert.equal(bootstrapIdentityBrokerRuntime(config, deps).configMapCreated, false);
  assert.equal(calls.filter(args => args.includes('create') && !args.includes('--dry-run=client')).length, writes);
  created.get('Service/identity-broker').spec.ports[0].port = 80;
  assert.throws(() => bootstrapIdentityBrokerRuntime(config, deps), /refusing replacement/);
});

test('native workload hashing ignores allocated Service addresses, not changed ports', () => {
  const source = { kind: 'Service', spec: { ports: [{ port: 443 }] } };
  assert.equal(brokerObjectHash(source), brokerObjectHash({ kind: 'Service', spec: {
    ...source.spec, clusterIP: '10.0.0.1', clusterIPs: ['10.0.0.1'], sessionAffinity: 'None',
  } }));
  assert.notEqual(brokerObjectHash(source), brokerObjectHash({ kind: 'Service', spec: { ports: [{ port: 80 }] } }));
});
