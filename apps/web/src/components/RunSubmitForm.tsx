import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Button,
  Dropdown,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Option,
  Textarea,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import type { ModelSource } from '../api/types';
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

const MODEL_LABELS: Record<ModelSource, string> = {
  'github-copilot': 'GitHub Copilot',
  'microsoft-foundry': 'Microsoft Foundry',
};

export function RunSubmitForm() {
  const styles = useStyles();
  const navigate = useNavigate();

  const [repositoryPath, setRepositoryPath] = useState('');
  const [originatingBranch, setOriginatingBranch] = useState('');
  const [task, setTask] = useState('');
  const [modelSource, setModelSource] = useState<ModelSource>('github-copilot');
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
        model_source: modelSource,
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

      <Field label="Model source" required>
        <Dropdown
          value={MODEL_LABELS[modelSource]}
          selectedOptions={[modelSource]}
          onOptionSelect={(_, data) => {
            if (data.optionValue) {
              setModelSource(data.optionValue as ModelSource);
            }
          }}
        >
          <Option value="github-copilot">GitHub Copilot</Option>
          <Option value="microsoft-foundry">Microsoft Foundry</Option>
        </Dropdown>
      </Field>

      <div className={styles.actions}>
        <Button appearance="primary" disabled={!canSubmit} onClick={onSubmit}>
          {submitting ? 'Submitting' : 'Submit'}
        </Button>
      </div>
    </div>
  );
}
