import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const namespace = 'agentweaver-v1-p0';
const service = 'projects-config';
const tenantId = '72f988bf-86f1-41af-91ab-2d7cd011db47';
const clientId = '4ca461a5-f347-4ed8-9923-a6e135145aaa';
const postgresHost = 'aw-v1-p0-pg.postgres.database.azure.com';
const fixedConfiguration = {
  ConnectionStrings__ProjectsConfig: `Host=${postgresHost};Port=5432;Database=agentweaver;Username=aw-v1-p0-id-projects-config;SSL Mode=VerifyFull`,
  ProjectsConfig__Database__WorkloadIdentity__TenantId: tenantId,
  ProjectsConfig__Database__WorkloadIdentity__ClientId: clientId,
  ProjectsConfig__Database__WorkloadIdentity__TokenFilePath: '/var/run/secrets/azure/tokens/azure-identity-token',
};
const requiredConfiguration = [
  ...Object.keys(fixedConfiguration),
  'ProjectsConfig__Authentication__Authority',
  'ProjectsConfig__Authentication__Audience',
];

function exactFields(value, fields, location) {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new Error(`${location} must be an explicit object.`);
  for (const field of Object.keys(value)) {
    if (!fields.includes(field)) throw new Error(`${location}: unsupported field '${field}'.`);
  }
  for (const field of fields) {
    if (!Object.hasOwn(value, field)) throw new Error(`${location}: missing field '${field}'.`);
  }
}

function positiveInteger(value, location) {
  if (!Number.isSafeInteger(value) || value < 1 || value > 2147483647)
    throw new Error(`${location} must be an explicit positive integer no greater than 2147483647.`);
}

function issuerHost(value) {
  if (!/^https:\/\/agentweaver\.[a-z0-9.-]+\.aksapp\.io\/$/.test(value))
    throw new Error('Projects authority requires the existing managed Broker HTTPS root on port 443.');
  let uri;
  try { uri = new URL(value); } catch {
    throw new Error('Projects authority must be a valid HTTPS root URI.');
  }
  return uri.hostname;
}

export function renderProjectsProfile(input) {
  exactFields(input, ['subscriptionId', 'resourceGroup', 'clusterName', 'image',
    'imageUser', 'imageGroup', 'tlsSecretName', 'providerCatalog', 'configuration'], 'Projects profile');
  if (input.subscriptionId !== '90ab3701-83f0-4ba1-a90a-f2e68683adab' ||
      input.resourceGroup !== 'aw-v1-p0' || input.clusterName !== 'aw-v1-p0-aks')
    throw new Error('Projects profile requires the exact existing approved 1.x target.');
  if (typeof input.image !== 'string' ||
      !/^ghcr\.io\/sabbour\/agentweaver\.projects\.config@sha256:[0-9a-f]{64}$/.test(input.image) ||
      input.image.endsWith('0'.repeat(64)))
    throw new Error('Projects profile requires a nonzero immutable product Projects image digest.');
  positiveInteger(input.imageUser, 'Operator-reviewed Projects image user');
  positiveInteger(input.imageGroup, 'Operator-reviewed Projects image group');
  if (typeof input.tlsSecretName !== 'string' || input.tlsSecretName.length > 63 ||
      !/^projects-config(?:-[a-z0-9]+)*$/.test(input.tlsSecretName))
    throw new Error('Supply the exact existing Projects TLS Secret name, not another service secret.');

  const catalog = input.providerCatalog;
  exactFields(catalog, ['configMapName', 'sourceCommit'], 'Existing provider catalog');
  if (typeof catalog.configMapName !== 'string' || catalog.configMapName.length > 253 ||
      !/^[a-z0-9](?:[-a-z0-9]*[a-z0-9])?(?:\.[a-z0-9](?:[-a-z0-9]*[a-z0-9])?)*$/.test(catalog.configMapName) ||
      catalog.configMapName === `${service}-runtime-config`)
    throw new Error('Supply the exact existing provider catalog ConfigMap name, separate from runtime configuration.');
  if (typeof catalog.sourceCommit !== 'string' || !/^[0-9a-f]{40}$/.test(catalog.sourceCommit) ||
      catalog.sourceCommit === '0'.repeat(40))
    throw new Error('The operator-reviewed provider catalog requires an explicit nonzero source commit.');

  const data = input.configuration;
  exactFields(data, requiredConfiguration, 'Projects configuration');
  for (const key of requiredConfiguration) {
    if (typeof data[key] !== 'string' || !data[key].trim() ||
        data[key] !== data[key].trim() || /[\u0000-\u001f\u007f]/.test(data[key]))
      throw new Error(`Projects configuration: '${key}' requires an explicit nonempty string.`);
  }
  for (const [key, value] of Object.entries(fixedConfiguration)) {
    if (data[key] !== value) throw new Error(`Projects configuration: '${key}' differs from the admitted Projects runtime mapping.`);
  }
  const issuer = issuerHost(data.ProjectsConfig__Authentication__Authority);
  const labels = { 'app.kubernetes.io/name': service, 'agentweaver.io/service': service };
  const metadata = name => ({ name, namespace, labels: { ...labels } });
  const endpoint = name => ({ matchLabels: {
    'k8s:io.kubernetes.pod.namespace': namespace,
    'agentweaver.io/workload': name,
  } });
  const tcp = port => ({ ports: [{ port, protocol: 'TCP' }] });
  const externalHosts = [issuer, 'login.microsoftonline.com', postgresHost];
  const dnsNames = [...externalHosts, 'aw-v1-p0-pg.privatelink.postgres.database.azure.com'];
  return {
    apiVersion: 'v1',
    kind: 'List',
    items: [
      { apiVersion: 'v1', kind: 'ServiceAccount', metadata: {
        ...metadata(service), annotations: {
          'azure.workload.identity/client-id': clientId,
          'azure.workload.identity/tenant-id': tenantId,
        },
      }, automountServiceAccountToken: true },
      { apiVersion: 'v1', kind: 'ConfigMap', metadata: metadata(`${service}-runtime-config`),
        immutable: true, data: { ...data } },
      { apiVersion: 'v1', kind: 'Service', metadata: metadata(service), spec: {
        type: 'ClusterIP', selector: { 'app.kubernetes.io/name': service },
        ports: [{ name: 'https', port: 8443, targetPort: 'https', protocol: 'TCP' }],
      } },
      { apiVersion: 'apps/v1', kind: 'Deployment', metadata: metadata(service), spec: {
        replicas: 1, strategy: { type: 'Recreate' },
        selector: { matchLabels: { 'app.kubernetes.io/name': service } },
        template: {
          metadata: { labels: { ...labels, 'agentweaver.io/workload': service,
            'azure.workload.identity/use': 'true' },
          annotations: { 'agentweaver.io/provider-catalog-source': catalog.sourceCommit } },
          spec: {
            serviceAccountName: service, automountServiceAccountToken: true,
            securityContext: { runAsNonRoot: true, runAsUser: input.imageUser,
              runAsGroup: input.imageGroup, fsGroup: input.imageGroup,
              fsGroupChangePolicy: 'OnRootMismatch', seccompProfile: { type: 'RuntimeDefault' } },
            terminationGracePeriodSeconds: 30,
            containers: [{
              name: service, image: input.image, imagePullPolicy: 'IfNotPresent',
              ports: [{ name: 'https', containerPort: 8443, protocol: 'TCP' }],
              env: [
                { name: 'ASPNETCORE_URLS', value: 'https://+:8443' },
                { name: 'ASPNETCORE_Kestrel__Certificates__Default__Path', value: '/var/run/projects-config-tls/tls.crt' },
                { name: 'ASPNETCORE_Kestrel__Certificates__Default__KeyPath', value: '/var/run/projects-config-tls/tls.key' },
              ],
              envFrom: [
                { configMapRef: { name: `${service}-runtime-config`, optional: false } },
                { prefix: 'ProjectsConfig__ProviderCatalog__',
                  configMapRef: { name: catalog.configMapName, optional: false } },
              ],
              volumeMounts: [
                { name: 'tls', mountPath: '/var/run/projects-config-tls', readOnly: true },
                { name: 'tmp', mountPath: '/tmp' },
              ],
              livenessProbe: { httpGet: { path: '/health/live', port: 'https', scheme: 'HTTPS' },
                periodSeconds: 10, timeoutSeconds: 3, failureThreshold: 3 },
              readinessProbe: { httpGet: { path: '/health/ready', port: 'https', scheme: 'HTTPS' },
                periodSeconds: 5, timeoutSeconds: 3, failureThreshold: 3 },
              resources: { requests: { cpu: '100m', memory: '128Mi' },
                limits: { cpu: '500m', memory: '512Mi' } },
              securityContext: { allowPrivilegeEscalation: false,
                capabilities: { drop: ['ALL'] }, readOnlyRootFilesystem: true },
            }],
            volumes: [
              { name: 'tls', secret: { secretName: input.tlsSecretName, optional: false,
                defaultMode: 0o440, items: [
                  { key: 'tls.crt', path: 'tls.crt' }, { key: 'tls.key', path: 'tls.key' },
                ] } },
              { name: 'tmp', emptyDir: { sizeLimit: '64Mi' } },
            ],
          },
        },
      } },
      { apiVersion: 'cilium.io/v2', kind: 'CiliumNetworkPolicy',
        metadata: metadata(`${service}-network`), spec: {
          endpointSelector: { matchLabels: { 'agentweaver.io/workload': service } },
          ingress: [
            { fromEntities: ['host'], toPorts: [tcp('8443')] },
            { fromEndpoints: ['identity-broker', 'orchestrator', 'gateway', 'events-and-sessions',
              'knowledge', 'environment', 'agenthost'].map(endpoint), toPorts: [tcp('8443')] },
          ],
          egress: [
            { toEndpoints: [{ matchLabels: {
              'k8s:io.kubernetes.pod.namespace': 'kube-system', 'k8s:k8s-app': 'kube-dns',
            } }], toPorts: [{ ports: [{ port: '53', protocol: 'UDP' }, { port: '53', protocol: 'TCP' }],
              rules: { dns: dnsNames.map(matchName => ({ matchName })) } }] },
            { toFQDNs: externalHosts.slice(0, 2).map(matchName => ({ matchName })), toPorts: [tcp('443')] },
            { toFQDNs: [{ matchName: postgresHost }], toPorts: [tcp('5432')] },
          ],
        } },
    ],
  };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 3) throw new Error('Usage: node render.mjs <operator-reviewed-input.json>. This only renders source; it never applies it.');
  const input = JSON.parse(readFileSync(process.argv[2], 'utf8'));
  console.log(JSON.stringify(renderProjectsProfile(input), null, 2));
}
