import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const namespace = 'agentweaver-v1-p0';
const service = 'events-and-sessions';
const tenantId = '72f988bf-86f1-41af-91ab-2d7cd011db47';
const clientId = '3ab39eda-7389-4b5e-b5b6-232d7671e012';
const postgresHost = 'aw-v1-p0-pg.postgres.database.azure.com';
const blobHost = 'awv1p0blob.blob.core.windows.net';
const connection = `Host=${postgresHost};Port=5432;Database=agentweaver;Username=aw-v1-p0-id-events-and-sessions;SSL Mode=VerifyFull`;
const fixedConfiguration = {
  ConnectionStrings__EventsAndSessions: connection,
  EventsAndSessions__Database__WorkloadIdentity__TenantId: tenantId,
  EventsAndSessions__Database__WorkloadIdentity__ClientId: clientId,
  EventsAndSessions__Database__WorkloadIdentity__TokenFilePath: '/var/run/secrets/azure/tokens/azure-identity-token',
  EventsAndSessions__Provider__DatabaseName: 'agentweaver',
  EventsAndSessions__Provider__Schema: 'events_sessions',
};
const requiredConfiguration = [
  ...Object.keys(fixedConfiguration),
  'Identity__Issuer',
  'Identity__Audience',
  'EventsAndSessions__Provider__ResourceId',
  'EventsAndSessions__Provider__ResourceGeneration',
  'EventsAndSessions__Knowledge__BaseAddress',
  'ProjectsConfig__BaseAddress',
  'ProjectsConfig__AuthorizationContext__OwnerBaseAddress',
  'ProjectsConfig__AuthorizationContext__Audience',
  'EventsAndSessions__OrchestratorOwner__OwnerBaseAddress',
  'EventsAndSessions__OrchestratorOwner__Audience',
  'EventsAndSessions__SessionMaterial__ContainerUri',
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

function positiveInteger(value, maximum, location) {
  if (!Number.isSafeInteger(value) || value < 1 || value > maximum)
    throw new Error(`${location} must be an explicit positive integer no greater than ${maximum}.`);
}

function httpsRoot(value, location) {
  if (typeof value !== 'string' || !/^https:\/\/[^/]+\/$/.test(value))
    throw new Error(`${location} must be an explicit HTTPS root URI.`);
  let uri;
  try { uri = new URL(value); } catch {
    throw new Error(`${location} must be a valid HTTPS root URI.`);
  }
  if (uri.protocol !== 'https:' || uri.username || uri.password || uri.search || uri.hash ||
      uri.pathname !== '/' || /[\s\\%]/.test(value))
    throw new Error(`${location} must not contain credentials, escapes, query, or fragment.`);
  return uri;
}

function owner(value, name, location) {
  const uri = httpsRoot(value, location);
  if (uri.hostname !== `${name}.${namespace}.svc.cluster.local` ||
      !uri.port || Number(uri.port) < 1024 || Number(uri.port) > 65535)
    throw new Error(`${location} requires the exact private ${name} service and explicit unprivileged HTTPS port.`);
  return { name, hostname: uri.hostname, port: uri.port };
}

export function renderEventsProfile(input) {
  exactFields(input, ['subscriptionId', 'resourceGroup', 'clusterName', 'image',
    'imageUser', 'imageGroup', 'tlsSecretName', 'configuration'], 'Events profile');
  if (input.subscriptionId !== '90ab3701-83f0-4ba1-a90a-f2e68683adab' ||
      input.resourceGroup !== 'aw-v1-p0' || input.clusterName !== 'aw-v1-p0-aks')
    throw new Error('Events profile requires the exact existing approved 1.x target.');
  if (typeof input.image !== 'string' ||
      !/^ghcr\.io\/sabbour\/agentweaver\.eventsandsessions@sha256:[0-9a-f]{64}$/.test(input.image) ||
      input.image.endsWith('0'.repeat(64)))
    throw new Error('Events profile requires a nonzero immutable product Events image digest.');
  positiveInteger(input.imageUser, 2147483647, 'Operator-reviewed Events image user');
  positiveInteger(input.imageGroup, 2147483647, 'Operator-reviewed Events image group');
  if (typeof input.tlsSecretName !== 'string' || input.tlsSecretName.length > 63 ||
      !/^events-and-sessions(?:-[a-z0-9]+)*$/.test(input.tlsSecretName))
    throw new Error('Supply the exact existing Events TLS Secret name, not another service secret.');

  const data = input.configuration;
  exactFields(data, requiredConfiguration, 'Events configuration');
  for (const key of requiredConfiguration) {
    if (typeof data[key] !== 'string' || !data[key].trim() ||
        data[key] !== data[key].trim() || /[\u0000-\u001f\u007f]/.test(data[key]))
      throw new Error(`Events configuration: '${key}' requires an explicit nonempty string.`);
  }
  for (const [key, value] of Object.entries(fixedConfiguration)) {
    if (data[key] !== value) throw new Error(`Events configuration: '${key}' differs from the admitted Events runtime mapping.`);
  }
  if (!/^[A-Za-z0-9._:-]{1,256}$/.test(data.EventsAndSessions__Provider__ResourceId))
    throw new Error('Events Sessions resource ID is invalid.');
  if (!/^[1-9][0-9]*$/.test(data.EventsAndSessions__Provider__ResourceGeneration))
    throw new Error('Events Sessions resource generation must be a positive integer string.');
  positiveInteger(Number(data.EventsAndSessions__Provider__ResourceGeneration),
    Number.MAX_SAFE_INTEGER, 'Events Sessions resource generation');
  const issuer = httpsRoot(data.Identity__Issuer, 'Identity issuer');
  if (!/^agentweaver\.[a-z0-9.-]+\.aksapp\.io$/.test(issuer.hostname) || issuer.port)
    throw new Error('Identity issuer requires the existing managed Broker HTTPS hostname on port 443.');
  const owners = [
    owner(data.EventsAndSessions__Knowledge__BaseAddress, 'knowledge', 'Knowledge owner'),
    owner(data.ProjectsConfig__BaseAddress, 'projects-config', 'Projects owner'),
    owner(data.EventsAndSessions__OrchestratorOwner__OwnerBaseAddress, 'orchestrator', 'Orchestrator owner'),
  ];
  if (data.ProjectsConfig__AuthorizationContext__OwnerBaseAddress !== data.ProjectsConfig__BaseAddress)
    throw new Error('Projects authorization must use the same exact Projects owner.');
  const container = data.EventsAndSessions__SessionMaterial__ContainerUri;
  if (!new RegExp(`^https://${blobHost.replaceAll('.', '\\.')}/[a-z0-9](?:[a-z0-9-]{1,61})[a-z0-9]$`).test(container) ||
      container.includes('--'))
    throw new Error('Session material requires one explicit container in the existing approved Blob account, without SAS or encoded paths.');

  const labels = { 'app.kubernetes.io/name': service, 'agentweaver.io/service': service };
  const metadata = name => ({ name, namespace, labels: { ...labels } });
  const endpoint = name => ({ matchLabels: {
    'k8s:io.kubernetes.pod.namespace': namespace,
    'agentweaver.io/workload': name,
  } });
  const tcp = port => ({ ports: [{ port, protocol: 'TCP' }] });
  const externalHosts = [issuer.hostname, 'login.microsoftonline.com', blobHost, postgresHost];
  const dnsNames = [...externalHosts, ...owners.map(value => value.hostname),
    'awv1p0blob.privatelink.blob.core.windows.net',
    'aw-v1-p0-pg.privatelink.postgres.database.azure.com'];
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
            'azure.workload.identity/use': 'true' } },
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
                { name: 'ASPNETCORE_Kestrel__Certificates__Default__Path', value: '/var/run/events-and-sessions-tls/tls.crt' },
                { name: 'ASPNETCORE_Kestrel__Certificates__Default__KeyPath', value: '/var/run/events-and-sessions-tls/tls.key' },
              ],
              envFrom: [{ configMapRef: { name: `${service}-runtime-config`, optional: false } }],
              volumeMounts: [
                { name: 'tls', mountPath: '/var/run/events-and-sessions-tls', readOnly: true },
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
            { fromEndpoints: ['agenthost', 'orchestrator', 'gateway', 'knowledge', 'projects-config']
              .map(endpoint), toPorts: [tcp('8443')] },
          ],
          egress: [
            { toEndpoints: [{ matchLabels: {
              'k8s:io.kubernetes.pod.namespace': 'kube-system', 'k8s:k8s-app': 'kube-dns',
            } }], toPorts: [{ ports: [{ port: '53', protocol: 'UDP' }, { port: '53', protocol: 'TCP' }],
              rules: { dns: dnsNames.map(matchName => ({ matchName })) } }] },
            ...owners.map(value => ({ toEndpoints: [endpoint(value.name)], toPorts: [tcp(value.port)] })),
            { toFQDNs: externalHosts.slice(0, 3).map(matchName => ({ matchName })), toPorts: [tcp('443')] },
            { toFQDNs: [{ matchName: postgresHost }], toPorts: [tcp('5432')] },
          ],
        } },
    ],
  };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 3) throw new Error('Usage: node render.mjs <operator-reviewed-input.json>. This only renders source; it never applies it.');
  const input = JSON.parse(readFileSync(process.argv[2], 'utf8'));
  console.log(JSON.stringify(renderEventsProfile(input), null, 2));
}
