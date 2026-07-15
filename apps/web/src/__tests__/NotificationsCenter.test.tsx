import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { NotificationBell } from '../components/shell/NotificationBell';
import { NotificationsProvider } from '../notifications/NotificationsProvider';
import { getNotificationsMuted, setNotificationsMuted } from '../notifications/sound';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { NotificationDto, NotificationsResponseDto } from '../api/types';

/**
 * Tests for the #247 global notification center: the polling NotificationsProvider
 * (context/state/toast/chime) and the NotificationBell (badge + popover UI), rendered
 * together the same way App.tsx composes them.
 *
 * Conventions follow PodIndicator.test.tsx / AppShell.test.tsx: vi.mock('../api/apiClient', ...),
 * AzureFluentProvider + MemoryRouter wrapper, cleanup()/afterEach, fake timers for polling.
 */

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getNotifications: vi.fn(),
  },
}));

function makeNotification(overrides?: Partial<NotificationDto>): NotificationDto {
  return {
    id: 'notif-1',
    type: 'human_review',
    run_id: 'run-1',
    project_id: 'proj-1',
    project_name: 'Demo Project',
    agent_name: 'Coordinator',
    title: 'Run "Demo Project" is awaiting your review',
    created_utc: '2026-07-14T00:00:00Z',
    cta_path: '/projects/proj-1/orchestrations/run-1',
    ...overrides,
  };
}

function respond(notifications: NotificationDto[]): NotificationsResponseDto {
  return { generated_utc: '2026-07-14T00:00:00Z', notifications };
}

function renderBell(pollIntervalMs = 1000) {
  return render(
    <AzureFluentProvider density="compact">
      <MemoryRouter>
        <NotificationsProvider pollIntervalMs={pollIntervalMs}>
          <NotificationBell />
        </NotificationsProvider>
      </MemoryRouter>
    </AzureFluentProvider>,
  );
}

beforeEach(() => {
  setNotificationsMuted(false);
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  vi.useRealTimers();
  window.localStorage.clear();
});

describe('NotificationBell + NotificationsProvider', () => {
  it('shows no badge when there are no pending notifications', async () => {
    vi.mocked(apiClient.getNotifications).mockResolvedValue(respond([]));

    renderBell();

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalled());
    expect(screen.queryByTestId('notification-bell-badge')).toBeNull();
  });

  it('shows the backlog count on initial load without spamming a toast', async () => {
    vi.mocked(apiClient.getNotifications).mockResolvedValue(respond([makeNotification()]));

    renderBell();

    await waitFor(() => expect(screen.getByTestId('notification-bell-badge').textContent).toContain('1'));
    // Initial backlog must not produce a toast — only genuinely new arrivals do.
    expect(screen.queryByText('Awaiting your review')).toBeNull();
  });

  it('toasts and increments the badge when a new notification arrives on a later poll', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.mocked(apiClient.getNotifications)
      .mockResolvedValueOnce(respond([]))
      .mockResolvedValueOnce(respond([makeNotification()]));

    renderBell(1000);

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(1));
    expect(screen.queryByTestId('notification-bell-badge')).toBeNull();

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.getByTestId('notification-bell-badge').textContent).toContain('1'));
    expect(await screen.findByText('Awaiting your review')).toBeTruthy();
  });

  it('opening the popover marks all notifications as seen (badge resets)', async () => {
    vi.mocked(apiClient.getNotifications).mockResolvedValue(respond([makeNotification()]));
    const user = userEvent.setup();

    renderBell();

    await waitFor(() => expect(screen.getByTestId('notification-bell-badge').textContent).toContain('1'));

    await user.click(screen.getByTestId('notification-bell'));

    await waitFor(() => expect(screen.queryByTestId('notification-bell-badge')).toBeNull());
    expect(screen.getByText(makeNotification().title)).toBeTruthy();
  });

  it('CTA click in the popover navigates to the notification cta_path', async () => {
    vi.mocked(apiClient.getNotifications).mockResolvedValue(respond([makeNotification()]));
    const user = userEvent.setup();

    renderBell();

    await waitFor(() => expect(screen.getByTestId('notification-bell-badge').textContent).toContain('1'));
    await user.click(screen.getByTestId('notification-bell'));

    const item = await screen.findByText(makeNotification().title);
    await user.click(item);

    // Popover should close after navigating.
    await waitFor(() => expect(screen.queryByText(makeNotification().title)).toBeNull());
  });

  it('mute toggle persists to localStorage', async () => {
    vi.mocked(apiClient.getNotifications).mockResolvedValue(respond([]));
    const user = userEvent.setup();

    renderBell();

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalled());
    await user.click(screen.getByTestId('notification-bell'));

    expect(getNotificationsMuted()).toBe(false);
    await user.click(screen.getByTestId('notification-mute-toggle'));
    await waitFor(() => expect(getNotificationsMuted()).toBe(true));
  });

  it('shows an empty state when there is nothing pending', async () => {
    vi.mocked(apiClient.getNotifications).mockResolvedValue(respond([]));
    const user = userEvent.setup();

    renderBell();

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalled());
    await user.click(screen.getByTestId('notification-bell'));

    expect(await screen.findByText('Nothing needs your attention right now.')).toBeTruthy();
  });

  // #319 — dropdown entries must surface a type badge so users can tell Human Review from
  // Tool Approval (and any future/unknown type) apart at a glance.
  describe('notification type badge', () => {
    it('renders the Human Review badge for a human_review notification', async () => {
      vi.mocked(apiClient.getNotifications).mockResolvedValue(
        respond([makeNotification({ type: 'human_review' })]),
      );
      const user = userEvent.setup();

      renderBell();

      await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalled());
      await user.click(screen.getByTestId('notification-bell'));

      const badge = await screen.findByTestId('notification-type-badge');
      expect(badge.textContent).toContain('Human Review');
      expect(badge.getAttribute('data-notification-type')).toBe('human_review');
    });

    it('renders the Tool Approval badge for a tool_approval notification (backend #321, not yet live)', async () => {
      vi.mocked(apiClient.getNotifications).mockResolvedValue(
        respond([makeNotification({ type: 'tool_approval' })]),
      );
      const user = userEvent.setup();

      renderBell();

      await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalled());
      await user.click(screen.getByTestId('notification-bell'));

      const badge = await screen.findByTestId('notification-type-badge');
      expect(badge.textContent).toContain('Tool Approval');
      expect(badge.getAttribute('data-notification-type')).toBe('tool_approval');
    });

    it('renders a generic fallback badge for an unrecognized type without crashing', async () => {
      vi.mocked(apiClient.getNotifications).mockResolvedValue(
        respond([makeNotification({ type: 'some_future_type' })]),
      );
      const user = userEvent.setup();

      renderBell();

      await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalled());
      await user.click(screen.getByTestId('notification-bell'));

      const badge = await screen.findByTestId('notification-type-badge');
      expect(badge.textContent).toContain('Action Needed');
      expect(badge.getAttribute('data-notification-type')).toBe('some_future_type');
    });
  });
});

