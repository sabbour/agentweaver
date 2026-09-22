import assert from 'node:assert/strict';
import test from 'node:test';
import { runValidationEvidence } from '../squad-validation-evidence.mjs';

const worktree = 'C:\\src\\agentweaver\\.worktrees\\issue-1502';
const branch = 'squad/1502-evidence-admission';
const headSha = 'a'.repeat(40);

function runner({ afterHead = headSha, commandExitCode = 0 } = {}) {
  let headReads = 0;
  const validationCommands = [];
  return {
    validationCommands,
    run: async (argv) => {
      if (argv[0] !== 'git') {
        validationCommands.push(argv);
        return { exitCode: commandExitCode, signal: null, stdout: 'ok', stderr: '' };
      }
      if (argv[1] === 'rev-parse' && argv[2] === '--show-toplevel') return { exitCode: 0, stdout: worktree, stderr: '' };
      if (argv[1] === 'branch') return { exitCode: 0, stdout: branch, stderr: '' };
      if (argv[1] === 'rev-parse' && argv[2] === 'HEAD') {
        headReads += 1;
        return { exitCode: 0, stdout: headReads === 1 ? headSha : afterHead, stderr: '' };
      }
      throw new Error(`unexpected git argv: ${argv.join(' ')}`);
    },
  };
}

test('runs exactly one argv command when CWD, worktree, branch, and SHA match', async () => {
  const fake = runner();
  const evidence = await runValidationEvidence({
    argv: ['node', '--test', 'focused.test.mjs'],
    cwd: worktree,
    expectedWorktree: worktree,
    expectedBranch: branch,
    expectedHeadSha: headSha,
  }, { run: fake.run, realpath: async (path) => path });
  assert.deepEqual(fake.validationCommands, [['node', '--test', 'focused.test.mjs']]);
  assert.equal(evidence.result, 'passed');
  assert.deepEqual(evidence.argv, ['node', '--test', 'focused.test.mjs']);
});

test('fails closed before execution for wrong CWD, worktree, branch, or SHA', async () => {
  for (const override of [
    { cwd: 'C:\\src\\agentweaver' },
    { expectedWorktree: 'C:\\src\\other-worktree' },
    { expectedBranch: 'dev' },
    { expectedHeadSha: 'b'.repeat(40) },
  ]) {
    const fake = runner();
    await assert.rejects(() => runValidationEvidence({
      argv: ['node', '--test', 'focused.test.mjs'],
      cwd: worktree,
      expectedWorktree: worktree,
      expectedBranch: branch,
      expectedHeadSha: headSha,
      ...override,
    }, { run: fake.run, realpath: async (path) => path }), /does not match/u);
    assert.equal(fake.validationCommands.length, 0);
  }
});

test('rejects changed HEAD after execution and preserves mismatch evidence', async () => {
  const fake = runner({ afterHead: 'b'.repeat(40) });
  await assert.rejects(
    () => runValidationEvidence({
      argv: ['node', '--test', 'focused.test.mjs'],
      cwd: worktree,
      expectedWorktree: worktree,
      expectedBranch: branch,
      expectedHeadSha: headSha,
    }, { run: fake.run, realpath: async (path) => path }),
    (error) => error.message.includes('after execution HEAD') && error.evidence.result === 'provenance-mismatch',
  );
  assert.equal(fake.validationCommands.length, 1);
});

test('contains no directory switching or destructive repair commands', async () => {
  const source = await import('node:fs/promises').then(({ readFile }) => readFile(new URL('../squad-validation-evidence.mjs', import.meta.url), 'utf8'));
  assert.doesNotMatch(source, /\b(?:chdir|checkout|pull|reset|stash|clean|remove|delete)\b/iu);
});
