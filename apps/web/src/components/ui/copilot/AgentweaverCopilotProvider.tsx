/**
 * AgentweaverCopilotProvider
 *
 * Wraps @1js/fluentai CopilotProvider with the warm-monochrome brand flair so
 * Copilot components inherit our theme. Must be rendered INSIDE the app's
 * existing <FluentProvider theme={agentweaverLightTheme}>; do NOT add another
 * FluentProvider.
 *
 * Usage:
 *   <AgentweaverCopilotProvider mode="sidecar">   // docked Console
 *   <AgentweaverCopilotProvider mode="canvas">    // full-page run/chat
 */

import type { ReactNode } from 'react';
import { CopilotProvider } from '@1js/fluentai';
import type { CopilotThemeExtension } from '@1js/fai-tokens';
import type { PartialTheme } from '@fluentui/react-components';

/**
 * Warm-monochrome flair extension — no blue. CopilotThemeExtension requires
 * colorBrandFlair1/2/3 (the gradient/flair used by OutputCard and the send
 * button animation). We map these to our warm near-black ramp so any Copilot
 * animation reads as a warm sweep rather than the default blue-purple gradient.
 */
const warmThemeExtension: PartialTheme & Partial<CopilotThemeExtension> = {
  // Flair colors — used for the OutputCard loading animation and send button glow.
  // Warm near-black → warm dark-brown → warm mid-brown (no blue/purple).
  colorBrandFlair1: '#272320',
  colorBrandFlair2: '#3f3935',
  colorBrandFlair3: '#635c57',
  colorBrandFlair1Transparent: 'rgba(39,35,32,0.12)',
  colorBrandFlair2Transparent: 'rgba(63,57,53,0.10)',
  colorBrandFlair3Transparent: 'rgba(99,92,87,0.08)',
  // MorseCode tokens (used by some Copilot shimmer effects) — warm neutrals
  colorBrandMorseCode1: '#272320',
  colorBrandMorseCode2: '#3f3935',
  colorBrandMorseCode3: '#635c57',
  colorBrandMorseCode4: '#746d68',
  colorBrandMorseCode5: '#8c837c',
  colorBrandMorseCode6: '#a39790',
  colorBrandMorseCode7: '#bdb7b2',
  colorBrandMorseCode8: '#d9d2cb',
  colorBrandMorseCode9: '#ece7e3',
};

export interface AgentweaverCopilotProviderProps {
  /**
   * "sidecar" — docked Console panel (compact layout).
   * "canvas" — full-page run/chat surface (generous layout).
   */
  mode?: 'sidecar' | 'canvas';
  children: ReactNode;
}

export function AgentweaverCopilotProvider({
  mode = 'canvas',
  children,
}: AgentweaverCopilotProviderProps) {
  return (
    <CopilotProvider mode={mode} themeExtension={warmThemeExtension}>
      {children}
    </CopilotProvider>
  );
}
