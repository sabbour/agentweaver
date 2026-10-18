import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { dirname } from 'node:path';
import {
  assertIdentityRoutingPlacement, bootstrapIdentityRouting, deriveIdentityRouting,
  managedBrokerHostname, planPublicBrokerCallback, verifyIdentityBrokerReadiness,
  projectIdentityRoutingReadback,
  registerPublicBrokerCallback,
} from '../lib/identity-broker-routing.mjs';
import { run } from '../lib/exec.mjs';

const p0 = 'agentweaver';
const gatewayNamespace = 'agentweaver-gateway';
const hostname = 'agentweaver.test.eastus2.aksapp.io';
const portalHostname = 'portal.test.eastus2.aksapp.io';
const controller = 'istio.aks.azure.com/gateway-controller';
const clientId = '11111111-1111-1111-1111-111111111111';
const applicationObjectId = '22222222-2222-2222-2222-222222222222';
const config = {
  resourceGroup: 'agentweaver', clusterName: 'agentweaver-aks', subscriptionId: clientId, tenantId: clientId,
  repoRoot: process.cwd(), gatewayNamespace, gatewaySecurityPolicy: 'baseline', upstreamClientId: clientId,
};
const ok = stdout => ({ status: 0, stdout: stdout ?? '', stderr: '' });
const conditions = (...types) => types.map(type => ({ type, status: 'True', observedGeneration: 1 }));
function object(kind, namespace, spec, status, { name = 'identity-broker', service = 'identity-broker' } = {}) {
  return {
    apiVersion: kind === 'DefaultDomainCertificate'
      ? 'approuting.kubernetes.azure.com/v1alpha1' : 'gateway.networking.k8s.io/v1',
    kind, metadata: { name, namespace, uid: `${kind}-${name}-uid`, generation: 1,
      labels: { 'agentweaver.io/service': service } }, spec, status,
  };
}
function networkPolicy(name, service) {
  return {
    apiVersion: 'cilium.io/v2', kind: 'CiliumNetworkPolicy',
    metadata: { name, namespace: p0, labels: { 'agentweaver.io/service': service } },
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
      listeners: ['https', 'portal'].map((name, index) => ({
        name, hostname: index === 0 ? hostname : portalHostname, port: 443, protocol: 'HTTPS',
        tls: { mode: 'Terminate', certificateRefs: [{ kind: 'Secret', name: 'identity-broker-tls' }] },
        allowedRoutes: { namespaces: { from: 'Selector',
          selector: { matchLabels: { 'kubernetes.io/metadata.name': p0 } } } },
      })),
    }, {
      conditions: conditions('Accepted', 'Programmed'), addresses: [{ type: 'IPAddress', value: '203.0.113.10' }],
      listeners: ['https', 'portal'].map(name => ({
        name, conditions: conditions('Accepted', 'Programmed', 'ResolvedRefs'),
      })),
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
    webRoute: object('HTTPRoute', p0, {
      hostnames: [portalHostname],
      parentRefs: [{ name: 'identity-broker', namespace: gatewayNamespace, sectionName: 'portal' }],
      rules: [{ matches: [{ path: { type: 'PathPrefix', value: '/' } }],
        backendRefs: [{ name: 'web', port: 8080 }] }],
    }, { parents: [{ controllerName: controller,
      parentRef: { name: 'identity-broker', namespace: gatewayNamespace, sectionName: 'portal' },
      conditions: conditions('Accepted', 'ResolvedRefs') }] }, { name: 'web-application', service: 'web' }),
    apiRoute: object('HTTPRoute', p0, {
      hostnames: [portalHostname],
      parentRefs: [{ name: 'identity-broker', namespace: gatewayNamespace, sectionName: 'portal' }],
      rules: [{ matches: [
        { path: { type: 'PathPrefix', value: '/api/v1' } },
        { path: { type: 'Exact', value: '/openapi/v1.json' } },
      ],
        backendRefs: [{ name: 'gateway', port: 8443 }] }],
    }, { parents: [{ controllerName: controller,
      parentRef: { name: 'identity-broker', namespace: gatewayNamespace, sectionName: 'portal' },
      conditions: conditions('Accepted', 'ResolvedRefs') }] }, { name: 'gateway-api', service: 'gateway' }),
    apiBackendPolicy: object('BackendTLSPolicy', p0, {
      targetRefs: [{ group: '', kind: 'Service', name: 'gateway', sectionName: 'https' }],
      validation: {
        hostname: 'gateway.agentweaver.svc.cluster.local',
        caCertificateRefs: [{ group: '', kind: 'ConfigMap', name: 'service-backend-ca' }],
      },
    }, { ancestors: [{ controllerName: controller,
      ancestorRef: { name: 'identity-broker', namespace: gatewayNamespace },
      conditions: conditions('Accepted', 'ResolvedRefs') }] }, { name: 'gateway-api', service: 'gateway' }),
    mcpRoute: object('HTTPRoute', p0, {
      hostnames: [portalHostname],
      parentRefs: [{ name: 'identity-broker', namespace: gatewayNamespace, sectionName: 'portal' }],
      rules: [{ matches: [
        { path: { type: 'Exact', value: '/mcp' } },
        { path: { type: 'Exact', value: '/.well-known/oauth-protected-resource/mcp' } },
      ],
        backendRefs: [{ name: 'mcp', port: 8443 }] }],
    }, { parents: [{ controllerName: controller,
      parentRef: { name: 'identity-broker', namespace: gatewayNamespace, sectionName: 'portal' },
      conditions: conditions('Accepted', 'ResolvedRefs') }] }, { name: 'mcp', service: 'mcp' }),
    mcpBackendPolicy: object('BackendTLSPolicy', p0, {
      targetRefs: [{ group: '', kind: 'Service', name: 'mcp', sectionName: 'https' }],
      validation: {
        hostname: 'mcp.agentweaver.svc.cluster.local',
        caCertificateRefs: [{ group: '', kind: 'ConfigMap', name: 'service-backend-ca' }],
      },
    }, { ancestors: [{ controllerName: controller,
      ancestorRef: { name: 'identity-broker', namespace: gatewayNamespace },
      conditions: conditions('Accepted', 'ResolvedRefs') }] }, { name: 'mcp', service: 'mcp' }),
    mcpService: {
      apiVersion: 'v1', kind: 'Service',
      metadata: { name: 'mcp', namespace: p0, resourceVersion: '1', labels: { 'agentweaver.io/service': 'mcp' } },
      spec: { type: 'ClusterIP', selector: { 'app.kubernetes.io/name': 'mcp' },
        ports: [{ name: 'https', port: 8443, targetPort: 'https', protocol: 'TCP' }] },
    },
  };
}
function transport({ native = snapshot(), namespace, failWait = false, deniedWrite,
  webPolicy = networkPolicy('web-gateway-ingress', 'web') } = {}) {
  const calls = [];
  const azureCalls = [];
  const policies = {
    'identity-broker-gateway-ingress': networkPolicy('identity-broker-gateway-ingress', 'identity-broker'),
    'web-gateway-ingress': webPolicy,
    'gateway-api-ingress': networkPolicy('gateway-api-ingress', 'gateway'),
    'mcp-gateway-ingress': networkPolicy('mcp-gateway-ingress', 'mcp'),
  };
  const labels = Object.fromEntries(['enforce', 'audit', 'warn']
    .map(mode => [`pod-security.kubernetes.io/${mode}`, 'restricted']));
  const dependencies = {
    execAz: args => {
      azureCalls.push(args);
      if (args[0] === 'account') return ok(JSON.stringify({
        id: config.subscriptionId, tenantId: config.tenantId, state: 'Enabled',
      }));
      return args[0] === 'ad'
        ? ok(JSON.stringify({ id: applicationObjectId, appId: clientId, publicClient: { redirectUris: ['http://localhost'] } }))
        : ok();
    },
    execKubelogin: () => ok(),
    execKubectl: (args, options) => {
      calls.push({ args, options });
      if (args.includes('auth')) {
        return deniedWrite?.(args) ? ok('no\n') : ok('yes\n');
      }
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
      const selectedName = command[2]?.startsWith('--field-selector=metadata.name=')
        ? command[2].slice('--field-selector=metadata.name='.length)
        : command[2];
      const resource = kind === 'defaultdomaincertificate'
        ? (ns === p0 ? native.brokerCertificate : native.gatewayCertificate)
        : kind === 'ciliumnetworkpolicy'
          ? policies[selectedName]
          : kind === 'httproute'
            ? ({
              'identity-broker': native.route,
              'web-application': native.webRoute,
              'gateway-api': native.apiRoute,
            mcp: native.mcpRoute,
          })[selectedName]
          : kind === 'backendtlspolicy'
            ? ({
              'identity-broker': native.backendPolicy,
              'gateway-api': native.apiBackendPolicy,
              mcp: native.mcpBackendPolicy,
            })[selectedName]
            : kind === 'service'
              ? native.mcpService
              : ({ gateway: native.gateway })[kind];
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
  assert.equal(receipt.portalHostname, portalHostname);
  assert.equal(receipt.callbackUri, `https://${hostname}/signin-oidc`);
  assert.equal(receipt.routingConfigured, true);
  assert.equal(receipt.runtimeVerified, false);
  assert.equal(receipt.routingConfigured, true);
  assert.deepEqual(receipt.addresses, ['203.0.113.10']);
});

test('omitted native policy namespace binds only to the independently verified route parent', () => {
  const native = snapshot();
  delete native.backendPolicy.status.ancestors[0].ancestorRef.namespace;
  native.backendPolicy.status.ancestors.unshift({
    controllerName: 'istio.io/mesh-controller',
    ancestorRef: { name: 'identity-broker', group: '', kind: 'Service', sectionName: 'https' },
    conditions: conditions('Accepted', 'ResolvedRefs'),
  });
  assert.equal(deriveIdentityRouting(native).hostname, hostname);
  for (const mutate of [
    value => { value.backendPolicy.status.ancestors[1].ancestorRef.namespace = p0; },
    value => { value.backendPolicy.status.ancestors[1].ancestorRef.name = 'foreign'; },
    value => { value.backendPolicy.status.ancestors[1].ancestorRef.sectionName = 'foreign'; },
    value => { value.backendPolicy.status.ancestors[1].controllerName = 'foreign'; },
    value => { value.backendPolicy.status.ancestors[1].conditions[0].observedGeneration = 0; },
    value => { value.route.status.parents[0].parentRef.namespace = p0; },
    value => { value.gateway.metadata.namespace = p0; value.gateway.status.conditions[1].status = 'False'; },
    value => { value.backendPolicy.status.ancestors.push(structuredClone(value.backendPolicy.status.ancestors[1])); },
  ]) {
    const changed = structuredClone(native);
    mutate(changed);
    assert.throws(() => deriveIdentityRouting(changed));
  }
});

test('routing checks all required writes before applying the first manifest', () => {
  const { calls, dependencies } = transport({
    deniedWrite: args => args.includes('gateways') && args.includes('patch'),
  });
  assert.throws(() => bootstrapIdentityRouting(config, dependencies),
    /Kubernetes API access is unavailable or insufficient for Identity routing bootstrap: required permission "patch gateways" in namespace agentweaver-gateway/);
  assert.equal(calls.filter(call => call.args.includes('apply')).length, 0);
});

test('routing waits for policy current-generation evidence before final strict derivation', () => {
  const native = snapshot();
  native.backendPolicy.metadata.generation = 2;
  const fixture = transport({ native });
  const original = fixture.dependencies.execKubectl;
  fixture.dependencies.execKubectl = (args, options) => {
    if (args.includes('wait') && args.includes('backendtlspolicy/identity-broker') &&
        args.some(value => value.includes('observedGeneration'))) {
      for (const condition of native.backendPolicy.status.ancestors[0].conditions) {
        condition.observedGeneration = 2;
      }
    }
    return original(args, options);
  };
  assert.equal(bootstrapIdentityRouting(config, fixture.dependencies).routingConfigured, true);
  assert.equal(fixture.calls.filter(call => call.args.includes('backendtlspolicy/identity-broker') &&
    call.args.some(value => value.includes('observedGeneration'))).length, 2);

  const blocked = transport();
  const previous = blocked.dependencies.execKubectl;
  blocked.dependencies.execKubectl = (args, options) =>
    args.includes('backendtlspolicy/identity-broker') && args.some(value => value.includes('observedGeneration'))
      ? { status: 1, stdout: '', stderr: 'Native policy current-generation convergence timed out' }
      : previous(args, options);
  assert.throws(() => bootstrapIdentityRouting(config, blocked.dependencies),
    /Native policy current-generation convergence timed out/);
});

test('missing, stale, unprogrammed, cross-target, or unsafe native evidence never derives a public issuer', () => {
  for (const mutate of [
    value => { value.gateway.status.conditions[1].status = 'False'; },
    value => { value.gateway.metadata.generation = 2; },
    value => { value.route.status.parents[0].conditions[0].observedGeneration = 0; },
    value => { value.gateway.status.addresses = []; },
    value => { value.gateway.spec.gatewayClassName = 'istio'; },
    value => { value.gateway.spec.listeners[1].hostname = hostname; },
    value => { value.route.apiVersion = 'networking.istio.io/v1'; },
    value => { value.route.spec.hostnames = ['test.eastus2.aksapp.io']; },
    value => { value.route.spec.parentRefs[0].namespace = 'default'; },
    value => { value.gateway.spec.listeners[0].allowedRoutes.namespaces.from = 'All'; },
    value => { value.gateway.spec.listeners[0].tls.certificateRefs[0].namespace = p0; },
    value => { value.route.spec.rules[0].backendRefs[0].port = 80; },
    value => { value.route.spec.rules[0].filters = [{ type: 'RequestRedirect' }]; },
    value => { value.webRoute.spec.hostnames = [hostname]; },
    value => { value.webRoute.spec.rules[0].backendRefs[0].port = 443; },
    value => { value.apiRoute.spec.rules[0].matches[0].path.value = '/'; },
    value => { value.apiRoute.spec.rules[0].matches[1].path.value = '/api/v1'; },
    value => { value.apiRoute.spec.rules[0].backendRefs[0].name = 'web'; },
    value => { value.apiBackendPolicy.spec.validation.hostname = portalHostname; },
    value => { value.mcpBackendPolicy.spec.validation.hostname = portalHostname; },
    value => { value.mcpBackendPolicy.spec.validation.caCertificateRefs = [{ name: 'untrusted-ca' }]; },
    value => { value.mcpRoute.spec.rules[0].matches[0].path.value = '/'; },
    value => { value.mcpRoute.spec.rules[0].matches[1].path.value = '/.well-known/oauth-protected-resource'; },
    value => { value.mcpRoute.spec.rules[0].backendRefs[0].name = 'gateway'; },
    value => { value.mcpBackendPolicy.spec.validation.hostname = portalHostname; },
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
  const application = { id: applicationObjectId, appId: clientId, publicClient: { redirectUris: ['http://localhost'] },
    web: { redirectUris: ['https://existing.example.com/callback'] } };
  const original = structuredClone(application);
  const plan = planPublicBrokerCallback(application, clientId, callbackUri, clientId);
  assert.equal(plan.action, 'append');
  assert.equal(plan.platform, 'publicClient');
  assert.equal(plan.mutationExecuted, false);
  assert.deepEqual(plan.redirectUris, ['http://localhost', callbackUri]);
  assert.deepEqual(application, original);
  application.publicClient.redirectUris.push(callbackUri);
  assert.equal(planPublicBrokerCallback(application, clientId, callbackUri, clientId).action, 'noop');
  assert.throws(() => planPublicBrokerCallback(application, 'wrong-client', callbackUri, clientId), /exact upstream/);
  assert.throws(() => planPublicBrokerCallback(application, clientId, callbackUri.replace('signin-oidc', 'auth/callback'), clientId),
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
  const observedDomain = '*.0123456789abcdef.eastus2euap.aksapp.io';
  const observedHost = `agentweaver.${observedDomain.slice(2)}`;
  native.gatewayCertificate.status.domain = observedDomain;
  native.brokerCertificate.status.domain = observedDomain;
  native.gateway.spec.listeners[0].hostname = observedHost;
  native.gateway.spec.listeners[1].hostname = `portal.${observedDomain.slice(2)}`;
  native.route.spec.hostnames = [observedHost];
  native.backendPolicy.spec.validation.hostname = observedHost;
  native.webRoute.spec.hostnames = [`portal.${observedDomain.slice(2)}`];
  native.apiRoute.spec.hostnames = [`portal.${observedDomain.slice(2)}`];
  native.mcpRoute.spec.hostnames = [`portal.${observedDomain.slice(2)}`];
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
  const application = { id: applicationObjectId, appId: clientId, publicClient: { redirectUris: ['http://localhost', callbackUri] } };
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

test('explicit confirmed callback registration fresh-reads and patches only public redirects, preserving legacy platforms', () => {
  const callbackUri = deriveIdentityRouting(snapshot()).callbackUri;
  const application = {
    id: applicationObjectId, appId: clientId, publicClient: { redirectUris: ['http://localhost'] },
    web: { redirectUris: ['https://legacy.example.com/callback'] }, isFallbackPublicClient: true,
    spa: { redirectUris: ['https://spa.example.com'] }, appRoles: [{ id: 'existing-role' }],
  };
  const original = structuredClone(application);
  const plan = planPublicBrokerCallback(application, clientId, callbackUri, clientId);
  application.publicClient.redirectUris.push('http://localhost/newly-added');
  const calls = [];
  const execAz = args => {
    calls.push(args);
    if (args[0] === 'account') return ok(JSON.stringify({ id: config.subscriptionId, tenantId: config.tenantId }));
    if (args[0] === 'ad') return ok(JSON.stringify(application));
    const patch = JSON.parse(args[args.indexOf('--body') + 1]);
    assert.deepEqual(Object.keys(patch), ['publicClient']);
    assert.deepEqual(Object.keys(patch.publicClient), ['redirectUris']);
    assert.equal(args[args.indexOf('--method') + 1], 'PATCH');
    assert.equal(args[args.indexOf('--uri') + 1], `https://graph.microsoft.com/v1.0/applications/${applicationObjectId}`);
    assert.equal(args[args.indexOf('--subscription') + 1], config.subscriptionId);
    application.publicClient = patch.publicClient;
    return ok();
  };
  const result = registerPublicBrokerCallback(plan, { ...config,
    registerBrokerCallback: true, confirmBrokerCallback: callbackUri,
  }, { execAz });
  assert.equal(result.mutationExecuted, true);
  assert.equal(result.action, 'append');
  assert.deepEqual(result.redirectUris, ['http://localhost', 'http://localhost/newly-added', callbackUri]);
  for (const field of ['web', 'spa', 'appRoles', 'isFallbackPublicClient']) {
    assert.deepEqual(application[field], original[field]);
  }
  assert.deepEqual(calls.map(args => args[0]), ['account', 'ad', 'rest', 'ad']);
  const again = registerPublicBrokerCallback(result, { ...config,
    registerBrokerCallback: true, confirmBrokerCallback: callbackUri,
  }, { execAz });
  assert.equal(again.action, 'noop');
  assert.equal(again.mutationExecuted, false);
  assert.equal(calls.filter(args => args[0] === 'rest').length, 1);
});

test('callback registration refuses absent confirmation, changed account/app, and preserves native permission failure', () => {
  const callbackUri = deriveIdentityRouting(snapshot()).callbackUri;
  const application = { id: applicationObjectId, appId: clientId, publicClient: { redirectUris: [] } };
  const plan = planPublicBrokerCallback(application, clientId, callbackUri, clientId);
  assert.equal(registerPublicBrokerCallback(plan, config, {
    execAz: () => { throw new Error('unexpected Azure call'); },
  }), plan);
  for (const flags of [{ registerBrokerCallback: true }, { confirmBrokerCallback: callbackUri },
    { registerBrokerCallback: true, confirmBrokerCallback: 'https://wrong.example.com/signin-oidc' }]) {
    assert.throws(() => registerPublicBrokerCallback(plan, { ...config, ...flags }, {
      execAz: () => { throw new Error('unexpected Azure call'); },
    }), /confirmation|confirmation requires/);
  }
  for (const changed of [
    { tenantId: applicationObjectId }, { id: applicationObjectId },
    { appId: applicationObjectId }, { objectId: clientId },
  ]) {
    let writes = 0;
    assert.throws(() => registerPublicBrokerCallback(plan, {
      ...config, registerBrokerCallback: true, confirmBrokerCallback: callbackUri,
    }, { execAz: args => {
      if (args[0] === 'account') return ok(JSON.stringify({
        id: changed.id ?? config.subscriptionId, tenantId: changed.tenantId ?? config.tenantId,
      }));
      if (args[0] === 'ad') return ok(JSON.stringify({
        ...application, id: changed.objectId ?? applicationObjectId, appId: changed.appId ?? clientId,
      }));
      writes += 1;
      return ok();
    } }), /changed|exact upstream/);
    assert.equal(writes, 0);
  }
  assert.throws(() => registerPublicBrokerCallback(plan, {
    ...config, registerBrokerCallback: true, confirmBrokerCallback: callbackUri,
  }, { execAz: args => {
    if (args[0] === 'account') return ok(JSON.stringify({ id: config.subscriptionId, tenantId: config.tenantId }));
    if (args[0] === 'ad') return ok(JSON.stringify(application));
    return run(process.execPath, ['-e',
      'process.stderr.write("403 Authorization_RequestDenied password=synthetic-sensitive-fixture-value");process.exit(1)'],
    { check: false, projectJson: value => value });
  } }), error => error.message.includes('403 Authorization_RequestDenied') &&
    !error.message.includes('synthetic-sensitive-fixture-value'));
});

test('guarded bootstrap uses normal user kubeconfig, managed certificates in both namespaces, and no app mutation', () => {
  const { calls, azureCalls, dependencies } = transport();
  const receipt = bootstrapIdentityRouting(config, dependencies);
  assert.ok(azureCalls.filter(args => args[0] === 'ad').every(args => !args.includes('--subscription')));
  assert.equal(receipt.runtimeVerified, false);
  assert.equal(receipt.publicCallbackPlan.action, 'append');
  assert.deepEqual(receipt.runtimeConfigInputs, {
    IdentityBroker__Issuer: `https://${hostname}/`, IdentityBroker__ExternalProvider__ClientId: clientId,
  });
  assert.equal(receipt.runtimeConfigMutationExecuted, false);
  const applies = calls.filter(call => call.args.includes('apply')).map(call => call.options.input);
  assert.equal(applies.length, 15);
  assert.ok(applies.every(text => !text.includes('CHANGEME-')));
  assert.ok(applies.some(text => text.includes('enforce: baseline')));
  assert.equal(applies.filter(text => text.includes('kind: DefaultDomainCertificate')).length, 2);
  assert.ok(applies.some(text => text.includes(`k8s:io.kubernetes.pod.namespace: ${gatewayNamespace}`)));
  assert.ok(applies.some(text => text.includes(`hostname: ${hostname}`)));
  const webPolicy = applies.find(text => text.includes('name: web-gateway-ingress'));
  assert.ok(webPolicy);
  assert.ok(webPolicy.includes('agentweaver.io/service: web'));
  assert.ok(webPolicy.includes('agentweaver.io/workload: web'));
  assert.ok(webPolicy.includes(`k8s:io.kubernetes.pod.namespace: ${gatewayNamespace}`));
  assert.ok(webPolicy.includes('port: "8080"'));
  assert.ok(!webPolicy.includes('CHANGEME-'));
  const webRoute = applies.find(text => text.includes('name: web-application'));
  assert.ok(webRoute);
  assert.ok(webRoute.includes(`- ${portalHostname}`));
  assert.match(webRoute, /backendRefs:\s+- name: web\s+port: 8080/);
  const apiRoute = applies.find(text => text.includes('kind: HTTPRoute') && text.includes('name: gateway-api'));
  assert.ok(apiRoute);
  assert.ok(apiRoute.includes(`- ${portalHostname}`));
  assert.match(apiRoute, /value: \/api\/v1/);
  assert.match(apiRoute, /type: Exact\s+value: \/openapi\/v1\.json/);
  assert.match(apiRoute, /backendRefs:\s+- name: gateway\s+port: 8443/);
  assert.ok(applies.some(text => text.includes('name: gateway-api-ingress') &&
    text.includes('agentweaver.io/workload: gateway') && text.includes('port: "8443"')));
  const mcpRoute = applies.find(text => text.includes('kind: HTTPRoute') && text.includes('name: mcp'));
  assert.ok(mcpRoute);
  assert.ok(mcpRoute.includes(`- ${portalHostname}`));
  assert.match(mcpRoute, /type: Exact\s+value: \/mcp/);
  assert.match(mcpRoute, /type: Exact\s+value: \/.well-known\/oauth-protected-resource\/mcp/);
  assert.match(mcpRoute, /backendRefs:\s+- name: mcp\s+port: 8443/);
  const mcpBackendPolicy = applies.find(text => text.includes('kind: BackendTLSPolicy') && text.includes('name: mcp'));
  assert.ok(mcpBackendPolicy);
  assert.match(mcpBackendPolicy, /hostname: mcp\.agentweaver\.svc\.cluster\.local/);
  assert.match(mcpBackendPolicy, /caCertificateRefs:\s+- group: ""\s+kind: ConfigMap\s+name: service-backend-ca/);
  assert.ok(applies.some(text => text.includes('name: mcp-gateway-ingress') &&
    text.includes(`k8s:io.kubernetes.pod.namespace: ${gatewayNamespace}`) &&
    text.includes('k8s:gateway.networking.k8s.io/gateway-name: identity-broker')));
  assert.ok(azureCalls.some(args => args[0] === 'ad' && args[2] === 'show' &&
    args[args.indexOf('--id') + 1] === config.upstreamClientId && !args.includes('--subscription')));
  assert.ok(azureCalls.every(args => !args.includes('--admin') && !args.includes('update')));
  const kubeconfig = calls[0].args[1];
  assert.ok(calls.every(call => call.args[1] === kubeconfig));
  assert.equal(existsSync(dirname(kubeconfig)), false);
});

test('routing validates the selected account before subscription-free directory reads or Kubernetes changes', () => {
  for (const selected of [
    { id: applicationObjectId, tenantId: config.tenantId, state: 'Enabled' },
    { id: config.subscriptionId, tenantId: applicationObjectId, state: 'Enabled' },
    { id: config.subscriptionId, tenantId: config.tenantId, state: 'Disabled' },
  ]) {
    const native = transport();
    const original = native.dependencies.execAz;
    native.dependencies.execAz = args => args[0] === 'account'
      ? ok(JSON.stringify(selected)) : original(args);
    assert.throws(() => bootstrapIdentityRouting(config, native.dependencies), /selected account\/tenant changed/);
    assert.equal(native.calls.length, 0);
    assert.equal(native.azureCalls.length, 0);
  }
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
    { webPolicy: networkPolicy('web-gateway-ingress', 'identity-broker') },
    { native: (() => { const value = snapshot(); value.apiRoute.metadata.labels = {}; return value; })() },
    { native: (() => { const value = snapshot(); value.mcpService.spec.selector['app.kubernetes.io/name'] = 'gateway'; return value; })() },
    { failWait: true },
  ];
  for (const value of cases) {
    const { calls, dependencies } = transport(value);
    assert.throws(() => bootstrapIdentityRouting(config, dependencies),
      /differs|not an owned|not the exact private|timed out/);
    assert.equal(existsSync(dirname(calls[0].args[1])), false);
    if (!value.failWait) assert.equal(calls.filter(call => call.args.includes('apply')).length, 0);
  }
});
