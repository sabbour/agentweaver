import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync, readFileSync } from 'node:fs';

const root = new URL('../../../', import.meta.url);
const read = (path) => readFileSync(new URL(path, root), 'utf8');

test('admission remains local and squash-only', () => {
  assert.equal(existsSync(new URL('.github/workflows/admission-findings.yml', root)), false);
  const contract = [read('CONTRIBUTING.md'), read('.github/agents/squad.agent.md'), read('.github/skills/git-workflow/SKILL.md'), read('.github/skills/gh-stack-parallel-work/SKILL.md')].join('\n');
  assert.match(contract, /squad:admission-preflight/u);
  assert.match(contract, /gh pr merge <number> --squash/u);
  assert.doesNotMatch(contract, /gh pr merge <number> --rebase --auto/u);
});
