/**
 * Typography — the ONE type convention for Agentweaver, aligned to DESIGN.md.
 *
 * Hierarchy is carried by size + weight, never by color or uppercase tracking.
 * There are NO all-caps "eyebrow" roles here on purpose — section scaffolding
 * uses <PageSection> (a sentence-case title + faint divider) instead.
 *
 * Scale (DESIGN.md · Segoe UI + system fallback via theme.fontFamilyBase):
 *   display  28 / 600 / 32   (-0.01em)  page titles
 *   headline 20 / 600 / 26              dialog + major section headers
 *   title    16 / 600 / 22              card / sub-section titles
 *   body     15 / 400 / 1.5             default prose
 *   nav      16 / 500 / 24              left-rail items
 *   label    13 / 500 / 18              field labels, quiet metadata
 *
 * Native @fluentui/react-components only; theme CSS vars only. No hard-coded
 * color. Use the role components (<Display>, <Body>, …) or the class hook
 * (useTypographyStyles) when you need to compose a role onto another element.
 */

import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import type { CSSProperties, ElementType, ReactNode } from 'react';

export type TypographyRole = 'display' | 'headline' | 'title' | 'body' | 'nav' | 'label';

export const useTypographyStyles = makeStyles({
  display: {
    fontSize: '28px',
    lineHeight: '32px',
    fontWeight: tokens.fontWeightSemibold,
    letterSpacing: '-0.01em',
    color: tokens.colorNeutralForeground1,
  },
  headline: {
    fontSize: '20px',
    lineHeight: '26px',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  title: {
    fontSize: '16px',
    lineHeight: '22px',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  body: {
    fontSize: '15px',
    lineHeight: '1.5',
    fontWeight: tokens.fontWeightRegular,
    color: tokens.colorNeutralForeground1,
  },
  nav: {
    fontSize: '16px',
    lineHeight: '24px',
    fontWeight: tokens.fontWeightMedium,
    color: tokens.colorNeutralForeground1,
  },
  label: {
    fontSize: '13px',
    lineHeight: '18px',
    fontWeight: tokens.fontWeightMedium,
    color: tokens.colorNeutralForeground1,
  },
  // Tone modifiers — pair with any role to soften supporting copy.
  muted: { color: tokens.colorNeutralForeground3 },
  quiet: { color: tokens.colorNeutralForeground4 },
});

export type TypeTone = 'default' | 'muted' | 'quiet';

export interface TypeTextProps {
  role: TypographyRole;
  tone?: TypeTone;
  /** Element to render (default: 'span'). */
  as?: ElementType;
  className?: string;
  children?: ReactNode;
  id?: string;
  style?: CSSProperties;
  title?: string;
  'aria-hidden'?: boolean;
  'aria-label'?: string;
}

/**
 * TypeText — text locked to one of the DESIGN.md roles, with an optional tone.
 * Renders a plain element (styling is pure CSS), so it composes onto any tag.
 * Prefer the named role components below for readability.
 */
export function TypeText({ role, tone = 'default', as, className, children, ...rest }: TypeTextProps) {
  const styles = useTypographyStyles();
  const toneClass = tone === 'muted' ? styles.muted : tone === 'quiet' ? styles.quiet : undefined;
  const Tag: ElementType = as ?? 'span';
  return (
    <Tag {...rest} className={mergeClasses(styles[role], toneClass, className)}>
      {children}
    </Tag>
  );
}

type RoleComponentProps = Omit<TypeTextProps, 'role'>;

/** Page title — 28/600. Renders as an <h1> by default. */
export function Display({ as = 'h1', ...rest }: RoleComponentProps) {
  return <TypeText role="display" as={as} {...rest} />;
}

/** Major section / dialog header — 20/600. */
export function Headline({ as = 'h2', ...rest }: RoleComponentProps) {
  return <TypeText role="headline" as={as} {...rest} />;
}

/** Card / sub-section title — 16/600. */
export function TitleText({ as = 'h3', ...rest }: RoleComponentProps) {
  return <TypeText role="title" as={as} {...rest} />;
}

/** Default prose — 15/1.5. */
export function Body({ as = 'span', ...rest }: RoleComponentProps) {
  return <TypeText role="body" as={as} {...rest} />;
}

/** Field labels + quiet metadata — 13/500. */
export function Label({ as = 'span', ...rest }: RoleComponentProps) {
  return <TypeText role="label" as={as} {...rest} />;
}
