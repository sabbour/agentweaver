#!/usr/bin/env node
// Converts the built-in workflow definitions in
// packages/Agentweaver.Squad/Catalog/Resources/workflows/*.yaml into
// docs/diagrams/src/workflow-<id>.json graph-specs, so the shipped workflows
// are documented by the same renderer as every other diagram and cannot drift
// from the YAML that actually drives them.
//
// Run with: node scripts/docs/workflows-to-graphspec.mjs
// Then re-render: npm run docs:render-diagrams

import { readdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse } from 'yaml';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const workflowsDir = path.join(
  repoRoot,
  'packages',
  'Agentweaver.Squad',
  'Catalog',
  'Resources',
  'workflows',
);
const specsDir = path.join(repoRoot, 'docs', 'diagrams', 'src');

// Node `type`/`kind` from the workflow schema mapped onto the graph-spec's
// icon + badge vocabulary. Everything a workflow can declare is covered, with
// `type` taking precedence over the coarser `kind`.
const BY_TYPE = {
  prompt: { icon: 'box', badge: { text: 'Step', tone: 'neutral' } },
  peer_review: { icon: 'branch', badge: { text: 'Peer review', tone: 'marigold' } },
  build_test: { icon: 'server', badge: { text: 'Build & test', tone: 'teal' } },
  check: { icon: 'branch', badge: { text: 'Gate', tone: 'marigold' } },
  terminal: { icon: 'window', badge: { text: 'Terminal', tone: 'green' } },
  parallel: { icon: 'route', badge: { text: 'Parallel', tone: 'lavender' } },
};

const GATE_LABEL = {
  rai: 'RAI gate',
  rubberduck: 'Rubberduck gate',
  'human-review': 'Human gate',
};

const DOCUMENTED_RETURN_JOINS = {
  // Review-driven rework cycles share the RAI decision's downstream
  // continuation in the workflow diagram. The runtime target remains
  // Implement; this metadata only supplies a clear visual return junction.
  'software-delivery': {
    revise: 'rai-check',
    'request-changes': 'rai-check',
  },
};

function documentedReturnJoin(workflowId, edge, isLoopback) {
  if (!isLoopback) return undefined;
  return DOCUMENTED_RETURN_JOINS[workflowId]?.[edge.when];
}

function describe(node) {
  const base = BY_TYPE[node.type] ?? { icon: 'box', badge: { text: 'Step', tone: 'neutral' } };
  if (node.type === 'check' && node.gate_kind) {
    return {
      icon: base.icon,
      badge: { text: GATE_LABEL[node.gate_kind] ?? 'Gate', tone: 'marigold' },
    };
  }
  // A terminal that ends the run cleanly reads differently from one that stops
  // it, and the distinction is the whole point of having several terminals.
  if (node.type === 'terminal') {
    const stops = /fail|declin|reject|abort/i.test(`${node.id} ${node.label ?? ''}`);
    return {
      icon: 'window',
      badge: { text: stops ? 'Stopped' : 'Done', tone: stops ? 'marigold' : 'green' },
    };
  }
  return base;
}

export function toSpec(wf) {
  const nodeOrder = new Map((wf.nodes ?? []).map((node, index) => [node.id, index]));
  const nodes = (wf.nodes ?? []).map((n) => {
    const { icon, badge } = describe(n);
    const subLabel = [n.agent, n.role && n.role !== 'plumbing' ? n.role : null]
      .filter(Boolean)
      .join(' · ');
    return {
      id: n.id,
      label: n.label ?? n.id,
      ...(subLabel ? { subLabel } : {}),
      icon,
      badge,
    };
  });

  const edges = (wf.edges ?? []).map((e) => {
    const isLoopback = (nodeOrder.get(e.to) ?? 0) <= (nodeOrder.get(e.from) ?? 0);
    const returnJoin = documentedReturnJoin(wf.id, e, isLoopback);
    return {
      from: e.from,
      to: e.to,
      ...(e.when ? { label: e.when } : {}),
      ...(isLoopback ? { loopback: true } : {}),
      ...(returnJoin ? { returnJoin } : {}),
    };
  });

  const title = `${wf.name} workflow`;
  return {
    $schema: './graph-spec.schema.json',
    title,
    alt: `${title}: ${nodes.map((n) => n.label).join(', ')}.`,
    direction: 'TB',
    nodes,
    edges,
  };
}

async function main() {
  const files = (await readdir(workflowsDir)).filter((f) => f.endsWith('.yaml')).sort();
  for (const file of files) {
    const wf = parse(await readFile(path.join(workflowsDir, file), 'utf8'));
    if (!wf?.id || !Array.isArray(wf.nodes)) {
      console.warn(`Skipping ${file}: no id/nodes`);
      continue;
    }
    const out = path.join(specsDir, `workflow-${wf.id}.json`);
    await writeFile(out, `${JSON.stringify(toSpec(wf), null, 2)}\n`);
    console.log(`Wrote ${path.basename(out)} (${wf.nodes.length} nodes, ${wf.edges?.length ?? 0} edges)`);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((err) => {
    console.error(err);
    process.exitCode = 1;
  });
}
