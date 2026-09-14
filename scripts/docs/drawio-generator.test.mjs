import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  DESIGN_TOKENS,
  graphSpecToDrawio,
  nativeShapeStyle,
  sequenceSpecToDrawio,
} from './drawio-generator.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');

test('design-system manifest matches generator tokens and reusable library', async () => {
  const manifest = JSON.parse(await readFile(path.join(repoRoot, 'docs', 'diagrams', 'drawio', 'design-system.json'), 'utf8'));
  const library = await readFile(path.join(repoRoot, 'docs', 'diagrams', 'drawio', 'fluent-library.xml'), 'utf8');
  assert.equal(manifest.cards.widthPx, DESIGN_TOKENS.geometry.cardWidth);
  assert.equal(manifest.cards.semanticAccentPx, 5);
  assert.equal(manifest.palette.surface, DESIGN_TOKENS.colors.surface);
  assert.equal(manifest.badges.marigold.foreground, DESIGN_TOKENS.badges.marigold.foreground);
  assert.match(library, /width=\\&quot;340\\&quot;/);
  assert.match(library, /x=\\&quot;5\\&quot;/);
  assert.match(library, /shape=mxgraph\.azure2/);
  assert.match(library, /shape=mxgraph\.kubernetes/);
});

test('selects draw.io native libraries semantically and explicitly', () => {
  assert.equal(nativeShapeStyle({ id: 'aks-pod', label: 'Kubernetes pod', icon: 'bot' }).library, 'kubernetes');
  assert.equal(nativeShapeStyle({ id: 'store', label: 'Postgres database', icon: 'database' }).library, 'database');
  assert.equal(nativeShapeStyle({ id: 'gateway', label: 'Network gateway', icon: 'route' }).library, 'networking');
  assert.equal(nativeShapeStyle({ id: 'custom', label: 'Custom', icon: 'box', library: 'bpmn' }).library, 'bpmn');
});

test('generates editable graph XML with the full visual and connector contract', () => {
  const output = graphSpecToDrawio({
    title: 'Graph',
    groups: [{ id: 'g', label: 'Control plane', tier: 1 }],
    nodes: [
      { id: 'a', label: 'API', subLabel: 'control plane', meta: 'http:5000', icon: 'server', badge: { text: 'Service', tone: 'green' }, group: 'g' },
      { id: 'b', label: 'Worker', icon: 'bot', badge: { text: 'Runtime', tone: 'lavender' }, group: 'g' },
      { id: 'c', label: 'Store', icon: 'database', badge: { text: 'Data', tone: 'teal' }, group: 'g' },
    ],
    edges: [
      { from: 'a', to: 'b', label: 'dispatch' },
      { from: 'a', to: 'c' },
      { from: 'b', to: 'a', label: 'revise', loopback: true },
    ],
  }, { name: 'graph' });

  assert.match(output, /compressed="false"/);
  assert.match(output, /width="340"/);
  assert.match(output, /x="5"/);
  assert.match(output, /font-size:20px/);
  assert.match(output, /font-size:16px/);
  assert.match(output, /font-size:12px/);
  assert.match(output, /fillColor=#0e700e/);
  assert.match(output, /edgeStyle=none/);
  assert.match(output, /rounded=1/);
  assert.match(output, /labelBackgroundColor=#fdfbf8/);
  assert.match(output, /jumpStyle=arc/);
  assert.match(output, /strokeColor=#d39300/);
  assert.match(output, /dashPattern=8 6/);
  assert.doesNotMatch(output, /id="split-a"/, 'Different source ports must not acquire an invented common junction');
  assert.doesNotMatch(output, /native-libraries:/);
});

test('generates native UML-style sequence XML with messages, notes, fragments, and activation bars', () => {
  const output = sequenceSpecToDrawio({
    kind: 'sequence',
    title: 'Sequence',
    autonumber: true,
    participants: [
      { id: 'User', label: 'User', icon: 'globe', badge: { text: 'Actor', tone: 'lavender' } },
      { id: 'API', label: 'API', icon: 'server', badge: { text: 'Service', tone: 'green' } },
    ],
    steps: [
      { type: 'activation', participant: 'API', action: 'start' },
      { type: 'message', from: 'User', to: 'API', label: 'start', line: 'solid', arrow: 'filled' },
      { type: 'note', over: ['API'], label: 'durable' },
      { type: 'fragment', operator: 'alt', label: 'ready', sections: [{ steps: [{ type: 'message', from: 'API', to: 'User', label: 'done', line: 'dashed', arrow: 'open' }] }] },
      { type: 'activation', participant: 'API', action: 'end' },
    ],
  }, { name: 'sequence' });

  assert.match(output, /shape=mxgraph\.uml\.frame/);
  assert.match(output, /shape=note/);
  assert.match(output, /id="activation-/);
  assert.match(output, /id="message-1-from-anchor"/);
  assert.match(output, /id="message-1-to-anchor"/);
  assert.match(output, /source="message-1-from-anchor" target="message-1-to-anchor"/);
  assert.match(output, /1\. start/);
  assert.match(output, /dashed=1/);
  assert.doesNotMatch(output, /native-libraries:/);
});

test('preserves nested group parentage and relative geometry', () => {
  const output = graphSpecToDrawio({
    title: 'Nested',
    groups: [
      { id: 'outer', label: 'Outer', tier: 1 },
      { id: 'inner', label: 'Inner', tier: 2, parent: 'outer' },
    ],
    nodes: [
      { id: 'a', label: 'A', icon: 'box', badge: { text: 'Step', tone: 'neutral' }, group: 'inner' },
    ],
    edges: [],
  });
  assert.match(output, /id="group-inner"[\s\S]*parent="group-outer"/);
  assert.match(output, /id="node-a"[\s\S]*parent="group-inner"/);
  assert.throws(
    () => graphSpecToDrawio({
      nodes: [{ id: 'a', label: 'A', icon: 'box', badge: { text: 'Step', tone: 'neutral' }, group: 'missing' }],
      edges: [],
      groups: [],
    }),
    /unknown group/,
  );
});

test('rejects ambiguous graph and sequence references', () => {
  assert.throws(
    () => graphSpecToDrawio({ nodes: [{ id: 'a', label: 'A', icon: 'box', badge: { text: 'Step', tone: 'neutral' } }], edges: [{ from: 'a', to: 'missing' }] }),
    /unknown node/,
  );
  assert.throws(
    () => sequenceSpecToDrawio({ kind: 'sequence', participants: [{ id: 'a' }], steps: [{ type: 'message', from: 'a', to: 'missing', label: 'x' }] }),
    /unknown participant/,
  );
});
