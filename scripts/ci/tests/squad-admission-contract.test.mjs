import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync, readFileSync } from 'node:fs';

const root = new URL('../../../', import.meta.url);
const read = (path) => readFileSync(new URL(path, root), 'utf8');

test('admission is external-state owned and squash-only', () => {
  assert.equal(existsSync(new URL('.github/workflows/admission-findings.yml', root)), false);
  const contract = [read('CONTRIBUTING.md'), read('.github/agents/squad.agent.md'), read('.github/skills/git-workflow/SKILL.md'), read('.github/skills/gh-stack-parallel-work/SKILL.md')].join('\n');
  assert.match(contract, /squad:admission-preflight/u);
  assert.match(contract, /Squad\/Ralph external-state preflight owns admission/u);
  assert.match(contract, /gh pr merge <number> --squash --match-head-commit <validated-sha>/u);
  assert.doesNotMatch(contract, /\b(?:rebase(?:-| )?merge|auto(?:-| )?merge)\b|gh\s+pr\s+merge\b[^\r\n]*\b(?:--rebase|--auto)\b/iu);
});
