import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { NotificationBell } from '../components/shell/NotificationBell';
import { NotificationsProvider } from '../notifications/NotificationsProvider';
import { getNotificationsMuted, setNotificationsMuted } from '../notifications/sound';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router-dom';
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
    dismissNotification: vi.fn(),
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
          <CurrentLocation />
        </NotificationsProvider>
      </MemoryRouter>
    </AzureFluentProvider>,
  );
}

function CurrentLocation() {
  const location = useLocation();
  return <span data-testid="current-location">{location.pathname}</span>;
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
    vi.mocked(apiClient.getNotifications)
      .mockResolvedValueOnce(respond([makeNotification()]))
      .mockResolvedValueOnce(respond([]));

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

  it('dismisses a permanent approval toast when its source disappears on a poll', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const approval = makeNotification({
      type: 'tool_approval',
      title: 'Approval needed to run "start_preview"',
    });
    vi.mocked(apiClient.getNotifications)
      .mockResolvedValueOnce(respond([]))
      .mockResolvedValueOnce(respond([approval]))
      .mockResolvedValueOnce(respond([]));

    renderBell(1000);

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(1));
    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });
    expect(await screen.findByText('Review now')).toBeTruthy();

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(3));
    await waitFor(() => expect(screen.queryByText('Review now')).toBeNull());
    expect(screen.queryByTestId('notification-bell-badge')).toBeNull();
  });

  it('invalidates a permanent approval toast when its source target changes on a poll', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const approval = makeNotification({
      id: 'notif-changed-approval',
      type: 'tool_approval',
      run_id: 'original-run',
      title: 'Approval needed to run "start_preview"',
    });
    const changedApproval = { ...approval, run_id: 'replacement-run' };
    vi.mocked(apiClient.getNotifications)
      .mockResolvedValueOnce(respond([]))
      .mockResolvedValueOnce(respond([approval]))
      .mockResolvedValueOnce(respond([changedApproval]));

    renderBell(1000);

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(1));
    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });
    expect(await screen.findByText('Review now')).toBeTruthy();

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(3));
    await waitFor(() => expect(screen.queryByText('Review now')).toBeNull());
    expect(screen.getByTestId('notification-bell').getAttribute('aria-label'))
      .toContain('1 pending tool approval');
  });

  it('shows the unavailable message instead of navigating when an approval disappears before its toast CTA is used', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const approval = makeNotification({
      type: 'tool_approval',
      title: 'Approval needed to run "start_preview"',
    });
    vi.mocked(apiClient.getNotifications)
      .mockResolvedValueOnce(respond([]))
      .mockResolvedValueOnce(respond([approval]))
      // The CTA verifies server truth instead of trusting the stale toast snapshot.
      .mockResolvedValueOnce(respond([]));
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

    renderBell(1000);

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(1));
    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });
    await user.click(await screen.findByText('Review now'));

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(3));
    expect(screen.getByTestId('current-location').textContent).toBe('/');
    expect(await screen.findByText('This approval no longer has a run to review.')).toBeTruthy();
  });

  it('does not navigate when a later poll removes an approval while its CTA validation is pending', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const approval = makeNotification({
      type: 'tool_approval',
      title: 'Approval needed to run "start_preview"',
    });
    let resolveValidation!: (response: NotificationsResponseDto) => void;
    const validationResponse = new Promise<NotificationsResponseDto>((resolve) => {
      resolveValidation = resolve;
    });
    vi.mocked(apiClient.getNotifications)
      .mockResolvedValueOnce(respond([]))
      .mockResolvedValueOnce(respond([approval]))
      .mockImplementationOnce(() => validationResponse)
      .mockResolvedValueOnce(respond([]));
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

    renderBell(1000);

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(1));
    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });
    await user.click(await screen.findByText('Review now'));
    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(3));

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });
    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(4));
    await waitFor(() => expect(screen.queryByText('Review now')).toBeNull());

    await act(async () => {
      resolveValidation(respond([approval]));
      await Promise.resolve();
    });

    expect(screen.getByTestId('current-location').textContent).toBe('/');
    expect(await screen.findByText('This approval no longer has a run to review.')).toBeTruthy();
  });

  it('blocks a pending CTA after its approval is dismissed from the bell and a later poll omits it', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const approval = makeNotification({
      type: 'tool_approval',
      title: 'Approval needed to run "start_preview"',
    });
    let resolveValidation!: (response: NotificationsResponseDto) => void;
    const validationResponse = new Promise<NotificationsResponseDto>((resolve) => {
      resolveValidation = resolve;
    });
    vi.mocked(apiClient.getNotifications)
      .mockResolvedValueOnce(respond([]))
      .mockResolvedValueOnce(respond([approval]))
      .mockImplementationOnce(() => validationResponse)
      .mockResolvedValueOnce(respond([]));
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

    renderBell(1000);

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(1));
    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });
    await user.click(await screen.findByText('Review now'));
    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(3));

    await user.click(screen.getByTestId('notification-bell'));
    await user.click(await screen.findByRole('button', {
      name: `Dismiss notification: ${approval.title}`,
    }));

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });
    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(4));

    await act(async () => {
      resolveValidation(respond([approval]));
      await Promise.resolve();
    });

    expect(screen.getByTestId('current-location').textContent).toBe('/');
    expect(await screen.findByText('This approval no longer has a run to review.')).toBeTruthy();
  });

  it('keeps two approval CTAs independent while both validations are pending', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const firstApproval = makeNotification({
      id: 'notif-first-approval',
      type: 'tool_approval',
      run_id: 'first-approval-run',
      title: 'Approval needed for first run',
    });
    const secondApproval = makeNotification({
      id: 'notif-second-approval',
      type: 'tool_approval',
      run_id: 'second-approval-run',
      title: 'Approval needed for second run',
    });
    let resolveFirstValidation!: (response: NotificationsResponseDto) => void;
    const firstValidation = new Promise<NotificationsResponseDto>((resolve) => {
      resolveFirstValidation = resolve;
    });
    const secondValidation = new Promise<NotificationsResponseDto>(() => {});
    vi.mocked(apiClient.getNotifications)
      .mockResolvedValueOnce(respond([]))
      .mockResolvedValueOnce(respond([firstApproval, secondApproval]))
      .mockImplementationOnce(() => firstValidation)
      .mockImplementationOnce(() => secondValidation);
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

    renderBell(100_000);

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(1));
    await act(async () => {
      vi.advanceTimersByTime(100_000);
      await Promise.resolve();
    });
    const firstToast = (await screen.findByText(firstApproval.title)).closest('[role="listitem"]');
    const secondToast = (await screen.findByText(secondApproval.title)).closest('[role="listitem"]');
    if (!(firstToast instanceof HTMLElement) || !(secondToast instanceof HTMLElement)) {
      throw new Error('Expected approval toasts to be HTML elements.');
    }
    await user.click(within(firstToast).getByText('Review now'));
    await user.click(within(secondToast).getByText('Review now'));
    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(4));

    await act(async () => {
      resolveFirstValidation(respond([firstApproval, secondApproval]));
      await Promise.resolve();
    });
    await waitFor(() => expect(screen.getByTestId('current-location').textContent)
      .toBe('/projects/proj-1/orchestrations/first-approval-run'));

    expect(screen.queryByText('This approval no longer has a run to review.')).toBeNull();
  });

  it('navigates when a newer accepted poll confirms a pending CTA source is still active', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const approval = makeNotification({
      type: 'tool_approval',
      title: 'Approval needed to run "start_preview"',
    });
    let resolveValidation!: (response: NotificationsResponseDto) => void;
    let resolvePoll!: (response: NotificationsResponseDto) => void;
    const validationResponse = new Promise<NotificationsResponseDto>((resolve) => {
      resolveValidation = resolve;
    });
    const pollResponse = new Promise<NotificationsResponseDto>((resolve) => {
      resolvePoll = resolve;
    });
    vi.mocked(apiClient.getNotifications)
      .mockResolvedValueOnce(respond([]))
      .mockResolvedValueOnce(respond([approval]))
      .mockImplementationOnce(() => validationResponse)
      .mockImplementationOnce(() => pollResponse);
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

    renderBell(1000);

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(1));
    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });
    await user.click(await screen.findByText('Review now'));
    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(3));

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });
    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(4));
    await act(async () => {
      resolvePoll(respond([approval]));
      await Promise.resolve();
    });
    await act(async () => {
      resolveValidation(respond([approval]));
      await Promise.resolve();
    });

    expect(screen.getByTestId('current-location').textContent)
      .toBe('/projects/proj-1/orchestrations/run-1');
    expect(screen.queryByText('This approval no longer has a run to review.')).toBeNull();
  });

  it('blocks an older CTA response after a newer poll changes a reused notification id', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const approval = makeNotification({
      id: 'reused-notification-id',
      type: 'tool_approval',
      run_id: 'original-approval-run',
      title: 'Approval needed to run "start_preview"',
    });
    const changedApproval = { ...approval, run_id: 'replacement-approval-run' };
    let resolveValidation!: (response: NotificationsResponseDto) => void;
    const validationResponse = new Promise<NotificationsResponseDto>((resolve) => {
      resolveValidation = resolve;
    });
    vi.mocked(apiClient.getNotifications)
      .mockResolvedValueOnce(respond([]))
      .mockResolvedValueOnce(respond([approval]))
      .mockImplementationOnce(() => validationResponse)
      .mockResolvedValueOnce(respond([changedApproval]));
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

    renderBell(1000);

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(1));
    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });
    await user.click(await screen.findByText('Review now'));
    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(3));

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });
    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(4));
    await waitFor(() => expect(screen.queryByText('Review now')).toBeNull());

    await act(async () => {
      resolveValidation(respond([approval]));
      await Promise.resolve();
    });

    expect(screen.getByTestId('current-location').textContent).toBe('/');
    expect(await screen.findByText('This approval no longer has a run to review.')).toBeTruthy();
  });

  it('navigates to an active approval target after verifying its source', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const approval = makeNotification({
      id: 'notif-active-approval',
      type: 'tool_approval',
      run_id: 'pending-approval-run',
      title: 'Approval needed to run "start_preview"',
    });
    vi.mocked(apiClient.getNotifications)
      .mockResolvedValueOnce(respond([]))
      .mockResolvedValueOnce(respond([approval]))
      .mockResolvedValueOnce(respond([approval]));
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

    renderBell(1000);

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(1));
    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });
    await user.click(await screen.findByText('Review now'));

    await waitFor(() => expect(screen.getByTestId('current-location').textContent)
      .toBe('/projects/proj-1/orchestrations/pending-approval-run'));
  });

  it('toasts a backlog_promoted notification with board-specific copy ("Subtasks created" / "View board")', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const promoted = makeNotification({
      id: 'backlog_promoted:run-9',
      type: 'backlog_promoted',
      title: '2 subtasks created',
      cta_path: '/projects/proj-1/board',
    });
    vi.mocked(apiClient.getNotifications)
      .mockResolvedValueOnce(respond([]))
      .mockResolvedValueOnce(respond([promoted]));

    renderBell(1000);

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(1));
    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });
    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(2));

    // Type-aware toast copy — not the generic "Awaiting your review".
    expect(await screen.findByText('Subtasks created')).toBeTruthy();
    expect(screen.getByText('View board')).toBeTruthy();
    expect(screen.queryByText('Awaiting your review')).toBeNull();
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

  it('keeps the badge visible for a pending tool approval after the popover is opened', async () => {
    vi.mocked(apiClient.getNotifications).mockResolvedValue(respond([
      makeNotification({ type: 'tool_approval', title: 'Approval needed to run "start_preview"' }),
    ]));
    const user = userEvent.setup();

    renderBell();

    await waitFor(() => expect(screen.getByTestId('notification-bell-badge').textContent).toContain('1'));
    await user.click(screen.getByTestId('notification-bell'));

    expect(screen.getByTestId('notification-bell-badge').textContent).toContain('1');
    expect(screen.getByTestId('notification-bell').getAttribute('aria-label'))
      .toContain('1 pending tool approval');
  });

  it('CTA click routes a pending approval to its run, not a concurrent newer draft', async () => {
    const newerDraft = makeNotification({
      id: 'notif-newer-draft',
      run_id: 'newer-draft-run',
      title: 'Newer unrelated draft',
      cta_path: '/projects/proj-1/orchestrations/newer-draft-run',
    });
    const pendingApproval = makeNotification({
      id: 'notif-pending-approval',
      type: 'tool_approval',
      run_id: 'pending-approval-run',
      title: 'Approval needed to run "start_preview"',
      // Simulates a stale server path: routing must use the notification's exact run_id.
      cta_path: '/projects/proj-1/orchestrations/newer-draft-run',
    });
    vi.mocked(apiClient.getNotifications).mockResolvedValue(respond([newerDraft, pendingApproval]));
    const user = userEvent.setup();

    renderBell();

    await waitFor(() => expect(screen.getByTestId('notification-bell-badge').textContent).toContain('2'));
    await user.click(screen.getByTestId('notification-bell'));
    const item = await screen.findByText(pendingApproval.title);
    await user.click(item);

    await waitFor(() => expect(screen.getByTestId('current-location').textContent)
      .toBe('/projects/proj-1/orchestrations/pending-approval-run'));
  });

  it('shows a safe message instead of routing when an approval target is missing', async () => {
    vi.mocked(apiClient.getNotifications).mockResolvedValue(respond([
      makeNotification({
        type: 'tool_approval',
        run_id: '',
        project_id: null,
        cta_path: '/projects/proj-1/orchestrations/newer-draft-run',
      }),
    ]));
    const user = userEvent.setup();

    renderBell();

    await waitFor(() => expect(screen.getByTestId('notification-bell-badge')).toBeTruthy());
    await user.click(screen.getByTestId('notification-bell'));

    expect(await screen.findByText('This approval no longer has a run to review.')).toBeTruthy();
    await user.click(screen.getByText(makeNotification().title));
    expect(screen.getByTestId('current-location').textContent).toBe('/');
  });

  it('dismisses one notification without navigating from its row', async () => {
    vi.mocked(apiClient.getNotifications).mockResolvedValue(respond([makeNotification()]));
    const user = userEvent.setup();

    renderBell();

    await waitFor(() => expect(screen.getByTestId('notification-bell-badge').textContent).toContain('1'));
    await user.click(screen.getByTestId('notification-bell'));
    await user.click(await screen.findByRole('button', {
      name: `Dismiss notification: ${makeNotification().title}`,
    }));
    expect(apiClient.dismissNotification).toHaveBeenCalledWith(makeNotification().id);

    expect(screen.queryByText(makeNotification().title)).toBeNull();
    expect(await screen.findByText('Nothing needs your attention right now.')).toBeTruthy();
  });

  it('keeps a dismissed notification hidden after a subsequent poll', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.mocked(apiClient.getNotifications)
      .mockResolvedValueOnce(respond([makeNotification()]))
      .mockResolvedValueOnce(respond([]));
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

    renderBell(1000);
    await waitFor(() => expect(screen.getByTestId('notification-bell-badge')).toBeTruthy());
    await user.click(screen.getByTestId('notification-bell'));
    await user.click(await screen.findByRole('button', {
      name: `Dismiss notification: ${makeNotification().title}`,
    }));

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });

    await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalledTimes(2));
    expect(screen.queryByText(makeNotification().title)).toBeNull();
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

    it('renders the Board badge for a backlog_promoted notification', async () => {
      vi.mocked(apiClient.getNotifications).mockResolvedValue(
        respond([makeNotification({ type: 'backlog_promoted' })]),
      );
      const user = userEvent.setup();

      renderBell();

      await waitFor(() => expect(apiClient.getNotifications).toHaveBeenCalled());
      await user.click(screen.getByTestId('notification-bell'));

      const badge = await screen.findByTestId('notification-type-badge');
      expect(badge.textContent).toContain('Board');
      expect(badge.getAttribute('data-notification-type')).toBe('backlog_promoted');
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
