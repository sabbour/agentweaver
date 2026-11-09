/**
 * Reads and verifies immutable container image manifests from the configured OCI registry.
 * Pull-only authorization is requested, and response digests are checked against requested references.
 */
import { createHash } from 'node:crypto';
import { readRegistry } from './registry-read.mjs';
import { runAzAcrLoginToken } from '../azure/lib/exec.mjs';

const DIGEST = /^sha256:[a-f0-9]{64}$/;
const SHA = /^[a-f0-9]{40}$/;
export const DEFAULT_REGISTRY = 'ghcr.io/sabbour';
const ACR_LOGIN_SERVER = /^([a-z0-9]{5,50})\.azurecr\.io$/;
const registryManifestAccept = [
  'application/vnd.oci.image.index.v1+json',
  'application/vnd.oci.image.manifest.v1+json',
  'application/vnd.docker.distribution.manifest.list.v2+json',
  'application/vnd.docker.distribution.manifest.v2+json',
].join(', ');
const indexTypes = new Set([
  'application/vnd.oci.image.index.v1+json',
  'application/vnd.docker.distribution.manifest.list.v2+json',
]);
const manifestTypes = new Set([
  'application/vnd.oci.image.manifest.v1+json',
  'application/vnd.docker.distribution.manifest.v2+json',
]);
const configTypes = new Set([
  'application/vnd.oci.image.config.v1+json',
  'application/vnd.docker.container.image.v1+json',
]);

function fail(message) {
  throw new Error(`release deployment: ${message}`);
}

export function parseRegistryTarget(registry = DEFAULT_REGISTRY) {
  if (registry === DEFAULT_REGISTRY) {
    return { registry, host: 'ghcr.io', kind: 'ghcr', repositoryPrefix: 'sabbour/' };
  }
  const acr = ACR_LOGIN_SERVER.exec(registry ?? '');
  if (acr) return { registry, host: registry, kind: 'acr', name: acr[1], repositoryPrefix: '' };
  fail(`registry must be ${DEFAULT_REGISTRY} or an Azure Container Registry login server.`);
}

function digest(bytes) {
  return `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
}

function parseJson(text, description) {
  try {
    return JSON.parse(text);
  } catch (error) {
    fail(`${description} is not valid JSON: ${error.message}`);
  }
}

function splitImageReference(image) {
  const separator = typeof image === 'string' ? image.lastIndexOf('@') : -1;
  const namedImage = separator < 0 ? '' : image.slice(0, separator);
  const slash = namedImage.indexOf('/');
  const imageDigest = separator < 0 ? '' : image.slice(separator + 1);
  const host = slash < 0 ? '' : namedImage.slice(0, slash);
  const target = host === 'ghcr.io'
    ? parseRegistryTarget(DEFAULT_REGISTRY)
    : parseRegistryTarget(host);
  const path = slash < 0 ? '' : namedImage.slice(slash + 1);
  const repository = path.startsWith(target.repositoryPrefix)
    ? path.slice(target.repositoryPrefix.length) : '';
  if (!repository || path.includes('@') || !DIGEST.test(imageDigest)) {
    fail(`invalid immutable ${target.kind === 'acr' ? 'ACR' : 'GHCR'} image reference "${image}"`);
  }
  return { repository: `${target.repositoryPrefix}${repository}`, digest: imageDigest, target };
}

/**
 * Requests a pull-scoped bearer token for one registry repository.
 * Transient reads retry; the token is used only in the registry request headers.
 */
export async function pullToken(repository, fetchImpl, delay) {
  const tokenUrl = new URL('https://ghcr.io/token');
  tokenUrl.searchParams.set('service', 'ghcr.io');
  tokenUrl.searchParams.set('scope', `repository:${repository}:pull`);
  const { value: tokenJson } = await readRegistry(tokenUrl,
    { headers: { accept: 'application/json' } }, response => response.json(), { fetchImpl, delay });
  const token = tokenJson.token ?? tokenJson.access_token;
  if (typeof token !== 'string' || !token) fail(`GHCR returned no pull token for ${repository}`);
  return token;
}

/** Creates a reader that retries each manifest or config request independently. */
async function registryReader(repository, fetchImpl, delay) {
  const token = await pullToken(repository, fetchImpl, delay);
  return {
    async manifest(imageDigest, expectedSize) {
      const { value: text } = await readRegistry(`https://ghcr.io/v2/${repository}/manifests/${imageDigest}`, {
        headers: { accept: registryManifestAccept, authorization: `Bearer ${token}` },
      }, response => {
        if (response.headers.get('docker-content-digest') !== imageDigest) {
          fail(`GHCR manifest bytes do not match the exact published digest for ${repository}@${imageDigest}`);
        }
        return response.text();
      }, { fetchImpl, delay });
      const bytes = Buffer.from(text, 'utf8');
      if (digest(bytes) !== imageDigest ||
          (expectedSize !== undefined && bytes.length !== expectedSize)) {
        fail(`GHCR manifest bytes do not match the exact published digest for ${repository}@${imageDigest}`);
      }
      return { bytes, value: parseJson(bytes.toString('utf8'), `GHCR manifest ${imageDigest}`) };
    },
    async blob(imageDigest, expectedSize) {
      const { value } = await readRegistry(`https://ghcr.io/v2/${repository}/blobs/${imageDigest}`, {
        headers: { authorization: `Bearer ${token}` },
      }, response => response.arrayBuffer(), { fetchImpl, delay });
      const bytes = Buffer.from(value);
      if (bytes.length !== expectedSize || digest(bytes) !== imageDigest) {
        fail('published image config bytes do not match their manifest descriptor');
      }
      return bytes;
    },
  };
}

async function acrRegistryReader(reference, fetchImpl, delay, options = {}) {
  const { repository, target } = reference;
  const refreshToken = await (options.getAcrLoginToken ?? runAzAcrLoginToken)(target.name);
  const body = new URLSearchParams({
    grant_type: 'refresh_token',
    service: target.host,
    scope: `repository:${repository}:pull`,
    refresh_token: refreshToken,
  });
  const { value: tokenJson } = await readRegistry(`https://${target.host}/oauth2/token`, {
    method: 'POST',
    headers: { accept: 'application/json', 'content-type': 'application/x-www-form-urlencoded' },
    body,
  }, response => response.json(), { fetchImpl, delay });
  const token = tokenJson?.access_token ?? tokenJson?.accessToken;
  if (typeof token !== 'string' || !token) fail(`ACR returned no pull token for ${repository}`);
  return {
    async manifest(imageDigest, expectedSize) {
      const { value: text } = await readRegistry(
        `https://${target.host}/v2/${repository}/manifests/${imageDigest}`, {
          headers: { accept: registryManifestAccept, authorization: `Bearer ${token}` },
        }, response => {
          if (response.headers.get('docker-content-digest') !== imageDigest) {
            fail(`ACR manifest bytes do not match the exact published digest for ${repository}@${imageDigest}`);
          }
          return response.text();
        }, { fetchImpl, delay });
      const bytes = Buffer.from(text, 'utf8');
      if (digest(bytes) !== imageDigest ||
          (expectedSize !== undefined && bytes.length !== expectedSize)) {
        fail(`ACR manifest bytes do not match the exact published digest for ${repository}@${imageDigest}`);
      }
      return { bytes, value: parseJson(bytes.toString('utf8'), `ACR manifest ${imageDigest}`) };
    },
    async blob(imageDigest, expectedSize) {
      const { value } = await readRegistry(
        `https://${target.host}/v2/${repository}/blobs/${imageDigest}`, {
          headers: { authorization: `Bearer ${token}` },
        }, response => response.arrayBuffer(), { fetchImpl, delay });
      const bytes = Buffer.from(value);
      if (bytes.length !== expectedSize || digest(bytes) !== imageDigest) {
        fail('published image config bytes do not match their manifest descriptor');
      }
      return bytes;
    },
  };
}

function registryReaderFor(reference, fetchImpl, delay, options) {
  return reference.target.kind === 'acr'
    ? acrRegistryReader(reference, fetchImpl, delay, options)
    : registryReader(reference.repository, fetchImpl, delay);
}

/**
 * Verifies a registry image reference and its manifest digest without downloading image layers.
 * Only transient reads retry; digest mismatches fail immediately.
 */
export async function verifyRegistryImage(image, fetchImpl = globalThis.fetch, delay, options = {}) {
  const parsed = splitImageReference(image);
  if (parsed.target.kind === 'acr') {
    const reader = await registryReaderFor(parsed, fetchImpl, delay, options);
    const { value } = await reader.manifest(parsed.digest);
    const acceptedDigests = new Set([parsed.digest]);
    if (Array.isArray(value.manifests)) {
      const amd64 = value.manifests.filter((entry) =>
        entry.platform?.os === 'linux' && entry.platform?.architecture === 'amd64' &&
        DIGEST.test(entry.digest ?? ''));
      if (amd64.length === 0) fail(`published image ${image} has no linux/amd64 manifest`);
      for (const entry of amd64) acceptedDigests.add(entry.digest);
    }
    return acceptedDigests;
  }
  const separator = image.indexOf('/', 'ghcr.io/'.length);
  const repository = image.slice('ghcr.io/'.length, image.lastIndexOf('@'));
  const imageDigest = image.slice(image.lastIndexOf('@') + 1);
  if (separator < 0 || !repository || !DIGEST.test(imageDigest)) fail(`invalid immutable GHCR image reference "${image}"`);
  const token = await pullToken(repository, fetchImpl, delay);
  const { value } = await readRegistry(`https://ghcr.io/v2/${repository}/manifests/${imageDigest}`, {
    headers: { accept: registryManifestAccept, authorization: `Bearer ${token}` },
  }, response => {
    if (response.headers.get('docker-content-digest') !== imageDigest) {
      fail(`GHCR returned a different manifest digest for ${image}`);
    }
    return response.json();
  }, { fetchImpl, delay });
  const acceptedDigests = new Set([imageDigest]);
  if (Array.isArray(value.manifests)) {
    const amd64 = value.manifests.filter((entry) =>
      entry.platform?.os === 'linux' && entry.platform?.architecture === 'amd64' &&
      DIGEST.test(entry.digest ?? ''));
    if (amd64.length === 0) fail(`published image ${image} has no linux/amd64 manifest`);
    for (const entry of amd64) acceptedDigests.add(entry.digest);
  }
  return acceptedDigests;
}

/**
 * Verifies service image manifests and their platform-specific child manifests.
 * Transient reads retry without weakening source, digest, or size checks.
 */
export async function verifyServiceRegistryImage(image, fetchImpl = globalThis.fetch, delay, options = {}) {
  const reference = splitImageReference(image);
  const reader = await registryReaderFor(reference, fetchImpl, delay, options);
  const top = await reader.manifest(reference.digest);
  let childDigest = reference.digest;
  let child = top;
  if (Array.isArray(top.value.manifests)) {
    if (top.value.schemaVersion !== 2 || !indexTypes.has(top.value.mediaType)) {
      fail('published service image has an invalid raw manifest index');
    }
    const amd64 = top.value.manifests.filter(entry =>
      entry.platform?.os === 'linux' && entry.platform?.architecture === 'amd64');
    if (amd64.length !== 1 || !DIGEST.test(amd64[0]?.digest ?? '') ||
        !Number.isSafeInteger(amd64[0]?.size) || amd64[0].size <= 0) {
      fail('published service image must have exactly one valid linux/amd64 platform manifest');
    }
    childDigest = amd64[0].digest;
    child = await reader.manifest(childDigest, amd64[0].size);
  }

  const manifest = child.value;
  if (manifest.schemaVersion !== 2 || !manifestTypes.has(manifest.mediaType) ||
      !configTypes.has(manifest.config?.mediaType) || !DIGEST.test(manifest.config?.digest ?? '') ||
      !Number.isSafeInteger(manifest.config.size) || manifest.config.size <= 0) {
    fail('published service image manifest has an invalid config descriptor');
  }
  const config = parseJson(
    (await reader.blob(manifest.config.digest, manifest.config.size)).toString('utf8'),
    'published service image config',
  );
  const labels = config.config?.Labels;
  const sourceSha = labels?.['org.opencontainers.image.revision'];
  const sourceTree = labels?.['io.agentweaver.source-tree'];
  const version = labels?.['org.opencontainers.image.version'];
  if (config.os !== 'linux' || config.architecture !== 'amd64' ||
      !SHA.test(sourceSha ?? '') || !SHA.test(sourceTree ?? '') ||
      typeof version !== 'string' || version.trim() !== version || version.length === 0) {
    fail('published service image config does not contain valid source, tree, version, and linux/amd64 identity');
  }

  return {
    image: `${reference.target.host}/${reference.repository}@${childDigest}`,
    digest: childDigest,
    acceptedDigests: new Set([reference.digest, childDigest]),
    configDigest: manifest.config.digest,
    sourceSha,
    sourceTree,
    version,
  };
}

/**
 * Resolves the AgentHost linux/amd64 image and measures its unique compressed pull descriptors.
 */
export async function measureAgentHostRegistryImage(image, fetchImpl = globalThis.fetch, delay, options = {}) {
  const reference = splitImageReference(image);
  const reader = await registryReaderFor(reference, fetchImpl, delay, options);
  const top = await reader.manifest(reference.digest);
  let platformDigest = reference.digest;
  let platformManifest = top;
  if (Array.isArray(top.value.manifests)) {
    if (top.value.schemaVersion !== 2 || !indexTypes.has(top.value.mediaType)) {
      fail('published AgentHost image has an invalid raw manifest index');
    }
    const amd64 = top.value.manifests.filter(entry =>
      entry.platform?.os === 'linux' && entry.platform?.architecture === 'amd64');
    if (amd64.length !== 1 || !DIGEST.test(amd64[0]?.digest ?? '') ||
        !Number.isSafeInteger(amd64[0]?.size) || amd64[0].size <= 0) {
      fail('published AgentHost image must have exactly one valid linux/amd64 platform manifest');
    }
    platformDigest = amd64[0].digest;
    platformManifest = await reader.manifest(platformDigest, amd64[0].size);
  }

  const manifest = platformManifest.value;
  if (manifest.schemaVersion !== 2 || !manifestTypes.has(manifest.mediaType) ||
      !Array.isArray(manifest.layers) || manifest.layers.length === 0 ||
      manifest.layers.some(layer => layer.mediaType !== 'application/vnd.oci.image.layer.v1.tar+gzip') ||
      !configTypes.has(manifest.config?.mediaType) || !DIGEST.test(manifest.config?.digest ?? '') ||
      !Number.isSafeInteger(manifest.config.size) || manifest.config.size <= 0) {
    fail('published AgentHost platform manifest has invalid config or compressed-layer descriptors');
  }

  const descriptors = new Map();
  for (const descriptor of [manifest.config, ...manifest.layers]) {
    if (!DIGEST.test(descriptor.digest ?? '') || !Number.isSafeInteger(descriptor.size) || descriptor.size <= 0 ||
        descriptors.has(descriptor.digest) && descriptors.get(descriptor.digest) !== descriptor.size) {
      fail('published AgentHost image contains an invalid or conflicting blob descriptor');
    }
    descriptors.set(descriptor.digest, descriptor.size);
  }
  const config = parseJson(
    (await reader.blob(manifest.config.digest, manifest.config.size)).toString('utf8'),
    'published AgentHost image config',
  );
  const labels = config.config?.Labels;
  if (config.os !== 'linux' || config.architecture !== 'amd64' || config.config?.User !== '1654:1654' ||
      JSON.stringify(config.config?.Entrypoint) !== JSON.stringify(['dotnet', 'Agentweaver.AgentHost.dll']) ||
      !SHA.test(labels?.['org.opencontainers.image.revision'] ?? '') ||
      !SHA.test(labels?.['io.agentweaver.source-tree'] ?? '') ||
      typeof labels?.['org.opencontainers.image.version'] !== 'string' ||
      labels['org.opencontainers.image.version'].trim() !== labels['org.opencontainers.image.version'] ||
      labels['org.opencontainers.image.version'].length === 0) {
    fail('published AgentHost image config does not match the linux/amd64 runtime identity');
  }

  const compressedPullBytes = [...descriptors.values()]
    .reduce((total, size) => total + size, platformManifest.bytes.length);
  if (!Number.isSafeInteger(compressedPullBytes) || compressedPullBytes <= 0) {
    fail('published AgentHost compressed pull-byte total is not a positive safe integer');
  }
  return {
    digest: platformDigest,
    platform: 'linux/amd64',
    compressedPullBytes,
    configDigest: manifest.config.digest,
    image: `${reference.target.host}/${reference.repository}@${platformDigest}`,
  };
}

/** Reads only the OCI revision label used to parse a migration image's schema support. */
export async function readServiceImageRevision(image, fetchImpl = globalThis.fetch, delay, options = {}) {
  const reference = splitImageReference(image);
  const reader = await registryReaderFor(reference, fetchImpl, delay, options);
  const top = await reader.manifest(reference.digest);
  let childDigest = reference.digest;
  let child = top;
  if (Array.isArray(top.value.manifests)) {
    const amd64 = top.value.manifests.filter(entry =>
      entry.platform?.os === 'linux' && entry.platform?.architecture === 'amd64');
    if (amd64.length !== 1 || !DIGEST.test(amd64[0]?.digest ?? '') ||
        !Number.isSafeInteger(amd64[0]?.size) || amd64[0].size <= 0) {
      fail('service image must have exactly one valid linux/amd64 platform manifest');
    }
    childDigest = amd64[0].digest;
    child = await reader.manifest(childDigest, amd64[0].size);
  }

  const manifest = child.value;
  if (!manifestTypes.has(manifest.mediaType) || !configTypes.has(manifest.config?.mediaType) ||
      !DIGEST.test(manifest.config?.digest ?? '') || !Number.isSafeInteger(manifest.config.size) ||
      manifest.config.size <= 0) {
    fail('service image manifest has an invalid config descriptor');
  }
  const config = parseJson(
    (await reader.blob(manifest.config.digest, manifest.config.size)).toString('utf8'),
    'service image config',
  );
  const sourceSha = config.config?.Labels?.['org.opencontainers.image.revision'];
  if (config.os !== 'linux' || config.architecture !== 'amd64' || !SHA.test(sourceSha ?? '')) {
    fail('service image has no valid linux/amd64 revision label');
  }
  return { digest: childDigest, sourceSha };
}
