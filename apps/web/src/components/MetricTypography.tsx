import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { Body, Headline, TitleText } from './ui';

const useStyles = makeStyles({
  sectionHeader: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
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
  cardSubtitleText: {
    maxWidth: '72ch',
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
      <Headline as="h2">{title}</Headline>
      {subtitle ? <Body tone="muted">{subtitle}</Body> : null}
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
        <TitleText as="h3">{title}</TitleText>
        {subtitle ? <Body tone="muted" className={styles.cardSubtitleText}>{subtitle}</Body> : null}
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
  return <Body tone="muted" className={className}>{children}</Body>;
}
