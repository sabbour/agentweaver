import { CopilotProvider } from '@1js/fluentai';
import type { CopilotThemeExtension } from '@1js/fluentai';
import type { ReactNode } from 'react';
import { agentweaverLightTheme } from '../../../theme';

/**
 * Warm-monochrome CopilotProvider for the Agentweaver run surfaces.
 *
 * @1js/fluentai's Copilot components (ChainOfThought, etc.) require a
 * CopilotProvider in the tree for their tokens + accessibility announcer.
 *
 * IMPORTANT: CopilotProvider renders its OWN FluentProvider and injects the
 * default (blue/purple) `copilotLightThemeExtension` — inheriting the surrounding
 * Fluent context is NOT enough, so we pass `theme={agentweaverLightTheme}` EXPLICITLY.
 * That makes the provider's inner FluentProvider the warm-monochrome one for this
 * surface, and we layer `themeExtension` on top to neutralize every Copilot flair /
 * morse-code / shadow accent slot to warm tones (no blue anywhere). @1js imports are
 * isolated to this file + RunTimeline so the rest of the app never pulls the feed.
 */

// Warm neutral overrides for the Copilot flair/gradient token slots. These are the
// only Copilot-specific colors that would otherwise render as blue/purple accents.
const warmMonochromeCopilotExtension = {
  colorBrandFlair1: '#3b352f',
  colorBrandFlair2: '#6f665d',
  colorBrandFlair3: '#a89f95',
  colorBrandFlair1Transparent: 'rgba(59, 53, 47, 0)',
  colorBrandFlair2Transparent: 'rgba(111, 102, 93, 0)',
  colorBrandFlair3Transparent: 'rgba(168, 159, 149, 0)',
  colorBrandMorseCode1: '#2a2622',
  colorBrandMorseCode2: '#3b352f',
  colorBrandMorseCode3: '#4c453d',
  colorBrandMorseCode4: '#5d554b',
  colorBrandMorseCode5: '#6f665d',
  colorBrandMorseCode6: '#877d72',
  colorBrandMorseCode7: '#9f9488',
  colorBrandMorseCode8: '#b8afa5',
  colorBrandMorseCode9: '#d0c8bf',
  colorFlairShadow1: 'rgba(59, 53, 47, 0.12)',
  colorFlairShadow2: 'rgba(59, 53, 47, 0.08)',
} satisfies Partial<CopilotThemeExtension>;

export interface AgentweaverCopilotProviderProps {
  children: ReactNode;
  className?: string;
}

export function AgentweaverCopilotProvider({ children, className }: AgentweaverCopilotProviderProps) {
  return (
    <CopilotProvider
      mode="canvas"
      className={className}
      theme={agentweaverLightTheme}
      themeExtension={warmMonochromeCopilotExtension}
    >
      {children}
    </CopilotProvider>
  );
}
