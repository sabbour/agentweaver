import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { run, runAz, redact } from './exec.mjs';
import { withP0UserKubeconfig } from './namespace-bootstrap.mjs';
import { foundationProbeEgress } from './foundation-probe-egress.mjs';

const NAMESPACE = 'agentweaver-v1-p0';
const OPTIONS = { check: false, timeout: 35_000 };
const GUID = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const hash = value => createHash('sha256').update(value).digest('hex');

export function bootstrapFoundationProbeInputs(config, {
  execAz = runAz, execKubectl = (args, options) => run('kubectl', args, options),
  withKubeconfig = withP0UserKubeconfig,
} = {}) {
  if (config.resourceGroup !== 'aw-v1-p0' || config.clusterName !== 'aw-v1-p0-aks' ||
      !GUID.test(config.subscriptionId ?? '') || !GUID.test(config.tenantId ?? '')) {
    throw new Error('Probe inputs require the explicit dedicated P0 target.');
  }
  function azure(args, projectJson) {
    const result = execAz(args, { ...OPTIONS, projectJson, preserveProjectedJson: true });
    if (result.status !== 0) throw new Error(`Probe input lookup failed: ${redact(result.stderr)}`);
    return JSON.parse(result.stdout);
  }
  const account = azure(['account', 'show', '-o', 'json'],
    value => ({ id: value.id, tenantId: value.tenantId, state: value.state }));
  if (account.id !== config.subscriptionId || account.tenantId !== config.tenantId || account.state !== 'Enabled') {
    throw new Error('Probe inputs account differs from the approved subscription and tenant.');
  }
  const identityName = 'aw-v1-p0-id-foundation-probe';
  const identity = azure(['identity', 'show', '--resource-group', config.resourceGroup, '--name', identityName,
    '--subscription', config.subscriptionId, '-o', 'json'],
  value => ({ id: value.id, clientId: value.clientId, principalId: value.principalId }));
  const prefix = `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}/providers/`;
  if (identity.id?.toLowerCase() !== `${prefix}Microsoft.ManagedIdentity/userAssignedIdentities/${identityName}`.toLowerCase() ||
      !GUID.test(identity.clientId ?? '') || !GUID.test(identity.principalId ?? '')) {
    throw new Error('Probe inputs identity does not match the existing exact dedicated identity.');
  }
  const cluster = azure(['aks', 'show', '--resource-group', config.resourceGroup, '--name', config.clusterName,
    '--subscription', config.subscriptionId, '-o', 'json'], value => ({ issuer: value.oidcIssuerProfile?.issuerUrl }));
  const fic = azure(['identity', 'federated-credential', 'show', '--resource-group', config.resourceGroup,
    '--identity-name', identityName, '--name', 'foundation-probe-workload-identity',
    '--subscription', config.subscriptionId, '-o', 'json'],
  value => ({ issuer: value.issuer, subject: value.subject, audiences: value.audiences }));
  if (!cluster.issuer?.startsWith(`https://eastus2euap.oic.prod-aks.azure.com/${config.tenantId}/`) ||
      fic.issuer !== cluster.issuer || fic.subject !== `system:serviceaccount:${NAMESPACE}:foundation-probe` ||
      fic.audiences?.length !== 1 || fic.audiences[0] !== 'api://AzureADTokenExchange') {
    throw new Error('Probe inputs require the existing exact native federation; no credential or role was created.');
  }
  let connectionString;
  const component = azure(['monitor', 'app-insights', 'component', 'show', '--app', 'aw-v1-p0-appi',
    '--resource-group', config.resourceGroup, '--subscription', config.subscriptionId, '-o', 'json'], value => {
    // Retain the service-owned installation value only in this closure; surfaced output is metadata.
    connectionString = value.connectionString;
    return { id: value.id, configured: typeof connectionString === 'string' && connectionString.length > 0 };
  });
  if (component.id?.toLowerCase() !== `${prefix}Microsoft.Insights/components/aw-v1-p0-appi`.toLowerCase() ||
      !component.configured || !/InstrumentationKey=[0-9a-f-]{36}(?:;|$)/i.test(connectionString)) {
    throw new Error('Probe Monitor component does not provide the exact native installation configuration.');
  }
  const secretValueHash = hash(connectionString);
  const ingestionEndpoint = connectionString.split(';').find(value => value.startsWith('IngestionEndpoint='))?.slice(18);
  const ingestionUrl = new URL(ingestionEndpoint);
  if (ingestionUrl.protocol !== 'https:' || ingestionUrl.port || ingestionUrl.username || ingestionUrl.password ||
      ingestionUrl.pathname !== '/' || ingestionUrl.search || ingestionUrl.hash) {
    throw new Error('Probe ingestion endpoint must use ordinary HTTPS without overrides.');
  }
  const endpoint = azure(['network', 'private-endpoint', 'show', '--resource-group', config.resourceGroup,
    '--name', 'aw-v1-p0-ampls-pe', '--subscription', config.subscriptionId, '-o', 'json'], value => value);
  const nicId = endpoint.networkInterfaces?.[0]?.id;
  if (typeof nicId !== 'string' ||
      !nicId.toLowerCase().startsWith(`${prefix}Microsoft.Network/networkInterfaces/aw-v1-p0-ampls-pe.nic.`.toLowerCase())) {
    throw new Error('Probe AMPLS NIC is outside the exact dedicated endpoint.');
  }
  const nic = azure(['network', 'nic', 'show', '--ids', nicId, '--subscription', config.subscriptionId, '-o', 'json'],
    value => value);
  const record = azure(['network', 'private-dns', 'record-set', 'a', 'show', '--resource-group', config.resourceGroup,
    '--zone-name', 'privatelink.monitor.azure.com', '--name', 'api',
    '--subscription', config.subscriptionId, '-o', 'json'], value => value);
  const egress = foundationProbeEgress(config, { endpoint, nic, record, ingestionHost: ingestionUrl.hostname });
  try {
    return withKubeconfig(config, base => {
      const args = [...base, '--namespace', NAMESPACE, '--request-timeout=30s'];
      function read(resource, name) {
        const result = execKubectl([...args, 'get', resource, '--field-selector', `metadata.name=${name}`, '-o', 'json'],
          { ...OPTIONS, preserveProjectedJson: true, projectJson: value => {
            if (value.items?.length > 1) throw new Error('Probe inputs exact lookup returned multiple objects.');
            const object = value.items?.[0];
            if (!object) return null;
            return {
              kind: object.kind, name: object.metadata?.name, namespace: object.metadata?.namespace,
              uid: object.metadata?.uid, annotations: object.kind === 'ServiceAccount' ? {
                clientId: object.metadata?.annotations?.['azure.workload.identity/client-id'],
                tenantId: object.metadata?.annotations?.['azure.workload.identity/tenant-id'],
              } : undefined,
              type: object.kind === 'Secret' ? object.type : undefined,
              valueHash: object.kind === 'Secret'
                ? hash(Buffer.from(object.data?.APPLICATIONINSIGHTS_CONNECTION_STRING ?? '', 'base64')) : undefined,
              keys: object.kind === 'Secret' ? Object.keys(object.data ?? {}) : undefined,
            };
          } });
        if (result.status !== 0) throw new Error(`Probe inputs native readback failed: ${redact(result.stderr)}`);
        return JSON.parse(result.stdout);
      }
      const serviceAccount = read('serviceaccounts', 'foundation-probe');
      const monitor = read('secrets', 'foundation-probe-monitor');
      function assertServiceAccount(value) {
        if (value?.kind !== 'ServiceAccount' || value.namespace !== NAMESPACE ||
            value.name !== 'foundation-probe' || !value.uid ||
            value.annotations.clientId !== identity.clientId || value.annotations.tenantId !== config.tenantId) {
          throw new Error('Existing Probe ServiceAccount differs from the exact existing federation; refusing replacement.');
        }
      }
      function assertMonitor(value) {
        if (value?.kind !== 'Secret' || value.namespace !== NAMESPACE ||
            value.name !== 'foundation-probe-monitor' || !value.uid || value.type !== 'Opaque' ||
            value.keys?.length !== 1 || value.keys[0] !== 'APPLICATIONINSIGHTS_CONNECTION_STRING' ||
            value.valueHash !== secretValueHash) {
          throw new Error('Existing Probe Monitor Secret differs from native install configuration; refusing replacement.');
        }
      }
      if (serviceAccount) assertServiceAccount(serviceAccount);
      if (monitor) assertMonitor(monitor);
      function readPolicy() {
        const result = execKubectl([...args, 'get', 'ciliumnetworkpolicies',
          '--field-selector', 'metadata.name=foundation-probe-egress', '-o', 'json'],
        { ...OPTIONS, preserveProjectedJson: true, projectJson: value => {
          if (!Array.isArray(value.items) || value.items.length > 1) throw new Error('Unexpected Probe policy lookup.');
          const object = value.items[0];
          return object ? { apiVersion: object.apiVersion, kind: object.kind,
            metadata: object.metadata, spec: object.spec } : null;
        } });
        if (result.status !== 0) throw new Error(`Probe policy read failed: ${redact(result.stderr)}`);
        return JSON.parse(result.stdout);
      }
      const policy = readPolicy();
      const equal = (left, right) => JSON.stringify(canonical(left)) === JSON.stringify(canonical(right));
      function canonical(value) {
        if (Array.isArray(value)) return value.map(canonical);
        return value && typeof value === 'object'
          ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value;
      }
      if (policy && (policy.apiVersion !== 'cilium.io/v2' || policy.kind !== 'CiliumNetworkPolicy' ||
          policy.metadata?.name !== 'foundation-probe-egress' || policy.metadata.namespace !== NAMESPACE ||
          !policy.metadata.uid || !policy.metadata.resourceVersion ||
          !equal(policy.spec, egress.baseline) && !equal(policy.spec, egress.spec))) {
        throw new Error('Existing Probe policy differs from the admitted exact baseline; refusing replacement.');
      }
      const policyChanged = !policy || !equal(policy.spec, egress.spec);
      if (policyChanged) {
        const manifest = { apiVersion: 'cilium.io/v2', kind: 'CiliumNetworkPolicy',
          metadata: policy?.metadata ?? { name: 'foundation-probe-egress', namespace: NAMESPACE }, spec: egress.spec };
        const result = execKubectl([...args, policy ? 'replace' : 'create', '--filename', '-', '-o', 'name'],
          { ...OPTIONS, input: JSON.stringify(manifest) });
        if (result.status !== 0) throw new Error(`Probe policy write failed: ${redact(result.stderr)}`);
      }
      const policyReadback = readPolicy();
      if (policyReadback?.metadata?.name !== 'foundation-probe-egress' ||
          policyReadback.metadata.namespace !== NAMESPACE || !policyReadback.metadata.uid ||
          policy && policyReadback.metadata.uid !== policy.metadata.uid ||
          !equal(policyReadback.spec, egress.spec)) {
        throw new Error('Probe policy readback differs from the exact private Logs egress.');
      }
      if (!serviceAccount) {
        const manifest = readFileSync(join(config.repoRoot ?? process.cwd(), 'deploy', 'k8s', 'base',
          'serviceaccounts', 'foundation-probe-sa.yaml'), 'utf8')
          .replaceAll('CHANGEME-foundation-probe-client-id', identity.clientId)
          .replaceAll('CHANGEME-tenant-id', config.tenantId);
        const result = execKubectl([...args, 'create', '--filename', '-', '-o', 'name'], { ...OPTIONS, input: manifest });
        if (result.status !== 0) throw new Error(`Probe ServiceAccount create failed: ${redact(result.stderr)}`);
        assertServiceAccount(read('serviceaccounts', 'foundation-probe'));
      }
      if (!monitor) {
        const manifest = { apiVersion: 'v1', kind: 'Secret', metadata: {
          name: 'foundation-probe-monitor', namespace: NAMESPACE,
          labels: { 'agentweaver.io/service': 'foundation-probe' },
        }, type: 'Opaque', immutable: true,
        data: { APPLICATIONINSIGHTS_CONNECTION_STRING: Buffer.from(connectionString).toString('base64') } };
        const result = execKubectl([...args, 'create', '--filename', '-', '-o', 'name'],
          { ...OPTIONS, input: JSON.stringify(manifest) });
        if (result.status !== 0) throw new Error(`Probe Monitor Secret create failed (exit ${result.status}); private output omitted.`);
        assertMonitor(read('secrets', 'foundation-probe-monitor'));
      }
      return {
        namespace: NAMESPACE, serviceAccount: { name: 'foundation-probe', created: !serviceAccount },
        monitor: { name: 'foundation-probe-monitor', created: !monitor, appInsightsResourceId: component.id },
        egress: { name: 'foundation-probe-egress', changed: policyChanged,
          logsHost: egress.logsHost, privateAddress: egress.privateAddress,
          endpointId: egress.endpointId, nicId: egress.nicId, recordId: egress.recordId },
        runtimeVerified: false,
      };
    });
  } finally {
    connectionString = undefined;
  }
}
