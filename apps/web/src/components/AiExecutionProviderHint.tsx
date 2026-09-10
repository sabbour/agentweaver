import {
  Button,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { cloneElement, useId } from 'react';
import type { ReactElement, ReactNode } from 'react';
import { aiExecutionProviderLabel, aiExecutionProviderScope } from './aiExecutionContext';
import type { AiExecutionContext } from '../api/types';

const useStyles = makeStyles({
  root: {
    display: 'inline-flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
    maxWidth: '100%',
  },
  label: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    lineHeight: tokens.lineHeightBase100,
    maxWidth: '100%',
    overflowWrap: 'anywhere',
    textAlign: 'left',
    whiteSpace: 'normal',
  },
});

export function AiExecutionProviderHint({
  context,
  loading = false,
  error,
  required = false,
  children,
}: {
  context: AiExecutionContext | null;
  loading?: boolean;
  error?: string | null;
  required?: boolean;
  children: ReactElement<{ 'aria-describedby'?: string; title?: string }>;
}) {
  const styles = useStyles();
  const label = required
    ? 'Enter a goal to continue'
    : loading
      ? 'Checking AI provider readiness'
      : error
        ? 'Could not check AI provider readiness'
        : context
          ? aiExecutionProviderLabel(context)
          : 'Provider details not recorded';
  const scope = aiExecutionProviderScope(context);
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
      {scope && <Text className={styles.label}>Scope: {scope}.</Text>}
    </span>
  );
}

export function AiExecutionProviderStatus({
  context,
  loading = false,
  error,
  children,
}: {
  context: AiExecutionContext | null;
  loading?: boolean;
  error?: string | null;
  children?: ReactNode;
}) {
  const styles = useStyles();
  const label = loading
    ? 'Checking AI provider readiness'
    : error
      ? 'Could not check AI provider readiness'
      : context
        ? aiExecutionProviderLabel(context)
        : 'Provider details not recorded';
  const scope = context ? aiExecutionProviderScope(context) : null;
  return (
    <span className={styles.root} title={label} role="status">
      {children}
      <Text className={styles.label}>{label}</Text>
      {scope && <Text className={styles.label}>Scope: {scope}.</Text>}
    </span>
  );
}

function remediation(
  context: AiExecutionContext | null,
  projectId?: string,
): { message: string; href?: string; action?: string } {
  const reason = context?.effective_model_provider?.unavailable_reason;
  switch (reason) {
    case 'no_provider':
      return {
        message: 'A Platform Administrator must configure a model provider before you can continue.',
        href: '/platform-settings',
        action: 'Open Platform settings',
      };
    case 'project_binding_requires_reauthorization':
      return {
        message: 'The project GitHub Copilot connection needs authorization again.',
        href: projectId ? `/projects/${encodeURIComponent(projectId)}/settings` : undefined,
        action: 'Open Project settings',
      };
    case 'user_provider_required':
      return {
        message: 'Configure personal AI access before you continue.',
        href: '/settings',
        action: 'Open AI Access settings',
      };
    case 'user_binding_requires_reauthorization':
      return {
        message: 'Your GitHub Copilot connection needs authorization again.',
        href: '/settings',
        action: 'Open AI Access settings',
      };
    case 'operation_requires_github_copilot':
      return {
        message: 'This operation requires GitHub Copilot and cannot use the current BYOK provider.',
        href: projectId ? `/projects/${encodeURIComponent(projectId)}/settings` : undefined,
        action: 'Open Project settings',
      };
    default:
      return { message: 'The AI provider is unavailable. Review the provider and try again.' };
  }
}

export function AiExecutionProviderReadiness({
  context,
  error,
  projectId,
  onRefresh,
}: {
  context: AiExecutionContext | null;
  error?: string | null;
  projectId?: string;
  onRefresh: () => void;
}) {
  const unavailable = context?.effective_model_provider?.state === 'unavailable';
  if (!unavailable && !error) return null;

  const nextStep = remediation(context, projectId);
  return (
    <MessageBar intent="warning">
      <MessageBarBody>
        {unavailable ? nextStep.message : error}
        {' '}
        {!unavailable && context && <span>{aiExecutionProviderLabel(context)} </span>}
      </MessageBarBody>
      <MessageBarActions>
        {nextStep.href && nextStep.action && (
          <Button appearance="secondary" size="small" as="a" href={nextStep.href}>
            {nextStep.action}
          </Button>
        )}
        <Button appearance="secondary" size="small" onClick={onRefresh}>
          Refresh provider
        </Button>
      </MessageBarActions>
    </MessageBar>
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
