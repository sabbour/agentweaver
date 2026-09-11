import { describe, expect, it } from 'vitest';
import {
  buildBridgedPath,
  findConnectorBridges,
  findConnectorJunctions,
} from './edges';

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

  it('emits one explicit junction for connectors with the same origin', () => {
    const junctions = findConnectorJunctions([
      { id: 'edge-b', source: 'origin', target: 'right', points: [{ x: 10, y: 20 }, { x: 100, y: 20 }] },
      { id: 'edge-a', source: 'origin', target: 'down', points: [{ x: 10, y: 20 }, { x: 20, y: 100 }] },
    ]);

    expect(junctions.get('edge-a')).toEqual([{ x: 10, y: 20 }]);
    expect(junctions.has('edge-b')).toBe(false);
  });

  it('marks both the outer return and central join of a semantic loopback', () => {
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
    ]);

    expect(junctions.get('revision')).toEqual([
      { x: 20, y: 40 },
      { x: 100, y: 40 },
    ]);
  });
});
