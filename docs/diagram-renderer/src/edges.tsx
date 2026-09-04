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
  /** Orthogonal poly-line waypoints the layout router computed for this edge,
   * in flow coordinates, already routed through the gutters between bands so
   * it never crosses a node card. */
  points: Point[];
  /** Position the router chose for this edge's label: on the edge's own
   * horizontal run, slid clear of crossing connectors and other labels. */
  labelPos?: Point;
  /** Reserved nudge applied on top of labelPos. */
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
              fontSize: 15,
              fontWeight: 600,
              lineHeight: 1.1,
              color: neutral.foreground2,
              backgroundColor: neutral.background1,
              border: `1px solid ${neutral.stroke2}`,
              padding: '5px 9px',
              borderRadius: 6,
              whiteSpace: 'nowrap',
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
