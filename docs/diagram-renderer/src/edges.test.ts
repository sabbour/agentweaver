import { describe, expect, it } from 'vitest';
import {
  alignLoopbackToContinuation,
  buildBridgedPath,
  findConnectorBridges,
  findConnectorJunctions,
} from './edges';

type Point = { x: number; y: number };

const cardinalTeeCases: Array<{
  name: string;
  kind: 'split' | 'merge';
  continuation: Point[];
  branch: Point[];
  tee: Point;
}> = [
  {
    name: 'top',
    kind: 'split',
    continuation: [{ x: 0, y: 100 }, { x: 0, y: -100 }],
    branch: [{ x: 0, y: 100 }, { x: 0, y: 0 }, { x: -100, y: 0 }],
    tee: { x: 0, y: 0 },
  },
  {
    name: 'right',
    kind: 'split',
    continuation: [{ x: -100, y: 0 }, { x: 100, y: 0 }],
    branch: [{ x: -100, y: 0 }, { x: 0, y: 0 }, { x: 0, y: -100 }],
    tee: { x: 0, y: 0 },
  },
  {
    name: 'bottom',
    kind: 'split',
    continuation: [{ x: 0, y: -100 }, { x: 0, y: 100 }],
    branch: [{ x: 0, y: -100 }, { x: 0, y: 0 }, { x: 100, y: 0 }],
    tee: { x: 0, y: 0 },
  },
  {
    name: 'left',
    kind: 'split',
    continuation: [{ x: 100, y: 0 }, { x: -100, y: 0 }],
    branch: [{ x: 100, y: 0 }, { x: 0, y: 0 }, { x: 0, y: 100 }],
    tee: { x: 0, y: 0 },
  },
  {
    name: 'top',
    kind: 'merge',
    continuation: [{ x: 0, y: 100 }, { x: 0, y: -100 }],
    branch: [{ x: -100, y: 0 }, { x: 0, y: 0 }, { x: 0, y: -100 }],
    tee: { x: 0, y: 0 },
  },
  {
    name: 'right',
    kind: 'merge',
    continuation: [{ x: -100, y: 0 }, { x: 100, y: 0 }],
    branch: [{ x: 0, y: -100 }, { x: 0, y: 0 }, { x: 100, y: 0 }],
    tee: { x: 0, y: 0 },
  },
  {
    name: 'bottom',
    kind: 'merge',
    continuation: [{ x: 0, y: -100 }, { x: 0, y: 100 }],
    branch: [{ x: 100, y: 0 }, { x: 0, y: 0 }, { x: 0, y: 100 }],
    tee: { x: 0, y: 0 },
  },
  {
    name: 'left',
    kind: 'merge',
    continuation: [{ x: 100, y: 0 }, { x: -100, y: 0 }],
    branch: [{ x: 0, y: 100 }, { x: 0, y: 0 }, { x: -100, y: 0 }],
    tee: { x: 0, y: 0 },
  },
];

describe('diagram connector geometry', () => {
  it('interrupts a crossing path with an overpass arc instead of a background mask', () => {
    const routes = [
      { id: 'edge-a', points: [{ x: 0, y: 50 }, { x: 200, y: 50 }] },
      { id: 'edge-b', points: [{ x: 100, y: 0 }, { x: 100, y: 200 }] },
    ];
    const bridges = findConnectorBridges(routes);
    const edgeBBridges = bridges.get('edge-b') ?? [];

    expect(edgeBBridges).toEqual([{ x: 100, y: 50, orientation: 'vertical' }]);
    expect(buildBridgedPath(routes[1].points, edgeBBridges)).toBe(
      'M 100,0 L 100,43 A 7,7 0 0 1 100,57 L 100,200',
    );
  });

  it('keeps bridges 18px clear of rounded connector endpoints', () => {
    const nearCorner = findConnectorBridges([
      { id: 'horizontal', points: [{ x: 0, y: 50 }, { x: 200, y: 50 }] },
      { id: 'vertical', points: [{ x: 17, y: 0 }, { x: 17, y: 200 }] },
    ]);
    const clearOfCorner = findConnectorBridges([
      { id: 'horizontal', points: [{ x: 0, y: 50 }, { x: 200, y: 50 }] },
      { id: 'vertical', points: [{ x: 19, y: 0 }, { x: 19, y: 200 }] },
    ]);

    expect(nearCorner.has('vertical')).toBe(false);
    expect(clearOfCorner.get('vertical')).toEqual([{ x: 19, y: 50, orientation: 'vertical' }]);
  });

  it('emits one explicit junction only for an exact shared split origin', () => {
    const junctions = findConnectorJunctions([
      { id: 'edge-b', source: 'origin', target: 'right', points: [{ x: 10, y: 20 }, { x: 100, y: 20 }] },
      { id: 'edge-a', source: 'origin', target: 'down', points: [{ x: 10, y: 20 }, { x: 20, y: 100 }] },
    ]);

    expect(junctions.get('edge-a')).toEqual([{ x: 10, y: 20 }]);
    expect(junctions.has('edge-b')).toBe(false);
  });

  it('does not mark an ordinary arrowhead endpoint, including a shared card-entry target', () => {
    const junctions = findConnectorJunctions([
      { id: 'edge-a', source: 'left', target: 'terminal', points: [{ x: 0, y: 20 }, { x: 100, y: 20 }] },
      { id: 'edge-b', source: 'right', target: 'terminal', points: [{ x: 0, y: 80 }, { x: 100, y: 20 }] },
    ]);

    expect(junctions).toEqual(new Map());
  });

  it('marks an incoming merge at its shared trunk before the arrowhead', () => {
    const junctions = findConnectorJunctions([
      {
        id: 'edge-a',
        source: 'left',
        target: 'terminal',
        points: [{ x: 0, y: 0 }, { x: 100, y: 0 }, { x: 100, y: 100 }],
      },
      {
        id: 'edge-b',
        source: 'right',
        target: 'terminal',
        points: [{ x: 200, y: 20 }, { x: 100, y: 20 }, { x: 100, y: 100 }],
      },
    ]);

    expect(junctions.get('edge-b')).toEqual([{ x: 100, y: 20 }]);
    expect([...junctions.values()].flat()).not.toContainEqual({ x: 100, y: 100 });
  });

  it.each(cardinalTeeCases)('marks a $kind $name tee exactly once', ({ kind, continuation, branch, tee }) => {
    const routes = kind === 'split'
      ? [
          { id: 'continue', source: 'decision', target: 'through', points: continuation },
          { id: 'branch', source: 'decision', target: 'branch', points: branch },
        ]
      : [
          { id: 'continue', source: 'through', target: 'decision', points: continuation },
          { id: 'branch', source: 'branch', target: 'decision', points: branch },
        ];

    const markers = [...findConnectorJunctions(routes).values()].flat()
      .filter((point) => point.x === tee.x && point.y === tee.y);

    expect(markers).toEqual([tee]);
  });

  it('does not mark container-border-style crossings or differently routed edges from one source', () => {
    const junctions = findConnectorJunctions([
      { id: 'edge-a', source: 'first', target: 'right', points: [{ x: 0, y: 50 }, { x: 200, y: 50 }] },
      { id: 'edge-b', source: 'second', target: 'down', points: [{ x: 100, y: 0 }, { x: 100, y: 200 }] },
      { id: 'edge-c', source: 'split', target: 'one', points: [{ x: 10, y: 10 }, { x: 10, y: 60 }] },
      { id: 'edge-d', source: 'split', target: 'two', points: [{ x: 20, y: 10 }, { x: 20, y: 60 }] },
    ]);

    expect(junctions).toEqual(new Map());
  });

  it('keeps a plain alignment elbow unmarked and marks one shared-edge elbow', () => {
    const junctions = findConnectorJunctions([
      {
        id: 'plain',
        source: 'plain-source',
        target: 'plain-target',
        points: [{ x: 0, y: 0 }, { x: 80, y: 0 }, { x: 80, y: 80 }],
      },
      {
        id: 'split-a',
        source: 'split',
        target: 'upper',
        points: [{ x: 0, y: 100 }, { x: 0, y: 160 }, { x: 160, y: 160 }],
      },
      {
        id: 'split-b',
        source: 'split',
        target: 'lower',
        points: [{ x: 0, y: 100 }, { x: 0, y: 220 }],
      },
    ]);

    expect(junctions.has('plain')).toBe(false);
    expect(junctions.get('split-a')).toEqual([
      { x: 0, y: 100 },
      { x: 0, y: 160 },
    ]);
    expect([...junctions.values()].flat().filter((point) => point.x === 0 && point.y === 160)).toHaveLength(1);
  });

  it('marks a tee when a branch elbow meets its sibling continuation', () => {
    const junctions = findConnectorJunctions([
      {
        id: 'continue',
        source: 'decision',
        target: 'downstream',
        points: [{ x: 100, y: 0 }, { x: 100, y: 200 }],
      },
      {
        id: 'branch',
        source: 'decision',
        target: 'side-path',
        points: [{ x: 100, y: 0 }, { x: 100, y: 80 }, { x: 240, y: 80 }],
      },
    ]);

    expect(junctions.get('branch')).toEqual([
      { x: 100, y: 0 },
      { x: 100, y: 80 },
    ]);
    expect([...junctions.values()].flat().filter((point) => point.x === 100 && point.y === 80)).toHaveLength(1);
  });

  it('marks only the return point shared with the target continuation', () => {
    const junctions = findConnectorJunctions([
      {
        id: 'revision',
        source: 'review',
        target: 'implement',
        loopback: true,
        points: [
          { x: 200, y: 100 },
          { x: 20, y: 100 },
          { x: 20, y: 40 },
          { x: 100, y: 40 },
        ],
      },
      {
        id: 'continue',
        source: 'implement',
        target: 'verify',
        points: [
          { x: 100, y: 0 },
          { x: 100, y: 100 },
        ],
      },
    ]);

    expect(junctions.get('revision')).toEqual([
      { x: 100, y: 40 },
    ]);
    expect(junctions.has('continue')).toBe(false);
  });

  it('ends a return rail at its target continuation junction, not on the card edge', () => {
    const aligned = alignLoopbackToContinuation(
      [
        { x: 500, y: 80 },
        { x: 40, y: 80 },
        { x: 40, y: 260 },
        { x: 250, y: 260 },
      ],
      [
        { x: 250, y: 140 },
        { x: 250, y: 190 },
        { x: 380, y: 190 },
      ],
    );

    expect(aligned).toEqual([
      { x: 500, y: 80 },
      { x: 40, y: 80 },
      { x: 40, y: 190 },
      { x: 250, y: 190 },
    ]);
  });

  it('moves an unsafe left-side return join below the destination card clearance', () => {
    const aligned = alignLoopbackToContinuation(
      [
        { x: 500, y: 180 },
        { x: 20, y: 180 },
        { x: 20, y: 80 },
        { x: 100, y: 80 },
      ],
      [
        { x: 100, y: 100 },
        { x: 100, y: 80 },
      ],
      { x: 50, y: 0, width: 100, height: 100 },
    );

    expect(aligned.at(-1)).toEqual({ x: 100, y: 118 });
    expect(aligned.at(-2)).toEqual({ x: 20, y: 118 });
  });
});
