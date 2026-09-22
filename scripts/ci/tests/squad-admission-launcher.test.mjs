import assert from 'node:assert/strict';
import test from 'node:test';
import { createHash } from 'node:crypto';
import { copyFile, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const files = [
  'squad-admission-authority.mjs',
  'squad-admission-ledger.mjs',
  'squad-admission-preflight.mjs',
];
const sha256 = (value) => `sha256:${createHash('sha256').update(value).digest('hex')}`;

async function installRuntime(directory) {
  const launcher = 'squad-admission-launcher.mjs';
  await copyFile(new URL(`../${launcher}`, import.meta.url), join(directory, launcher));
  const digests = {};
  for (const file of files) {
    await copyFile(new URL(`../${file}`, import.meta.url), join(directory, file));
    digests[file] = sha256(await readFile(join(directory, file)));
  }
  const manifest = {
    kind: 'agentweaver.squad-admission-runtime/v1',
    source: {
      ref: 'refs/remotes/origin/dev',
      commit: 'c'.repeat(40),
    },
    launcherDigest: sha256(await readFile(join(directory, launcher))),
    policyDigest: sha256(files.map((file) => `${file}:${digests[file]}`).join('\n')),
    files: digests,
  };
  const manifestPath = join(directory, 'squad-admission-runtime.json');
  await writeFile(manifestPath, JSON.stringify(manifest));
  return { manifest, manifestPath, launcher: join(directory, launcher) };
}

function evidence(exitCode = 0) {
  const worktree = 'C:\\trusted\\worktree';
  const branch = 'squad/candidate';
  const headSha = 'a'.repeat(40);
  return {
    kind: 'agentweaver.validation-evidence/v1',
    argv: ['node', '--test'],
    cwd: worktree,
    worktree,
    branch,
    headSha,
    startedAt: '2026-09-22T18:00:00.000Z',
    completedAt: '2026-09-22T18:00:01.000Z',
    exitCode,
    signal: null,
    result: exitCode === 0 ? 'passed' : 'failed',
    stdout: '',
    stderr: '',
    after: { cwd: worktree, worktree, branch, headSha },
  };
}

function review(source, reviewer, findings = []) {
  return {
    kind: 'agentweaver.squad-review/v2',
    phase: 'implementation',
    source,
    reviewer,
    verdict: 'approved',
    target: {
      type: 'worktree',
      worktree: 'C:\\trusted\\worktree',
      branch: 'squad/candidate',
      headSha: 'a'.repeat(40),
    },
    findings,
  };
}

function input(extra = {}) {
  return {
    repository: 'sabbour/agentweaver',
    prNumber: 1504,
    worktree: 'C:\\trusted\\worktree',
    branch: 'squad/candidate',
    headSha: 'a'.repeat(40),
    requiredReviewSources: ['code-review', 'security-review', 'ponytail-review'],
    reviews: [
      review('code-review', 'smith'),
      review('security-review', 'seraph'),
      review('ponytail-review', 'ponytail-reviewer'),
    ],
    validations: [evidence()],
    materializedAt: '2026-09-22T18:00:02.000Z',
    ...extra,
  };
}

test('installed runtime ignores candidate policy, waiver, and validation replacements', async () => {
  const trustedDirectory = await mkdtemp(join(tmpdir(), 'agentweaver-trusted-admission-'));
  const candidateDirectory = await mkdtemp(join(tmpdir(), 'agentweaver-candidate-admission-'));
  const installed = await installRuntime(trustedDirectory);
  await Promise.all(files.map((file) => writeFile(
    join(candidateDirectory, file),
    'export const REQUIRED_REVIEW_SOURCES=[]; export const WAIVER_ACTORS=["attacker"]; export const validateAdmissionLedger=()=>({admitted:true});\n',
  )));

  const launcher = await import(`${pathToFileURL(installed.launcher).href}?installed`);
  const runtime = await launcher.loadTrustedRuntime(installed.manifestPath);
  const options = {
    trustedRuntime: runtime.manifest,
    baseSha: 'b'.repeat(40),
  };

  assert.deepEqual(runtime.manifest, {
    kind: installed.manifest.kind,
    source: installed.manifest.source,
    launcherDigest: installed.manifest.launcherDigest,
    policyDigest: installed.manifest.policyDigest,
  });
  assert.throws(() => runtime.ledger.materializeLedger(input({
    requiredReviewSources: ['code-review'],
    reviews: [review('code-review', 'smith')],
  }), options), /configured reviewer policy/u);
  assert.throws(() => runtime.ledger.materializeLedger(input({
    reviews: [
      review('code-review', 'smith', [{
        id: 'F-WAIVE',
        policy: 'required',
        summary: 'Candidate-authored waiver.',
        waiver: { actor: 'attacker', rationale: 'Self-authorized.' },
      }]),
      review('security-review', 'seraph'),
      review('ponytail-review', 'ponytail-reviewer'),
    ],
  }), options), /not authorized/u);
  assert.throws(() => runtime.ledger.materializeLedger(input({
    validations: [evidence(1)],
  }), options), /did not pass/u);
});

test('installed runtime rejects policy bytes that no longer match recorded digests', async () => {
  const trustedDirectory = await mkdtemp(join(tmpdir(), 'agentweaver-trusted-admission-'));
  const installed = await installRuntime(trustedDirectory);
  await writeFile(join(trustedDirectory, files[0]), 'export const REQUIRED_REVIEW_SOURCES=[];\n');
  const launcher = await import(`${pathToFileURL(installed.launcher).href}?tampered`);
  await assert.rejects(() => launcher.loadTrustedRuntime(installed.manifestPath), /digest does not match/u);
});
