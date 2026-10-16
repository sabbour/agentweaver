/**
 * EmptyState / LoadingState / ErrorState — consistent status surfaces.
 *
 * - EmptyState: a calm, centered prompt with an optional icon + action.
 * - LoadingState: skeletons (Fluent Skeleton, which honors reduced-motion).
 * - ErrorState: a plainly-worded message with a retry action.
 *
 * Native @fluentui/react-components only; theme tokens only. Status color is
 * reserved for the error icon and always paired with text (never color-alone).
 */

import {
  Button,
  makeStyles,
  mergeClasses,
  Skeleton,
  SkeletonItem,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwiseRegular, ErrorCircleRegular } from '@fluentui/react-icons';
import type { ReactNode } from 'react';
import { Body, TitleText } from './typography';

/* -------------------------------------------------------------------------- */
/* EmptyState                                                                  */
/* -------------------------------------------------------------------------- */

const useEmptyStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: tokens.spacingVerticalS,
    textAlign: 'center',
    padding: `${tokens.spacingVerticalXXL} ${tokens.spacingHorizontalXL}`,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  icon: {
    fontSize: '28px',
    lineHeight: '28px',
    color: tokens.colorNeutralForeground3,
  },
  description: {
    maxWidth: '52ch',
  },
  action: {
    marginTop: tokens.spacingVerticalS,
  },
});

export interface EmptyStateProps {
  title: ReactNode;
  description?: ReactNode;
  icon?: ReactNode;
  /** Optional call-to-action. */
  action?: ReactNode;
  className?: string;
}

export function EmptyState({ title, description, icon, action, className }: EmptyStateProps) {
  const styles = useEmptyStyles();
  return (
    <div className={mergeClasses(styles.root, className)} role="status">
      {icon && <span className={styles.icon}>{icon}</span>}
      <TitleText>{title}</TitleText>
      {description && (
        <Body tone="muted" className={styles.description}>
          {description}
        </Body>
      )}
      {action && <div className={styles.action}>{action}</div>}
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* LoadingState                                                                */
/* -------------------------------------------------------------------------- */

const useLoadingStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalL,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  line: {
    height: '12px',
    borderRadius: tokens.borderRadiusSmall,
  },
  short: { width: '40%' },
  medium: { width: '65%' },
});

export interface LoadingStateProps {
  /** Number of skeleton rows to render (default: 3). */
  rows?: number;
  /** Accessible label announced to screen readers. */
  label?: string;
  className?: string;
}

export function LoadingState({ rows = 3, label = 'Loading', className }: LoadingStateProps) {
  const styles = useLoadingStyles();
  return (
    <div className={mergeClasses(styles.root, className)} role="status" aria-label={label} aria-busy="true">
      {Array.from({ length: rows }).map((_, index) => (
        <Skeleton key={index} className={styles.card} aria-hidden="true">
          <SkeletonItem className={mergeClasses(styles.line, styles.medium)} />
          <SkeletonItem className={mergeClasses(styles.line, styles.short)} />
        </Skeleton>
      ))}
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* ErrorState                                                                  */
/* -------------------------------------------------------------------------- */

const useErrorStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: tokens.spacingVerticalS,
    textAlign: 'center',
    padding: `${tokens.spacingVerticalXXL} ${tokens.spacingHorizontalXL}`,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  icon: {
    fontSize: '28px',
    lineHeight: '28px',
    color: tokens.colorStatusDangerForeground1,
  },
  message: {
    maxWidth: '52ch',
  },
  action: {
    marginTop: tokens.spacingVerticalS,
  },
});

export interface ErrorStateProps {
  /** Short, plainly-worded headline (default: "Something went wrong"). */
  title?: ReactNode;
  /** The error detail / message. */
  message?: ReactNode;
  /** Retry handler — renders a retry button when provided. */
  onRetry?: () => void;
  retryLabel?: string;
  className?: string;
}

export function ErrorState({
  title = 'Something went wrong',
  message,
  onRetry,
  retryLabel = 'Try again',
  className,
}: ErrorStateProps) {
  const styles = useErrorStyles();
  return (
    <div className={mergeClasses(styles.root, className)} role="alert">
      <span className={styles.icon}>
        <ErrorCircleRegular aria-hidden="true" />
      </span>
      <TitleText>{title}</TitleText>
      {message && (
        <Body tone="muted" className={styles.message}>
          {message}
        </Body>
      )}
      {onRetry && (
        <div className={styles.action}>
          <Button appearance="secondary" icon={<ArrowClockwiseRegular />} onClick={onRetry}>
            {retryLabel}
          </Button>
        </div>
      )}
    </div>
  );
}
