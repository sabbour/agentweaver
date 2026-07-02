import type { ReactNode } from 'react';
import { Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';

const useStyles = makeStyles({
  sectionHeader: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  sectionTitle: {
    display: 'block',
    fontSize: '20px',
    lineHeight: '28px',
    fontWeight: tokens.fontWeightSemibold,
  },
  sectionSubtitle: {
    display: 'block',
    fontSize: '14px',
    lineHeight: '20px',
    fontWeight: tokens.fontWeightRegular,
    color: tokens.colorNeutralForeground3,
  },
  cardHeader: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  cardHeaderText: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  cardTitle: {
    display: 'block',
    fontSize: '16px',
    lineHeight: '22px',
    fontWeight: tokens.fontWeightSemibold,
  },
  cardSubtitle: {
    display: 'block',
    fontSize: '14px',
    lineHeight: '20px',
    fontWeight: tokens.fontWeightRegular,
    color: tokens.colorNeutralForeground3,
    maxWidth: '72ch',
  },
  emptyState: {
    display: 'block',
    fontSize: '14px',
    lineHeight: '20px',
    fontWeight: tokens.fontWeightRegular,
    color: tokens.colorNeutralForeground2,
  },
});

export function MetricSectionHeading({
  title,
  subtitle,
  className,
}: {
  title: ReactNode;
  subtitle?: ReactNode;
  className?: string;
}) {
  const styles = useStyles();

  return (
    <div className={mergeClasses(styles.sectionHeader, className)}>
      <Text as="h2" className={styles.sectionTitle}>
        {title}
      </Text>
      {subtitle ? <Text className={styles.sectionSubtitle}>{subtitle}</Text> : null}
    </div>
  );
}

export function MetricCardHeader({
  title,
  subtitle,
  aside,
  className,
}: {
  title: ReactNode;
  subtitle?: ReactNode;
  aside?: ReactNode;
  className?: string;
}) {
  const styles = useStyles();

  return (
    <div className={mergeClasses(styles.cardHeader, className)}>
      <div className={styles.cardHeaderText}>
        <Text as="h3" className={styles.cardTitle}>
          {title}
        </Text>
        {subtitle ? <Text className={styles.cardSubtitle}>{subtitle}</Text> : null}
      </div>
      {aside}
    </div>
  );
}

export function MetricEmptyState({
  children,
  className,
}: {
  children: ReactNode;
  className?: string;
}) {
  const styles = useStyles();

  return <Text className={mergeClasses(styles.emptyState, className)}>{children}</Text>;
}
