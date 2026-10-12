import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
export const approvedWebLockSha256 = 'a6e3ac6dc04086969ba78898057bda4273c3f5491185d15437143cddaef214db';
export const approvedBaseImages = Object.freeze([
  'mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317',
  'mcr.microsoft.com/dotnet/aspnet:10.0.12-noble@sha256:0fa044f682cb7d93a5a90401a00c626c66f7b00b86922be9441f869eae039f80',
  'mcr.microsoft.com/dotnet/aspnet@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4',
]);
const approvedHosts = new Set([
  'ms-feed-25.pkgs.visualstudio.com', 'ms-feed-12.pkgs.visualstudio.com',
  'ms-feed-2.pkgs.visualstudio.com', 'registry.npmjs.org',
]);

export function readApprovedBuildInputs(root) {
  const lockBytes = readFileSync(path.join(root, 'apps', 'web', 'package-lock.json'));
  if (createHash('sha256').update(lockBytes).digest('hex') !== approvedWebLockSha256) {
    throw new Error('Web lock does not match the approved release-build inputs');
  }
  const lock = JSON.parse(lockBytes);
  const packages = Object.entries(lock.packages).filter(([, item]) => item.resolved)
    .map(([name, item]) => ({ name, resolved: item.resolved, integrity: item.integrity }));
  if (lock.lockfileVersion !== 3 || packages.length !== 467) {
    throw new Error('Release-build preparation requires exactly 467 approved locked dependencies');
  }
  for (const item of packages) {
    const url = new URL(item.resolved);
    if (url.protocol !== 'https:' || !approvedHosts.has(url.hostname) ||
        url.username || url.password || url.port || url.search || url.hash ||
        !/^sha(?:1|512)-[A-Za-z0-9+/]+={0,2}$/.test(item.integrity ?? '')) {
      throw new Error(`Locked release-build dependency has invalid destination or integrity: ${item.name}`);
    }
  }
  const images = [
    'services/agenthost/Agentweaver.AgentHost/Dockerfile', 'apps/web/Dockerfile',
  ].flatMap(file => [...readFileSync(path.join(root, file), 'utf8')
    .matchAll(/^FROM(?:\s+--platform=\S+)?\s+(\S+)/gm)].map(match => match[1]));
  if (JSON.stringify(images) !== JSON.stringify(approvedBaseImages)) {
    throw new Error('Dockerfiles do not match the three approved immutable base images');
  }
  return { packages, images };
}

export function requireSharedImageStore(run) {
  if (run('docker', ['context', 'show']).trim() !== 'default') {
    throw new Error('Release input preparation requires the default local Docker context');
  }
  const info = JSON.parse(run('docker', ['info', '--format', '{{json .}}']));
  if (!/^(\d+)\./.test(info.ServerVersion) || Number(info.ServerVersion.split('.')[0]) < 27 ||
      !info.DriverStatus?.some(([key, value]) =>
        key === 'driver-type' && value === 'io.containerd.snapshotter.v1')) {
    throw new Error('Release input preparation requires Docker 27 or later with the shared containerd image store');
  }
  const buildxVersion = run('docker', ['buildx', 'version']).match(/\bv(\d+)\.(\d+)\./);
  if (!buildxVersion || (Number(buildxVersion[1]) === 0 && Number(buildxVersion[2]) < 13)) {
    throw new Error('Release input preparation requires an existing Buildx 0.13 or later');
  }
  if (run('docker', ['buildx', 'inspect', 'default', '--format', '{{.Driver}}']).trim() !== 'docker' ||
      run('docker', ['buildx', 'inspect', 'default', '--format', '{{range .Nodes}}{{.Endpoint}}{{end}}']).trim() !== 'default') {
    throw new Error('Release Buildx must use the default Docker builder and its shared image store');
  }
}

export function prepareBuildInputs({
  root = repositoryRoot, expectedSource = process.env.EXPECTED_SOURCE,
  cache = process.env.npm_config_cache, run,
} = {}) {
  run ??= (bin, args, cwd = root) => {
    if (bin === 'npm') {
      if (!process.env.npm_execpath) throw new Error('Run release input preparation through npm');
      args = [process.env.npm_execpath, ...args];
      bin = process.execPath;
    }
    return execFileSync(bin, args, {
      cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
    }).trim();
  };
  const requireSource = () => {
    if (!/^[a-f0-9]{40}$/.test(expectedSource ?? '') ||
        run('git', ['rev-parse', 'HEAD']).trim() !== expectedSource ||
        run('git', ['status', '--porcelain']).trim() !== '') {
      throw new Error('Release input preparation requires the exact approved clean source');
    }
  };
  requireSource();
  if (!cache || !path.isAbsolute(cache) ||
      !path.relative(root, cache).startsWith(`..${path.sep}`)) {
    throw new Error('Release input preparation requires an isolated npm cache outside the source checkout');
  }
  const inputs = readApprovedBuildInputs(root);
  requireSharedImageStore(run);
  for (const reference of inputs.images) {
    run('docker', ['pull', '--platform', 'linux/amd64', reference]);
    const image = JSON.parse(run('docker', ['image', 'inspect', '--format', '{{json .}}', reference]));
    const digestReference = reference.replace(/:[^/:]+(?=@)/, '');
    if (image.Os !== 'linux' || image.Architecture !== 'amd64' ||
        !/^sha256:[a-f0-9]{64}$/.test(image.Id ?? '') || !image.RepoDigests?.includes(digestReference)) {
      throw new Error(`Pulled image does not match its approved immutable reference: ${reference}`);
    }
  }
  run('npm', [
    'ci', '--ignore-scripts', '--no-audit', '--no-fund', '--no-progress',
    '--replace-registry-host=never', '--registry=https://registry.npmjs.org', '--cache', cache,
  ], path.join(root, 'apps', 'web'));
  requireSource();
  readApprovedBuildInputs(root);
  return {
    sourceSha: expectedSource, webLockSha256: approvedWebLockSha256,
    baseImages: inputs.images, approvedLockedDependencyCount: inputs.packages.length,
    npmCachePreparedForRunner: true,
    builder: 'default', driver: 'docker', imageStore: 'containerd',
  };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    console.log(JSON.stringify(prepareBuildInputs(), null, 2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
