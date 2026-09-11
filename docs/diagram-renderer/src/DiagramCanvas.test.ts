import { describe, expect, it } from 'vitest';
import type { Edge } from '@xyflow/react';
import memoryDecisionsFixture from '../../diagrams/src/memory-decisions-fig2.json';
import { layout } from './DiagramCanvas';
import type { GraphSpec } from './types';

interface RouteData {
  points: Array<{ x: number; y: number }>;
  junctions?: Array<{ x: number; y: number }>;
}

function samePoint(left: { x: number; y: number }, right: { x: number; y: number }): boolean {
  return Math.abs(left.x - right.x) < 0.5 && Math.abs(left.y - right.y) < 0.5;
}

function isElbow(
  points: Array<{ x: number; y: number }>,
  index: number,
): boolean {
  const previous = points[index - 1];
  const current = points[index];
  const next = points[index + 1];
  if (!previous || !current || !next) return false;
  const verticalBefore = Math.abs(previous.x - current.x) < 0.5;
  const verticalAfter = Math.abs(current.x - next.x) < 0.5;
  const horizontalBefore = Math.abs(previous.y - current.y) < 0.5;
  const horizontalAfter = Math.abs(current.y - next.y) < 0.5;
  return (verticalBefore && horizontalAfter) || (horizontalBefore && verticalAfter);
}

function passesThrough(
  point: { x: number; y: number },
  points: Array<{ x: number; y: number }>,
): boolean {
  return points.some((candidate, index) => {
    if (index === 0) return false;
    const previous = points[index - 1];
    return (
      (Math.abs(previous.x - candidate.x) < 0.5 && Math.abs(point.x - previous.x) < 0.5 &&
        point.y > Math.min(previous.y, candidate.y) + 0.5 && point.y < Math.max(previous.y, candidate.y) - 0.5) ||
      (Math.abs(previous.y - candidate.y) < 0.5 && Math.abs(point.y - previous.y) < 0.5 &&
        point.x > Math.min(previous.x, candidate.x) + 0.5 && point.x < Math.max(previous.x, candidate.x) - 0.5)
    );
  });
}

function routeData(edge: Edge): RouteData {
  return edge.data as RouteData;
}

describe('memory decisions tee fixture', () => {
  it.each([
    ['B', 'Requested slug exists?'],
    ['D', 'Same agent?'],
    ['E', 'Existing entry pending?'],
  ])('marks the %s (%s) branch tee exactly once', (source) => {
    const { edges } = layout(memoryDecisionsFixture as GraphSpec);
    const outgoing = edges.filter((edge) => edge.source === source);
    expect(outgoing).toHaveLength(2);

    const tee = outgoing
      .flatMap((edge) => routeData(edge).points.map((point, index) => ({ edge, point, index })))
      .find(({ edge, point, index }) =>
        isElbow(routeData(edge).points, index) &&
        outgoing.some((candidate) =>
          candidate.id !== edge.id && passesThrough(point, routeData(candidate).points)));

    expect(tee).toBeDefined();
    const rendered = edges.flatMap((edge) => (routeData(edge).junctions ?? []).map((point) => ({ edge, point })));
    const markers = rendered.filter(({ point }) => samePoint(point, tee!.point));
    expect(markers).toHaveLength(1);
    expect(markers[0].edge.source).toBe(source);
  });
});
