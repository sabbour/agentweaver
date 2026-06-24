import type { ReactNode } from 'react';
import { Text, Title2, makeStyles, tokens } from '@fluentui/react-components';

// Shared header for every main page: a Title2 with an optional subtitle block beneath it
// (consistent vertical rhythm via tokens) and an optional right-aligned actions slot. An
// optional breadcrumb renders above the title. Centralizing this keeps page headers visually
// consistent and gives every page a subtitle without per-page spacing drift.

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  row: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
  },
  titleBlock: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  subtitle: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
    flexShrink: 0,
  },
});

export interface PageHeaderProps {
  title: string;
  subtitle?: string;
  actions?: ReactNode;
  breadcrumb?: ReactNode;
}

export function PageHeader({ title, subtitle, actions, breadcrumb }: PageHeaderProps) {
  const styles = useStyles();
  return (
    <div className={styles.root}>
      {breadcrumb}
      <div className={styles.row}>
        <div className={styles.titleBlock}>
          <Title2>{title}</Title2>
          {subtitle && <Text className={styles.subtitle}>{subtitle}</Text>}
        </div>
        {actions && <div className={styles.actions}>{actions}</div>}
      </div>
    </div>
  );
}
