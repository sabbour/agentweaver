import assert from 'node:assert/strict';
import test from 'node:test';
import { runValidationEvidence } from '../squad-validation-evidence.mjs';

const worktree = 'C:\\src\\agentweaver\\.worktrees\\issue-1502';
const branch = 'squad/1502-evidence-admission';
const headSha = 'a'.repeat(40);

function runner({ afterHead = headSha, commandExitCode = 0, beforeStatus = '', afterStatus = '' } = {}) {
  let headReads = 0;
  let statusReads = 0;
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
      if (argv[1] === 'status') {
        statusReads += 1;
        return { exitCode: 0, stdout: statusReads === 1 ? beforeStatus : afterStatus, stderr: '' };
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

test('rejects dirty contents before execution and changes made during execution', async () => {
  for (const [fake, expectedCommands] of [
    [runner({ beforeStatus: ' M tracked.mjs' }), 0],
    [runner({ afterStatus: '?? generated.txt' }), 1],
  ]) {
    await assert.rejects(() => runValidationEvidence({
      argv: ['node', '--test', 'focused.test.mjs'],
      cwd: worktree,
      expectedWorktree: worktree,
      expectedBranch: branch,
      expectedHeadSha: headSha,
    }, { run: fake.run, realpath: async (path) => path }), /worktree is not clean/u);
    assert.equal(fake.validationCommands.length, expectedCommands);
  }
});
