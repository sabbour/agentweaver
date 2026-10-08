// Only the manually confirmed publication workflow invokes this script.
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { existsSync, lstatSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateFile, WEB_LOCK_PATH, WEB_PROJECT_PATH } from './validate.mjs';
import { componentImageRepository } from './pack.mjs';
import { resolveProbeImageSource } from '../azure/build-foundation-probe-image.mjs';
import { runPublicationCommand as command } from './command.mjs';

const hash = (bytes) => createHash('sha256').update(bytes).digest('hex');
const fail = (message) => { throw new Error(`manual publication: ${message}`); };
const repositoryPathPattern = /^[a-z0-9]+(?:[._-][a-z0-9]+)*(?:\/[a-z0-9]+(?:[._-][a-z0-9]+)*)*$/;
const initialProbeTarget = 'ghcr.io/sabbour/agentweaver-foundation-probe:0.0.0';
const initialProbeDigest = 'sha256:452be7e284ee6c33814fcedcf1d7c98f98384d09ea7239ad851f9cb316727c9a';
const initialProbeClaim = 'agentweaver-publication/initial-foundation-probe-0.0.0-replacement';
const samplerProbeDigest = 'sha256:835d5b8899f2a8956faf24d46a934ec745d91ff83363d77f22f2859c2f743969';
const samplerProbeClaim = 'agentweaver-publication/foundation-probe-0.0.0-sampler-replacement';
const contentHashPattern = /^[a-f0-9]{64}$/;
const imageIdPattern = /^sha256:[a-f0-9]{64}$/;

function validateWebArtifact(component, artifact, componentProvenance, sourceSha, root, git) {
  const reject = () => fail('prepared web image provenance does not match the exact source, version, or linux/amd64 artifact');
  const repository = componentImageRepository(component.id);
  const tag = `${component.version.replaceAll('+', '_')}-${sourceSha}`;
  const imageReference = `${repository}:${tag}`;
  const source = componentProvenance?.source;
  const build = componentProvenance?.build;
  const sourceFiles = source?.files;
  if (component.id !== 'Agentweaver.Web' || component.kind !== 'service' ||
      component.project !== WEB_PROJECT_PATH || artifact.repository !== repository ||
      artifact.tag !== tag || artifact.imageReference !== imageReference ||
      artifact.platform !== 'linux/amd64' || !imageIdPattern.test(artifact.imageId ?? '') ||
      artifact.labels?.['org.opencontainers.image.revision'] !== sourceSha ||
      artifact.labels?.['org.opencontainers.image.version'] !== component.version ||
      !Array.isArray(sourceFiles) || sourceFiles.length === 0) {
    reject();
  }

  const sourcePaths = git('ls-tree', '-r', '--name-only', sourceSha, '--', 'apps/web')
    .split(/\r?\n/).filter(Boolean).sort();
  if (sourcePaths.length !== sourceFiles.length ||
      sourcePaths.some((file, index) => sourceFiles[index]?.path !== file)) {
    reject();
  }
  const actualFiles = sourcePaths.map((file) => {
    const full = path.resolve(root, file);
    if (!full.startsWith(root + path.sep)) reject();
    let stat;
    let bytes;
    try {
      stat = lstatSync(full);
      bytes = readFileSync(full);
    } catch (error) {
      fail(`prepared web source file ${file} cannot be verified: ${error.message}`);
    }
    if (!stat.isFile()) reject();
    return { path: file, sha256: hash(bytes), size: stat.size };
  });
  if (actualFiles.some((file, index) =>
    file.sha256 !== sourceFiles[index]?.sha256 || file.size !== sourceFiles[index]?.size)) {
    reject();
  }
  const treeSha256 = hash(JSON.stringify(actualFiles.map(({ path: file, sha256 }) => [file, sha256])));
  const sourceByPath = new Map(actualFiles.map(({ path: file, sha256 }) => [file, sha256]));
  if (source.treeSha256 !== treeSha256 ||
      source.packageJsonSha256 !== sourceByPath.get(WEB_PROJECT_PATH) ||
      source.packageLockSha256 !== sourceByPath.get(WEB_LOCK_PATH) ||
      source.dockerfileSha256 !== sourceByPath.get('apps/web/Dockerfile') ||
      componentProvenance?.lock?.path !== WEB_LOCK_PATH ||
      componentProvenance.lock.sha256 !== sourceByPath.get(WEB_LOCK_PATH)) {
    reject();
  }

  const baseImages = artifact.baseImages;
  const output = artifact.buildOutput;
  if (!Array.isArray(baseImages) || baseImages.length === 0 ||
      baseImages.some((image) => typeof image.reference !== 'string' ||
        !/@sha256:[a-f0-9]{64}$/.test(image.reference) ||
        image.repositoryDigest !== image.reference ||
        !imageIdPattern.test(image.imageId ?? '')) ||
      !Array.isArray(output?.files) || output.files.length === 0 ||
      output.files.some((file) => typeof file.path !== 'string' ||
        !contentHashPattern.test(file.sha256 ?? '') ||
        !Number.isSafeInteger(file.size) || file.size < 0) ||
      !contentHashPattern.test(output.sha256 ?? '') ||
      hash(JSON.stringify(output.files)) !== output.sha256 ||
      build?.tool !== 'npm' || build.dockerfile !== 'apps/web/Dockerfile' ||
      build.dockerBuildArguments?.IMAGE_TAG !== component.version ||
      build.dockerBuildArguments?.GIT_SHA !== sourceSha ||
      build.imageId !== artifact.imageId || build.imageReference !== imageReference ||
      build.platform !== 'linux/amd64' ||
      JSON.stringify(build.baseImages) !== JSON.stringify(baseImages) ||
      JSON.stringify(build.output) !== JSON.stringify(output)) {
    reject();
  }
  return imageReference;
}

function parseRegistry(value) {
  if (typeof value !== 'string' || value.trim() !== value) return undefined;
  const slash = value.indexOf('/');
  const host = slash < 0 ? value : value.slice(0, slash);
  const repositoryPath = slash < 0 ? undefined : value.slice(slash + 1);
  const authority = /^([a-z0-9.-]+)(?::([0-9]+))?$/.exec(host);
  if (!authority) return undefined;

  const [, hostname, port] = authority;
  if (hostname.length > 253 || hostname.split('.').some((label) =>
    label.length > 63 || !/^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/.test(label))) return undefined;
  if (port !== undefined && (!/^[1-9]\d{0,4}$/.test(port) || Number(port) > 65535)) return undefined;
  if (repositoryPath !== undefined &&
      (repositoryPath.length > 255 || !repositoryPathPattern.test(repositoryPath))) return undefined;

  return { host, repositoryPath: repositoryPath ?? '' };
}

export function publishArtifacts(manifestPath, outDir, sourceSha, {
  root = process.cwd(), confirmed = false, env = process.env, run = command,
  writeFileSync: writeReceipt = writeFileSync,
  packagesOnly = false,
  foundationProbeOnly = false,
  confirmFoundationProbeInitialReplacement,
  confirmFoundationProbeSamplerReplacement,
  expectedFoundationProbeSamplerDigest,
} = {}) {
  root = path.resolve(root);
  if (!confirmed || !/^[a-f0-9]{40}$/.test(sourceSha ?? '')) fail('explicit confirmation and exact source SHA are required');
  if (packagesOnly && foundationProbeOnly) fail('package-only and Foundation Probe-only publication are mutually exclusive');
  const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  if (git('rev-parse', 'HEAD') !== sourceSha || git('status', '--porcelain')) fail('publication requires a clean exact source HEAD');
  const manifest = validateFile(path.resolve(root, manifestPath), { root });
  const sourceManifest = JSON.parse(execFileSync('git', ['show', `${sourceSha}:${path.relative(root, path.resolve(root, manifestPath)).replaceAll('\\', '/')}`],
    { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }));
  if (JSON.stringify(sourceManifest) !== JSON.stringify(manifest)) fail('manifest differs from source');
  const directory = path.resolve(root, outDir);
  if (existsSync(path.join(directory, 'publication.json'))) fail('publication receipt already exists; inspect prior or partial publication before any retry');
  const provenance = JSON.parse(readFileSync(path.join(directory, 'provenance.json'), 'utf8'));
  if (provenance.schemaVersion !== 1 || provenance.sourceSha !== sourceSha ||
      provenance.manifestSha256 !== hash(JSON.stringify(manifest))) fail('provenance does not match exact source composition');
  const components = foundationProbeOnly
    ? manifest.components.filter(component => component.id === 'Agentweaver.FoundationProbe' && component.kind === 'service')
    : packagesOnly
    ? manifest.components.filter((component) => component.kind !== 'service')
    : manifest.components;
  if (components.length === 0) fail('the selected composition has no components to publish');
  const replaceInitialProbe = confirmFoundationProbeInitialReplacement !== undefined;
  const replaceSamplerProbe = confirmFoundationProbeSamplerReplacement !== undefined;
  if (replaceInitialProbe && replaceSamplerProbe) fail('Probe replacement intents are mutually exclusive');
  if ((replaceSamplerProbe && expectedFoundationProbeSamplerDigest !== samplerProbeDigest) ||
      (!replaceSamplerProbe && expectedFoundationProbeSamplerDigest !== undefined)) {
    fail('sampler Probe replacement requires only the exact approved current digest');
  }
  const replaceProbe = replaceInitialProbe || replaceSamplerProbe;
  const replacementDigest = replaceSamplerProbe ? samplerProbeDigest : initialProbeDigest;
  const replacementClaim = replaceSamplerProbe ? samplerProbeClaim : initialProbeClaim;
  const replacementField = replaceSamplerProbe ? 'samplerProbeReplacement' : 'initialProbeReplacement';
  const replacementConfirmation = replaceSamplerProbe
    ? confirmFoundationProbeSamplerReplacement : confirmFoundationProbeInitialReplacement;
  if (replaceInitialProbe && (!foundationProbeOnly || packagesOnly || components.length !== 1 ||
      components[0].id !== 'Agentweaver.FoundationProbe' || components[0].version !== '0.0.0' ||
      confirmFoundationProbeInitialReplacement !== sourceSha ||
      env.GITHUB_REPOSITORY !== 'sabbour/agentweaver' || env.RELEASE_REGISTRY !== 'ghcr.io/sabbour')) {
    fail('initial Probe replacement requires only the approved Probe 0.0.0, exact source confirmation, and existing repository target');
  }
  if (replaceSamplerProbe && (!foundationProbeOnly || packagesOnly || components.length !== 1 ||
      components[0].id !== 'Agentweaver.FoundationProbe' || components[0].version !== '0.0.0' ||
      replacementConfirmation !== sourceSha ||
      env.GITHUB_REPOSITORY !== 'sabbour/agentweaver' || env.RELEASE_REGISTRY !== 'ghcr.io/sabbour')) {
    fail('sampler Probe replacement requires only the approved Probe 0.0.0, exact source confirmation, and existing repository target');
  }
  if (replaceProbe && git('rev-parse', 'refs/remotes/origin/v1') !== sourceSha) {
    fail('Probe replacement requires the exact admitted origin/v1 source');
  }
  if (!replaceProbe && components.some(component =>
    component.id === 'Agentweaver.FoundationProbe' && component.version === '0.0.0')) {
    fail('the initial Probe tag cannot be published without its separately confirmed one-time replacement');
  }
  if (!Array.isArray(provenance.components) || provenance.components.length !== components.length ||
      provenance.components.some((record, index) => {
        const component = components[index];
        return !component || record?.id !== component.id || record?.kind !== component.kind ||
          record?.version !== component.version || record?.project !== component.project;
      })) {
    fail(`provenance component selection does not match the ${foundationProbeOnly ? 'Foundation Probe-only' : packagesOnly ? 'package-only' : 'full'} manifest selection`);
  }
  if (!Array.isArray(provenance.artifacts) || provenance.artifacts.length !== components.length) {
    fail('missing or unexpected artifact provenance');
  }
  const selected = [];
  for (let index = 0; index < components.length; index++) {
    const component = components[index];
    const kind = component.kind === 'service' ? 'image' : 'package';
    const filename = kind === 'image' ? `${component.id}.${component.version}.tar.gz`
      : `${path.basename(component.project, '.csproj')}.${component.version}.nupkg`;
    const artifactFilename = component.project === WEB_PROJECT_PATH
      ? `${component.id}.${component.version}.tar`
      : filename;
    const matches = provenance.artifacts.filter((artifact) =>
      artifact.path === artifactFilename && artifact.kind === kind && artifact.componentId === component.id);
    if (matches.length !== 1) fail(`missing unique artifact for ${component.id}`);
    const artifact = matches[0];
    const file = path.join(directory, artifactFilename);
    if (hash(readFileSync(file)) !== artifact.sha256) fail(`artifact hash mismatch for ${component.id}`);
    const componentProvenance = provenance.components[index];
    const localImageReference = component.project === WEB_PROJECT_PATH
      ? validateWebArtifact(component, artifact, componentProvenance, sourceSha, root, git)
      : undefined;
    selected.push({ component, artifact, file, localImageReference });
  }
  const hasPackages = selected.some(({ artifact }) => artifact.kind === 'package');
  const hasImages = selected.some(({ artifact }) => artifact.kind === 'image');
  let registry;
  if (hasPackages) {
    let feed;
    try { feed = new URL(env.RELEASE_NUGET_SOURCE); } catch { fail('configure an HTTPS NuGet feed and API key'); }
    if (feed.protocol !== 'https:' || feed.username || feed.password || feed.search || feed.hash || !env.RELEASE_NUGET_API_KEY) {
      fail('configure a credential-free HTTPS feed URL and a separate secret API key');
    }
  }
  if (hasImages) {
    registry = parseRegistry(env.RELEASE_REGISTRY);
    if (!registry || !env.RELEASE_REGISTRY_USER || !env.RELEASE_REGISTRY_PASSWORD || !env.DOCKER_CONFIG) {
      fail('configure a normalized registry host with an optional lowercase repository path, separate secret credentials, and an isolated Docker configuration directory');
    }
  }
  const imageRepositories = new Map();
  for (const { component, artifact } of selected) {
    if (artifact.kind !== 'image') continue;
    const repositoryPath = [registry.repositoryPath, componentImageRepository(component.id)].filter(Boolean).join('/');
    if (repositoryPath.length > 255) fail(`image repository path for ${component.id} exceeds 255 characters`);
    imageRepositories.set(component.id, `${registry.host}/${repositoryPath}`);
  }
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(env.GITHUB_REPOSITORY ?? '') || !env.GH_TOKEN) {
    fail('configure the GitHub repository and scoped publication token for durable claims');
  }
  const namespace = `agentweaver-publication/${sourceSha}`;
  const repository = `repos/${env.GITHUB_REPOSITORY}`;
  const api = (method, endpoint, body) => {
    const args = ['api', '--method', method, `${repository}/${endpoint}`];
    if (body) args.push('--input', '-');
    return JSON.parse(run('gh', args, body ? JSON.stringify(body) : undefined));
  };
  const readDescriptor = reference => {
    const descriptor = JSON.parse(run('docker', ['buildx', 'imagetools', 'inspect', reference,
      '--format', '{{json .Manifest}}']));
    if (!/^sha256:[a-f0-9]{64}$/.test(descriptor?.digest ?? '')) {
      fail('native Probe manifest descriptor is missing or malformed');
    }
    if (descriptor.schemaVersion === undefined) {
      const raw = run('docker', ['buildx', 'imagetools', 'inspect', reference, '--raw']);
      const manifest = JSON.parse(raw);
      if (`sha256:${hash(raw)}` !== descriptor.digest || Buffer.byteLength(raw) !== descriptor.size ||
          manifest.schemaVersion !== 2 || manifest.mediaType !== descriptor.mediaType) {
        fail('native Probe raw manifest does not match its descriptor bytes, size, and schema');
      }
      return { ...manifest, digest: descriptor.digest, size: descriptor.size };
    }
    if (descriptor.schemaVersion !== 2) fail('native Probe manifest descriptor is missing or malformed');
    return descriptor;
  };
  let previousProbeIndex;
  let preservedInitialProbeIndex;
  if (replaceProbe) {
    const replacementPrior = api('GET', `git/matching-refs/tags/${replacementClaim}/`);
    if (!Array.isArray(replacementPrior) || replacementPrior.length !== 0) {
      fail('the single-use Probe replacement was already claimed or its state is ambiguous');
    }
    previousProbeIndex = readDescriptor(initialProbeTarget);
    if (previousProbeIndex.digest !== replacementDigest ||
        (replaceInitialProbe && (previousProbeIndex.mediaType !== 'application/vnd.oci.image.index.v1+json' ||
          !Array.isArray(previousProbeIndex.manifests) || previousProbeIndex.manifests.length === 0)) ||
        (replaceSamplerProbe && (!['application/vnd.oci.image.manifest.v1+json',
          'application/vnd.docker.distribution.manifest.v2+json'].includes(previousProbeIndex.mediaType) ||
          previousProbeIndex.manifests !== undefined))) {
      fail('Probe tag drifted from the exact approved previous manifest; refusing replacement');
    }
    if (replaceSamplerProbe) {
      preservedInitialProbeIndex = readDescriptor(`ghcr.io/sabbour/agentweaver-foundation-probe@${initialProbeDigest}`);
      if (preservedInitialProbeIndex.digest !== initialProbeDigest ||
          preservedInitialProbeIndex.mediaType !== 'application/vnd.oci.image.index.v1+json' ||
          !Array.isArray(preservedInitialProbeIndex.manifests) || preservedInitialProbeIndex.manifests.length === 0) {
        fail('the preserved initial Probe index is missing or malformed; refusing sampler replacement');
      }
    }
  }
  const prior = api('GET', `git/matching-refs/tags/${namespace}/`);
  if (!Array.isArray(prior) || prior.length !== 0) {
    fail('durable publication claim/result already exists or is ambiguous; inspect remote state before any retry');
  }
  const planned = selected.map(({ component, artifact }) => ({
    id: component.id, version: component.version, kind: artifact.kind, sha256: artifact.sha256,
    destination: artifact.kind === 'package' ? env.RELEASE_NUGET_SOURCE
      : `${imageRepositories.get(component.id)}:${component.version}`,
  }));
  const receipt = {
    schemaVersion: 1, sourceSha, provenanceSha256: hash(readFileSync(path.join(directory, 'provenance.json'))),
    planned, status: 'partial', published: [],
  };
  if (replaceProbe) receipt[replacementField] = {
    target: initialProbeTarget, previousIndex: previousProbeIndex, sourceSha,
    ...(replaceSamplerProbe ? { expectedCurrentDigest: replacementDigest, preservedInitialIndex: preservedInitialProbeIndex } : {}),
    userConfirmedBaselineOverride: true,
    guard: 'single-use-claim-and-fresh-native-index',
  };
  const createRecord = (name, record, recordNamespace = namespace) => {
    const tag = `${recordNamespace}/${name}`;
    const message = JSON.stringify(record);
    const object = api('POST', 'git/tags', { tag, message, object: sourceSha, type: 'commit' });
    if (!/^[a-f0-9]{40}$/.test(object?.sha ?? '') || object.tag !== tag || object.message !== message ||
        object.object?.sha !== sourceSha || object.object?.type !== 'commit') {
      fail(`durable ${name} tag response does not match exact source record`);
    }
    const ref = `refs/tags/${tag}`;
    const created = api('POST', 'git/refs', { ref, sha: object.sha });
    if (created?.ref !== ref || created.object?.type !== 'tag' || created.object?.sha !== object.sha) {
      fail(`durable ${name} ref response does not match the created tag; inspect remote state`);
    }
    return object.sha;
  };
  if (replaceProbe) {
    receipt[replacementField].claimSha = createRecord('claim',
      { ...receipt, status: 'claimed' }, replacementClaim);
  }
  receipt.claimSha = createRecord('claim', { ...receipt, status: 'claimed' });
  let publicationError;
  try {
    for (const { component, artifact, file, localImageReference } of selected) {
      if (localImageReference === undefined) continue;
      run('docker', ['load', '--input', file]);
      let image;
      try {
        image = JSON.parse(run('docker', ['image', 'inspect', '--format', '{{json .}}', localImageReference]));
      } catch (error) {
        fail(`loaded web image cannot be inspected: ${error.message}`);
      }
      const labels = image?.Config?.Labels;
      if (image?.Id !== artifact.imageId || image?.Os !== 'linux' || image?.Architecture !== 'amd64' ||
          labels?.['org.opencontainers.image.revision'] !== sourceSha ||
          labels?.['org.opencontainers.image.version'] !== component.version) {
        fail('loaded web image does not match its source, version, local image identity, or linux/amd64 provenance');
      }
    }
    if (hasImages) run('docker', ['login', registry.host, '--username', env.RELEASE_REGISTRY_USER, '--password-stdin'], env.RELEASE_REGISTRY_PASSWORD);
    for (const { component, artifact, file } of selected) {
      if (artifact.kind === 'package') {
        run('dotnet', ['nuget', 'push', file, '--source', env.RELEASE_NUGET_SOURCE, '--api-key', env.RELEASE_NUGET_API_KEY]);
        receipt.published.push({ id: component.id, version: component.version, kind: 'package', feed: env.RELEASE_NUGET_SOURCE, sha256: artifact.sha256 });
      } else {
        const local = component.project === WEB_PROJECT_PATH
          ? artifact.imageReference
          : `${componentImageRepository(component.id)}:${component.version}`;
        const remote = `${imageRepositories.get(component.id)}:${component.version}`;
        if (component.project !== WEB_PROJECT_PATH) run('docker', ['load', '--input', file]);
        if (replaceProbe) {
          const config = JSON.parse(run('docker', ['inspect', '--format', '{{json .Config}}', local]));
          const source = resolveProbeImageSource({ repoRoot: root });
          if (remote !== initialProbeTarget || source.sourceSha !== sourceSha ||
              config.User !== '10001:10001' ||
              JSON.stringify(config.Entrypoint) !== JSON.stringify(['dotnet', 'Agentweaver.FoundationProbe.dll']) ||
              config.Labels?.['org.opencontainers.image.revision'] !== sourceSha ||
              config.Labels?.['io.agentweaver.source-tree'] !== source.sourceTree ||
              config.Labels?.['io.agentweaver.infrastructure-source-hash'] !== source.sourceHash ||
              config.Labels?.['org.opencontainers.image.version'] !== '0.0.0') {
            fail('prepared Probe image does not match the corrected source, binary baseline, and image contract');
          }
        }
        run('docker', ['tag', local, remote]);
        if (replaceProbe && readDescriptor(initialProbeTarget).digest !== replacementDigest) {
          fail('Probe tag changed before push; refusing concurrent replacement');
        }
        run('docker', ['push', remote]);
        const digests = JSON.parse(run('docker', ['inspect', '--format', '{{json .RepoDigests}}', remote]));
        const digest = digests.find((value) => value.startsWith(`${imageRepositories.get(component.id)}@sha256:`));
        if (!digest || !/@sha256:[a-f0-9]{64}$/.test(digest)) fail(`registry did not return an immutable digest for ${component.id}`);
        if (replaceProbe) {
          receipt[replacementField].newDigest = digest.slice(digest.lastIndexOf('@') + 1);
          receipt.published.push({ id: component.id, version: component.version, kind: 'image', image: digest, archiveSha256: artifact.sha256 });
          const current = readDescriptor(initialProbeTarget);
          if (current.digest === replacementDigest || digest !== `${imageRepositories.get(component.id)}@${current.digest}`) {
            fail('corrected Probe tag did not resolve to the actual new published manifest');
          }
          for (const previous of [previousProbeIndex, ...(preservedInitialProbeIndex ? [preservedInitialProbeIndex] : [])]) {
            const retained = readDescriptor(`${imageRepositories.get(component.id)}@${previous.digest}`);
            if (retained.digest !== previous.digest) fail('the previous Probe manifest is not retained by digest');
            for (const manifest of previous.manifests ?? []) {
              if (readDescriptor(`${imageRepositories.get(component.id)}@${manifest.digest}`).digest !== manifest.digest) {
                fail('an original Probe platform manifest is not retained by digest');
              }
            }
          }
          receipt[replacementField].originalIndexAndPlatformsRetained = true;
        } else {
          receipt.published.push({ id: component.id, version: component.version, kind: 'image', image: digest, archiveSha256: artifact.sha256 });
        }
      }
    }
    receipt.status = 'published';
    return receipt;
  } catch (error) {
    publicationError = error;
    throw error;
  } finally {
    const failures = [];
    try {
      createRecord('result', receipt);
      if (replaceProbe) createRecord('result', receipt, replacementClaim);
    } catch (receiptError) {
      failures.push(receiptError);
    }
    try {
      writeReceipt(path.join(directory, 'publication.json'), JSON.stringify(receipt, null, 2) + '\n');
    } catch (receiptError) {
      failures.push(receiptError);
    }
    if (failures.length) {
      throw new AggregateError([...(publicationError ? [publicationError] : []), ...failures],
        `publication receipt persistence failed: ${failures.map(({ message }) => message).join('; ')}; permanent claim blocks retry; inspect remote state`,
        { cause: publicationError ?? failures[0] });
    }
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2);
    const [manifest, output, sourceSha] = args;
    const replacementIndex = args.indexOf('--confirm-foundation-probe-initial-replacement');
    const samplerIndex = args.indexOf('--confirm-foundation-probe-sampler-replacement');
    const expectedDigestIndex = args.indexOf('--expected-foundation-probe-sampler-digest');
    if (replacementIndex >= 0 && !/^[a-f0-9]{40}$/.test(args[replacementIndex + 1] ?? '')) {
      fail('the one-time initial Probe replacement requires its exact source SHA confirmation');
    }
    if (samplerIndex >= 0 && !/^[a-f0-9]{40}$/.test(args[samplerIndex + 1] ?? '')) {
      fail('the one-time sampler Probe replacement requires its exact source SHA confirmation');
    }
    if (expectedDigestIndex >= 0 && args[expectedDigestIndex + 1] !== samplerProbeDigest) {
      fail('sampler Probe replacement requires the exact approved current digest');
    }
    publishArtifacts(manifest, output, sourceSha, {
      confirmed: args.includes('--confirm-publication'),
      packagesOnly: args.includes('--packages-only'),
      foundationProbeOnly: args.includes('--foundation-probe-only'),
      confirmFoundationProbeInitialReplacement: replacementIndex >= 0 ? args[replacementIndex + 1] : undefined,
      confirmFoundationProbeSamplerReplacement: samplerIndex >= 0 ? args[samplerIndex + 1] : undefined,
      expectedFoundationProbeSamplerDigest: expectedDigestIndex >= 0 ? args[expectedDigestIndex + 1] : undefined,
    });
    console.log('Manual artifact publication completed; see publication.json. No platform release or deployment was performed.');
  } catch (error) {
    console.error(`Publication failed: ${error.message}`);
    process.exitCode = 1;
  }
}
