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
const contributing = await readFile(path.join(here, '..', '..', '..', 'CONTRIBUTING.md'), 'utf8');
const protection = await readFile(path.join(here, '..', '..', '..', '.github', 'dev-branch-protection.md'), 'utf8');

test('admission runs trusted base-ref code without PR checkout or write permissions', () => {
  assert.match(workflow, /pull_request_target:/);
  assert.match(workflow, /workflow_dispatch:/);
  assert.match(workflow, /ref: dev/);
  assert.doesNotMatch(workflow, /pull_request\.head\.sha|ref: \$\{\{ github\.event\.pull_request\.head/u);
  assert.match(workflow, /permissions:[\s\S]*pull-requests: read[\s\S]*issues: read/u);
  assert.match(workflow, /admission-findings-gate\.mjs github/);
  assert.match(workflow, /vars\.ADMISSION_FINDINGS_AUTHORIZED_REVIEWERS/);
  assert.match(workflow, /vars\.ADMISSION_FINDINGS_ADMISSION_OWNERS/);
  assert.doesNotMatch(workflow, /github\.event\.pull_request\.draft == false/);
});

test('active Squad instructions allow only manual squash admission and terminal cohort draining', () => {
  const contract = [agent, gitWorkflow, stackWorkflow, contributing].join('\n');
  assert.match(contract, /gh pr merge <number> --squash/);
  assert.doesNotMatch(contract, /gh pr merge <[^>]+> --(?:auto|rebase|merge)/);
  assert.doesNotMatch(contributing, /rebase[- ]merge|auto-merge/u);
  assert.match(agent, /Confirmed Merged or Owned\s+blocker/);
});

test('documents the one-time trusted-workflow bootstrap before ruleset enforcement', () => {
  assert.match(protection, /one bootstrap sequence/u);
  assert.match(protection, /origin\/dev.*trusted workflow|trusted workflow.*origin\/dev/u);
  assert.match(protection, /disposable test PR/u);
  assert.match(protection, /GitHub returned only direct collaborator `sabbour`/u);
  assert.match(protection, /permits `merge`, `squash`, and `rebase`/u);
  assert.match(protection, /allowed merge methods to `squash`/u);
});
