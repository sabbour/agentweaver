#!/usr/bin/env node
import { randomUUID } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { IMAGE_REPOSITORIES } from './image-repositories.mjs';
import { measureAgentHostRegistryImage, parseRegistryTarget, verifyServiceRegistryImage } from './registry.mjs';
import { run, runAz } from '../azure/lib/exec.mjs';

const SHA = /^[a-f0-9]{40}$/;
const SOURCE = 'https://github.com/sabbour/agentweaver';
const COMPONENTS = [
  { id: 'Agentweaver.AgentHost', repository: 'agentweaver-agenthost', mode: 'docker',
    dockerfile: path.join('services', 'agenthost', 'Agentweaver.AgentHost', 'Dockerfile') },
  { id: 'Agentweaver.Identity.Broker', repository: 'agentweaver.identity.broker', mode: 'docker',
    dockerfile: path.join('services', 'identity', 'Agentweaver.Identity.Broker', 'Dockerfile') },
  { id: 'Agentweaver.EventsAndSessions', repository: 'agentweaver.eventsandsessions', mode: 'dotnet',
    project: path.join('services', 'events-and-sessions', 'Agentweaver.EventsAndSessions', 'Agentweaver.EventsAndSessions.csproj') },
  { id: 'Agentweaver.Gateway', repository: 'agentweaver.gateway', mode: 'dotnet',
    project: path.join('services', 'gateway', 'Agentweaver.Gateway', 'Agentweaver.Gateway.csproj') },
  { id: 'Agentweaver.Mcp', repository: 'agentweaver.mcp', mode: 'dotnet',
    project: path.join('services', 'mcp', 'Agentweaver.Mcp', 'Agentweaver.Mcp.csproj') },
  { id: 'Agentweaver.Web', repository: 'agentweaver-web', mode: 'web',
    project: path.join('apps', 'web', 'host', 'Agentweaver.Web.Host.csproj') },
  { id: 'Agentweaver.Orchestrator', repository: 'agentweaver.orchestrator', mode: 'docker',
    dockerfile: path.join('services', 'orchestrator', 'Agentweaver.Orchestrator', 'Dockerfile'),
    project: path.join('services', 'orchestrator', 'Agentweaver.Orchestrator', 'Agentweaver.Orchestrator.csproj') },
  { id: 'Agentweaver.Environment', repository: 'agentweaver.environment', mode: 'dotnet',
    project: path.join('services', 'environment', 'Agentweaver.Environment', 'Agentweaver.Environment.csproj') },
  { id: 'Agentweaver.Knowledge', repository: 'agentweaver.knowledge', mode: 'dotnet',
    project: path.join('services', 'knowledge', 'Agentweaver.Knowledge', 'Agentweaver.Knowledge.csproj') },
  { id: 'Agentweaver.Projects.Config', repository: 'agentweaver.projects.config', mode: 'dotnet',
    project: path.join('services', 'projects-config', 'Agentweaver.Projects.Config', 'Agentweaver.Projects.Config.csproj') },
];
const OPA_URL = 'https://openpolicyagent.org/downloads/v0.70.0/opa_linux_amd64_static';

export const BUILD_ACR_CANDIDATE_USAGE =
  'Usage: npm run azure:build-acr-candidate -- <full-lowercase-40-character-commit-sha> <name>.azurecr.io';

function fail(message) {
  throw new Error(`ACR candidate build: ${message}`);
}

export function parseBuildAcrCandidateArgs(argv) {
  if (argv.length !== 2 || !SHA.test(argv[0] ?? '')) fail(BUILD_ACR_CANDIDATE_USAGE);
  let target;
  try {
    target = parseRegistryTarget(argv[1]);
  } catch {
    fail('registry must be an Azure Container Registry login server.');
  }
  if (target.kind !== 'acr') fail('registry must be an Azure Container Registry login server.');
  return { sourceSha: argv[0], registry: target.registry, registryName: target.name };
}

function command(exec, name, args, cwd, action) {
  const result = exec(name, args, { cwd, check: false });
  if (result.status !== 0) {
    fail(`${action}: ${(result.stderr || result.stdout || `exit ${result.status}`).trim()}`);
  }
  return result;
}

function sourceVersion(manifest, id) {
  const matches = manifest.components.filter(component => component.id === id);
  if (matches.length !== 1 || typeof matches[0].version !== 'string' || !matches[0].version) {
    fail(`release manifest must contain exactly one version for ${id}.`);
  }
  return matches[0].version;
}

function buildxArgs({ registry, sourceSha, sourceTree, version, component, root, webVersion }) {
  const image = `${registry}/${component.repository}:sha-${sourceSha}`;
  const args = ['buildx', 'build', '--push', '--provenance=false', '--platform', 'linux/amd64', '--tag', image,
    '--label', `org.opencontainers.image.source=${SOURCE}`,
    '--label', `org.opencontainers.image.revision=${sourceSha}`,
    '--label', `io.agentweaver.source-tree=${sourceTree}`,
    '--label', `org.opencontainers.image.version=${version}`];
  if (component.mode === 'web') {
    args.push('--build-arg', `IMAGE_TAG=${webVersion}`, '--build-arg', `GIT_SHA=${sourceSha}`,
      '--file', path.join(root, 'apps', 'web', 'Dockerfile'), path.join(root, 'apps', 'web'));
  } else {
    args.push('--file', path.join(root, component.dockerfile), root);
  }
  return { image, args };
}

function dotnetArgs({ registry, sourceSha, sourceTree, version, component }) {
  return [
    'publish', component.project, '-t:PublishContainer', '--configuration', 'Release', '--no-restore',
    '-p:EnableSdkContainerSupport=true',
    '-p:ContainerTargetOs=linux',
    '-p:ContainerTargetArchitecture=x64',
    `-p:ContainerRegistry=${registry}`,
    `-p:ContainerRepository=${component.repository}`,
    `-p:ContainerImageTag=sha-${sourceSha}`,
    `-p:RepositoryCommit=${sourceSha}`,
    `-p:AgentweaverImageSourceSha=${sourceSha}`,
    `-p:AgentweaverImageSourceTree=${sourceTree}`,
    `-p:AgentweaverImageVersion=${version}`,
  ];
}

export async function buildAcrCandidate(
  { sourceSha, registry }, overrides = {},
) {
  if (!SHA.test(sourceSha ?? '')) fail('source SHA must be a lowercase full 40-character commit SHA.');
  let target;
  try {
    target = parseRegistryTarget(registry);
  } catch {
    fail('registry must be an Azure Container Registry login server.');
  }
  if (target.kind !== 'acr') fail('registry must be an Azure Container Registry login server.');
  const root = path.resolve(overrides.root ?? process.cwd());
  const exec = overrides.exec ?? run;
  const runAzure = overrides.runAzure ?? runAz;
  const verifyImage = overrides.verifyImage ?? verifyServiceRegistryImage;
  const measureAgentHost = overrides.measureAgentHost ?? measureAgentHostRegistryImage;
  const fetchImpl = overrides.fetchImpl ?? globalThis.fetch;
  const worktreePath = overrides.worktreePath ??
    path.join(path.dirname(root), `.agentweaver-acr-candidate-${randomUUID()}`);
  if (existsSync(worktreePath)) fail(`temporary worktree path already exists: ${worktreePath}`);

  command(exec, 'git', [
    'fetch', '--no-tags', 'origin', '+refs/heads/v1:refs/remotes/origin/v1',
  ], root, 'Could not fetch origin/v1');
  const ancestry = exec('git', [
    'merge-base', '--is-ancestor', sourceSha, 'refs/remotes/origin/v1',
  ], { cwd: root, check: false });
  if (ancestry.status !== 0) fail('selected commit must be an ancestor of the current origin/v1.');
  const treeResult = command(exec, 'git', ['rev-parse', '--verify', `${sourceSha}^{tree}`],
    root, 'Could not read the selected source tree');
  const sourceTree = treeResult.stdout.trim();
  if (!SHA.test(sourceTree)) fail('Git returned an invalid source tree SHA.');

  let worktreeAdded = false;
  let worktreeAttempted = false;
  let failure;
  let report;
  try {
    worktreeAttempted = true;
    command(exec, 'git', ['worktree', 'add', '--detach', worktreePath, sourceSha],
      root, `Could not create the detached worktree at ${sourceSha}`);
    worktreeAdded = true;
    const checkoutSha = command(exec, 'git', ['rev-parse', '--verify', 'HEAD'],
      worktreePath, 'Could not verify the detached source commit').stdout.trim();
    if (checkoutSha !== sourceSha) fail('detached worktree does not match the selected source commit.');
    const checkoutTree = command(exec, 'git', ['rev-parse', '--verify', 'HEAD^{tree}'],
      worktreePath, 'Could not verify the detached source tree').stdout.trim();
    if (checkoutTree !== sourceTree) fail('detached worktree does not match the selected source tree.');
    const manifest = JSON.parse(readFileSync(path.join(worktreePath, 'releases', 'foundation.json'), 'utf8'));
    const hasAcsRuntime = existsSync(path.join(
      worktreePath, 'eng', 'acs', 'nuget', 'AgentControlSpecification.0.4.0-beta.0.nupkg',
    ));
    const components = COMPONENTS.map(component =>
      component.id === 'Agentweaver.Orchestrator' && !hasAcsRuntime
        ? { ...component, mode: 'dotnet' }
        : component);
    const repositories = COMPONENTS.map(component => component.repository);
    if (repositories.length !== 10 || new Set(repositories).size !== 10 ||
        repositories.some(repository => !IMAGE_REPOSITORIES.includes(repository)) ||
        IMAGE_REPOSITORIES.some(repository => !repositories.includes(repository))) {
      fail('candidate build matrix does not match the ten v1 image repositories.');
    }

    runAzure(['acr', 'login', '--name', target.name], { cwd: worktreePath });
    command(exec, 'docker', ['buildx', 'inspect', '--bootstrap'], worktreePath,
      'Could not initialize Docker Buildx');
    if (hasAcsRuntime) {
      command(exec, 'curl', [
        '--fail', '--location', '--proto', '=https', '--tlsv1.2', '--retry', '5',
        '--retry-all-errors', '--retry-delay', '5', '--connect-timeout', '20',
        OPA_URL, '--output', path.join(worktreePath, 'eng', 'acs', 'opa_linux_amd64_static'),
      ], worktreePath, 'Could not download pinned OPA');
    }

    let webVersion;
    for (const component of components) {
      const version = sourceVersion(manifest, component.id);
      if (component.mode === 'docker') continue;
      command(exec, 'dotnet', ['restore', component.project, '--locked-mode'], worktreePath,
        `Could not restore ${component.id}`);
      if (component.mode === 'web') {
        const webRoot = path.join(worktreePath, 'apps', 'web');
        command(exec, 'npm', ['ci', '--legacy-peer-deps'], webRoot, 'Could not install web dependencies');
        command(exec, 'npm', ['run', 'build'], webRoot, 'Could not build web assets');
        command(exec, 'dotnet', [
          'publish', component.project, '--configuration', 'Release', '--no-restore',
          '--output', path.join(worktreePath, 'apps', 'web', 'publish'),
        ], worktreePath, 'Could not publish the Web host');
        webVersion = JSON.parse(readFileSync(path.join(webRoot, 'package.json'), 'utf8')).version;
        if (typeof webVersion !== 'string' || !webVersion) fail('Web package has no version.');
      }
    }

    const images = [];
    for (const component of components) {
      const version = sourceVersion(manifest, component.id);
      if (component.mode === 'dotnet') {
        command(exec, 'dotnet', dotnetArgs({ registry, sourceSha, sourceTree, version, component }),
          worktreePath, `Could not build and push ${component.id}`);
      } else {
        const build = buildxArgs({
          registry, sourceSha, sourceTree, version, component, root: worktreePath, webVersion,
        });
        command(exec, 'docker', build.args, worktreePath, `Could not build and push ${component.id}`);
      }
      const image = `${registry}/${component.repository}:sha-${sourceSha}`;
      const inspected = command(exec, 'docker', [
        'buildx', 'imagetools', 'inspect', image, '--format', '{{.Manifest.Digest}}',
      ], worktreePath, `Could not resolve ${image}`);
      const digest = inspected.stdout.trim();
      if (!/^sha256:[a-f0-9]{64}$/.test(digest)) fail(`Registry returned an invalid digest for ${image}.`);
      const verified = await verifyImage(`${registry}/${component.repository}@${digest}`,
        fetchImpl, undefined, overrides.registryOptions);
      if (verified.sourceSha !== sourceSha || verified.sourceTree !== sourceTree ||
          verified.version !== version) {
        fail(`Image ${component.repository} does not match source, tree, and version identity.`);
      }
      images.push({ repository: component.repository, digest, image: verified.image });
    }

    const agentHost = await measureAgentHost(
      `${registry}/agentweaver-agenthost@${images[0].digest}`,
      fetchImpl, undefined, overrides.registryOptions);
    report = {
      sourceSha,
      sourceTree,
      registry,
      images,
      agentHost: {
        digest: agentHost.digest,
        platform: agentHost.platform,
        compressedPullBytes: agentHost.compressedPullBytes,
      },
    };
  } catch (error) {
    failure = error;
  }

  if (worktreeAdded || (worktreeAttempted && existsSync(worktreePath))) {
    try {
      command(exec, 'git', ['worktree', 'remove', '--force', worktreePath],
        root, `Could not remove temporary worktree ${worktreePath}`);
    } catch (error) {
      const cleanupError = `Could not remove temporary worktree "${worktreePath}": ${error.message}`;
      throw new Error(failure ? `${failure.message}; ${cleanupError}` : cleanupError);
    }
  }
  if (failure) throw failure;
  return report;
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))) {
  try {
    const report = await buildAcrCandidate(parseBuildAcrCandidateArgs(process.argv.slice(2)));
    console.log(JSON.stringify(report, null, 2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
