import { readFileSync } from 'node:fs';
import { isIP } from 'node:net';
import { join } from 'node:path';
import { run, runAz, redact } from './exec.mjs';
import { withP0UserKubeconfig } from './namespace-bootstrap.mjs';

const P0_NAMESPACE = 'agentweaver-v1-p0';
const GATEWAY_NAMESPACE = 'agentweaver-v1-gateway';
const CONTROLLER = 'istio.aks.azure.com/gateway-controller';
const COMMAND_OPTIONS = { check: false, timeout: 35_000 };
const GUID_PATTERN = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;

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
  });
  switch (value?.kind) {
    case 'Namespace':
    case 'CiliumNetworkPolicy':
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
          caCertificateRefs: value.spec?.validation?.caCertificateRefs?.map(() => ({})),
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

function projectPublicApplication(value) {
  return { id: value?.id, appId: value?.appId, publicClient: { redirectUris: value?.publicClient?.redirectUris } };
}

export function assertIdentityRoutingPlacement({ gatewayNamespace, gatewaySecurityPolicy }) {
  if (gatewayNamespace !== GATEWAY_NAMESPACE ||
      !['restricted', 'baseline'].includes(gatewaySecurityPolicy)) {
    throw new Error('Identity routing requires explicit agentweaver-v1-gateway placement and an explicit restricted or baseline policy. No placement is selected by default.');
  }
}

function assertObject(object, kind, namespace) {
  const apiVersion = kind === 'DefaultDomainCertificate'
    ? 'approuting.kubernetes.azure.com/v1alpha1' : 'gateway.networking.k8s.io/v1';
  if (object?.kind !== kind || object.apiVersion !== apiVersion || object.metadata?.name !== 'identity-broker' ||
      object.metadata?.namespace !== namespace || !object.metadata?.uid ||
      !Number.isInteger(object.metadata?.generation) || object.metadata.generation < 1) {
    throw new Error(`Native ${kind} does not identify the exact Identity routing object.`);
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

export function managedBrokerHostname(certificate, namespace) {
  assertObject(certificate, 'DefaultDomainCertificate', namespace);
  requireConditions(certificate, certificate.status?.conditions, ['Available']);
  const domain = certificate.status?.domain;
  const labels = typeof domain === 'string' ? domain.slice(2).split('.') : [];
  if (certificate.spec?.target?.secret !== 'identity-broker-tls' ||
      !domain?.startsWith('*.') || !domain.endsWith('.aksapp.io') || domain.length > 242 ||
      labels.length < 4 || labels.some(label => !/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(label))) {
    throw new Error('Managed certificate must report an available AKS wildcard domain and the exact TLS Secret.');
  }
  return `agentweaver.${domain.slice(2)}`;
}

export function observeExistingP0BrokerRouting({ certificate, gateway, route, backendPolicy }) {
  const hostname = managedBrokerHostname(certificate, P0_NAMESPACE);
  const permittedHosts = [hostname, hostname.replace(/^agentweaver\./, 'identity-broker.')];
  const gatewayNamespace = route?.spec?.parentRefs?.[0]?.namespace;
  if (gatewayNamespace !== GATEWAY_NAMESPACE) {
    throw new Error('Existing P0 HTTPRoute does not identify the exact approved Gateway namespace.');
  }
  for (const [object, kind] of [[gateway, 'Gateway'], [route, 'HTTPRoute'], [backendPolicy, 'BackendTLSPolicy']]) {
    assertObject(object, kind, kind === 'Gateway' ? gatewayNamespace : P0_NAMESPACE);
    if (object.metadata.labels?.['agentweaver.io/service'] !== 'identity-broker' ||
        !object.metadata.resourceVersion) throw new Error('Existing P0 routing object is not the exact owned version.');
  }
  const listener = gateway.spec?.listeners?.[0];
  const reference = listener?.tls?.certificateRefs?.[0];
  const namespaces = listener?.allowedRoutes?.namespaces;
  const exactAttachment = namespaces?.from === 'Selector' &&
      namespaces.selector?.matchLabels?.['kubernetes.io/metadata.name'] === P0_NAMESPACE &&
      Object.keys(namespaces.selector.matchLabels).length === 1 &&
      !namespaces.selector.matchExpressions?.length;
  if (gateway.spec?.gatewayClassName !== 'approuting-istio' || gateway.spec.listeners?.length !== 1 ||
      listener?.name !== 'https' || !permittedHosts.includes(listener.hostname) ||
      listener.protocol !== 'HTTPS' || listener.port !== 443 || listener.tls?.mode !== 'Terminate' ||
      listener.tls.certificateRefs?.length !== 1 || reference?.name !== 'identity-broker-tls' ||
      (reference.kind ?? 'Secret') !== 'Secret' || (reference.group ?? '') !== '' ||
      (reference.namespace ?? gatewayNamespace) !== gatewayNamespace || !exactAttachment) {
    throw new Error('Existing P0 Gateway references or managed TLS configuration changed.');
  }
  const parent = route.spec?.parentRefs?.[0];
  const rule = route.spec?.rules?.[0];
  const backend = rule?.backendRefs?.[0];
  const match = rule?.matches?.[0];
  if (route.spec?.hostnames?.length !== 1 || !permittedHosts.includes(route.spec.hostnames[0]) ||
      route.spec.parentRefs?.length !== 1 || !gatewayReference(parent, gatewayNamespace, P0_NAMESPACE) ||
      parent.sectionName !== 'https' || route.spec.rules?.length !== 1 ||
      rule.backendRefs?.length !== 1 || rule.filters?.length || backend?.filters?.length ||
      backend?.name !== 'identity-broker' || (backend.namespace ?? P0_NAMESPACE) !== P0_NAMESPACE ||
      (backend.kind ?? 'Service') !== 'Service' || (backend.group ?? '') !== '' || backend.port !== 443 ||
      (backend.weight ?? 1) !== 1 || rule.matches?.length !== 1 ||
      Object.keys(match ?? {}).length !== 1 || match?.path?.type !== 'PathPrefix' || match.path.value !== '/') {
    throw new Error('Existing P0 HTTPRoute references or backend configuration changed.');
  }
  const target = backendPolicy.spec?.targetRefs?.[0];
  const validation = backendPolicy.spec?.validation;
  if (backendPolicy.spec?.targetRefs?.length !== 1 || target?.name !== 'identity-broker' ||
      (target.kind ?? 'Service') !== 'Service' || (target.group ?? '') !== '' ||
      (target.namespace ?? P0_NAMESPACE) !== P0_NAMESPACE || target.sectionName !== 'https' ||
      !permittedHosts.includes(validation?.hostname) || validation.wellKnownCACertificates !== 'System' ||
      validation.caCertificateRefs?.length || validation.subjectAltNames?.length) {
    throw new Error('Existing P0 BackendTLSPolicy target or system trust configuration changed.');
  }
  return {
    hostname,
    issuer: `https://${route.spec.hostnames[0]}/`,
    hostnameAligned: [listener.hostname, route.spec.hostnames[0], validation.hostname].every(value => value === hostname),
    gatewayProgrammed: gateway.status?.conditions?.some(condition => condition.type === 'Programmed' &&
      condition.status === 'True' && condition.observedGeneration === gateway.metadata.generation) ?? false,
    gatewayNamespace,
    configurationOnly: true,
    runtimeVerified: false,
  };
}

export function alignExistingP0BrokerHostname(config, {
  withKubeconfig = withP0UserKubeconfig,
  execKubectl = (args, options) => run('kubectl', args, options),
} = {}) {
  return withKubeconfig(config, base => {
    const args = [...base, '--request-timeout=30s'];
    function read(resource, namespace = P0_NAMESPACE) {
      const result = execKubectl([...args, '--namespace', namespace, 'get', resource, 'identity-broker', '-o', 'json'], {
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
    const observed = observeExistingP0BrokerRouting({ certificate, gateway, route, backendPolicy });
    const updates = [
      ['gateway', gateway, '/spec/listeners/0/hostname', gateway.spec.listeners[0].hostname, observed.hostname],
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
    });
    if (!receipt.hostnameAligned) throw new Error('Existing P0 hostname alignment did not read back the exact native hostname.');
    return { ...receipt, changed, routingBlocked: !receipt.gatewayProgrammed };
  });
}

function gatewayReference(reference, gatewayNamespace, objectNamespace) {
  return reference?.name === 'identity-broker' &&
    (reference.namespace ?? objectNamespace) === gatewayNamespace &&
    (reference.group ?? 'gateway.networking.k8s.io') === 'gateway.networking.k8s.io' &&
    (reference.kind ?? 'Gateway') === 'Gateway';
}

export function deriveIdentityRouting({ gatewayCertificate, brokerCertificate, gateway, route, backendPolicy }) {
  const hostname = managedBrokerHostname(gatewayCertificate, GATEWAY_NAMESPACE);
  if (managedBrokerHostname(brokerCertificate, P0_NAMESPACE) !== hostname) {
    throw new Error('Gateway and Broker managed certificates do not identify the same domain.');
  }
  assertObject(gateway, 'Gateway', GATEWAY_NAMESPACE);
  assertObject(route, 'HTTPRoute', P0_NAMESPACE);
  assertObject(backendPolicy, 'BackendTLSPolicy', P0_NAMESPACE);
  if (gateway.spec?.gatewayClassName !== 'approuting-istio') {
    throw new Error('Identity Gateway must use the native AKS approuting-istio Gateway class.');
  }
  requireConditions(gateway, gateway.status?.conditions, ['Accepted', 'Programmed']);
  const listener = gateway.spec?.listeners?.find(item => item.name === 'https');
  if (gateway.spec.listeners.length !== 1 || listener?.hostname !== hostname ||
      listener.port !== 443 || listener.protocol !== 'HTTPS' || listener.tls?.mode !== 'Terminate' ||
      listener.tls.certificateRefs?.length !== 1 ||
      listener.tls.certificateRefs[0].name !== 'identity-broker-tls' ||
      (listener.tls.certificateRefs[0].kind ?? 'Secret') !== 'Secret' ||
      (listener.tls.certificateRefs[0].group ?? '') !== '' ||
      (listener.tls.certificateRefs[0].namespace ?? GATEWAY_NAMESPACE) !== GATEWAY_NAMESPACE ||
      listener.allowedRoutes?.namespaces?.from !== 'Selector' ||
      listener.allowedRoutes.namespaces.selector?.matchLabels?.['kubernetes.io/metadata.name'] !== P0_NAMESPACE ||
      Object.keys(listener.allowedRoutes.namespaces.selector.matchLabels).length !== 1 ||
      listener.allowedRoutes.namespaces.selector.matchExpressions?.length) {
    throw new Error('Identity Gateway must terminate managed HTTPS and admit only the exact P0 namespace.');
  }
  const observedListener = gateway.status?.listeners?.find(item => item.name === 'https');
  requireConditions(gateway, observedListener?.conditions, ['Accepted', 'Programmed', 'ResolvedRefs']);
  if (route.spec?.hostnames?.length !== 1 || route.spec.hostnames[0] !== listener.hostname ||
      route.spec.parentRefs?.length !== 1 ||
      !gatewayReference(route.spec.parentRefs[0], GATEWAY_NAMESPACE, P0_NAMESPACE) ||
      route.spec.parentRefs[0].sectionName !== 'https') {
    throw new Error('Native HTTPRoute must identify exactly the admitted Gateway listener and hostname.');
  }
  const rules = route.spec?.rules;
  const backend = rules?.[0]?.backendRefs?.[0];
  if (rules?.length !== 1 || rules[0].backendRefs?.length !== 1 ||
      backend?.name !== 'identity-broker' || backend.port !== 443 ||
      (backend.namespace ?? P0_NAMESPACE) !== P0_NAMESPACE ||
      (backend.kind ?? 'Service') !== 'Service' || (backend.group ?? '') !== '' ||
      (backend.weight ?? 1) !== 1 || rules[0].filters?.length ||
      backend.filters?.length || rules[0].matches?.length !== 1 ||
      rules[0].matches[0].path?.type !== 'PathPrefix' ||
      rules[0].matches[0].path?.value !== '/' ||
      Object.keys(rules[0].matches[0]).length !== 1) {
    throw new Error('Native HTTPRoute must forward the complete path to the exact HTTPS Broker Service.');
  }
  const parent = route.status?.parents?.find(item => item.controllerName === CONTROLLER &&
    gatewayReference(item.parentRef, GATEWAY_NAMESPACE, P0_NAMESPACE) &&
    item.parentRef.sectionName === 'https');
  requireConditions(route, parent?.conditions, ['Accepted', 'ResolvedRefs']);
  const target = backendPolicy.spec?.targetRefs?.[0];
  if (backendPolicy.spec?.targetRefs?.length !== 1 || target?.name !== 'identity-broker' ||
      target.kind !== 'Service' || (target.group ?? '') !== '' || target.sectionName !== 'https' ||
      backendPolicy.spec.validation?.hostname !== route.spec.hostnames[0] ||
      backendPolicy.spec.validation.wellKnownCACertificates !== 'System' ||
      backendPolicy.spec.validation.caCertificateRefs?.length ||
      backendPolicy.spec.validation.subjectAltNames?.length) {
    throw new Error('Broker backend TLS must use the exact route hostname and system certificate trust.');
  }
  const managedAncestors = backendPolicy.status?.ancestors?.filter(item => item.controllerName === CONTROLLER);
  const ancestorReference = managedAncestors?.length === 1 ? managedAncestors[0].ancestorRef : undefined;
  // AKS may omit this status namespace; bind it to the independently verified route parent.
  const scopedAncestorReference = {
    ...ancestorReference, namespace: ancestorReference?.namespace ?? parent.parentRef.namespace,
  };
  const ancestor = managedAncestors?.length === 1 &&
    gatewayReference(scopedAncestorReference, gateway.metadata.namespace, P0_NAMESPACE) &&
    (ancestorReference?.sectionName === undefined || ancestorReference.sectionName === parent.parentRef.sectionName)
    ? managedAncestors[0] : undefined;
  requireConditions(backendPolicy, ancestor?.conditions, ['Accepted', 'ResolvedRefs']);
  const addresses = gateway.status?.addresses?.filter(item => item.type === 'IPAddress' && isIP(item.value))
    .map(item => item.value);
  if (!addresses?.length) throw new Error('Programmed Gateway has no observed public IP address.');
  const issuer = `https://${route.spec.hostnames[0]}/`;
  return {
    hostname: route.spec.hostnames[0], issuer, callbackUri: `${issuer}signin-oidc`, addresses,
    gatewayUid: gateway.metadata.uid, routeUid: route.metadata.uid,
    routingConfigured: true, runtimeVerified: false,
  };
}

export function planPublicBrokerCallback(application, clientId, callbackUri, tenantId) {
  const callback = new URL(callbackUri);
  if (!GUID_PATTERN.test(application?.id ?? '') || !GUID_PATTERN.test(clientId ?? '') ||
      !GUID_PATTERN.test(tenantId ?? '') || application?.appId?.toLowerCase() !== clientId?.toLowerCase() ||
      callback.protocol !== 'https:' || callback.pathname !== '/signin-oidc' ||
      callback.search || callback.hash || callback.username || callback.password ||
      !callback.hostname.startsWith('agentweaver.') || !callback.hostname.endsWith('.aksapp.io') ||
      !Array.isArray(application.publicClient?.redirectUris) ||
      application.publicClient.redirectUris.some(uri => typeof uri !== 'string')) {
    throw new Error('Public callback plan requires the exact upstream application and managed Broker callback URI.');
  }
  const existing = application.publicClient.redirectUris;
  const unchanged = existing.includes(callbackUri);
  return {
    tenantId, clientId, applicationObjectId: application.id,
    patchUri: `https://graph.microsoft.com/v1.0/applications/${application.id}`,
    platform: 'publicClient', action: unchanged ? 'noop' : 'append',
    redirectUris: unchanged ? [...existing] : [...existing, callbackUri],
    callbackUri, mutationExecuted: false,
  };
}

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
      '--query', '{id:id,appId:appId,publicClient:publicClient}', '-o', 'json'], {
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
  if (fresh.action === 'noop') return fresh;
  const patch = execAz(['rest', '--method', 'PATCH', '--uri', fresh.patchUri,
    '--subscription', config.subscriptionId, '--headers', 'Content-Type=application/json',
    '--body', JSON.stringify({ publicClient: { redirectUris: fresh.redirectUris } }), '-o', 'none'], COMMAND_OPTIONS);
  if (patch.status !== 0) {
    throw new Error(`Public callback append failed; no permissions were granted: ${redact(patch.stderr)}`);
  }
  const verified = readApplication();
  if (verified.action !== 'noop' || fresh.redirectUris.some(uri => !verified.redirectUris.includes(uri))) {
    throw new Error('Public callback append returned success but the exact URI or previous public redirects are missing.');
  }
  return { ...verified, action: 'append', mutationExecuted: true };
}

export function verifyIdentityBrokerReadiness(receipt, {
  execCurl = (args, options) => run('curl', args, options),
} = {}) {
  if (!receipt?.routingConfigured || receipt.issuer !== `https://${receipt.hostname}/` ||
      !/^agentweaver\.[a-z0-9.-]+\.aksapp\.io$/.test(receipt.hostname) ||
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
  return { ...receipt, runtimeVerified: true, dnsVerified: true, httpsVerified: true };
}

export function bootstrapIdentityRouting(config, {
  execAz = runAz, execKubelogin = (args, options) => run('kubelogin', args, options),
  execKubectl = (args, options) => run('kubectl', args, options), execCurl,
} = {}) {
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
    function read(kind, namespace) {
      return JSON.parse(command(['get', kind, 'identity-broker', '--namespace', namespace, '-o', 'json']));
    }
    function apply(filename, replacements = {}) {
      const text = readFileSync(join(config.repoRoot ?? process.cwd(), 'deploy', 'k8s', 'routing',
        'identity-broker', filename), 'utf8');
      const rendered = Object.entries({
        'CHANGEME-GATEWAY-NAMESPACE': config.gatewayNamespace,
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
    const application = execAz(['ad', 'app', 'show', '--id', config.upstreamClientId,
      '--query', '{id:id,appId:appId,publicClient:publicClient}', '-o', 'json'], {
      ...COMMAND_OPTIONS, projectJson: projectPublicApplication, preserveProjectedJson: true,
    });
    if (application.status !== 0) {
      throw new Error(`Public callback read failed: ${redact(application.stderr)}`);
    }
    const publicApplication = JSON.parse(application.stdout);
    if (!GUID_PATTERN.test(publicApplication.id ?? '') ||
        publicApplication.appId?.toLowerCase() !== config.upstreamClientId.toLowerCase() ||
        !Array.isArray(publicApplication.publicClient?.redirectUris)) {
      throw new Error('Exact upstream public application metadata is unavailable.');
    }
    for (const [kind, namespace, name] of [
      ['defaultdomaincertificate', GATEWAY_NAMESPACE, 'identity-broker'],
      ['defaultdomaincertificate', P0_NAMESPACE, 'identity-broker'],
      ['gateway', GATEWAY_NAMESPACE, 'identity-broker'],
      ['httproute', P0_NAMESPACE, 'identity-broker'],
      ['backendtlspolicy', P0_NAMESPACE, 'identity-broker'],
      ['ciliumnetworkpolicy', P0_NAMESPACE, 'identity-broker-gateway-ingress'],
    ]) {
      const object = JSON.parse(command(['get', kind, `--field-selector=metadata.name=${name}`,
        '--namespace', namespace, '-o', 'json']));
      if (object && (object.metadata?.name !== name || object.metadata?.namespace !== namespace ||
          object.metadata?.labels?.['agentweaver.io/service'] !== 'identity-broker')) {
        throw new Error(`Existing ${kind} is not an owned Identity routing object. Refusing replacement.`);
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
    } else {
      apply('gateway-namespace.yaml');
    }
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
    for (const filename of ['gateway.yaml', 'httproute.yaml', 'backend-tls-policy.yaml', 'gateway-ingress.yaml']) {
      apply(filename, { 'CHANGEME-MANAGED-HOST': hostname });
    }
    command(['wait', '--for=condition=Programmed', 'gateway/identity-broker',
      '--namespace', GATEWAY_NAMESPACE, '--timeout=180s'], { timeout: 190_000 });
    for (const [kind, field] of [['httproute', 'parents'], ['backendtlspolicy', 'ancestors']]) {
      for (const condition of ['Accepted', 'ResolvedRefs']) {
        command(['wait',
          `--for=jsonpath={.status.${field}[?(@.controllerName=="${CONTROLLER}")].conditions[?(@.type=="${condition}")].status}=True`,
          `${kind}/identity-broker`, '--namespace', P0_NAMESPACE, '--timeout=180s'], { timeout: 190_000 });
      }
      const generation = read(kind, P0_NAMESPACE).metadata?.generation;
      if (!Number.isInteger(generation) || generation < 1) {
        throw new Error(`${kind} has no current native generation.`);
      }
      for (const condition of ['Accepted', 'ResolvedRefs']) {
        command(['wait',
          `--for=jsonpath={.status.${field}[?(@.controllerName=="${CONTROLLER}")].conditions[?(@.type=="${condition}")].observedGeneration}=${generation}`,
          `${kind}/identity-broker`, '--namespace', P0_NAMESPACE, '--timeout=180s'], { timeout: 190_000 });
      }
    }
    const receipt = deriveIdentityRouting({
      gatewayCertificate, brokerCertificate,
      gateway: read('gateway', GATEWAY_NAMESPACE),
      route: read('httproute', P0_NAMESPACE),
      backendPolicy: read('backendtlspolicy', P0_NAMESPACE),
    });
    receipt.publicCallbackPlan = registerPublicBrokerCallback(planPublicBrokerCallback(
      publicApplication, config.upstreamClientId, receipt.callbackUri, config.tenantId), config, { execAz });
    receipt.runtimeConfigInputs = {
      IdentityBroker__Issuer: receipt.issuer,
      IdentityBroker__ExternalProvider__ClientId: config.upstreamClientId,
    };
    receipt.runtimeConfigMutationExecuted = false;
    return config.verifyBrokerReadiness
      ? verifyIdentityBrokerReadiness(receipt, { execCurl })
      : receipt;
  }, { execAz, execKubelogin });
}
