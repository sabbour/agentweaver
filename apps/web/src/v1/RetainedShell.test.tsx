import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { useEffect } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import packageInfo from '../../package.json';
import { BrokerIdentity, RetainedShell } from './RetainedShell';
import { useSharedRunAttention } from './RunAttentionContext';
import type { RunAttention } from './runAttention';

const mocks = vi.hoisted(() => ({ apiCall: vi.fn(), useRunAttention: vi.fn(), instances: 0 }));
const signOut = vi.fn();
vi.mock('./AuthContext', () => ({ useAuth: () => ({ signOut, session: { accessToken: 'token' }, apiCall: mocks.apiCall }) }));
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
  mocks.useRunAttention.mockReset().mockReturnValue(attention);
  // The shell loads the project list for the switcher.
  mocks.apiCall.mockReset().mockResolvedValue([
    { projectId: 'p1', name: 'Alpha', state: 'active', revision: 1, configurationRevision: 1, createdAt: '', updatedAt: '' },
  ]);
});

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
    render(<BrokerIdentity collapsed={false} />);

    const trigger = screen.getByRole('button', { name: 'Account' });
    expect(within(trigger).getByText('Account')).toBeDefined();
    expect(getComputedStyle(trigger).justifyContent).toBe('flex-start');

    fireEvent.click(trigger);
    const footer = await screen.findByTestId('identity-menu-footer');
    expect(within(footer).getByText(`v${packageInfo.version}`)).toBeDefined();
    fireEvent.click(within(footer).getByRole('button', { name: 'Sign out' }));
    expect(signOut).toHaveBeenCalledTimes(1);
  });

  it('shows only the avatar when the rail is collapsed', () => {
    render(<BrokerIdentity collapsed />);

    const trigger = screen.getByRole('button', { name: 'Account' });
    expect(within(trigger).queryByText('Account')).toBeNull();
    expect(getComputedStyle(trigger).justifyContent).toBe('center');
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
});
