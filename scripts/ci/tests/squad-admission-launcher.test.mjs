import assert from 'node:assert/strict';
import test from 'node:test';
import { execFile } from 'node:child_process';
import { copyFile, mkdir, mkdtemp, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { promisify } from 'node:util';
import { loadTrustedRuntime, runTrustedAdmission } from '../squad-admission-launcher.mjs';

const execFileAsync = promisify(execFile);
const runtimePaths = [
  'squad-admission-launcher.mjs',
  'squad-admission-authority.mjs',
  'squad-admission-ledger.mjs',
  'squad-admission-preflight.mjs',
];

async function git(cwd, ...args) {
  return (await execFileAsync('git', args, { cwd })).stdout.trim();
}

function review(source, reviewer, worktree, branch, headSha, findings = []) {
  return {
    kind: 'agentweaver.squad-review/v2',
    phase: 'implementation',
    source,
    reviewer,
    verdict: 'approved',
    target: { type: 'worktree', worktree, branch, headSha },
    findings,
  };
}

function evidence(worktree, branch, headSha) {
  return {
    kind: 'agentweaver.validation-evidence/v1',
    argv: ['node', '--test'],
    cwd: worktree,
    worktree,
    branch,
    headSha,
    startedAt: '2026-09-22T18:00:00.000Z',
    completedAt: '2026-09-22T18:00:01.000Z',
    exitCode: 0,
    signal: null,
    result: 'passed',
    stdout: '',
    stderr: '',
    after: { cwd: worktree, worktree, branch, headSha },
  };
}

async function fixture() {
  const root = await mkdtemp(join(tmpdir(), 'agentweaver-admission-repo-'));
  const teamRoot = join(root, 'team');
  const source = new URL('../', import.meta.url);
  await git(root, 'init', '-b', 'dev');
  await git(root, 'config', 'user.email', 'test@example.com');
  await git(root, 'config', 'user.name', 'Test');
  await git(root, 'remote', 'add', 'origin', '.');
  await mkdir(join(root, 'scripts', 'ci'), { recursive: true });
  await mkdir(join(root, '.squad'), { recursive: true });
  await mkdir(join(teamRoot, '.squad'), { recursive: true });
  for (const file of runtimePaths) {
    await copyFile(new URL(`../${file}`, import.meta.url), join(root, 'scripts', 'ci', file));
  }
  await writeFile(join(root, '.squad', 'config.json'), JSON.stringify({ teamRoot }));
  await writeFile(join(teamRoot, '.squad', 'config.json'), JSON.stringify({ stateBackend: 'local' }));
  await git(root, 'add', '.');
  await git(root, 'commit', '-m', 'trusted base');
  const baseSha = await git(root, 'rev-parse', 'HEAD');
  await git(root, 'checkout', '-b', 'candidate');
  for (const file of runtimePaths) {
    await writeFile(join(root, 'scripts', 'ci', file), [
      'export const REQUIRED_REVIEW_SOURCES=[];',
      'export const WAIVER_ACTORS=["attacker"];',
      'export const materializeAdmissionLedger=()=>({admitted:true});',
      'export const runAdmissionPreflight=()=>({admitted:true});',
    ].join('\n'));
  }
  await git(root, 'add', '.');
  await git(root, 'commit', '-m', 'malicious candidate admission');
  const headSha = await git(root, 'rev-parse', 'HEAD');
  const launcherDirectory = await mkdtemp(join(tmpdir(), 'agentweaver-launcher-'));
  const launcherPath = join(launcherDirectory, 'squad-admission-launcher.mjs');
  const launcherBytes = await execFileAsync('git', ['cat-file', 'blob', `${baseSha}:scripts/ci/squad-admission-launcher.mjs`], {
    cwd: root,
    encoding: 'buffer',
    maxBuffer: 1024 * 1024,
  });
  await writeFile(launcherPath, launcherBytes.stdout);
  await mkdir(join(teamRoot, 'admission', 'runtime'), { recursive: true });
  await writeFile(join(teamRoot, 'admission', 'runtime', 'squad-admission-launcher.mjs'), 'throw new Error("persistent replacement executed");');
  return {
    root,
    teamRoot,
    baseSha,
    headSha,
    launcherPath,
    branch: 'candidate',
  };
}

function input(value, extra = {}) {
  return {
    repository: 'sabbour/agentweaver',
    prNumber: 1504,
    worktree: value.root,
    branch: value.branch,
    headSha: value.headSha,
    requiredReviewSources: ['code-review', 'security-review', 'ponytail-review'],
    reviews: [
      review('code-review', 'smith', value.root, value.branch, value.headSha),
      review('security-review', 'seraph', value.root, value.branch, value.headSha),
      review('ponytail-review', 'ponytail-reviewer', value.root, value.branch, value.headSha),
    ],
    validations: [evidence(value.root, value.branch, value.headSha)],
    materializedAt: '2026-09-22T18:00:02.000Z',
    ...extra,
  };
}

function options(value, command, inputPath) {
  return {
    '--repository': 'sabbour/agentweaver',
    '--pr-number': '1504',
    '--worktree': value.root,
    '--head-sha': value.headSha,
    '--base-ref': 'refs/remotes/origin/dev',
    '--base-sha': value.baseSha,
    '--team-root': value.teamRoot,
    '--state-backend': 'local',
    '--launcher-path': value.launcherPath,
    ...(command === 'materialize' ? { '--input': inputPath } : {}),
  };
}

test('trusted base bytes ignore candidate and persistent runtime replacements', async () => {
  const value = await fixture();
  const loaded = await loadTrustedRuntime({
    worktree: value.root,
    baseRef: 'refs/remotes/origin/dev',
    baseSha: value.baseSha,
    launcherPath: value.launcherPath,
  });
  try {
    assert.equal(loaded.runtime.kind, 'agentweaver.squad-admission-runtime/v2');
    assert.equal(loaded.runtime.source.commit, value.baseSha);
    assert.deepEqual(Object.keys(loaded.runtime.files).sort(), [
      'scripts/ci/squad-admission-authority.mjs',
      'scripts/ci/squad-admission-launcher.mjs',
      'scripts/ci/squad-admission-ledger.mjs',
      'scripts/ci/squad-admission-preflight.mjs',
    ]);
    for (const identity of Object.values(loaded.runtime.files)) {
      assert.match(identity.objectId, /^[0-9a-f]{40,64}$/u);
      assert.match(identity.digest, /^sha256:[0-9a-f]{64}$/u);
    }
    assert.match(loaded.runtime.aggregateDigest, /^sha256:[0-9a-f]{64}$/u);
    assert.equal(
      loaded.runtime.files['scripts/ci/squad-admission-launcher.mjs'].objectId,
      await git(value.root, 'rev-parse', `${value.baseSha}:scripts/ci/squad-admission-launcher.mjs`),
    );
  } finally {
    await loaded.cleanup();
  }
});

test('materialize creates a usable v3 ledger and preflight consumes it', async () => {
  const value = await fixture();
  const inputPath = join(value.root, 'admission-input.json');
  await writeFile(inputPath, JSON.stringify(input(value)));
  const materialized = await runTrustedAdmission('materialize', options(value, 'materialize', inputPath));
  assert.equal(materialized.ledger.kind, 'agentweaver.squad-admission-findings/v3');
  assert.equal(materialized.ledger.baseSha, value.baseSha);
  assert.equal(materialized.ledger.trustedRuntime.source.commit, value.baseSha);
  assert.equal(materialized.ledger.trustedRuntime.files['scripts/ci/squad-admission-launcher.mjs'].objectId.length >= 40, true);

  const preflight = await runTrustedAdmission('preflight', options(value, 'preflight'));
  assert.equal(preflight.admitted, true);
  assert.equal(preflight.headSha, value.headSha);
  assert.equal(preflight.baseSha, value.baseSha);
  assert.deepEqual(preflight.trustedRuntime, materialized.ledger.trustedRuntime);
});

test('candidate-lowered reviewer and waiver policy fails closed', async () => {
  const value = await fixture();
  const inputPath = join(value.root, 'admission-input.json');
  await writeFile(inputPath, JSON.stringify(input(value, {
    requiredReviewSources: ['code-review'],
    reviews: [review('code-review', 'smith', value.root, value.branch, value.headSha, [{
      id: 'F-WAIVE',
      policy: 'required',
      summary: 'Candidate-authored waiver.',
      waiver: { actor: 'attacker', rationale: 'Self-authorized.' },
    }])],
  })));
  await assert.rejects(
    () => runTrustedAdmission('materialize', options(value, 'materialize', inputPath)),
    /configured reviewer policy/u,
  );
  await writeFile(inputPath, JSON.stringify(input(value, {
    reviews: [
      review('code-review', 'smith', value.root, value.branch, value.headSha, [{
        id: 'F-WAIVE',
        policy: 'required',
        summary: 'Candidate-authored waiver.',
        waiver: { actor: 'attacker', rationale: 'Self-authorized.' },
      }]),
      review('security-review', 'seraph', value.root, value.branch, value.headSha),
      review('ponytail-review', 'ponytail-reviewer', value.root, value.branch, value.headSha),
    ],
  })));
  await assert.rejects(
    () => runTrustedAdmission('materialize', options(value, 'materialize', inputPath)),
    /waiver actor attacker is not authorized/u,
  );
});

test('wrong or stale base and failed fetch block trusted materialization', async () => {
  const value = await fixture();
  await assert.rejects(() => loadTrustedRuntime({
    worktree: value.root,
    baseRef: 'refs/remotes/origin/dev',
    baseSha: value.headSha,
    launcherPath: value.launcherPath,
  }), /moved or is stale/u);
  await assert.rejects(() => loadTrustedRuntime({
    worktree: value.root,
    baseRef: 'refs/remotes/origin/dev',
    baseSha: value.baseSha,
    launcherPath: value.launcherPath,
  }, {
    runGit: async (args) => ({
      exitCode: args[0] === 'fetch' ? 1 : 0,
      stdout: Buffer.alloc(0),
      stderr: 'network unavailable',
    }),
  }), /fetch.*network unavailable/u);
});

test('ephemeral policy files are removed on success and failure', async () => {
  const value = await fixture();
  const created = [];
  const trackedTemp = async (prefix) => {
    const directory = await mkdtemp(prefix);
    created.push(directory);
    return directory;
  };
  const loaded = await loadTrustedRuntime({
    worktree: value.root,
    baseRef: 'refs/remotes/origin/dev',
    baseSha: value.baseSha,
    launcherPath: value.launcherPath,
  }, { mkdtemp: trackedTemp });
  await loaded.cleanup();
  await assert.rejects(() => stat(created[0]), /ENOENT/u);

  await assert.rejects(() => loadTrustedRuntime({
    worktree: value.root,
    baseRef: 'refs/remotes/origin/dev',
    baseSha: value.baseSha,
    launcherPath: value.launcherPath,
  }, {
    mkdtemp: trackedTemp,
    importModule: async () => { throw new Error('import failed'); },
  }), /import failed/u);
  await assert.rejects(() => stat(created[1]), /ENOENT/u);
});
