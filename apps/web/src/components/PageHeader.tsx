import type { ReactNode } from 'react';
import { Text, makeStyles, tokens } from '@fluentui/react-components';
import { AzureSurface } from './azure/AzureLayout';

// Shared header for every main page: a Fluent 2 page title with an optional subtitle block beneath it
// (consistent vertical rhythm via tokens) and an optional right-aligned actions slot. An
// optional breadcrumb renders above the title. Centralizing this keeps page headers visually
// consistent and gives every page a subtitle without per-page spacing drift.

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  row: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
    rowGap: tokens.spacingVerticalS,
    flexWrap: 'wrap',
  },
  titleBlock: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    minWidth: 0,
  },
  title: {
    display: 'block',
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    letterSpacing: '-0.01em',
  },
  subtitle: {
    display: 'block',
    color: tokens.colorNeutralForeground3,
    fontSize: '14px',
    lineHeight: '20px',
    fontWeight: tokens.fontWeightRegular,
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
    flexWrap: 'wrap',
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
    <AzureSurface className={styles.root} density="spacious" tone="flat">
      {breadcrumb}
      <div className={styles.row}>
        <div className={styles.titleBlock}>
          <Text as="h1" className={styles.title}>{title}</Text>
          {subtitle && <Text className={styles.subtitle}>{subtitle}</Text>}
        </div>
        {actions && <div className={styles.actions}>{actions}</div>}
      </div>
    </AzureSurface>
  );
}
