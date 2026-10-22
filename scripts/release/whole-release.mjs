/**
 * Shared whole-release planning and verification for the P0 installation.
 * Component rollouts may succeed while the installation is still partial; only this
 * flow can write a deployed whole-release receipt.
 */
import { mkdirSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { AgentweaverClient } from '../api-harness/lib/client.mjs';
import { createBrokerAuthProvider } from '../harness-shared/broker-auth.mjs';
import {
  authorizeP1InBrowser, getValidatedP1BrokerTokenActorId, P1_RUN_SCOPES,
  registeredPortalRedirectUri, withFreshP1BrowserContext,
} from '../harness-shared/p1-signin.mjs';
import { validateNetworkTarget } from '../harness-shared/target-guard.mjs';
import {
  canonicalJson, deploymentInventory, imageDigest, planDeploymentInventory,
  verifyDeploymentPreservation, verifyDeploymentReady,
} from './image-rollout-common.mjs';
import { selectPublishedComponentImage } from './publication-image.mjs';
import { componentImageRepository } from './pack.mjs';
import { verifyServiceRegistryImage } from './registry.mjs';
import { readImageSchemaRange, STATE_NAME } from '../azure/lib/db-maintenance.mjs';

export const WHOLE_RELEASE_RECEIPT_DIRECTORY =
  path.join(homedir(), '.agentweaver', 'whole-release');
export const WHOLE_RELEASE_LAST_RECEIPT =
  path.join(WHOLE_RELEASE_RECEIPT_DIRECTORY, 'last.json');
export const WHOLE_RELEASE_SERVICES = Object.freeze([
  { componentId: 'Agentweaver.Web', deployment: 'web', container: 'web' },
  { componentId: 'Agentweaver.Identity.Broker', deployment: 'identity-broker', container: 'identity-broker' },
  { componentId: 'Agentweaver.EventsAndSessions', deployment: 'events-and-sessions', container: 'events-and-sessions' },
  { componentId: 'Agentweaver.Gateway', deployment: 'gateway', container: 'gateway' },
  { componentId: 'Agentweaver.Mcp', deployment: 'mcp', container: 'mcp' },
  { componentId: 'Agentweaver.Orchestrator', deployment: 'orchestrator', container: 'orchestrator' },
  { componentId: 'Agentweaver.Environment', deployment: 'environment', container: 'environment' },
  { componentId: 'Agentweaver.Knowledge', deployment: 'knowledge', container: 'knowledge' },
  { componentId: 'Agentweaver.Projects.Config', deployment: 'projects-config', container: 'projects-config' },
]);

const SHA = /^[a-f0-9]{40}$/;
const IMAGE_DIGEST = /^sha256:[a-f0-9]{64}$/;
const SERVICE_NAMES = new Set(WHOLE_RELEASE_SERVICES.map(service => service.deployment));

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

export function wholeReleaseFail(message) {
  throw new Error(`Whole-release rollout: ${message}`);
}

function parseJson(text, name) {
  try {
    return JSON.parse(text);
  } catch (error) {
    wholeReleaseFail(`${name} is not valid JSON: ${error.message}`);
  }
}

function requireAncestor(exec, root, sourceSha, target, description) {
  // Git ancestry prevents a valid-looking receipt from selecting an image outside the v1 release history.
  const result = exec('git', ['merge-base', '--is-ancestor', sourceSha, target], { cwd: root, check: false });
  if (result.status !== 0) wholeReleaseFail(`${description} ${sourceSha} is not an ancestor of ${target}.`);
}

function gitOutput(exec, root, args, action) {
  const result = exec('git', args, { cwd: root, check: false });
  if (result.status !== 0) wholeReleaseFail(`${action}: ${(result.stderr ?? '').trim()}`);
  return result.stdout;
}

/**
 * Resolve every service from the current receipt, or from the explicitly supplied
 * previous receipt when a patch publication omitted an unchanged image.
 */
export function resolveWholeReleaseImages(publication, manifest, previousPublication, {
  git, exec, root, verifyImage = verifyServiceRegistryImage,
}) {
  // Bind the full service set to a complete publication before comparing it with live Deployments.
  if (!isRecord(publication) || publication.status !== 'published' ||
      !SHA.test(publication.sourceSha ?? '') || !Array.isArray(publication.planned) ||
      !Array.isArray(publication.published)) {
    wholeReleaseFail('publication receipt is not a complete published receipt.');
  }
  if (!isRecord(manifest) || manifest.stage !== 'draft' || !Array.isArray(manifest.components)) {
    wholeReleaseFail('source manifest is not a tracked draft with a component list.');
  }
  const previous = previousPublication;
  if (previous !== undefined && (!isRecord(previous) || previous.status !== 'published' ||
      !SHA.test(previous.sourceSha ?? ''))) {
    wholeReleaseFail('the previous publication receipt is not a complete published receipt.');
  }
  const results = [];
  for (const service of WHOLE_RELEASE_SERVICES) {
    const currentComponents = manifest.components.filter(component => component?.id === service.componentId);
    if (currentComponents.length !== 1) {
      wholeReleaseFail(`source manifest must contain exactly one ${service.componentId} component.`);
    }
    const currentPlanned = publication.planned.filter(entry => entry?.id === service.componentId);
    const currentPublished = publication.published.filter(entry => entry?.id === service.componentId);
    const currentHasImage = currentPublished.length > 0;
    // A planned image without a published result is a failed publication, not permission to reuse an older image.
    if (!currentHasImage && currentPlanned.length > 0) {
      wholeReleaseFail(`${service.deployment} was planned by this publication but has no published image.`);
    }
    const sourcePublication = currentHasImage ? publication : previous;
    if (!sourcePublication) {
      wholeReleaseFail(`${service.deployment} is absent from the publication and has no previous receipt.`);
    }
    // Only an unchanged manifest version can reuse an omitted image from the previous publication.
    const sourceManifest = currentHasImage ? manifest : manifestForPrevious(previous, service, exec, root);
    if (!currentHasImage) {
      // Matching manifest versions prove this omission is unchanged rather than an unbuilt service update.
      const previousComponents = sourceManifest.components?.filter(component =>
        component?.id === service.componentId) ?? [];
      if (previousComponents.length !== 1 ||
          previousComponents[0].version !== currentComponents[0].version) {
        wholeReleaseFail(`${service.deployment} is missing from this publication but changed since the previous publication.`);
      }
      // A fallback must come from this release's history, never a newer publication.
      requireAncestor(exec, root, previous.sourceSha, publication.sourceSha, `${service.deployment} previous publication source`);
    }
    const selected = selectPublishedComponentImage(sourcePublication, sourceManifest, service.componentId, {
      git, exec, root, label: service.deployment,
    });
    requireAncestor(exec, root, selected.imageSourceSha, 'origin/v1', `${service.deployment} image source`);
    results.push({ ...service, ...selected, sourcePublicationSha: sourcePublication.sourceSha });
  }
  return results;
}

function manifestForPrevious(previous, service, exec, root) {
  // Read the previous manifest at its recorded source commit; the current manifest cannot prove its old artifact.
  const manifest = parseJson(gitOutput(exec, root, ['show', `${previous.sourceSha}:releases/foundation.json`],
    'Could not read the previous source-bound manifest'), 'previous source-bound release manifest');
  if (!manifest.components?.some(component => component?.id === service.componentId)) {
    wholeReleaseFail(`previous source-bound manifest has no ${service.deployment} component.`);
  }
  return manifest;
}

export async function verifyWholeReleaseImages(images, verifyImage) {
  const verified = [];
  for (const image of images) {
    // The registry independently proves the published digest and source metadata before cluster access.
    const registry = await verifyImage(image.image);
    if (!registry || !IMAGE_DIGEST.test(registry.digest ?? '') ||
        !(registry.acceptedDigests instanceof Set) || !registry.acceptedDigests.has(image.digest) ||
        registry.sourceSha !== image.imageSourceSha || registry.sourceTree !== image.imageSourceTree ||
        registry.version !== image.version) {
      wholeReleaseFail(`${image.deployment} digest, source commit, source tree, or version differs from its publication receipt.`);
    }
    verified.push({ ...image, acceptedDigests: registry.acceptedDigests });
  }
  return verified;
}

/**
 * Reject unknown Deployments and validate the fixed service identity before a
 * patch is planned. This prevents a similarly named workload from being selected.
 */
export function compareWholeReleaseState(inventory, images) {
  // Require exact coverage so an unrecognized or missing Deployment cannot be skipped in a whole-release receipt.
  const liveNames = new Set(inventory.byName.keys());
  const unknown = [...liveNames].filter(name => !SERVICE_NAMES.has(name));
  if (unknown.length) wholeReleaseFail(`unknown or unmapped Deployments: ${unknown.sort().join(', ')}.`);
  const missing = [...SERVICE_NAMES].filter(name => !liveNames.has(name));
  if (missing.length) wholeReleaseFail(`required Deployments are missing: ${missing.sort().join(', ')}.`);

  return images.map(image => {
    const deployment = inventory.byName.get(image.deployment);
    const labels = deployment.metadata?.labels ?? {};
    const podLabels = deployment.spec?.template?.metadata?.labels ?? {};
    const selector = { 'app.kubernetes.io/name': image.deployment };
    if (labels['app.kubernetes.io/name'] !== image.deployment ||
        labels['agentweaver.io/service'] !== image.deployment ||
        podLabels['app.kubernetes.io/name'] !== image.deployment ||
        podLabels['agentweaver.io/service'] !== image.deployment ||
        podLabels['agentweaver.io/workload'] !== image.deployment ||
        canonicalJson(deployment.spec?.selector?.matchLabels) !== canonicalJson(selector)) {
      wholeReleaseFail(`${image.deployment} Deployment identity and selector do not match the reviewed workload.`);
    }
    const containers = deployment.spec?.template?.spec?.containers ?? [];
    const matches = containers.map((container, index) => ({ container, index }))
      .filter(entry => entry.container?.name === image.container);
    if (matches.length !== 1 || (deployment.spec.replicas ?? 1) < 1 ||
        !Number.isSafeInteger(deployment.spec.replicas ?? 1)) {
      wholeReleaseFail(`${image.deployment} must have one named application container and at least one replica.`);
    }
    const selected = matches[0];
    const repository = `ghcr.io/sabbour/${componentImageRepository(image.componentId)}`;
    if (typeof deployment.metadata?.uid !== 'string' ||
        typeof selected.container.image !== 'string' ||
        !selected.container.image.startsWith(`${repository}@`)) {
      wholeReleaseFail(`${image.deployment} has no stable identity or digest-pinned service image.`);
    }
    return {
      ...image,
      deploymentObject: deployment,
      containerIndex: selected.index,
      currentImage: selected.container.image,
      currentDigest: imageDigest(selected.container.image, `${image.deployment} container ${image.container}`, wholeReleaseFail),
      replicas: deployment.spec.replicas ?? 1,
      changed: selected.container.image !== image.image,
    };
  });
}

export function wholeReleaseComparison(services) {
  return services.map(service => ({
    service: service.deployment,
    intended: service.targetImage ?? service.image,
    live: service.currentImage,
    change: Boolean(service.changed),
  }));
}

/**
 * Check the two schema-owning services against the recorded migration state.
 * This is read-only: schema changes remain the responsibility of db:maintenance.
 */
export async function checkSchemaCompatibility({ services, kubectl, deps, root, exec }) {
  // Compare recorded schema state to image-supported ranges; this path never changes database state.
  const stateResult = kubectl(['get', 'configmap', STATE_NAME, '-o', 'json']);
  if (stateResult.status !== 0) {
    wholeReleaseFail(`could not read ${STATE_NAME}; run db:maintenance before the whole release.`);
  }
  const data = parseJson(stateResult.stdout, 'schema state ConfigMap').data ?? {};
  const checks = [];
  for (const service of services.filter(item =>
    ['Agentweaver.Orchestrator', 'Agentweaver.EventsAndSessions'].includes(item.componentId))) {
    const key = service.deployment === 'orchestrator' ? 'orchestrator.schema' : 'events-and-sessions.schema';
    const currentSchema = Number(data[key]);
    if (!Number.isSafeInteger(currentSchema) || currentSchema < 1) {
      wholeReleaseFail(`${service.deployment} has no recorded migration schema version.`);
    }
    const range = await readImageSchemaRange({
      image: service.image,
      component: service.componentId,
      readImage: deps.readImage ?? (reference => deps.verifyImage(reference)),
      exec,
      repoRoot: root,
    });
    if (range.unknown || currentSchema < range.minSchema || currentSchema > range.maxSchema) {
      wholeReleaseFail(`${service.deployment} target image is incompatible with recorded schema ${currentSchema}.`);
    }
    checks.push({ deployment: service.deployment, schema: currentSchema, accepted: range });
  }
  return checks;
}

const REQUIRED_RUN_SCOPES = P1_RUN_SCOPES.filter(scope =>
  !['openid', 'profile', 'email', 'offline_access'].includes(scope));

function requireHttpsRoot(value, description) {
  let target;
  try {
    target = validateNetworkTarget(value, { exactPath: '/' });
  } catch {
    wholeReleaseFail(`${description} must be an absolute HTTPS origin.`);
  }
  if (target.protocol !== 'https:') wholeReleaseFail(`${description} must use HTTPS.`);
  return target;
}

function decodeWebRuntimeSettings(script) {
  // Treat the generated assignment as data; never evaluate JavaScript from the live Web endpoint.
  const assignment = /^window\.__AGENTWEAVER_CONFIG_BASE64__ = (\{[\s\S]*\});\s*$/.exec(script);
  if (!assignment) wholeReleaseFail('live Web runtime configuration is not a serialized settings object.');
  const encoded = parseJson(assignment[1], 'live Web runtime configuration');
  if (!isRecord(encoded)) wholeReleaseFail('live Web runtime configuration is not an object.');
  const settings = {};
  for (const name of ['GATEWAY_URL', 'IDENTITY_BROKER_URL', 'IDENTITY_BROKER_ISSUER']) {
    const value = encoded[name];
    if (value === undefined) continue;
    if (typeof value !== 'string' || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(value)) {
      wholeReleaseFail(`live Web ${name} setting is not valid base64.`);
    }
    if (!value) continue;
    const bytes = Buffer.from(value, 'base64');
    if (bytes.toString('base64') !== value) wholeReleaseFail(`live Web ${name} setting is not valid base64.`);
    try {
      settings[name] = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    } catch {
      wholeReleaseFail(`live Web ${name} setting is not valid UTF-8.`);
    }
  }
  return settings;
}

async function readLiveWebRuntimeSettings(webOrigin, fetchImpl) {
  let response;
  try {
    response = await fetchImpl(new URL('/env-config.js', webOrigin), {
      method: 'GET',
      headers: { Accept: 'application/javascript' },
      cache: 'no-store',
      redirect: 'error',
    });
  } catch {
    wholeReleaseFail('could not read the live Web runtime configuration.');
  }
  if (response.status !== 200) wholeReleaseFail(`live Web runtime configuration returned HTTP ${response.status}.`);
  let script;
  try {
    script = await response.text();
  } catch {
    wholeReleaseFail('live Web runtime configuration could not be read.');
  }
  return decodeWebRuntimeSettings(script);
}

/**
 * Resolve the smoke target from the live P0 Gateway and Web configuration.
 * Manual values remain explicit overrides for the existing release command.
 */
export async function resolveWholeReleaseSmokeEnvironment({
  gatewayRuntimeConfig, options, fetchImpl = globalThis.fetch,
}) {
  if (!isRecord(gatewayRuntimeConfig)) wholeReleaseFail('live Gateway runtime configuration is missing.');
  const webOrigin = requireHttpsRoot(gatewayRuntimeConfig.Gateway__WebOrigin, 'live Web origin');
  const runtimeIssuer = requireHttpsRoot(gatewayRuntimeConfig.Identity__Issuer, 'live Gateway issuer');
  const runtimeAudience = gatewayRuntimeConfig.Identity__Audience;
  if ((typeof runtimeAudience !== 'string' || !runtimeAudience.trim()) && !options.audience) {
    wholeReleaseFail('live Gateway audience is missing.');
  }

  const webSettings = await readLiveWebRuntimeSettings(webOrigin, fetchImpl);
  const configuredIssuer = webSettings.IDENTITY_BROKER_ISSUER || webSettings.IDENTITY_BROKER_URL;
  let webIssuer;
  if (configuredIssuer) {
    webIssuer = requireHttpsRoot(configuredIssuer, 'live Web Identity Broker issuer');
    if (webIssuer.href !== runtimeIssuer.href) {
      wholeReleaseFail('live Web and Gateway Identity Broker issuers do not match.');
    }
    if (new URL(registeredPortalRedirectUri(webIssuer.href)).origin !== webOrigin.origin) {
      wholeReleaseFail('live Web origin does not match the registered Identity Broker callback.');
    }
  } else if (!options.issuer) {
    wholeReleaseFail('live Web Identity Broker issuer is missing.');
  }

  let discoveredGatewayOrigin;
  if (webSettings.GATEWAY_URL) {
    let gatewayTarget;
    try {
      if (webSettings.GATEWAY_URL.includes('\\')) throw new Error();
      gatewayTarget = validateNetworkTarget(new URL(webSettings.GATEWAY_URL, webOrigin));
    } catch {
      wholeReleaseFail('live Web Gateway URL is invalid.');
    }
    if (gatewayTarget.protocol !== 'https:' || gatewayTarget.username || gatewayTarget.password
      || gatewayTarget.search || gatewayTarget.hash
      || gatewayTarget.pathname.replace(/\/+$/, '') !== '/api/v1'
      || gatewayTarget.origin !== webOrigin.origin) {
      wholeReleaseFail('live Web Gateway URL must be the same-origin HTTPS /api/v1 route.');
    }
    discoveredGatewayOrigin = gatewayTarget.origin;
  } else if (!options.gatewayUrl) {
    wholeReleaseFail('live Web Gateway URL is missing.');
  }

  return {
    gatewayUrl: options.gatewayUrl
      ? requireHttpsRoot(options.gatewayUrl, 'Gateway URL override').origin
      : discoveredGatewayOrigin,
    issuer: options.issuer
      ? requireHttpsRoot(options.issuer, 'Identity Broker issuer override').href
      : webIssuer.href,
    audience: options.audience ?? runtimeAudience,
    webOrigin: webOrigin.origin,
  };
}

function requireAuthorizationContext(value, { issuer, actorId, tenantId }) {
  const expectedKeys = [
    'contractVersion', 'issuer', 'actorId', 'tenantId', 'membershipRevision',
    'boundProjectId', 'boundRunId', 'effectiveAuthority',
  ];
  const keysMatch = isRecord(value)
    && Object.keys(value).length === expectedKeys.length
    && expectedKeys.every(key => Object.hasOwn(value, key));
  if (!keysMatch || value.contractVersion !== 1
    || value.issuer !== issuer || value.actorId !== actorId
    || typeof value.tenantId !== 'string' || !value.tenantId.trim()
    || !Number.isSafeInteger(value.membershipRevision) || value.membershipRevision < 1
    || value.boundProjectId !== null || value.boundRunId !== null
    || !Array.isArray(value.effectiveAuthority)
    || (tenantId !== undefined && value.tenantId !== tenantId)) {
    wholeReleaseFail('Gateway returned an invalid authorization context for the signed-in actor.');
  }
  return value.tenantId;
}

function selectSmokeProject(projects, requestedProjectId) {
  if (!Array.isArray(projects)) wholeReleaseFail('Gateway project list is not an array.');
  const active = projects.filter(project => isRecord(project)
    && typeof project.projectId === 'string' && project.projectId.trim()
    && project.state === 'active');
  const ids = active.map(project => project.projectId);
  if (new Set(ids).size !== ids.length) wholeReleaseFail('Gateway project list contains duplicate active project IDs.');
  if (requestedProjectId) {
    if (!active.some(project => project.projectId === requestedProjectId)) {
      wholeReleaseFail('the requested smoke project is not an active project for the signed-in actor.');
    }
    return requestedProjectId;
  }
  active.sort((left, right) => left.projectId.localeCompare(right.projectId));
  if (!active.length) wholeReleaseFail('the signed-in actor has no active project for Gateway smoke checks.');
  return active[0].projectId;
}

/**
 * Establish a P1 run-scope identity and discover an authorized project before any image is patched.
 */
export async function prepareGatewaySmokeContext({
  gatewayRuntimeConfig,
  options,
  fetchImpl = globalThis.fetch,
  env = process.env,
  signal,
  loadChromium = async () => (await import('playwright')).chromium,
  withBrowserContext = withFreshP1BrowserContext,
  authorize = authorizeP1InBrowser,
  io = console,
}) {
  const environment = await resolveWholeReleaseSmokeEnvironment({
    gatewayRuntimeConfig, options, fetchImpl,
  });
  let token = env[options.gatewayTokenEnv ?? 'AGENTWEAVER_GATEWAY_TOKEN'];
  if (!token) {
    const chromium = await loadChromium();
    // The shared helper creates and deletes a fresh profile; it can use browser SSO without copying user data.
    token = await withBrowserContext({ chromium, signal }, context => authorize({
      context,
      environment: { issuer: environment.issuer, audience: environment.audience },
      actorId: options.actorId,
      scopes: P1_RUN_SCOPES,
      signal,
      io,
    }));
  }
  const actorId = getValidatedP1BrokerTokenActorId(token, {
    issuer: environment.issuer,
    audience: environment.audience,
    actorId: options.actorId,
    requiredScopes: REQUIRED_RUN_SCOPES,
  });
  // Tenant membership is confirmed by the live Projects owner, not inferred from Broker token claims.
  const contextUrl = new URL('/api/v1/authorization/context', environment.gatewayUrl);
  const contextHeaders = {
    Accept: 'application/json',
    Authorization: `Bearer ${token}`,
  };
  if (options.tenantId) contextHeaders['X-Agentweaver-Tenant'] = options.tenantId;
  let contextResponse;
  try {
    contextResponse = await fetchImpl(contextUrl, {
      method: 'GET',
      headers: contextHeaders,
      cache: 'no-store',
      credentials: 'omit',
      redirect: 'error',
      signal: AbortSignal.timeout(30_000),
    });
  } catch {
    wholeReleaseFail('the signed-in actor authorization context could not be read from Gateway.');
  }
  if (contextResponse.status !== 200) {
    wholeReleaseFail(`Gateway authorization context returned HTTP ${contextResponse.status}.`);
  }
  let authorizationContext;
  try {
    authorizationContext = await contextResponse.json();
  } catch {
    wholeReleaseFail('Gateway authorization context is not valid JSON.');
  }
  const tenantId = requireAuthorizationContext(authorizationContext, {
    issuer: environment.issuer, actorId, tenantId: options.tenantId,
  });
  const authProvider = createBrokerAuthProvider({
    target: new URL(environment.gatewayUrl), token, issuer: environment.issuer,
    audience: environment.audience, actorId,
  });
  const client = new AgentweaverClient({
    baseUrl: environment.gatewayUrl, authProvider, tenantId, fetchImpl,
  });
  await client.discover();
  const projectCall = await client.invoke('listProjects');
  if (projectCall.status < 200 || projectCall.status >= 300) {
    wholeReleaseFail(`listProjects returned HTTP ${projectCall.status}.`);
  }
  const projectId = selectSmokeProject(projectCall.transientResponseBody, options.projectId);
  return { client, projectId };
}

/**
 * Use the existing authenticated Gateway client for non-mutating route smoke
 * checks. Every operation is discovered from the live OpenAPI document.
 */
export async function runGatewaySmokeChecks({ client, projectId }) {
  if (!client || typeof projectId !== 'string' || !projectId) {
    wholeReleaseFail('authenticated Gateway smoke check context is incomplete.');
  }
  // Fresh discovery and Projects reads ensure the receipt describes the newly deployed images.
  await client.discover();
  const checks = [];
  const projectCall = await client.invoke('listProjects');
  if (projectCall.status < 200 || projectCall.status >= 300) {
    wholeReleaseFail(`listProjects returned HTTP ${projectCall.status}.`);
  }
  selectSmokeProject(projectCall.transientResponseBody, projectId);
  checks.push({ operationId: 'listProjects', status: projectCall.status, path: projectCall.path });
  const calls = [
    ['listChiefOfStaffPriorities', {}],
    ['listProjectRuns', { pathParameters: { projectId } }],
    ['readProjectMetrics', { pathParameters: { projectId } }],
  ];
  // These discovered Gateway operations are read-only smoke checks, not deployment mutations.
  for (const [operationId, input] of calls) {
    if (!client.operations.has(operationId)) wholeReleaseFail(`Gateway does not advertise ${operationId}.`);
    const call = await client.invoke(operationId, input);
    if (call.status < 200 || call.status >= 300) wholeReleaseFail(`${operationId} returned HTTP ${call.status}.`);
    checks.push({ operationId, status: call.status, path: call.path });
  }
  return checks;
}

export function writeWholeReleaseReceipt(file, receipt, write = writeFileSync, lastFile = WHOLE_RELEASE_LAST_RECEIPT) {
  const contents = `${JSON.stringify(receipt, null, 2)}\n`;
  mkdirSync(path.dirname(file), { recursive: true });
  mkdirSync(path.dirname(lastFile), { recursive: true });
  write(file, contents, { flag: 'wx' });
  write(lastFile, contents);
}

export function wholeReleaseUpdates(services) {
  return new Map(services.map(service => [service.deployment, [{
    containerIndex: service.containerIndex,
    image: service.image,
  }]]));
}

export function verifyWholeReleaseReadback(before, after, services, updates, fail = wholeReleaseFail) {
  // Ignore only controller revision metadata; every other Deployment setting must survive the image update.
  verifyDeploymentPreservation(before, after, updates, {
    namespace: 'agentweaver',
    label: 'whole release',
    ignoredAnnotations: new Map(services.map(service =>
      [service.deployment, ['deployment.kubernetes.io/revision']])),
    fail,
  });
  return services.map(service => {
    const deployment = after.byName.get(service.deployment);
    if (deployment.spec.template.spec.containers[service.containerIndex]?.image !== service.image) {
      fail(`${service.deployment} Deployment digest does not match the intended image.`);
    }
    return {
      deployment: service.deployment,
      ...verifyDeploymentReady(deployment, service.replicas, service.deployment, fail),
    };
  });
}

export function plannedWholeReleaseInventory(before, services) {
  return planDeploymentInventory(before, wholeReleaseUpdates(services), 'agentweaver', wholeReleaseFail).records;
}
