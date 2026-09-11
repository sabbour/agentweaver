import { BaseEdge, EdgeLabelRenderer, type EdgeProps } from '@xyflow/react';
import { fontFamily, neutral } from './theme';

export interface LabelOffset {
  dx: number;
  dy: number;
}

export interface Point {
  x: number;
  y: number;
}

export interface ConnectorBridge {
  x: number;
  y: number;
  orientation: 'horizontal' | 'vertical';
}

export interface RoutedEdgeData extends Record<string, unknown> {
  /** Orthogonal poly-line waypoints the layout router computed for this edge,
   * in flow coordinates, already routed through the gutters between bands so
   * it never crosses a node card. */
  points: Point[];
  /** Position the router chose for this edge's label: on the edge's own
   * horizontal run, slid clear of crossing connectors and other labels. */
  labelPos?: Point;
  /** Reserved nudge applied on top of labelPos. */
  labelOffset?: LabelOffset;
  /** Interior crossings where this connector visibly passes over an earlier route. */
  bridges?: ConnectorBridge[];
  /** Shared source ports rendered as explicit junction circles. */
  junctions?: Point[];
}

/** Corner radius used to round the orthogonal joints of a routed edge so it
 * reads like the product's smoothstep edges rather than hard right angles. */
const CORNER_RADIUS = 10;

function dist(a: Point, b: Point): number {
  return Math.hypot(b.x - a.x, b.y - a.y);
}

interface OrthogonalSegment {
  orientation: ConnectorBridge['orientation'];
  constant: number;
  start: number;
  end: number;
}

function segments(points: Point[]): OrthogonalSegment[] {
  const result: OrthogonalSegment[] = [];
  for (let index = 0; index < points.length - 1; index += 1) {
    const from = points[index];
    const to = points[index + 1];
    if (Math.abs(from.x - to.x) < 0.5 && Math.abs(from.y - to.y) > 0.5) {
      result.push({
        orientation: 'vertical',
        constant: from.x,
        start: Math.min(from.y, to.y),
        end: Math.max(from.y, to.y),
      });
    } else if (Math.abs(from.y - to.y) < 0.5 && Math.abs(from.x - to.x) > 0.5) {
      result.push({
        orientation: 'horizontal',
        constant: from.y,
        start: Math.min(from.x, to.x),
        end: Math.max(from.x, to.x),
      });
    }
  }
  return result;
}

/**
 * Assigns each perpendicular crossing to the later stable edge. The router
 * already prevents card collisions; this makes an unavoidable connector
 * crossing explicit instead of visually merging both routes.
 */
export function findConnectorBridges(
  routes: Array<{ id: string; points: Point[] }>,
): Map<string, ConnectorBridge[]> {
  const ordered = routes
    .map((route) => ({ ...route, segments: segments(route.points) }))
    .sort((left, right) => left.id.localeCompare(right.id));
  const bridges = new Map<string, ConnectorBridge[]>();

  for (let current = 1; current < ordered.length; current += 1) {
    for (let prior = 0; prior < current; prior += 1) {
      for (const currentSegment of ordered[current].segments) {
        for (const priorSegment of ordered[prior].segments) {
          if (currentSegment.orientation === priorSegment.orientation) continue;
          const horizontal = currentSegment.orientation === 'horizontal' ? currentSegment : priorSegment;
          const vertical = currentSegment.orientation === 'vertical' ? currentSegment : priorSegment;
          const x = vertical.constant;
          const y = horizontal.constant;
          const inset = 8;
          if (
            x <= horizontal.start + inset || x >= horizontal.end - inset ||
            y <= vertical.start + inset || y >= vertical.end - inset
          ) {
            continue;
          }
          const edgeBridges = bridges.get(ordered[current].id) ?? [];
          if (!edgeBridges.some((bridge) => Math.abs(bridge.x - x) < 0.5 && Math.abs(bridge.y - y) < 0.5)) {
            edgeBridges.push({ x, y, orientation: currentSegment.orientation });
            bridges.set(ordered[current].id, edgeBridges);
          }
        }
      }
    }
  }

  return bridges;
}

function turnsBetween(previous: Point, current: Point, next: Point): boolean {
  const verticalBefore = Math.abs(previous.x - current.x) < 0.5;
  const verticalAfter = Math.abs(current.x - next.x) < 0.5;
  return verticalBefore !== verticalAfter;
}

/**
 * Marks semantic split and merge locations only. Generic geometric crossings
 * are represented by bridge paths, never by junction circles.
 */
export function findConnectorJunctions(
  routes: Array<{ id: string; source: string; target: string; points: Point[]; loopback?: boolean }>,
): Map<string, Point[]> {
  const bySource = new Map<string, typeof routes>();
  const byTarget = new Map<string, typeof routes>();
  for (const route of routes) {
    const source = bySource.get(route.source) ?? [];
    source.push(route);
    bySource.set(route.source, source);
    const target = byTarget.get(route.target) ?? [];
    target.push(route);
    byTarget.set(route.target, target);
  }

  const junctions = new Map<string, Point[]>();
  const claimed = new Set<string>();
  const add = (edgeId: string, point: Point | undefined) => {
    if (!point) return;
    const key = `${Math.round(point.x * 10)}:${Math.round(point.y * 10)}`;
    if (claimed.has(key)) return;
    claimed.add(key);
    const markers = junctions.get(edgeId) ?? [];
    markers.push(point);
    junctions.set(edgeId, markers);
  };

  for (const group of bySource.values()) {
    if (group.length < 2) continue;
    group.sort((left, right) => left.id.localeCompare(right.id));
    add(group[0].id, group[0].points[0]);
    for (const route of group) {
      for (let index = 1; index < route.points.length - 1; index += 1) {
        if (turnsBetween(route.points[index - 1], route.points[index], route.points[index + 1])) {
          add(route.id, route.points[index]);
        }
      }
    }
  }
  for (const group of byTarget.values()) {
    if (group.length < 2) continue;
    group.sort((left, right) => left.id.localeCompare(right.id));
    add(group[0].id, group[0].points.at(-1));
  }
  for (const route of routes.filter((route) => route.loopback)) {
    add(route.id, route.points.at(-2));
    add(route.id, route.points.at(-1));
  }
  return junctions;
}

/**
 * Builds an SVG path that follows `points` exactly but replaces each interior
 * vertex with a short quadratic-bezier fillet, so the router's orthogonal
 * poly-line renders with rounded corners. Because the geometry comes straight
 * from `layout()` in DiagramCanvas.tsx (which assigns every horizontal run its
 * own lane inside a gutter and fans each card's ports out along its edge), the
 * resulting line never cuts through an unrelated card and never lies on top of
 * another edge the way a naive handle-to-handle smoothstep path does.
 */
export function buildRoundedPath(points: Point[], radius = CORNER_RADIUS): string {
  if (points.length === 0) return '';
  if (points.length === 1) return `M ${points[0].x},${points[0].y}`;
  if (points.length === 2) {
    return `M ${points[0].x},${points[0].y} L ${points[1].x},${points[1].y}`;
  }

  let d = `M ${points[0].x},${points[0].y}`;
  for (let i = 1; i < points.length - 1; i += 1) {
    const p0 = points[i - 1];
    const p1 = points[i];
    const p2 = points[i + 1];

    const d01 = dist(p0, p1);
    const d12 = dist(p1, p2);
    if (d01 === 0 || d12 === 0) {
      d += ` L ${p1.x},${p1.y}`;
      continue;
    }
    const r = Math.min(radius, d01 / 2, d12 / 2);
    const enter = {
      x: p1.x - ((p1.x - p0.x) / d01) * r,
      y: p1.y - ((p1.y - p0.y) / d01) * r,
    };
    const exit = {
      x: p1.x + ((p2.x - p1.x) / d12) * r,
      y: p1.y + ((p2.y - p1.y) / d12) * r,
    };
    d += ` L ${enter.x},${enter.y} Q ${p1.x},${p1.y} ${exit.x},${exit.y}`;
  }
  const last = points[points.length - 1];
  d += ` L ${last.x},${last.y}`;
  return d;
}

/**
 * Builds a true bridge path: the lower connector is interrupted around the
 * crossing and the same stroke draws a short overpass arc. No background mask
 * is used, so the interruption remains correct on any canvas color.
 */
export function buildBridgedPath(
  points: Point[],
  bridges: ConnectorBridge[],
  radius = 7,
): string {
  if (bridges.length === 0) return buildRoundedPath(points);
  if (points.length < 2) return buildRoundedPath(points);

  let d = `M ${points[0].x},${points[0].y}`;
  for (let index = 0; index < points.length - 1; index += 1) {
    const from = points[index];
    const to = points[index + 1];
    const horizontal = Math.abs(from.y - to.y) < 0.5;
    const vertical = Math.abs(from.x - to.x) < 0.5;
    const segmentBridges = bridges
      .filter((bridge) =>
        (horizontal && bridge.orientation === 'horizontal' && Math.abs(bridge.y - from.y) < 0.5 &&
          bridge.x > Math.min(from.x, to.x) + radius && bridge.x < Math.max(from.x, to.x) - radius) ||
        (vertical && bridge.orientation === 'vertical' && Math.abs(bridge.x - from.x) < 0.5 &&
          bridge.y > Math.min(from.y, to.y) + radius && bridge.y < Math.max(from.y, to.y) - radius))
      .sort((left, right) => horizontal
        ? (to.x >= from.x ? left.x - right.x : right.x - left.x)
        : vertical
          ? (to.y >= from.y ? left.y - right.y : right.y - left.y)
          : 0);
    if (segmentBridges.length === 0) {
      d += ` L ${to.x},${to.y}`;
      continue;
    }
    for (const bridge of segmentBridges) {
      const forward = horizontal ? Math.sign(to.x - from.x) : Math.sign(to.y - from.y);
      const start = horizontal
        ? { x: bridge.x - radius * forward, y: bridge.y }
        : { x: bridge.x, y: bridge.y - radius * forward };
      const end = horizontal
        ? { x: bridge.x + radius * forward, y: bridge.y }
        : { x: bridge.x, y: bridge.y + radius * forward };
      const sweep = horizontal ? (forward > 0 ? 0 : 1) : (forward > 0 ? 1 : 0);
      d += ` L ${start.x},${start.y} A ${radius},${radius} 0 0 ${sweep} ${end.x},${end.y}`;
    }
    d += ` L ${to.x},${to.y}`;
  }
  return d;
}

/**
 * Joins a return rail to the first outward segment of its destination's
 * ordinary flow. This keeps the semantic loop outside the card face and makes
 * its last horizontal segment terminate at the real continuation junction.
 */
export function alignLoopbackToContinuation(
  points: Point[],
  continuation: Point[] | undefined,
  targetBounds?: { x: number; y: number; width: number; height: number },
): Point[] {
  if (points.length < 4 || !continuation?.length) return points;
  const candidate = continuation[1] ?? continuation[0];
  const clearance = 18;
  const join = targetBounds
    ? {
        // Static diagrams flow top-to-bottom. The normal connector leaves
        // here, so this is the local continuation junction rather than any
        // point on the destination card's face or top edge.
        x: continuation[0].x,
        y: targetBounds.y + targetBounds.height + clearance,
      }
    : candidate;
  const sideX = points[1].x;
  return [
    points[0],
    { x: sideX, y: points[0].y },
    { x: sideX, y: join.y },
    join,
  ];
}

/**
 * An edge that draws the exact orthogonal poly-line the layout router computed
 * for it (see `layout()` in DiagramCanvas.tsx) instead of a handle-to-handle
 * smoothstep path. The router gives every horizontal run its own lane inside
 * the gutter it travels through and fans each card's connections out along the
 * card edge, which is what keeps parallel edges from collapsing onto each
 * other. The label is rendered via `EdgeLabelRenderer` at the anchor the
 * router picked for it -- a point on this edge's own run that was searched
 * clear of crossing connectors, other labels, and cards.
 */
export function RoutedEdge({ id, style, markerEnd, label, data }: EdgeProps) {
  const { points, labelPos, labelOffset, bridges = [], junctions = [] } = (data as RoutedEdgeData | undefined) ?? {
    points: [],
  };
  const edgePath = buildBridgedPath(points ?? [], bridges);
  const offset = labelOffset ?? { dx: 0, dy: 0 };
  const stroke = typeof style?.stroke === 'string' ? style.stroke : neutral.foreground4;
  const strokeWidth = typeof style?.strokeWidth === 'number' ? style.strokeWidth : 1.8;

  return (
    <>
      <BaseEdge id={id} path={edgePath} style={style} markerEnd={markerEnd} />
      {junctions.map((junction, index) => (
        <circle
          key={`${junction.x}-${junction.y}-${index}`}
          data-testid="diagram-connector-junction"
          cx={junction.x}
          cy={junction.y}
          r={2.5}
          fill={stroke}
        />
      ))}
      {label && labelPos ? (
        <EdgeLabelRenderer>
          <div
            className="nodrag nopan"
            style={{
              position: 'absolute',
              transform: `translate(-50%, -50%) translate(${labelPos.x + offset.dx}px, ${labelPos.y + offset.dy}px)`,
              fontFamily,
              fontSize: 15,
              fontWeight: 600,
              lineHeight: 1.1,
              color: neutral.foreground2,
              backgroundColor: neutral.background1,
              border: `1px solid ${neutral.stroke2}`,
              padding: '5px 9px',
              borderRadius: 6,
              // The router pre-wraps long labels and reserves space for the
              // wrapped block, so honour its line breaks exactly ('pre') and
              // never let the browser re-wrap to a different shape.
              whiteSpace: 'pre',
              textAlign: 'center',
              pointerEvents: 'none',
              // Labels belong on top of every connector, including the ones
              // they do not describe. Edge SVGs carry their own zIndex, so the
              // label needs an explicit higher one to win.
              zIndex: 50,
            }}
          >
            {label}
          </div>
        </EdgeLabelRenderer>
      ) : null}
    </>
  );
}
