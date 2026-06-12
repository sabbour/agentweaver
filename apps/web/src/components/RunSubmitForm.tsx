import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Button,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Textarea,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';

const useStyles = makeStyles({
  form: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '640px',
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
  },
});

export function RunSubmitForm() {
  const styles = useStyles();
  const navigate = useNavigate();

  const [repositoryPath, setRepositoryPath] = useState('');
  const [originatingBranch, setOriginatingBranch] = useState('');
  const [task, setTask] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const onSubmit = async () => {
    setError(null);
    setSubmitting(true);
    try {
      const response = await apiClient.submitRun({
        repository_path: repositoryPath,
        originating_branch: originatingBranch,
        task,
        model_source: 'github-copilot',
      });
      navigate(`/watch/${response.run_id}`);
    } catch (err) {
      if (err instanceof ApiError) {
        setError(`API error ${err.status}: ${err.body}`);
      } else {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setSubmitting(false);
    }
  };

  const canSubmit =
    repositoryPath.trim().length > 0 &&
    originatingBranch.trim().length > 0 &&
    task.trim().length > 0 &&
    !submitting;

  return (
    <div className={styles.form}>
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      <Field label="Repository path" required>
        <Input
          value={repositoryPath}
          placeholder="C:/path/to/repo"
          onChange={(_, data) => setRepositoryPath(data.value)}
        />
      </Field>

      <Field label="Originating branch" required>
        <Input
          value={originatingBranch}
          placeholder="main"
          onChange={(_, data) => setOriginatingBranch(data.value)}
        />
      </Field>

      <Field label="Task description" required>
        <Textarea
          value={task}
          resize="vertical"
          rows={5}
          placeholder="Describe the task for the agent to perform"
          onChange={(_, data) => setTask(data.value)}
        />
      </Field>

      <div className={styles.actions}>
        <Button appearance="primary" disabled={!canSubmit} onClick={onSubmit}>
          {submitting ? 'Submitting' : 'Submit'}
        </Button>
      </div>
    </div>
  );
}
