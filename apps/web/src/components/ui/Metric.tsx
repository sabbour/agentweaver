/**
 * MetricRow + StatTile — restrained metric displays.
 *
 * These deliberately are NOT the banned "hero-metric" template (giant number
 * grids) and NOT uppercase eyebrows. Label sits in muted sentence case; value
 * sits in ink. MetricRow (a few inline stats, faint separators) is the default;
 * StatTile is a minimal single card for the rare standalone metric.
 *
 * Native @fluentui/react-components only; theme tokens only.
 */

import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { Label, TitleText } from './typography';

export interface MetricItem {
  /** Muted, sentence-case label (e.g. "In flight"). */
  label: ReactNode;
  /** The value, shown in ink. */
  value: ReactNode;
  /** Optional leading icon. */
  icon?: ReactNode;
  /** Optional quiet hint after the value. */
  hint?: ReactNode;
}

const useRowStyles = makeStyles({
  root: {
    display: 'flex',
    alignItems: 'stretch',
    gap: tokens.spacingHorizontalXL,
    flexWrap: 'wrap',
    minWidth: 0,
  },
  item: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  // Faint vertical separators between inline stats.
  divided: {
    paddingLeft: tokens.spacingHorizontalXL,
    borderLeft: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  labelRow: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorNeutralForeground3,
  },
  valueRow: {
    display: 'inline-flex',
    alignItems: 'baseline',
    gap: tokens.spacingHorizontalXS,
  },
  value: {
    fontVariantNumeric: 'tabular-nums',
  },
  hint: {
    color: tokens.colorNeutralForeground4,
  },
});

export interface MetricRowProps {
  items: MetricItem[];
  /** Draw faint separators between items (default: true). */
  separators?: boolean;
  className?: string;
}

export function MetricRow({ items, separators = true, className }: MetricRowProps) {
  const styles = useRowStyles();
  return (
    <div className={mergeClasses(styles.root, className)} role="group">
      {items.map((item, index) => (
        <div
          key={index}
          className={mergeClasses(styles.item, separators && index > 0 && styles.divided)}
        >
          <span className={styles.labelRow}>
            {item.icon}
            <Label as="span" tone="muted">
              {item.label}
            </Label>
          </span>
          <span className={styles.valueRow}>
            <TitleText as="span" className={styles.value}>
              {item.value}
            </TitleText>
            {item.hint && (
              <Label as="span" className={styles.hint}>
                {item.hint}
              </Label>
            )}
          </span>
        </div>
      ))}
    </div>
  );
}

const useTileStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    minWidth: 0,
  },
  labelRow: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorNeutralForeground3,
  },
  value: {
    fontVariantNumeric: 'tabular-nums',
  },
  hint: {
    color: tokens.colorNeutralForeground4,
  },
});

export interface StatTileProps {
  label: ReactNode;
  value: ReactNode;
  icon?: ReactNode;
  hint?: ReactNode;
  className?: string;
}

export function StatTile({ label, value, icon, hint, className }: StatTileProps) {
  const styles = useTileStyles();
  return (
    <div className={mergeClasses(styles.root, className)}>
      <span className={styles.labelRow}>
        {icon}
        <Label as="span" tone="muted">
          {label}
        </Label>
      </span>
      <TitleText as="span" className={styles.value}>
        {value}
      </TitleText>
      {hint && (
        <Label as="span" className={styles.hint}>
          {hint}
        </Label>
      )}
    </div>
  );
}
