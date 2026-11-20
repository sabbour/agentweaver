import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DeploymentByokBindings } from './DeploymentByokBindings';

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  auth: {
    session: { accessToken: 'platform-token', binding: null as null | { projectId: string } } as
      { accessToken: string; binding: null | { projectId: string } } | null,
    authSessionGeneration: 1,
    authSessionEpoch: 1,
    isActiveSession: (session: unknown) => mocks.auth.session === session,
    ensureSession: vi.fn(),
    authorize: vi.fn(),
    apiCall: (operation: (token: string) => unknown) => operation(mocks.auth.session!.accessToken),
  },
}));
vi.mock('./AuthContext', () => ({ useAuth: () => mocks.auth }));
vi.mock('./api', () => ({ gatewayClient: { getByokDeploymentBindings: mocks.get } }));

beforeEach(() => {
  vi.clearAllMocks();
  mocks.auth.session = { accessToken: 'platform-token', binding: null };
  mocks.auth.authSessionGeneration = 1;
  mocks.auth.authSessionEpoch = 1;
});

describe('Deployment-managed BYOK bindings', () => {
  it('shows the startup copy without edit controls', async () => {
    mocks.get.mockResolvedValue({
      revision: 'r1',
      bindings: [
        { reference: 'first', enabled: true, reasoningEfforts: ['high'] },
        { reference: 'second', enabled: false, reasoningEfforts: [] },
      ],
    });
    render(<DeploymentByokBindings />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), { target: { value: 'tenant-a' } });
    fireEvent.click(screen.getByRole('button', { name: 'Load bindings' }));
    const list = await screen.findByRole('list', { name: 'Deployment-managed BYOK bindings' });
    expect(within(list).getByText(/first · Managed by deployment · Enabled · high/)).toBeTruthy();
    expect(within(list).getByText(/second · Managed by deployment · Disabled/)).toBeTruthy();
    expect(screen.getByText('Projects startup revision: r1')).toBeTruthy();
    expect(mocks.get).toHaveBeenCalledWith('platform-token', 'tenant-a');
    expect(screen.queryByRole('button', { name: /edit|delete|activate/i })).toBeNull();
  });

  it('drops stale replies when the tenant or active session changes', async () => {
    let resolve!: (value: unknown) => void;
    mocks.get.mockReturnValue(new Promise((done) => { resolve = done; }));
    const { rerender } = render(<DeploymentByokBindings />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), { target: { value: 'tenant-a' } });
    fireEvent.click(screen.getByRole('button', { name: 'Load bindings' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), { target: { value: 'tenant-b' } });
    await act(async () => resolve({ revision: 'old', bindings: [{ reference: 'old', enabled: true, reasoningEfforts: [] }] }));
    expect(screen.queryByText(/old · Managed/)).toBeNull();
    mocks.get.mockResolvedValue({ revision: 'new', bindings: [] });
    fireEvent.click(screen.getByRole('button', { name: 'Load bindings' }));
    await screen.findByText('No BYOK bindings are in this startup copy.');
    mocks.auth.session = { accessToken: 'other', binding: { projectId: 'p' } };
    mocks.auth.authSessionGeneration += 1;
    mocks.auth.authSessionEpoch += 1;
    rerender(<DeploymentByokBindings />);
    expect(screen.queryByText('No BYOK bindings are in this startup copy.')).toBeNull();
    expect(screen.getByText('Use a platform sign-in to read deployment bindings.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Load bindings' })).toHaveProperty('disabled', true);
  });

  it('restarts an in-flight load after token refresh and ignores its old reply', async () => {
    let resolveOld!: (value: unknown) => void;
    let resolveNew!: (value: unknown) => void;
    mocks.get.mockReturnValueOnce(new Promise((done) => { resolveOld = done; }))
      .mockReturnValueOnce(new Promise((done) => { resolveNew = done; }));
    const { rerender } = render(<DeploymentByokBindings />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), { target: { value: 'tenant-a' } });
    fireEvent.click(screen.getByRole('button', { name: 'Load bindings' }));
    mocks.auth.session = { accessToken: 'refreshed', binding: null };
    mocks.auth.authSessionGeneration += 1;
    rerender(<DeploymentByokBindings />);
    await waitFor(() => expect(mocks.get).toHaveBeenCalledWith('refreshed', 'tenant-a'));
    await act(async () => resolveOld({ revision: 'old', bindings: [] }));
    expect(screen.queryByText('Projects startup revision: old')).toBeNull();
    await act(async () => resolveNew({ revision: 'new', bindings: [] }));
    expect(screen.getByText('Projects startup revision: new')).toBeTruthy();
  });

  it('restarts after an old reply arrives before the refreshed session renders', async () => {
    let resolveOld!: (value: unknown) => void;
    let resolveNew!: (value: unknown) => void;
    mocks.get.mockReturnValueOnce(new Promise((done) => { resolveOld = done; }))
      .mockReturnValueOnce(new Promise((done) => { resolveNew = done; }));
    const { rerender } = render(<DeploymentByokBindings />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), { target: { value: 'tenant-a' } });
    fireEvent.click(screen.getByRole('button', { name: 'Load bindings' }));
    expect(mocks.get).toHaveBeenCalledWith('platform-token', 'tenant-a');

    mocks.auth.session = { accessToken: 'refreshed', binding: null };
    mocks.auth.authSessionGeneration += 1;
    await act(async () => resolveOld({ revision: 'old', bindings: [] }));
    expect(screen.queryByText('Projects startup revision: old')).toBeNull();
    rerender(<DeploymentByokBindings />);
    await waitFor(() => expect(mocks.get.mock.calls).toEqual([
      ['platform-token', 'tenant-a'],
      ['refreshed', 'tenant-a'],
    ]));
    await act(async () => resolveNew({ revision: 'new', bindings: [] }));
    expect(screen.getByText('Projects startup revision: new')).toBeTruthy();
  });

  it('stops and shows an error when the session refreshes again during the restart', async () => {
    mocks.get.mockReturnValue(new Promise(() => {}));
    const { rerender } = render(<DeploymentByokBindings />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), { target: { value: 'tenant-a' } });
    fireEvent.click(screen.getByRole('button', { name: 'Load bindings' }));
    mocks.auth.session = { accessToken: 'refreshed-1', binding: null };
    mocks.auth.authSessionGeneration += 1;
    rerender(<DeploymentByokBindings />);
    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(2));
    mocks.auth.session = { accessToken: 'refreshed-2', binding: null };
    mocks.auth.authSessionGeneration += 1;
    rerender(<DeploymentByokBindings />);
    expect(await screen.findByText(/could not load/)).toBeTruthy();
    expect(mocks.get).toHaveBeenCalledTimes(2);
  });

  it.each(['replacement', 'same-token replacement', 'logout'])('rejects an in-flight reply after %s', async (change) => {
    let resolve!: (value: unknown) => void;
    mocks.get.mockReturnValue(new Promise((done) => { resolve = done; }));
    const { rerender } = render(<DeploymentByokBindings />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), { target: { value: 'tenant-a' } });
    fireEvent.click(screen.getByRole('button', { name: 'Load bindings' }));
    const newToken = change === 'same-token replacement' ? 'platform-token' : 'other';
    mocks.auth.session = change === 'logout' ? null : { accessToken: newToken, binding: null };
    if (change !== 'same-token replacement') mocks.auth.authSessionGeneration += 1;
    mocks.auth.authSessionEpoch += 1;
    await act(async () => resolve({ revision: 'old', bindings: [] }));
    expect(screen.queryByText('Projects startup revision: old')).toBeNull();
    rerender(<DeploymentByokBindings />);
    expect(mocks.get).toHaveBeenCalledTimes(1);
    if (change !== 'logout') {
      mocks.get.mockResolvedValueOnce({ revision: 'replacement', bindings: [] });
      fireEvent.click(screen.getByRole('button', { name: 'Load bindings' }));
      await screen.findByText('Projects startup revision: replacement');
      expect(mocks.get).toHaveBeenLastCalledWith(newToken, 'tenant-a');
    }
  });

  it('clears a previous result when a refresh fails', async () => {
    mocks.get.mockResolvedValueOnce({ revision: 'r1', bindings: [] }).mockRejectedValueOnce(new Error('denied'));
    render(<DeploymentByokBindings />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), { target: { value: 'tenant-a' } });
    fireEvent.click(screen.getByRole('button', { name: 'Load bindings' }));
    await screen.findByText('No BYOK bindings are in this startup copy.');
    fireEvent.click(screen.getByRole('button', { name: 'Load bindings' }));
    await waitFor(() => expect(screen.getByText(/could not load/)).toBeTruthy());
    expect(screen.queryByText('No BYOK bindings are in this startup copy.')).toBeNull();
  });
});
