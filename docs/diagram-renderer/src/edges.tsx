import { BaseEdge, EdgeLabelRenderer, getSmoothStepPath, type EdgeProps } from '@xyflow/react';
import { fontFamily, neutral } from './theme';

export interface LabelOffset {
  dx: number;
  dy: number;
}

export interface LabeledEdgeData extends Record<string, unknown> {
  labelOffset?: LabelOffset;
}

/**
 * A smoothstep edge whose label is rendered via `EdgeLabelRenderer` (plain
 * HTML, not the built-in SVG `<text>`+bg-rect label) so its position can be
 * nudged by a precomputed `data.labelOffset` -- see
 * `resolveLabelCollisions()` in DiagramCanvas.tsx. The built-in smoothstep
 * edge always centers its label on the path's own midpoint; with several
 * parallel/fanning edges crossing the same rank band (e.g. several services
 * all pulling images from one registry, or two sibling nodes both calling
 * the same downstream target) those midpoints land on top of each other,
 * making the label text illegible. This component still draws the exact
 * same path, it just lets the label be shifted a few pixels off that
 * midpoint when doing so is required to avoid overlapping a neighboring
 * label's bounding box.
 */
export function LabeledSmoothStepEdge({
  id,
  sourceX,
  sourceY,
  targetX,
  targetY,
  sourcePosition,
  targetPosition,
  style,
  markerEnd,
  label,
  data,
}: EdgeProps) {
  const [edgePath, labelX, labelY] = getSmoothStepPath({
    sourceX,
    sourceY,
    sourcePosition,
    targetX,
    targetY,
    targetPosition,
  });

  const offset = (data as LabeledEdgeData | undefined)?.labelOffset ?? { dx: 0, dy: 0 };

  return (
    <>
      <BaseEdge id={id} path={edgePath} style={style} markerEnd={markerEnd} />
      {label ? (
        <EdgeLabelRenderer>
          <div
            className="nodrag nopan"
            style={{
              position: 'absolute',
              transform: `translate(-50%, -50%) translate(${labelX + offset.dx}px, ${labelY + offset.dy}px)`,
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
