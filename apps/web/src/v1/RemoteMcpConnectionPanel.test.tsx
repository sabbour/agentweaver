import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { RemoteMcpConnectionPanel } from './RemoteMcpConnectionPanel';

const mocks = vi.hoisted(() => ({ read: vi.fn(), refresh: vi.fn(), disconnect: vi.fn(), prepare: vi.fn(), register: vi.fn(), list: vi.fn() }));
// One stable apiCall, like the real provider, so effects that depend on it run once.
const auth = vi.hoisted(() => ({ apiCall: <T,>(operation: (token: string, tenant: string) => Promise<T>) => operation('token', 'tenant') }));
vi.mock('./AuthContext', () => ({ useAuth: () => auth }));
vi.mock('./api', async (importActual) => ({
  ...(await importActual<typeof import('./api')>()),
  gatewayClient: {
    getRemoteMcpOAuthConnection: mocks.read, refreshRemoteMcpOAuthConnection: mocks.refresh,
    disconnectRemoteMcpOAuthConnection: mocks.disconnect, prepareRemoteMcpOAuthConsent: mocks.prepare,
    registerRemoteMcpOAuthConnection: mocks.register, listRemoteMcpConnections: mocks.list,
  },
}));
const id = '11111111-1111-4111-8111-111111111111';
/** One saved connection as the project directory lists it. */
function entry(connectionId: string, displayName: string, overrides: Record<string, unknown> = {}) {
  return {
    connectionId, displayName, endpointUri: `https://${displayName.toLowerCase()}.example/mcp`, resourceUri: null,
    authenticationMode: 'delegatedOAuth', transportProfile: 'streamableHttp20250618', state: 'enabled',
    rowRevision: 1, configurationRevision: 4, configurationSha256: 'b'.repeat(64), identityBound: false,
    registryServerName: null, registryExactVersion: null, discoveryRevision: 2, catalogSha256: 'c'.repeat(64), toolCount: 3,
    ...overrides,
  };
}
const status = {
  projectId: 'project', connectionId: id, connectionRevision: 2, credentialRevision: 1,
  state: 'Authorized', storedConfigurationRevision: 4, currentConfigurationRevision: 4,
  currentConfigurationMatches: true, credentialUseAvailable: true,
};
beforeEach(() => {
  vi.clearAllMocks();
  mocks.read.mockResolvedValue(status);
  mocks.list.mockResolvedValue({ connections: [], nextConnectionId: null });
  mocks.disconnect.mockResolvedValue({ ...status, connectionRevision: 3, state: 'Disconnected', credentialUseAvailable: false });
});
afterEach(() => { vi.restoreAllMocks(); });

describe('Remote MCP settings connection', () => {
  it('lists saved connections and loads the one the user selects', async () => {
    const other = '22222222-2222-4222-8222-222222222222';
    mocks.list.mockResolvedValue({
      connections: [entry(id, 'Tracker'), entry(other, 'Wiki', { discoveryRevision: null, toolCount: 0 }), entry('33333333-3333-4333-8333-333333333333', 'Gone', { state: 'removed' })],
      nextConnectionId: null,
    });
    render(<RemoteMcpConnectionPanel projectId="project" configurationRevision={4} />);
    expect(await screen.findByText('Tracker')).toBeTruthy();
    expect(mocks.list).toHaveBeenCalledWith('token', 'project', { pageSize: 25 }, 'tenant');
    // Removed connections are not offered, and an unread tool list is not shown as zero tools.
    expect(screen.queryByText('Gone')).toBeNull();
    expect(screen.getByText('Not read yet')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Select Tracker' }));
    await screen.findByText('Authorized');
    expect(mocks.read).toHaveBeenCalledWith('token', id, 'tenant');
    expect((screen.getByLabelText('Remote MCP connection ID') as HTMLInputElement).value).toBe(id);
    fireEvent.click(screen.getByText('Connection setup'));
    expect((screen.getByLabelText('Configuration checksum') as HTMLInputElement).value).toBe('b'.repeat(64));
  });

  it('pages through saved connections', async () => {
    const other = '22222222-2222-4222-8222-222222222222';
    mocks.list
      .mockResolvedValueOnce({ connections: [entry(id, 'Tracker')], nextConnectionId: id })
      .mockResolvedValueOnce({ connections: [entry(other, 'Wiki')], nextConnectionId: null });
    render(<RemoteMcpConnectionPanel projectId="project" configurationRevision={4} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Load more connections' }));
    expect(await screen.findByText('Wiki')).toBeTruthy();
    expect(screen.getByText('Tracker')).toBeTruthy();
    expect(mocks.list).toHaveBeenLastCalledWith('token', 'project', { pageSize: 25, after: id }, 'tenant');
    expect(screen.queryByRole('button', { name: 'Load more connections' })).toBeNull();
  });

  it('shows an empty directory and a list error plainly', async () => {
    render(<RemoteMcpConnectionPanel projectId="project" configurationRevision={4} />);
    expect(await screen.findByText('This project has no saved Remote MCP connections.')).toBeTruthy();
    mocks.list.mockRejectedValueOnce(new Error('Listing failed.'));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh list' }));
    expect(await screen.findByText('Listing failed.')).toBeTruthy();
  });

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
