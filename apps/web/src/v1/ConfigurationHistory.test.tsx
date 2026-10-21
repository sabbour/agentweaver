import { act, fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ConfigurationHistory } from './ConfigurationHistory';

const read = vi.hoisted(() => vi.fn());
// apiCall runs the operation right away with fixed token and tenant values.
vi.mock('./AuthContext', () => ({
  useAuth: () => ({ apiCall: <T,>(operation: (token: string, tenant: string) => Promise<T>) => operation('token', 'tenant') }),
}));
vi.mock('./api', () => ({ gatewayClient: { getProjectConfiguration: read } }));
const snapshot = {
  projectId: 'project', revision: 2,
  configuration: {
    modelSelection: { reference: 'historical-model' }, providerOverrides: [], orderedProviderOverrides: [],
    agentCharters: [], casting: [], blueprintWorkflowReferences: [], skills: [], runLimits: {},
  },
};
beforeEach(() => { read.mockReset(); read.mockResolvedValue(snapshot); });

describe('configuration history', () => {
  it('requests an exact revision and presents it read-only', async () => {
    render(<ConfigurationHistory projectId="project" currentRevision={4} />);
    fireEvent.change(screen.getByLabelText('Configuration revision'), { target: { value: '2' } });
    fireEvent.click(screen.getByRole('button', { name: 'View revision' }));
    await screen.findByRole('heading', { name: 'Configuration revision 2' });
    // The revision number must be passed through, and the fields must be read-only with no save button.
    expect(read).toHaveBeenCalledWith('token', 'project', 'tenant', 2);
    expect((screen.getByLabelText('Default model reference') as HTMLInputElement).readOnly).toBe(true);
    expect(screen.queryByRole('button', { name: 'Save changes' })).toBeNull();
  });

  it('rejects revisions outside the saved history without making a request', async () => {
    render(<ConfigurationHistory projectId="project" currentRevision={4} />);
    fireEvent.change(screen.getByLabelText('Configuration revision'), { target: { value: '5' } });
    fireEvent.click(screen.getByRole('button', { name: 'View revision' }));
    await screen.findByText('Enter a revision from 1 to 4.');
    expect(read).not.toHaveBeenCalled();
  });

  it('rejects a snapshot for a different project', async () => {
    read.mockResolvedValue({ ...snapshot, projectId: 'other' });
    render(<ConfigurationHistory projectId="project" currentRevision={2} />);
    fireEvent.click(screen.getByRole('button', { name: 'View revision' }));
    await screen.findByText('The service returned a different project or revision.');
    expect(screen.queryByLabelText('Default model reference')).toBeNull();
  });

  it('ignores a historical response after unmount', async () => {
    // Hold the read open, unmount, then finish it. The late response must not render.
    let complete!: (value: typeof snapshot) => void;
    read.mockReturnValue(new Promise((resolve) => { complete = resolve; }));
    const { unmount } = render(<ConfigurationHistory projectId="project" currentRevision={2} />);
    fireEvent.click(screen.getByRole('button', { name: 'View revision' }));
    unmount();
    await act(async () => { complete(snapshot); });
    expect(screen.queryByLabelText('Default model reference')).toBeNull();
  });
});
