// Resolved static hex values -- NOT live CSS custom properties / Fluent
// theme tokens -- because this app renders headlessly (Playwright capture,
// no <FluentProvider> in the real product's DOM tree). Values are pulled
// from two real sources so the exported diagrams match production exactly:
//
//   1. Neutral surface/foreground/border/radius values come from
//      apps/web/src/theme.ts's `agentweaverLightTheme` (the theme the
//      product's <FluentProvider> actually uses in apps/web/src/App.tsx) --
//      Agentweaver's warm-monochrome palette, not Fluent's default blue-gray.
//   2. Badge tone colors come from `webLightTheme`'s default Fluent palette
//      tokens (colorPalette*Background2 / *Foreground2), resolved via
//      `require('@fluentui/react-components').webLightTheme.<token>` --
//      `agentweaverLightTheme` does not override these, so the shipped app
//      renders these exact hex values for any palette-tone badge today.
//
// If apps/web/src/theme.ts changes its neutral ramp, re-resolve these by
// hand (there is intentionally no runtime dependency on @fluentui/react-components'
// full theme object here, to keep this capture-only app tiny and buildable
// without a live FluentProvider).

export const neutral = {
  background1: '#fdfbf8', // card surface (agentweaverLightTheme colorNeutralBackground1)
  background2: '#f8f4f1', // canvas / tier-1 group fill (colorNeutralBackground2)
  background3: '#efeae7', // tier-2 (nested) group fill (colorNeutralBackground3)
  stroke2: '#ece7e3', // card + container border (colorNeutralStroke2)
  foreground1: '#272320', // card title ink (colorNeutralForeground1)
  foreground2: '#3f3935', // card icon / strong subtext (colorNeutralForeground2)
  foreground3: '#635c57', // card subLabel muted text (colorNeutralForeground3)
  foreground4: '#746d68', // card meta / monospace faint text (colorNeutralForeground4)
  badgeNeutralBg: '#e7e1dc', // colorNeutralBackground4-equivalent for "neutral" tone badges
  badgeNeutralFg: '#635c57',
} as const;

export const radius = {
  card: '16px', // borderRadiusXLarge
  container: '8px',
  badge: '9999px', // borderRadiusCircular
} as const;

export type BadgeTone = 'lavender' | 'teal' | 'green' | 'marigold' | 'neutral';

export const badgeTones: Record<BadgeTone, { bg: string; fg: string }> = {
  lavender: { bg: '#d2ccf8', fg: '#3f3682' }, // colorPaletteLavenderBackground2 / Foreground2
  teal: { bg: '#a6e9ed', fg: '#00666d' }, // colorPaletteLightTealBackground2 / Foreground2
  green: { bg: '#9fd89f', fg: '#0e700e' }, // colorPaletteGreenBackground2 / Foreground1
  marigold: { bg: '#f9e2ae', fg: '#835b00' }, // colorPaletteMarigoldBackground2 / Foreground2
  neutral: { bg: neutral.badgeNeutralBg, fg: neutral.badgeNeutralFg },
};

export const fontFamily =
  '"Segoe UI", ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, Roboto, "Helvetica Neue", sans-serif';

export const fontFamilyMonospace =
  '"Cascadia Code", Consolas, "Courier New", monospace';
