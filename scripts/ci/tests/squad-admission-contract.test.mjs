import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync, readFileSync } from 'node:fs';

const root = new URL('../../../', import.meta.url);
const read = (path) => readFileSync(new URL(path, root), 'utf8');

test('admission is external-state owned and squash-only', () => {
  assert.equal(existsSync(new URL('.github/workflows/admission-findings.yml', root)), false);
  const contract = [read('CONTRIBUTING.md'), read('.github/agents/squad.agent.md'), read('.github/skills/git-workflow/SKILL.md'), read('.github/skills/gh-stack-parallel-work/SKILL.md')].join('\n');
  const validator = read('scripts/ci/squad-admission-preflight.mjs');
  const launcher = read('scripts/ci/squad-admission-launcher.mjs');
  const packageJson = JSON.parse(read('package.json'));
  assert.match(contract, /Squad\/Ralph external-state preflight owns admission/u);
  assert.match(contract, /git show origin\/dev:scripts\/ci\/squad-admission-preflight\.mjs/u);
  assert.match(contract, /git show origin\/dev:scripts\/ci\/squad-admission-launcher\.mjs \| node --input-type=module -/u);
  assert.match(contract, /Bootstrap exception — \*\*#1490 only\*\*/u);
  assert.match(contract, /cannot validate its own introduction/u);
  assert.match(contract, /gh pr merge <number> --squash --match-head-commit <validated-sha>/u);
  assert.equal(packageJson.devDependencies['@bradygaster/squad-cli'], '0.13.1');
  assert.equal(packageJson.devDependencies['@bradygaster/squad-sdk'], '0.13.1');
  assert.match(launcher, /resolveCanonicalExternalStateDir/u);
  assert.match(launcher, /STATE_CONFIG_PATH/u);
  assert.match(validator, /readAuthoritativeLedger/u);
  assert.match(launcher, /mkdtemp/u);
  assert.match(launcher, /git', \['show'/u);
  assert.match(launcher, /trusted validator bytes do not match origin\/dev blob/u);
  assert.doesNotMatch(validator, /@bradygaster\/squad-sdk|state-mcp|from ['"](?:[^n]|n[^o]|no[^d]|nod[^e])[^'"]*['"]/u);
  assert.doesNotMatch(validator, /Active squad:\s*external/u);
  assert.doesNotMatch(contract, /npm run squad:admission-preflight/u);
  assert.doesNotMatch(contract, /\b(?:rebase(?:-| )?merge|auto(?:-| )?merge)\b|gh\s+pr\s+merge\b[^\r\n]*\b(?:--rebase|--auto)\b/iu);
});
