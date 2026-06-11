---
version: "1.0"
name: "Fluent 2 Design System"
description: >
  Design language for this project, implemented with @fluentui/react-components v9 (Fluent 2).
  All tokens are sourced from the microsoft/fluentui repository (web light theme, Communication Blue brand).
  Theme switching (dark / high-contrast) is handled automatically by FluentProvider.

colors:
  # ── Brand ─────────────────────────────────────────────────────────────────
  brand-primary: "#0078d4"          # brand[80]  colorBrandBackground / colorBrandForeground1
  brand-hover: "#106ebe"            # brand[70]  colorBrandBackgroundHover / colorBrandForeground2
  brand-selected: "#005a9e"         # brand[60]  colorBrandBackgroundSelected
  brand-pressed: "#004578"          # brand[40]  colorBrandBackgroundPressed
  brand-tint: "#eff6fc"             # brand[160] colorBrandBackground2
  brand-stroke: "#0078d4"           # brand[80]  colorBrandStroke1
  brand-stroke-subtle: "#c7e0f4"    # brand[140] colorBrandStroke2

  # ── Neutral Foreground / Text ─────────────────────────────────────────────
  colorNeutralForeground1: "#242424"     # grey[14] — primary text
  colorNeutralForeground2: "#424242"     # grey[26] — secondary text
  colorNeutralForeground3: "#616161"     # grey[38] — subtle / metadata
  colorNeutralForeground4: "#707070"     # grey[44] — decorative / tertiary
  colorNeutralForegroundDisabled: "#bdbdbd"   # grey[74]
  colorNeutralForegroundOnBrand: "#ffffff"    # white
  colorNeutralForegroundInverted: "#ffffff"   # white
  colorBrandForeground1: "#0078d4"            # brand[80]
  colorBrandForeground2: "#106ebe"            # brand[70]
  colorBrandForegroundLink: "#106ebe"         # brand[70]

  # ── Neutral Backgrounds ───────────────────────────────────────────────────
  colorNeutralBackground1: "#ffffff"     # white — cards, inputs (highest elevation)
  colorNeutralBackground1Hover: "#f5f5f5"
  colorNeutralBackground1Pressed: "#e0e0e0"
  colorNeutralBackground1Selected: "#ebebeb"
  colorNeutralBackground2: "#fafafa"     # grey[98] — page canvas
  colorNeutralBackground2Hover: "#f0f0f0"
  colorNeutralBackground3: "#f5f5f5"     # grey[96] — hover fills
  colorNeutralBackground4: "#f0f0f0"     # grey[94] — pressed fills
  colorNeutralBackground5: "#ebebeb"     # grey[92] — selected fills
  colorNeutralBackground6: "#e6e6e6"     # grey[90] — disabled fills
  colorNeutralBackgroundInverted: "#292929"   # grey[16]
  colorNeutralBackgroundDisabled: "#f0f0f0"   # grey[94]
  colorNeutralCardBackground: "#fafafa"       # grey[98]
  colorBrandBackground: "#0078d4"
  colorBrandBackgroundHover: "#106ebe"
  colorBrandBackgroundPressed: "#004578"
  colorBrandBackgroundSelected: "#005a9e"
  colorBrandBackground2: "#eff6fc"

  # ── Subtle / Transparent ─────────────────────────────────────────────────
  # colorSubtleBackground: transparent  (documented in prose)
  colorSubtleBackgroundHover: "#f5f5f5"
  colorSubtleBackgroundPressed: "#e0e0e0"
  colorSubtleBackgroundSelected: "#ebebeb"
  # colorTransparentBackground: transparent (documented in prose)

  # ── Strokes / Borders ─────────────────────────────────────────────────────
  colorNeutralStroke1: "#d1d1d1"          # grey[82] — default border
  colorNeutralStroke1Hover: "#c7c7c7"     # grey[78]
  colorNeutralStroke1Pressed: "#b3b3b3"   # grey[70]
  colorNeutralStroke2: "#e0e0e0"          # grey[88] — subtle divider
  colorNeutralStroke3: "#f0f0f0"          # grey[94]
  colorNeutralStrokeDisabled: "#e0e0e0"   # grey[88]
  colorNeutralStrokeOnBrand: "#ffffff"
  colorNeutralStrokeAccessible: "#616161" # grey[38]
  colorBrandStroke1: "#0078d4"
  colorBrandStroke2: "#c7e0f4"
  colorCompoundBrandStroke: "#0078d4"
  colorStrokeFocus1: "#ffffff"
  colorStrokeFocus2: "#000000"

  # ── Status — Warning (orange) ─────────────────────────────────────────────
  colorStatusWarningBackground1: "#fff9f5"
  colorStatusWarningBackground2: "#fdcfb4"
  colorStatusWarningBackground3: "#faa06b"
  colorStatusWarningForeground1: "#bc4b09"
  colorStatusWarningForeground2: "#de590b"
  colorStatusWarningForeground3: "#8a3707"
  colorStatusWarningBorderActive: "#f7630c"
  colorStatusWarningBorder1: "#fdcfb4"

  # ── Status — Danger (cranberry) ───────────────────────────────────────────
  colorStatusDangerBackground1: "#fdf3f4"
  colorStatusDangerBackground2: "#eeacb2"
  colorStatusDangerBackground3: "#dc626d"
  colorStatusDangerBackground1Hover: "#f6d1d5"
  colorStatusDangerForeground1: "#c50f1f"
  colorStatusDangerForeground2: "#b10e1c"
  colorStatusDangerForeground3: "#6e0811"
  colorStatusDangerBorderActive: "#c50f1f"
  colorStatusDangerBorder1: "#eeacb2"

  # ── Status — Success (green) ──────────────────────────────────────────────
  colorStatusSuccessBackground1: "#f1faf1"
  colorStatusSuccessBackground2: "#9fd89f"
  colorStatusSuccessBackground3: "#359b35"
  colorStatusSuccessForeground1: "#107c10"
  colorStatusSuccessForeground2: "#0e700e"
  colorStatusSuccessForeground3: "#094509"
  colorStatusSuccessBorderActive: "#218c21"
  colorStatusSuccessBorder1: "#9fd89f"

  # ── Status — Informative (royalBlue) ─────────────────────────────────────
  colorStatusInformativeBackground1: "#f0f6fa"
  colorStatusInformativeBackground2: "#9abfdc"
  colorStatusInformativeBackground3: "#286fa8"
  colorStatusInformativeForeground1: "#004e8c"
  colorStatusInformativeForeground2: "#00467e"
  colorStatusInformativeForeground3: "#002c4e"
  colorStatusInformativeBorderActive: "#125e9a"
  colorStatusInformativeBorder1: "#9abfdc"

typography:
  # Fluent 2 Web type ramp — size / line-height / weight / usage
  Caption2:
    size: "10px"
    lineHeight: "14px"
    weight: 400
    token: fontSizeBase100 / lineHeightBase100
  Caption1:
    size: "12px"
    lineHeight: "16px"
    weight: 400
    token: fontSizeBase200 / lineHeightBase200
  Body1:
    size: "14px"
    lineHeight: "20px"
    weight: 400
    token: fontSizeBase300 / lineHeightBase300
    note: default body text
  Subtitle2:
    size: "16px"
    lineHeight: "22px"
    weight: 600
    token: fontSizeBase400 / lineHeightBase400
  Subtitle1:
    size: "20px"
    lineHeight: "26px"
    weight: 600
    token: fontSizeBase500 / lineHeightBase500
  Title3:
    size: "24px"
    lineHeight: "32px"
    weight: 600
    token: fontSizeBase600 / lineHeightBase600
  Title2:
    size: "28px"
    lineHeight: "36px"
    weight: 600
    token: fontSizeHero700 / lineHeightHero700
  Title1:
    size: "32px"
    lineHeight: "40px"
    weight: 700
    token: fontSizeHero800 / lineHeightHero800
  LargeTitle:
    size: "40px"
    lineHeight: "52px"
    weight: 700
    token: fontSizeHero900 / lineHeightHero900
  Display:
    size: "68px"
    lineHeight: "92px"
    weight: 700
    token: fontSizeHero1000 / lineHeightHero1000

  font-families:
    base: '"Segoe UI", "Segoe UI Web (West European)", -apple-system, BlinkMacSystemFont, Roboto, "Helvetica Neue", sans-serif'
    monospace: 'Consolas, "Courier New", Courier, monospace'
    numeric: 'Bahnschrift, "DIN Alternate", "Franklin Gothic Medium", "Nimbus Sans Narrow", sans-serif-condensed, sans-serif'

spacing:
  None: "0px"
  XXS: "2px"     # spacingHorizontalXXS  / spacingVerticalXXS
  XS: "4px"      # spacingHorizontalXS   / spacingVerticalXS
  SNudge: "6px"  # spacingHorizontalSNudge
  S: "8px"       # spacingHorizontalS    / spacingVerticalS
  MNudge: "10px" # spacingHorizontalMNudge
  M: "12px"      # spacingHorizontalM    / spacingVerticalM
  L: "16px"      # spacingHorizontalL    / spacingVerticalL
  XL: "20px"     # spacingHorizontalXL   / spacingVerticalXL
  XXL: "24px"    # spacingHorizontalXXL  / spacingVerticalXXL
  XXXL: "32px"   # spacingHorizontalXXXL / spacingVerticalXXXL

rounded:
  None: "0px"         # borderRadiusNone
  Small: "2px"        # borderRadiusSmall
  Medium: "4px"       # borderRadiusMedium   (default for most controls)
  Large: "6px"        # borderRadiusLarge
  XLarge: "8px"       # borderRadiusXLarge
  Circular: "9999px"  # borderRadiusCircular

components:
  button-primary:
    background: "#0078d4"
    background-hover: "#106ebe"
    background-pressed: "#004578"
    color: "#ffffff"
    border: "none"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600
    padding: "5px 12px"

  button-outline:
    background: "transparent"
    background-hover: "#f5f5f5"
    background-pressed: "#e0e0e0"
    color: "#0078d4"
    border: "1px solid #d1d1d1"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600

  button-subtle:
    background: "transparent"
    background-hover: "#f5f5f5"
    background-pressed: "#e0e0e0"
    color: "#242424"
    border: "none"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600

  button-transparent:
    background: "transparent"
    background-hover: "transparent"
    color: "#0078d4"
    border: "none"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600

  button-warning-primary:
    background: "#f7630c"
    background-hover: "#bc4b09"
    color: "#ffffff"
    border: "none"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600

  button-warning-outline:
    background: "#fff9f5"
    background-hover: "#fdcfb4"
    color: "#bc4b09"
    border: "1px solid #fdcfb4"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600

  button-danger-outline:
    background: "#fdf3f4"
    background-hover: "#f6d1d5"
    color: "#c50f1f"
    border: "1px solid #eeacb2"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600

  button-success-outline:
    background: "#f1faf1"
    background-hover: "#9fd89f"
    color: "#107c10"
    border: "1px solid #9fd89f"
    border-radius: "4px"
    font-size: "14px"
    font-weight: 600

  card:
    background: "#ffffff"
    border: "1px solid #e0e0e0"
    border-radius: "4px"
    shadow: "0 0 2px rgba(0,0,0,0.12), 0 1px 2px rgba(0,0,0,0.14)"
    padding: "16px"

  card-filled:
    background: "#fafafa"
    border: "1px solid #e0e0e0"
    border-radius: "4px"
    shadow: "none"
    padding: "16px"

  badge-informative:
    background: "#f0f6fa"
    color: "#004e8c"
    border: "1px solid #9abfdc"
    border-radius: "9999px"
    font-size: "12px"
    font-weight: 600

  badge-warning:
    background: "#fff9f5"
    color: "#bc4b09"
    border: "1px solid #fdcfb4"
    border-radius: "9999px"
    font-size: "12px"
    font-weight: 600

  badge-danger:
    background: "#fdf3f4"
    color: "#c50f1f"
    border: "1px solid #eeacb2"
    border-radius: "9999px"
    font-size: "12px"
    font-weight: 600

  badge-success:
    background: "#f1faf1"
    color: "#107c10"
    border: "1px solid #9fd89f"
    border-radius: "9999px"
    font-size: "12px"
    font-weight: 600

  input:
    background: "#ffffff"
    background-disabled: "#f0f0f0"
    color: "#242424"
    color-placeholder: "#707070"
    border: "1px solid #d1d1d1"
    border-hover: "1px solid #c7c7c7"
    border-focus: "2px solid #0078d4"
    border-radius: "4px"
    font-size: "14px"
    padding: "5px 12px"

  tooltip:
    background: "#292929"
    color: "#ffffff"
    border-radius: "4px"
    font-size: "12px"
    padding: "4px 8px"
    shadow: "0 0 2px rgba(0,0,0,0.12), 0 4px 8px rgba(0,0,0,0.14)"
---

# Fluent 2 Design System

**Implementation:** `@fluentui/react-components` v9  
**Theme:** Web Light (Communication Blue brand)  
**Source:** [microsoft/fluentui](https://github.com/microsoft/fluentui) — tokens are authoritative; do not invent values.

---

## Overview

This project uses **Fluent 2**, Microsoft's second-generation design system. All visual decisions — color, typography, spacing, motion — are expressed through design tokens exposed by `@fluentui/tokens`. `FluentProvider` resolves the correct CSS custom properties at runtime, so dark mode and high-contrast mode require zero additional code in components.

**Key principles:**
- **Semantic tokens over raw values.** Always reference alias tokens (e.g., `colorNeutralForeground1`) rather than global palette values (e.g., `grey[14]`).
- **Elevation communicates hierarchy.** Shadows and background layers indicate depth; use the correct layer for each surface.
- **Motion is purposeful.** Animations use the defined duration + easing curve tokens.
- **Accessibility first.** Color contrast, focus rings, and ARIA are built into Fluent components.

---

## Colors

### Brand palette (Communication Blue)

The web brand maps to the `blue` color family. These tokens adapt automatically across themes.

| Token | Light value | Usage |
|---|---|---|
| `colorBrandBackground` | `#0078d4` | Primary button fill, selected indicators |
| `colorBrandBackgroundHover` | `#106ebe` | Primary button hover |
| `colorBrandBackgroundSelected` | `#005a9e` | Primary button selected/active |
| `colorBrandBackgroundPressed` | `#004578` | Primary button pressed |
| `colorBrandBackground2` | `#eff6fc` | Light brand tint backgrounds |
| `colorBrandForeground1` | `#0078d4` | Brand-colored text, icon on light bg |
| `colorBrandForeground2` | `#106ebe` | Brand-colored text hover |
| `colorBrandForegroundLink` | `#106ebe` | Hyperlinks |
| `colorBrandStroke1` | `#0078d4` | Brand border, compound checkbox stroke |
| `colorBrandStroke2` | `#c7e0f4` | Subtle brand border |
| `colorCompoundBrandStroke` | `#0078d4` | Checkbox / toggle compound border |
| `colorNeutralForegroundOnBrand` | `#ffffff` | Text/icons rendered on brand fill |

### Global grey scale

The full grey scale underpins all neutral tokens. Reference only via alias tokens.

| Step | Value | Step | Value | Step | Value |
|---|---|---|---|---|---|
| grey[2] | `#050505` | grey[38] | `#616161` | grey[74] | `#bdbdbd` |
| grey[4] | `#0a0a0a` | grey[40] | `#666666` | grey[76] | `#c2c2c2` |
| grey[6] | `#0f0f0f` | grey[44] | `#707070` | grey[78] | `#c7c7c7` |
| grey[8] | `#141414` | grey[46] | `#757575` | grey[80] | `#cccccc` |
| grey[10] | `#1a1a1a` | grey[50] | `#808080` | grey[82] | `#d1d1d1` |
| grey[12] | `#1f1f1f` | grey[52] | `#858585` | grey[84] | `#d6d6d6` |
| grey[14] | `#242424` | grey[54] | `#8a8a8a` | grey[86] | `#dbdbdb` |
| grey[16] | `#292929` | grey[56] | `#8f8f8f` | grey[88] | `#e0e0e0` |
| grey[18] | `#2e2e2e` | grey[58] | `#949494` | grey[90] | `#e6e6e6` |
| grey[20] | `#333333` | grey[60] | `#999999` | grey[92] | `#ebebeb` |
| grey[22] | `#383838` | grey[62] | `#9e9e9e` | grey[94] | `#f0f0f0` |
| grey[24] | `#3d3d3d` | grey[64] | `#a3a3a3` | grey[96] | `#f5f5f5` |
| grey[26] | `#424242` | grey[66] | `#a8a8a8` | grey[98] | `#fafafa` |
| grey[28] | `#474747` | grey[68] | `#adadad` | grey[99] | `#fcfcfc` |
| grey[30] | `#4d4d4d` | grey[70] | `#b3b3b3` | white | `#ffffff` |
| grey[32] | `#525252` | grey[72] | `#b8b8b8` | black | `#000000` |
| grey[34] | `#575757` | | | | |
| grey[36] | `#5c5c5c` | | | | |

### Neutral foreground / text tokens

| Token | Value | Usage |
|---|---|---|
| `colorNeutralForeground1` | `#242424` | Primary text — body copy, headings |
| `colorNeutralForeground2` | `#424242` | Secondary text — labels, secondary copy |
| `colorNeutralForeground3` | `#616161` | Subtle / metadata — timestamps, captions |
| `colorNeutralForeground4` | `#707070` | Decorative / tertiary — less emphasis |
| `colorNeutralForegroundDisabled` | `#bdbdbd` | Disabled state text |
| `colorNeutralForegroundInverted` | `#ffffff` | Text on dark/inverted surfaces |
| `colorNeutralForegroundOnBrand` | `#ffffff` | Text/icons on brand-colored fills |
| `colorNeutralStrokeAccessible` | `#616161` | Meets AA contrast for borders |

### Neutral background tokens

| Token | Value | Usage |
|---|---|---|
| `colorNeutralBackground1` | `#ffffff` | Cards, dialogs, inputs — highest layer |
| `colorNeutralBackground1Hover` | `#f5f5f5` | Hovered state of bg1 |
| `colorNeutralBackground1Pressed` | `#e0e0e0` | Pressed state of bg1 |
| `colorNeutralBackground1Selected` | `#ebebeb` | Selected state of bg1 |
| `colorNeutralBackground2` | `#fafafa` | Page canvas, panel backgrounds |
| `colorNeutralBackground2Hover` | `#f0f0f0` | Hovered state of bg2 |
| `colorNeutralBackground3` | `#f5f5f5` | Hover fills (list items) |
| `colorNeutralBackground4` | `#f0f0f0` | Pressed fills |
| `colorNeutralBackground5` | `#ebebeb` | Selected fills |
| `colorNeutralBackground6` | `#e6e6e6` | Disabled fills |
| `colorNeutralBackgroundInverted` | `#292929` | Inverted/dark surfaces |
| `colorNeutralBackgroundDisabled` | `#f0f0f0` | Disabled control backgrounds |
| `colorNeutralCardBackground` | `#fafafa` | Card surface fill |
| `colorSubtleBackground` | `transparent` | Default for ghost/subtle elements |
| `colorSubtleBackgroundHover` | `#f5f5f5` | Ghost element hover |
| `colorSubtleBackgroundPressed` | `#e0e0e0` | Ghost element pressed |
| `colorSubtleBackgroundSelected` | `#ebebeb` | Ghost element selected |
| `colorTransparentBackground` | `transparent` | Fully transparent surfaces |

### Stroke / border tokens

| Token | Value | Usage |
|---|---|---|
| `colorNeutralStroke1` | `#d1d1d1` | Default control borders |
| `colorNeutralStroke1Hover` | `#c7c7c7` | Border on hover |
| `colorNeutralStroke1Pressed` | `#b3b3b3` | Border on press |
| `colorNeutralStroke2` | `#e0e0e0` | Subtle dividers |
| `colorNeutralStroke3` | `#f0f0f0` | Very subtle structural lines |
| `colorNeutralStrokeDisabled` | `#e0e0e0` | Disabled borders |
| `colorNeutralStrokeOnBrand` | `#ffffff` | Border on brand fill |
| `colorStrokeFocus1` | `#ffffff` | Inner focus ring |
| `colorStrokeFocus2` | `#000000` | Outer focus ring |

**Shadow color primitives** (used in `box-shadow` only, not as fills):

| Token | Value |
|---|---|
| `colorNeutralShadowAmbient` | `rgba(0,0,0,0.12)` |
| `colorNeutralShadowKey` | `rgba(0,0,0,0.14)` |

### Status semantic tokens

#### Warning (orange palette)

| Token | Value |
|---|---|
| `colorStatusWarningBackground1` | `#fff9f5` |
| `colorStatusWarningBackground2` | `#fdcfb4` |
| `colorStatusWarningBackground3` | `#faa06b` |
| `colorStatusWarningForeground1` | `#bc4b09` |
| `colorStatusWarningForeground2` | `#de590b` |
| `colorStatusWarningForeground3` | `#8a3707` |
| `colorStatusWarningBorderActive` | `#f7630c` |
| `colorStatusWarningBorder1` | `#fdcfb4` |

#### Danger (cranberry palette)

| Token | Value |
|---|---|
| `colorStatusDangerBackground1` | `#fdf3f4` |
| `colorStatusDangerBackground1Hover` | `#f6d1d5` |
| `colorStatusDangerBackground2` | `#eeacb2` |
| `colorStatusDangerBackground3` | `#dc626d` |
| `colorStatusDangerForeground1` | `#c50f1f` |
| `colorStatusDangerForeground2` | `#b10e1c` |
| `colorStatusDangerForeground3` | `#6e0811` |
| `colorStatusDangerBorderActive` | `#c50f1f` |
| `colorStatusDangerBorder1` | `#eeacb2` |

#### Success (green palette)

| Token | Value |
|---|---|
| `colorStatusSuccessBackground1` | `#f1faf1` |
| `colorStatusSuccessBackground2` | `#9fd89f` |
| `colorStatusSuccessBackground3` | `#359b35` |
| `colorStatusSuccessForeground1` | `#107c10` |
| `colorStatusSuccessForeground2` | `#0e700e` |
| `colorStatusSuccessForeground3` | `#094509` |
| `colorStatusSuccessBorderActive` | `#218c21` |
| `colorStatusSuccessBorder1` | `#9fd89f` |

#### Informative (royalBlue palette)

| Token | Value |
|---|---|
| `colorStatusInformativeBackground1` | `#f0f6fa` |
| `colorStatusInformativeBackground2` | `#9abfdc` |
| `colorStatusInformativeBackground3` | `#286fa8` |
| `colorStatusInformativeForeground1` | `#004e8c` |
| `colorStatusInformativeForeground2` | `#00467e` |
| `colorStatusInformativeForeground3` | `#002c4e` |
| `colorStatusInformativeBorderActive` | `#125e9a` |
| `colorStatusInformativeBorder1` | `#9abfdc` |

### Palette colors (shared accent / avatar / badge fills)

50 palette families are available as structured tokens. Each family exposes the pattern:

```
colorPalette{Name}Background1   — lightest tint (icon container background)
colorPalette{Name}Background2   — mid tint (badge background)
colorPalette{Name}Background3   — strong fill (icon background)
colorPalette{Name}Foreground1   — dark shade (primary foreground on tint)
colorPalette{Name}Foreground2   — mid shade
colorPalette{Name}Foreground3   — darkest shade
colorPalette{Name}BorderActive  — primary color (active border)
colorPalette{Name}Border1       — tint border
```

**Available palette families:**

| Group | Families |
|---|---|
| Reds | darkRed, burgundy, cranberry, red |
| Oranges | darkOrange, bronze, pumpkin, orange, peach, marigold |
| Yellows | yellow, gold, brass |
| Browns | brown, darkBrown |
| Greens | lime, forest, seafoam, lightGreen, green, darkGreen |
| Teals | lightTeal, teal, darkTeal |
| Blues | cyan, steel, lightBlue, blue, royalBlue, darkBlue, cornflower, navy |
| Purples | lavender, purple, darkPurple, orchid, grape, berry, lilac |
| Pinks | pink, hotPink, magenta |
| Neutral/Meta | plum, beige, mink, silver, platinum, anchor, charcoal |

**Example usage:**

```tsx
// Cranberry badge
<Badge
  style={{
    backgroundColor: tokens.colorPaletteCranberryBackground2,
    color: tokens.colorPaletteCranberryForeground1,
    borderColor: tokens.colorPaletteCranberryBorder1,
  }}
>
  Error
</Badge>
```

---

## Typography

### Font families

| Token | Value | Usage |
|---|---|---|
| `fontFamilyBase` | `"Segoe UI", "Segoe UI Web (West European)", -apple-system, BlinkMacSystemFont, Roboto, "Helvetica Neue", sans-serif` | All UI text |
| `fontFamilyMonospace` | `Consolas, "Courier New", Courier, monospace` | Code blocks, technical values |
| `fontFamilyNumeric` | `Bahnschrift, "DIN Alternate", "Franklin Gothic Medium", "Nimbus Sans Narrow", sans-serif-condensed, sans-serif` | Tabular numbers, financial data |

### Font weights

| Token | Value | Usage |
|---|---|---|
| `fontWeightRegular` | `400` | Body text, captions |
| `fontWeightMedium` | `500` | Emphasized body, secondary labels |
| `fontWeightSemibold` | `600` | Subtitles, titles, button labels |
| `fontWeightBold` | `700` | Display, Title 1, Large Title |

### Type ramp

| Fluent name | Size token | Size | Line-height token | Line-height | Weight | Use for |
|---|---|---|---|---|---|---|
| Caption 2 | `fontSizeBase100` | `10px` | `lineHeightBase100` | `14px` | 400 | Fine print, legal |
| Caption 1 | `fontSizeBase200` | `12px` | `lineHeightBase200` | `16px` | 400 | Labels, tags, tooltips |
| Body 1 | `fontSizeBase300` | `14px` | `lineHeightBase300` | `20px` | 400 | **Default body text** |
| Subtitle 2 | `fontSizeBase400` | `16px` | `lineHeightBase400` | `22px` | 600 | Section headings, prominent labels |
| Subtitle 1 | `fontSizeBase500` | `20px` | `lineHeightBase500` | `26px` | 600 | Panel headings |
| Title 3 | `fontSizeBase600` | `24px` | `lineHeightBase600` | `32px` | 600 | Card headings, dialog titles |
| Title 2 | `fontSizeHero700` | `28px` | `lineHeightHero700` | `36px` | 600 | Page section titles |
| Title 1 | `fontSizeHero800` | `32px` | `lineHeightHero800` | `40px` | 700 | Page titles |
| Large Title | `fontSizeHero900` | `40px` | `lineHeightHero900` | `52px` | 700 | Hero headings |
| Display | `fontSizeHero1000` | `68px` | `lineHeightHero1000` | `92px` | 700 | Marketing / splash |

**Usage example:**

```tsx
import { Text, makeStyles, tokens } from "@fluentui/react-components";

const useStyles = makeStyles({
  heading: {
    fontSize: tokens.fontSizeHero800,
    lineHeight: tokens.lineHeightHero800,
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorNeutralForeground1,
  },
});

// Or use Fluent's <Text> with built-in ramp variants:
<Text as="h1" size={800} weight="bold">Page Title</Text>
```

---

## Layout

### Spacing scale

Horizontal and vertical spacing tokens mirror each other exactly.

| Level | Token (H) | Token (V) | Value |
|---|---|---|---|
| None | `spacingHorizontalNone` | `spacingVerticalNone` | `0px` |
| XXS | `spacingHorizontalXXS` | `spacingVerticalXXS` | `2px` |
| XS | `spacingHorizontalXS` | `spacingVerticalXS` | `4px` |
| SNudge | `spacingHorizontalSNudge` | `spacingVerticalSNudge` | `6px` |
| S | `spacingHorizontalS` | `spacingVerticalS` | `8px` |
| MNudge | `spacingHorizontalMNudge` | `spacingVerticalMNudge` | `10px` |
| M | `spacingHorizontalM` | `spacingVerticalM` | `12px` |
| L | `spacingHorizontalL` | `spacingVerticalL` | `16px` |
| XL | `spacingHorizontalXL` | `spacingVerticalXL` | `20px` |
| XXL | `spacingHorizontalXXL` | `spacingVerticalXXL` | `24px` |
| XXXL | `spacingHorizontalXXXL` | `spacingVerticalXXXL` | `32px` |

**Usage rules:**
- Use **S (8px)** for internal component padding (icon gaps, badge padding).
- Use **M (12px)** for compact form field padding.
- Use **L (16px)** for card padding, section gaps.
- Use **XL–XXL (20–24px)** for layout column gaps.
- Use **XXXL (32px)** for major section separators.

### Grid

Fluent 2 does not mandate a fixed grid. Recommended approach:
- Fluid 12-column grid with `spacingHorizontalXXL` (24px) gutters.
- Content max-width: `1280px` (with `spacingHorizontalXXXL` (32px) horizontal padding on mobile).
- Use CSS Grid or Flexbox; Fluent provides no layout primitives.

### Z-index / layer system

Fluent 2 defines a logical stacking order. Use these values for custom overlays:

| Layer | z-index | Surfaces |
|---|---|---|
| Base | `0` | Page content, cards |
| Raised | `1` | Raised cards, sticky headers |
| Overlay | `1000` | Drawers, side panels |
| Dialog | `1000` | Modal dialogs, popups |
| Flyout | `1000` | Dropdowns, menus, tooltips |
| Toast | `1000` | Notification toasts |
| Overlay (critical) | `9999` | Full-screen blocking overlays |

> Fluent components manage their own z-index internally via the `Portal` component.

---

## Elevation & Depth

### Shadow tokens

Shadow tokens communicate elevation. Higher shadow = higher perceived layer.

| Token | Value | Use for |
|---|---|---|
| `shadow2` | `0 0 2px rgba(0,0,0,0.12), 0 1px 2px rgba(0,0,0,0.14)` | Cards resting on page |
| `shadow4` | `0 0 2px rgba(0,0,0,0.12), 0 2px 4px rgba(0,0,0,0.14)` | Cards on cards, dropdowns |
| `shadow8` | `0 0 2px rgba(0,0,0,0.12), 0 4px 8px rgba(0,0,0,0.14)` | Menus, tooltips |
| `shadow16` | `0 0 2px rgba(0,0,0,0.12), 0 8px 16px rgba(0,0,0,0.14)` | Dialogs, panels |
| `shadow28` | `0 0 8px rgba(0,0,0,0.12), 0 14px 28px rgba(0,0,0,0.14)` | High-elevation panels |
| `shadow64` | `0 0 8px rgba(0,0,0,0.12), 0 32px 64px rgba(0,0,0,0.14)` | Full-screen overlays |

### Stroke width tokens

| Token | Value | Usage |
|---|---|---|
| `strokeWidthThin` | `1px` | Default borders |
| `strokeWidthThick` | `2px` | Focus indicators, selected indicators |
| `strokeWidthThicker` | `3px` | Emphasis borders |
| `strokeWidthThickest` | `4px` | Heavy structural dividers |

### Surface layer hierarchy (light theme)

```
colorNeutralBackground2 (#fafafa)  — page canvas / app shell
  └─ colorNeutralBackground1 (#ffffff) — cards, panels
       └─ shadow2 / shadow4            — floating elements (dropdowns, combobox)
            └─ shadow16                — dialogs
                 └─ shadow28           — high-elevation overlays
```

---

## Shapes

### Border radius

| Token | Value | Applied to |
|---|---|---|
| `borderRadiusNone` | `0px` | Flush / square elements (dividers) |
| `borderRadiusSmall` | `2px` | Tags, compact chips |
| `borderRadiusMedium` | `4px` | **Default** — buttons, inputs, cards |
| `borderRadiusLarge` | `6px` | Large cards, dialogs |
| `borderRadiusXLarge` | `8px` | Panels, sheets |
| `borderRadiusCircular` | `9999px` | Badges, avatars, pills, FABs |

**Rule:** Nested elements use the same or smaller radius than their container.

---

## Motion

### Duration tokens

| Token | Value | Use for |
|---|---|---|
| `durationUltraFast` | `50ms` | Micro interactions (checkbox tick) |
| `durationFaster` | `100ms` | Icon swaps, color transitions |
| `durationFast` | `150ms` | Tooltip appear, badge pop |
| `durationNormal` | `200ms` | **Default** — most state changes |
| `durationGentle` | `250ms` | Expand/collapse (accordion) |
| `durationSlow` | `300ms` | Drawer slide-in, page transitions |
| `durationSlower` | `400ms` | Complex choreography |
| `durationUltraSlow` | `500ms` | Full-screen transitions |

### Easing curve tokens

| Token | Value | Use for |
|---|---|---|
| `curveLinear` | `cubic-bezier(0, 0, 1, 1)` | Looping animations (spinners) |
| `curveEasyEase` | `cubic-bezier(0.33, 0, 0.67, 1)` | **Default** — neutral state changes |
| `curveEasyEaseMax` | `cubic-bezier(0.8, 0, 0.2, 1)` | Emphasis interactions |
| `curveDecelerateMax` | `cubic-bezier(0.1, 0.9, 0.2, 1)` | Elements entering the screen |
| `curveDecelerateMid` | `cubic-bezier(0, 0, 0, 1)` | Mid-screen element entrances |
| `curveDecelerateMin` | `cubic-bezier(0.33, 0, 0.1, 1)` | Subtle entrances |
| `curveAccelerateMax` | `cubic-bezier(0.9, 0.1, 1, 0.2)` | Elements leaving the screen |
| `curveAccelerateMid` | `cubic-bezier(1, 0, 1, 1)` | Fast exits |
| `curveAccelerateMin` | `cubic-bezier(0.8, 0, 0.78, 1)` | Subtle exits |

### Motion principles

1. **Direction matters.** Entering elements decelerate (`curveDecelerate*`); exiting elements accelerate (`curveAccelerate*`).
2. **Default to `durationNormal` + `curveEasyEase`** for most hover/focus/active transitions.
3. **Reduce motion.** Honor `prefers-reduced-motion`; Fluent's `useMotion` hooks do this automatically.
4. **No gratuitous animation.** Motion should communicate state, not decorate.

**CSS example:**

```css
.button {
  transition: background-color var(--durationNormal) var(--curveEasyEase);
}
```

**React + Fluent example:**

```tsx
import { tokens } from "@fluentui/react-components";
import { makeStyles } from "@fluentui/react-components";

const useStyles = makeStyles({
  panel: {
    transitionProperty: "transform, opacity",
    transitionDuration: tokens.durationSlow,
    transitionTimingFunction: tokens.curveDecelerateMax,
  },
});
```

---

## Components

All components must be imported from `@fluentui/react-components` and wrapped in `<FluentProvider theme={webLightTheme}>`.

### Button

Fluent 2 Button has five visual appearances. Use the `appearance` prop.

| Appearance | Token mapping | When to use |
|---|---|---|
| `primary` | `colorBrandBackground` fill, white text | Primary CTA — one per view |
| `outline` | transparent fill, `colorNeutralStroke1` border, brand text | Secondary actions |
| `subtle` | transparent fill, neutral text | Tertiary / ghost actions |
| `transparent` | fully transparent, brand text | Link-like inline actions |
| `secondary` (default) | `colorNeutralBackground1` fill, border, neutral text | General-purpose secondary |

**Status-colored buttons** are achieved by overriding tokens via `makeStyles`:

```tsx
const useStyles = makeStyles({
  dangerButton: {
    backgroundColor: tokens.colorStatusDangerBackground1,
    color: tokens.colorStatusDangerForeground1,
    borderColor: tokens.colorStatusDangerBorder1,
    ":hover": {
      backgroundColor: tokens.colorStatusDangerBackground1Hover,
      borderColor: tokens.colorStatusDangerBorderActive,
    },
  },
});
```

**Focus ring:** All buttons render a double-ring focus indicator using `colorStrokeFocus1` (white inner) and `colorStrokeFocus2` (black outer) with `strokeWidthThick` (2px).

### Input / TextField

| State | Background | Border |
|---|---|---|
| Default | `colorNeutralBackground1` (`#ffffff`) | `colorNeutralStroke1` (`#d1d1d1`) |
| Hover | `colorNeutralBackground1` | `colorNeutralStroke1Hover` (`#c7c7c7`) |
| Focus | `colorNeutralBackground1` | `colorBrandStroke1` (`#0078d4`) 2px |
| Disabled | `colorNeutralBackgroundDisabled` (`#f0f0f0`) | `colorNeutralStrokeDisabled` (`#e0e0e0`) |
| Error | `colorNeutralBackground1` | `colorStatusDangerBorderActive` (`#c50f1f`) |

Placeholder text: `colorNeutralForeground4` (`#707070`)  
Input text: `colorNeutralForeground1` (`#242424`)  
Border radius: `borderRadiusMedium` (4px)

### Card

| Variant | Background | Border | Shadow |
|---|---|---|---|
| `filled` | `colorNeutralCardBackground` (`#fafafa`) | `colorNeutralStroke2` (`#e0e0e0`) | none |
| `filled-alternative` | `colorNeutralBackground2` (`#fafafa`) | none | none |
| `outline` | `colorNeutralBackground1` (`#ffffff`) | `colorNeutralStroke1` (`#d1d1d1`) | none |
| `subtle` | `colorSubtleBackground` (transparent) | none | none |
| Default (raised) | `colorNeutralBackground1` (`#ffffff`) | `colorNeutralStroke2` | `shadow4` |

Padding: `spacingHorizontalL` / `spacingVerticalL` (16px)  
Border radius: `borderRadiusMedium` (4px)

### Badge

| Appearance | Background | Foreground | Border |
|---|---|---|---|
| `filled` | color-family based | white | none |
| `ghost` | transparent | color-family based | none |
| `outline` | transparent | color-family based | color-family based |
| `tint` | lightest tint | dark shade | light tint border |

**Status badge tokens:**

```tsx
// Informative tint badge
<Badge appearance="tint" color="informative">Info</Badge>
// Uses colorStatusInformativeBackground1 + colorStatusInformativeForeground1 + colorStatusInformativeBorder1

// Warning
<Badge appearance="tint" color="warning">Warn</Badge>

// Danger
<Badge appearance="tint" color="danger">Error</Badge>

// Success
<Badge appearance="tint" color="success">Done</Badge>
```

Shape: `borderRadiusCircular` (9999px)  
Font: `fontSizeBase200` (12px), `fontWeightSemibold` (600)

### Tooltip

Background: `colorNeutralBackgroundInverted` (`#292929`)  
Text: `colorNeutralForegroundInverted` (`#ffffff`)  
Font: `fontSizeBase200` (12px)  
Border radius: `borderRadiusMedium` (4px)  
Shadow: `shadow8`  
Padding: `spacingVerticalXS` / `spacingHorizontalS` (4px 8px)  
Motion: `durationFast` + `curveDecelerateMax` (in), `durationFaster` + `curveAccelerateMin` (out)

### Spinner / Progress

Active color: `colorBrandBackground` (`#0078d4`)  
Track color: `colorNeutralBackground6` (`#e6e6e6`)  
Animation: linear rotation, `durationUltraSlow` per cycle

### Checkbox & Toggle

| State | Indicator fill | Border |
|---|---|---|
| Unchecked | `colorNeutralBackground1` | `colorNeutralStrokeAccessible` (`#616161`) |
| Checked | `colorBrandBackground` (`#0078d4`) | `colorBrandBackground` |
| Indeterminate | `colorBrandBackground` | `colorBrandBackground` |
| Disabled | `colorNeutralBackgroundDisabled` | `colorNeutralStrokeDisabled` |

Compound border: `colorCompoundBrandStroke` (`#0078d4`)

---

## Do's and Don'ts

### Colors

✅ **Do** use semantic alias tokens (`colorNeutralForeground1`) — they adapt to light/dark/HC automatically.  
✅ **Do** ensure text on `colorBrandBackground` uses `colorNeutralForegroundOnBrand` (white).  
✅ **Do** use `colorStatusDangerForeground1` on `colorStatusDangerBackground1` for status messaging.  
❌ **Don't** hardcode hex values — use `tokens.colorNeutralForeground1` from `@fluentui/react-components`.  
❌ **Don't** use foreground-1 tokens on foreground-1 backgrounds (no contrast).  
❌ **Don't** mix palette global tokens directly into components; use alias tokens.

### Typography

✅ **Do** use the Fluent `<Text>` component with `size` + `weight` props for the type ramp.  
✅ **Do** default to Body 1 (`fontSizeBase300`, 14px) for body copy.  
❌ **Don't** use font sizes outside the 10-level type ramp.  
❌ **Don't** set `font-family` manually — it is inherited from `FluentProvider`.

### Spacing

✅ **Do** use spacing tokens for all padding, margin, and gap values.  
✅ **Do** use `L` (16px) for card/panel internal padding.  
❌ **Don't** use arbitrary pixel values like `13px` or `7px`.

### Motion

✅ **Do** use `durationNormal` (200ms) + `curveEasyEase` as the default transition.  
✅ **Do** test with `prefers-reduced-motion` — Fluent hooks handle this, but custom CSS must too.  
❌ **Don't** animate layout properties (width, height) — prefer `transform` and `opacity`.  
❌ **Don't** use durations above `durationSlow` (300ms) for interactive feedback.

### Components

✅ **Do** use one `primary` button per view.  
✅ **Do** wrap everything in `<FluentProvider theme={webLightTheme}>`.  
✅ **Do** use `makeStyles` + `mergeClasses` for all custom styles (Griffel CSS-in-JS).  
❌ **Don't** apply inline `style` props for token-based values — use `makeStyles`.  
❌ **Don't** create custom components for things Fluent already provides (Button, Badge, Card, Input, Tooltip, Dialog, Menu, etc.).

---

## Dark Mode & High Contrast

`FluentProvider` handles theming transparently. To support dark mode:

```tsx
import {
  FluentProvider,
  webLightTheme,
  webDarkTheme,
  webHighContrastTheme,
} from "@fluentui/react-components";

function App({ prefersDark }: { prefersDark: boolean }) {
  return (
    <FluentProvider theme={prefersDark ? webDarkTheme : webLightTheme}>
      {/* All components automatically use correct tokens */}
    </FluentProvider>
  );
}
```

**Dark theme token shifts (informative — do not hardcode):**
- `colorNeutralBackground1` shifts from `#ffffff` → `#292929`
- `colorNeutralForeground1` shifts from `#242424` → `#ffffff`
- Brand tokens shift to lighter shades for contrast on dark backgrounds

**High contrast:** `webHighContrastTheme` maps tokens to system colors (`ButtonText`, `Highlight`, etc.) per Windows HC mode. Never override these.

---

## Appendix: Token Quick Reference

### All foreground tokens
`colorNeutralForeground1` · `colorNeutralForeground2` · `colorNeutralForeground3` · `colorNeutralForeground4` · `colorNeutralForegroundDisabled` · `colorNeutralForegroundOnBrand` · `colorNeutralForegroundInverted` · `colorBrandForeground1` · `colorBrandForeground2` · `colorBrandForegroundLink` · `colorNeutralStrokeAccessible`

### All background tokens
`colorNeutralBackground1` · `colorNeutralBackground1Hover` · `colorNeutralBackground1Pressed` · `colorNeutralBackground1Selected` · `colorNeutralBackground2` · `colorNeutralBackground2Hover` · `colorNeutralBackground3` · `colorNeutralBackground4` · `colorNeutralBackground5` · `colorNeutralBackground6` · `colorNeutralBackgroundInverted` · `colorNeutralBackgroundDisabled` · `colorNeutralCardBackground` · `colorSubtleBackground` · `colorSubtleBackgroundHover` · `colorSubtleBackgroundPressed` · `colorSubtleBackgroundSelected` · `colorTransparentBackground` · `colorBrandBackground` · `colorBrandBackgroundHover` · `colorBrandBackgroundPressed` · `colorBrandBackgroundSelected` · `colorBrandBackground2`

### All stroke tokens
`colorNeutralStroke1` · `colorNeutralStroke1Hover` · `colorNeutralStroke1Pressed` · `colorNeutralStroke2` · `colorNeutralStroke3` · `colorNeutralStrokeDisabled` · `colorNeutralStrokeOnBrand` · `colorNeutralStrokeAccessible` · `colorBrandStroke1` · `colorBrandStroke2` · `colorCompoundBrandStroke` · `colorStrokeFocus1` · `colorStrokeFocus2`

### All motion tokens
**Duration:** `durationUltraFast` · `durationFaster` · `durationFast` · `durationNormal` · `durationGentle` · `durationSlow` · `durationSlower` · `durationUltraSlow`  
**Curves:** `curveLinear` · `curveEasyEase` · `curveEasyEaseMax` · `curveDecelerateMax` · `curveDecelerateMid` · `curveDecelerateMin` · `curveAccelerateMax` · `curveAccelerateMid` · `curveAccelerateMin`
