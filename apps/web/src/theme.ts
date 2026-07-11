import { webLightTheme, type Theme } from '@fluentui/react-components';

/**
 * Agentweaver light theme — warm-monochrome palette aligned to copilot.com
 * computed tokens (measured 2026-07-10). Brown-tinted, not neutral-gray.
 *
 *   canvas       #f8f4f1  warm paper (body / rail background)
 *   card         #fdfbf8  near-white panel surface (subtly lighter than canvas)
 *   ink          #272320  warm near-black foreground
 *   selected     #efeae7  warm hover / selected fill
 *   pressed      #e7e1dc  warm pressed fill / bg4
 *   border       #e2ddd9  warm stroke-1
 *
 * No blue accent anywhere. Primary actions, links, focus, selection = warm ink.
 * Radius: controls 8px, nav rows 12px (Large), panels 16px (XLarge). Light only.
 */

const ink       = '#272320';   // warm near-black
const inkHover  = '#3f3935';   // warm dark-brown hover
const inkPressed = '#1c1815';  // darkest press
const onBrand   = '#faf6f2';   // text on near-black brand surfaces
const canvas    = '#f8f4f1';   // warm canvas / sidebar background
const card      = '#fdfbf8';   // lighter warm panel / card surface
const selected  = '#efeae7';   // hover + selected fill (bg3)
const pressed   = '#e7e1dc';   // pressed fill (bg4)
const border    = '#e2ddd9';   // warm stroke-1

export const agentweaverLightTheme: Theme = {
  ...webLightTheme,

  // Typography — Copilot uses Ginto; M uses Segoe Sans; we ship Segoe UI + system fallback.
  fontFamilyBase:
    '"Segoe UI", ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, Roboto, "Helvetica Neue", sans-serif',

  // Body base = 15px / 1.5 (DESIGN.md — a hair denser than Copilot's 16px).
  // Fluent's default <Text> and controls read fontSizeBase300; setting it here
  // makes the whole app's default prose coherent from one source. The named
  // roles in components/ui/typography.ts carry the rest of the scale.
  fontSizeBase300:   '15px',
  lineHeightBase300: '22px',

  // Radius — controls 8px, nav rows / large controls 12px, panels 16px, pills 9999px.
  borderRadiusNone:     '0',
  borderRadiusSmall:    '6px',
  borderRadiusMedium:   '8px',
  borderRadiusLarge:    '12px',
  borderRadiusXLarge:   '16px',
  borderRadiusCircular: '9999px',

  // Neutral surfaces (warm brown-tinted).
  colorNeutralBackground1:          card,      // #fdfbf8
  colorNeutralBackground1Hover:     selected,  // #efeae7
  colorNeutralBackground1Pressed:   pressed,   // #e7e1dc
  colorNeutralBackground1Selected:  selected,
  colorNeutralBackground2:          canvas,    // #f8f4f1
  colorNeutralBackground2Hover:     selected,
  colorNeutralBackground2Pressed:   pressed,
  colorNeutralBackground3:          selected,  // #efeae7
  colorNeutralBackground4:          pressed,   // #e7e1dc
  colorNeutralBackground5:          '#e0d9d3',
  colorNeutralBackground6:          '#d9d2cb',
  colorNeutralBackgroundStatic:     ink,
  colorNeutralBackgroundDisabled:   '#f0f0f0',
  colorSubtleBackground:            'transparent',
  colorSubtleBackgroundHover:       selected,
  colorSubtleBackgroundPressed:     pressed,
  colorSubtleBackgroundSelected:    selected,

  // Neutral foreground (warm taupe ink, not neutral gray).
  colorNeutralForeground1:         ink,       // #272320
  colorNeutralForeground1Hover:    ink,
  colorNeutralForeground1Pressed:  ink,
  colorNeutralForeground1Selected: ink,
  colorNeutralForeground2:         inkHover,  // #3f3935
  colorNeutralForeground2Hover:    ink,
  colorNeutralForeground2Pressed:  ink,
  colorNeutralForeground2Selected: ink,
  colorNeutralForeground3:         '#635c57', // warm taupe muted
  colorNeutralForeground4:         '#746d68', // warm quiet metadata
  colorNeutralForegroundDisabled:  '#bdbdbd',
  colorNeutralForegroundOnBrand:           onBrand,
  colorNeutralForegroundStaticInverted:    onBrand,
  colorNeutralForegroundInverted:          onBrand,

  // Strokes / borders (warm).
  colorNeutralStroke1:          border,      // #e2ddd9
  colorNeutralStroke1Hover:     '#d8d2cd',
  colorNeutralStroke1Pressed:   '#cec7c1',
  colorNeutralStroke1Selected:  '#d8d2cd',
  colorNeutralStroke2:          '#ece7e3',
  colorNeutralStroke3:          '#f2ede9',
  colorNeutralStrokeAccessible: '#635c57',
  colorNeutralStrokeDisabled:   '#e0e0e0',

  // Brand ramp = warm near-black (monochrome primary). No blue.
  colorBrandBackground:          ink,
  colorBrandBackgroundHover:     inkHover,
  colorBrandBackgroundPressed:   inkPressed,
  colorBrandBackgroundSelected:  inkHover,
  colorBrandBackground2:         selected,
  colorBrandBackground2Hover:    pressed,
  colorBrandBackground2Pressed:  '#e0d9d3',
  colorBrandBackgroundStatic:    ink,
  colorBrandBackgroundInverted:         card,
  colorBrandBackgroundInvertedHover:    selected,
  colorBrandBackgroundInvertedPressed:  pressed,
  colorBrandBackgroundInvertedSelected: selected,

  colorCompoundBrandBackground:        ink,
  colorCompoundBrandBackgroundHover:   inkHover,
  colorCompoundBrandBackgroundPressed: inkPressed,

  colorBrandForeground1:        ink,
  colorBrandForeground2:        inkHover,
  colorBrandForeground2Hover:   ink,
  colorBrandForeground2Pressed: inkPressed,
  colorCompoundBrandForeground1:        ink,
  colorCompoundBrandForeground1Hover:   inkHover,
  colorCompoundBrandForeground1Pressed: inkPressed,

  colorBrandForegroundLink:         ink,
  colorBrandForegroundLinkHover:    inkPressed,
  colorBrandForegroundLinkPressed:  inkPressed,
  colorBrandForegroundLinkSelected: ink,
  colorBrandForegroundInverted:         card,
  colorBrandForegroundInvertedHover:    selected,
  colorBrandForegroundInvertedPressed:  selected,
  colorBrandForegroundOnLight:         ink,
  colorBrandForegroundOnLightHover:    inkHover,
  colorBrandForegroundOnLightPressed:  inkPressed,
  colorBrandForegroundOnLightSelected: ink,

  colorBrandStroke1:          ink,
  colorBrandStroke2:          border,
  colorBrandStroke2Hover:     '#d8d2cd',
  colorBrandStroke2Pressed:   '#cec7c1',
  colorBrandStroke2Contrast:  ink,
  colorCompoundBrandStroke:        ink,
  colorCompoundBrandStrokeHover:   inkHover,
  colorCompoundBrandStrokePressed: inkPressed,

  // Focus stroke — warm mid-gray soft ring (M --ring: hsl(0,0%,44%) ≈ #707070, warm shift).
  // Softer than near-black ink so focus-visible on inputs/buttons feels like M's
  // 3px ring at 50% opacity rather than a hard black outline.
  colorStrokeFocus2: '#8c837c',

  // Destructive / danger tokens — warm crimson (M --destructive: hsl(343 67% 39%)).
  colorPaletteRedForeground1:   '#a62147',
  colorPaletteRedForeground2:   '#881838',
  colorPaletteRedForeground3:   '#c43055',
  colorPaletteRedBackground1:   '#fdf0f3',
  colorPaletteRedBackground2:   '#fad7df',
  colorStatusDangerForeground1: '#a62147',
  colorStatusDangerBackground1: '#fdf0f3',
  colorStatusDangerBackground2: '#fad7df',
};
