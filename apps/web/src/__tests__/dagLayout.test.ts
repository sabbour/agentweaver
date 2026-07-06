import { describe, it, expect } from 'vitest';
import type { Edge, Node } from '@xyflow/react';
import { layoutDagColumns, NODE_W } from '../utils/dagLayout';

function makeNode(id: string): Node {
  return { id, position: { x: 0, y: 0 }, data: {} };
}

function centerX(node: Node, width = NODE_W): number {
  return node.position.x + width / 2;
}

describe('layoutDagColumns TB centering', () => {
  it('centres a single-node spine rank over a multi-node fan-out rank on a shared axis', () => {
    // coordinator → {a, b, c} fan-out
    const nodes: Node[] = [
      makeNode('coordinator'),
      makeNode('a'),
      makeNode('b'),
      makeNode('c'),
    ];
    const edges: Edge[] = [
      { id: 'e1', source: 'coordinator', target: 'a' },
      { id: 'e2', source: 'coordinator', target: 'b' },
      { id: 'e3', source: 'coordinator', target: 'c' },
    ];

    const laid = layoutDagColumns(nodes, edges, { rankdir: 'TB' });
    const byId = new Map(laid.map((n) => [n.id, n]));

    const coord = byId.get('coordinator')!;
    const a = byId.get('a')!;
    const c = byId.get('c')!;

    // The fan-out row's centre is the midpoint between its first and last cards.
    const fanCenter = (centerX(a) + centerX(c)) / 2;
    // The single spine node must sit on the same centre axis.
    expect(Math.abs(centerX(coord) - fanCenter)).toBeLessThanOrEqual(1);
  });

  it('keeps a linear spine (coordinator→outcome→work) vertically aligned', () => {
    const nodes: Node[] = [
      makeNode('coordinator'),
      makeNode('outcome'),
      makeNode('work'),
    ];
    const edges: Edge[] = [
      { id: 'e1', source: 'coordinator', target: 'outcome' },
      { id: 'e2', source: 'outcome', target: 'work' },
    ];

    const laid = layoutDagColumns(nodes, edges, { rankdir: 'TB' });
    const xs = laid.map((n) => centerX(n));
    // All three single-node ranks share one centre X.
    expect(Math.max(...xs) - Math.min(...xs)).toBeLessThanOrEqual(1);
  });

  it('never produces negative coordinates', () => {
    const nodes: Node[] = [makeNode('root'), makeNode('a'), makeNode('b')];
    const edges: Edge[] = [
      { id: 'e1', source: 'root', target: 'a' },
      { id: 'e2', source: 'root', target: 'b' },
    ];
    const laid = layoutDagColumns(nodes, edges, { rankdir: 'TB' });
    for (const n of laid) {
      expect(n.position.x).toBeGreaterThanOrEqual(0);
      expect(n.position.y).toBeGreaterThanOrEqual(0);
    }
  });
});
