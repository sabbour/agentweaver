import { Handle, Position, type NodeProps } from '@xyflow/react';
import { badgeTones, fontFamily, fontFamilyMonospace, neutral, radius } from './theme';
import { iconRegistry } from './icons';
import type { GraphNode } from './types';

export const CARD_WIDTH = 340;

// The layout math places connector endpoints at `y` and `y + cardHeight(n)`.
// If the card auto-sizes from its content instead, the rendered border sits
// above the assumed bottom edge and every outgoing connector appears to start
// in empty space. Fixing the height here is what keeps geometry and pixels in
// agreement -- these two values are the single source of truth, and
// DiagramCanvas imports them rather than redeclaring its own copies.
export const CARD_HEIGHT_2 = 104;
export const CARD_HEIGHT_3 = 132;

export function cardHeightFor(node: { meta?: string }): number {
  return node.meta ? CARD_HEIGHT_3 : CARD_HEIGHT_2;
}

// Mirrors apps/web/src/components/CoordinatorTopologyGraph.tsx's `.card` /
// `.cardMain` / `.cardTitleGroup` / `.statusBadge` layout: rounded card,
// icon + title/subtitle column, a pill-shaped badge pinned top-right. Static
// inline styles (not makeStyles/tokens) because this renders outside a
// <FluentProvider> -- see theme.ts for where each hex value comes from.
export function CardNode({ data }: NodeProps) {
  const node = data as unknown as GraphNode;
  const Icon = iconRegistry[node.icon];
  const tone = badgeTones[node.badge.tone];

  return (
    <div
      style={{
        width: CARD_WIDTH,
        height: cardHeightFor(node),
        boxSizing: 'border-box',
        display: 'flex',
        flexDirection: 'column',
        justifyContent: 'center',
        padding: 18,
        paddingLeft: 22,
        backgroundColor: neutral.background1,
        border: `1px solid ${neutral.stroke2}`,
        borderLeft: `5px solid ${tone.fg}`,
        borderRadius: radius.card,
        fontFamily,
      }}
    >
      <Handle type="target" position={Position.Top} style={{ opacity: 0 }} />
      <Handle type="source" position={Position.Bottom} style={{ opacity: 0 }} />

      <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
        <span style={{ display: 'flex', flexShrink: 0, color: tone.fg }} aria-hidden="true">
          <Icon fontSize={30} />
        </span>
        <div style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
          <span
            style={{
              fontWeight: 600,
              fontSize: 20,
              color: neutral.foreground1,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            {node.label}
          </span>
          {node.subLabel && (
            <span
              style={{
                fontSize: 15,
                color: neutral.foreground3,
                marginTop: 2,
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
              }}
            >
              {node.subLabel}
            </span>
          )}
          {node.meta && (
            <span
              style={{
                fontSize: 13,
                color: neutral.foreground4,
                fontFamily: fontFamilyMonospace,
                marginTop: 2,
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
              }}
            >
              {node.meta}
            </span>
          )}
        </div>
      </div>
    </div>
  );
}

// Non-interactive tinted rounded container drawn behind a cluster of cards
// (the diagram-renderer analogue of mermaid's subgraph clusters). Tier 1
// (outermost) uses Background2, tier 2+ (nested) uses Background3, matching
// the reference component's grouping/hierarchy-tier convention.
export function GroupNode({ data }: NodeProps) {
  const { tier } = data as unknown as { tier: number };
  return (
    <div
      style={{
        width: '100%',
        height: '100%',
        boxSizing: 'border-box',
        borderRadius: radius.card,
        border: 'none',
        backgroundColor: tier <= 1 ? neutral.background1 : neutral.background2,
        boxShadow: '0 2px 4px rgba(28,24,20,0.05), 0 8px 24px rgba(28,24,20,0.07)',
      }}
    />
  );
}

// The band title is a separate node from the band surface on purpose. React
// Flow stacks nodes by their own zIndex, so a title baked into the background
// node would inherit the background's low zIndex and end up underneath any
// connector that passes through the band's top padding. Rendering it as its
// own node lets it sit above the edge layer while the tinted surface stays
// below it.
export function GroupLabelNode({ data }: NodeProps) {
  const { label, tier } = data as unknown as { label: string; tier: number };
  return (
    <span
      style={{
        display: 'inline-block',
        fontFamily,
        fontSize: 19,
        fontWeight: 700,
        letterSpacing: '0.02em',
        color: neutral.foreground2,
        // Matches the band surface underneath, so the chip reads as a hole
        // punched in the connectors rather than a floating tag.
        backgroundColor: tier <= 1 ? neutral.background1 : neutral.background2,
        padding: '2px 10px',
        margin: '-2px -10px',
        borderRadius: 6,
        whiteSpace: 'nowrap',
        pointerEvents: 'none',
      }}
    >
      {label}
    </span>
  );
}
