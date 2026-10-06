import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { findMissingDocumentation, inspectMarkdownPage } from './check-docs.mjs';

test('checks local page anchors and static assets', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-docs-'));
  try {
    await mkdir(path.join(root, 'docs', 'guide'), { recursive: true });
    await mkdir(path.join(root, 'docs', 'public', 'diagrams'), { recursive: true });
    await writeFile(path.join(root, 'docs', 'index.md'), '# Home\n\n[Build](./guide/build#steps)\n![Figure](/agentweaver/v1/diagrams/figure.png)\n<a :href="\'/agentweaver/v1/diagrams/figure.drawio\'">Editable</a>\n');
    await writeFile(path.join(root, 'docs', 'guide', 'build.md'), '# Build\n\n## Steps\n');
    await writeFile(path.join(root, 'docs', 'public', 'diagrams', 'figure.png'), 'image');
    await writeFile(path.join(root, 'docs', 'public', 'diagrams', 'figure.drawio'), '<mxfile/>');
    const errors = await inspectMarkdownPage(
      'docs/index.md',
      await readFile(path.join(root, 'docs', 'index.md'), 'utf8'),
      root,
    );
    assert.deepEqual(errors, []);
    assert.deepEqual(
      await inspectMarkdownPage('docs/index.md', '<a :href="\'/agentweaver/v1/diagrams/missing.drawio\'">Missing</a>', root),
      ['docs/index.md: missing local target /agentweaver/v1/diagrams/missing.drawio'],
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('reports missing local anchors and files', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-docs-'));
  try {
    await mkdir(path.join(root, 'docs'), { recursive: true });
    await writeFile(path.join(root, 'docs', 'index.md'), '# Home\n\n[Missing](./absent#lost)\n');
    const errors = await inspectMarkdownPage('docs/index.md', '# Home\n\n[Missing](./absent#lost)\n', root);
    assert.deepEqual(errors, ['docs/index.md: missing local target ./absent#lost']);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('warns when implementation paths change without mapped documentation', () => {
  const rules = [{
    name: 'providers',
    sourcePrefixes: ['packages/Agentweaver.Providers/'],
    required: ['docs/architecture/providers-models.md', 'docs/diagrams/src/flagship/v1-provider-resolution.json'],
  }];
  assert.deepEqual(
    findMissingDocumentation(['packages/Agentweaver.Providers/ProviderResolver.cs'], rules),
    [{
      rule: 'providers',
      affected: ['packages/Agentweaver.Providers/ProviderResolver.cs'],
      missing: ['docs/architecture/providers-models.md', 'docs/diagrams/src/flagship/v1-provider-resolution.json'],
    }],
  );
  assert.deepEqual(
    findMissingDocumentation([
      'packages/Agentweaver.Providers/ProviderResolver.cs',
      'docs/architecture/providers-models.md',
      'docs/diagrams/src/flagship/v1-provider-resolution.json',
    ], rules),
    [],
  );
});

test('maps Orchestrator Core changes to its architecture and test documentation', () => {
  const sourceMap = JSON.parse(
    readFileSync(new URL('../../docs/docs-source-map.json', import.meta.url), 'utf8'),
  );
  const rule = sourceMap.rules.find(({ name }) => name === 'Orchestrator workflow catalogs');
  assert.ok(rule);
  assert.deepEqual(
    findMissingDocumentation(
      ['services/orchestrator/Agentweaver.Orchestrator.Core/WorkflowPlan.cs'],
      [rule],
    ),
    [{
      rule: 'Orchestrator workflow catalogs',
      affected: ['services/orchestrator/Agentweaver.Orchestrator.Core/WorkflowPlan.cs'],
      missing: rule.required,
    }],
  );
  assert.deepEqual(
    findMissingDocumentation([
      'services/orchestrator/Agentweaver.Orchestrator.Core/WorkflowPlan.cs',
      ...rule.required,
    ], [rule]),
    [],
  );
});
