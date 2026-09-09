import { Text, makeStyles, tokens } from '@fluentui/react-components';
import { cloneElement, useId } from 'react';
import type { ReactElement, ReactNode } from 'react';
import { aiExecutionProviderLabel } from './aiExecutionContext';
import type { AiExecutionContext } from '../api/types';

const useStyles = makeStyles({
  root: {
    display: 'inline-flex',
    flexDirection: 'column',
    alignItems: 'flex-end',
    gap: tokens.spacingVerticalXXS,
  },
  label: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    lineHeight: tokens.lineHeightBase100,
  },
});

export function AiExecutionProviderHint({
  context,
  children,
}: {
  context: AiExecutionContext | null;
  children: ReactElement<{ 'aria-describedby'?: string; title?: string }>;
}) {
  const styles = useStyles();
  const label = aiExecutionProviderLabel(context);
  const descriptionId = useId();
  const describedBy = [children.props['aria-describedby'], descriptionId]
    .filter(Boolean)
    .join(' ');
  return (
    <span className={styles.root}>
      {cloneElement(children, {
        'aria-describedby': describedBy,
        title: children.props.title ?? label,
      })}
      <Text id={descriptionId} className={styles.label}>{label}</Text>
    </span>
  );
}

export function AiExecutionProviderStatus({
  context,
  children,
}: {
  context: AiExecutionContext | null;
  children: ReactNode;
}) {
  const styles = useStyles();
  const label = aiExecutionProviderLabel(context);
  return (
    <span className={styles.root} title={label}>
      {children}
      <Text className={styles.label}>{label}</Text>
    </span>
  );
}

export function AiProviderChangeAnnouncement({ message }: { message: string }) {
  return (
    <span
      aria-atomic="true"
      aria-live="polite"
      style={{
        position: 'absolute',
        width: 1,
        height: 1,
        padding: 0,
        margin: -1,
        overflow: 'hidden',
        clip: 'rect(0, 0, 0, 0)',
        whiteSpace: 'nowrap',
        border: 0,
      }}
    >
      {message}
    </span>
  );
}
