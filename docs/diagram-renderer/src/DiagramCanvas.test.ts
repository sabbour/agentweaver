import { describe, expect, it } from 'vitest';
import type { Edge } from '@xyflow/react';
import memoryDecisionsFixture from '../../diagrams/src/memory-decisions-fig2.json';
import softwareDeliveryFixture from '../../diagrams/src/workflow-software-delivery.json';
import { findLoopbackReturnJoinNode, layout } from './DiagramCanvas';
import type { GraphEdge, GraphSpec } from './types';

interface RouteData {
  points: Array<{ x: number; y: number }>;
  junctions?: Array<{ x: number; y: number }>;
  loopback?: boolean;
  returnJoin?: string;
  loopbackLabel?: string;
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

function pointLiesOnRoute(point: { x: number; y: number }, points: Array<{ x: number; y: number }>): boolean {
  return points.some((candidate, index) => {
    if (index === 0) return false;
    const previous = points[index - 1];
    return (
      (Math.abs(previous.x - candidate.x) < 0.5 && Math.abs(point.x - previous.x) < 0.5 &&
        point.y >= Math.min(previous.y, candidate.y) - 0.5 && point.y <= Math.max(previous.y, candidate.y) + 0.5) ||
      (Math.abs(previous.y - candidate.y) < 0.5 && Math.abs(point.y - previous.y) < 0.5 &&
        point.x >= Math.min(previous.x, candidate.x) - 0.5 && point.x <= Math.max(previous.x, candidate.x) + 0.5)
    );
  });
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

describe('software delivery return lane fixture', () => {
  it('derives review-driven returns below RAI Check on one labeled external lane', () => {
    expect(softwareDeliveryFixture.edges.every((edge) => !('returnJoin' in edge))).toBe(true);
    const { edges } = layout(softwareDeliveryFixture as GraphSpec);
    const returns = edges.filter((edge) => {
      const data = routeData(edge);
      return data.loopback && data.returnJoin === 'rai-check' &&
        (data.loopbackLabel === 'revise' || data.loopbackLabel === 'request-changes');
    });
    const normalRaiEdges = edges.filter((edge) =>
      edge.source === 'rai-check' && !routeData(edge).loopback);

    expect(returns).toHaveLength(4);
    const join = routeData(returns[0]).points.at(-1)!;
    expect(returns.every((edge) => samePoint(routeData(edge).points.at(-1)!, join))).toBe(true);
    expect(new Set(returns.map((edge) => routeData(edge).points[1].x)).size).toBe(1);
    expect(normalRaiEdges.every((edge) => pointLiesOnRoute(join, routeData(edge).points))).toBe(true);

    const markers = edges.flatMap((edge) => routeData(edge).junctions ?? [])
      .filter((point) => samePoint(point, join));
    expect(markers).toEqual([join]);
    expect(returns.filter((edge) => edge.label != null).map((edge) => edge.label).sort())
      .toEqual(['request-changes', 'revise']);
  });
});

describe('semantic return join discovery', () => {
  const fixtures: Array<{
    name: string;
    loopback: GraphEdge;
    edges: GraphEdge[];
    expected: string;
  }> = [
    {
      name: 'linear revision',
      loopback: { from: 'review', to: 'implement', loopback: true },
      edges: [
        { from: 'implement', to: 'test' },
        { from: 'test', to: 'done' },
        { from: 'review', to: 'implement', loopback: true },
      ],
      expected: 'implement',
    },
    {
      name: 'branching review',
      loopback: { from: 'review', to: 'implement', loopback: true },
      edges: [
        { from: 'implement', to: 'test' },
        { from: 'test', to: 'review-gate' },
        { from: 'review-gate', to: 'approve' },
        { from: 'review-gate', to: 'decline' },
        { from: 'review', to: 'implement', loopback: true },
      ],
      expected: 'review-gate',
    },
    {
      name: 'converging review',
      loopback: { from: 'retry', to: 'implement', loopback: true },
      edges: [
        { from: 'implement', to: 'left' },
        { from: 'left', to: 'join' },
        { from: 'right', to: 'join' },
        { from: 'join', to: 'done' },
        { from: 'retry', to: 'implement', loopback: true },
      ],
      expected: 'join',
    },
    {
      name: 'multiple returns',
      loopback: { from: 'human-review', to: 'implement', loopback: true },
      edges: [
        { from: 'implement', to: 'test' },
        { from: 'test', to: 'review-gate' },
        { from: 'review-gate', to: 'approved' },
        { from: 'review-gate', to: 'declined' },
        { from: 'qa-review', to: 'implement', loopback: true },
        { from: 'human-review', to: 'implement', loopback: true },
      ],
      expected: 'review-gate',
    },
  ];

  it.each(fixtures)('finds the $name join without workflow-specific metadata', ({ loopback, edges, expected }) => {
    expect(findLoopbackReturnJoinNode(loopback, edges)).toBe(expected);
  });
});
