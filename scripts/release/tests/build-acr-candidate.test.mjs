import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { IMAGE_REPOSITORIES } from '../image-repositories.mjs';
import {
  buildAcrCandidate,
  BUILD_ACR_CANDIDATE_USAGE,
  parseBuildAcrCandidateArgs,
} from '../build-acr-candidate.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const sourceSha = 'a'.repeat(40);
const sourceTree = 'b'.repeat(40);
const registry = 'agentweaverasabbour.azurecr.io';
const componentIds = [
  'Agentweaver.AgentHost', 'Agentweaver.Identity.Broker', 'Agentweaver.EventsAndSessions',
  'Agentweaver.Gateway', 'Agentweaver.Mcp', 'Agentweaver.Web', 'Agentweaver.Orchestrator',
  'Agentweaver.Environment', 'Agentweaver.Knowledge', 'Agentweaver.Projects.Config',
];

test('requires a full source SHA and an ACR login server', () => {
  assert.deepEqual(parseBuildAcrCandidateArgs([sourceSha, registry]), {
    sourceSha, registry, registryName: 'agentweaverasabbour',
  });
  assert.match(BUILD_ACR_CANDIDATE_USAGE, /azure:build-acr-candidate/);
  for (const args of [
    ['A'.repeat(40), registry],
    [sourceSha],
    [sourceSha, 'ghcr.io/sabbour'],
    [sourceSha, 'not-a-registry'],
  ]) assert.throws(() => parseBuildAcrCandidateArgs(args), /Usage:|registry must be/);
});

test('builds and verifies all ten images from one detached source worktree', async () => {
  const testRoot = path.join(root, `.acr-candidate-test-${randomUUID()}`);
  const worktreePath = path.join(testRoot, 'source');
  mkdirSync(testRoot);
  const calls = [];
  const version = '1.2.3';
  const digests = Object.fromEntries(IMAGE_REPOSITORIES.map((repository, index) =>
    [repository, `sha256:${(index + 1).toString(16).padStart(64, '0')}`]));
  const exec = (name, args, options = {}) => {
    calls.push({ name, args, cwd: options.cwd });
    if (name === 'git' && args[0] === 'fetch') return { status: 0, stdout: '', stderr: '' };
    if (name === 'git' && args[0] === 'merge-base') return { status: 0, stdout: '', stderr: '' };
    if (name === 'git' && args[0] === 'rev-parse') {
      return { status: 0, stdout: `${args[2] === 'HEAD' ? sourceSha : sourceTree}\n`, stderr: '' };
    }
    if (name === 'git' && args[0] === 'worktree' && args[1] === 'add') {
      mkdirSync(path.join(worktreePath, 'releases'), { recursive: true });
      mkdirSync(path.join(worktreePath, 'apps', 'web'), { recursive: true });
      writeFileSync(path.join(worktreePath, 'releases', 'foundation.json'), JSON.stringify({
        components: componentIds.map(id => ({ id, version })),
      }));
      writeFileSync(path.join(worktreePath, 'apps', 'web', 'package.json'), JSON.stringify({ version }));
      return { status: 0, stdout: '', stderr: '' };
    }
    if (name === 'git' && args[0] === 'worktree' && args[1] === 'remove') {
      rmSync(worktreePath, { recursive: true, force: true });
      return { status: 0, stdout: '', stderr: '' };
    }
    if (name === 'docker' && args[0] === 'buildx' && args[1] === 'imagetools') {
      const image = args[3];
      const repository = image.slice(`${registry}/`.length).split(/[:@]/)[0];
      return { status: 0, stdout: `${digests[repository]}\n`, stderr: '' };
    }
    return { status: 0, stdout: '', stderr: '' };
  };
  const verified = [];
  const verifyImage = async image => {
    verified.push(image);
    return { sourceSha, sourceTree, version, image };
  };
  let measuredImage;
  const measureAgentHost = async image => {
    measuredImage = image;
    return { digest: digests['agentweaver-agenthost'], platform: 'linux/amd64', compressedPullBytes: 123 };
  };
  let loginArgs;
  try {
    const report = await buildAcrCandidate({ sourceSha, registry }, {
      root,
      worktreePath,
      exec,
      runAzure: args => { loginArgs = args; },
      verifyImage,
      measureAgentHost,
    });

    assert.deepEqual(loginArgs, ['acr', 'login', '--name', 'agentweaverasabbour']);
    assert.deepEqual(report.images.map(image => image.repository), IMAGE_REPOSITORIES);
    assert.equal(report.sourceTree, sourceTree);
    assert.equal(report.agentHost.compressedPullBytes, 123);
    assert.equal(verified.length, 10);
    assert.equal(measuredImage, `${registry}/agentweaver-agenthost@${digests['agentweaver-agenthost']}`);
    const dockerBuild = calls.find(({ name, args }) =>
      name === 'docker' && args[0] === 'buildx' && args[1] === 'build');
    assert.ok(dockerBuild.args.includes('linux/amd64'));
    assert.ok(dockerBuild.args.includes(`org.opencontainers.image.revision=${sourceSha}`));
    assert.ok(dockerBuild.args.includes(`io.agentweaver.source-tree=${sourceTree}`));
    const dotnetPublish = calls.find(({ name, args }) =>
      name === 'dotnet' && args[0] === 'publish' && args.includes('-t:PublishContainer'));
    assert.ok(dotnetPublish.args.includes(`-p:ContainerRegistry=${registry}`));
    assert.ok(dotnetPublish.args.includes('-p:ContainerTargetOs=linux'));
    assert.ok(dotnetPublish.args.includes('-p:ContainerTargetArchitecture=x64'));
    assert.ok(dotnetPublish.args.includes(`-p:AgentweaverImageVersion=${version}`));
  } finally {
    rmSync(testRoot, { recursive: true, force: true });
  }
});
