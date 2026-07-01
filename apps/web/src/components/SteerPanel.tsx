import { useState } from 'react';
import {
  Button,
  Field,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Textarea,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowRoutingRegular, EditRegular, SendRegular, StopRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { SteerCoordinatorRequest, SteerKind } from '../api/types';
import { SteeringLegend } from './SteeringLegend';

// ---------------------------------------------------------------------------
// Payload builder — contract confirmed by Morpheus 2026-06-22.
// Update ONLY this function if the verb/body fields ever change.
// ---------------------------------------------------------------------------
function buildSteerPayload(
  kind: SteerKind,
  instruction: string,
  targetChildRunId?: string,
): SteerCoordinatorRequest {
  return {
    kind,
    instruction: kind === 'stop' ? undefined : instruction || undefined,
    ...(targetChildRunId ? { target_child_run_id: targetChildRunId } : {}),
  };
}

// Maps a successful steer response status to a user-facing message.
function successMessage(kind: SteerKind, status: string): string {
  if (kind === 'send')
    return 'Message sent — waiting for coordinator response.';
  if (status === 'applied')
    return 'Coordinator resumed — waiting for updated progress.';
  if (status === 'queued')
    return 'Directive queued — waiting for the coordinator to reach the next step.';
  return 'Steering request sent — waiting for coordinator response.';
}

// Maps API error body to a user-facing message.
// Handles: 403 not-owner, 409 steering_recovery_exhausted, generic.
function errorMessageFromApiError(err: ApiError): string {
  if (err.status === 403)
    return 'You are not the owner of this run and cannot steer it.';
  if (err.status === 409) {
    try {
      const body = JSON.parse(err.body) as { error?: string; message?: string };
      if (body.error === 'steering_recovery_exhausted') {
        return body.message?.trim()
          || 'This run has already been retried the maximum number of times and can\'t be auto-resumed. You may need to stop it and start fresh.';
      }
    } catch { /* not JSON — fall through to generic */ }
  }
  return `Steer failed (${err.status}): ${err.body}`;
}

// Auto-generated default instruction shown in the text area placeholder and
// used as fallback when the user submits without typing.
function defaultInstruction(blockReason: string | undefined): string {
  switch (blockReason) {
    case 'integration_conflict':
      return 'Resolve the integration conflict by re-running the affected subtask(s) against the latest changes on the base branch.';
    case 'integration_build_error':
      return 'Re-run the failing subtask(s) after reviewing the build error in the integration branch.';
    case 'merge_failed':
      return 'Re-run the failing assembly step against the updated base branch.';
    default:
      return 'Review the blocked state and proceed by re-running or adjusting the affected subtasks.';
  }
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  noPermission: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },
});

export interface SteerPanelProps {
  runId: string;
  /** The raw block/fail reason code from the orchestration state. */
  blockReason?: string;
  /**
   * When set, a Redirect carries this child run id as target_child_run_id so the backend
   * force-completes that child's stream to unblock it.
   */
  targetChildRunId?: string;
  /**
   * When false, renders a read-only "owner only" note instead of the controls.
   * Defaults to true. Set to false when the viewer is not the run owner.
   */
  canSteer?: boolean;
  /** Called after a successful steer submission (e.g. to trigger a board refresh). */
  onSteered?: (result: { kind: SteerKind; status: string }) => void;
}

type SteerState = 'idle' | 'pending' | 'success' | 'error';

export function SteerPanel({ runId, blockReason, targetChildRunId, canSteer = true, onSteered }: SteerPanelProps) {
  const styles = useStyles();
  const [instruction, setInstruction] = useState('');
  const [steerState, setSteerState] = useState<SteerState>('idle');
  const [statusMessage, setStatusMessage] = useState<string | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  const isPending = steerState === 'pending';
  const fallbackInstruction = defaultInstruction(blockReason);

  const submit = async (kind: SteerKind) => {
    if (isPending) return;
    setSteerState('pending');
    setStatusMessage(null);
    setErrorMessage(null);
    const text = instruction.trim() || (kind === 'stop' ? '' : fallbackInstruction);
    // A child target only applies to a Redirect (force-complete that child to unblock it).
    const target = kind === 'redirect' ? targetChildRunId : undefined;
    try {
      const res = await apiClient.steerCoordinator(runId, buildSteerPayload(kind, text, target));
      setSteerState('success');
      setStatusMessage(successMessage(kind, res.status));
      setInstruction('');
      onSteered?.({ kind, status: res.status });
    } catch (err) {
      setErrorMessage(
        err instanceof ApiError
          ? errorMessageFromApiError(err)
          : err instanceof Error
            ? err.message
            : String(err),
      );
      setSteerState('error');
    }
  };

  if (!canSteer) {
    return (
      <div className={styles.root} data-testid="steer-panel">
        <Text className={styles.noPermission} data-testid="steer-panel-no-permission">
          Only the run owner can steer the coordinator.
        </Text>
      </div>
    );
  }

  return (
    <div className={styles.root} data-testid="steer-panel">
      <Field
        label="Tell the coordinator how to resolve this (optional)"
        hint="Leave blank to use the suggested default action."
      >
        <Textarea
          resize="vertical"
          placeholder={fallbackInstruction}
          value={instruction}
          onChange={(_, v) => setInstruction(v.value)}
          disabled={isPending}
          rows={3}
          data-testid="steer-panel-instruction"
        />
      </Field>

      {steerState === 'success' && statusMessage && (
        <MessageBar intent="success" data-testid="steer-panel-success">
          <MessageBarBody>{statusMessage}</MessageBarBody>
        </MessageBar>
      )}

      {steerState === 'error' && errorMessage && (
        <MessageBar intent="error" data-testid="steer-panel-error">
          <MessageBarBody>{errorMessage}</MessageBarBody>
        </MessageBar>
      )}

      <SteeringLegend />

      <div className={styles.actions}>
        <Button
          appearance="primary"
          icon={isPending ? <Spinner size="tiny" /> : <SendRegular />}
          disabled={isPending}
          onClick={() => void submit('send')}
          data-testid="steer-panel-send"
        >
          Send
        </Button>
        <Button
          appearance="outline"
          icon={<ArrowRoutingRegular />}
          disabled={isPending}
          onClick={() => void submit('redirect')}
          data-testid="steer-panel-redirect"
        >
          Redirect
        </Button>
        <Button
          appearance="outline"
          icon={<EditRegular />}
          disabled={isPending}
          onClick={() => void submit('amend')}
          data-testid="steer-panel-amend"
        >
          Amend
        </Button>
        <Button
          appearance="subtle"
          icon={<StopRegular />}
          disabled={isPending}
          onClick={() => void submit('stop')}
          data-testid="steer-panel-stop"
        >
          Stop run
        </Button>
        {isPending && <Spinner size="extra-tiny" aria-label="Steering" />}
      </div>
    </div>
  );
}
