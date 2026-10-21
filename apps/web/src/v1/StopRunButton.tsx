/**
 * Stop run action for the run page header. It shows only while the run is
 * open and asks for confirmation before stopping the run.
 */
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
} from '@fluentui/react-components';
import { StopRegular } from '@fluentui/react-icons';
import { useRef, useState } from 'react';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { CancelProjectRunResponse } from './contracts';
import { errorMessage } from './errors';
import { ErrorNotice } from './pageParts';
import { cancelRunError } from './pendingRun';

export interface StopRunButtonProps {
  projectId: string;
  runId: string;
  /** Owner execution state from the latest run snapshot. */
  state: string | null | undefined;
  /** Called after the service accepted the stop, or reported the run already ended. */
  onStopped?: (result: CancelProjectRunResponse) => void;
}

/** Execution states in which a run can still be stopped. */
const OPEN_STATES = new Set(['idle', 'active', 'blocked']);

/** Confirmed Stop run button. Renders nothing for finished or unknown states. */
export function StopRunButton({ projectId, runId, state, onStopped }: StopRunButtonProps) {
  const { apiCall, ensureSession } = useAuth();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  // One idempotency key per opened dialog. A retry after a failure reuses it,
  // so the service treats both requests as the same stop.
  const key = useRef<string | null>(null);

  if (!state || !OPEN_STATES.has(state)) return null;

  /** Opens the dialog with a fresh key and no old error. */
  const openDialog = () => {
    key.current = crypto.randomUUID();
    setError(null);
    setOpen(true);
  };

  /** Sends the stop request and closes the dialog when it succeeds. */
  const confirm = async () => {
    if (busy) return;
    key.current ??= crypto.randomUUID();
    const idempotencyKey = key.current;
    setBusy(true);
    setError(null);
    try {
      // Stop needs the project session that is not bound to a run. The run page
      // uses a run-bound session, so get the other one without a prompt. It is
      // stored but not made active, so the run page keeps its session.
      if (!await ensureSession(null, false))
        throw new Error('Your sign-in expired. Sign in again, then stop the run.');
      const result = await apiCall(
        (token, tenant) => gatewayClient.cancelProjectRun(token, projectId, runId, idempotencyKey, undefined, tenant),
        null,
      );
      key.current = null;
      setOpen(false);
      onStopped?.(result);
    } catch (reason) {
      // Keep the dialog and key so the user can retry the same stop.
      setError(cancelRunError(reason));
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <Button appearance="secondary" icon={<StopRegular />} onClick={openDialog}>Stop run</Button>
      <Dialog open={open} onOpenChange={(_, data) => { if (!busy) setOpen(data.open); }}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Stop this run?</DialogTitle>
            <DialogContent>
              <p>Agents stop working and the run ends.</p>
              {error != null && <ErrorNotice>{errorMessage(error)}</ErrorNotice>}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" disabled={busy} onClick={() => setOpen(false)}>Cancel</Button>
              <Button appearance="primary" disabled={busy} onClick={() => void confirm()}>
                {busy ? 'Stopping…' : 'Stop run'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </>
  );
}
