import { Handle, Position, type NodeProps } from '@xyflow/react';
import { badgeTones, fontFamily, fontFamilyMonospace, neutral, radius } from './theme';
import { iconRegistry } from './icons';
import type { GraphNode } from './types';

export const CARD_WIDTH = 260;

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
        boxSizing: 'border-box',
        display: 'flex',
        flexDirection: 'column',
        gap: 8,
        padding: 14,
        backgroundColor: neutral.background1,
        border: `1px solid ${neutral.stroke2}`,
        borderRadius: radius.card,
        fontFamily,
      }}
    >
      <Handle type="target" position={Position.Top} style={{ opacity: 0 }} />
      <Handle type="source" position={Position.Bottom} style={{ opacity: 0 }} />

      <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
        <span
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            padding: '2px 8px',
            borderRadius: radius.badge,
            fontSize: 11,
            fontWeight: 600,
            whiteSpace: 'nowrap',
            backgroundColor: tone.bg,
            color: tone.fg,
          }}
        >
          {node.badge.text}
        </span>
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <span style={{ display: 'flex', flexShrink: 0, color: neutral.foreground2 }} aria-hidden="true">
          <Icon fontSize={22} />
        </span>
        <div style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
          <span
            style={{
              fontWeight: 600,
              fontSize: 15,
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
                fontSize: 12,
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
                fontSize: 10,
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
  const { label, tier } = data as unknown as { label: string; tier: number };
  return (
    <div
      style={{
        width: '100%',
        height: '100%',
        boxSizing: 'border-box',
        borderRadius: radius.card,
        border: `1px solid ${neutral.stroke2}`,
        backgroundColor: tier <= 1 ? neutral.background2 : neutral.background3,
      }}
    >
      <span
        style={{
          position: 'absolute',
          top: 10,
          left: 16,
          fontFamily,
          fontSize: 12,
          fontWeight: 600,
          color: neutral.foreground3,
        }}
      >
        {label}
      </span>
    </div>
  );
}
