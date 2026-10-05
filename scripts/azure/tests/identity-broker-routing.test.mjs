import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { dirname } from 'node:path';
import {
  assertIdentityRoutingPlacement, bootstrapIdentityRouting, deriveIdentityRouting,
  managedBrokerHostname, planPublicBrokerCallback, verifyIdentityBrokerReadiness,
  projectIdentityRoutingReadback,
} from '../lib/identity-broker-routing.mjs';
import { run } from '../lib/exec.mjs';

const p0 = 'agentweaver-v1-p0';
const gatewayNamespace = 'agentweaver-v1-gateway';
const hostname = 'agentweaver.test.eastus2.aksapp.io';
const controller = 'istio.aks.azure.com/gateway-controller';
const clientId = '11111111-1111-1111-1111-111111111111';
const config = {
  resourceGroup: 'aw-v1-p0', clusterName: 'aw-v1-p0-aks', subscriptionId: clientId,
  repoRoot: process.cwd(), gatewayNamespace, gatewaySecurityPolicy: 'baseline', upstreamClientId: clientId,
};
const ok = stdout => ({ status: 0, stdout: stdout ?? '', stderr: '' });
const conditions = (...types) => types.map(type => ({ type, status: 'True', observedGeneration: 1 }));
function object(kind, namespace, spec, status) {
  return {
    apiVersion: kind === 'DefaultDomainCertificate'
      ? 'approuting.kubernetes.azure.com/v1alpha1' : 'gateway.networking.k8s.io/v1',
    kind, metadata: { name: 'identity-broker', namespace, uid: `${kind}-uid`, generation: 1,
      labels: { 'agentweaver.io/service': 'identity-broker' } }, spec, status,
  };
}
function snapshot() {
  return {
    gatewayCertificate: object('DefaultDomainCertificate', gatewayNamespace,
      { target: { secret: 'identity-broker-tls' } },
      { domain: '*.test.eastus2.aksapp.io', conditions: conditions('Available') }),
    brokerCertificate: object('DefaultDomainCertificate', p0,
      { target: { secret: 'identity-broker-tls' } },
      { domain: '*.test.eastus2.aksapp.io', conditions: conditions('Available') }),
    gateway: object('Gateway', gatewayNamespace, {
      gatewayClassName: 'approuting-istio',
      listeners: [{ name: 'https', hostname, port: 443, protocol: 'HTTPS',
        tls: { mode: 'Terminate', certificateRefs: [{ kind: 'Secret', name: 'identity-broker-tls' }] },
        allowedRoutes: { namespaces: { from: 'Selector',
          selector: { matchLabels: { 'kubernetes.io/metadata.name': p0 } } } } }],
    }, {
      conditions: conditions('Accepted', 'Programmed'), addresses: [{ type: 'IPAddress', value: '203.0.113.10' }],
      listeners: [{ name: 'https', conditions: conditions('Accepted', 'Programmed', 'ResolvedRefs') }],
    }),
    route: object('HTTPRoute', p0, {
      hostnames: [hostname], parentRefs: [{ name: 'identity-broker', namespace: gatewayNamespace, sectionName: 'https' }],
      rules: [{ matches: [{ path: { type: 'PathPrefix', value: '/' } }],
        backendRefs: [{ name: 'identity-broker', port: 443 }] }],
    }, { parents: [{ controllerName: controller,
      parentRef: { name: 'identity-broker', namespace: gatewayNamespace, sectionName: 'https' },
      conditions: conditions('Accepted', 'ResolvedRefs') }] }),
    backendPolicy: object('BackendTLSPolicy', p0, {
      targetRefs: [{ group: '', kind: 'Service', name: 'identity-broker', sectionName: 'https' }],
      validation: { hostname, wellKnownCACertificates: 'System' },
    }, { ancestors: [{ controllerName: controller,
      ancestorRef: { name: 'identity-broker', namespace: gatewayNamespace },
      conditions: conditions('Accepted', 'ResolvedRefs') }] }),
  };
}
function transport({ native = snapshot(), namespace, failWait = false } = {}) {
  const calls = [];
  const azureCalls = [];
  const labels = Object.fromEntries(['enforce', 'audit', 'warn']
    .map(mode => [`pod-security.kubernetes.io/${mode}`, 'restricted']));
  const dependencies = {
    execAz: args => {
      azureCalls.push(args);
      return args[0] === 'ad'
        ? ok(JSON.stringify({ appId: clientId, publicClient: { redirectUris: ['http://localhost'] } }))
        : ok();
    },
    execKubelogin: () => ok(),
    execKubectl: (args, options) => {
      calls.push({ args, options });
      const command = args.slice(args.findIndex(value => ['get', 'wait', 'apply'].includes(value)));
      if (command[0] === 'apply') return ok();
      if (command[0] === 'wait') {
        return failWait ? { status: 1, stdout: '', stderr: 'Gateway Programmed timed out' } : ok();
      }
      if (command[1] === 'namespace') {
        return command[2] === p0
          ? ok(JSON.stringify({ metadata: { name: p0, labels } }))
          : ok(namespace ? JSON.stringify(namespace) : 'null');
      }
      if (command[1] === 'gatewayclass') return ok(JSON.stringify({
        kind: 'GatewayClass', metadata: { name: 'approuting-istio', generation: 1 },
        spec: { controllerName: controller }, status: { conditions: conditions('Accepted') },
      }));
      const kind = command[1];
      const ns = command[command.indexOf('--namespace') + 1];
      const resource = kind === 'defaultdomaincertificate'
        ? (ns === p0 ? native.brokerCertificate : native.gatewayCertificate)
        : ({ gateway: native.gateway, httproute: native.route, backendtlspolicy: native.backendPolicy })[kind];
      return ok(resource ? JSON.stringify(resource) : 'null');
    },
  };
  return { calls, azureCalls, dependencies };
}

test('placement requires explicit namespace and policy without a default or a P0 downgrade', () => {
  for (const value of [{}, { gatewayNamespace }, { ...config, gatewayNamespace: p0 },
    { ...config, gatewaySecurityPolicy: 'privileged' }]) {
    assert.throws(() => assertIdentityRoutingPlacement(value), /requires explicit/);
  }
  assert.doesNotThrow(() => assertIdentityRoutingPlacement(config));
  assert.doesNotThrow(() => assertIdentityRoutingPlacement({ ...config, gatewaySecurityPolicy: 'restricted' }));
});

test('issuer and native signin callback come from the programmed exact managed HTTPRoute', () => {
  const receipt = deriveIdentityRouting(snapshot());
  assert.equal(receipt.hostname, hostname);
  assert.equal(receipt.issuer, `https://${hostname}/`);
  assert.equal(receipt.callbackUri, `https://${hostname}/signin-oidc`);
  assert.equal(receipt.runtimeVerified, false);
  assert.equal(receipt.routingConfigured, true);
  assert.deepEqual(receipt.addresses, ['203.0.113.10']);
});

test('missing, stale, unprogrammed, cross-target, or unsafe native evidence never derives a public issuer', () => {
  for (const mutate of [
    value => { value.gateway.status.conditions[1].status = 'False'; },
    value => { value.gateway.metadata.generation = 2; },
    value => { value.route.status.parents[0].conditions[0].observedGeneration = 0; },
    value => { value.gateway.status.addresses = []; },
    value => { value.gateway.spec.gatewayClassName = 'istio'; },
    value => { value.route.apiVersion = 'networking.istio.io/v1'; },
    value => { value.route.spec.hostnames = ['test.eastus2.aksapp.io']; },
    value => { value.route.spec.parentRefs[0].namespace = 'default'; },
    value => { value.gateway.spec.listeners[0].allowedRoutes.namespaces.from = 'All'; },
    value => { value.gateway.spec.listeners[0].tls.certificateRefs[0].namespace = p0; },
    value => { value.route.spec.rules[0].backendRefs[0].port = 80; },
    value => { value.route.spec.rules[0].filters = [{ type: 'RequestRedirect' }]; },
    value => { value.backendPolicy.spec.validation.hostname = 'other.test.eastus2.aksapp.io'; },
    value => { value.backendPolicy.spec.validation.wellKnownCACertificates = undefined; },
    value => { value.backendPolicy.spec.validation.subjectAltNames = [{ type: 'Hostname', hostname: 'other.example.com' }]; },
    value => { value.backendPolicy.spec.validation.subjectAltNames = [{ type: 'URI', uri: 'spiffe://other.example.com/backend' }]; },
    value => { value.backendPolicy.status.ancestors[0].controllerName = 'other-controller'; },
    value => { value.brokerCertificate.status.domain = '*.different.eastus2.aksapp.io'; },
  ]) {
    const value = snapshot();
    mutate(value);
    assert.throws(() => deriveIdentityRouting(value));
  }
  for (const domain of ['test.eastus2.aksapp.io', '*.test..aksapp.io', '*.bad-.eastus2.aksapp.io', '*.example.com']) {
    const certificate = snapshot().gatewayCertificate;
    certificate.status.domain = domain;
    assert.throws(() => managedBrokerHostname(certificate, gatewayNamespace), /AKS wildcard/);
  }
});

test('public callback planning appends without loss and becomes a no-op without application writes', () => {
  const callbackUri = deriveIdentityRouting(snapshot()).callbackUri;
  const application = { appId: clientId, publicClient: { redirectUris: ['http://localhost'] },
    web: { redirectUris: ['https://existing.example.com/callback'] } };
  const original = structuredClone(application);
  const plan = planPublicBrokerCallback(application, clientId, callbackUri);
  assert.equal(plan.action, 'append');
  assert.equal(plan.platform, 'publicClient');
  assert.equal(plan.mutationExecuted, false);
  assert.deepEqual(plan.redirectUris, ['http://localhost', callbackUri]);
  assert.deepEqual(application, original);
  application.publicClient.redirectUris.push(callbackUri);
  assert.equal(planPublicBrokerCallback(application, clientId, callbackUri).action, 'noop');
  assert.throws(() => planPublicBrokerCallback(application, 'wrong-client', callbackUri), /exact upstream/);
  assert.throws(() => planPublicBrokerCallback(application, clientId, callbackUri.replace('signin-oidc', 'auth/callback')),
    /exact upstream/);
});

test('readiness proves ordinary DNS, trusted HTTPS, and both health paths at the native Gateway IP', () => {
  const calls = [];
  const result = verifyIdentityBrokerReadiness(deriveIdentityRouting(snapshot()), {
    execCurl: args => { calls.push(args); return ok('Healthy\n200\n203.0.113.10'); },
  });
  assert.equal(result.runtimeVerified, true);
  assert.equal(result.dnsVerified, true);
  assert.equal(result.httpsVerified, true);
  assert.deepEqual(calls.map(args => args.at(-1)), [`https://${hostname}/health/live`, `https://${hostname}/health/ready`]);
  assert.ok(calls.every(args => !args.some(value => ['-k', '--insecure', '--resolve', '--location'].includes(value))));
  assert.ok(calls.every(args => args[0] === '--disable'));
  for (const result of [ok('Healthy\n200\n203.0.113.20'), ok('Redirect\n302\n203.0.113.10'),
    { status: 60, stdout: '', stderr: 'certificate validation failed' }]) {
    assert.throws(() => verifyIdentityBrokerReadiness(deriveIdentityRouting(snapshot()), {
      execCurl: () => result,
    }), /DNS\/TLS\/readiness verification failed/);
  }
});

test('production JSON projections preserve the native long DNS hostname and existing public redirect URIs', () => {
  const native = snapshot();
  const observedDomain = '*.6ac31fa7dd86cf00014876f0.eastus2euap.aksapp.io';
  const observedHost = `agentweaver.${observedDomain.slice(2)}`;
  native.gatewayCertificate.status.domain = observedDomain;
  native.brokerCertificate.status.domain = observedDomain;
  native.gateway.spec.listeners[0].hostname = observedHost;
  native.route.spec.hostnames = [observedHost];
  native.backendPolicy.spec.validation.hostname = observedHost;
  function throughProductionWrapper(value, projectJson) {
    return JSON.parse(run(process.execPath, ['-e',
      `process.stdout.write(${JSON.stringify(JSON.stringify(value))})`], {
      projectJson, preserveProjectedJson: true,
    }).stdout);
  }
  const projected = Object.fromEntries(Object.entries(native)
    .map(([key, value]) => [key, throughProductionWrapper(value, projectIdentityRoutingReadback)]));
  assert.equal(deriveIdentityRouting(projected).hostname, observedHost);
  assert.equal(throughProductionWrapper({ kind: 'List', items: [] }, projectIdentityRoutingReadback), null);
  assert.deepEqual(throughProductionWrapper({ kind: 'GatewayList', items: [native.gateway] },
    projectIdentityRoutingReadback), projected.gateway);
  assert.throws(() => projectIdentityRoutingReadback({ kind: 'List', items: [native.gateway, native.gateway] }),
    /more than one object/);
  let publicReadOptions;
  const { dependencies } = transport({ native: projected });
  const previousAzure = dependencies.execAz;
  const callbackUri = `https://${observedHost}/signin-oidc`;
  const application = { appId: clientId, publicClient: { redirectUris: ['http://localhost', callbackUri] } };
  dependencies.execAz = (args, options) => {
    if (args[0] !== 'ad') return previousAzure(args, options);
    publicReadOptions = options;
    return ok(JSON.stringify(throughProductionWrapper(application, options.projectJson)));
  };
  const receipt = bootstrapIdentityRouting(config, dependencies);
  assert.equal(typeof publicReadOptions.projectJson, 'function');
  assert.equal(publicReadOptions.preserveProjectedJson, true);
  assert.equal(receipt.publicCallbackPlan.action, 'noop');
  assert.deepEqual(receipt.publicCallbackPlan.redirectUris, application.publicClient.redirectUris);
  const withSensitiveFields = { ...native.route, metadata: {
    ...native.route.metadata, annotations: { credential: 'secret-value' },
  } };
  withSensitiveFields.spec.rules[0].filters = [{ type: 'RequestHeaderModifier',
    requestHeaderModifier: { add: [{ name: 'Authorization', value: 'secret-value' }] } }];
  assert.ok(!JSON.stringify(projectIdentityRoutingReadback(withSensitiveFields)).includes('secret-value'));
});

test('guarded bootstrap uses normal user kubeconfig, managed certificates in both namespaces, and no app mutation', () => {
  const { calls, azureCalls, dependencies } = transport();
  const receipt = bootstrapIdentityRouting(config, dependencies);
  assert.equal(receipt.runtimeVerified, false);
  assert.equal(receipt.publicCallbackPlan.action, 'append');
  assert.deepEqual(receipt.runtimeConfigInputs, {
    IdentityBroker__Issuer: `https://${hostname}/`, IdentityBroker__ExternalProvider__ClientId: clientId,
  });
  assert.equal(receipt.runtimeConfigMutationExecuted, false);
  const applies = calls.filter(call => call.args.includes('apply')).map(call => call.options.input);
  assert.equal(applies.length, 7);
  assert.ok(applies.every(text => !text.includes('CHANGEME-')));
  assert.ok(applies.some(text => text.includes('enforce: baseline')));
  assert.equal(applies.filter(text => text.includes('kind: DefaultDomainCertificate')).length, 2);
  assert.ok(applies.some(text => text.includes(`k8s:io.kubernetes.pod.namespace: ${gatewayNamespace}`)));
  assert.ok(applies.some(text => text.includes(`hostname: ${hostname}`)));
  assert.ok(azureCalls.some(args => args[0] === 'ad' && args[2] === 'show' &&
    args[args.indexOf('--subscription') + 1] === config.subscriptionId));
  assert.ok(azureCalls.every(args => !args.includes('--admin') && !args.includes('update')));
  const kubeconfig = calls[0].args[1];
  assert.ok(calls.every(call => call.args[1] === kubeconfig));
  assert.equal(existsSync(dirname(kubeconfig)), false);
});

test('bootstrap refuses an existing namespace policy change or unowned route and cleans temporary kubeconfig on failure', () => {
  const invalidNamespace = { metadata: { name: gatewayNamespace, labels: {
    'agentweaver.io/environment': 'v1-p0', 'agentweaver.io/managed-by': 'kustomize',
    'agentweaver.io/workload': 'identity-broker-gateway',
    'pod-security.kubernetes.io/enforce': 'restricted',
    'pod-security.kubernetes.io/audit': 'restricted', 'pod-security.kubernetes.io/warn': 'restricted',
  } } };
  const cases = [
    { namespace: invalidNamespace },
    { native: (() => { const value = snapshot(); value.route.metadata.labels = {}; return value; })() },
    { failWait: true },
  ];
  for (const value of cases) {
    const { calls, dependencies } = transport(value);
    assert.throws(() => bootstrapIdentityRouting(config, dependencies), /differs|not an owned|timed out/);
    assert.equal(existsSync(dirname(calls[0].args[1])), false);
    if (!value.failWait) assert.equal(calls.filter(call => call.args.includes('apply')).length, 0);
  }
});
