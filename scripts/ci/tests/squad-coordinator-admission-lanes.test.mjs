import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';

const root = new URL('../../../', import.meta.url);
const policy = readFileSync(new URL('.github/agents/squad.agent.md', root), 'utf8');

test('coordinator separates parallel implementation from sequential admission', () => {
  assert.match(policy, /at most five independent implementation streams/u);
  assert.match(policy, /exactly one queue-head draft PR is the admission candidate/u);
  assert.match(policy, /Only\s+this candidate may receive the final queue-head rebase, final CI waiting or diagnosis,\s+exact-head reviews, findings-ledger work, readiness, merge, and post-merge cleanup\./u);
  assert.match(policy, /A draft that is not the admission candidate remains untouched after other PRs\s+merge: do not rebase it, wait for or investigate final CI, run final reviews, create\s+or update a findings ledger, mark it ready, merge it, or clean up its worktree\./u);
  assert.match(policy, /After that final queue-head rebase, run\s+the risk-based required review set at its exact head/u);
  assert.match(policy, /allow one bounded correction\s+on this candidate.*re-review only the stated\s+finding at the corrected exact head/su);
  assert.match(policy, /A routine, known\s+issue fix uses Standard mode/u);
  assert.match(policy, /Scribe, status reporting, and common-gotchas maintenance are asynchronous support work:\s+they do not consume an implementation-lane slot and never block admission\./u);
});
