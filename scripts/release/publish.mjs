// Only the manually confirmed publication workflow invokes this script.
import { createHash } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateFile } from './validate.mjs';

const hash = (bytes) => createHash('sha256').update(bytes).digest('hex');
const fail = (message) => { throw new Error(`manual publication: ${message}`); };

function command(bin, args, input) {
  const result = spawnSync(bin, args, { input, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] });
  // Subprocess output can contain credentials. Never relay it, even on failure.
  if (result.error || result.status !== 0) fail(`${bin} failed; inspect the target independently before retrying`);
  return result.stdout.trim();
}

export function publishArtifacts(manifestPath, outDir, sourceSha, {
  root = process.cwd(), confirmed = false, env = process.env, run = command,
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
  if (hasPackages) {
    let feed;
    try { feed = new URL(env.RELEASE_NUGET_SOURCE); } catch { fail('configure an HTTPS NuGet feed and API key'); }
    if (feed.protocol !== 'https:' || feed.username || feed.password || feed.search || !env.RELEASE_NUGET_API_KEY) {
      fail('configure a credential-free HTTPS feed URL and a separate secret API key');
    }
  }
  if (hasImages && (!/^[a-z0-9.-]+(?::[0-9]+)?$/.test(env.RELEASE_REGISTRY ?? '') ||
      !env.RELEASE_REGISTRY_USER || !env.RELEASE_REGISTRY_PASSWORD || !env.DOCKER_CONFIG)) {
    fail('configure a registry host, separate secret credentials, and an isolated Docker configuration directory');
  }
  const receipt = { schemaVersion: 1, sourceSha, provenanceSha256: hash(readFileSync(path.join(directory, 'provenance.json'))), status: 'partial', published: [] };
  let publicationError;
  try {
    if (hasImages) run('docker', ['login', env.RELEASE_REGISTRY, '--username', env.RELEASE_REGISTRY_USER, '--password-stdin'], env.RELEASE_REGISTRY_PASSWORD);
    for (const { component, artifact, file } of selected) {
      if (artifact.kind === 'package') {
        run('dotnet', ['nuget', 'push', file, '--source', env.RELEASE_NUGET_SOURCE, '--api-key', env.RELEASE_NUGET_API_KEY]);
        receipt.published.push({ id: component.id, version: component.version, kind: 'package', feed: env.RELEASE_NUGET_SOURCE, sha256: artifact.sha256 });
      } else {
        const local = `${component.id.toLowerCase()}:${component.version}`;
        const remote = `${env.RELEASE_REGISTRY}/${local}`;
        run('docker', ['load', '--input', file]);
        run('docker', ['tag', local, remote]);
        run('docker', ['push', remote]);
        const digests = JSON.parse(run('docker', ['inspect', '--format', '{{json .RepoDigests}}', remote]));
        const digest = digests.find((value) => value.startsWith(`${env.RELEASE_REGISTRY}/${component.id.toLowerCase()}@sha256:`));
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
    try {
      writeFileSync(path.join(directory, 'publication.json'), JSON.stringify(receipt, null, 2) + '\n');
    } catch (receiptError) {
      if (publicationError) throw new AggregateError([publicationError, receiptError],
        `publication failed (${publicationError.message}); receipt write also failed: ${receiptError.message}; inspect remote state before retrying`);
      throw receiptError;
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
