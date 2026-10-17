/**
 * Builds the Kubernetes runtime resource list for the selected release images and namespace.
 * It validates image and broker run-binding contracts before returning resource documents.
 */
import { createHash } from 'node:crypto';

const SERVICE_NAMES = Object.freeze([
  'identity-broker',
  'events-and-sessions',
  'gateway',
  'mcp',
  'web',
  'orchestrator',
  'environment',
  'knowledge',
  'projects-config',
]);

/** Keeps each catalog revision equal to the consumer input used by the bootstrap List. */
export const PROVIDER_OPTIONS_REVISION_RUNTIME_INPUTS = Object.freeze([
  Object.freeze({
    workload: 'environment',
    name: 'Environment__Sandbox__AgentSandbox__OptionsRevision',
    value: 'p1-v1.0.0',
  }),
  Object.freeze({
    workload: 'environment',
    name: 'Environment__Storage__AzureFiles__OptionsRevision',
    value: 'p1-v1.0.0',
  }),
  Object.freeze({
    workload: 'environment',
    name: 'Environment__NetworkPolicy__Cilium__OptionsRevision',
    value: 'p1-v1.0.0',
  }),
  Object.freeze({
    workload: 'orchestrator',
    name: 'AgentGovernance__Policy__OptionsRevision',
    value: 'p1-v1.0.0',
  }),
  Object.freeze({
    workload: 'events-and-sessions',
    name: 'EventsAndSessions__Cost__Copilot__OptionsRevision',
    value: 'p1-v1.0.0',
  }),
]);

/** Pins the Agentweaver Cilium adapter version shipped in Environment 1.0.0. */
export const PROVIDER_ADAPTER_VERSION_RUNTIME_INPUTS = Object.freeze([
  Object.freeze({
    workload: 'environment',
    name: 'Environment__NetworkPolicy__Cilium__AdapterVersion',
    value: '1.0.0',
  }),
]);

const TARGET_NAMESPACE = 'agentweaver-v1-p0';
const TLS_MODE = 0o440;
const BACKEND_TLS_CA_CONFIGMAP = 'service-backend-ca';
const ENVIRONMENT_CILIUM_CONFIGMAP = 'environment-cilium-config';
const ENVIRONMENT_CILIUM_CONFIG_KEY = 'appsettings.Production.json';
const ENVIRONMENT_CILIUM_CONFIG_PATH = '/app/appsettings.Production.json';
/** Names and mount paths shared by broker run-binding runtime resources. */
export const BROKER_RUN_BINDING_EGRESS_POLICY_NAME = 'identity-broker-projects-egress';
export const BROKER_RUN_BINDING_INGRESS_POLICY_NAME = 'projects-config-broker-ingress';
export const BROKER_RUN_BINDING_POLICY_LABEL = 'agentweaver.io/run-binding';
export const BROKER_RUN_BINDING_ANNOTATION = 'agentweaver.io/broker-run-binding-sha256';
export const BROKER_RUN_BINDING_CA_BUNDLE_PATH = '/var/run/service-backend-ca/ca.crt';
export const PROJECTS_CONFIG_TLS_CERTIFICATE_PATH = '/var/run/projects-config-tls/tls.crt';
export const PROJECTS_CONFIG_TLS_KEY_PATH = '/var/run/projects-config-tls/tls.key';
const BROKER_RUN_BINDING_FIELDS = Object.freeze([
  'projectsOwnerAddress',
  'projectsAudience',
  'projectsServerCertificateSha256',
  'projectsTrustBundleSha256',
]);
const WORKLOAD_IDENTITY_SERVICES = new Set([
  'identity-broker', 'events-and-sessions', 'environment', 'knowledge', 'orchestrator', 'projects-config',
]);

function fail(message) {
  throw new Error(`runtime manifest: ${message}`);
}

function requiredImage(images, name) {
  const image = images.find((entry) => entry.name === name);
  if (!image || typeof image.image !== 'string') {
    fail(`the validated publication receipt has no image for ${name}`);
  }
  return image;
}

/** Validates the complete broker-to-projects run-binding descriptor and returns normalized fields. */
export function validateBrokerRunBinding(binding) {
  if (!binding || typeof binding !== 'object' || Array.isArray(binding)) {
    fail('an explicit Broker run-binding descriptor must be an object');
  }
  const keys = Reflect.ownKeys(binding);
  if (keys.length !== BROKER_RUN_BINDING_FIELDS.length ||
      keys.some((key) => typeof key !== 'string' || !BROKER_RUN_BINDING_FIELDS.includes(key))) {
    fail(`Broker run-binding descriptor must contain exactly: ${BROKER_RUN_BINDING_FIELDS.join(', ')}`);
  }
  const descriptor = Object.fromEntries(BROKER_RUN_BINDING_FIELDS.map((key) => {
    const value = binding[key];
    if (typeof value !== 'string' || value.length === 0 || value.trim() !== value || value.length > 2048) {
      fail(`Broker run-binding ${key} must be an exact nonempty string`);
    }
    return [key, value];
  }));
  for (const key of ['projectsServerCertificateSha256', 'projectsTrustBundleSha256']) {
    if (!/^[a-f0-9]{64}$/.test(descriptor[key])) {
      fail(`Broker run-binding ${key} must be a lowercase SHA-256 hex digest`);
    }
  }
  let audience;
  try {
    audience = new URL(descriptor.projectsAudience);
  } catch {
    fail('Broker run-binding projectsAudience must be an absolute HTTPS URI without user info, query, or fragment');
  }
  const audienceAuthority = descriptor.projectsAudience.slice('https://'.length).split(/[/?#]/, 1)[0];
  if (audience.protocol !== 'https:' || audience.username || audience.password ||
      audienceAuthority.includes('@') || descriptor.projectsAudience.includes('?') ||
      descriptor.projectsAudience.includes('#')) {
    fail('Broker run-binding projectsAudience must be an absolute HTTPS URI without user info, query, or fragment');
  }
  let ownerAddress;
  try {
    ownerAddress = new URL(descriptor.projectsOwnerAddress);
  } catch {
    fail('Broker run-binding projectsOwnerAddress must be the exact Projects Config HTTPS service address');
  }
  if (ownerAddress.protocol !== 'https:' ||
      ownerAddress.hostname !== `projects-config.${TARGET_NAMESPACE}.svc.cluster.local` ||
      ownerAddress.port !== '8443' || ownerAddress.username || ownerAddress.password ||
      ownerAddress.pathname !== '/' || ownerAddress.search || ownerAddress.hash) {
    fail('Broker run-binding projectsOwnerAddress must target the exact Projects Config HTTPS service on port 8443');
  }
  return descriptor;
}

/** Returns a stable SHA-256 fingerprint for the normalized broker run-binding descriptor. */
export function brokerRunBindingFingerprint(binding) {
  const descriptor = validateBrokerRunBinding(binding);
  return createHash('sha256').update(JSON.stringify(descriptor)).digest('hex');
}

function runBindingPolicyMetadata(name, service, namespace) {
  return {
    name,
    namespace,
    labels: {
      'agentweaver.io/service': service,
      [BROKER_RUN_BINDING_POLICY_LABEL]: 'projects',
    },
  };
}

/** Creates the ingress and egress policy documents restricted to the run-binding contract. */
export function createBrokerRunBindingPolicies(namespace) {
  return [{
    apiVersion: 'cilium.io/v2',
    kind: 'CiliumNetworkPolicy',
    metadata: runBindingPolicyMetadata(BROKER_RUN_BINDING_EGRESS_POLICY_NAME, 'identity-broker', namespace),
    spec: {
      endpointSelector: {
        matchLabels: { 'agentweaver.io/workload': 'identity-broker' },
      },
      egress: [{
        toEndpoints: [{
          matchLabels: {
            'k8s:io.kubernetes.pod.namespace': namespace,
            'agentweaver.io/workload': 'projects-config',
          },
        }],
        toPorts: [{ ports: [{ port: '8443', protocol: 'TCP' }] }],
      }],
    },
  }, {
    apiVersion: 'cilium.io/v2',
    kind: 'CiliumNetworkPolicy',
    metadata: runBindingPolicyMetadata(BROKER_RUN_BINDING_INGRESS_POLICY_NAME, 'projects-config', namespace),
    spec: {
      endpointSelector: {
        matchLabels: { 'agentweaver.io/workload': 'projects-config' },
      },
      ingress: [{
        fromEndpoints: [{
          matchLabels: {
            'k8s:io.kubernetes.pod.namespace': namespace,
            'agentweaver.io/workload': 'identity-broker',
          },
        }],
        toPorts: [{ ports: [{ port: '8443', protocol: 'TCP' }] }],
      }],
    },
  }];
}

function createWorkload(name, image, namespace, providerCatalog, agentHost, brokerRunBinding, bindingFingerprint) {
  const web = name === 'web';
  const identity = name === 'identity-broker';
  const portName = web ? 'http' : 'https';
  const port = web ? 8080 : 8443;
  const scheme = web ? 'http' : 'https';
  const runtimeUid = identity ? 10001 : 1654;
  const usesWorkloadIdentity = WORKLOAD_IDENTITY_SERVICES.has(name);
  const usesIdentityBroker = ['gateway', 'orchestrator'].includes(name);
  const labels = {
    'app.kubernetes.io/name': name,
    'agentweaver.io/service': name,
  };
  const podLabels = {
    ...labels,
    'agentweaver.io/workload': name,
    ...(usesIdentityBroker ? { 'agentweaver.io/identity-client': 'true' } : {}),
    ...(usesWorkloadIdentity ? { 'azure.workload.identity/use': 'true' } : {}),
  };
  const tlsDirectory = `/var/run/${name}-tls`;
  const volumes = [{ name: 'tmp', emptyDir: { sizeLimit: '64Mi' } }];
  const volumeMounts = [{ name: 'tmp', mountPath: '/tmp' }];
  const env = [{ name: 'ASPNETCORE_URLS', value: `${scheme}://+:${port}` }];
  env.push(...PROVIDER_ADAPTER_VERSION_RUNTIME_INPUTS
    .filter(({ workload }) => workload === name)
    .map(({ name: envName, value }) => ({ name: envName, value })));
  env.push(...PROVIDER_OPTIONS_REVISION_RUNTIME_INPUTS
    .filter(({ workload }) => workload === name)
    .map(({ name: envName, value }) => ({ name: envName, value })));
  const envFrom = [{
    configMapRef: { name: `${name}-runtime-config`, optional: false },
  }];

  if (!web) {
    env.push(
      { name: 'ASPNETCORE_Kestrel__Certificates__Default__Path', value: `${tlsDirectory}/tls.crt` },
      { name: 'ASPNETCORE_Kestrel__Certificates__Default__KeyPath', value: `${tlsDirectory}/tls.key` },
    );
    volumes.push({
      name: 'tls',
      secret: {
        secretName: `${name}-tls`,
        optional: false,
        defaultMode: TLS_MODE,
        items: [{ key: 'tls.crt', path: 'tls.crt' }, { key: 'tls.key', path: 'tls.key' }],
      },
    });
    volumeMounts.push({ name: 'tls', mountPath: tlsDirectory, readOnly: true });
  }

  if (name === 'mcp') {
    volumes.push({
      name: 'backend-ca',
      configMap: {
        name: BACKEND_TLS_CA_CONFIGMAP,
        optional: false,
        items: [{ key: 'ca.crt', path: 'ca.crt' }],
      },
    });
    volumeMounts.push({ name: 'backend-ca', mountPath: '/var/run/service-backend-ca', readOnly: true });
  }

  if (identity) {
    env.push(
      { name: 'IdentityBroker__Signing__PfxPath', value: '/var/run/identity-broker-signing/signing.pfx' },
      {
        name: 'IdentityBroker__Signing__PfxPassword',
        valueFrom: { secretKeyRef: { name: 'identity-broker-signing', key: 'password', optional: false } },
      },
    );
    envFrom.push({
      secretRef: { name: 'identity-broker-client-secrets', optional: true },
    });
    volumes.push(
      {
        name: 'signing-material',
        secret: {
          secretName: 'identity-broker-signing',
          optional: false,
          defaultMode: TLS_MODE,
          items: [{ key: 'signing.pfx', path: 'signing.pfx' }],
        },
      },
      { name: 'key-ring', persistentVolumeClaim: { claimName: 'identity-broker-key-ring' } },
    );
    volumeMounts.push(
      { name: 'signing-material', mountPath: '/var/run/identity-broker-signing', readOnly: true },
      { name: 'key-ring', mountPath: '/var/lib/identity-broker/key-ring' },
    );
    if (brokerRunBinding) {
      volumes.push({
        name: 'projects-ca',
        configMap: {
          name: BACKEND_TLS_CA_CONFIGMAP,
          optional: false,
          defaultMode: TLS_MODE,
          items: [{ key: 'ca.crt', path: 'ca.crt' }],
        },
      });
      volumeMounts.push({
        name: 'projects-ca',
        mountPath: '/var/run/service-backend-ca',
        readOnly: true,
      });
    }
  }

  if (name === 'environment') {
    // Cilium endpoint-selector keys contain '/' and ':', which Kubernetes env vars cannot represent.
    volumes.push({
      name: 'cilium-options',
      configMap: {
        name: ENVIRONMENT_CILIUM_CONFIGMAP,
        optional: false,
        items: [{ key: ENVIRONMENT_CILIUM_CONFIG_KEY, path: ENVIRONMENT_CILIUM_CONFIG_KEY }],
      },
    });
    volumeMounts.push({
      name: 'cilium-options',
      mountPath: ENVIRONMENT_CILIUM_CONFIG_PATH,
      subPath: ENVIRONMENT_CILIUM_CONFIG_KEY,
      readOnly: true,
    });
  }

  if (['orchestrator', 'projects-config'].includes(name)) {
    // Both services resolve and validate the same provider choices and source revision.
    envFrom.push({
      prefix: 'ProjectsConfig__ProviderCatalog__',
      configMapRef: { name: providerCatalog.configMapName, optional: false },
    });
  }

  if (name === 'environment') {
    env.push(
      { name: 'Environment__Sandbox__AgentSandbox__ContainerImage', value: agentHost.sandboxImage },
      { name: 'Environment__Sandbox__AgentSandbox__ContainerImagePlatform', value: 'linux/amd64' },
      {
        name: 'Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes',
        value: String(agentHost.compressedPullBytes),
      },
    );
  }

  const deploymentMetadata = {
    name,
    namespace,
    labels: { ...labels },
  };
  const podMetadata = { labels: podLabels };
  if (['orchestrator', 'projects-config'].includes(name)) {
    podMetadata.annotations = { 'agentweaver.io/provider-catalog-source': providerCatalog.sourceCommit };
  }
  if (brokerRunBinding && ['identity-broker', 'projects-config'].includes(name)) {
    podMetadata.annotations = {
      ...podMetadata.annotations,
      [BROKER_RUN_BINDING_ANNOTATION]: bindingFingerprint,
    };
  }

  const deployment = {
    apiVersion: 'apps/v1',
    kind: 'Deployment',
    metadata: deploymentMetadata,
    spec: {
      replicas: 1,
      strategy: { type: 'Recreate' },
      selector: { matchLabels: { 'app.kubernetes.io/name': name } },
      template: {
        metadata: podMetadata,
        spec: {
          serviceAccountName: name,
          automountServiceAccountToken: true,
          securityContext: {
            runAsNonRoot: true,
            runAsUser: runtimeUid,
            runAsGroup: runtimeUid,
            fsGroup: runtimeUid,
            fsGroupChangePolicy: 'OnRootMismatch',
            seccompProfile: { type: 'RuntimeDefault' },
          },
          terminationGracePeriodSeconds: 30,
          volumes,
          containers: [{
            name,
            image: image.image,
            imagePullPolicy: 'IfNotPresent',
            ports: [{ name: portName, containerPort: port, protocol: 'TCP' }],
            env,
            envFrom,
            volumeMounts,
            livenessProbe: {
              httpGet: { path: '/health/live', port: portName, scheme: web ? 'HTTP' : 'HTTPS' },
              periodSeconds: 10,
              timeoutSeconds: 3,
              failureThreshold: 3,
            },
            readinessProbe: {
              httpGet: { path: '/health/ready', port: portName, scheme: web ? 'HTTP' : 'HTTPS' },
              periodSeconds: 5,
              timeoutSeconds: 3,
              failureThreshold: 3,
            },
            resources: {
              requests: { cpu: '100m', memory: '128Mi' },
              limits: { cpu: '500m', memory: '512Mi' },
            },
            securityContext: {
              runAsNonRoot: true,
              allowPrivilegeEscalation: false,
              capabilities: { drop: ['ALL'] },
              readOnlyRootFilesystem: true,
            },
          }],
        },
      },
    },
  };

  const service = {
    apiVersion: 'v1',
    kind: 'Service',
    metadata: { ...deploymentMetadata, labels: { ...labels } },
    spec: {
      type: 'ClusterIP',
      selector: { 'app.kubernetes.io/name': name },
      ports: [{
        name: portName,
        port: name === 'identity-broker' ? 443 : port,
        targetPort: portName,
        protocol: 'TCP',
      }],
    },
  };

  return [deployment, service];
}

/**
 * Generates Kubernetes runtime resources for the validated image set and provider catalog.
 * Invalid namespace, image digests, or binding configuration are rejected before a resource list is returned.
 */
export function createRuntimeList(images, namespace, providerCatalog, brokerRunBinding) {
  if (namespace !== TARGET_NAMESPACE) {
    fail(`namespace must be the exact approved v1 P0 namespace (${TARGET_NAMESPACE})`);
  }
  const binding = brokerRunBinding === undefined
    ? undefined
    : validateBrokerRunBinding(brokerRunBinding);
  const bindingFingerprint = binding ? brokerRunBindingFingerprint(binding) : undefined;
  if (typeof providerCatalog?.configMapName !== 'string' ||
      providerCatalog.configMapName.length > 253 ||
      !/^[a-z0-9](?:[-a-z0-9]*[a-z0-9])?(?:\.[a-z0-9](?:[-a-z0-9]*[a-z0-9])?)*$/.test(providerCatalog.configMapName) ||
      providerCatalog.configMapName === 'projects-config-runtime-config') {
    fail('an explicit existing Projects provider catalog ConfigMap name is required');
  }
  if (!/^[a-f0-9]{40}$/.test(providerCatalog.sourceCommit ?? '') ||
      /^0+$/.test(providerCatalog.sourceCommit)) {
    fail('an explicit nonzero Projects provider catalog source commit is required');
  }
  const agentHost = requiredImage(images, 'agenthost');
  if (typeof agentHost.sandboxImage !== 'string' ||
      !Number.isSafeInteger(agentHost.compressedPullBytes) || agentHost.compressedPullBytes < 1) {
    fail('the validated publication receipt has no measured AgentHost image and pull size');
  }

  const items = SERVICE_NAMES.flatMap((name) =>
    createWorkload(name, requiredImage(images, name), namespace, providerCatalog, agentHost,
      binding, bindingFingerprint));
  if (binding) items.push(...createBrokerRunBindingPolicies(namespace));
  return { apiVersion: 'v1', kind: 'List', items };
}
