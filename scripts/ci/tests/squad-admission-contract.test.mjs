import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync, readFileSync } from 'node:fs';

const root = new URL('../../../', import.meta.url);
const read = (path) => readFileSync(new URL(path, root), 'utf8');

test('admission is external-state owned and squash-only', () => {
  assert.equal(existsSync(new URL('.github/workflows/admission-findings.yml', root)), false);
  const contract = [read('CONTRIBUTING.md'), read('.github/agents/squad.agent.md'), read('.github/skills/git-workflow/SKILL.md'), read('.github/skills/gh-stack-parallel-work/SKILL.md')].join('\n');
  const validator = read('scripts/ci/squad-admission-preflight.mjs');
  const packageJson = JSON.parse(read('package.json'));
  assert.match(contract, /Squad\/Ralph external-state preflight owns admission/u);
  assert.match(contract, /Coordinator\/Ralph process and authoritative external Squad state are trusted\s+operational components/u);
  assert.match(contract, /gh pr merge <number> --squash --match-head-commit <validated-sha>/u);
  assert.equal(packageJson.devDependencies['@bradygaster/squad-sdk'], '0.13.1');
  assert.equal(existsSync(new URL('scripts/ci/squad-admission-launcher.mjs', root)), false);
  assert.match(contract, /not a tamper-proof sandbox/u);
  assert.match(contract, /does not provide an adversarially immutable execution boundary/u);
  assert.doesNotMatch(validator, /Active squad:\s*external/u);
  assert.doesNotMatch(contract, /npm run squad:admission-preflight/u);
  assert.doesNotMatch(contract, /\b(?:rebase(?:-| )?merge|auto(?:-| )?merge)\b|gh\s+pr\s+merge\b[^\r\n]*\b(?:--rebase|--auto)\b/iu);
});
