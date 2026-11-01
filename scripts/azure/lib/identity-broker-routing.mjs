/**
 * Plans, verifies, and bootstraps the P0 identity broker's external routing and callback configuration.
 * Azure and Kubernetes readbacks are validated before routing changes are applied.
 */
import { readFileSync } from 'node:fs';
import { isIP } from 'node:net';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { run, runAz, redact } from './exec.mjs';
import { assertP0KubernetesPermissions, withP0UserKubeconfig } from './namespace-bootstrap.mjs';

const P0_NAMESPACE = 'agentweaver';
const GATEWAY_NAMESPACE = 'agentweaver-gateway';
const CONTROLLER = 'istio.aks.azure.com/gateway-controller';
const COMMAND_OPTIONS = { check: false, timeout: 35_000 };
const GUID_PATTERN = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const HTTP2_ROUTE_PROBE = fileURLToPath(new URL('./http2-route-probe.mjs', import.meta.url));

/** Projects routing readback data to the fields that are safe and relevant for verification. */
export function projectIdentityRoutingReadback(value) {
  if (Array.isArray(value?.items)) {
    if (value.items.length > 1) throw new Error('Exact-name routing lookup returned more than one object.');
    return value.items.length === 1 ? projectIdentityRoutingReadback(value.items[0]) : null;
  }
  const metadata = value?.metadata;
  const projected = {
    apiVersion: value?.apiVersion, kind: value?.kind,
    metadata: {
      name: metadata?.name, namespace: metadata?.namespace, uid: metadata?.uid,
      resourceVersion: metadata?.resourceVersion, generation: metadata?.generation,
      labels: Object.fromEntries(Object.entries(metadata?.labels ?? {}).filter(([key]) =>
        ['agentweaver.io/environment', 'agentweaver.io/managed-by', 'agentweaver.io/workload',
          'agentweaver.io/service', 'pod-security.kubernetes.io/enforce',
          'pod-security.kubernetes.io/audit', 'pod-security.kubernetes.io/warn'].includes(key))),
    },
  };
  const conditions = items => items?.map(({ type, status, observedGeneration }) => ({ type, status, observedGeneration }));
  const reference = item => item && ({
    name: item.name, namespace: item.namespace, group: item.group, kind: item.kind, sectionName: item.sectionName,
    port: item.port,
  });
  switch (value?.kind) {
    case 'Namespace':
    case 'CiliumNetworkPolicy':
      break;
    case 'Service':
      projected.spec = {
        type: value.spec?.type,
        selector: value.spec?.selector,
        ports: value.spec?.ports?.map(item => ({
          name: item.name, port: item.port, targetPort: item.targetPort, protocol: item.protocol,
        })),
      };
      break;
    case 'GatewayClass':
      projected.spec = { controllerName: value.spec?.controllerName };
      projected.status = { conditions: conditions(value.status?.conditions) };
      break;
    case 'DefaultDomainCertificate':
      projected.spec = { target: { secret: value.spec?.target?.secret } };
      projected.status = { domain: value.status?.domain, conditions: conditions(value.status?.conditions) };
      break;
    case 'Gateway':
      projected.spec = {
        gatewayClassName: value.spec?.gatewayClassName,
        listeners: value.spec?.listeners?.map(item => ({
          name: item.name, hostname: item.hostname, port: item.port, protocol: item.protocol,
          tls: { mode: item.tls?.mode, certificateRefs: item.tls?.certificateRefs?.map(reference) },
          allowedRoutes: item.allowedRoutes,
        })),
      };
      projected.status = {
        conditions: conditions(value.status?.conditions), addresses: value.status?.addresses,
        listeners: value.status?.listeners?.map(item => ({ name: item.name, conditions: conditions(item.conditions) })),
      };
      break;
    case 'HTTPRoute':
      projected.spec = {
        hostnames: value.spec?.hostnames, parentRefs: value.spec?.parentRefs?.map(reference),
        rules: value.spec?.rules?.map(item => ({
          filters: item.filters?.map(() => ({})),
          matches: item.matches?.map(match => Object.fromEntries(Object.entries(match)
            .map(([key, field]) => [key, key === 'path' ? field : true]))),
          backendRefs: item.backendRefs?.map(backend => ({
            ...reference(backend), port: backend.port, weight: backend.weight,
            filters: backend.filters?.map(() => ({})),
          })),
        })),
      };
      projected.status = {
        parents: value.status?.parents?.map(item => ({
          controllerName: item.controllerName, parentRef: reference(item.parentRef), conditions: conditions(item.conditions),
        })),
      };
      break;
    case 'BackendTLSPolicy':
      projected.spec = {
        targetRefs: value.spec?.targetRefs?.map(reference),
        validation: {
          hostname: value.spec?.validation?.hostname,
          wellKnownCACertificates: value.spec?.validation?.wellKnownCACertificates,
          caCertificateRefs: value.spec?.validation?.caCertificateRefs?.map(reference),
          subjectAltNames: value.spec?.validation?.subjectAltNames?.map(() => ({})),
        },
      };
      projected.status = {
        ancestors: value.status?.ancestors?.map(item => ({
          controllerName: item.controllerName, ancestorRef: reference(item.ancestorRef), conditions: conditions(item.conditions),
        })),
      };
      break;
    default:
      throw new Error('Unexpected native Identity routing readback kind.');
  }
  return projected;
}

/** Projects a namespace-wide routing inventory without exposing unrelated native fields. */
export function projectIdentityRoutingInventory(value) {
  if (!Array.isArray(value?.items)) throw new Error('Routing inventory readback is not a Kubernetes list.');
  return { items: value.items.map(projectIdentityRoutingReadback) };
}

function projectPublicApplication(value) {
  return {
    id: value?.id,
    appId: value?.appId,
    signInAudience: value?.signInAudience,
    isFallbackPublicClient: value?.isFallbackPublicClient,
    publicClient: { redirectUris: value?.publicClient?.redirectUris },
  };
}

/** Requires the gateway and its security policy to be placed in the approved namespace. */
export function assertIdentityRoutingPlacement({ gatewayNamespace, gatewaySecurityPolicy }) {
  if (gatewayNamespace !== GATEWAY_NAMESPACE ||
      !['restricted', 'baseline'].includes(gatewaySecurityPolicy)) {
    throw new Error('Identity routing requires explicit agentweaver-gateway placement and an explicit restricted or baseline policy. No placement is selected by default.');
  }
}

function assertObject(object, kind, namespace, name = 'identity-broker') {
  const apiVersion = kind === 'DefaultDomainCertificate'
    ? 'approuting.kubernetes.azure.com/v1alpha1' : 'gateway.networking.k8s.io/v1';
  if (object?.kind !== kind || object.apiVersion !== apiVersion || object.metadata?.name !== name ||
      object.metadata?.namespace !== namespace || !object.metadata?.uid ||
      !Number.isInteger(object.metadata?.generation) || object.metadata.generation < 1) {
    throw new Error(`Native ${kind} does not identify the exact ${name} routing object.`);
  }
}

function requireConditions(object, conditions, types) {
  if (!Number.isInteger(object.metadata?.generation) || object.metadata.generation < 1) {
    throw new Error(`${object.kind} has no current native generation.`);
  }
  for (const type of types) {
    const condition = conditions?.find(item => item.type === type);
    if (condition?.status !== 'True' || condition.observedGeneration !== object.metadata.generation) {
      throw new Error(`${object.kind} ${type} is not true for its current generation.`);
    }
  }
}

/** Derives the broker hostname from the managed AKS Application Routing domain. */
export function managedBrokerHostnameFromDomain(domain) {
  const bareDomain = typeof domain === 'string' && domain.startsWith('*.') ? domain.slice(2) : domain;
  const labels = typeof bareDomain === 'string' ? bareDomain.split('.') : [];
  if (typeof bareDomain !== 'string' || !bareDomain.endsWith('.aksapp.io') || bareDomain.length > 240 ||
      labels.length < 4 || labels.some(label => !/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(label))) {
    throw new Error('The managed AKS wildcard host must use the exact default *.aksapp.io domain.');
  }
  return `agentweaver.${bareDomain}`;
}

/** Derives the broker hostname from the managed certificate and namespace inputs. */
export function managedBrokerHostname(certificate, namespace) {
  assertObject(certificate, 'DefaultDomainCertificate', namespace);
  requireConditions(certificate, certificate.status?.conditions, ['Available']);
  const domain = certificate.status?.domain;
  if (certificate.spec?.target?.secret !== 'identity-broker-tls' ||
      typeof domain !== 'string' || !domain.startsWith('*.')) {
    throw new Error('Managed certificate must report an available AKS wildcard domain and the exact TLS Secret.');
  }
  return managedBrokerHostnameFromDomain(domain);
}

function managedPortalHostname(certificate, namespace) {
  return `portal.${managedBrokerHostname(certificate, namespace).slice('agentweaver.'.length)}`;
}

function managedWildcardHostname(hostname) {
  return `*.${hostname.slice('agentweaver.'.length)}`;
}

/** Reads existing P0 broker routing resources and checks them against the desired routing shape. */
export function observeExistingP0BrokerRouting({
  certificate, gateway, route, backendPolicy, webRoute, apiRoute, apiBackendPolicy,
  mcpRoute, mcpBackendPolicy,
  requireApplicationRoutes = true,
}) {
  const hostname = managedBrokerHostname(certificate, P0_NAMESPACE);
  const portalHostname = managedPortalHostname(certificate, P0_NAMESPACE);
  const gatewayNamespace = route?.spec?.parentRefs?.[0]?.namespace;
  if (gatewayNamespace !== GATEWAY_NAMESPACE) {
    throw new Error('Existing P0 HTTPRoute does not identify the exact approved Gateway namespace.');
  }
  const ownedObjects = [
    [gateway, 'Gateway', 'identity-broker', 'identity-broker'],
    [route, 'HTTPRoute', 'identity-broker', 'identity-broker'],
    [backendPolicy, 'BackendTLSPolicy', 'identity-broker', 'identity-broker'],
  ];
  if (requireApplicationRoutes) ownedObjects.push(
    [webRoute, 'HTTPRoute', 'web-application', 'web'],
    [apiRoute, 'HTTPRoute', 'gateway-api', 'gateway'],
    [apiBackendPolicy, 'BackendTLSPolicy', 'gateway-api', 'gateway'],
    [mcpRoute, 'HTTPRoute', 'mcp', 'mcp'],
    [mcpBackendPolicy, 'BackendTLSPolicy', 'mcp', 'mcp'],
  );
  for (const [object, kind, name, service] of ownedObjects) {
    assertObject(object, kind, kind === 'Gateway' ? gatewayNamespace : P0_NAMESPACE, name);
    if (object.metadata.labels?.['agentweaver.io/service'] !== service ||
        !object.metadata.resourceVersion) throw new Error('Existing P0 routing object is not the exact owned version.');
  }
  const brokerListener = gateway.spec?.listeners?.find(item => item.name === 'https');
  const observedBrokerHost = route.spec?.hostnames?.[0];
  const legacyHost = hostname.replace(/^agentweaver\./, 'identity-broker.');
  const permittedBrokerHost = requireApplicationRoutes ? hostname : [hostname, legacyHost].includes(observedBrokerHost)
    ? observedBrokerHost : hostname;
  if (gateway.spec?.gatewayClassName !== 'approuting-istio' ||
      (requireApplicationRoutes ? gateway.spec.listeners?.length !== 1
        : ![1, 2].includes(gateway.spec.listeners?.length)) ||
      !hasManagedP0Listener(brokerListener, 'https',
        requireApplicationRoutes ? managedWildcardHostname(hostname) : permittedBrokerHost, gatewayNamespace) ||
      (!requireApplicationRoutes && gateway.spec.listeners.length === 2 && !hasManagedP0Listener(
        gateway.spec.listeners.find(item => item.name === 'portal'), 'portal', portalHostname, gatewayNamespace))) {
    throw new Error('Existing P0 Gateway references or managed TLS configuration changed.');
  }
  assertManagedRoute(route, {
    name: 'identity-broker', service: 'identity-broker', hostname: permittedBrokerHost, gatewayNamespace,
    sectionName: 'https', paths: [{ type: 'PathPrefix', value: '/' }], backend: 'identity-broker', port: 443,
  });
  assertManagedBackendTlsPolicy(backendPolicy, 'identity-broker', 'identity-broker', permittedBrokerHost);
  if (requireApplicationRoutes) {
    assertManagedRoute(webRoute, {
      name: 'web-application', service: 'web', hostname: portalHostname, gatewayNamespace,
      sectionName: 'https', paths: [{ type: 'PathPrefix', value: '/' }], backend: 'web', port: 8080,
    });
    assertManagedRoute(apiRoute, {
      name: 'gateway-api', service: 'gateway', hostname: portalHostname, gatewayNamespace,
      sectionName: 'https', paths: [
        { type: 'PathPrefix', value: '/api/v1' },
        { type: 'Exact', value: '/openapi/v1.json' },
      ], backend: 'gateway', port: 8443,
    });
    assertManagedBackendTlsPolicy(apiBackendPolicy, 'gateway-api', 'gateway',
      'gateway.agentweaver.svc.cluster.local', 'service-backend-ca');
    assertManagedRoute(mcpRoute, {
      name: 'mcp', service: 'mcp', hostname: portalHostname, gatewayNamespace,
      sectionName: 'https', paths: [
        { type: 'Exact', value: '/mcp' },
        { type: 'Exact', value: '/.well-known/oauth-protected-resource/mcp' },
      ], backend: 'mcp', port: 8443,
    });
    assertManagedBackendTlsPolicy(mcpBackendPolicy, 'mcp', 'mcp',
      'mcp.agentweaver.svc.cluster.local', 'service-backend-ca');
    requireManagedRouteConditions(route, gatewayNamespace, 'https');
    requireManagedRouteConditions(webRoute, gatewayNamespace, 'https');
    requireManagedRouteConditions(apiRoute, gatewayNamespace, 'https');
    requireManagedRouteConditions(mcpRoute, gatewayNamespace, 'https');
    requireManagedBackendConditions(backendPolicy, gatewayNamespace, 'https');
    requireManagedBackendConditions(apiBackendPolicy, gatewayNamespace, 'https');
    requireManagedBackendConditions(mcpBackendPolicy, gatewayNamespace, 'https');
  }
  if (requireApplicationRoutes && (gateway.spec.listeners.length !== 1 ||
      !hasCurrentListenerConditions(gateway, 'https'))) {
    throw new Error('Existing P0 shared Gateway listener is not accepted and programmed.');
  }
  return {
    hostname,
    issuer: `https://${route.spec.hostnames[0]}/`,
    portalHostname,
    applicationRoutesConfigured: requireApplicationRoutes,
    hostnameAligned: route.spec.hostnames[0] === hostname &&
      gateway.spec.listeners.find(item => item.name === 'https').hostname ===
        (requireApplicationRoutes ? managedWildcardHostname(hostname) : hostname) &&
      (!requireApplicationRoutes || (
      webRoute.spec.hostnames[0] === portalHostname && apiRoute.spec.hostnames[0] === portalHostname)),
    gatewayProgrammed: gateway.status?.conditions?.some(condition => condition.type === 'Programmed' &&
      condition.status === 'True' && condition.observedGeneration === gateway.metadata.generation) ?? false,
    gatewayNamespace,
    configurationOnly: true,
    runtimeVerified: false,
  };
}

/** Aligns a pre-existing broker route only after its observed resources pass the ownership checks. */
export function alignExistingP0BrokerHostname(config, {
  withKubeconfig = withP0UserKubeconfig,
  execKubectl = (args, options) => run('kubectl', args, options),
} = {}) {
  return withKubeconfig(config, base => {
    const args = [...base, '--request-timeout=30s'];
    function read(resource, namespace = P0_NAMESPACE, name = 'identity-broker') {
      const result = execKubectl([...args, '--namespace', namespace, 'get', resource, name, '-o', 'json'], {
        ...COMMAND_OPTIONS, projectJson: projectIdentityRoutingReadback, preserveProjectedJson: true,
      });
      if (result.status !== 0) throw new Error(`Existing P0 routing readback failed: ${redact(result.stderr)}`);
      return JSON.parse(result.stdout);
    }
    const certificate = read('defaultdomaincertificate');
    const route = read('httproute');
    const gatewayNamespace = route?.spec?.parentRefs?.[0]?.namespace;
    if (gatewayNamespace !== GATEWAY_NAMESPACE) {
      throw new Error('Existing P0 HTTPRoute does not identify the exact approved Gateway namespace.');
    }
    const gateway = read('gateway', gatewayNamespace);
    const backendPolicy = read('backendtlspolicy');
    const observed = observeExistingP0BrokerRouting({
      certificate, gateway, route, backendPolicy, requireApplicationRoutes: false,
    });
    const brokerListenerIndex = gateway.spec.listeners.findIndex(item => item.name === 'https');
    const updates = [
      ['gateway', gateway, `/spec/listeners/${brokerListenerIndex}/hostname`,
        gateway.spec.listeners[brokerListenerIndex].hostname, observed.hostname],
      ['httproute', route, '/spec/hostnames', route.spec.hostnames, [observed.hostname]],
      ['backendtlspolicy', backendPolicy, '/spec/validation/hostname', backendPolicy.spec.validation.hostname, observed.hostname],
    ];
    let changed = false;
    for (const [resource, object, path, current, value] of updates) {
      if (JSON.stringify(current) === JSON.stringify(value)) continue;
      const patch = [
        { op: 'test', path: '/metadata/uid', value: object.metadata.uid },
        { op: 'test', path: '/metadata/resourceVersion', value: object.metadata.resourceVersion },
        { op: 'test', path, value: current },
        { op: 'replace', path, value },
      ];
      const result = execKubectl([...args, '--namespace', object.metadata.namespace, 'patch', resource, 'identity-broker', '--type=json',
        '--patch', JSON.stringify(patch), '-o', 'name'], COMMAND_OPTIONS);
      if (result.status !== 0) throw new Error(`Existing P0 hostname alignment failed for ${resource}: ${redact(result.stderr)}`);
      changed = true;
    }
    const receipt = observeExistingP0BrokerRouting({
      certificate: read('defaultdomaincertificate'), gateway: read('gateway', gatewayNamespace),
      route: read('httproute'), backendPolicy: read('backendtlspolicy'),
      requireApplicationRoutes: false,
    });
    if (!receipt.hostnameAligned) throw new Error('Existing P0 hostname alignment did not read back the exact native hostname.');
    return { ...receipt, changed, routingBlocked: !receipt.gatewayProgrammed || !receipt.applicationRoutesConfigured };
  }, { execKubectl });
}

/** Matches the approved Gateway parent and allows only its HTTPS listener port when specified. */
function gatewayReference(reference, gatewayNamespace, objectNamespace) {
  return reference?.name === 'identity-broker' &&
    (reference.port === undefined || reference.port === 443) &&
    (reference.namespace ?? objectNamespace) === gatewayNamespace &&
    (reference.group ?? 'gateway.networking.k8s.io') === 'gateway.networking.k8s.io' &&
    (reference.kind ?? 'Gateway') === 'Gateway';
}

function hasManagedP0Listener(listener, name, hostname, gatewayNamespace) {
  const reference = listener?.tls?.certificateRefs?.[0];
  const namespaces = listener?.allowedRoutes?.namespaces;
  return listener?.name === name && listener.hostname === hostname &&
    listener.protocol === 'HTTPS' && listener.port === 443 && listener.tls?.mode === 'Terminate' &&
    listener.tls.certificateRefs?.length === 1 && reference?.name === 'identity-broker-tls' &&
    (reference.kind ?? 'Secret') === 'Secret' && (reference.group ?? '') === '' &&
    (reference.namespace ?? gatewayNamespace) === gatewayNamespace &&
    namespaces?.from === 'Selector' &&
    namespaces.selector?.matchLabels?.['kubernetes.io/metadata.name'] === P0_NAMESPACE &&
    Object.keys(namespaces.selector.matchLabels).length === 1 &&
    !namespaces.selector.matchExpressions?.length;
}

function hasCurrentListenerConditions(gateway, name) {
  const conditions = gateway.status?.listeners?.find(item => item.name === name)?.conditions;
  return ['Accepted', 'Programmed', 'ResolvedRefs'].every(type => {
    const condition = conditions?.find(item => item.type === type);
    return condition?.status === 'True' && condition.observedGeneration === gateway.metadata.generation;
  });
}

function assertManagedRoute(route, {
  name, service, hostname, gatewayNamespace, sectionName, paths, backend, port,
}) {
  assertObject(route, 'HTTPRoute', P0_NAMESPACE, name);
  const parent = route.spec?.parentRefs?.[0];
  const rule = route.spec?.rules?.[0];
  const target = rule?.backendRefs?.[0];
  const matches = rule?.matches ?? [];
  const exactPaths = Array.isArray(paths) && matches.length === paths.length &&
    paths.every(path => matches.filter(match => Object.keys(match ?? {}).length === 1 &&
      match?.path?.type === path.type && match.path.value === path.value).length === 1);
  if (route.metadata.labels?.['agentweaver.io/service'] !== service ||
      route.spec?.hostnames?.length !== 1 || route.spec.hostnames[0] !== hostname ||
      route.spec.parentRefs?.length !== 1 || !gatewayReference(parent, gatewayNamespace, P0_NAMESPACE) ||
      parent.sectionName !== sectionName || route.spec.rules?.length !== 1 ||
      rule.backendRefs?.length !== 1 || rule.filters?.length || target?.filters?.length ||
      target?.name !== backend || (target.namespace ?? P0_NAMESPACE) !== P0_NAMESPACE ||
      (target.kind ?? 'Service') !== 'Service' || (target.group ?? '') !== '' || target.port !== port ||
      (target.weight ?? 1) !== 1 || !exactPaths) {
    throw new Error(`HTTPRoute ${name} does not match its exact source-owned host, path, listener, and backend.`);
  }
}

/**
 * Validates the root bundle's Web route while the separately managed Gateway is absent.
 * The route must retain the approved parent, managed Portal host, and exact Web backend.
 * An explicitly supplied parent port must match the approved HTTPS listener.
 */
export function assertExistingP0BundleWebRoute(webRoute, portalHostname) {
  assertManagedRoute(webRoute, {
    name: 'web-application', service: 'web', hostname: portalHostname, gatewayNamespace: GATEWAY_NAMESPACE,
    sectionName: 'https', paths: [{ type: 'PathPrefix', value: '/' }], backend: 'web', port: 8080,
  });
  if (!webRoute.metadata.resourceVersion) {
    throw new Error('Existing P0 bundle Web HTTPRoute has no current resource version.');
  }
}

function assertManagedBackendTlsPolicy(policy, name, service, hostname, caConfigMapName) {
  assertObject(policy, 'BackendTLSPolicy', P0_NAMESPACE, name);
  const target = policy.spec?.targetRefs?.[0];
  const validation = policy.spec?.validation;
  if (policy.metadata.labels?.['agentweaver.io/service'] !== service ||
      policy.spec?.targetRefs?.length !== 1 || target?.name !== service ||
      (target.kind ?? 'Service') !== 'Service' || (target.group ?? '') !== '' ||
      (target.namespace ?? P0_NAMESPACE) !== P0_NAMESPACE || target.sectionName !== 'https' ||
      validation?.hostname !== hostname || (caConfigMapName
        ? validation.wellKnownCACertificates !== undefined ||
          validation.caCertificateRefs?.length !== 1 ||
          validation.caCertificateRefs[0].name !== caConfigMapName ||
          (validation.caCertificateRefs[0].namespace ?? P0_NAMESPACE) !== P0_NAMESPACE ||
          (validation.caCertificateRefs[0].kind ?? 'ConfigMap') !== 'ConfigMap' ||
          (validation.caCertificateRefs[0].group ?? '') !== ''
        : validation.wellKnownCACertificates !== 'System' || validation.caCertificateRefs?.length) ||
      validation.subjectAltNames?.length) {
    throw new Error(`BackendTLSPolicy ${name} does not use its exact source-owned Service and certificate trust.`);
  }
}

function requireManagedRouteConditions(route, gatewayNamespace, sectionName) {
  const parent = route.status?.parents?.find(item => item.controllerName === CONTROLLER &&
    gatewayReference(item.parentRef, gatewayNamespace, P0_NAMESPACE) &&
    item.parentRef.sectionName === sectionName);
  requireConditions(route, parent?.conditions, ['Accepted', 'ResolvedRefs']);
}

function requireManagedBackendConditions(policy, gatewayNamespace, sectionName) {
  const managedAncestors = policy.status?.ancestors?.filter(item => item.controllerName === CONTROLLER);
  const ancestorReference = managedAncestors?.length === 1 ? managedAncestors[0].ancestorRef : undefined;
  const scopedReference = {
    ...ancestorReference, namespace: ancestorReference?.namespace ?? gatewayNamespace,
  };
  const ancestor = managedAncestors?.length === 1 &&
    gatewayReference(scopedReference, gatewayNamespace, P0_NAMESPACE) &&
    (ancestorReference?.sectionName === undefined || ancestorReference.sectionName === sectionName)
    ? managedAncestors[0] : undefined;
  requireConditions(policy, ancestor?.conditions, ['Accepted', 'ResolvedRefs']);
}

/** Builds the desired routing resource description from validated certificate and callback inputs. */
export function deriveIdentityRouting({
  gatewayCertificate, brokerCertificate, gateway, route, backendPolicy, webRoute, apiRoute, apiBackendPolicy,
  mcpRoute, mcpBackendPolicy,
}) {
  const hostname = managedBrokerHostname(gatewayCertificate, GATEWAY_NAMESPACE);
  if (managedBrokerHostname(brokerCertificate, P0_NAMESPACE) !== hostname) {
    throw new Error('Gateway and Broker managed certificates do not identify the same domain.');
  }
  const portalHostname = managedPortalHostname(gatewayCertificate, GATEWAY_NAMESPACE);
  assertObject(gateway, 'Gateway', GATEWAY_NAMESPACE);
  if (gateway.spec?.gatewayClassName !== 'approuting-istio') {
    throw new Error('Identity Gateway must use the native AKS approuting-istio Gateway class.');
  }
  requireConditions(gateway, gateway.status?.conditions, ['Accepted', 'Programmed']);
  const listener = gateway.spec?.listeners?.find(item => item.name === 'https');
  if (gateway.spec.listeners.length !== 1 ||
      !hasManagedP0Listener(listener, 'https', managedWildcardHostname(hostname), GATEWAY_NAMESPACE)) {
    throw new Error('Identity Gateway must terminate managed wildcard HTTPS on the exact P0 namespace.');
  }
  if (!hasCurrentListenerConditions(gateway, 'https')) {
    throw new Error('Identity Gateway shared listener is not accepted, programmed, and resolved for its current generation.');
  }
  assertManagedRoute(route, {
    name: 'identity-broker', service: 'identity-broker', hostname, gatewayNamespace: GATEWAY_NAMESPACE,
    sectionName: 'https', paths: [{ type: 'PathPrefix', value: '/' }], backend: 'identity-broker', port: 443,
  });
  assertManagedRoute(webRoute, {
    name: 'web-application', service: 'web', hostname: portalHostname, gatewayNamespace: GATEWAY_NAMESPACE,
    sectionName: 'https', paths: [{ type: 'PathPrefix', value: '/' }], backend: 'web', port: 8080,
  });
  assertManagedRoute(apiRoute, {
    name: 'gateway-api', service: 'gateway', hostname: portalHostname, gatewayNamespace: GATEWAY_NAMESPACE,
    sectionName: 'https', paths: [
      { type: 'PathPrefix', value: '/api/v1' },
      { type: 'Exact', value: '/openapi/v1.json' },
    ], backend: 'gateway', port: 8443,
  });
  assertManagedBackendTlsPolicy(backendPolicy, 'identity-broker', 'identity-broker', hostname);
  assertManagedBackendTlsPolicy(apiBackendPolicy, 'gateway-api', 'gateway',
    'gateway.agentweaver.svc.cluster.local', 'service-backend-ca');
  assertManagedRoute(mcpRoute, {
    name: 'mcp', service: 'mcp', hostname: portalHostname, gatewayNamespace: GATEWAY_NAMESPACE,
    sectionName: 'https', paths: [
      { type: 'Exact', value: '/mcp' },
      { type: 'Exact', value: '/.well-known/oauth-protected-resource/mcp' },
    ], backend: 'mcp', port: 8443,
  });
  assertManagedBackendTlsPolicy(mcpBackendPolicy, 'mcp', 'mcp',
    'mcp.agentweaver.svc.cluster.local', 'service-backend-ca');
  for (const [managedRoute, section] of [
    [route, 'https'], [webRoute, 'https'], [apiRoute, 'https'], [mcpRoute, 'https'],
  ]) requireManagedRouteConditions(managedRoute, GATEWAY_NAMESPACE, section);
  requireManagedBackendConditions(backendPolicy, GATEWAY_NAMESPACE, 'https');
  requireManagedBackendConditions(apiBackendPolicy, GATEWAY_NAMESPACE, 'https');
  requireManagedBackendConditions(mcpBackendPolicy, GATEWAY_NAMESPACE, 'https');
  const addresses = gateway.status?.addresses?.filter(item => item.type === 'IPAddress' && isIP(item.value))
    .map(item => item.value);
  if (!addresses?.length) throw new Error('Programmed Gateway has no observed public IP address.');
  const issuer = `https://${route.spec.hostnames[0]}/`;
  return {
    hostname: route.spec.hostnames[0], portalHostname, issuer, callbackUri: `${issuer}signin-oidc`, addresses,
    gatewayUid: gateway.metadata.uid, routeUid: route.metadata.uid,
    routingConfigured: true, runtimeVerified: false,
  };
}

/** Produces a tenant- and application-bound callback plan without applying it. */
export function planPublicBrokerCallback(application, clientId, callbackUri, tenantId, stableIssuerUri) {
  let callback, stableIssuer, stableDomain;
  try {
    callback = new URL(callbackUri);
  } catch {
    throw new Error('Public callback plan requires the exact upstream application and managed Broker callback URI.');
  }
  if (stableIssuerUri !== undefined) {
    try {
      stableIssuer = new URL(stableIssuerUri);
      stableDomain = stableIssuer.hostname.startsWith('agentweaver.')
        ? stableIssuer.hostname.slice('agentweaver.'.length) : '';
      if (stableIssuer.protocol !== 'https:' || stableIssuerUri !== `https://${stableIssuer.hostname}/` ||
          stableIssuer.pathname !== '/' ||
          stableIssuer.search || stableIssuer.hash || stableIssuer.username || stableIssuer.password ||
          stableIssuer.port || stableIssuerUri.includes('?') || stableIssuerUri.includes('#') ||
          stableIssuerUri.includes('@') ||
          managedBrokerHostnameFromDomain(stableDomain) !== stableIssuer.hostname) {
        stableIssuer = undefined;
      }
    } catch {
      stableIssuer = undefined;
    }
  }
  const callbackHostAllowed = stableIssuerUri === undefined
    ? callback.hostname.startsWith('agentweaver.') && callback.hostname.endsWith('.aksapp.io')
    : stableIssuer && [stableIssuer.hostname, `auth.${stableDomain}`].includes(callback.hostname);
  const redirects = application?.publicClient?.redirectUris;
  if (!GUID_PATTERN.test(application?.id ?? '') || !GUID_PATTERN.test(clientId ?? '') ||
      !GUID_PATTERN.test(tenantId ?? '') || typeof application?.appId !== 'string' ||
      application.appId.toLowerCase() !== clientId.toLowerCase() ||
      callback.protocol !== 'https:' || callback.pathname !== '/signin-oidc' ||
      callback.search || callback.hash || callback.username || callback.password || callback.port ||
      callbackUri.includes('?') || callbackUri.includes('#') || callbackUri.includes('@') ||
      callbackUri !== `https://${callback.hostname}/signin-oidc` || !callbackHostAllowed ||
      (redirects != null && (!Array.isArray(redirects) ||
        redirects.some(uri => typeof uri !== 'string')))) {
    throw new Error('Public callback plan requires the exact upstream application and managed Broker callback URI.');
  }
  const existing = redirects ?? [];
  const unchanged = existing.includes(callbackUri);
  const missingPrerequisites = [];
  if (application.signInAudience !== 'AzureADMyOrg')
    missingPrerequisites.push('single-tenant sign-in audience (AzureADMyOrg)');
  if (application.isFallbackPublicClient !== true)
    missingPrerequisites.push('public-client flow (isFallbackPublicClient=true)');
  if (!unchanged)
    missingPrerequisites.push('the exact route-derived callback in publicClient.redirectUris');
  const patch = {};
  if (application.signInAudience !== 'AzureADMyOrg') patch.signInAudience = 'AzureADMyOrg';
  if (application.isFallbackPublicClient !== true) patch.isFallbackPublicClient = true;
  if (!unchanged) patch.publicClient = { redirectUris: [...existing, callbackUri] };
  // The installed provider requests standard OpenID Connect scopes only, so this plan never adds API permissions or consent.
  return {
    tenantId, clientId, applicationObjectId: application.id,
    patchUri: `https://graph.microsoft.com/v1.0/applications/${application.id}`,
    platform: 'publicClient', action: unchanged ? (Object.keys(patch).length ? 'reconcile' : 'noop') : 'append',
    redirectUris: unchanged ? [...existing] : [...existing, callbackUri],
    callbackUri, missingPrerequisites, registrationReady: missingPrerequisites.length === 0,
    patch, mutationExecuted: false,
  };
}

/** Applies a validated public broker callback registration through Azure CLI. */
export function registerPublicBrokerCallback(plan, config, { execAz = runAz } = {}) {
  if (!config.registerBrokerCallback) {
    if (config.confirmBrokerCallback) throw new Error('Callback confirmation requires --register-identity-broker-callback.');
    return plan;
  }
  if (config.confirmBrokerCallback !== plan.callbackUri || config.tenantId !== plan.tenantId ||
      config.upstreamClientId !== plan.clientId) {
    throw new Error('Callback registration requires confirmation of the exact route-derived URI and selected tenant/application.');
  }
  const account = execAz(['account', 'show',
    '--query', '{id:id,tenantId:tenantId}', '-o', 'json'], COMMAND_OPTIONS);
  if (account.status !== 0) throw new Error(`Callback account read failed: ${redact(account.stderr)}`);
  const selected = JSON.parse(account.stdout);
  if (selected.id?.toLowerCase() !== config.subscriptionId.toLowerCase() ||
      selected.tenantId?.toLowerCase() !== plan.tenantId.toLowerCase()) {
    throw new Error('Callback registration selected account/tenant changed; refusing an application write.');
  }
  function readApplication() {
    const result = execAz(['ad', 'app', 'show', '--id', plan.clientId,
      '--query', '{id:id,appId:appId,signInAudience:signInAudience,isFallbackPublicClient:isFallbackPublicClient,publicClient:publicClient}', '-o', 'json'], {
      ...COMMAND_OPTIONS, projectJson: projectPublicApplication, preserveProjectedJson: true,
    });
    if (result.status !== 0) throw new Error(`Public callback read failed: ${redact(result.stderr)}`);
    const fresh = planPublicBrokerCallback(JSON.parse(result.stdout), plan.clientId, plan.callbackUri, plan.tenantId);
    if (fresh.applicationObjectId.toLowerCase() !== plan.applicationObjectId.toLowerCase()) {
      throw new Error('Callback application object identity changed; refusing an application write.');
    }
    return fresh;
  }
  const fresh = readApplication();
  if (Object.keys(fresh.patch).length === 0) return fresh;
  // Patch only the protocol settings this Broker needs; Graph receives no role, scope,
  // permission, consent, Web redirect, or SPA redirect fields.
  const patch = execAz(['rest', '--method', 'PATCH', '--uri', fresh.patchUri,
    '--subscription', config.subscriptionId, '--headers', 'Content-Type=application/json',
    '--body', JSON.stringify(fresh.patch), '-o', 'none'], COMMAND_OPTIONS);
  if (patch.status !== 0) {
    throw new Error(`Public client reconciliation failed; no API permission or consent grant was attempted: ${redact(patch.stderr)}`);
  }
  const verified = readApplication();
  if (!verified.registrationReady || fresh.redirectUris.some(uri => !verified.redirectUris.includes(uri))) {
    throw new Error('Public client reconciliation returned success but the required settings or previous public redirects are missing.');
  }
  return { ...verified, action: fresh.action, mutationExecuted: true };
}

function registrationReconciliationCommand(callbackUri) {
  return 'node scripts/azure/deploy.mjs --resource-group <approved-resource-group> ' +
    '--subscription <approved-subscription> --allowed-subscription <approved-subscription> ' +
    '--tenant <approved-tenant> --allowed-tenant <approved-tenant> ' +
    '--template <reviewed-template> --parameters <reviewed-parameters-file> ' +
    '--identity-upstream-client-id <client-id> --identity-gateway-namespace agentweaver-gateway ' +
    '--identity-gateway-security-policy <restricted|baseline> --bootstrap-identity-routing ' +
    '--identity-routing-certificates-only ' +
    '--register-identity-broker-callback ' +
    `--confirm-identity-broker-callback "${callbackUri}" --execute`;
}

export function assertPublicBrokerRegistration(plan) {
  if (!plan.registrationReady) {
    throw new Error(
      `The exact Entra application is not ready for Broker sign-in. Missing: ${plan.missingPrerequisites.join(', ')}. ` +
        `Reconcile it explicitly with: ${registrationReconciliationCommand(plan.callbackUri)}. ` +
        'No API permission or consent grant is required or changed.',
    );
  }
  return plan;
}

/**
 * Checks the selected tenant, existing Entra application, and managed route callback
 * before a deployment or routing write. The callback uses the AKS domain that generates the route certificate.
 */
export function preflightPublicBrokerRegistration(config, {
  execAz = runAz,
} = {}) {
  if (!GUID_PATTERN.test(config.subscriptionId ?? '') || !GUID_PATTERN.test(config.tenantId ?? '') ||
      !GUID_PATTERN.test(config.upstreamClientId ?? '')) {
    throw new Error('Identity Broker registration preflight requires the exact subscription, tenant, and client IDs.');
  }
  const account = execAz(['account', 'show', '-o', 'json'], {
    ...COMMAND_OPTIONS,
    projectJson: value => ({ id: value.id, tenantId: value.tenantId, state: value.state }),
    preserveProjectedJson: true,
  });
  if (account.status !== 0) {
    throw new Error(`Entra preflight could not read the selected account: ${redact(account.stderr)}. ` +
      'Confirm the account with `az account show -o json`, then rerun the guarded installer.');
  }
  const selected = JSON.parse(account.stdout);
  if (selected.id?.toLowerCase() !== config.subscriptionId.toLowerCase() ||
      selected.tenantId?.toLowerCase() !== config.tenantId.toLowerCase() || selected.state !== 'Enabled') {
    throw new Error('Entra preflight selected account differs from the exact approved subscription and tenant.');
  }
  const clusterName = config.clusterName ?? `${config.resourceGroup}-aks`;
  const cluster = execAz(['aks', 'show', '--resource-group', config.resourceGroup, '--name', clusterName,
    '--subscription', config.subscriptionId, '-o', 'json'], {
    ...COMMAND_OPTIONS,
    projectJson: value => ({
      id: value.id,
      defaultDomainEnabled: value.ingressProfile?.webAppRouting?.defaultDomain?.enabled,
      domainName: value.ingressProfile?.webAppRouting?.defaultDomain?.domainName,
    }),
    preserveProjectedJson: true,
  });
  if (cluster.status !== 0) {
    throw new Error(`Could not read the exact AKS managed Application Routing domain: ${redact(cluster.stderr)}. ` +
      'Provision the platform and its managed default domain first, then rerun the guarded Broker installer.');
  }
  const observedCluster = JSON.parse(cluster.stdout);
  const expectedClusterId = `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}` +
    `/providers/Microsoft.ContainerService/managedClusters/${clusterName}`;
  if (observedCluster.id?.toLowerCase() !== expectedClusterId.toLowerCase() ||
      observedCluster.defaultDomainEnabled !== true) {
    throw new Error('The exact AKS target does not expose an enabled managed Application Routing default domain. ' +
      'Complete the platform setup first; no callback can be inferred from a custom or missing domain.');
  }
  const hostname = managedBrokerHostnameFromDomain(observedCluster.domainName);
  const result = execAz(['ad', 'app', 'show', '--id', config.upstreamClientId,
    '--query', '{id:id,appId:appId,signInAudience:signInAudience,isFallbackPublicClient:isFallbackPublicClient,publicClient:publicClient}',
    '-o', 'json'], {
    ...COMMAND_OPTIONS, projectJson: projectPublicApplication, preserveProjectedJson: true,
  });
  if (result.status !== 0) {
    throw new Error(`Could not read the exact Entra client ${config.upstreamClientId} in tenant ${config.tenantId}: ` +
      `${redact(result.stderr)}. Verify it with \`az ad app show --id ${config.upstreamClientId} -o json\`; ` +
      'the signed-in account must be in the application tenant.');
  }
  const application = JSON.parse(result.stdout);
  if (!GUID_PATTERN.test(application.id ?? '') ||
      typeof application.appId !== 'string' ||
      application.appId.toLowerCase() !== config.upstreamClientId.toLowerCase()) {
    throw new Error(`Entra returned a different application for client ${config.upstreamClientId}. ` +
      `Verify the intended registration with \`az ad app show --id ${config.upstreamClientId} -o json\`; ` +
      'the installer will not create or select another registration.');
  }
  // Application Routing's managed domain is the source for the certificate and HTTPRoute host, including on first install.
  const callbackUri = `https://${hostname}/signin-oidc`;
  const plan = planPublicBrokerCallback(application, config.upstreamClientId, callbackUri, config.tenantId);
  const reconciled = registerPublicBrokerCallback(plan, config, { execAz });
  return assertPublicBrokerRegistration(reconciled);
}

/** Checks the routing receipt and live prerequisites required before broker runtime bootstrap. */
export function verifyIdentityBrokerReadiness(receipt, {
  execCurl = (args, options) => run('curl', args, options),
  execHttp2 = (args, options) => run(process.execPath, [HTTP2_ROUTE_PROBE, ...args], options),
} = {}) {
  if (!receipt?.routingConfigured || receipt.issuer !== `https://${receipt.hostname}/` ||
      !/^agentweaver\.[a-z0-9.-]+\.aksapp\.io$/.test(receipt.hostname) ||
      receipt.portalHostname !== `portal.${receipt.hostname.slice('agentweaver.'.length)}` ||
      !receipt.addresses?.length || receipt.addresses.some(address => !isIP(address))) {
    throw new Error('Broker readiness requires an independently derived managed routing receipt.');
  }
  for (const path of ['health/live', 'health/ready']) {
    const result = execCurl([
      '--disable', '--fail', '--silent', '--show-error', '--proto', '=https', '--tlsv1.2',
      '--connect-timeout', '10', '--max-time', '20',
      '--write-out', '\n%{http_code}\n%{remote_ip}', `${receipt.issuer}${path}`,
    ], COMMAND_OPTIONS);
    const lines = result.stdout.trim().split(/\r?\n/);
    const remoteIp = lines.pop();
    const status = lines.pop();
    if (result.status !== 0 || status !== '200' || !receipt.addresses.includes(remoteIp)) {
      throw new Error(`Broker HTTPS ${path} DNS/TLS/readiness verification failed: ${redact(result.stderr || `HTTP ${status}, remote IP ${remoteIp}`)}`);
    }
  }
  const crossHostRequests = [
    { servername: receipt.hostname, authority: receipt.portalHostname, path: '/' },
    { servername: receipt.portalHostname, authority: receipt.hostname, path: '/health/live' },
  ];
  for (const address of receipt.addresses) {
    for (const { servername, authority, path } of crossHostRequests) {
      const result = execHttp2([address, servername, authority, path], COMMAND_OPTIONS);
      if (result.status !== 0) {
        throw new Error(`HTTP/2 cross-host routing verification failed (SNI ${servername}, authority ${authority}, path ${path}): ${redact(result.stderr || 'probe failed')}`);
      }
      let response;
      try {
        response = JSON.parse(result.stdout);
      } catch {
        throw new Error(`HTTP/2 cross-host routing verification returned invalid probe output (SNI ${servername}, authority ${authority}, path ${path}).`);
      }
      if (response.alpn !== 'h2' || response.status !== 200) {
        throw new Error(`HTTP/2 cross-host routing verification failed (SNI ${servername}, authority ${authority}, path ${path}): ALPN ${response.alpn || 'none'}, HTTP ${response.status ?? 'unknown'}.`);
      }
    }
  }
  return {
    ...receipt, runtimeVerified: true, dnsVerified: true, httpsVerified: true, http2CoalescingVerified: true,
  };
}

/**
 * Verifies and applies identity broker certificates and routing for the dedicated P0 target.
 * Certificate-only mode stages the managed TLS Secret before workloads; full routing still checks the exact MCP Service.
 */
export function bootstrapIdentityRouting(config, {
  execAz = runAz, execKubelogin = (args, options) => run('kubelogin', args, options),
  execKubectl = (args, options) => run('kubectl', args, options), execCurl,
} = {}) {
  const certificatesOnly = config.identityRoutingCertificatesOnly === true;
  assertIdentityRoutingPlacement(config);
  if (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(config.upstreamClientId ?? '')) {
    throw new Error('Identity routing requires the exact upstream public application client ID.');
  }
  const account = execAz(['account', 'show', '-o', 'json'], {
    ...COMMAND_OPTIONS,
    projectJson: value => ({ id: value.id, tenantId: value.tenantId, state: value.state }),
    preserveProjectedJson: true,
  });
  if (account.status !== 0) throw new Error(`Routing account read failed: ${redact(account.stderr)}`);
  const selected = JSON.parse(account.stdout);
  if (selected.id?.toLowerCase() !== config.subscriptionId.toLowerCase() ||
      selected.tenantId?.toLowerCase() !== config.tenantId.toLowerCase() || selected.state !== 'Enabled') {
    throw new Error('Identity routing selected account/tenant changed; refusing native routing changes.');
  }
  const publicCallbackPlan = preflightPublicBrokerRegistration(config, { execAz });
  return withP0UserKubeconfig(config, baseArgs => {
    function command(args, options = {}) {
      const result = execKubectl([...baseArgs, '--request-timeout=30s', ...args], {
        ...COMMAND_OPTIONS,
        ...(args[0] === 'get' ? {
          projectJson: projectIdentityRoutingReadback, preserveProjectedJson: true,
        } : {}),
        ...options,
      });
      if (result.status !== 0) {
        throw new Error(`Identity routing Kubernetes operation failed: ${redact(result.stderr || result.stdout)}`);
      }
      return result.stdout;
    }
    function read(kind, namespace, name = 'identity-broker') {
      return JSON.parse(command(['get', kind, name, '--namespace', namespace, '-o', 'json']));
    }
    function apply(filename, replacements = {}) {
      const text = readFileSync(join(config.repoRoot ?? process.cwd(), 'deploy', 'k8s', 'routing',
        'identity-broker', filename), 'utf8');
      const rendered = Object.entries({
        'CHANGEME-GATEWAY-POLICY': config.gatewaySecurityPolicy, ...replacements,
      }).reduce((value, [placeholder, replacement]) => value.replaceAll(placeholder, replacement), text);
      if (rendered.includes('CHANGEME-')) throw new Error('Identity routing manifest has unresolved operator inputs.');
      command(['apply', '--filename', '-'], { input: rendered });
    }
    const p0 = JSON.parse(command(['get', 'namespace', P0_NAMESPACE, '-o', 'json']));
    for (const mode of ['enforce', 'audit', 'warn']) {
      if (p0.metadata?.labels?.[`pod-security.kubernetes.io/${mode}`] !== 'restricted') {
        throw new Error('Identity routing requires unchanged restricted P0 namespace security.');
      }
    }
    const gatewayClass = JSON.parse(command(['get', 'gatewayclass', 'approuting-istio', '-o', 'json']));
    if (gatewayClass.kind !== 'GatewayClass' || gatewayClass.metadata?.name !== 'approuting-istio' ||
        gatewayClass.spec?.controllerName !== CONTROLLER) {
      throw new Error('Native managed AKS GatewayClass is not available.');
    }
    requireConditions(gatewayClass, gatewayClass.status?.conditions, ['Accepted']);
    const existingObjects = new Map();
    for (const [kind, namespace, name, service] of [
      ['defaultdomaincertificate', GATEWAY_NAMESPACE, 'identity-broker', 'identity-broker'],
      ['defaultdomaincertificate', P0_NAMESPACE, 'identity-broker', 'identity-broker'],
      ['gateway', GATEWAY_NAMESPACE, 'identity-broker', 'identity-broker'],
      ['httproute', P0_NAMESPACE, 'identity-broker', 'identity-broker'],
      ['backendtlspolicy', P0_NAMESPACE, 'identity-broker', 'identity-broker'],
      ['httproute', P0_NAMESPACE, 'web-application', 'web'],
      ['httproute', P0_NAMESPACE, 'gateway-api', 'gateway'],
      ['backendtlspolicy', P0_NAMESPACE, 'gateway-api', 'gateway'],
      ['httproute', P0_NAMESPACE, 'mcp', 'mcp'],
      ['backendtlspolicy', P0_NAMESPACE, 'mcp', 'mcp'],
      ['service', P0_NAMESPACE, 'mcp', 'mcp'],
      ['ciliumnetworkpolicy', P0_NAMESPACE, 'identity-broker-gateway-ingress', 'identity-broker'],
      ['ciliumnetworkpolicy', P0_NAMESPACE, 'web-gateway-ingress', 'web'],
      ['ciliumnetworkpolicy', P0_NAMESPACE, 'gateway-api-ingress', 'gateway'],
      ['ciliumnetworkpolicy', P0_NAMESPACE, 'mcp-gateway-ingress', 'mcp'],
    ]) {
      // Certificate staging applies no route, so the full phase owns the MCP Service check.
      if (kind === 'service' && certificatesOnly) {
        existingObjects.set(`${kind}/${namespace}/${name}`, false);
        continue;
      }
      const object = JSON.parse(command(['get', kind, `--field-selector=metadata.name=${name}`,
        '--namespace', namespace, '-o', 'json']));
      if (object && (object.metadata?.name !== name || object.metadata?.namespace !== namespace ||
          object.metadata?.labels?.['agentweaver.io/service'] !== service)) {
        throw new Error(`Existing ${kind} is not an owned routing object. Refusing replacement.`);
      }
      existingObjects.set(`${kind}/${namespace}/${name}`, Boolean(object));
      if (kind === 'service' && (!object ||
          object.spec?.type !== 'ClusterIP' ||
           object.spec?.selector?.['app.kubernetes.io/name'] !== 'mcp' ||
           object.spec?.ports?.length !== 1 ||
           object.spec.ports[0].name !== 'https' || object.spec.ports[0].port !== 8443 ||
           object.spec.ports[0].targetPort !== 'https' || object.spec.ports[0].protocol !== 'TCP')) {
        throw new Error('Existing MCP Service is not the exact private HTTPS backend. Refusing routing changes.');
      }
    }
    const existing = JSON.parse(command(['get', 'namespace',
      `--field-selector=metadata.name=${config.gatewayNamespace}`, '-o', 'json']));
    if (existing) {
      const labels = existing.metadata?.labels;
      if (existing.metadata?.name !== config.gatewayNamespace ||
          labels?.['agentweaver.io/environment'] !== 'v1-p0' ||
          labels?.['agentweaver.io/managed-by'] !== 'kustomize' ||
          labels?.['agentweaver.io/workload'] !== 'identity-broker-gateway' ||
          labels?.['pod-security.kubernetes.io/enforce'] !== config.gatewaySecurityPolicy ||
          labels?.['pod-security.kubernetes.io/audit'] !== 'restricted' ||
          labels?.['pod-security.kubernetes.io/warn'] !== 'restricted') {
        throw new Error('Existing Gateway namespace ownership or security differs. No policy change is permitted.');
      }
    }
    const routingWrites = [
      ['defaultdomaincertificate', 'defaultdomaincertificates', GATEWAY_NAMESPACE, 'identity-broker'],
      ['defaultdomaincertificate', 'defaultdomaincertificates', P0_NAMESPACE, 'identity-broker'],
      ['gateway', 'gateways', GATEWAY_NAMESPACE, 'identity-broker'],
      ['httproute', 'httproutes', P0_NAMESPACE, 'identity-broker'],
      ['backendtlspolicy', 'backendtlspolicies', P0_NAMESPACE, 'identity-broker'],
      ['httproute', 'httproutes', P0_NAMESPACE, 'web-application'],
      ['httproute', 'httproutes', P0_NAMESPACE, 'gateway-api'],
      ['backendtlspolicy', 'backendtlspolicies', P0_NAMESPACE, 'gateway-api'],
      ['httproute', 'httproutes', P0_NAMESPACE, 'mcp'],
      ['backendtlspolicy', 'backendtlspolicies', P0_NAMESPACE, 'mcp'],
      ['ciliumnetworkpolicy', 'ciliumnetworkpolicies', P0_NAMESPACE, 'identity-broker-gateway-ingress'],
      ['ciliumnetworkpolicy', 'ciliumnetworkpolicies', P0_NAMESPACE, 'web-gateway-ingress'],
      ['ciliumnetworkpolicy', 'ciliumnetworkpolicies', P0_NAMESPACE, 'gateway-api-ingress'],
      ['ciliumnetworkpolicy', 'ciliumnetworkpolicies', P0_NAMESPACE, 'mcp-gateway-ingress'],
    ];
    const certificateWrites = certificatesOnly
      ? routingWrites.filter(([kind]) => kind === 'defaultdomaincertificate')
      : routingWrites;
    const writePermissions = existing ? [] : [['create', 'namespaces']];
    writePermissions.push(...certificateWrites.map(([kind, resource, namespace, name]) => [
      existingObjects.get(`${kind}/${namespace}/${name}`) ? 'patch' : 'create', resource, namespace,
    ]));
    assertP0KubernetesPermissions(baseArgs, execKubectl, writePermissions,
      certificatesOnly ? 'Identity routing certificate bootstrap' : 'Identity routing bootstrap');
    if (!existing) apply('gateway-namespace.yaml');
    for (const [filename, namespace] of [
      ['gateway-certificate.yaml', GATEWAY_NAMESPACE],
      ['default-domain-certificate.yaml', P0_NAMESPACE],
    ]) {
      apply(filename);
      command(['wait', '--for=condition=Available', 'defaultdomaincertificate/identity-broker',
        '--namespace', namespace, '--timeout=180s'], { timeout: 190_000 });
    }
    const gatewayCertificate = read('defaultdomaincertificate', GATEWAY_NAMESPACE);
    const brokerCertificate = read('defaultdomaincertificate', P0_NAMESPACE);
    const hostname = managedBrokerHostname(gatewayCertificate, GATEWAY_NAMESPACE);
    if (managedBrokerHostname(brokerCertificate, P0_NAMESPACE) !== hostname) {
      throw new Error('Managed Gateway and Broker certificate domains differ.');
    }
    const callbackUri = `https://${hostname}/signin-oidc`;
    // Keep the certificate-derived callback bound to preflight before applying any route.
    if (callbackUri !== publicCallbackPlan.callbackUri) {
      throw new Error('The certificate-derived callback differs from the preflighted AKS domain; route writes were not attempted.');
    }
    const portalHostname = managedPortalHostname(gatewayCertificate, GATEWAY_NAMESPACE);
    const wildcardHostname = managedWildcardHostname(hostname);
    // Return after both managed certificates converge; only the full phase may apply routes.
    if (certificatesOnly) {
      return {
        certificatesConfigured: true, hostname, portalHostname,
        callbackUri, publicCallbackPlan, routingConfigured: false, runtimeVerified: false,
      };
    }
    for (const [filename, replacements] of [
      ['gateway.yaml', {
        'CHANGEME-MANAGED-WILDCARD-HOST': wildcardHostname,
      }],
      ['httproute.yaml', { 'CHANGEME-MANAGED-HOST': hostname }],
      ['backend-tls-policy.yaml', { 'CHANGEME-MANAGED-HOST': hostname }],
      ['web-application.yaml', { 'CHANGEME-PORTAL-HOST': portalHostname }],
      ['gateway-api.yaml', { 'CHANGEME-PORTAL-HOST': portalHostname }],
      ['gateway-api-backend-tls.yaml', {}],
      ['gateway-mcp.yaml', { 'CHANGEME-PORTAL-HOST': portalHostname }],
      ['gateway-mcp-backend-tls.yaml', {}],
      ['gateway-ingress.yaml', {}],
      ['web-gateway-ingress.yaml', {}],
      ['gateway-api-ingress.yaml', {}],
      ['mcp-gateway-ingress.yaml', {}],
    ]) {
      apply(filename, replacements);
    }
    command(['wait', '--for=condition=Programmed', 'gateway/identity-broker',
      '--namespace', GATEWAY_NAMESPACE, '--timeout=180s'], { timeout: 190_000 });
    for (const [kind, name, field] of [
      ['httproute', 'identity-broker', 'parents'],
      ['backendtlspolicy', 'identity-broker', 'ancestors'],
      ['httproute', 'web-application', 'parents'],
      ['httproute', 'gateway-api', 'parents'],
      ['backendtlspolicy', 'gateway-api', 'ancestors'],
      ['httproute', 'mcp', 'parents'],
      ['backendtlspolicy', 'mcp', 'ancestors'],
    ]) {
      for (const condition of ['Accepted', 'ResolvedRefs']) {
        command(['wait',
          `--for=jsonpath={.status.${field}[?(@.controllerName=="${CONTROLLER}")].conditions[?(@.type=="${condition}")].status}=True`,
          `${kind}/${name}`, '--namespace', P0_NAMESPACE, '--timeout=180s'], { timeout: 190_000 });
      }
      const generation = read(kind, P0_NAMESPACE, name).metadata?.generation;
      if (!Number.isInteger(generation) || generation < 1) {
        throw new Error(`${kind}/${name} has no current native generation.`);
      }
      for (const condition of ['Accepted', 'ResolvedRefs']) {
        command(['wait',
          `--for=jsonpath={.status.${field}[?(@.controllerName=="${CONTROLLER}")].conditions[?(@.type=="${condition}")].observedGeneration}=${generation}`,
          `${kind}/${name}`, '--namespace', P0_NAMESPACE, '--timeout=180s'], { timeout: 190_000 });
      }
    }
    const receipt = deriveIdentityRouting({
      gatewayCertificate, brokerCertificate,
      gateway: read('gateway', GATEWAY_NAMESPACE),
      route: read('httproute', P0_NAMESPACE),
      backendPolicy: read('backendtlspolicy', P0_NAMESPACE),
      webRoute: read('httproute', P0_NAMESPACE, 'web-application'),
      apiRoute: read('httproute', P0_NAMESPACE, 'gateway-api'),
      apiBackendPolicy: read('backendtlspolicy', P0_NAMESPACE, 'gateway-api'),
      mcpRoute: read('httproute', P0_NAMESPACE, 'mcp'),
      mcpBackendPolicy: read('backendtlspolicy', P0_NAMESPACE, 'mcp'),
    });
    if (receipt.callbackUri !== publicCallbackPlan.callbackUri) {
      throw new Error('The route callback changed after preflight; routing was not accepted as a verified result.');
    }
    receipt.publicCallbackPlan = publicCallbackPlan;
    receipt.runtimeConfigInputs = {
      IdentityBroker__Issuer: receipt.issuer,
      IdentityBroker__ExternalProvider__ClientId: config.upstreamClientId,
    };
    receipt.runtimeConfigMutationExecuted = false;
    return config.verifyBrokerReadiness
      ? verifyIdentityBrokerReadiness(receipt, { execCurl })
      : receipt;
  }, { execAz, execKubelogin, execKubectl });
}
