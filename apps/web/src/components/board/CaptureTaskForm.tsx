import { useState } from 'react';
import { Button, Input, Text, makeStyles, tokens } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  row: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
  },
  input: {
    flex: 1,
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
  },
});

export interface CaptureTaskFormProps {
  projectId: string;
  onCaptured: () => void | Promise<void>;
}

// Capture a task into Backlog (FR-001/002). Empty/whitespace titles are rejected
// client-side; the server is the backstop. On success, refetch the board.
export function CaptureTaskForm({ projectId, onCaptured }: CaptureTaskFormProps) {
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
      await apiClient.captureBacklogTask(projectId, { title: title.trim() });
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
      <div className={styles.row}>
        <Input
          className={styles.input}
          value={title}
          placeholder="Capture a task into Backlog"
          aria-label="New task title"
          disabled={busy}
          onChange={(_, v) => { setTitle(v.value); if (error) setError(null); }}
          onKeyDown={(e) => { if (e.key === 'Enter') void submit(); }}
        />
        <Button appearance="primary" icon={<AddRegular />} disabled={busy || !title.trim()} onClick={() => void submit()}>
          Add
        </Button>
      </div>
      {error && <Text className={styles.error}>{error}</Text>}
    </div>
  );
}
