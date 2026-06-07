import { useState } from 'react';
import {
  Button,
  Field,
  Input,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { createRun, ApiError } from '../api/client';
import type { RunResponse } from '../api/client';

interface SubmitRunPageProps {
  onRunCreated: (run: RunResponse) => void;
}

/**
 * T061: SubmitRunPage — Fluent 2 form for submitting a new agent run.
 * modelSource dropdown limited to exactly the two supported providers.
 * No emojis (NFR-002).
 */
export function SubmitRunPage({ onRunCreated }: SubmitRunPageProps) {
  const [originatingBranch, setOriginatingBranch] = useState('');
  const [taskPrompt, setTaskPrompt] = useState('');
  const [modelSource, setModelSource] = useState<'CopilotSdk' | 'MicrosoftFoundry'>('CopilotSdk');
  const [maxSteps, setMaxSteps] = useState('');
  const [maxDuration, setMaxDuration] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});

  const validate = (): boolean => {
    const errors: Record<string, string> = {};
    if (!originatingBranch.trim()) {
      errors.originatingBranch = 'Originating branch is required.';
    }
    if (!taskPrompt.trim()) {
      errors.taskPrompt = 'Task prompt is required.';
    }
    if (maxSteps && (isNaN(Number(maxSteps)) || Number(maxSteps) < 1)) {
      errors.maxSteps = 'Max steps must be a positive integer.';
    }
    if (maxDuration && (isNaN(Number(maxDuration)) || Number(maxDuration) < 1)) {
      errors.maxDuration = 'Max duration must be a positive integer.';
    }
    setFieldErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!validate()) return;

    setLoading(true);
    setError(null);

    try {
      const run = await createRun({
        originatingBranch: originatingBranch.trim(),
        taskPrompt: taskPrompt.trim(),
        modelSource,
        maxSteps: maxSteps ? Number(maxSteps) : undefined,
        maxDurationSeconds: maxDuration ? Number(maxDuration) : undefined,
      });
      onRunCreated(run);
    } catch (err) {
      if (err instanceof ApiError) {
        setError(`API error ${err.status}: ${err.body}`);
      } else {
        setError('An unexpected error occurred. Is the API server running?');
      }
    } finally {
      setLoading(false);
    }
  };

  return (
    <div style={{ maxWidth: '600px', padding: '24px' }}>
      <Text as="h1" size={700} weight="semibold" block>
        Submit Agent Run
      </Text>
      <Text block style={{ color: tokens.colorNeutralForeground3, marginBottom: '24px' }}>
        Configure and submit a new file-editing agent run.
      </Text>

      <form onSubmit={handleSubmit} style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
        <Field
          label="Originating Branch"
          required
          validationMessage={fieldErrors.originatingBranch}
          validationState={fieldErrors.originatingBranch ? 'error' : 'none'}
        >
          <Input
            value={originatingBranch}
            onChange={(_, d) => setOriginatingBranch(d.value)}
            placeholder="main"
          />
        </Field>

        <Field
          label="Task Prompt"
          required
          validationMessage={fieldErrors.taskPrompt}
          validationState={fieldErrors.taskPrompt ? 'error' : 'none'}
        >
          <Textarea
            value={taskPrompt}
            onChange={(_, d) => setTaskPrompt(d.value)}
            placeholder="Describe what the agent should do..."
            rows={4}
          />
        </Field>

        <Field label="Model Source" required>
          <Select
            value={modelSource}
            onChange={(_, d) =>
              setModelSource(d.value as 'CopilotSdk' | 'MicrosoftFoundry')
            }
          >
            <option value="CopilotSdk">GitHub Copilot SDK</option>
            <option value="MicrosoftFoundry">Microsoft Foundry</option>
          </Select>
        </Field>

        <Field
          label="Max Steps (optional)"
          validationMessage={fieldErrors.maxSteps}
          validationState={fieldErrors.maxSteps ? 'error' : 'none'}
        >
          <Input
            type="number"
            value={maxSteps}
            onChange={(_, d) => setMaxSteps(d.value)}
            placeholder="200"
          />
        </Field>

        <Field
          label="Max Duration in Seconds (optional)"
          validationMessage={fieldErrors.maxDuration}
          validationState={fieldErrors.maxDuration ? 'error' : 'none'}
        >
          <Input
            type="number"
            value={maxDuration}
            onChange={(_, d) => setMaxDuration(d.value)}
            placeholder="1800"
          />
        </Field>

        {error && (
          <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{error}</Text>
        )}

        <Button
          type="submit"
          appearance="primary"
          disabled={loading}
          icon={loading ? <Spinner size="tiny" /> : undefined}
        >
          {loading ? 'Submitting...' : 'Submit Run'}
        </Button>
      </form>
    </div>
  );
}
