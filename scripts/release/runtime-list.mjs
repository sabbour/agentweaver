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

const TARGET_NAMESPACE = 'agentweaver-v1-p0';
const TLS_MODE = 0o440;
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

function createWorkload(name, image, namespace, providerCatalog, agentHost) {
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
  }

  if (name === 'projects-config') {
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
  if (name === 'projects-config') {
    podMetadata.annotations = { 'agentweaver.io/provider-catalog-source': providerCatalog.sourceCommit };
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

export function createRuntimeList(images, namespace, providerCatalog) {
  if (namespace !== TARGET_NAMESPACE) {
    fail(`namespace must be the exact approved v1 P0 namespace (${TARGET_NAMESPACE})`);
  }
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
    createWorkload(name, requiredImage(images, name), namespace, providerCatalog, agentHost));
  return { apiVersion: 'v1', kind: 'List', items };
}
