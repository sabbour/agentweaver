/**
 * TileGrid + Tile — a card-grid alternative to RichList for collections where a
 * visual/scannable layout reads better than a dense list (project gallery, agent
 * roster). Built on AppCard (soft-ring, native Fluent tokens) so it shares the
 * app's one card language rather than introducing a second card style.
 *
 * Layout: `repeat(auto-fill, minmax(220px, 1fr))` — no hard breakpoints, tiles
 * reflow naturally from phone to ultrawide.
 */

import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import type { ElementType, ReactNode } from 'react';
import { AppCard } from './AppCard';
import { Body, Label } from './typography';

const useGridStyles = makeStyles({
  root: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))',
    gap: tokens.spacingHorizontalM,
    alignItems: 'stretch',
  },
});

export interface TileGridProps {
  children: ReactNode;
  className?: string;
  'aria-label'?: string;
}

export function TileGrid({ children, className, ...rest }: TileGridProps) {
  const styles = useGridStyles();
  return (
    <div role="list" className={mergeClasses(styles.root, className)} {...rest}>
      {children}
    </div>
  );
}

const useTileStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    height: '100%',
    minWidth: 0,
    textAlign: 'left',
    cursor: 'pointer',
    transitionProperty: 'transform, box-shadow, border-color, background-color',
    transitionDuration: '150ms',
    transitionTimingFunction: 'ease-out',
    ':hover': {
      transform: 'translateY(-2px)',
      boxShadow: tokens.shadow8,
      border: `1px solid ${tokens.colorNeutralStroke1}`,
    },
    ':active': {
      transform: 'translateY(0)',
      boxShadow: 'none',
    },
    ':focus-visible': {
      outline: `2px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: '2px',
    },
    '@media (prefers-reduced-motion: reduce)': {
      transitionDuration: '0.01ms',
      ':hover': { transform: 'none' },
    },
  },
  top: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
  },
  media: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: tokens.colorNeutralForeground2,
    flexShrink: 0,
  },
  mediaBubble: {
    width: '40px',
    height: '40px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  badgeRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    flexWrap: 'wrap',
    justifyContent: 'flex-end',
  },
  text: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
    flexGrow: 1,
  },
  primary: {
    fontWeight: tokens.fontWeightSemibold,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    display: '-webkit-box',
    WebkitLineClamp: 2,
    WebkitBoxOrient: 'vertical',
  },
  secondary: {
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  footer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
    marginTop: 'auto',
  },
  meta: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorNeutralForeground3,
    minWidth: 0,
    overflow: 'hidden',
  },
  // Actions are quiet at rest and revealed on hover/focus, mirroring ListRow.
  actions: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexShrink: 0,
    opacity: 0,
    transitionProperty: 'opacity',
    transitionDuration: '150ms',
    transitionTimingFunction: 'ease-out',
    '@media (prefers-reduced-motion: reduce)': { transitionDuration: '0.01ms' },
  },
  actionsVisible: {
    opacity: 1,
  },
});

export interface TileProps {
  /** Leading icon / avatar node, shown top-left in a soft tile unless `bubble` is false. */
  media?: ReactNode;
  /** Wrap `media` in a soft neutral bubble (default: true). */
  bubble?: boolean;
  /** Inline nodes in the top-right corner (e.g. status badges). */
  badges?: ReactNode;
  /** Title — semibold, clamped to two lines. */
  primary: ReactNode;
  /** Supporting line (muted, single line, ellipsized). */
  secondary?: ReactNode;
  /** Trailing metadata rendered bottom-left (e.g. a repo path, a role). */
  meta?: ReactNode;
  /** Hover/focus-revealed actions rendered bottom-right. */
  actions?: ReactNode;
  /** Keep actions always visible instead of hover-revealing them. */
  actionsAlwaysVisible?: boolean;
  onClick?: () => void;
  as?: ElementType;
  className?: string;
  [key: string]: unknown;
}

export function Tile({
  media,
  bubble = true,
  badges,
  primary,
  secondary,
  meta,
  actions,
  actionsAlwaysVisible = false,
  onClick,
  as,
  className,
  ...rest
}: TileProps) {
  const styles = useTileStyles();
  const Root: ElementType = as ?? (onClick ? 'button' : 'div');

  const rootProps: Record<string, unknown> = {
    role: 'listitem',
    className: undefined,
    ...rest,
  };
  if (onClick) rootProps.onClick = onClick;
  if (Root === 'button') rootProps.type = 'button';

  return (
    <AppCard
      as={Root}
      interactive
      className={mergeClasses(styles.root, className)}
      {...rootProps}
    >
      <div className={styles.top}>
        {media && (
          <span className={mergeClasses(styles.media, bubble && styles.mediaBubble)}>{media}</span>
        )}
        {badges && <span className={styles.badgeRow}>{badges}</span>}
      </div>
      <div className={styles.text}>
        <Body as="span" className={styles.primary}>{primary}</Body>
        {secondary && (
          <Label as="span" tone="muted" className={styles.secondary}>{secondary}</Label>
        )}
      </div>
      {(meta || actions) && (
        <div className={styles.footer}>
          {meta && <span className={styles.meta}>{meta}</span>}
          {actions && (
            <span className={mergeClasses(styles.actions, actionsAlwaysVisible && styles.actionsVisible)}>
              {actions}
            </span>
          )}
        </div>
      )}
    </AppCard>
  );
}
