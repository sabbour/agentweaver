const PROVIDER_HOST_REQUEST_OVERLAY_KIND = 'agentweaver.provider-host-request-overlay';
const NAMESPACE = 'agentweaver';
const PORT = 8444;
const CRL_PORT = 8445;
const PORT_NAME = 'host-request';
const CRL_CONFIG_MAP = 'service-backend-crl';
const CRL_MOUNT = '/var/run/service-backend-crl';
const PROJECTS_CONFIG_MAP = 'projects-config-runtime-config';
const ENVIRONMENT_CONFIG_MAP = 'environment-runtime-config';
const PROJECTS_SECRET = 'projects-config-host-request-tls';
const ENVIRONMENT_SECRET = 'environment-host-request-tls';
const PROJECTS_MOUNT = '/var/run/projects-config-host-request-tls';
const ENVIRONMENT_MOUNT = '/var/run/environment-host-request-tls';
const SHA256_HEX = /^[a-f0-9]{64}$/;
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function fail(message) {
  throw new Error(`Kustomize deployment: ${message}`);
}

const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);

function requireFields(value, allowed, required, location) {
  if (!isObject(value)) fail(`${location} must be a JSON object.`);
  const unexpected = Object.keys(value).find(key => !allowed.includes(key));
  if (unexpected) {
    fail(`${location} has the unsupported field "${unexpected}". Do not put private keys or Secret data in the file.`);
  }
  const missing = required.find(key => value[key] === undefined);
  if (missing) fail(`${location} must set "${missing}".`);
}

function requirePin(value, name) {
  if (typeof value !== 'string' || !SHA256_HEX.test(value)) {
    fail(`The provider host-request file ${name} must be a 64-character lowercase SHA-256 hex digest.`);
  }
  return value;
}

/** Reads and validates an off-repo provider host-request TLS fingerprint file. */
export function parseProviderHostRequestOverlay(text) {
  let overlay;
  try {
    overlay = JSON.parse(text);
  } catch {
    fail('The provider host-request file must be valid JSON.');
  }
  requireFields(
    overlay,
    ['kind', 'schemaVersion', 'projectsCertificateSha256', 'environmentCertificateSha256'],
    ['kind', 'schemaVersion', 'projectsCertificateSha256', 'environmentCertificateSha256'],
    'The provider host-request file');
  if (overlay.kind !== PROVIDER_HOST_REQUEST_OVERLAY_KIND || overlay.schemaVersion !== 1) {
    fail(`The provider host-request file must have kind ${PROVIDER_HOST_REQUEST_OVERLAY_KIND} and schemaVersion 1.`);
  }
  const projectsCertificateSha256 = requirePin(overlay.projectsCertificateSha256, 'projectsCertificateSha256');
  const environmentCertificateSha256 = requirePin(overlay.environmentCertificateSha256, 'environmentCertificateSha256');
  if (projectsCertificateSha256 === environmentCertificateSha256) {
    fail('The provider host-request file must supply distinct certificate SHA-256 digests for Projects and Environment.');
  }
  return {
    kind: PROVIDER_HOST_REQUEST_OVERLAY_KIND,
    schemaVersion: 1,
    projectsCertificateSha256,
    environmentCertificateSha256,
  };
}

function findResource(resources, kind, name) {
  const resource = resources.find(item =>
    item.kind === kind && item.metadata?.name === name && item.metadata?.namespace === NAMESPACE);
  if (!resource) fail(`Kustomize must render ${kind}/${name} in namespace ${NAMESPACE}.`);
  return resource;
}

function requireWorkloadPrincipal(configMap, key, label) {
  const value = configMap.data?.[key];
  if (typeof value !== 'string' || !UUID_PATTERN.test(value) || value === '00000000-0000-0000-0000-000000000000') {
    fail(`ConfigMap/${configMap.metadata.name} must define a valid non-empty UUID in ${key} for ${label}.`);
  }
  return value;
}

function addSecretMountAndPort(deployment, containerName, secretName, mountPath) {
  const spec = deployment.spec?.template?.spec;
  if (!spec || !Array.isArray(spec.volumes) || !Array.isArray(spec.containers)) {
    fail(`Deployment/${deployment.metadata.name} is missing pod volumes or containers.`);
  }
  const volumeName = PORT_NAME;
  if (spec.volumes.some(v => v.name === volumeName || v.secret?.secretName === secretName)) {
    fail(`Deployment/${deployment.metadata.name} already defines ${secretName}.`);
  }
  spec.volumes.push({
    name: volumeName,
    secret: {
      secretName,
      optional: false,
      defaultMode: 288,
      items: [
        { key: 'tls.crt', path: 'tls.crt' },
        { key: 'tls.key', path: 'tls.key' },
      ],
    },
  });
  const container = spec.containers.find(c => c.name === containerName);
  if (!container || !Array.isArray(container.ports) || !Array.isArray(container.volumeMounts)) {
    fail(`Deployment/${deployment.metadata.name} is missing container ${containerName}.`);
  }
  if (container.ports.some(p => p.containerPort === PORT || p.name === PORT_NAME)) {
    fail(`Deployment/${deployment.metadata.name} already exposes port ${PORT}.`);
  }
  container.ports.push({
    name: PORT_NAME,
    containerPort: PORT,
    protocol: 'TCP',
  });
  if (container.volumeMounts.some(m => m.name === volumeName || m.mountPath === mountPath)) {
    fail(`Deployment/${deployment.metadata.name} already mounts ${mountPath}.`);
  }
  container.volumeMounts.push({
    name: volumeName,
    mountPath,
    readOnly: true,
  });
}

function addCrlConfigMapMount(deployment, containerName) {
  const spec = deployment.spec?.template?.spec;
  if (!spec || !Array.isArray(spec.volumes) || !Array.isArray(spec.containers)) {
    fail(`Deployment/${deployment.metadata.name} is missing pod volumes or containers.`);
  }
  if (spec.volumes.some(v => v.name === CRL_CONFIG_MAP || v.configMap?.name === CRL_CONFIG_MAP)) {
    fail(`Deployment/${deployment.metadata.name} already defines ${CRL_CONFIG_MAP}.`);
  }
  spec.volumes.push({
    name: CRL_CONFIG_MAP,
    configMap: {
      name: CRL_CONFIG_MAP,
      optional: false,
      items: [{ key: 'ca.crl', path: 'ca.crl' }],
    },
  });
  const container = spec.containers.find(c => c.name === containerName);
  if (!container || !Array.isArray(container.volumeMounts)) {
    fail(`Deployment/${deployment.metadata.name} is missing container ${containerName}.`);
  }
  if (container.volumeMounts.some(m => m.name === CRL_CONFIG_MAP || m.mountPath === CRL_MOUNT)) {
    fail(`Deployment/${deployment.metadata.name} already mounts ${CRL_MOUNT}.`);
  }
  container.volumeMounts.push({
    name: CRL_CONFIG_MAP,
    mountPath: CRL_MOUNT,
    readOnly: true,
  });
}

function addServicePort(service) {
  const ports = service.spec?.ports;
  if (!Array.isArray(ports)) fail(`Service/${service.metadata.name} is missing ports.`);
  if (ports.some(p => p.port === PORT || p.name === PORT_NAME)) {
    fail(`Service/${service.metadata.name} already exposes port ${PORT}.`);
  }
  ports.push({
    name: PORT_NAME,
    port: PORT,
    targetPort: PORT_NAME,
    protocol: 'TCP',
  });
}

function createHostRequestPolicy(name, service, peerService) {
  return {
    apiVersion: 'cilium.io/v2',
    kind: 'CiliumNetworkPolicy',
    metadata: {
      name,
      namespace: NAMESPACE,
      labels: {
        'agentweaver.io/service': service,
      },
    },
    spec: {
      endpointSelector: {
        matchLabels: {
          'agentweaver.io/workload': service,
        },
      },
      ingress: [{
        fromEndpoints: [{
          matchLabels: {
            'k8s:io.kubernetes.pod.namespace': NAMESPACE,
            'agentweaver.io/workload': peerService,
          },
        }],
        toPorts: [{
          ports: [{ port: String(PORT), protocol: 'TCP' }],
        }],
      }],
      egress: [{
        toEndpoints: [{
          matchLabels: {
            'k8s:io.kubernetes.pod.namespace': NAMESPACE,
            'agentweaver.io/workload': peerService,
          },
        }],
        toPorts: [{
          ports: [{ port: String(PORT), protocol: 'TCP' }],
        }],
      }],
    },
  };
}

/** Applies the validated provider host-request channel configuration to rendered Kustomize resources. */
export function applyProviderHostRequestOverlay(resources, overlay) {
  if (!Array.isArray(resources)) fail('Rendered Kustomize resources must be an array.');
  const projectsConfig = findResource(resources, 'ConfigMap', PROJECTS_CONFIG_MAP);
  const environmentConfig = findResource(resources, 'ConfigMap', ENVIRONMENT_CONFIG_MAP);
  const projectsDeployment = findResource(resources, 'Deployment', 'projects-config');
  const environmentDeployment = findResource(resources, 'Deployment', 'environment');
  const projectsService = findResource(resources, 'Service', 'projects-config');
  const environmentService = findResource(resources, 'Service', 'environment');

  const projectsPrincipal = requireWorkloadPrincipal(
    projectsConfig, 'ProjectsConfig__Database__WorkloadIdentity__ClientId', 'Projects');
  const environmentPrincipal = requireWorkloadPrincipal(
    environmentConfig, 'Environment__Database__WorkloadIdentity__ClientId', 'Environment');
  if (projectsPrincipal === environmentPrincipal) {
    fail('Projects and Environment workload identity client IDs must be distinct.');
  }

  const projectsEntries = {
    Kestrel__Endpoints__ProviderHostRequest__Url: `https://+:${PORT}`,
    ProjectsConfig__ProviderHostRequest__Tls__LocalServiceName: 'Projects',
    ProjectsConfig__ProviderHostRequest__Tls__LocalServicePrincipalId: projectsPrincipal,
    ProjectsConfig__ProviderHostRequest__Tls__LocalCertificatePemPath: `${PROJECTS_MOUNT}/tls.crt`,
    ProjectsConfig__ProviderHostRequest__Tls__LocalPrivateKeyPemPath: `${PROJECTS_MOUNT}/tls.key`,
    ProjectsConfig__ProviderHostRequest__Tls__LocalCertificateSha256: overlay.projectsCertificateSha256,
    ProjectsConfig__ProviderHostRequest__Tls__ListenerPort: String(PORT),
    ProjectsConfig__ProviderHostRequest__Tls__PeerServiceName: 'Environment',
    ProjectsConfig__ProviderHostRequest__Tls__PeerServicePrincipalId: environmentPrincipal,
    ProjectsConfig__ProviderHostRequest__Tls__PeerOwnerAddress: `https://environment.${NAMESPACE}.svc.cluster.local:${PORT}/`,
    ProjectsConfig__ProviderHostRequest__Tls__PeerCertificateSha256Pins__0: overlay.environmentCertificateSha256,
    ServiceBackendCrl__LoopbackPort: String(CRL_PORT),
    ServiceBackendCrl__FilePath: `${CRL_MOUNT}/ca.crl`,
  };
  const environmentEntries = {
    Kestrel__Endpoints__ProviderHostRequest__Url: `https://+:${PORT}`,
    Environment__ProviderHostRequest__Tls__LocalServiceName: 'Environment',
    Environment__ProviderHostRequest__Tls__LocalServicePrincipalId: environmentPrincipal,
    Environment__ProviderHostRequest__Tls__LocalCertificatePemPath: `${ENVIRONMENT_MOUNT}/tls.crt`,
    Environment__ProviderHostRequest__Tls__LocalPrivateKeyPemPath: `${ENVIRONMENT_MOUNT}/tls.key`,
    Environment__ProviderHostRequest__Tls__LocalCertificateSha256: overlay.environmentCertificateSha256,
    Environment__ProviderHostRequest__Tls__ListenerPort: String(PORT),
    Environment__ProviderHostRequest__Tls__PeerServiceName: 'Projects',
    Environment__ProviderHostRequest__Tls__PeerServicePrincipalId: projectsPrincipal,
    Environment__ProviderHostRequest__Tls__PeerOwnerAddress: `https://projects-config.${NAMESPACE}.svc.cluster.local:${PORT}/`,
    Environment__ProviderHostRequest__Tls__PeerCertificateSha256Pins__0: overlay.projectsCertificateSha256,
    ServiceBackendCrl__LoopbackPort: String(CRL_PORT),
    ServiceBackendCrl__FilePath: `${CRL_MOUNT}/ca.crl`,
  };

  for (const key of Object.keys(projectsEntries)) {
    if (Object.hasOwn(projectsConfig.data ?? {}, key)) {
      fail(`ConfigMap/${PROJECTS_CONFIG_MAP} already defines ${key}.`);
    }
  }
  for (const key of Object.keys(environmentEntries)) {
    if (Object.hasOwn(environmentConfig.data ?? {}, key)) {
      fail(`ConfigMap/${ENVIRONMENT_CONFIG_MAP} already defines ${key}.`);
    }
  }

  projectsConfig.data = { ...projectsConfig.data, ...projectsEntries };
  environmentConfig.data = { ...environmentConfig.data, ...environmentEntries };

  addSecretMountAndPort(projectsDeployment, 'projects-config', PROJECTS_SECRET, PROJECTS_MOUNT);
  addSecretMountAndPort(environmentDeployment, 'environment', ENVIRONMENT_SECRET, ENVIRONMENT_MOUNT);
  addCrlConfigMapMount(projectsDeployment, 'projects-config');
  addCrlConfigMapMount(environmentDeployment, 'environment');
  addServicePort(projectsService);
  addServicePort(environmentService);

  resources.push(
    createHostRequestPolicy('projects-config-host-request-network', 'projects-config', 'environment'),
    createHostRequestPolicy('environment-host-request-network', 'environment', 'projects-config'));
}

function hasHostRequestKeys(configMap, prefix) {
  const data = configMap?.data;
  if (!data) return false;
  return Object.keys(data).some(key =>
    key === 'Kestrel__Endpoints__ProviderHostRequest__Url' || key.startsWith(prefix));
}

/** Stops before writes if a live enabled provider host-request channel would be removed by redeploy. */
export function requireLiveProviderHostRequestPreserved(
  liveProjectsConfig,
  liveEnvironmentConfig,
  desiredProjectsConfig,
  desiredEnvironmentConfig) {
  const liveProjectsEnabled = hasHostRequestKeys(liveProjectsConfig, 'ProjectsConfig__ProviderHostRequest__');
  const liveEnvironmentEnabled = hasHostRequestKeys(liveEnvironmentConfig, 'Environment__ProviderHostRequest__');
  if (!liveProjectsEnabled && !liveEnvironmentEnabled) return;

  const desiredProjectsEnabled = hasHostRequestKeys(desiredProjectsConfig, 'ProjectsConfig__ProviderHostRequest__') &&
    Object.hasOwn(desiredProjectsConfig?.data ?? {}, 'Kestrel__Endpoints__ProviderHostRequest__Url');
  const desiredEnvironmentEnabled = hasHostRequestKeys(desiredEnvironmentConfig, 'Environment__ProviderHostRequest__') &&
    Object.hasOwn(desiredEnvironmentConfig?.data ?? {}, 'Kestrel__Endpoints__ProviderHostRequest__Url');

  if (!desiredProjectsEnabled || !desiredEnvironmentEnabled) {
    fail(
      'The live cluster has the private provider host-request channel enabled, but this deploy would remove it. ' +
      'Supply --provider-host-request <file.json> with both certificate SHA-256 digests to retain it.');
  }
}

/** Rejects first enablement with a partial manifest that omits Projects, Environment, or their migrations. */
export function requireFirstEnablementImages(
  providerHostRequest,
  liveProjectsConfig,
  liveEnvironmentConfig,
  requestedRepositories) {
  if (!providerHostRequest) return;
  const liveProjectsEnabled = hasHostRequestKeys(liveProjectsConfig, 'ProjectsConfig__ProviderHostRequest__');
  const liveEnvironmentEnabled = hasHostRequestKeys(liveEnvironmentConfig, 'Environment__ProviderHostRequest__');
  if (liveProjectsEnabled && liveEnvironmentEnabled) return;

  const missing = ['agentweaver.projects.config', 'agentweaver.environment']
    .filter(repository => !requestedRepositories.has(repository));
  if (missing.length) {
    fail(
      'Enabling the private provider host-request channel for the first time requires ' +
      `${missing.join(' and ')} in --image-manifest so feature-capable workloads and migrations run.`);
  }
}

export { hasHostRequestKeys, PROVIDER_HOST_REQUEST_OVERLAY_KIND };
