/**
 * PageSection — the structural replacement for the banned uppercase eyebrow.
 *
 * A section is delimited by a sentence-case Title (16/600) and a faint 1px
 * divider (colorNeutralStroke2), NEVER an all-caps tracked eyebrow. Optional
 * description and right-aligned actions sit on the header row.
 *
 * Native @fluentui/react-components only; theme tokens only.
 */

import { makeStyles, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { Body, TitleText } from './typography';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    minWidth: 0,
  },
  header: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  headerRow: {
    display: 'flex',
    alignItems: 'flex-end',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
    flexWrap: 'wrap',
  },
  titleWrap: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  description: {
    maxWidth: '75ch',
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    backgroundColor: 'transparent',
  },
  // The faint divider — the only section scaffolding allowed.
  divider: {
    height: '1px',
    backgroundColor: tokens.colorNeutralStroke2,
    border: 'none',
    margin: 0,
  },
  body: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
});

export interface PageSectionProps {
  /** Sentence-case heading. Rendered at Title role (16/600). */
  title: ReactNode;
  /** Optional supporting sentence (muted). */
  description?: ReactNode;
  /** Optional right-aligned actions on the header row. */
  actions?: ReactNode;
  /** Hide the faint divider under the header (default: shown). */
  hideDivider?: boolean;
  children?: ReactNode;
  className?: string;
}

export function PageSection({
  title,
  description,
  actions,
  hideDivider = false,
  children,
  className,
}: PageSectionProps) {
  const styles = useStyles();
  return (
    <section className={className ? `${styles.root} ${className}` : styles.root}>
      <div className={styles.header}>
        <div className={styles.headerRow}>
          <div className={styles.titleWrap}>
            <TitleText>{title}</TitleText>
            {description && (
              <Body tone="muted" className={styles.description}>
                {description}
              </Body>
            )}
          </div>
          {actions && <div className={styles.actions}>{actions}</div>}
        </div>
        {!hideDivider && <hr className={styles.divider} aria-hidden="true" />}
      </div>
      {children && <div className={styles.body}>{children}</div>}
    </section>
  );
}
