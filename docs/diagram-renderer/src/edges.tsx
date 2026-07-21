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

export interface RoutedEdgeData extends Record<string, unknown> {
  /** Poly-line waypoints dagre computed for this edge, in flow coordinates,
   * already routed to avoid intervening node cards. */
  points: Point[];
  /** Label anchor dagre reserved for this edge (edge.x/edge.y). */
  labelPos?: Point;
  /** Small nudge applied on top of labelPos to break residual label overlaps. */
  labelOffset?: LabelOffset;
}

/** Corner radius used to round the orthogonal joints of a routed edge so it
 * reads like the product's smoothstep edges rather than hard right angles. */
const CORNER_RADIUS = 10;

function dist(a: Point, b: Point): number {
  return Math.hypot(b.x - a.x, b.y - a.y);
}

/**
 * Builds an SVG path that follows `points` exactly but replaces each interior
 * vertex with a short quadratic-bezier fillet, so dagre's routed orthogonal
 * poly-line renders with rounded corners. Because the geometry comes straight
 * from dagre's edge routing (which threads edges through the gaps between
 * ranked nodes), the resulting line never cuts through an unrelated card the
 * way a naive handle-to-handle smoothstep path does.
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
 * An edge that draws the exact poly-line dagre routed for it (see
 * `layout()` in DiagramCanvas.tsx) instead of a handle-to-handle smoothstep
 * path. dagre performs real layered edge routing -- inserting per-rank
 * routing waypoints that thread the line through the gaps between cards -- so
 * using those waypoints is what keeps edges from visually overlapping or
 * crossing straight through unrelated nodes. The label is rendered via
 * `EdgeLabelRenderer` at dagre's reserved label anchor (dagre reserves label
 * space in the layout, so labels no longer pile onto a shared midpoint),
 * with a small precomputed `labelOffset` applied to break any residual
 * overlap between co-located labels.
 */
export function RoutedEdge({ id, style, markerEnd, label, data }: EdgeProps) {
  const { points, labelPos, labelOffset } = (data as RoutedEdgeData | undefined) ?? {
    points: [],
  };
  const edgePath = buildRoundedPath(points ?? []);
  const offset = labelOffset ?? { dx: 0, dy: 0 };

  return (
    <>
      <BaseEdge id={id} path={edgePath} style={style} markerEnd={markerEnd} />
      {label && labelPos ? (
        <EdgeLabelRenderer>
          <div
            className="nodrag nopan"
            style={{
              position: 'absolute',
              transform: `translate(-50%, -50%) translate(${labelPos.x + offset.dx}px, ${labelPos.y + offset.dy}px)`,
              fontFamily,
              fontSize: 11,
              fontWeight: 600,
              lineHeight: 1,
              color: neutral.foreground3,
              backgroundColor: neutral.background1,
              opacity: 0.95,
              padding: '3px 5px',
              borderRadius: 4,
              whiteSpace: 'nowrap',
              pointerEvents: 'none',
            }}
          >
            {label}
          </div>
        </EdgeLabelRenderer>
      ) : null}
    </>
  );
}
