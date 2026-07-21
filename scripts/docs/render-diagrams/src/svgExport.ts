import {
  EDGE_STROKE_MUTED,
  FONT_FAMILY,
  GROUP_BORDER,
  GROUP_FILL,
  NODE_VARIANT_STYLES,
  TEXT_COLOR,
  type NodeVariant,
} from './diagramTypes';
import { FONT_SIZE, GROUP_LABEL_HEIGHT, LINE_HEIGHT, type DiagramLayout, type LaidOutEdge } from './layout';

function escapeXml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function renderMultilineText(x: number, y: number, height: number, label: string): string {
  const lines = label.split('\n');
  const totalHeight = lines.length * LINE_HEIGHT;
  const startY = y + (height - totalHeight) / 2 + LINE_HEIGHT * 0.72;
  return lines
    .map((line, i) => `<text x="${x}" y="${startY + i * LINE_HEIGHT}" text-anchor="middle" font-size="${FONT_SIZE}">${escapeXml(line)}</text>`)
    .join('');
}

function renderGroupBoxes(layout: DiagramLayout): string {
  return layout.groups
    .map((group) => {
      return `<rect x="${group.x}" y="${group.y}" width="${group.width}" height="${group.height}" rx="10" fill="${GROUP_FILL}" stroke="${GROUP_BORDER}" stroke-width="1.5" />`;
    })
    .join('\n');
}

function renderGroupLabels(layout: DiagramLayout): string {
  return layout.groups
    .map((group) => {
      return `<g><rect x="${group.x + 8}" y="${group.y + 4}" width="${Math.min(group.width - 16, group.label.length * 8 + 12)}" height="${GROUP_LABEL_HEIGHT - 6}" fill="${GROUP_FILL}" opacity="0.92" /><text x="${group.x + 14}" y="${group.y + GROUP_LABEL_HEIGHT - 8}" font-size="${FONT_SIZE}" font-weight="600" fill="${TEXT_COLOR}">${escapeXml(group.label)}</text></g>`;
    })
    .join('\n');
}

function renderNodes(layout: DiagramLayout): string {
  return layout.nodes
    .map((node) => {
      const style = NODE_VARIANT_STYLES[node.variant as NodeVariant];
      return `<g>
        <rect x="${node.x}" y="${node.y}" width="${node.width}" height="${node.height}" rx="8" fill="${style.fill}" stroke="${style.stroke}" stroke-width="${style.strokeWidth}" />
        ${renderMultilineText(node.x + node.width / 2, node.y, node.height, node.label)}
      </g>`;
    })
    .join('\n');
}

function pathFromPoints(points: { x: number; y: number }[]): string {
  return points.map((point, i) => `${i === 0 ? 'M' : 'L'} ${point.x} ${point.y}`).join(' ');
}

function renderEdgePath(edge: LaidOutEdge): string {
  const dashArray = edge.style === 'dashed' ? ' stroke-dasharray="6 4"' : '';
  const marker = edge.style === 'plain' ? '' : ' marker-end="url(#arrowhead)"';
  return `<path d="${pathFromPoints(edge.points)}" fill="none" stroke="${EDGE_STROKE_MUTED}" stroke-width="1.5"${dashArray}${marker} />`;
}

function renderEdgeLabel(edge: LaidOutEdge): string {
  if (!edge.label) return '';
  const mid = edge.points[Math.floor(edge.points.length / 2)];
  const labelLines = edge.label.split('\n');
  const labelWidth = Math.max(...labelLines.map((line) => line.length)) * 6.4 + 10;
  const labelHeight = labelLines.length * LINE_HEIGHT;
  const labelX = mid.x - labelWidth / 2;
  const labelY = mid.y - labelHeight / 2;
  const labelBg = `<rect x="${labelX}" y="${labelY}" width="${labelWidth}" height="${labelHeight}" fill="#FFFFFF" opacity="0.95" stroke="#EDEBE9" stroke-width="0.5" />`;
  const labelText = labelLines
    .map(
      (line, i) =>
        `<text x="${mid.x}" y="${labelY + (i + 1) * LINE_HEIGHT - 5}" text-anchor="middle" font-size="12" fill="${TEXT_COLOR}">${escapeXml(line)}</text>`,
    )
    .join('');
  return `<g>${labelBg}${labelText}</g>`;
}

/** Builds a clean, self-contained SVG (real <rect>/<text>/<path>, no foreignObject/HTML) so it
 *  renders identically and safely everywhere an <img> can point at an SVG — GitHub's markdown
 *  image pipeline included. This is the checked-in artifact `docs:render-diagrams` produces.
 *
 *  Paint order matters: group boxes → edge paths → nodes → group labels → edge labels, so no
 *  label text is ever hidden behind a node/group box (edge labels paint last, on top of
 *  everything, so dense diagrams stay crowded-but-legible instead of clipped). */
export function buildSvg(layout: DiagramLayout, title: string): string {
  const arrowMarker = `<marker id="arrowhead" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8 Z" fill="${EDGE_STROKE_MUTED}" /></marker>`;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${layout.width}" height="${layout.height}" viewBox="0 0 ${layout.width} ${layout.height}" font-family="${FONT_FAMILY}" fill="${TEXT_COLOR}">
  <title>${escapeXml(title)}</title>
  <defs>${arrowMarker}</defs>
  <rect x="0" y="0" width="${layout.width}" height="${layout.height}" fill="#FFFFFF" />
  ${renderGroupBoxes(layout)}
  ${layout.edges.map(renderEdgePath).join('\n')}
  ${renderNodes(layout)}
  ${renderGroupLabels(layout)}
  ${layout.edges.map(renderEdgeLabel).join('\n')}
</svg>`;
}
