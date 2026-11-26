import { isIP } from 'node:net';

export const PROVIDER_RATE_CARD_OVERLAY_KIND = 'agentweaver.provider-rate-card-overlay';

const NAMESPACE = 'agentweaver';
const EVENTS_PORT = 8443;
const PROJECTS_CONFIG_MAP = 'projects-config-runtime-config';
const EVENTS_CONFIG_MAP = 'events-and-sessions-runtime-config';
const PROJECTS_SERVICE_ACCOUNT = 'projects-config';
const TOKEN_VOLUME_NAME = 'platform-components-token';
const TOKEN_MOUNT_PATH = '/var/run/projects-config-platform-components';
const TOKEN_FILE = `${TOKEN_MOUNT_PATH}/token`;
const TOKEN_EXPIRATION_SECONDS = 3600;
const EVENTS_BASE_ADDRESS = `https://events-and-sessions.${NAMESPACE}.svc.cluster.local:${EVENTS_PORT}/`;
const AZURE_AD_TOKEN_EXCHANGE_AUDIENCE = 'api://AzureADTokenExchange';
const DNS_NAME = /^(?=.{1,253}$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$/i;

export const PROJECTS_RATE_CARD_KEYS = [
  'PlatformComponents__ServiceToken__TokenFile',
  'PlatformComponents__Owners__eventsAndSessions__BaseAddress',
];

export const EVENTS_RATE_CARD_KEYS = [
  'PlatformComponents__ServiceAuthentication__Authority',
  'PlatformComponents__ServiceAuthentication__Audience',
  'PlatformComponents__ServiceAuthentication__CallerSubject',
];

function fail(message) {
  throw new Error(`Kustomize deployment: ${message}`);
}

const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);

function requireFields(value, allowed, required, location) {
  if (!isObject(value)) fail(`${location} must be a JSON object.`);
  const unexpected = Object.keys(value).find(key => !allowed.includes(key));
  if (unexpected) {
    fail(`${location} has the unsupported field "${unexpected}". Do not put tokens, private keys, or Secret data in the file.`);
  }
  const missing = required.find(key => value[key] === undefined);
  if (missing) fail(`${location} must set "${missing}".`);
}

function requireHttpsUrl(value, name, { requirePort443 = false, requireDnsHost = false } = {}) {
  if (typeof value !== 'string' || !value.trim()) {
    fail(`The provider rate-card file ${name} must be a non-empty HTTPS URL.`);
  }
  if (value.includes('?') || value.includes('#')) {
    fail(`The provider rate-card file ${name} must not contain a query or fragment.`);
  }
  let parsed;
  try {
    parsed = new URL(value);
  } catch {
    fail(`The provider rate-card file ${name} must be a valid HTTPS URL.`);
  }
  if (parsed.protocol !== 'https:' || !parsed.hostname ||
      parsed.username || parsed.password || parsed.search || parsed.hash) {
    fail(`The provider rate-card file ${name} must be an absolute HTTPS URL with a host and no userinfo, query, or fragment.`);
  }
  if (requirePort443 && parsed.port !== '') {
    fail(`The provider rate-card file ${name} must use default HTTPS port 443.`);
  }
  if (requireDnsHost && (isIP(parsed.hostname) !== 0 || !DNS_NAME.test(parsed.hostname))) {
    fail(`The provider rate-card file ${name} must use a DNS host name, not an IP address or single-label host.`);
  }
  return {
    href: parsed.href,
    hostname: parsed.hostname.toLowerCase(),
  };
}

function normalizeAudienceValue(value) {
  if (typeof value !== 'string') return undefined;
  try {
    return new URL(value).href;
  } catch {
    return value.trim();
  }
}

/** Reads and validates an off-repo provider rate-card authentication overlay file. */
export function parseProviderRateCardOverlay(text) {
  let overlay;
  try {
    overlay = JSON.parse(text);
  } catch {
    fail('The provider rate-card file must be valid JSON.');
  }
  requireFields(
    overlay,
    ['kind', 'schemaVersion', 'authority', 'audience', 'jwksUri'],
    ['kind', 'schemaVersion', 'authority', 'audience'],
    'The provider rate-card file',
  );
  if (overlay.kind !== PROVIDER_RATE_CARD_OVERLAY_KIND || overlay.schemaVersion !== 1) {
    fail(`The provider rate-card file must have kind ${PROVIDER_RATE_CARD_OVERLAY_KIND} and schemaVersion 1.`);
  }
  const authority = requireHttpsUrl(overlay.authority, 'authority', { requirePort443: true, requireDnsHost: true });
  const audience = requireHttpsUrl(overlay.audience, 'audience');
  const jwks = overlay.jwksUri === undefined
    ? undefined
    : requireHttpsUrl(overlay.jwksUri, 'jwksUri', { requirePort443: true, requireDnsHost: true });

  return {
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: authority.href,
    authorityHost: authority.hostname,
    audience: audience.href,
    ...(jwks ? { jwksUri: jwks.href, jwksHost: jwks.hostname } : {}),
  };
}

function findResource(resources, kind, name) {
  const resource = resources.find(item =>
    item.kind === kind && item.metadata?.name === name && item.metadata?.namespace === NAMESPACE);
  if (!resource) fail(`Kustomize must render ${kind}/${name} in namespace ${NAMESPACE}.`);
  return resource;
}

function addProjectedTokenMount(deployment, audience) {
  const spec = deployment.spec?.template?.spec;
  if (!spec || !Array.isArray(spec.volumes) || !Array.isArray(spec.containers)) {
    fail(`Deployment/${deployment.metadata.name} is missing pod volumes or containers.`);
  }
  if (spec.serviceAccountName !== PROJECTS_SERVICE_ACCOUNT) {
    fail(`Deployment/${deployment.metadata.name} must use serviceAccountName ${PROJECTS_SERVICE_ACCOUNT}.`);
  }
  if (spec.volumes.some(v => v.name === TOKEN_VOLUME_NAME)) {
    fail(`Deployment/${deployment.metadata.name} already defines volume ${TOKEN_VOLUME_NAME}.`);
  }
  spec.volumes.push({
    name: TOKEN_VOLUME_NAME,
    projected: {
      defaultMode: 288,
      sources: [{
        serviceAccountToken: {
          audience,
          expirationSeconds: TOKEN_EXPIRATION_SECONDS,
          path: 'token',
        },
      }],
    },
  });
  const container = spec.containers.find(c => c.name === 'projects-config');
  if (!container || !Array.isArray(container.volumeMounts)) {
    fail(`Deployment/${deployment.metadata.name} is missing container projects-config.`);
  }
  if (container.volumeMounts.some(m => m.name === TOKEN_VOLUME_NAME || m.mountPath === TOKEN_MOUNT_PATH)) {
    fail(`Deployment/${deployment.metadata.name} already mounts ${TOKEN_MOUNT_PATH}.`);
  }
  container.volumeMounts.push({
    name: TOKEN_VOLUME_NAME,
    mountPath: TOKEN_MOUNT_PATH,
    readOnly: true,
  });
}

function createProjectsToEventsPolicy() {
  return {
    apiVersion: 'cilium.io/v2',
    kind: 'CiliumNetworkPolicy',
    metadata: {
      name: 'projects-config-events-network',
      namespace: NAMESPACE,
      labels: {
        'agentweaver.io/service': 'projects-config',
      },
    },
    spec: {
      endpointSelector: {
        matchLabels: {
          'agentweaver.io/workload': 'projects-config',
        },
      },
      egress: [
        {
          toEndpoints: [{
            matchLabels: {
              'k8s:io.kubernetes.pod.namespace': 'kube-system',
              'k8s:k8s-app': 'kube-dns',
            },
          }],
          toPorts: [{
            ports: [
              { port: '53', protocol: 'UDP' },
              { port: '53', protocol: 'TCP' },
            ],
            rules: {
              dns: [{
                matchName: `events-and-sessions.${NAMESPACE}.svc.cluster.local`,
              }],
            },
          }],
        },
        {
          toEndpoints: [{
            matchLabels: {
              'k8s:io.kubernetes.pod.namespace': NAMESPACE,
              'agentweaver.io/workload': 'events-and-sessions',
            },
          }],
          toPorts: [{
            ports: [{ port: String(EVENTS_PORT), protocol: 'TCP' }],
          }],
        },
      ],
    },
  };
}

function createEventsProjectsAndOidcPolicy(oidcHosts) {
  const dnsNames = oidcHosts.map(matchName => ({ matchName }));
  return {
    apiVersion: 'cilium.io/v2',
    kind: 'CiliumNetworkPolicy',
    metadata: {
      name: 'events-and-sessions-projects-network',
      namespace: NAMESPACE,
      labels: {
        'agentweaver.io/service': 'events-and-sessions',
      },
    },
    spec: {
      endpointSelector: {
        matchLabels: {
          'agentweaver.io/workload': 'events-and-sessions',
        },
      },
      ingress: [{
        fromEndpoints: [{
          matchLabels: {
            'k8s:io.kubernetes.pod.namespace': NAMESPACE,
            'agentweaver.io/workload': 'projects-config',
          },
        }],
        toPorts: [{
          ports: [{ port: String(EVENTS_PORT), protocol: 'TCP' }],
        }],
      }],
      egress: [
        {
          toEndpoints: [{
            matchLabels: {
              'k8s:io.kubernetes.pod.namespace': 'kube-system',
              'k8s:k8s-app': 'kube-dns',
            },
          }],
          toPorts: [{
            ports: [
              { port: '53', protocol: 'UDP' },
              { port: '53', protocol: 'TCP' },
            ],
            rules: {
              dns: dnsNames,
            },
          }],
        },
        {
          toFQDNs: dnsNames.map(entry => ({ ...entry })),
          toPorts: [{
            ports: [{ port: '443', protocol: 'TCP' }],
          }],
        },
      ],
    },
  };
}

/** Applies the validated Projects-to-Events rate-card auth and network wiring to rendered Kustomize resources. */
export function applyProviderRateCardOverlay(resources, overlay) {
  if (!Array.isArray(resources)) fail('Rendered Kustomize resources must be an array.');
  const projectsConfig = findResource(resources, 'ConfigMap', PROJECTS_CONFIG_MAP);
  const eventsConfig = findResource(resources, 'ConfigMap', EVENTS_CONFIG_MAP);
  const projectsDeployment = findResource(resources, 'Deployment', 'projects-config');
  findResource(resources, 'Deployment', 'events-and-sessions');
  const eventsService = findResource(resources, 'Service', 'events-and-sessions');
  findResource(resources, 'ServiceAccount', PROJECTS_SERVICE_ACCOUNT);

  if (!eventsService.spec?.ports?.some(p => p.port === EVENTS_PORT && (p.protocol ?? 'TCP') === 'TCP')) {
    fail(`Service/events-and-sessions must expose TCP port ${EVENTS_PORT}.`);
  }

  const collidingAudiences = [
    ['ProjectsConfig__Authentication__Audience', projectsConfig.data?.ProjectsConfig__Authentication__Audience],
    ['ProjectsConfig__Authentication__BrokerRunBindingAudience', projectsConfig.data?.ProjectsConfig__Authentication__BrokerRunBindingAudience],
    ['Identity__Audience', eventsConfig.data?.Identity__Audience],
    ['Identity__SecretRedemption__Audience', eventsConfig.data?.Identity__SecretRedemption__Audience],
    ['AzureADTokenExchange', AZURE_AD_TOKEN_EXCHANGE_AUDIENCE],
  ];
  for (const [label, existingAudience] of collidingAudiences) {
    if (existingAudience !== undefined &&
        normalizeAudienceValue(existingAudience) === overlay.audience) {
      fail(`The provider rate-card audience must differ from ${label}.`);
    }
  }

  const projectsEntries = {
    PlatformComponents__ServiceToken__TokenFile: TOKEN_FILE,
    PlatformComponents__Owners__eventsAndSessions__BaseAddress: EVENTS_BASE_ADDRESS,
  };
  const eventsEntries = {
    PlatformComponents__ServiceAuthentication__Authority: overlay.authority,
    PlatformComponents__ServiceAuthentication__Audience: overlay.audience,
    PlatformComponents__ServiceAuthentication__CallerSubject: `system:serviceaccount:${NAMESPACE}:${PROJECTS_SERVICE_ACCOUNT}`,
  };

  for (const key of Object.keys(projectsEntries)) {
    if (Object.hasOwn(projectsConfig.data ?? {}, key)) {
      fail(`ConfigMap/${PROJECTS_CONFIG_MAP} already defines ${key}.`);
    }
  }
  for (const key of Object.keys(eventsEntries)) {
    if (Object.hasOwn(eventsConfig.data ?? {}, key)) {
      fail(`ConfigMap/${EVENTS_CONFIG_MAP} already defines ${key}.`);
    }
  }

  projectsConfig.data = { ...projectsConfig.data, ...projectsEntries };
  eventsConfig.data = { ...eventsConfig.data, ...eventsEntries };

  addProjectedTokenMount(projectsDeployment, overlay.audience);

  const oidcHosts = [...new Set([
    overlay.authorityHost,
    ...(overlay.jwksHost ? [overlay.jwksHost] : []),
  ])].sort();

  resources.push(
    createProjectsToEventsPolicy(),
    createEventsProjectsAndOidcPolicy(oidcHosts),
  );
}

export function hasRateCardKeys(configMap, keys) {
  const data = configMap?.data;
  if (!data) return false;
  return keys.some(key => Object.hasOwn(data, key));
}

function hasAllRateCardKeys(configMap, keys) {
  const data = configMap?.data;
  if (!data) return false;
  return keys.every(key => typeof data[key] === 'string' && data[key].trim().length > 0);
}

/** Stops before writes if live rate-card settings (complete or partial) would be removed by redeploy. */
export function requireLiveProviderRateCardPreserved(
  liveProjectsConfig,
  liveEventsConfig,
  desiredProjectsConfig,
  desiredEventsConfig,
) {
  const liveHasAny =
    hasRateCardKeys(liveProjectsConfig, PROJECTS_RATE_CARD_KEYS) ||
    hasRateCardKeys(liveEventsConfig, EVENTS_RATE_CARD_KEYS);
  if (!liveHasAny) return;

  const desiredComplete =
    hasAllRateCardKeys(desiredProjectsConfig, PROJECTS_RATE_CARD_KEYS) &&
    hasAllRateCardKeys(desiredEventsConfig, EVENTS_RATE_CARD_KEYS);
  if (!desiredComplete) {
    fail(
      'The live cluster has private Projects-to-Events rate-card wiring enabled, but this deploy would remove it. ' +
      'Supply --provider-rate-card <file.json> to retain it.',
    );
  }
}

/** Rejects first enablement with a partial manifest that omits Projects or Events. */
export function requireFirstEnablementRateCardImages(
  providerRateCard,
  liveProjectsConfig,
  liveEventsConfig,
  requestedRepositories,
) {
  if (!providerRateCard) return;
  const liveComplete =
    hasAllRateCardKeys(liveProjectsConfig, PROJECTS_RATE_CARD_KEYS) &&
    hasAllRateCardKeys(liveEventsConfig, EVENTS_RATE_CARD_KEYS);
  if (liveComplete) return;

  const missing = ['agentweaver.projects.config', 'agentweaver.eventsandsessions']
    .filter(repository => !requestedRepositories.has(repository));
  if (missing.length) {
    fail(
      'Enabling the private Projects-to-Events rate-card channel for the first time requires ' +
      `${missing.join(' and ')} in --image-manifest so feature-capable workloads run.`,
    );
  }
}

/** Verifies that the overlay authority and optional jwksUri match the target cluster OIDC discovery document. */
export function verifyClusterOidcIssuer(exec, context, root, overlay) {
  const result = exec('kubectl', [
    'get', '--raw', '/.well-known/openid-configuration', '--context', context,
  ], { check: false, cwd: root });
  if (result.status !== 0) {
    const diagnostic = (result.stderr || result.stdout || `exit ${result.status}`).trim();
    fail(`Could not read the cluster OIDC discovery document (/.well-known/openid-configuration): ${diagnostic}`);
  }
  let discovery;
  try {
    discovery = JSON.parse(result.stdout);
  } catch {
    fail('Cluster OIDC discovery document (/.well-known/openid-configuration) returned invalid JSON.');
  }
  let clusterIssuer;
  try {
    clusterIssuer = requireHttpsUrl(discovery?.issuer, 'cluster discovery issuer', {
      requirePort443: true,
      requireDnsHost: true,
    });
  } catch {
    fail('Cluster OIDC discovery document does not contain a valid HTTPS issuer.');
  }
  if (clusterIssuer.href !== overlay.authority) {
    fail(
      `The provider rate-card authority (${overlay.authority}) does not match the selected cluster OIDC issuer (${clusterIssuer.href}).`,
    );
  }
  if (discovery?.jwks_uri !== undefined) {
    let clusterJwks;
    try {
      clusterJwks = requireHttpsUrl(discovery.jwks_uri, 'cluster discovery jwks_uri', {
        requirePort443: true,
        requireDnsHost: true,
      });
    } catch {
      fail('Cluster OIDC discovery document does not contain a valid HTTPS jwks_uri.');
    }
    if (overlay.jwksUri && overlay.jwksUri !== clusterJwks.href) {
      fail(
        `The provider rate-card jwksUri (${overlay.jwksUri}) does not match cluster discovery jwks_uri (${clusterJwks.href}).`,
      );
    }
    if (clusterJwks.hostname !== overlay.authorityHost && !overlay.jwksUri) {
      fail(
        `Cluster OIDC jwks_uri uses host ${clusterJwks.hostname}; set jwksUri in the provider rate-card file to allow egress to it.`,
      );
    }
  } else if (overlay.jwksUri) {
    fail('Cluster OIDC discovery document has no jwks_uri, so jwksUri must not be set.');
  }
}
