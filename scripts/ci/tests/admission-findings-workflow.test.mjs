import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const workflow = await readFile(path.join(here, '..', '..', '..', '.github', 'workflows', 'admission-findings.yml'), 'utf8');
const agent = await readFile(path.join(here, '..', '..', '..', '.github', 'agents', 'squad.agent.md'), 'utf8');
const gitWorkflow = await readFile(path.join(here, '..', '..', '..', '.github', 'skills', 'git-workflow', 'SKILL.md'), 'utf8');
const stackWorkflow = await readFile(path.join(here, '..', '..', '..', '.github', 'skills', 'gh-stack-parallel-work', 'SKILL.md'), 'utf8');

test('admission runs trusted base-ref code without PR checkout or write permissions', () => {
  assert.match(workflow, /pull_request_target:/);
  assert.match(workflow, /workflow_dispatch:/);
  assert.match(workflow, /ref: dev/);
  assert.doesNotMatch(workflow, /pull_request\.head\.sha|ref: \$\{\{ github\.event\.pull_request\.head/u);
  assert.match(workflow, /permissions:[\s\S]*pull-requests: read[\s\S]*issues: read/u);
  assert.match(workflow, /admission-findings-gate\.mjs github/);
  assert.match(workflow, /ADMISSION_FINDINGS_AUTHORIZED_REVIEWERS/);
  assert.match(workflow, /ADMISSION_FINDINGS_ADMISSION_OWNERS/);
  assert.doesNotMatch(workflow, /github\.event\.pull_request\.draft == false/);
});

test('active Squad instructions allow only manual squash admission and terminal cohort draining', () => {
  const contract = [agent, gitWorkflow, stackWorkflow].join('\n');
  assert.match(contract, /gh pr merge <number> --squash/);
  assert.doesNotMatch(contract, /gh pr merge <[^>]+> --(?:auto|rebase|merge)/);
  assert.match(agent, /Confirmed Merged or Owned\s+blocker/);
});
