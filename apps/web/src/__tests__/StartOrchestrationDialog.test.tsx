import { apiClient } from '../api/apiClient';
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
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

beforeEach(() => {
  vi.clearAllMocks();
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
    fireEvent.click(screen.getByRole('button', { name: 'Direct' }));

    await waitFor(() =>
      expect(apiClient.startOrchestration).toHaveBeenCalledWith('proj-1', 'Make startup faster', null, 'direct'),
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
    fireEvent.click(screen.getByRole('button', { name: 'Define Outcome' }));

    await waitFor(() =>
      expect(apiClient.startOrchestration).toHaveBeenCalledWith('proj-1', 'Ship structured work', 'software-delivery'),
    );
    expect(onStarted).toHaveBeenCalledWith('run-defined');
  });
});
