import { createHash } from 'node:crypto';

const DIGEST = /^sha256:[a-f0-9]{64}$/;
const SHA = /^[a-f0-9]{40}$/;
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
  const repository = separator < 0 ? '' : image.slice(0, separator);
  const imageDigest = separator < 0 ? '' : image.slice(separator + 1);
  if (!repository.startsWith('ghcr.io/') ||
      repository.slice('ghcr.io/'.length).split('/').length < 2 ||
      repository.includes('@') || !DIGEST.test(imageDigest)) {
    fail(`invalid immutable GHCR image reference "${image}"`);
  }
  return { repository: repository.slice('ghcr.io/'.length), digest: imageDigest };
}

export async function pullToken(repository, fetchImpl) {
  const tokenUrl = new URL('https://ghcr.io/token');
  tokenUrl.searchParams.set('service', 'ghcr.io');
  tokenUrl.searchParams.set('scope', `repository:${repository}:pull`);
  const tokenResponse = await fetchImpl(tokenUrl, { headers: { accept: 'application/json' } });
  if (!tokenResponse.ok) {
    fail(`GHCR does not expose ${repository} for anonymous pull verification (HTTP ${tokenResponse.status})`);
  }
  const tokenJson = await tokenResponse.json();
  const token = tokenJson.token ?? tokenJson.access_token;
  if (typeof token !== 'string' || !token) fail(`GHCR returned no pull token for ${repository}`);
  return token;
}

async function registryReader(repository, fetchImpl) {
  const token = await pullToken(repository, fetchImpl);
  return {
    async manifest(imageDigest, expectedSize) {
      const response = await fetchImpl(`https://ghcr.io/v2/${repository}/manifests/${imageDigest}`, {
        headers: { accept: registryManifestAccept, authorization: `Bearer ${token}` },
      });
      if (!response.ok) fail(`published image ${repository}@${imageDigest} is unavailable from GHCR (HTTP ${response.status})`);
      const bytes = Buffer.from(await response.text(), 'utf8');
      if (response.headers.get('docker-content-digest') !== imageDigest ||
          digest(bytes) !== imageDigest ||
          (expectedSize !== undefined && bytes.length !== expectedSize)) {
        fail(`GHCR manifest bytes do not match the exact published digest for ${repository}@${imageDigest}`);
      }
      return { bytes, value: parseJson(bytes.toString('utf8'), `GHCR manifest ${imageDigest}`) };
    },
    async blob(imageDigest, expectedSize) {
      const response = await fetchImpl(`https://ghcr.io/v2/${repository}/blobs/${imageDigest}`, {
        headers: { authorization: `Bearer ${token}` },
      });
      if (!response.ok) fail(`published image config is unavailable from GHCR (HTTP ${response.status})`);
      const bytes = Buffer.from(await response.arrayBuffer());
      if (bytes.length !== expectedSize || digest(bytes) !== imageDigest) {
        fail('published image config bytes do not match their manifest descriptor');
      }
      return bytes;
    },
  };
}

export async function verifyRegistryImage(image, fetchImpl = globalThis.fetch) {
  const separator = image.indexOf('/', 'ghcr.io/'.length);
  const repository = image.slice('ghcr.io/'.length, image.lastIndexOf('@'));
  const imageDigest = image.slice(image.lastIndexOf('@') + 1);
  if (separator < 0 || !repository || !DIGEST.test(imageDigest)) fail(`invalid immutable GHCR image reference "${image}"`);
  const token = await pullToken(repository, fetchImpl);
  const response = await fetchImpl(`https://ghcr.io/v2/${repository}/manifests/${imageDigest}`, {
    headers: { accept: registryManifestAccept, authorization: `Bearer ${token}` },
  });
  if (!response.ok) fail(`published image ${image} is unavailable from GHCR (HTTP ${response.status})`);
  if (response.headers.get('docker-content-digest') !== imageDigest) {
    fail(`GHCR returned a different manifest digest for ${image}`);
  }
  const value = await response.json();
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

export async function verifyServiceRegistryImage(image, fetchImpl = globalThis.fetch) {
  const reference = splitImageReference(image);
  const reader = await registryReader(reference.repository, fetchImpl);
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
    image: `ghcr.io/${reference.repository}@${childDigest}`,
    digest: childDigest,
    acceptedDigests: new Set([reference.digest, childDigest]),
    configDigest: manifest.config.digest,
    sourceSha,
    sourceTree,
    version,
  };
}
