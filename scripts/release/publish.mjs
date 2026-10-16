// Only the manually confirmed publication workflow invokes this script.
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { chmodSync, existsSync, lstatSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  validateFile,
  WEB_HOST_LOCK_PATH,
  WEB_HOST_PROJECT_PATH,
  WEB_LOCK_PATH,
  WEB_PROJECT_PATH,
} from './validate.mjs';
import { componentImageRepository, selectIntegrationImageComponents } from './pack.mjs';
import { resolveProbeImageSource } from '../azure/build-foundation-probe-image.mjs';
import { runPublicationCommand as command } from './command.mjs';
import {
  AGENTHOST_ID, validateAgentHostArtifact, verifyAgentHostArchives, verifyLoadedAgentHostImage,
} from './agenthost-image.mjs';
import { measureAgentHostArchiveSync } from './measure-agenthost-image.mjs';
import { readDockerArchiveConfig } from './docker-archive.mjs';
import { createNugetConfig } from './nuget-config.mjs';
import { continuePartialPublication } from './partial-publication.mjs';
import { PARTIAL_PUBLICATION } from './partial-publication-contract.mjs';

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
const nugetSourceName = 'agentweaver-publication';

function validBuildOutput(output) {
  return Array.isArray(output?.files) && output.files.length > 0 &&
    output.files.every((file) => file && typeof file === 'object' && typeof file.path === 'string' &&
      contentHashPattern.test(file.sha256 ?? '') &&
      Number.isSafeInteger(file.size) && file.size >= 0) &&
    contentHashPattern.test(output.sha256 ?? '') &&
    hash(JSON.stringify(output.files)) === output.sha256;
}

function validateWebArtifact(component, artifact, componentProvenance, sourceSha, root, git, integrationSourceTree) {
  const reject = () => fail('prepared web image provenance does not match the exact source, version, or linux/amd64 artifact');
  const repository = componentImageRepository(component.id);
  const tag = integrationSourceTree ? sourceSha : `${component.version.replaceAll('+', '_')}-${sourceSha}`;
  const imageReference = `${repository}:${tag}`;
  const integrationLabels = {
    'org.opencontainers.image.revision': sourceSha,
    'io.agentweaver.source-tree': integrationSourceTree,
    'org.opencontainers.image.version': component.version,
  };
  const source = componentProvenance?.source;
  const build = componentProvenance?.build;
  const sourceFiles = source?.files;
  if (component.id !== 'Agentweaver.Web' || component.kind !== 'service' ||
      component.project !== WEB_PROJECT_PATH || artifact.repository !== repository ||
      artifact.tag !== tag || artifact.imageReference !== imageReference ||
      artifact.platform !== 'linux/amd64' || !imageIdPattern.test(artifact.imageId ?? '') ||
      artifact.labels?.['org.opencontainers.image.revision'] !== sourceSha ||
      (integrationSourceTree &&
        (artifact.labels?.['io.agentweaver.source-tree'] !== integrationSourceTree ||
          JSON.stringify(artifact.integrationLabels) !== JSON.stringify(integrationLabels))) ||
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
      source.hostProjectSha256 !== sourceByPath.get(WEB_HOST_PROJECT_PATH) ||
      source.hostLockSha256 !== sourceByPath.get(WEB_HOST_LOCK_PATH) ||
      source.dockerfileSha256 !== sourceByPath.get('apps/web/Dockerfile') ||
      componentProvenance?.lock?.path !== WEB_LOCK_PATH ||
      componentProvenance.lock.sha256 !== sourceByPath.get(WEB_LOCK_PATH)) {
    reject();
  }

  const baseImages = artifact.baseImages;
  const output = artifact.buildOutput;
  const dotnetBuild = build?.dotnet;
  const hostOutput = dotnetBuild?.output;
  const packagedOutputFiles = new Map((Array.isArray(output?.files) ? output.files : [])
    .filter((file) => file && typeof file === 'object' && typeof file.path === 'string')
    .map((file) => [file.path, file]));
  const hostOutputMatches = validBuildOutput(hostOutput) && hostOutput.files.every((file) => {
    const packaged = packagedOutputFiles.get(`publish/${file.path}`);
    return packaged?.sha256 === file.sha256 && packaged.size === file.size;
  });
  if (!Array.isArray(baseImages) || baseImages.length === 0 ||
      baseImages.some((image) => typeof image.reference !== 'string' ||
        !/@sha256:[a-f0-9]{64}$/.test(image.reference) ||
        image.repositoryDigest !== image.reference ||
        !imageIdPattern.test(image.imageId ?? '')) ||
      !validBuildOutput(output) || !validBuildOutput(hostOutput) ||
      !hostOutputMatches ||
      !output.files.some((file) => file.path === 'wwwroot/index.html') ||
      !hostOutput.files.some((file) => file.path === 'Agentweaver.Web.Host.dll') ||
      build?.tool !== 'npm+dotnet' || build.dockerfile !== 'apps/web/Dockerfile' ||
      dotnetBuild?.project !== WEB_HOST_PROJECT_PATH ||
      dotnetBuild?.lock?.path !== WEB_HOST_LOCK_PATH ||
      dotnetBuild?.lock?.sha256 !== sourceByPath.get(WEB_HOST_LOCK_PATH) ||
      dotnetBuild?.restoreMode !== 'locked' ||
      dotnetBuild?.publishConfiguration !== 'Release' ||
      dotnetBuild?.useAppHost !== false ||
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
  measureAgentHostArchive = measureAgentHostArchiveSync,
  writeFileSync: writeReceipt = writeFileSync,
  packagesOnly = false,
  foundationProbeOnly = false,
  platformOnly = false,
  integrationImagesOnly = false,
  integrationConfirmed = false,
  partialContinuation = false,
  originalReceiptPath,
  confirmFoundationProbeInitialReplacement,
  confirmFoundationProbeSamplerReplacement,
  expectedFoundationProbeSamplerDigest,
} = {}) {
  root = path.resolve(root);
  if (!/^[a-f0-9]{40}$/.test(sourceSha ?? '') ||
      (partialContinuation
        ? (sourceSha !== PARTIAL_PUBLICATION.sourceSha || !platformOnly || confirmed || integrationConfirmed ||
          !originalReceiptPath)
        : (integrationImagesOnly ? (!integrationConfirmed || confirmed) : (!confirmed || integrationConfirmed)))) {
    fail('explicit confirmation for the selected publication mode and an exact source SHA are required');
  }
  if (partialContinuation && (packagesOnly || foundationProbeOnly || integrationImagesOnly ||
      confirmFoundationProbeInitialReplacement !== undefined ||
      confirmFoundationProbeSamplerReplacement !== undefined ||
      expectedFoundationProbeSamplerDigest !== undefined)) {
    fail('partial continuation cannot combine with another publication mode or Probe replacement');
  }
  if (integrationImagesOnly && (packagesOnly || foundationProbeOnly || platformOnly ||
      confirmFoundationProbeInitialReplacement !== undefined ||
      confirmFoundationProbeSamplerReplacement !== undefined ||
      expectedFoundationProbeSamplerDigest !== undefined)) {
    fail('integration image-only publication cannot combine with another selection or Probe replacement');
  }
  if (packagesOnly && foundationProbeOnly) fail('package-only and Foundation Probe-only publication are mutually exclusive');
  if (platformOnly && (packagesOnly || foundationProbeOnly))
    fail('platform-only publication cannot combine with package-only or Foundation Probe-only publication');
  const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  const headSha = git('rev-parse', 'HEAD');
  if ((!partialContinuation && headSha !== sourceSha) ||
      (partialContinuation && (headSha !== env.GITHUB_SHA || env.GITHUB_REF !== 'refs/heads/v1' ||
        env.GITHUB_EVENT_NAME !== 'workflow_dispatch' || env.GITHUB_RUN_ATTEMPT !== '1' ||
        env.GITHUB_REPOSITORY !== 'sabbour/agentweaver' ||
        env.EXPECTED_SOURCE !== PARTIAL_PUBLICATION.sourceSha)) ||
      git('status', '--porcelain')) {
    fail('publication requires a clean exact source HEAD and approved dispatch context');
  }
  const manifest = validateFile(path.resolve(root, manifestPath), { root });
  const sourceManifest = JSON.parse(execFileSync('git', ['show', `${sourceSha}:${path.relative(root, path.resolve(root, manifestPath)).replaceAll('\\', '/')}`],
    { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }));
  if (JSON.stringify(sourceManifest) !== JSON.stringify(manifest)) fail('manifest differs from source');
  if (integrationImagesOnly && (manifest.stage !== 'draft' || manifest.evidence !== undefined)) {
    fail('integration image-only publication requires a tracked draft manifest with no deployment evidence');
  }
  if (integrationImagesOnly && git('rev-parse', 'HEAD^{tree}') !== git('rev-parse', `${sourceSha}^{tree}`)) {
    fail('integration source tree must match the exact source SHA');
  }
  const integrationSourceTree = integrationImagesOnly ? git('rev-parse', `${sourceSha}^{tree}`) : undefined;
  if (integrationImagesOnly && !/^[a-f0-9]{40}$/.test(integrationSourceTree ?? '')) {
    fail('integration source tree must be an exact 40-character Git tree SHA');
  }
  const directory = path.resolve(root, outDir);
  const receiptName = integrationImagesOnly ? 'integration-publication.json' : 'publication.json';
  const receiptPath = path.join(directory, receiptName);
  if (existsSync(receiptPath)) fail(`receipt already exists at ${receiptName}; inspect prior or partial publication before any retry`);
  const provenance = JSON.parse(readFileSync(path.join(directory, 'provenance.json'), 'utf8'));
  if (provenance.schemaVersion !== 1 || provenance.sourceSha !== sourceSha ||
      provenance.manifestSha256 !== hash(JSON.stringify(manifest))) fail('provenance does not match exact source composition');
  if (integrationImagesOnly &&
      (JSON.stringify(provenance.integration) !== JSON.stringify({
        mode: 'images-only', sourceTree: integrationSourceTree, imageTag: sourceSha,
      }))) {
    fail('provenance does not match the exact integration image-only source and tree');
  }
  const components = integrationImagesOnly
    ? selectIntegrationImageComponents(manifest)
    : platformOnly
    ? manifest.components.filter(component => component.id !== 'Agentweaver.FoundationProbe')
    : foundationProbeOnly
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
    fail(`provenance component selection does not match the ${integrationImagesOnly ? 'integration images-only' : platformOnly ? 'platform-only' : foundationProbeOnly ? 'Foundation Probe-only' : packagesOnly ? 'package-only' : 'full'} manifest selection`);
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
    const artifactFilename = component.project === WEB_PROJECT_PATH || component.id === AGENTHOST_ID
      ? `${component.id}.${component.version}.tar`
      : filename;
    const matches = provenance.artifacts.filter((artifact) =>
      artifact.path === artifactFilename && artifact.kind === kind && artifact.componentId === component.id);
    if (matches.length !== 1) fail(`missing unique artifact for ${component.id}`);
    const artifact = matches[0];
    if (integrationImagesOnly) {
      const labels = {
        'org.opencontainers.image.revision': sourceSha,
        'io.agentweaver.source-tree': integrationSourceTree,
        'org.opencontainers.image.version': component.version,
      };
      if (kind !== 'image' || artifact.tag !== sourceSha ||
          artifact.imageReference !== `${componentImageRepository(component.id)}:${sourceSha}` ||
          JSON.stringify(artifact.integrationLabels) !== JSON.stringify(labels)) {
        fail(`prepared integration image metadata does not match the exact source for ${component.id}`);
      }
    }
    const file = path.join(directory, artifactFilename);
    if (component.id !== AGENTHOST_ID && hash(readFileSync(file)) !== artifact.sha256)
      fail(`artifact hash mismatch for ${component.id}`);
    const componentProvenance = provenance.components[index];
    const integrationLabels = integrationImagesOnly ? {
      'org.opencontainers.image.revision': sourceSha,
      'io.agentweaver.source-tree': integrationSourceTree,
      'org.opencontainers.image.version': component.version,
    } : undefined;
    let preparedImageConfig;
    if (integrationImagesOnly) {
      try {
        preparedImageConfig = readDockerArchiveConfig(file, artifact.imageReference);
      } catch (error) {
        fail(`prepared integration image archive for ${component.id} cannot be verified: ${error.message}`);
      }
      const labels = preparedImageConfig.config?.config?.Labels;
      if (preparedImageConfig.configDigest !== artifact.imageConfigDigest ||
          preparedImageConfig.config?.os !== 'linux' ||
          preparedImageConfig.config?.architecture !== 'amd64' ||
          Object.entries(integrationLabels).some(([name, value]) => labels?.[name] !== value) ||
          JSON.stringify(artifact.labels) !== JSON.stringify(integrationLabels)) {
        fail(`prepared integration image config does not match the source, tree, version, and preparation receipt for ${component.id}`);
      }
    }
    const localImageReference = component.project === WEB_PROJECT_PATH
      ? validateWebArtifact(component, artifact, componentProvenance, sourceSha, root, git, integrationSourceTree)
      : undefined;
    const agentHost = component.id === AGENTHOST_ID
      ? validateAgentHostArtifact(component, artifact, componentProvenance, root, directory, sourceSha, git,
        measureAgentHostArchive, integrationImagesOnly)
      : undefined;
    selected.push({ component, artifact, file, preparedImageConfig,
      localImageReference: integrationImagesOnly
        ? agentHost?.imageReference ?? localImageReference ?? artifact.imageReference
        : agentHost?.imageReference ?? localImageReference,
      agentHost });
  }
  const hasPackages = selected.some(({ artifact }) => artifact.kind === 'package');
  const hasImages = selected.some(({ artifact }) => artifact.kind === 'image');
  let nugetUsesCredentials = false;
  if (integrationImagesOnly && (hasPackages || selected.length !== 10)) {
    fail('integration image-only publication requires exactly ten product service images and no packages');
  }
  let registry;
  if (hasPackages) {
    let feed;
    try { feed = new URL(env.RELEASE_NUGET_SOURCE); } catch { fail('configure an HTTPS NuGet feed and API key'); }
    if (feed.protocol !== 'https:' || feed.username || feed.password || feed.search || feed.hash || !env.RELEASE_NUGET_API_KEY) {
      fail('configure a credential-free HTTPS feed URL and a separate secret API key');
    }
    if (feed.hostname === 'nuget.pkg.github.com') {
      const validNugetUsername = /^(?=.{1,39}$)[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*$/.test(
        env.RELEASE_NUGET_USERNAME ?? '',
      );
      if (!validNugetUsername) {
        fail('GitHub Packages publication requires a valid repository-owner username');
      }
      nugetUsesCredentials = true;
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
  if (partialContinuation) {
    const receiptPath = path.resolve(root, originalReceiptPath);
    const originalReceiptBytes = readFileSync(receiptPath);
    let originalReceipt;
    try {
      originalReceipt = JSON.parse(originalReceiptBytes.toString('utf8'));
    } catch (error) {
      fail(`the retained original publication receipt is invalid: ${error.message}`);
    }
    return continuePartialPublication({
      sourceSha,
      helperSha: headSha,
      selected,
      imageRepositories,
      env,
      run,
      directory,
      originalReceiptPath: receiptPath,
      originalReceiptBytes,
      originalReceipt,
      runId: env.GITHUB_RUN_ID,
      runAttempt: env.GITHUB_RUN_ATTEMPT,
    });
  }
  const namespace = integrationImagesOnly
    ? `agentweaver-integration/${sourceSha}`
    : `agentweaver-publication/${sourceSha}`;
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
      : `${imageRepositories.get(component.id)}:${integrationImagesOnly ? sourceSha : component.version}`,
  }));
  const receipt = {
    schemaVersion: 1, sourceSha,
    ...(integrationImagesOnly ? { mode: 'images-only', sourceTree: integrationSourceTree } : {}),
    provenanceSha256: hash(readFileSync(path.join(directory, 'provenance.json'))),
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
  let nugetConfigDirectory;
  let nugetConfigFile;
  let publicationError;
  try {
    if (nugetUsesCredentials) {
      nugetConfigDirectory = mkdtempSync(path.join(tmpdir(), 'agentweaver-nuget-'));
      chmodSync(nugetConfigDirectory, 0o700);
      nugetConfigFile = path.join(nugetConfigDirectory, 'NuGet.Config');
      writeFileSync(nugetConfigFile, createNugetConfig(
        env.RELEASE_NUGET_SOURCE, env.RELEASE_NUGET_USERNAME, env.RELEASE_NUGET_API_KEY,
      ), {
        encoding: 'utf8', flag: 'wx', mode: 0o600,
      });
    }
    for (const { component, artifact, file, localImageReference, agentHost, preparedImageConfig } of selected) {
      if (localImageReference === undefined) continue;
      if (agentHost) verifyAgentHostArchives(agentHost.archives, directory);
      run('docker', ['load', '--input', file]);
      if (agentHost) {
        agentHost.loadedImageId = verifyLoadedAgentHostImage(localImageReference, agentHost.measured, agentHost.source,
          component.version, args => run('docker', args)).loadedImageId;
        verifyAgentHostArchives(agentHost.archives, directory);
        continue;
      }
      let image;
      try {
        image = JSON.parse(run('docker', ['image', 'inspect', '--format', '{{json .}}', localImageReference]));
      } catch (error) {
        fail(`loaded ${component.id} image cannot be inspected: ${error.message}`);
      }
      const labels = image?.Config?.Labels;
      const expectedLabels = integrationImagesOnly ? {
        'org.opencontainers.image.revision': sourceSha,
        'io.agentweaver.source-tree': integrationSourceTree,
        'org.opencontainers.image.version': component.version,
      } : {
        'org.opencontainers.image.revision': sourceSha,
        'org.opencontainers.image.version': component.version,
      };
      if ((artifact.imageId !== undefined && image?.Id !== artifact.imageId) ||
          image?.Os !== 'linux' || image?.Architecture !== 'amd64' ||
          Object.entries(expectedLabels).some(([name, value]) => labels?.[name] !== value) ||
          (integrationImagesOnly &&
            (preparedImageConfig?.configDigest !== artifact.imageConfigDigest ||
              Object.entries(expectedLabels).some(([name, value]) =>
                preparedImageConfig?.config?.config?.Labels?.[name] !== value)))) {
        fail(`loaded ${component.id} image does not match its source, tree, version, identity, or linux/amd64 provenance`);
      }
    }
    if (hasImages) run('docker', ['login', registry.host, '--username', env.RELEASE_REGISTRY_USER, '--password-stdin'], env.RELEASE_REGISTRY_PASSWORD);
    for (const { component, artifact, file, localImageReference, agentHost, preparedImageConfig } of selected) {
      if (artifact.kind === 'package') {
        const args = ['nuget', 'push', file, '--source',
          nugetUsesCredentials ? nugetSourceName : env.RELEASE_NUGET_SOURCE,
          '--api-key', env.RELEASE_NUGET_API_KEY];
        if (nugetConfigFile) args.push('--configfile', nugetConfigFile);
        run('dotnet', args);
        receipt.published.push({ id: component.id, version: component.version, kind: 'package', feed: env.RELEASE_NUGET_SOURCE, sha256: artifact.sha256 });
      } else {
        if (agentHost && !/^sha256:[a-f0-9]{64}$/.test(agentHost.loadedImageId ?? ''))
          fail('AgentHost has no verified loaded image identity to tag');
        const local = agentHost ? agentHost.loadedImageId
          : localImageReference ?? `${componentImageRepository(component.id)}:${component.version}`;
        const imageTag = integrationImagesOnly ? sourceSha : component.version;
        const remote = `${imageRepositories.get(component.id)}:${imageTag}`;
        if (localImageReference === undefined) run('docker', ['load', '--input', file]);
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
          receipt.published.push({
            id: component.id, version: component.version, kind: 'image', image: digest, archiveSha256: artifact.sha256,
            ...(integrationImagesOnly ? {
              sourceSha, sourceTree: integrationSourceTree, tag: sourceSha,
              configDigest: agentHost?.measured.image.configDigest ?? preparedImageConfig?.configDigest,
            } : {}),
            ...(agentHost ? {
              ociArchiveSha256: artifact.ociArchive.sha256,
              imageConfigDigest: agentHost.measured.image.configDigest,
              preparedOciMeasurement: agentHost.measured,
            } : {}),
          });
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
    if (nugetConfigDirectory) {
      try {
        rmSync(nugetConfigDirectory, { recursive: true, force: true });
      } catch (error) {
        failures.push(new Error('temporary NuGet credentials cleanup failed', { cause: error }));
      }
    }
    try {
      createRecord('result', receipt);
      if (replaceProbe) createRecord('result', receipt, replacementClaim);
    } catch (receiptError) {
      failures.push(receiptError);
    }
    try {
      writeReceipt(receiptPath, JSON.stringify(receipt, null, 2) + '\n',
        integrationImagesOnly ? { flag: 'wx' } : undefined);
    } catch (receiptError) {
      failures.push(receiptError);
    }
    if (failures.length) {
      throw new AggregateError([...(publicationError ? [publicationError] : []), ...failures],
        `publication finalization failed: ${failures.map(({ message }) => message).join('; ')}; permanent claim blocks retry; inspect remote state`,
        { cause: publicationError ?? failures[0] });
    }
  }
}

export function findNugetPushDiagnostic(error) {
  if (!error || typeof error !== 'object') return undefined;
  if (error.operation === 'nuget.push' && typeof error.nativeDiagnostic === 'string')
    return error.nativeDiagnostic;
  if (error instanceof AggregateError) {
    for (const nested of error.errors) {
      const diagnostic = findNugetPushDiagnostic(nested);
      if (diagnostic !== undefined) return diagnostic;
    }
  }
  return undefined;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  void (async () => {
    try {
      const args = process.argv.slice(2);
      const [manifest, output, sourceSha] = args;
      const replacementIndex = args.indexOf('--confirm-foundation-probe-initial-replacement');
      const samplerIndex = args.indexOf('--confirm-foundation-probe-sampler-replacement');
      const expectedDigestIndex = args.indexOf('--expected-foundation-probe-sampler-digest');
      const originalReceiptIndex = args.indexOf('--original-receipt');
      const partialContinuation = args.includes('--continue-partial-publication');
      if (replacementIndex >= 0 && !/^[a-f0-9]{40}$/.test(args[replacementIndex + 1] ?? '')) {
        fail('the one-time initial Probe replacement requires its exact source SHA confirmation');
      }
      if (samplerIndex >= 0 && !/^[a-f0-9]{40}$/.test(args[samplerIndex + 1] ?? '')) {
        fail('the one-time sampler Probe replacement requires its exact source SHA confirmation');
      }
      if (expectedDigestIndex >= 0 && args[expectedDigestIndex + 1] !== samplerProbeDigest) {
        fail('sampler Probe replacement requires the exact approved current digest');
      }
      if ((partialContinuation && (originalReceiptIndex < 0 || !args[originalReceiptIndex + 1])) ||
          (!partialContinuation && originalReceiptIndex >= 0)) {
        fail('--original-receipt is required only for partial-continuation mode');
      }
      await publishArtifacts(manifest, output, sourceSha, {
        confirmed: args.includes('--confirm-publication'),
        packagesOnly: args.includes('--packages-only'),
        foundationProbeOnly: args.includes('--foundation-probe-only'),
        platformOnly: args.includes('--platform-only'),
        integrationImagesOnly: args.includes('--integration-images-only'),
        integrationConfirmed: args.includes('--confirm-integration-publication'),
        partialContinuation,
        originalReceiptPath: originalReceiptIndex >= 0 ? args[originalReceiptIndex + 1] : undefined,
        confirmFoundationProbeInitialReplacement: replacementIndex >= 0 ? args[replacementIndex + 1] : undefined,
        confirmFoundationProbeSamplerReplacement: samplerIndex >= 0 ? args[samplerIndex + 1] : undefined,
        expectedFoundationProbeSamplerDigest: expectedDigestIndex >= 0 ? args[expectedDigestIndex + 1] : undefined,
      });
      console.log(partialContinuation
        ? 'The one-use partial publication continuation finished; inspect continuation.json and publication.json.'
        : args.includes('--integration-images-only')
          ? 'Manual integration image publication completed; see integration-publication.json. No package, release, or deployment was performed.'
          : 'Manual artifact publication completed; see publication.json. No platform release or deployment was performed.');
    } catch (error) {
      console.error(`Publication failed: ${error.message}`);
      const nativeDiagnostic = findNugetPushDiagnostic(error);
      if (nativeDiagnostic) {
        const diagnostic = nativeDiagnostic.replace(/%/g, '%25').replace(/\r/g, '%0D').replace(/\n/g, '%0A');
        console.error(`::error title=NuGet push response::${diagnostic}`);
      }
      process.exitCode = 1;
    }
  })();
}
