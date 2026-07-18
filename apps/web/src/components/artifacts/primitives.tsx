import type { CSSProperties, ReactNode } from 'react';

/**
 * Inert, non-focusable "controls" for artifact previews.
 *
 * Every button/link inside a generated artifact is decorative: the artifact is a
 * still preview of what an agent produced, not a live app. These primitives render
 * plain <span> elements — no href, no tabindex, no role, no handlers — so they can
 * never receive focus or be mistaken for a working control (no dead controls).
 */

export interface FauxProps {
  children: ReactNode;
  className?: string;
  style?: CSSProperties;
}

/** A button-shaped decorative span. */
export function FauxButton({ children, className, style }: FauxProps) {
  return (
    <span className={className} style={style} data-inert-preview="button" aria-hidden="true">
      {children}
    </span>
  );
}

/** A link-shaped decorative span. */
export function FauxLink({ children, className, style }: FauxProps) {
  return (
    <span className={className} style={style} data-inert-preview="link" aria-hidden="true">
      {children}
    </span>
  );
}

/** A generic decorative control (chip, tab, toggle) with no interactive semantics. */
export function FauxControl({ children, className, style }: FauxProps) {
  return (
    <span className={className} style={style} data-inert-preview="control" aria-hidden="true">
      {children}
    </span>
  );
}
