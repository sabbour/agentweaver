import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { useEffect } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import packageInfo from '../../package.json';
import { BrokerIdentity, RetainedShell } from './RetainedShell';
import { useSharedRunAttention } from './RunAttentionContext';
import type { RunAttention } from './runAttention';

const packageVersion = { display: packageInfo.version, gitSha: null };

const mocks = vi.hoisted(() => ({
  apiCall: vi.fn(), useRunAttention: vi.fn(), instances: 0, listProjects: vi.fn(), getAuthorizationContext: vi.fn(),
  session: { accessToken: 'token', displayName: null as string | null },
}));
const signOut = vi.fn();
vi.mock('./AuthContext', () => ({ useAuth: () => ({ signOut, session: mocks.session, apiCall: mocks.apiCall }) }));
vi.mock('./api', async (importOriginal) => ({
  ...await importOriginal<typeof import('./api')>(),
  gatewayClient: { listProjects: mocks.listProjects, getAuthorizationContext: mocks.getAuthorizationContext },
}));
vi.mock('./runAttention', async (importOriginal) => ({
  ...await importOriginal<typeof import('./runAttention')>(),
  // Counts hook instances: each mounted instance would run its own poll.
  useRunAttention: () => {
    useEffect(() => { mocks.instances += 1; }, []);
    return mocks.useRunAttention();
  },
}));

const attention: RunAttention = {
  items: [], count: 0, loading: false, error: null, reload: vi.fn(), dismiss: vi.fn(), dismissError: null,
};

beforeEach(() => {
  mocks.instances = 0;
  mocks.session.displayName = null;
  mocks.useRunAttention.mockReset().mockReturnValue(attention);
  // The shell loads the project list for the switcher, and Start task reads the caller's access.
  mocks.apiCall.mockReset().mockImplementation((operation: (token: string, tenant: string) => Promise<unknown>) => operation('token', 'tenant-1'));
  mocks.listProjects.mockReset().mockResolvedValue([
    { projectId: 'p1', name: 'Alpha', state: 'active', revision: 1, configurationRevision: 1, createdAt: '', updatedAt: '' },
  ]);
  mocks.getAuthorizationContext.mockReset().mockResolvedValue(authorization(['readProjects', 'writeProjects']));
});

function authorization(permissions: string[]) {
  return {
    contractVersion: 1, issuer: 'https://broker.test', actorId: 'user-1', tenantId: 'tenant-1', membershipRevision: 1,
    boundProjectId: null, boundRunId: null,
    effectiveAuthority: [{ resourceType: 'project', resourceId: 'p1', permissions: permissions.map((permission) => ({ permission, roleRevision: 1 })) }],
  };
}

/** A page that reads the shared notifications state, like the Overview list. */
function AttentionReader({ onRead }: { onRead: (value: RunAttention) => void }) {
  onRead(useSharedRunAttention());
  return <p>Page</p>;
}

function renderShell(path: string, children = <p>Page</p>) {
  return render(<MemoryRouter initialEntries={[path]}><RetainedShell>{children}</RetainedShell></MemoryRouter>);
}

describe('BrokerIdentity', () => {
  it('keeps the 0.x persona row and menu footer', async () => {
    render(<BrokerIdentity collapsed={false} version={packageVersion} />);

    const trigger = screen.getByRole('button', { name: 'Account' });
    expect(within(trigger).getByText('Account')).toBeDefined();
    // Expanded rail: avatar and label sit at the left, like the older persona row.
    expect(getComputedStyle(trigger).justifyContent).toBe('flex-start');

    // The menu footer shows the app version and a working Sign out button.
    fireEvent.click(trigger);
    const footer = await screen.findByTestId('identity-menu-footer');
    expect(within(footer).getByText(`v${packageInfo.version}`)).toBeDefined();
    fireEvent.click(within(footer).getByRole('button', { name: 'Sign out' }));
    expect(signOut).toHaveBeenCalledTimes(1);
  });

  it('shows only the avatar when the rail is collapsed', () => {
    render(<BrokerIdentity collapsed version={packageVersion} />);

    const trigger = screen.getByRole('button', { name: 'Account' });
    expect(within(trigger).queryByText('Account')).toBeNull();
    expect(getComputedStyle(trigger).justifyContent).toBe('center');
  });

  it('shows the display name from the sign-in session instead of "Account"', async () => {
    mocks.session.displayName = 'Ada Lovelace';
    render(<BrokerIdentity collapsed={false} version={packageVersion} />);

    const trigger = screen.getByRole('button', { name: 'Account: Ada Lovelace' });
    expect(within(trigger).getByText('Ada Lovelace')).toBeDefined();
    expect(within(trigger).queryByText('Account')).toBeNull();
    fireEvent.click(trigger);
    expect(await screen.findByText('Signed in as Ada Lovelace')).toBeDefined();
  });

  it('shows the release commit in the badge tooltip', async () => {
    render(<BrokerIdentity collapsed={false} version={{ display: '1.5.0', gitSha: 'abcdef0123456789' }} />);

    fireEvent.click(screen.getByRole('button', { name: 'Account' }));
    const footer = await screen.findByTestId('identity-menu-footer');
    const badgeText = within(footer).getByText('v1.5.0');
    expect(badgeText.closest('.aw-identity-menu-footer__version')?.getAttribute('title'))
      .toContain('Full version: v1.5.0 (build abcdef0123456789)');
  });
});

describe('RetainedShell version', () => {
  it('replaces the bundled version with the version the web host reports', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({ version: '9.8.7', gitSha: 'abc1234', isRelease: false }),
    } as Response);
    try {
      renderShell('/projects');

      fireEvent.click(await screen.findByRole('button', { name: 'Account' }));
      const footer = await screen.findByTestId('identity-menu-footer');
      expect(await within(footer).findByText('v9.8.7-dev+abc1234')).toBeDefined();
      expect(fetchSpy).toHaveBeenCalledWith('/api/version', { cache: 'no-store' });
    } finally {
      fetchSpy.mockRestore();
    }
  });
});

describe('RetainedShell', () => {
  it('reads notifications once and shares them with the page', async () => {
    const onRead = vi.fn();
    renderShell('/overview', <AttentionReader onRead={onRead} />);
    await screen.findByText('Page');
    await waitFor(() => expect(mocks.apiCall).toHaveBeenCalled());
    // One hook instance means one poll for the whole shell. The page gets the
    // same state the bell gets.
    expect(mocks.instances).toBe(1);
    expect(onRead).toHaveBeenLastCalledWith(attention);
  });

  it('highlights New session and shows the project from the address on /assistant', async () => {
    renderShell('/assistant?project=p1');
    await waitFor(() => expect((screen.getByRole('combobox', { name: 'Project switcher' }) as HTMLInputElement).value).toBe('Alpha'));
    expect(screen.getByRole('button', { name: 'New session' }).getAttribute('aria-current')).toBe('page');
    expect(screen.getByRole('link', { name: 'Overview' }).getAttribute('aria-current')).toBeNull();
  });

  it('highlights Settings, not Overview, on /platform-settings', async () => {
    renderShell('/platform-settings');
    expect(screen.getByRole('button', { name: 'Settings' }).getAttribute('aria-current')).toBe('page');
    expect(screen.getByRole('link', { name: 'Overview' }).getAttribute('aria-current')).toBeNull();
    await waitFor(() => expect(mocks.apiCall).toHaveBeenCalled());
  });

  it('shows Board in the project rail and Start task for users who can change the project', async () => {
    renderShell('/projects/p1');
    expect((await screen.findByRole('link', { name: 'Board' })).getAttribute('href')).toBe('/projects/p1/board');
    expect(await screen.findByRole('button', { name: 'Start task' })).toBeTruthy();
  });

  it('hides Start task from users who can only view the project', async () => {
    mocks.getAuthorizationContext.mockResolvedValue(authorization(['readProjects']));
    renderShell('/projects/p1');
    await waitFor(() => expect(mocks.getAuthorizationContext).toHaveBeenCalled());
    await screen.findByRole('link', { name: 'Board' });
    expect(screen.queryByRole('button', { name: 'Start task' })).toBeNull();
  });

  it('hides Start task outside a project', async () => {
    renderShell('/overview');
    await waitFor(() => expect(mocks.listProjects).toHaveBeenCalled());
    expect(screen.queryByRole('button', { name: 'Start task' })).toBeNull();
  });
});
