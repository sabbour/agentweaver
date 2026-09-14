import { readFileSync } from 'node:fs';

export const DESIGN_SYSTEM = JSON.parse(readFileSync(
  new URL('../../docs/diagrams/drawio/design-system.json', import.meta.url), 'utf8',
));

const { palette: p, typography: t, cards: c, layout: l } = DESIGN_SYSTEM;
export const DESIGN_TOKENS = Object.freeze({
  colors: Object.freeze({
    canvas: p.canvas, surface: p.surface, group1: p.groupTier1, group2: p.groupTier2,
    stroke: p.stroke, ink: p.foreground1, inkStrong: p.foreground2,
    inkMuted: p.foreground3, inkFaint: p.foreground4, connector: p.connector,
    loopback: p.loopback,
  }),
  badges: Object.freeze(DESIGN_SYSTEM.badges),
  typography: Object.freeze({
    family: t.family, monoFamily: t.monospaceFamily, titleSize: t.titlePx,
    subtitleSize: t.subtitlePx, metaSize: t.metaPx, badgeSize: t.badgePx,
    edgeSize: t.connectorLabelPx,
  }),
  geometry: Object.freeze({
    cardWidth: c.widthPx, cardMinHeight: c.minimumHeightPx,
    cardThreeLineHeight: c.threeLineHeightPx, cardRadius: c.radiusPx,
    accentWidth: c.semanticAccentPx, iconSize: c.iconPx, iconInset: c.iconInsetPx,
    textInset: c.textInsetPx, padding: c.paddingPx,
    groupRadius: c.radiusPx, ...l,
  }),
});

export function cardContentMetrics(node) {
  const lines=(value,capacity)=>String(value).split('\n')
    .reduce((sum,line)=>sum+Math.max(1,Math.ceil(line.length/capacity)),0);
  const title=lines(node.label??node.id,23)*t.titlePx*t.titleLineHeight;
  const subtitle=node.subLabel?lines(node.subLabel,28)*t.subtitlePx*t.subtitleLineHeight+2:0;
  const meta=node.meta?t.metaPx*1.2+2:0;
  const content=title+subtitle+meta;
  return {title,subtitle,meta,content,
    height:content+2*c.paddingPx<=c.minimumHeightPx?c.minimumHeightPx:c.threeLineHeightPx};
}
