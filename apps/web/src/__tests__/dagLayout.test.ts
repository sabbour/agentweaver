import { layoutDagBalancedGrid, layoutDagColumns, NODE_H, NODE_W } from '../utils/dagLayout';
import { describe, expect, it } from 'vitest';
import type { NodeSizeHint } from '../utils/dagLayout';
import type { Edge, Node } from '@xyflow/react';
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

function makeEdge(source: string, target: string): Edge {
  return { id: `${source}-${target}`, source, target };
}

function centerY(node: Node, height = NODE_H): number {
  return node.position.y + height / 2;
}

function rounded(value: number): number {
  return Math.round(value);
}

function coordinatorWithSubtasks(count: number): { nodes: Node[]; edges: Edge[]; hints: Record<string, NodeSizeHint> } {
  const subtaskIds = Array.from({ length: count }, (_, index) => `subtask-${index + 1}`);
  const nodeIds = ['coordinator', 'outcome', 'work', ...subtaskIds, 'rai', 'review', 'merge', 'scribe'];
  const nodes = nodeIds.map(makeNode);
  const edges = [
    makeEdge('coordinator', 'outcome'),
    makeEdge('outcome', 'work'),
    ...subtaskIds.map((id) => makeEdge('work', id)),
    ...subtaskIds.map((id) => makeEdge(id, 'rai')),
    makeEdge('rai', 'review'),
    makeEdge('review', 'merge'),
    makeEdge('merge', 'scribe'),
  ];
  const hints = Object.fromEntries(
    nodeIds.map((id) => [id, { width: id.startsWith('subtask') ? 220 : 180, height: id.startsWith('subtask') ? 180 : 130 }]),
  );
  return { nodes, edges, hints };
}

describe('layoutDagBalancedGrid', () => {
  it('wraps 5+ subtasks into multiple rows and columns', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(5);

    const laid = layoutDagBalancedGrid(nodes, edges, { viewportWidth: 900, rankSep: 48, nodeSep: 56 }, hints);
    const byId = new Map(laid.map((n) => [n.id, n]));
    const subtasks = ['subtask-1', 'subtask-2', 'subtask-3', 'subtask-4', 'subtask-5'].map((id) => byId.get(id)!);
    const subtaskRows = new Set(subtasks.map((node) => rounded(centerY(node, hints[node.id].height))));
    const subtaskCols = new Set(subtasks.map((node) => rounded(centerX(node, hints[node.id].width))));

    expect(subtaskRows.size).toBeGreaterThan(1);
    expect(subtaskCols.size).toBeGreaterThan(1);
    expect(subtaskCols.size).toBeLessThanOrEqual(3);
  });

  it('reduces to one column for narrow inspector widths', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(3);

    const laid = layoutDagBalancedGrid(nodes, edges, { viewportWidth: 360, rankSep: 48, nodeSep: 56 }, hints);
    const centers = laid.map((node) => rounded(centerX(node, hints[node.id].width)));

    expect(Math.max(...centers) - Math.min(...centers)).toBeLessThanOrEqual(1);
  });

  it('caps height-driven balancing to columns that fit the inspector width', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(5);
    const viewportWidth = 700;
    const viewportHeight = 600;
    const nodeSep = 56;
    const margin = 24;
    const maxNodeWidth = Math.max(...Object.values(hints).map((hint) => hint.width));
    const widthSafeColumns = Math.max(1, Math.floor((viewportWidth - margin * 2 + nodeSep) / (maxNodeWidth + nodeSep)));

    const laid = layoutDagBalancedGrid(nodes, edges, { viewportWidth, viewportHeight, rankSep: 48, nodeSep, maxColumns: 5 }, hints);
    const occupiedColumns = new Set(laid.map((node) => rounded(centerX(node, hints[node.id].width))));
    const contentRight = Math.max(...laid.map((node) => node.position.x + hints[node.id].width));

    expect(widthSafeColumns).toBe(2);
    expect(occupiedColumns.size).toBeLessThanOrEqual(widthSafeColumns);
    expect(contentRight + margin).toBeLessThanOrEqual(viewportWidth);
  });

  it('packs the assembly tail across columns after fan-in instead of one node per row', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(3);

    const laid = layoutDagBalancedGrid(nodes, edges, { viewportWidth: 900, rankSep: 48, nodeSep: 56 }, hints);
    const byId = new Map(laid.map((n) => [n.id, n]));
    const tail = ['rai', 'review', 'merge', 'scribe'].map((id) => byId.get(id)!);
    const tailRows = new Set(tail.map((node) => rounded(centerY(node, hints[node.id].height))));

    expect(tailRows.size).toBeLessThan(tail.length);
    expect(rounded(centerY(byId.get('rai')!, hints.rai.height))).toBe(rounded(centerY(byId.get('review')!, hints.review.height)));
    expect(rounded(centerY(byId.get('merge')!, hints.merge.height))).toBe(rounded(centerY(byId.get('scribe')!, hints.scribe.height)));
  });

  it('keeps stable positions with no node overlap', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(5);

    const first = layoutDagBalancedGrid(nodes, edges, { viewportWidth: 900, rankSep: 48, nodeSep: 56 }, hints);
    const second = layoutDagBalancedGrid(nodes, edges, { viewportWidth: 900, rankSep: 48, nodeSep: 56 }, hints);

    expect(second.map((node) => [node.id, node.position])).toEqual(first.map((node) => [node.id, node.position]));
    for (let i = 0; i < first.length; i += 1) {
      for (let j = i + 1; j < first.length; j += 1) {
        const a = first[i];
        const b = first[j];
        const ah = hints[a.id];
        const bh = hints[b.id];
        const separated =
          a.position.x + ah.width <= b.position.x ||
          b.position.x + bh.width <= a.position.x ||
          a.position.y + ah.height <= b.position.y ||
          b.position.y + bh.height <= a.position.y;
        expect(separated).toBe(true);
      }
    }
  });
});
