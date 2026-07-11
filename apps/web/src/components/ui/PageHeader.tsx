/**
 * PageHeader — the single header pattern for every page.
 *
 * Replaces every per-page BladeHeader / CommandBar / "Azure … blade" header.
 * Structure: optional breadcrumbs → [ title (+ optional description)  |  actions ].
 *
 * Rules baked in (so they can't be reintroduced):
 *   - Title is the Display role (28/600). NO uppercase eyebrow above it.
 *   - No Azure/blade/resource/operator vocabulary — it's just a title.
 *   - The actions area is a transparent toolbar (no filled command bar).
 *
 * Native @fluentui/react-components only; theme tokens only.
 */

import { makeStyles, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { Body, Display } from './typography';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
  breadcrumbs: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    fontSize: '13px',
    lineHeight: '18px',
    color: tokens.colorNeutralForeground3,
    minWidth: 0,
  },
  bar: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
    flexWrap: 'wrap',
    minWidth: 0,
  },
  titleBlock: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    minWidth: 0,
  },
  description: {
    maxWidth: '75ch',
  },
  // Transparent toolbar — never a filled command bar.
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    justifyContent: 'flex-end',
    backgroundColor: 'transparent',
  },
});

export interface PageHeaderProps {
  title: string;
  /** Optional supporting sentence beneath the title (muted). */
  description?: ReactNode;
  /** Optional breadcrumb trail rendered above the title. */
  breadcrumbs?: ReactNode;
  /** Optional right-aligned actions (buttons, menus). Rendered transparently. */
  actions?: ReactNode;
}

export function PageHeader({ title, description, breadcrumbs, actions }: PageHeaderProps) {
  const styles = useStyles();
  return (
    <header className={styles.root} aria-label={`${title} header`}>
      {breadcrumbs && (
        <nav className={styles.breadcrumbs} aria-label="Breadcrumb">
          {breadcrumbs}
        </nav>
      )}
      <div className={styles.bar}>
        <div className={styles.titleBlock}>
          <Display>{title}</Display>
          {description && (
            <Body tone="muted" className={styles.description}>
              {description}
            </Body>
          )}
        </div>
        {actions && (
          <div className={styles.actions} role="toolbar" aria-label={`${title} actions`}>
            {actions}
          </div>
        )}
      </div>
    </header>
  );
}
