/**
 * The 0.x "Start task" floating button for the v1 shell. It opens a dialog
 * with the capture form, adds the task to the current project's backlog, and
 * then opens the project Board, where the new task shows in Backlog.
 */
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Text,
  Tooltip,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular, FlowRegular } from '@fluentui/react-icons';
import { useCallback, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { CaptureTaskForm } from '../components/board/CaptureTaskForm';
import { useAuth } from './AuthContext';
import type { BoardLocationState } from './BoardPage';
import { boardErrorMessage, captureTask } from './boardData';
import { permissionsForProject, useAuthorizationContext } from './projectData';

/**
 * Renders the Start task button for one project. It renders nothing until the
 * access check shows that the user can change the project, so a user who
 * can't add tasks never sees a button that would fail.
 */
export function StartTaskAction({ projectId, projectName }: { projectId: string; projectName?: string }) {
  const { apiCall } = useAuth();
  const navigate = useNavigate();
  const authorization = useAuthorizationContext();
  const [open, setOpen] = useState(false);
  // The title of the task just added, kept for the Board's confirmation after the form resets.
  const capturedTitle = useRef<string | null>(null);
  const canWrite = useMemo(() => !!authorization.data
    && permissionsForProject(authorization.data, projectId).some((item) => item.permission === 'writeProjects'),
  [authorization.data, projectId]);

  // Called by the capture form. Errors are rethrown as plain messages because the form shows `error.message`.
  const capture = useCallback(async (title: string) => {
    try {
      // No graph is loaded here, so captureTask reads the current revision first.
      await captureTask(apiCall, projectId, title);
      capturedTitle.current = title;
    } catch (reason) {
      throw new Error(boardErrorMessage(reason, 'capture'), { cause: reason });
    }
  }, [apiCall, projectId]);

  // Runs after a successful capture: close the dialog and open the Board. The
  // route state carries the title so the Board can confirm what was added.
  const onCaptured = useCallback(() => {
    setOpen(false);
    const state: BoardLocationState = { capturedTaskTitle: capturedTitle.current ?? undefined };
    navigate(`/projects/${encodeURIComponent(projectId)}/board`, { state });
  }, [navigate, projectId]);

  if (!canWrite) return null;

  // The wrapper uses the 0.x floating-actions strip styles from the shell stylesheet.
  return (
    <div className="aw-floating-actions">
      <Dialog open={open} onOpenChange={(_, data) => setOpen(data.open)}>
        <Tooltip content="Start task" relationship="label" positioning="before">
          <Button
            icon={<FlowRegular />}
            aria-label="Start task"
            data-testid="start-task-topbar-action"
            onClick={() => setOpen(true)}
          >
            Start task
          </Button>
        </Tooltip>
        <DialogSurface>
          <DialogBody>
            <DialogTitle
              action={(
                <DialogTrigger disableButtonEnhancement>
                  <Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} />
                </DialogTrigger>
              )}
            >
              Start a task
            </DialogTitle>
            <DialogContent>
              <Text as="p" style={{ marginTop: 0, color: tokens.colorNeutralForeground2 }}>
                {projectName
                  ? `The task is added to the Backlog of ${projectName}. The Board opens when it is saved.`
                  : 'The task is added to this project\'s Backlog. The Board opens when it is saved.'}
              </Text>
              <CaptureTaskForm projectId={projectId} capture={capture} onCaptured={onCaptured} />
            </DialogContent>
            <DialogActions>
              <DialogTrigger disableButtonEnhancement>
                <Button appearance="secondary">Cancel</Button>
              </DialogTrigger>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
