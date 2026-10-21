import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import packageInfo from '../../package.json';
import { BrokerIdentity } from './RetainedShell';

const signOut = vi.fn();
vi.mock('./AuthContext', () => ({ useAuth: () => ({ signOut }) }));

describe('BrokerIdentity', () => {
  it('keeps the 0.x persona row and menu footer', async () => {
    render(<BrokerIdentity collapsed={false} />);

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
    render(<BrokerIdentity collapsed />);

    const trigger = screen.getByRole('button', { name: 'Account' });
    // The label is hidden and the avatar is centered in the narrow rail.
    expect(within(trigger).queryByText('Account')).toBeNull();
    expect(getComputedStyle(trigger).justifyContent).toBe('center');
  });
});
