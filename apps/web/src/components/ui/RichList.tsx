/**
 * RichList + ListRow — the M-style rich list, the app's default collection
 * affordance. Replaces hero-metric grids and identical repeating card grids.
 *
 * A ListRow carries: a leading icon/avatar (media), primary + secondary text,
 * trailing metadata, and hover/focus-revealed actions. Rows get a rounded
 * hover fill; RichList draws faint 1px dividers between rows and can wrap the
 * whole set in a soft-ring card.
 *
 * Native @fluentui/react-components only; theme tokens only. No blue; hover and
 * focus use warm neutral fills from the theme.
 */

import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { Children, Fragment, isValidElement } from 'react';
import type { ElementType, ReactNode } from 'react';
import { Body, Label } from './typography';

const useListStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  bordered: {
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    padding: tokens.spacingVerticalXXS,
  },
  divider: {
    height: '1px',
    backgroundColor: tokens.colorNeutralStroke2,
    marginLeft: tokens.spacingHorizontalM,
    marginRight: tokens.spacingHorizontalM,
  },
});

export interface RichListProps {
  children: ReactNode;
  /** Wrap rows in a soft-ring card (default: true). */
  bordered?: boolean;
  /** Draw faint 1px dividers between rows (default: true). */
  dividers?: boolean;
  className?: string;
  'aria-label'?: string;
}

export function RichList({
  children,
  bordered = true,
  dividers = true,
  className,
  ...rest
}: RichListProps) {
  const styles = useListStyles();
  const items = Children.toArray(children).filter((child) => isValidElement(child) || child);
  return (
    <div
      role="list"
      className={mergeClasses(styles.root, bordered && styles.bordered, className)}
      {...rest}
    >
      {items.map((child, index) => (
        <Fragment key={index}>
          {index > 0 && dividers && <div className={styles.divider} aria-hidden="true" />}
          {child}
        </Fragment>
      ))}
    </div>
  );
}

const useRowStyles = makeStyles({
  root: {
    display: 'grid',
    gridTemplateColumns: 'auto minmax(0, 1fr) auto',
    alignItems: 'center',
    gap: tokens.spacingHorizontalL,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusLarge,
    minWidth: 0,
    width: '100%',
    textAlign: 'left',
    font: 'inherit',
    color: tokens.colorNeutralForeground1,
    textDecorationLine: 'none',
    backgroundColor: 'transparent',
    // Drives the hover/focus reveal of the actions slot (see `actions`).
    '--aw-row-actions-opacity': '0',
    transitionProperty: 'background-color',
    transitionDuration: '150ms',
    transitionTimingFunction: 'ease-out',
    ':hover': { '--aw-row-actions-opacity': '1' },
    ':focus-within': { '--aw-row-actions-opacity': '1' },
    '@media (prefers-reduced-motion: reduce)': { transitionDuration: '0.01ms' },
  },
  interactive: {
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover, '--aw-row-actions-opacity': '1' },
    ':active': { backgroundColor: tokens.colorSubtleBackgroundPressed },
    ':focus-visible': {
      outline: `2px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: '-2px',
    },
  },
  media: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: tokens.colorNeutralForeground2,
    flexShrink: 0,
  },
  mediaBubble: {
    width: '32px',
    height: '32px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  text: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  primaryRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    minWidth: 0,
  },
  primary: {
    fontWeight: tokens.fontWeightSemibold,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  secondary: {
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  trailing: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    flexShrink: 0,
  },
  meta: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
  },
  // Actions are quiet at rest and revealed on hover/focus so rows stay calm.
  actions: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    opacity: 'var(--aw-row-actions-opacity, 1)',
    transitionProperty: 'opacity',
    transitionDuration: '150ms',
    transitionTimingFunction: 'ease-out',
    '@media (prefers-reduced-motion: reduce)': { transitionDuration: '0.01ms' },
  },
  actionsVisible: {
    opacity: 1,
  },
});

export interface ListRowProps {
  /** Leading icon / avatar node. When `bubble` is set it sits in a soft tile. */
  media?: ReactNode;
  /** Wrap `media` in a soft neutral bubble (default: false). */
  bubble?: boolean;
  /** Primary line (semibold). */
  primary: ReactNode;
  /** Inline nodes rendered after the primary text (e.g. status badges). */
  primaryAside?: ReactNode;
  /** Secondary supporting line (muted). */
  secondary?: ReactNode;
  /** Trailing metadata (timestamps, counts) rendered before actions. */
  meta?: ReactNode;
  /** Hover/focus-revealed actions. */
  actions?: ReactNode;
  /** Keep actions always visible instead of hover-revealing them. */
  actionsAlwaysVisible?: boolean;
  /** Click handler — makes the whole row interactive. */
  onClick?: () => void;
  /** Render the row as another element (e.g. Link) for navigation. */
  as?: ElementType;
  /** Extra props forwarded to the root element (e.g. `to`, `href`). */
  [key: string]: unknown;
}

export function ListRow({
  media,
  bubble = false,
  primary,
  primaryAside,
  secondary,
  meta,
  actions,
  actionsAlwaysVisible = false,
  onClick,
  as,
  className,
  ...rest
}: ListRowProps & { className?: string }) {
  const styles = useRowStyles();
  const Root: ElementType = as ?? (onClick ? 'button' : 'div');
  const interactive = Boolean(onClick || as);

  const rootProps: Record<string, unknown> = {
    role: 'listitem',
    className: mergeClasses(styles.root, interactive && styles.interactive, className),
    ...rest,
  };
  if (onClick) rootProps.onClick = onClick;
  if (Root === 'button') rootProps.type = 'button';

  return (
    <Root {...rootProps}>
      {media && (
        <span className={mergeClasses(styles.media, bubble && styles.mediaBubble)}>{media}</span>
      )}
      <div className={styles.text} style={media ? undefined : { gridColumn: '1 / 2' }}>
        <div className={styles.primaryRow}>
          <Body as="span" className={styles.primary}>
            {primary}
          </Body>
          {primaryAside}
        </div>
        {secondary && (
          <Label as="span" tone="muted" className={styles.secondary}>
            {secondary}
          </Label>
        )}
      </div>
      {(meta || actions) && (
        <div className={styles.trailing}>
          {meta && <span className={styles.meta}>{meta}</span>}
          {actions && (
            <span
              className={mergeClasses(
                styles.actions,
                actionsAlwaysVisible && styles.actionsVisible,
              )}
            >
              {actions}
            </span>
          )}
        </div>
      )}
    </Root>
  );
}
