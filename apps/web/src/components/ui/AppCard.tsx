/**
 * AppCard — a thin wrapper over Fluent Card using the soft-ring card style.
 *
 * DESIGN.md: cards prefer a soft ring (low-opacity border) over a hard border
 * or shadow, 12px radius, surface fill, 16px padding. Never nest cards.
 *
 * Native @fluentui/react-components only; theme tokens only.
 */

import { Card, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import type { CardProps } from '@fluentui/react-components';

const useStyles = makeStyles({
  root: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    // Soft ring, not a hard border or shadow.
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: 'none',
    padding: tokens.spacingVerticalL,
    minWidth: 0,
  },
  interactive: {
    transitionProperty: 'background-color, border-color',
    transitionDuration: '150ms',
    transitionTimingFunction: 'ease-out',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
      border: `1px solid ${tokens.colorNeutralStroke1}`,
    },
    '@media (prefers-reduced-motion: reduce)': { transitionDuration: '0.01ms' },
  },
});

export interface AppCardProps extends Omit<CardProps, 'appearance'> {
  /** Add a subtle hover affordance (for clickable cards). */
  interactive?: boolean;
}

export function AppCard({ interactive = false, className, ...rest }: AppCardProps) {
  const styles = useStyles();
  return (
    <Card
      appearance="subtle"
      className={mergeClasses(styles.root, interactive && styles.interactive, className)}
      {...rest}
    />
  );
}
