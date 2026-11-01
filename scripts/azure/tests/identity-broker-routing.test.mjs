import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { dirname } from 'node:path';
import { parse, stringify } from 'yaml';
import {
  assertIdentityRoutingPlacement, bootstrapIdentityRouting, deriveIdentityRouting,
  managedBrokerHostname, managedBrokerHostnameFromDomain, planPublicBrokerCallback,
  preflightPublicBrokerRegistration, verifyIdentityBrokerReadiness,
  projectIdentityRoutingReadback,
  registerPublicBrokerCallback,
} from '../lib/identity-broker-routing.mjs';
import { run } from '../lib/exec.mjs';

const p0 = 'agentweaver';
const gatewayNamespace = 'agentweaver-gateway';
const hostname = 'agentweaver.test.eastus2.aksapp.io';
const authHostname = 'auth.test.eastus2.aksapp.io';
const apiHostname = 'api.test.eastus2.aksapp.io';
const portalHostname = 'portal.test.eastus2.aksapp.io';
const wildcardHostname = '*.test.eastus2.aksapp.io';
const controller = 'istio.aks.azure.com/gateway-controller';
const clientId = '11111111-1111-1111-1111-111111111111';
const applicationObjectId = '22222222-2222-2222-2222-222222222222';
const config = {
  resourceGroup: 'agentweaver', clusterName: 'agentweaver-aks', subscriptionId: clientId, tenantId: clientId,
  repoRoot: process.cwd(), gatewayNamespace, gatewaySecurityPolicy: 'baseline', upstreamClientId: clientId,
};
const ok = stdout => ({ status: 0, stdout: stdout ?? '', stderr: '' });
const conditions = (...types) => types.map(type => ({ type, status: 'True', observedGeneration: 1 }));
function kubectlCommand(args) {
  if (args.includes('auth')) return [];
  const index = args.findIndex(value => ['get', 'wait', 'apply', 'create', 'patch'].includes(value));
  return index < 0 ? [] : args.slice(index);
}
function writtenResource(call) {
  const command = kubectlCommand(call.args);
  if (command[0] === 'create') return parse(call.options.input);
  const patch = JSON.parse(command[command.indexOf('--patch') + 1]);
  const kind = {
    backendtlspolicy: 'BackendTLSPolicy',
    ciliumnetworkpolicy: 'CiliumNetworkPolicy',
    defaultdomaincertificate: 'DefaultDomainCertificate',
    gateway: 'Gateway',
    httproute: 'HTTPRoute',
  }[command[1]];
  return {
    kind,
    metadata: {
      name: command[2],
      namespace: command[command.indexOf('--namespace') + 1],
      labels: Object.fromEntries(patch.filter(operation => operation.path.startsWith('/metadata/labels/'))
        .map(operation => [operation.path.slice('/metadata/labels/'.length)
          .replaceAll('~1', '/').replaceAll('~0', '~'), operation.value])),
    },
    spec: patch.find(operation => operation.path === '/spec').value,
  };
}
function writtenText(call) {
  const command = kubectlCommand(call.args);
  return command[0] === 'create' ? call.options.input : stringify(writtenResource(call));
}
function object(kind, namespace, spec, status, { name = 'identity-broker', service = 'identity-broker' } = {}) {
  return {
    apiVersion: kind === 'DefaultDomainCertificate'
      ? 'approuting.kubernetes.azure.com/v1alpha1' : 'gateway.networking.k8s.io/v1',
    kind, metadata: { name, namespace, uid: `${kind}-${name}-uid`, resourceVersion: '1', generation: 1,
      labels: { 'agentweaver.io/service': service } }, spec, status,
  };
}
function networkPolicy(name, service) {
  return {
    apiVersion: 'cilium.io/v2', kind: 'CiliumNetworkPolicy',
    metadata: { name, namespace: p0, uid: `CiliumNetworkPolicy-${name}-uid`, resourceVersion: '1',
      labels: { 'agentweaver.io/service': service } },
  };
}
function managedListener(name, host) {
  return {
    name, hostname: host, port: 443, protocol: 'HTTPS',
    tls: { mode: 'Terminate', certificateRefs: [{ kind: 'Secret', name: 'identity-broker-tls' }] },
    allowedRoutes: { namespaces: { from: 'Selector',
      selector: { matchLabels: { 'kubernetes.io/metadata.name': p0 } } } },
  };
}
function managedRoute(name, service, host, sectionName, paths, backend, port) {
  return object('HTTPRoute', p0, {
    hostnames: [host],
    parentRefs: [{ name: 'identity-broker', namespace: gatewayNamespace, sectionName }],
    rules: [{ matches: paths.map(({ type, value }) => ({ path: { type, value } })),
      backendRefs: [{ name: backend, port }] }],
  }, { parents: [{ controllerName: controller,
    parentRef: { name: 'identity-broker', namespace: gatewayNamespace, sectionName },
    conditions: conditions('Accepted', 'ResolvedRefs') }] }, { name, service });
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
      listeners: [
        managedListener('https', wildcardHostname),
        managedListener('auth', authHostname),
        managedListener('api', apiHostname),
      ],
    }, {
      conditions: conditions('Accepted', 'Programmed'), addresses: [{ type: 'IPAddress', value: '203.0.113.10' }],
      listeners: ['https', 'auth', 'api'].map(name => ({
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
    authRoute: managedRoute('identity-broker-auth', 'identity-broker', authHostname, 'auth',
      [{ type: 'PathPrefix', value: '/' }], 'identity-broker', 443),
    webRoute: object('HTTPRoute', p0, {
      hostnames: [portalHostname],
      parentRefs: [{ name: 'identity-broker', namespace: gatewayNamespace, sectionName: 'https' }],
      rules: [{ matches: [{ path: { type: 'PathPrefix', value: '/' } }],
        backendRefs: [{ name: 'web', port: 8080 }] }],
    }, { parents: [{ controllerName: controller,
      parentRef: { name: 'identity-broker', namespace: gatewayNamespace, sectionName: 'https' },
      conditions: conditions('Accepted', 'ResolvedRefs') }] }, { name: 'web-application', service: 'web' }),
    apiRoute: object('HTTPRoute', p0, {
      hostnames: [portalHostname],
      parentRefs: [{ name: 'identity-broker', namespace: gatewayNamespace, sectionName: 'https' }],
      rules: [{ matches: [
        { path: { type: 'PathPrefix', value: '/api/v1' } },
        { path: { type: 'Exact', value: '/openapi/v1.json' } },
      ],
        backendRefs: [{ name: 'gateway', port: 8443 }] }],
    }, { parents: [{ controllerName: controller,
      parentRef: { name: 'identity-broker', namespace: gatewayNamespace, sectionName: 'https' },
      conditions: conditions('Accepted', 'ResolvedRefs') }] }, { name: 'gateway-api', service: 'gateway' }),
    apiHostRoute: managedRoute('gateway-api-host', 'gateway', apiHostname, 'api', [
      { type: 'PathPrefix', value: '/api/v1' },
      { type: 'PathPrefix', value: '/api/connections/copilot-user/v1' },
      { type: 'PathPrefix', value: '/api/connections/remote-mcp/v1' },
      { type: 'PathPrefix', value: '/api/auth/github/repo-app' },
      { type: 'Exact', value: '/openapi/v1.json' },
    ], 'gateway', 8443),
    copilotBffRoute: managedRoute('gateway-copilot-bff', 'gateway', portalHostname, 'https',
      [{ type: 'PathPrefix', value: '/api/connections/copilot-user/v1' }], 'gateway', 8443),
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
      parentRefs: [{ name: 'identity-broker', namespace: gatewayNamespace, sectionName: 'https' }],
      rules: [{ matches: [
        { path: { type: 'Exact', value: '/mcp' } },
        { path: { type: 'Exact', value: '/.well-known/oauth-protected-resource/mcp' } },
      ],
        backendRefs: [{ name: 'mcp', port: 8443 }] }],
    }, { parents: [{ controllerName: controller,
      parentRef: { name: 'identity-broker', namespace: gatewayNamespace, sectionName: 'https' },
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
function transport({ native: nativeFixture = snapshot(), namespace, failWait = false, deniedWrite,
  certificateOnlyFresh = false,
  webPolicy = networkPolicy('web-gateway-ingress', 'web') } = {}) {
  const native = nativeFixture;
  if (certificateOnlyFresh) {
    native.gatewayCertificate = null;
    native.brokerCertificate = null;
    native.mcpService = null;
  }
  const issuedCertificates = snapshot();
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
    execAz: (args, options = {}) => {
      azureCalls.push(args);
      const projected = value => options.projectJson ? options.projectJson(value) : value;
      if (args[0] === 'account') return ok(JSON.stringify(projected({
        id: config.subscriptionId, tenantId: config.tenantId, state: 'Enabled',
      })));
      if (args[0] === 'aks' && args[1] === 'show') return ok(JSON.stringify(projected({
        id: `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}` +
          `/providers/Microsoft.ContainerService/managedClusters/${config.clusterName}`,
        ingressProfile: { webAppRouting: { defaultDomain: {
          enabled: true, domainName: hostname.slice('agentweaver.'.length),
        } } },
      })));
      return args[0] === 'ad'
        ? ok(JSON.stringify(projected({
          id: applicationObjectId, appId: clientId, signInAudience: 'AzureADMyOrg',
          isFallbackPublicClient: true,
          publicClient: { redirectUris: ['http://localhost', `https://${hostname}/signin-oidc`] },
        })))
        : ok();
    },
    execKubelogin: () => ok(),
    execKubectl: (args, options) => {
      calls.push({ args, options });
      if (args.includes('auth')) {
        return deniedWrite?.(args) ? ok('no\n') : ok('yes\n');
      }
      const command = kubectlCommand(args);
      if (command[0] === 'create') {
        const resource = parse(options.input);
        if (certificateOnlyFresh && resource.kind === 'DefaultDomainCertificate') {
          native[resource.metadata.namespace === gatewayNamespace ? 'gatewayCertificate' : 'brokerCertificate'] =
            issuedCertificates[resource.metadata.namespace === gatewayNamespace ? 'gatewayCertificate' : 'brokerCertificate'];
        }
        return ok();
      }
      if (command[0] === 'patch') {
        const patch = JSON.parse(command[command.indexOf('--patch') + 1]);
        const namespace = command[command.indexOf('--namespace') + 1];
        const resource = [...Object.values(native), ...Object.values(policies)]
          .find(value => value?.kind?.toLowerCase() === command[1] &&
            value.metadata?.name === command[2] && value.metadata?.namespace === namespace);
        if (!resource || patch.some(operation => operation.op === 'test' &&
            (operation.path === '/metadata/uid' && operation.value !== resource.metadata.uid ||
             operation.path === '/metadata/resourceVersion' && operation.value !== resource.metadata.resourceVersion))) {
          return { status: 1, stdout: '', stderr: 'Conflict: routing object changed after inventory' };
        }
        for (const operation of patch) {
          if (operation.path === '/spec') resource.spec = operation.value;
          if (operation.path.startsWith('/metadata/labels/')) {
            const key = operation.path.slice('/metadata/labels/'.length).replaceAll('~1', '/').replaceAll('~0', '~');
            resource.metadata.labels[key] = operation.value;
          }
        }
        return ok();
      }
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
              'identity-broker-auth': native.authRoute,
              'web-application': native.webRoute,
              'gateway-api': native.apiRoute,
              'gateway-api-host': native.apiHostRoute,
              'gateway-copilot-bff': native.copilotBffRoute,
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
  const native = snapshot();
  const receipt = deriveIdentityRouting(native);
  assert.equal(receipt.hostname, hostname);
  assert.equal(receipt.authHostname, authHostname);
  assert.equal(receipt.apiHostname, apiHostname);
  assert.equal(receipt.issuer, `https://${hostname}/`);
  assert.equal(receipt.portalHostname, portalHostname);
  assert.equal(receipt.callbackUri, `https://${hostname}/signin-oidc`);
  assert.equal(receipt.routingConfigured, true);
  assert.equal(receipt.runtimeVerified, false);
  assert.equal(receipt.routingConfigured, true);
  assert.deepEqual(receipt.addresses, ['203.0.113.10']);
  assert.deepEqual(native.gateway.spec.listeners.map(({ name, hostname: listenerHostname }) =>
    ({ name, hostname: listenerHostname })), [
    { name: 'https', hostname: wildcardHostname },
    { name: 'auth', hostname: authHostname },
    { name: 'api', hostname: apiHostname },
  ]);
  for (const [route, routeHostname, sectionName] of [
    [native.route, hostname], [native.webRoute, portalHostname],
    [native.authRoute, authHostname, 'auth'], [native.apiRoute, portalHostname, 'https'],
    [native.apiHostRoute, apiHostname, 'api'], [native.copilotBffRoute, portalHostname, 'https'],
    [native.mcpRoute, portalHostname, 'https'],
  ]) {
    assert.deepEqual(route.spec.hostnames, [routeHostname]);
    assert.equal(route.spec.parentRefs[0].sectionName, sectionName ?? 'https');
  }
});

test('first-install preflight derives the callback from AKS before certificate or route resources exist', () => {
  const callbackUri = `https://${hostname}/signin-oidc`;
  const application = {
    id: applicationObjectId, appId: clientId, signInAudience: 'AzureADMyOrg',
    isFallbackPublicClient: true, publicClient: { redirectUris: ['http://localhost'] },
  };
  const calls = [];
  const execAz = (args, options) => {
    calls.push(args);
    if (args[0] === 'account') return ok(JSON.stringify({
      id: config.subscriptionId, tenantId: config.tenantId, state: 'Enabled',
    }));
    if (args[0] === 'aks') return ok(JSON.stringify(options.projectJson({
      id: `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}` +
        `/providers/Microsoft.ContainerService/managedClusters/${config.clusterName}`,
      ingressProfile: { webAppRouting: { defaultDomain: {
        enabled: true, domainName: hostname.slice('agentweaver.'.length),
      } } },
    })));
    if (args[0] === 'ad') return ok(JSON.stringify(options.projectJson(application)));
    throw new Error(`unexpected Azure command: ${args.join(' ')}`);
  };
  assert.throws(() => preflightPublicBrokerRegistration(config, { execAz }),
    error => error.message.includes(callbackUri) &&
      error.message.includes('--identity-routing-certificates-only') &&
      error.message.includes('--register-identity-broker-callback') &&
      error.message.includes(`--confirm-identity-broker-callback "${callbackUri}"`));
  assert.ok(calls.some(args => args[0] === 'aks'));
  assert.ok(calls.some(args => args[0] === 'ad'));
  assert.ok(calls.every(args => args[0] !== 'rest'));
  assert.equal(managedBrokerHostnameFromDomain('test.eastus2.aksapp.io'), hostname);
});

test('existing-app preflight is read-only when tenant, client modes, and exact callback are ready', () => {
  const { azureCalls, dependencies } = transport();
  const plan = preflightPublicBrokerRegistration(config, dependencies);
  assert.equal(plan.registrationReady, true);
  assert.equal(plan.callbackUri, `https://${hostname}/signin-oidc`);
  assert.equal(plan.mutationExecuted, false);
  assert.ok(azureCalls.some(args => args[0] === 'account'));
  assert.ok(azureCalls.some(args => args[0] === 'aks'));
  assert.ok(azureCalls.some(args => args[0] === 'ad'));
  assert.ok(azureCalls.every(args => args[0] !== 'rest'));
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
  assert.equal(calls.filter(call => ['apply', 'create', 'patch'].includes(kubectlCommand(call.args)[0])).length, 0);
});

test('fresh certificate-only bootstrap works before identity-broker-tls and Services exist', () => {
  const { calls, dependencies } = transport({ certificateOnlyFresh: true });
  const receipt = bootstrapIdentityRouting({
    ...config, identityRoutingCertificatesOnly: true,
  }, dependencies);

  assert.equal(receipt.certificatesConfigured, true);
  assert.equal(receipt.routingConfigured, false);
  assert.equal(receipt.runtimeVerified, false);
  assert.equal(receipt.hostname, hostname);
  const applies = calls.filter(call => ['create', 'patch'].includes(kubectlCommand(call.args)[0]));
  const rendered = applies.map(writtenText);
  assert.equal(applies.length, 3);
  assert.equal(rendered.filter(text => text.includes('kind: DefaultDomainCertificate')).length, 2);
  assert.equal(calls.some(call => call.args.includes('service')), false);
  assert.ok(!rendered.some(text => /kind: (?:Gateway|HTTPRoute|BackendTLSPolicy|CiliumNetworkPolicy)/.test(text)));
  assert.equal(calls.filter(call => call.args.includes('wait') &&
    call.args.some(value => value.startsWith('--for=condition=Available'))).length, 2);
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
    value => { value.gateway.spec.listeners[0].hostname = hostname; },
    value => { value.gateway.spec.listeners.pop(); },
    value => { value.gateway.spec.listeners[1].hostname = portalHostname; },
    value => { value.authRoute.spec.parentRefs[0].sectionName = 'https'; },
    value => { value.apiHostRoute.spec.rules[0].matches[1].path.value = '/'; },
    value => { value.copilotBffRoute.spec.hostnames = [apiHostname]; },
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
    value => { value.mcpRoute.spec.hostnames = ['unknown.test.eastus2.aksapp.io']; },
    value => { value.mcpRoute.spec.hostnames = [wildcardHostname]; },
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

test('public callback planning includes only required public-client settings and never grants permissions', () => {
  const callbackUri = deriveIdentityRouting(snapshot()).callbackUri;
  const application = { id: applicationObjectId, appId: clientId, publicClient: { redirectUris: ['http://localhost'] },
    web: { redirectUris: ['https://existing.example.com/callback'] }, signInAudience: 'AzureADMultipleOrgs',
    isFallbackPublicClient: false, requiredResourceAccess: [{ resourceAppId: 'unrelated' }] };
  const original = structuredClone(application);
  const plan = planPublicBrokerCallback(application, clientId, callbackUri, clientId);
  assert.equal(plan.action, 'append');
  assert.equal(plan.platform, 'publicClient');
  assert.equal(plan.mutationExecuted, false);
  assert.deepEqual(plan.redirectUris, ['http://localhost', callbackUri]);
  assert.deepEqual(plan.missingPrerequisites, [
    'single-tenant sign-in audience (AzureADMyOrg)',
    'public-client flow (isFallbackPublicClient=true)',
    'the exact route-derived callback in publicClient.redirectUris',
  ]);
  assert.deepEqual(plan.patch, {
    signInAudience: 'AzureADMyOrg', isFallbackPublicClient: true,
    publicClient: { redirectUris: ['http://localhost', callbackUri] },
  });
  assert.deepEqual(application, original);
  application.publicClient.redirectUris.push(callbackUri);
  assert.equal(planPublicBrokerCallback(application, clientId, callbackUri, clientId).action, 'reconcile');
  assert.throws(() => planPublicBrokerCallback(application, 'wrong-client', callbackUri, clientId), /exact upstream/);
  assert.throws(() => planPublicBrokerCallback(application, clientId, callbackUri.replace('signin-oidc', 'auth/callback'), clientId),
    /exact upstream/);
});

test('public callback planning accepts only the auth alias of an explicit stable issuer', () => {
  const stableIssuerUri = `https://${hostname}/`;
  const oldCallbackUri = `${stableIssuerUri}signin-oidc`;
  const authHostname = `auth.${hostname.slice('agentweaver.'.length)}`;
  const callbackUri = `https://${authHostname}/signin-oidc`;
  const application = {
    id: applicationObjectId, appId: clientId, signInAudience: 'AzureADMyOrg', isFallbackPublicClient: true,
    publicClient: { redirectUris: ['http://localhost', oldCallbackUri] },
    web: { redirectUris: ['https://portal.example.com/auth/callback'] },
    requiredResourceAccess: [{ resourceAppId: 'unchanged' }],
  };
  const original = structuredClone(application);
  const plan = planPublicBrokerCallback(application, clientId, callbackUri, clientId, stableIssuerUri);
  assert.equal(plan.action, 'append');
  assert.equal(plan.platform, 'publicClient');
  assert.equal(plan.mutationExecuted, false);
  assert.deepEqual(plan.redirectUris, ['http://localhost', oldCallbackUri, callbackUri]);
  assert.deepEqual(plan.patch, {
    publicClient: { redirectUris: ['http://localhost', oldCallbackUri, callbackUri] },
  });
  assert.deepEqual(application, original);
  assert.equal(planPublicBrokerCallback(application, clientId, oldCallbackUri, clientId, stableIssuerUri).action, 'noop');

  for (const [invalidCallback, issuer] of [
    [callbackUri, undefined],
    [callbackUri, `https://agentweaver.other.eastus2.aksapp.io/`],
    [`https://auth.other.eastus2.aksapp.io/signin-oidc`, stableIssuerUri],
    [`https://attacker.${hostname.slice('agentweaver.'.length)}/signin-oidc`, stableIssuerUri],
    [`https://auth.${hostname.slice('agentweaver.'.length)}:8443/signin-oidc`, stableIssuerUri],
    [`https://auth.${hostname.slice('agentweaver.'.length)}:443/signin-oidc`, stableIssuerUri],
    [`https://auth.${hostname.slice('agentweaver.'.length)}/signin-oidc?`, stableIssuerUri],
    [`https://auth.${hostname.slice('agentweaver.'.length)}/signin-oidc#`, stableIssuerUri],
    [`https://@auth.${hostname.slice('agentweaver.'.length)}/signin-oidc`, stableIssuerUri],
    [`https://auth.${hostname.slice('agentweaver.'.length)}/auth/callback`, stableIssuerUri],
    [`http://auth.${hostname.slice('agentweaver.'.length)}/signin-oidc`, stableIssuerUri],
  ]) {
    assert.throws(() => planPublicBrokerCallback(application, clientId, invalidCallback, clientId, issuer),
      /exact upstream/);
  }
  assert.throws(() => planPublicBrokerCallback(application, 'wrong-client', callbackUri, clientId, stableIssuerUri),
    /exact upstream/);
  assert.throws(() => planPublicBrokerCallback(application, clientId, callbackUri, 'wrong-tenant', stableIssuerUri),
    /exact upstream/);
});

test('readiness proves DNS, trusted HTTPS aliases, API routing, and HTTP/2 cross-host routing', () => {
  const calls = [];
  const http2Calls = [];
  const result = verifyIdentityBrokerReadiness(deriveIdentityRouting(snapshot()), {
    execCurl: args => { calls.push(args); return ok('Healthy\n200\n203.0.113.10'); },
    execHttp2: args => { http2Calls.push(args); return ok(JSON.stringify({ alpn: 'h2', status: 200 })); },
  });
  assert.equal(result.runtimeVerified, true);
  assert.equal(result.dnsVerified, true);
  assert.equal(result.httpsVerified, true);
  assert.equal(result.http2CoalescingVerified, true);
  assert.deepEqual(calls.map(args => args.at(-1)), [
    `https://${hostname}/health/live`, `https://${hostname}/health/ready`,
    `https://${authHostname}/health/live`, `https://${authHostname}/health/ready`,
    `https://${apiHostname}/openapi/v1.json`,
  ]);
  assert.ok(calls.every(args => !args.some(value => ['-k', '--insecure', '--resolve', '--location'].includes(value))));
  assert.ok(calls.every(args => args[0] === '--disable'));
  assert.deepEqual(http2Calls, [
    ['203.0.113.10', hostname, portalHostname, '/'],
    ['203.0.113.10', portalHostname, hostname, '/health/live'],
  ]);
  for (const result of [ok('Healthy\n200\n203.0.113.20'), ok('Redirect\n302\n203.0.113.10'),
    { status: 60, stdout: '', stderr: 'certificate validation failed' }]) {
    assert.throws(() => verifyIdentityBrokerReadiness(deriveIdentityRouting(snapshot()), {
      execCurl: () => result,
    }), /DNS\/TLS verification failed/);
  }
  for (const response of [{ alpn: 'h2', status: 404 }, { alpn: 'h2', status: 421 }, { alpn: 'http/1.1', status: 200 }]) {
    assert.throws(() => verifyIdentityBrokerReadiness(deriveIdentityRouting(snapshot()), {
      execCurl: () => ok('Healthy\n200\n203.0.113.10'),
      execHttp2: () => ok(JSON.stringify(response)),
    }), /HTTP\/2 cross-host routing verification failed/);
  }
});

test('production JSON projections preserve the native long DNS hostname and existing public redirect URIs', () => {
  const native = snapshot();
  const observedDomain = '*.0123456789abcdef.eastus2euap.aksapp.io';
  const observedHost = `agentweaver.${observedDomain.slice(2)}`;
  native.gatewayCertificate.status.domain = observedDomain;
  native.brokerCertificate.status.domain = observedDomain;
  native.gateway.spec.listeners[0].hostname = `*.${observedDomain.slice(2)}`;
  native.gateway.spec.listeners[1].hostname = `auth.${observedDomain.slice(2)}`;
  native.gateway.spec.listeners[2].hostname = `api.${observedDomain.slice(2)}`;
  native.route.spec.hostnames = [observedHost];
  native.backendPolicy.spec.validation.hostname = observedHost;
  native.authRoute.spec.hostnames = [`auth.${observedDomain.slice(2)}`];
  native.webRoute.spec.hostnames = [`portal.${observedDomain.slice(2)}`];
  native.apiRoute.spec.hostnames = [`portal.${observedDomain.slice(2)}`];
  native.apiHostRoute.spec.hostnames = [`api.${observedDomain.slice(2)}`];
  native.copilotBffRoute.spec.hostnames = [`portal.${observedDomain.slice(2)}`];
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
  const application = {
    id: applicationObjectId, appId: clientId, signInAudience: 'AzureADMyOrg',
    isFallbackPublicClient: true,
    publicClient: { redirectUris: ['http://localhost', callbackUri] },
  };
  dependencies.execAz = (args, options) => {
    if (args[0] === 'aks' && args[1] === 'show') return ok(JSON.stringify(throughProductionWrapper({
      id: `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}` +
        `/providers/Microsoft.ContainerService/managedClusters/${config.clusterName}`,
      ingressProfile: { webAppRouting: { defaultDomain: {
        enabled: true, domainName: observedDomain.slice(2),
      } } },
    }, options.projectJson)));
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

test('explicit confirmed registration narrowly reconciles client modes and public redirects, preserving legacy fields', () => {
  const callbackUri = deriveIdentityRouting(snapshot()).callbackUri;
  const application = {
    id: applicationObjectId, appId: clientId, publicClient: { redirectUris: ['http://localhost'] },
    web: { redirectUris: ['https://legacy.example.com/callback'] }, isFallbackPublicClient: false,
    signInAudience: 'AzureADMultipleOrgs',
    spa: { redirectUris: ['https://spa.example.com'] }, appRoles: [{ id: 'existing-role' }],
    oauth2PermissionScopes: [{ id: 'existing-scope' }],
    requiredResourceAccess: [{ resourceAppId: 'unrelated' }],
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
    assert.deepEqual(Object.keys(patch), ['signInAudience', 'isFallbackPublicClient', 'publicClient']);
    assert.equal(patch.signInAudience, 'AzureADMyOrg');
    assert.equal(patch.isFallbackPublicClient, true);
    assert.deepEqual(Object.keys(patch.publicClient), ['redirectUris']);
    assert.equal(args[args.indexOf('--method') + 1], 'PATCH');
    assert.equal(args[args.indexOf('--uri') + 1], `https://graph.microsoft.com/v1.0/applications/${applicationObjectId}`);
    assert.equal(args[args.indexOf('--subscription') + 1], config.subscriptionId);
    Object.assign(application, patch);
    return ok();
  };
  const result = registerPublicBrokerCallback(plan, { ...config,
    registerBrokerCallback: true, confirmBrokerCallback: callbackUri,
  }, { execAz });
  assert.equal(result.mutationExecuted, true);
  assert.equal(result.action, 'append');
  assert.deepEqual(result.redirectUris, ['http://localhost', 'http://localhost/newly-added', callbackUri]);
  for (const field of ['web', 'spa', 'appRoles', 'oauth2PermissionScopes', 'requiredResourceAccess']) {
    assert.deepEqual(application[field], original[field]);
  }
  assert.equal(application.signInAudience, 'AzureADMyOrg');
  assert.equal(application.isFallbackPublicClient, true);
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
  assert.equal(receipt.publicCallbackPlan.action, 'noop');
  assert.deepEqual(receipt.runtimeConfigInputs, {
    IdentityBroker__Issuer: `https://${hostname}/`, IdentityBroker__ExternalProvider__ClientId: clientId,
  });
  assert.equal(receipt.runtimeConfigMutationExecuted, false);
  const applies = calls.filter(call => ['create', 'patch'].includes(kubectlCommand(call.args)[0])).map(writtenText);
  assert.equal(applies.length, 18);
  assert.ok(applies.every(text => !text.includes('CHANGEME-')));
  assert.ok(applies.some(text => text.includes('enforce: baseline')));
  assert.equal(applies.filter(text => text.includes('kind: DefaultDomainCertificate')).length, 2);
  assert.ok(applies.some(text => text.includes(`k8s:io.kubernetes.pod.namespace: ${gatewayNamespace}`)));
  assert.ok(applies.some(text => text.includes(`hostname: ${hostname}`)));
  const gateway = applies.find(text => text.includes('kind: Gateway'));
  assert.ok(gateway);
  assert.equal(parse(gateway).spec.listeners[0].hostname, wildcardHostname);
  assert.deepEqual(parse(gateway).spec.listeners.map(({ name, hostname: listenerHost }) =>
    ({ name, hostname: listenerHost })), [
    { name: 'https', hostname: wildcardHostname },
    { name: 'auth', hostname: authHostname },
    { name: 'api', hostname: apiHostname },
  ]);
  assert.doesNotMatch(gateway, /name: portal/);
  const authRoute = applies.find(text => text.includes('name: identity-broker-auth'));
  assert.ok(authRoute);
  assert.ok(authRoute.includes(`- ${authHostname}`));
  assert.match(authRoute, /sectionName: auth/);
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
  assert.match(webRoute, /sectionName: https/);
  assert.match(webRoute, /backendRefs:\s+- name: web\s+port: 8080/);
  const apiRoute = applies.find(text => text.includes('kind: HTTPRoute') && text.includes('name: gateway-api'));
  assert.ok(apiRoute);
  assert.ok(apiRoute.includes(`- ${portalHostname}`));
  assert.match(apiRoute, /sectionName: https/);
  assert.match(apiRoute, /value: \/api\/v1/);
  assert.match(apiRoute, /type: Exact\s+value: \/openapi\/v1\.json/);
  assert.match(apiRoute, /backendRefs:\s+- name: gateway\s+port: 8443/);
  const apiHostRoute = applies.find(text => text.includes('name: gateway-api-host'));
  assert.ok(apiHostRoute);
  assert.ok(apiHostRoute.includes(`- ${apiHostname}`));
  assert.match(apiHostRoute, /sectionName: api/);
  assert.match(apiHostRoute, /value: \/api\/connections\/copilot-user\/v1/);
  const copilotBffRoute = applies.find(text => text.includes('name: gateway-copilot-bff'));
  assert.ok(copilotBffRoute);
  assert.ok(copilotBffRoute.includes(`- ${portalHostname}`));
  assert.match(copilotBffRoute, /sectionName: https/);
  assert.match(copilotBffRoute, /value: \/api\/connections\/copilot-user\/v1/);
  assert.ok(applies.some(text => text.includes('name: gateway-api-ingress') &&
    text.includes('agentweaver.io/workload: gateway') && text.includes('port: "8443"')));
  const mcpRoute = applies.find(text => text.includes('kind: HTTPRoute') && text.includes('name: mcp'));
  assert.ok(mcpRoute);
  assert.ok(mcpRoute.includes(`- ${portalHostname}`));
  assert.match(mcpRoute, /sectionName: https/);
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

test('routing refuses to overwrite an object changed after ownership inventory', () => {
  const { calls, dependencies } = transport();
  const original = dependencies.execKubectl;
  let conditionalPatch;
  dependencies.execKubectl = (args, options) => {
    const command = kubectlCommand(args);
    if (command[0] === 'patch' && command[1] === 'httproute' && command[2] === 'gateway-api-host') {
      calls.push({ args, options });
      conditionalPatch = JSON.parse(command[command.indexOf('--patch') + 1]);
      return { status: 1, stdout: '', stderr: 'Conflict: routing object changed after inventory' };
    }
    return original(args, options);
  };
  assert.throws(() => bootstrapIdentityRouting(config, dependencies),
    /Identity routing Kubernetes operation failed: Conflict: routing object changed after inventory/);
  assert.deepEqual(conditionalPatch.slice(0, 2), [
    { op: 'test', path: '/metadata/uid', value: 'HTTPRoute-gateway-api-host-uid' },
    { op: 'test', path: '/metadata/resourceVersion', value: '1' },
  ]);
  assert.equal(calls.some(call => kubectlCommand(call.args)[0] === 'apply'), false);
  assert.equal(calls.some(call => kubectlCommand(call.args)[0] === 'patch' &&
    kubectlCommand(call.args)[2] === 'gateway-copilot-bff'), false);
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
    if (!value.failWait) {
      assert.equal(calls.filter(call => ['apply', 'create', 'patch'].includes(kubectlCommand(call.args)[0])).length, 0);
    }
  }
});
