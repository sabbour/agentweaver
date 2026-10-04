// Only the manually confirmed publication workflow invokes this script.
import { createHash } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateFile } from './validate.mjs';

const hash = (bytes) => createHash('sha256').update(bytes).digest('hex');
const fail = (message) => { throw new Error(`manual publication: ${message}`); };
const repositoryPathPattern = /^[a-z0-9]+(?:[._-][a-z0-9]+)*(?:\/[a-z0-9]+(?:[._-][a-z0-9]+)*)*$/;

function command(bin, args, input) {
  const result = spawnSync(bin, args, { input, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] });
  // Subprocess output can contain credentials. Never relay it, even on failure.
  if (result.error || result.status !== 0) fail(`${bin} failed; inspect the target independently before retrying`);
  return result.stdout.trim();
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
} = {}) {
  if (!confirmed || !/^[a-f0-9]{40}$/.test(sourceSha ?? '')) fail('explicit confirmation and exact source SHA are required');
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
  if (!Array.isArray(provenance.artifacts)) fail('missing artifact provenance');
  const selected = [];
  for (const component of manifest.components) {
    const kind = component.kind === 'service' ? 'image' : 'package';
    const filename = kind === 'image' ? `${component.id}.${component.version}.tar.gz`
      : `${path.basename(component.project, '.csproj')}.${component.version}.nupkg`;
    const matches = provenance.artifacts.filter((artifact) => artifact.path === filename && artifact.kind === kind && artifact.componentId === component.id);
    if (matches.length !== 1) fail(`missing unique artifact for ${component.id}`);
    const artifact = matches[0];
    const file = path.join(directory, filename);
    if (hash(readFileSync(file)) !== artifact.sha256) fail(`artifact hash mismatch for ${component.id}`);
    selected.push({ component, artifact, file });
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
    const repositoryPath = [registry.repositoryPath, component.id.toLowerCase()].filter(Boolean).join('/');
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
  const createRecord = (name, record) => {
    const tag = `${namespace}/${name}`;
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
  receipt.claimSha = createRecord('claim', { ...receipt, status: 'claimed' });
  let publicationError;
  try {
    if (hasImages) run('docker', ['login', registry.host, '--username', env.RELEASE_REGISTRY_USER, '--password-stdin'], env.RELEASE_REGISTRY_PASSWORD);
    for (const { component, artifact, file } of selected) {
      if (artifact.kind === 'package') {
        run('dotnet', ['nuget', 'push', file, '--source', env.RELEASE_NUGET_SOURCE, '--api-key', env.RELEASE_NUGET_API_KEY]);
        receipt.published.push({ id: component.id, version: component.version, kind: 'package', feed: env.RELEASE_NUGET_SOURCE, sha256: artifact.sha256 });
      } else {
        const local = `${component.id.toLowerCase()}:${component.version}`;
        const remote = `${imageRepositories.get(component.id)}:${component.version}`;
        run('docker', ['load', '--input', file]);
        run('docker', ['tag', local, remote]);
        run('docker', ['push', remote]);
        const digests = JSON.parse(run('docker', ['inspect', '--format', '{{json .RepoDigests}}', remote]));
        const digest = digests.find((value) => value.startsWith(`${imageRepositories.get(component.id)}@sha256:`));
        if (!digest || !/@sha256:[a-f0-9]{64}$/.test(digest)) fail(`registry did not return an immutable digest for ${component.id}`);
        receipt.published.push({ id: component.id, version: component.version, kind: 'image', image: digest, archiveSha256: artifact.sha256 });
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
    const [manifest, output, sourceSha, confirmation] = process.argv.slice(2);
    publishArtifacts(manifest, output, sourceSha, { confirmed: confirmation === '--confirm-publication' });
    console.log('Manual artifact publication completed; see publication.json. No platform release or deployment was performed.');
  } catch (error) {
    console.error(`Publication failed: ${error.message}`);
    process.exitCode = 1;
  }
}
