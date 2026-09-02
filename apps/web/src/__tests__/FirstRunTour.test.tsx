import { AzureFluentProvider } from '../copilot-fluent-system';
import { FirstRunTour } from '../components/onboarding/FirstRunTour';
import {
  firstRunTourStorageKey,
  hasCompletedFirstRunTour,
  markFirstRunTourComplete,
} from '../components/onboarding/firstRunTourStorage';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useRef } from 'react';

function TourHarness({
  open = true,
  onDismiss = () => {},
}: {
  open?: boolean;
  onDismiss?: () => void;
}) {
  const projects = useRef<HTMLAnchorElement>(null);
  const sessions = useRef<HTMLAnchorElement>(null);
  const startTask = useRef<HTMLButtonElement>(null);
  const returnFocusTarget = useRef<HTMLButtonElement>(null);

  return (
    <AzureFluentProvider density="compact">
      <a ref={projects} href="/projects">Projects</a>
      <a ref={sessions} href="/sessions">Sessions</a>
      <button ref={startTask} type="button">Start task</button>
      <button ref={returnFocusTarget} type="button">Settings</button>
      <FirstRunTour
        open={open}
        targets={{ projects, sessions, startTask }}
        returnFocusTarget={returnFocusTarget}
        onDismiss={onDismiss}
      />
    </AzureFluentProvider>
  );
}

afterEach(() => {
  cleanup();
  localStorage.clear();
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1024 });
});

describe('FirstRunTour', () => {
  it('moves through the three product steps', async () => {
    const onDismiss = vi.fn();
    render(<TourHarness onDismiss={onDismiss} />);

    expect(await screen.findByRole('dialog')).toBeDefined();
    expect(screen.getByText('Step 1 of 3')).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Create a project' })).toBeDefined();

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByRole('heading', { name: 'Use Sessions' })).toBeDefined();
    expect(screen.getByText('Step 2 of 3')).toBeDefined();

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByRole('heading', { name: 'Start a task' })).toBeDefined();
    expect(screen.getByText('Step 3 of 3')).toBeDefined();

    fireEvent.click(screen.getByRole('button', { name: 'Finish' }));
    expect(onDismiss).toHaveBeenCalledTimes(1);
  });

  it('dismisses with Escape', async () => {
    const onDismiss = vi.fn();
    render(<TourHarness onDismiss={onDismiss} />);

    await screen.findByRole('dialog');
    fireEvent.keyDown(window, { key: 'Escape' });

    expect(onDismiss).toHaveBeenCalledTimes(1);
  });

  it('returns focus to the settings button after dismissal', async () => {
    const onDismiss = vi.fn();
    render(<TourHarness onDismiss={onDismiss} />);

    await screen.findByRole('dialog');
    fireEvent.click(screen.getByRole('button', { name: 'Skip tour' }));
    await Promise.resolve();

    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Settings' }));
  });

  it('uses a bottom layout on narrow screens', async () => {
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 480 });
    render(<TourHarness />);

    const dialog = await screen.findByRole('dialog');
    fireEvent(window, new Event('resize'));

    await waitFor(() => expect(dialog.style.bottom).toBe('16px'));
    expect(dialog.style.left).toBe('16px');
    expect(dialog.style.right).toBe('16px');
  });

  it('stores completion by normalized user and tour version', () => {
    const storageKey = firstRunTourStorageKey(' Admin@Example.COM ');
    expect(hasCompletedFirstRunTour(storageKey)).toBe(false);

    markFirstRunTourComplete(storageKey);

    expect(storageKey).toContain('admin%40example.com');
    expect(hasCompletedFirstRunTour(storageKey)).toBe(true);
  });
});
