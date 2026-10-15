import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { componentImageRepository, packComponentsFromFile } from '../pack.mjs';
import { publishArtifacts } from '../publish.mjs';
import { WEB_HOST_PROJECT_PATH, WEB_PROJECT_PATH } from '../validate.mjs';
import { agentHostComponent, agentHostDockerFixture, addAgentHostSource } from './agenthost-fixture.mjs';

const imageDigest = `sha256:${'a'.repeat(64)}`;
const webBaseImage = `mcr.microsoft.com/dotnet/aspnet@${imageDigest}`;
const productServices = [
  { id: 'Agentweaver.AgentHost', project: agentHostComponent.project },
  { id: 'Agentweaver.Identity.Broker', project: 'services/identity/Agentweaver.Identity.Broker/Agentweaver.Identity.Broker.csproj' },
  { id: 'Agentweaver.EventsAndSessions', project: 'services/events-and-sessions/Agentweaver.EventsAndSessions/Agentweaver.EventsAndSessions.csproj' },
  { id: 'Agentweaver.Gateway', project: 'services/gateway/Agentweaver.Gateway/Agentweaver.Gateway.csproj' },
  { id: 'Agentweaver.Mcp', project: 'services/mcp/Agentweaver.Mcp/Agentweaver.Mcp.csproj' },
  { id: 'Agentweaver.Web', project: WEB_PROJECT_PATH },
  { id: 'Agentweaver.Orchestrator', project: 'services/orchestrator/Agentweaver.Orchestrator/Agentweaver.Orchestrator.csproj' },
  { id: 'Agentweaver.Environment', project: 'services/environment/Agentweaver.Environment/Agentweaver.Environment.csproj' },
  { id: 'Agentweaver.Knowledge', project: 'services/knowledge/Agentweaver.Knowledge/Agentweaver.Knowledge.csproj' },
  { id: 'Agentweaver.Projects.Config', project: 'services/projects-config/Agentweaver.Projects.Config/Agentweaver.Projects.Config.csproj' },
].map(component => ({
  ...component,
  kind: 'service',
  version: '1.0.0',
}));

function git(root, ...args) {
  return execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();
}

function writeDockerArchive(archive, imageReference, sourceSha, sourceTree, version, configValue, containerdBlob = false) {
  const directory = mkdtempSync(path.join(path.dirname(archive), 'docker-archive-'));
  const labels = {
    'org.opencontainers.image.revision': sourceSha,
    'io.agentweaver.source-tree': sourceTree,
    'org.opencontainers.image.version': version,
  };
  const config = Buffer.from(JSON.stringify(configValue ?? {
    os: 'linux', architecture: 'amd64', config: { Labels: labels },
  }));
  const configDigest = createHash('sha256').update(config).digest('hex');
  const configName = containerdBlob ? `blobs/sha256/${configDigest}` : `${configDigest}.json`;
  mkdirSync(path.dirname(path.join(directory, configName)), { recursive: true });
  writeFileSync(path.join(directory, 'manifest.json'), JSON.stringify([{
    Config: configName, RepoTags: [imageReference], Layers: [],
  }]));
  writeFileSync(path.join(directory, configName), config);
  try {
    execFileSync('tar', ['-cf', archive, 'manifest.json', configName], { cwd: directory });
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

function integrationRepository(t) {
  const fixtureRoot = path.resolve('artifacts', 'release-tests');
  mkdirSync(fixtureRoot, { recursive: true });
  const root = mkdtempSync(path.join(fixtureRoot, 'v1-integration-pack-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  writeFileSync(path.join(root, '.gitignore'),
    'artifacts/\napps/web/node_modules/\napps/web/dist/\n**/bin/\n**/obj/\n.env.*.local\n');
  addAgentHostSource(root, '1.0.0');

  for (const component of productServices) {
    if ([agentHostComponent.project, WEB_PROJECT_PATH].includes(component.project)) continue;
    const project = path.join(root, component.project);
    mkdirSync(path.dirname(project), { recursive: true });
    writeFileSync(project, `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><Version>${component.version}</Version><ContainerBaseImage>mcr.microsoft.com/dotnet/aspnet@${imageDigest}</ContainerBaseImage></PropertyGroup></Project>\n`);
    writeFileSync(path.join(path.dirname(project), 'packages.lock.json'), '{"version":1,"dependencies":{}}\n');
  }

  const web = path.join(root, 'apps', 'web');
  const host = path.join(web, 'host');
  mkdirSync(path.join(web, 'src'), { recursive: true });
  mkdirSync(host, { recursive: true });
  writeFileSync(path.join(web, 'package.json'), JSON.stringify({
    name: 'web', private: true, version: '1.0.0', scripts: { build: 'vite build' },
  }) + '\n');
  writeFileSync(path.join(web, 'package-lock.json'), JSON.stringify({
    name: 'web', version: '1.0.0', lockfileVersion: 3,
    packages: {
      '': { name: 'web', version: '1.0.0' },
      'node_modules/vite': { version: '8.0.0', resolved: 'https://registry.invalid/vite.tgz', integrity: 'sha512-test' },
    },
  }) + '\n');
  writeFileSync(path.join(web, 'src', 'main.ts'), 'export const app = "web";\n');
  writeFileSync(path.join(host, 'Agentweaver.Web.Host.csproj'),
    '<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n');
  writeFileSync(path.join(host, 'Program.cs'), 'var app = WebApplication.Create();\n');
  writeFileSync(path.join(host, 'packages.lock.json'), '{"version":1,"dependencies":{".NETCoreApp,Version=v10.0":{}}}\n');
  writeFileSync(path.join(web, 'Dockerfile'), readFileSync(path.resolve('apps', 'web', 'Dockerfile')));

  const manifest = { schemaVersion: 1, stage: 'draft', components: productServices, compatibility: [] };
  mkdirSync(path.join(root, 'releases'), { recursive: true });
  writeFileSync(path.join(root, 'releases', 'foundation.json'), `${JSON.stringify(manifest, null, 2)}\n`);
  git(root, 'init', '-q');
  git(root, 'config', 'core.autocrlf', 'false');
  git(root, 'add', '.');
  git(root, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'integration source');
  return { root, manifest };
}

test('integration image-only publication keeps ten image outputs without NuGet credentials', t => {
  const { root, manifest } = integrationRepository(t);
  const sourceSha = git(root, 'rev-parse', 'HEAD');
  const sourceTree = git(root, 'rev-parse', `${sourceSha}^{tree}`);
  const output = path.join(root, 'artifacts', 'release', 'integration');
  const dotnetCalls = [];
  let webDockerBuild;
  const hostDocker = agentHostDockerFixture(root, { version: '1.0.0' });
  const docker = (args, cwd) => {
    if (args[0] === 'build') {
      webDockerBuild = args;
      return '';
    }
    if (args[0] === 'image' && args[1] === 'inspect' && args.at(-1).startsWith('agentweaver-web:')) {
      return JSON.stringify({
        Id: `sha256:${'e'.repeat(64)}`,
        Os: 'linux',
        Architecture: 'amd64',
        Config: { Labels: {
          'org.opencontainers.image.revision': sourceSha,
          'io.agentweaver.source-tree': sourceTree,
          'org.opencontainers.image.version': '1.0.0',
        } },
      });
    }
    if (args[0] === 'save' && args.at(-1).startsWith('agentweaver-web:')) {
      writeDockerArchive(args[args.indexOf('--output') + 1], args.at(-1), sourceSha, sourceTree, '1.0.0', undefined, true);
      return '';
    }
    if (args[0] === 'save' && args.at(-1).startsWith('agentweaver-agenthost:')) {
      hostDocker(args, cwd);
      writeDockerArchive(args[args.indexOf('--output') + 1], args.at(-1), sourceSha, sourceTree, '1.0.0', {
        os: 'linux',
        architecture: 'amd64',
        config: {
          User: '1654:1654',
          Entrypoint: ['dotnet', 'Agentweaver.AgentHost.dll'],
          Labels: {
            'org.opencontainers.image.revision': sourceSha,
            'io.agentweaver.source-tree': sourceTree,
            'org.opencontainers.image.version': '1.0.0',
          },
        },
      });
      return '';
    }
    if (args[0] === 'image' && args[1] === 'inspect' && args.at(-1).startsWith('agentweaver-agenthost:')) {
      return hostDocker(args, cwd);
    }
    if (args[0] === 'image' && args[1] === 'inspect' && args.at(-1).startsWith('agentweaver-')) {
      return JSON.stringify({
        Id: `sha256:${'f'.repeat(64)}`,
        Os: 'linux',
        Architecture: 'amd64',
        Config: { Labels: {
          'org.opencontainers.image.revision': sourceSha,
          'io.agentweaver.source-tree': sourceTree,
          'org.opencontainers.image.version': '1.0.0',
        } },
      });
    }
    if (args[0] === 'load') return '';
    return hostDocker(args, cwd);
  };
  const provenance = packComponentsFromFile(path.join(root, 'releases', 'foundation.json'), {
    root,
    outDir: output,
    integrationSourceSha: sourceSha,
    npm(args, cwd) {
      if (args[0] === 'run') {
        mkdirSync(path.join(cwd, 'dist'), { recursive: true });
        writeFileSync(path.join(cwd, 'dist', 'index.html'), '<html>web</html>\n');
      }
    },
    dotnet(args) {
      dotnetCalls.push(args);
      if (args[0] !== 'publish') return;
      if (args[1] === path.resolve(root, WEB_HOST_PROJECT_PATH)) {
        const outputDirectory = args[args.indexOf('--output') + 1];
        mkdirSync(outputDirectory, { recursive: true });
        writeFileSync(path.join(outputDirectory, 'Agentweaver.Web.Host.dll'), 'web host');
        writeFileSync(path.join(outputDirectory, 'Agentweaver.Web.Host.runtimeconfig.json'), '{"runtimeOptions":{}}\n');
      } else {
        const archive = args.find(value => value.startsWith('-p:ContainerArchiveOutputPath=')).slice('-p:ContainerArchiveOutputPath='.length);
        const repository = args.find(value => value.startsWith('-p:ContainerRepository=')).slice('-p:ContainerRepository='.length);
        const tag = args.find(value => value.startsWith('-p:ContainerImageTag=')).slice('-p:ContainerImageTag='.length);
        const version = args.find(value => value.startsWith('-p:AgentweaverIntegrationVersion='))
          .slice('-p:AgentweaverIntegrationVersion='.length);
        writeDockerArchive(archive, `${repository}:${tag}`, sourceSha, sourceTree, version);
      }
    },
    docker,
    environment: { PATH: process.env.PATH },
  });

  assert.equal(provenance.stage, 'draft');
  assert.equal(provenance.sourceSha, sourceSha);
  assert.deepEqual(provenance.integration, { mode: 'images-only', sourceTree, imageTag: sourceSha });
  assert.deepEqual(provenance.components.map(({ id }) => id).sort(), productServices.map(({ id }) => id).sort());
  assert.equal(provenance.artifacts.length, 10);
  assert.ok(provenance.artifacts.every(artifact =>
    artifact.kind === 'image' && artifact.tag === sourceSha &&
    artifact.imageReference === `${componentImageRepository(artifact.componentId)}:${sourceSha}`));

  const webArtifact = provenance.artifacts.find(({ componentId }) => componentId === 'Agentweaver.Web');
  assert.equal(webArtifact.labels['io.agentweaver.source-tree'], sourceTree);
  assert.match(webArtifact.imageConfigDigest, /^sha256:[a-f0-9]{64}$/);
  const webArchiveManifest = JSON.parse(execFileSync('tar', [
    '-xOf', path.join(output, 'Agentweaver.Web.1.0.0.tar'), 'manifest.json',
  ], { encoding: 'utf8' }));
  assert.match(webArchiveManifest[0].Config, /^blobs\/sha256\/[a-f0-9]{64}$/);
  for (const [name, value] of [
    ['org.opencontainers.image.revision', sourceSha],
    ['io.agentweaver.source-tree', sourceTree],
    ['org.opencontainers.image.version', '1.0.0'],
  ]) {
    assert.ok(webDockerBuild.includes('--label'));
    assert.ok(webDockerBuild.includes(`${name}=${value}`));
  }

  const containerPublish = dotnetCalls.filter(args =>
    args[0] === 'publish' && args.includes('-t:PublishContainer'));
  assert.equal(containerPublish.length, 8);
  assert.ok(containerPublish.every(args =>
    args.includes(`-p:ContainerImageTag=${sourceSha}`) &&
    args.includes('-p:AgentweaverIntegrationImagesOnly=true') &&
    args.includes(`-p:AgentweaverIntegrationSourceSha=${sourceSha}`) &&
    args.includes(`-p:AgentweaverIntegrationSourceTree=${sourceTree}`) &&
    args.includes('-p:AgentweaverIntegrationVersion=1.0.0')));

  const hostArtifact = provenance.artifacts.find(({ componentId }) => componentId === 'Agentweaver.AgentHost');
  assert.equal(hostArtifact.tag, sourceSha);
  assert.ok(existsSync(path.join(output, 'provenance.json')));
  assert.equal(JSON.parse(readFileSync(path.join(output, 'provenance.json'), 'utf8')).manifestSha256,
    provenance.manifestSha256);

  const tags = new Map();
  const refs = new Map();
  const githubCalls = [];
  const dockerCalls = [];
  const github = (args, input) => {
    githubCalls.push(args);
    const method = args[args.indexOf('--method') + 1];
    const endpoint = args[3];
    const body = input ? JSON.parse(input) : undefined;
    if (method === 'GET' && endpoint.includes('/git/matching-refs/')) return '[]';
    if (method === 'POST' && endpoint.endsWith('/git/tags')) {
      const sha = createHash('sha1').update(body.tag).digest('hex');
      const tag = { sha, tag: body.tag, message: body.message, object: { type: body.type, sha: body.object } };
      tags.set(sha, tag);
      return JSON.stringify(tag);
    }
    if (method === 'POST' && endpoint.endsWith('/git/refs')) {
      const ref = body.ref;
      const created = { ref, object: { type: 'tag', sha: body.sha } };
      refs.set(ref, created);
      return JSON.stringify(created);
    }
    throw new Error(`unexpected GitHub call: ${method} ${endpoint}`);
  };
  const run = (bin, args, input) => {
    if (bin === 'gh') return github(args, input);
    assert.equal(bin, 'docker');
    dockerCalls.push(args);
    if (args[0] === 'image' && args[1] === 'inspect') {
      const image = args.at(-1);
      if (image.startsWith('agentweaver-agenthost:')) return hostDocker(args, root);
      const web = image.startsWith('agentweaver-web:');
      return JSON.stringify({
        Id: web ? `sha256:${'e'.repeat(64)}` : `sha256:${'f'.repeat(64)}`,
        Os: 'linux',
        Architecture: 'amd64',
        Config: { Labels: {
          'org.opencontainers.image.revision': sourceSha,
          'io.agentweaver.source-tree': sourceTree,
          'org.opencontainers.image.version': '1.0.0',
        } },
      });
    }
    if (args[0] === 'run') return hostDocker(args, root);
    if (args[0] === 'load') return '';
    if (args[0] === 'inspect' && args[1] === '--format' && args[2] === '{{json .RepoDigests}}') {
      const repository = args.at(-1).replace(/:[^/:]+$/, '');
      return JSON.stringify([`${repository}@sha256:${'b'.repeat(64)}`]);
    }
    return '';
  };
  const published = publishArtifacts(path.join(root, 'releases', 'foundation.json'), output, sourceSha, {
    root,
    integrationImagesOnly: true,
    integrationConfirmed: true,
    env: {
      GITHUB_REPOSITORY: 'sabbour/agentweaver',
      GH_TOKEN: 'fixture-token',
      RELEASE_REGISTRY: 'ghcr.io/sabbour',
      RELEASE_REGISTRY_USER: 'fixture-user',
      RELEASE_REGISTRY_PASSWORD: 'fixture-password',
      DOCKER_CONFIG: path.join(root, 'artifacts', 'docker-config'),
    },
    run,
  });
  assert.equal(published.status, 'published');
  assert.equal(published.mode, 'images-only');
  assert.equal(published.sourceTree, sourceTree);
  assert.equal(published.planned.length, 10);
  assert.equal(published.published.length, 10);
  assert.ok(published.planned.every(record => record.kind === 'image'));
  assert.ok(published.planned.every(record => record.destination.endsWith(`:${sourceSha}`)));
  assert.ok(published.published.every(record => record.tag === sourceSha && record.sourceTree === sourceTree));
  assert.ok(published.published.every(record => /^sha256:[a-f0-9]{64}$/.test(record.configDigest)));
  assert.ok(existsSync(path.join(output, 'integration-publication.json')));
  assert.equal(existsSync(path.join(output, 'publication.json')), false);
  assert.ok([...refs.keys()].some(ref => ref === `refs/tags/agentweaver-integration/${sourceSha}/claim`));
  assert.ok([...refs.keys()].some(ref => ref === `refs/tags/agentweaver-integration/${sourceSha}/result`));
  assert.equal(githubCalls.some(args => args.some(arg => String(arg).includes('/releases/'))), false);
  assert.equal(dockerCalls.filter(args => args[0] === 'push').length, 10);
  assert.equal(dockerCalls.some(args => args[0] === 'login' && args[1] === 'ghcr.io'), true);
});
