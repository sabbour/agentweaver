import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { StartOrchestrationDialog } from '../components/StartOrchestrationDialog';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { ReactNode } from 'react';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    startOrchestration: vi.fn(),
    listWorkflows: vi.fn(() => Promise.resolve({ default_workflow_id: 'default', workflows: [] })),
    prepareAiExecutionContext: vi.fn(),
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.prepareAiExecutionContext).mockResolvedValue({
    ai_required: true,
    operation: 'orchestration',
    phase: 'prepared',
    execution_key: 'signed-provider-key',
    expires_at: '2099-01-01T00:00:00Z',
    effective_model_provider: {
      state: 'resolved',
      provider_kind: 'platform_github_copilot',
      resolution_scope: 'project',
      provider_scope: 'platform',
      provider_type: null,
      model_id: 'gpt-5',
      provider_key: 'provider-fingerprint',
      unavailable_reason: null,
    },
  });
});

afterEach(() => {
  cleanup();
});

describe('StartOrchestrationDialog', () => {
  it('starts direct from the prompt without the outcome definition route', async () => {
    vi.mocked(apiClient.startOrchestration).mockResolvedValue({ runId: 'run-direct' } as never);
    const onStarted = vi.fn();

    render(
      <Wrapper>
        <StartOrchestrationDialog projectId="proj-1" onStarted={onStarted} />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start task' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Goal' }), {
      target: { value: 'Make startup faster' },
    });
    await waitFor(() =>
      expect((screen.getByRole('button', { name: 'Direct' }) as HTMLButtonElement).disabled).toBe(false),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Direct' }));

    await waitFor(() =>
      expect(apiClient.startOrchestration).toHaveBeenCalledWith(
        'proj-1',
        'Make startup faster',
        null,
        'direct',
        'signed-provider-key',
      ),
    );
    expect(onStarted).toHaveBeenCalledWith('run-direct');
  });

  it('preserves the outcome definition route under Define Outcome', async () => {
    vi.mocked(apiClient.listWorkflows).mockResolvedValue({
      default_workflow_id: 'software-delivery',
      workflows: [
        { id: 'software-delivery', name: 'Software Delivery', valid: true, source: 'catalog', is_built_in: true, is_default: true, warnings: [] },
      ],
    } as never);
    vi.mocked(apiClient.startOrchestration).mockResolvedValue({ runId: 'run-defined' } as never);
    const onStarted = vi.fn();

    render(
      <Wrapper>
        <StartOrchestrationDialog projectId="proj-1" onStarted={onStarted} />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start task' }));
    const workflow = await screen.findByRole('combobox', { name: 'Workflow' });
    fireEvent.change(workflow, { target: { value: 'software-delivery' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Goal' }), {
      target: { value: 'Ship structured work' },
    });
    await waitFor(() =>
      expect((screen.getByRole('button', { name: 'Define Outcome' }) as HTMLButtonElement).disabled).toBe(false),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Define Outcome' }));

    await waitFor(() =>
      expect(apiClient.startOrchestration).toHaveBeenCalledWith(
        'proj-1',
        'Ship structured work',
        'software-delivery',
        undefined,
        'signed-provider-key',
      ),
    );
    expect(onStarted).toHaveBeenCalledWith('run-defined');
  });

  it('shows a Cast a team CTA when start fails with no_team', async () => {
    vi.mocked(apiClient.startOrchestration).mockRejectedValue(new ApiError(
      409,
      JSON.stringify({
        error: 'no_team',
        message: 'This project has no team. Cast a team before starting an orchestration.',
      }),
    ));

    render(
      <Wrapper>
        <StartOrchestrationDialog projectId="proj-1" onStarted={vi.fn()} />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start task' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Goal' }), {
      target: { value: 'Build something' },
    });
    await waitFor(() =>
      expect((screen.getByRole('button', { name: 'Direct' }) as HTMLButtonElement).disabled).toBe(false),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Direct' }));

    expect(await screen.findByText('This project has no team. Cast a team before starting an orchestration.')).toBeDefined();
    const cta = screen.getByRole('link', { name: 'Cast a team' });
    expect(cta.getAttribute('href')).toBe('/projects/proj-1/team/cast');
    expect(document.body.textContent).not.toContain('API error 409');
  });

  it('shows the replacement provider and requires another click after a provider change', async () => {
    vi.mocked(apiClient.startOrchestration).mockRejectedValueOnce(new ApiError(
      409,
      JSON.stringify({
        error: 'model_provider_changed',
        context: {
          ai_required: true,
          operation: 'orchestration',
          phase: 'prepared',
          execution_key: 'replacement-key',
          expires_at: '2099-01-01T00:00:00Z',
          effective_model_provider: {
            state: 'resolved',
            provider_kind: 'byok',
            resolution_scope: 'project',
            provider_scope: 'platform',
            provider_type: 'azure',
            model_id: 'gpt-5',
            provider_key: 'replacement-fingerprint',
            unavailable_reason: null,
          },
        },
      }),
    ));

    render(
      <Wrapper>
        <StartOrchestrationDialog projectId="proj-1" onStarted={vi.fn()} />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start task' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Goal' }), {
      target: { value: 'Keep the provider stable' },
    });
    const direct = await screen.findByRole('button', { name: 'Direct' });
    await waitFor(() => expect((direct as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(direct);

    expect(await screen.findByText(
      'The AI provider changed. Review the updated provider and start again.',
    )).toBeDefined();
    expect(document.body.textContent).toContain('Expected provider: Azure BYOK');
    expect(apiClient.startOrchestration).toHaveBeenCalledTimes(1);
  });
});
