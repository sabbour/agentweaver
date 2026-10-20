import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { RemoteMcpConnectionPanel } from './RemoteMcpConnectionPanel';

const mocks = vi.hoisted(() => ({ read: vi.fn(), refresh: vi.fn(), disconnect: vi.fn(), prepare: vi.fn(), register: vi.fn() }));
vi.mock('./AuthContext', () => ({
  useAuth: () => ({ apiCall: <T,>(operation: (token: string, tenant: string) => Promise<T>) => operation('token', 'tenant') }),
}));
vi.mock('./api', () => ({
  gatewayClient: {
    getRemoteMcpOAuthConnection: mocks.read, refreshRemoteMcpOAuthConnection: mocks.refresh,
    disconnectRemoteMcpOAuthConnection: mocks.disconnect, prepareRemoteMcpOAuthConsent: mocks.prepare,
    registerRemoteMcpOAuthConnection: mocks.register,
  },
}));
const id = '11111111-1111-4111-8111-111111111111';
const status = {
  projectId: 'project', connectionId: id, connectionRevision: 2, credentialRevision: 1,
  state: 'Authorized', storedConfigurationRevision: 4, currentConfigurationRevision: 4,
  currentConfigurationMatches: true, credentialUseAvailable: true,
};
beforeEach(() => {
  vi.clearAllMocks();
  mocks.read.mockResolvedValue(status);
  mocks.disconnect.mockResolvedValue({ ...status, connectionRevision: 3, state: 'Disconnected', credentialUseAvailable: false });
});
afterEach(() => { vi.restoreAllMocks(); });

describe('Remote MCP settings connection', () => {
  /** Loads owner status and supplies the saved setup digest before starting consent. */
  async function prepareAuthorization() {
    render(<RemoteMcpConnectionPanel projectId="project" configurationRevision={4} />);
    fireEvent.change(screen.getByLabelText('Remote MCP connection ID'), { target: { value: id } });
    fireEvent.click(screen.getByRole('button', { name: 'Load connection' }));
    await screen.findByText('Authorized');
    fireEvent.click(screen.getByText('Connection setup'));
    fireEvent.change(screen.getByLabelText('Configuration checksum'), { target: { value: 'a'.repeat(64) } });
  }

  it('loads an exact connection and disconnects using owner revision fences', async () => {
    render(<RemoteMcpConnectionPanel projectId="project" configurationRevision={4} />);
    fireEvent.change(screen.getByLabelText('Remote MCP connection ID'), { target: { value: id } });
    fireEvent.click(screen.getByRole('button', { name: 'Load connection' }));
    await screen.findByText('Authorized');
    expect(mocks.read).toHaveBeenCalledWith('token', id, 'tenant');
    fireEvent.click(screen.getByRole('button', { name: 'Disconnect tool service' }));
    await screen.findByText('Disconnected');
    expect(mocks.disconnect).toHaveBeenCalledWith('token', id, expect.objectContaining({
      expectedConnectionRevision: 2, expectedCredentialRevision: 1, expectedConfigurationRevision: 4,
      idempotencyKey: expect.any(String),
    }), 'tenant');
  });

  it('rejects a connection returned for another project', async () => {
    mocks.read.mockResolvedValue({ ...status, projectId: 'other' });
    render(<RemoteMcpConnectionPanel projectId="project" configurationRevision={4} />);
    fireEvent.change(screen.getByLabelText('Remote MCP connection ID'), { target: { value: id } });
    fireEvent.click(screen.getByRole('button', { name: 'Load connection' }));
    await screen.findByText('The service returned a different or invalid connection.');
    expect(screen.queryByText('Authorized')).toBeNull();
  });

  it('does not authorize or refresh a connection for an outdated configuration', async () => {
    mocks.read.mockResolvedValue({ ...status, currentConfigurationMatches: false });
    render(<RemoteMcpConnectionPanel projectId="project" configurationRevision={4} />);
    fireEvent.change(screen.getByLabelText('Remote MCP connection ID'), { target: { value: id } });
    fireEvent.click(screen.getByRole('button', { name: 'Load connection' }));
    await screen.findByText('Authorized');
    expect((screen.getByRole('button', { name: 'Refresh connection' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByText('Connection setup'));
    expect((screen.getByRole('button', { name: 'Authorize tool service' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('ignores a connection response after the panel unmounts', async () => {
    let resolve!: (value: typeof status) => void;
    mocks.read.mockReturnValue(new Promise((complete) => { resolve = complete; }));
    const { unmount } = render(<RemoteMcpConnectionPanel projectId="project" configurationRevision={4} />);
    fireEvent.change(screen.getByLabelText('Remote MCP connection ID'), { target: { value: id } });
    fireEvent.click(screen.getByRole('button', { name: 'Load connection' }));
    await waitFor(() => expect(mocks.read).toHaveBeenCalledOnce());
    unmount();
    await act(async () => { resolve(status); });
    expect(screen.queryByText('Authorized')).toBeNull();
  });

  it('reports a blocked popup without preparing consent', async () => {
    vi.spyOn(window, 'open').mockReturnValue(null);
    await prepareAuthorization();
    fireEvent.click(screen.getByRole('button', { name: 'Authorize tool service' }));
    await screen.findByText('The sign-in window was blocked. Allow popups and try again.');
    expect(mocks.prepare).not.toHaveBeenCalled();
  });

  it('rejects a non-HTTPS sign-in address and closes its popup', async () => {
    const close = vi.fn();
    const replace = vi.fn();
    const popup = { closed: false, close, location: { replace } };
    vi.spyOn(window, 'open').mockReturnValue(popup as unknown as Window);
    mocks.prepare.mockResolvedValue({
      correlationId: id, state: 's'.repeat(43), pkceChallenge: 'p'.repeat(43),
      authorizationUri: 'http://tool.example/authorize', expiresAt: '2030-01-01T00:00:00Z',
      connectionRevision: 2, configurationRevision: 4, configurationSha256: 'a'.repeat(64),
    });
    await prepareAuthorization();
    fireEvent.click(screen.getByRole('button', { name: 'Authorize tool service' }));
    await screen.findByText('The sign-in address is invalid or its window was closed. Try again.');
    expect(replace).not.toHaveBeenCalled();
    expect(close).toHaveBeenCalled();
  });

  it('submits registration metadata without collecting a credential', async () => {
    mocks.register.mockResolvedValue(status);
    render(<RemoteMcpConnectionPanel projectId="project" configurationRevision={4} />);
    fireEvent.change(screen.getByLabelText('Remote MCP connection ID'), { target: { value: id } });
    fireEvent.click(screen.getByText('Connection setup'));
    fireEvent.change(screen.getByLabelText('Configuration checksum'), { target: { value: 'a'.repeat(64) } });
    fireEvent.change(screen.getByLabelText('Sign-in service address'), { target: { value: 'https://tool.example' } });
    fireEvent.change(screen.getByLabelText('Requested tool permissions'), { target: { value: 'read write' } });
    fireEvent.click(screen.getByRole('button', { name: 'Register connection' }));
    await screen.findByText('Authorized');
    expect(mocks.register).toHaveBeenCalledWith('token', {
      projectId: 'project', connectionId: id, expectedConfigurationRevision: 4,
      expectedConfigurationSha256: 'a'.repeat(64), issuerUri: 'https://tool.example', scopes: ['read', 'write'],
    }, 'tenant');
  });
});
