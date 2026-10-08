/**
 * PageContainer — standard in-page vertical rhythm + optional readable max width.
 *
 * The shell (.aw-shell-scroll) already provides the floating panel and its
 * 24×32 gutter, so this does NOT add outer padding. It only:
 *   - stacks page blocks with one consistent vertical rhythm, and
 *   - optionally caps content at a readable measure and centers it.
 *
 * Native @fluentui/react-components only; theme tokens only.
 */

import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';

export type PageWidth = 'full' | 'readable' | 'narrow';

const WIDTHS: Record<PageWidth, string | undefined> = {
  full: undefined,
  readable: '1120px',
  narrow: '720px',
};

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXL,
    minWidth: 0,
  },
  constrained: {
    width: '100%',
    marginLeft: 'auto',
    marginRight: 'auto',
  },
});

export interface PageContainerProps {
  children: ReactNode;
  /** Cap the readable measure. Defaults to 'full' (fills the panel). */
  width?: PageWidth;
  className?: string;
}

export function PageContainer({ children, width = 'full', className }: PageContainerProps) {
  const styles = useStyles();
  const maxWidth = WIDTHS[width];
  return (
    <div
      className={mergeClasses(styles.root, maxWidth && styles.constrained, className)}
      style={maxWidth ? { maxWidth } : undefined}
    >
      {children}
    </div>
  );
}
