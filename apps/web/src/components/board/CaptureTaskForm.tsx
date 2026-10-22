import {
  apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import { Button, makeStyles, Text, Textarea, tokens } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { useState } from 'react';
const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  textarea: {
    width: '100%',
    // Resize handle hidden; height is set by rows prop.
    resize: 'none',
  },
  actions: {
    display: 'flex',
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'flex-end',
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
  },
});

export interface CaptureTaskFormProps {
  projectId: string;
  onCaptured: () => void | Promise<void>;
  /**
   * Optional submit handler for callers that own their own API client. When it is
   * set, the form calls it with the trimmed title instead of the built-in client.
   */
  capture?: (title: string) => Promise<void>;
  /** Disables the form while the caller can't accept a capture, for example during a board reload. */
  disabled?: boolean;
}

// Capture a task into Backlog (FR-001/002). Empty/whitespace titles are rejected
// client-side; the server is the backstop. On success, refetch the board.
/**
 * Renders a form for capturing a new task and submits it through the board/task API.
 * Local state holds the draft and submit feedback; props identify the project and report success
 * or dismissal to the parent.
 */
export function CaptureTaskForm({ projectId, onCaptured, capture, disabled = false }: CaptureTaskFormProps) {
  const styles = useStyles();
  const [title, setTitle] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    if (!title.trim()) {
      setError('Title is required.');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      // An injected handler owns the request, its revision checks and its errors.
      // Without one, keep the original board client call.
      if (capture) await capture(title.trim());
      else await apiClient.captureBacklogTask(projectId, { title: title.trim() });
      setTitle('');
      await onCaptured();
    } catch (e) {
      setError(e instanceof ApiError ? `API error ${e.status}: ${e.body}` : e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.root}>
      <Textarea
        className={styles.textarea}
        value={title}
        placeholder="Capture a task into Backlog"
        aria-label="New task title"
        disabled={busy || disabled}
        rows={3}
        resize="none"
        onChange={(_, v) => { setTitle(v.value); if (error) setError(null); }}
        onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void submit(); } }}
      />
      {error && <Text className={styles.error}>{error}</Text>}
      <div className={styles.actions}>
        <Button appearance="primary" icon={<AddRegular />} disabled={busy || disabled || !title.trim()} onClick={() => void submit()}>
          Add
        </Button>
      </div>
    </div>
  );
}
