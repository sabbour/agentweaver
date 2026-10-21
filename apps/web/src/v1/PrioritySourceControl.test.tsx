import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError, gatewayClient } from './api';
import type { ProjectIssuePage, ProjectIssueSummary } from './contracts';
import { PrioritySourceControl } from './PrioritySourceControl';

const auth = vi.hoisted(() => ({
  session: { accessToken: 'actor-1' },
  authorizationContext: { tenantId: 'tenant-1', membershipRevision: 1 },
  apiCall: vi.fn(<T,>(op: (token: string, tenant: string) => Promise<T>, binding: unknown) => {
    if (binding !== null) throw new Error('Stored project lists require an unbound token.');
    return op('unbound', 'tenant-1');
  }),
}));
vi.mock('./AuthContext', () => ({ useAuth: () => auth }));

function issue(number: number): ProjectIssueSummary {
  return {
    taskId: `task-${number}`, taskState: 'ready', providerId: 'github',
    repository: { owner: 'octo', name: 'api' }, number, title: `Fix ${number}`,
    state: 'open', createdAt: '2026-10-20T00:00:00Z', updatedAt: '2026-10-20T00:00:00Z',
  };
}
function page(items: ProjectIssueSummary[], nextCursor: string | null = null): ProjectIssuePage {
  return { projectId: 'p1', items, nextCursor };
}
beforeEach(() => {
  vi.restoreAllMocks();
  auth.authorizationContext.membershipRevision = 1;
  vi.spyOn(gatewayClient, 'listProjectIssues').mockResolvedValue(page([issue(42)]));
  vi.spyOn(gatewayClient, 'listProjectPullRequests').mockResolvedValue({
    projectId: 'p1', items: [], nextCursor: null,
    coverage: { recordedSince: '2026-10-20T00:00:00Z', includesEarlierPullRequests: false },
  });
});

describe('Priority source control choices', () => {
  it('selects a stored issue without typing an ID and pages with unchanged filters', async () => {
    vi.mocked(gatewayClient.listProjectIssues)
      .mockResolvedValueOnce(page([issue(42)], 'next'))
      .mockResolvedValueOnce(page([issue(41)]))
      .mockResolvedValueOnce(page([issue(42), issue(41)]));
    const select = vi.fn();
    render(<PrioritySourceControl projectId="p1" kind="issue" onSelect={select} />);
    await screen.findByRole('option', { name: /Fix 42/ });
    expect(screen.getByRole('button', { name: 'Link issue' }).hasAttribute('disabled')).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Load more issues' }));
    await screen.findByRole('option', { name: /Fix 41/ });
    expect(gatewayClient.listProjectIssues).toHaveBeenLastCalledWith(
      'unbound', 'p1', { state: 'open', cursor: 'next', pageSize: 25 }, 'tenant-1');
    fireEvent.change(screen.getByLabelText('Issue'), {
      target: { value: JSON.stringify(['github', 'octo', 'api', 41]) },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Link issue' }));
    await waitFor(() => expect(select).toHaveBeenCalledExactlyOnceWith(41));
  });

  it('distinguishes unavailable pull request storage from an empty stored list', async () => {
    vi.mocked(gatewayClient.listProjectPullRequests).mockRejectedValueOnce(
      new GatewayError(503, { code: 'pull_request_records_unavailable' }, 'Pull request records are not available.'));
    render(<PrioritySourceControl projectId="p1" kind="pull_request" />);
    await screen.findByRole('alert');
    expect(screen.queryByText('No stored pull requests match this state.')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await screen.findByText('No stored pull requests match this state.');
    expect(screen.getByText(/Earlier pull requests are not included/)).toBeTruthy();
  });

  it('removes choices on a failed refresh and recovers through a new read', async () => {
    render(<PrioritySourceControl projectId="p1" kind="issue" />);
    await screen.findByRole('option', { name: /Fix 42/ });
    vi.mocked(gatewayClient.listProjectIssues).mockRejectedValueOnce(new GatewayError(403, {}, 'Access removed.'));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh records' }));
    await screen.findByRole('alert');
    expect(screen.queryByRole('option', { name: /Fix 42/ })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await screen.findByRole('option', { name: /Fix 42/ });
  });

  it('discards late responses after changing authority and exposes no old target', async () => {
    let finish!: (value: ProjectIssuePage) => void;
    vi.mocked(gatewayClient.listProjectIssues).mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    const view = render(<PrioritySourceControl projectId="p1" kind="issue" number={42} />);
    await waitFor(() => expect(finish).toBeDefined());
    auth.authorizationContext.membershipRevision++;
    vi.mocked(gatewayClient.listProjectIssues).mockResolvedValue(page([]));
    view.rerender(<PrioritySourceControl projectId="p1" kind="issue" number={42} />);
    await screen.findByText('This link is not in the stored records.');
    await act(async () => { finish(page([issue(42)])); });
    expect(screen.queryByRole('link', { name: 'Open issue' })).toBeNull();
  });

  it('reopens a saved target from an authorized stored record, not an editable URL', async () => {
    render(<PrioritySourceControl projectId="p1" kind="issue" number={42} />);
    expect((await screen.findByRole('link', { name: 'Open issue' })).getAttribute('href'))
      .toBe('https://github.com/octo/api/issues/42');
    expect(gatewayClient.listProjectIssues).toHaveBeenCalledWith(
      'unbound', 'p1', { state: 'all', cursor: null, pageSize: 25 }, 'tenant-1');
  });

  it('refuses a number shared by repositories, even on another page or in another state', async () => {
    const other = { ...issue(42), repository: { owner: 'other', name: 'repo' }, state: 'closed' as const };
    let finish!: (value: ProjectIssuePage) => void;
    vi.mocked(gatewayClient.listProjectIssues)
      .mockResolvedValueOnce(page([issue(42)]))
      .mockResolvedValueOnce(page([issue(42)], 'next'))
      .mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    const select = vi.fn();
    render(<PrioritySourceControl projectId="p1" kind="issue" onSelect={select} />);
    await screen.findByRole('option', { name: /Fix 42/ });
    fireEvent.change(screen.getByLabelText('Issue'), {
      target: { value: JSON.stringify(['github', 'octo', 'api', 42]) },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Link issue' }));
    await waitFor(() => expect(finish).toBeDefined());
    // The unchecked final page is not proof that the number is unique.
    expect(select).not.toHaveBeenCalled();
    await act(async () => { finish(page([other])); });
    await screen.findByText('More than one repository has this number. The link cannot identify its target and was not saved.');
    expect(select).not.toHaveBeenCalled();
    expect(screen.queryByRole('option', { name: /Fix 42/ })).toBeNull();
    expect(gatewayClient.listProjectIssues).toHaveBeenLastCalledWith(
      'unbound', 'p1', { state: 'all', cursor: 'next', pageSize: 25 }, 'tenant-1');
  });

  it('never reopens the first repository when another page makes the saved number ambiguous', async () => {
    vi.mocked(gatewayClient.listProjectIssues)
      .mockResolvedValueOnce(page([issue(42)], 'next'))
      .mockResolvedValueOnce(page([{ ...issue(42), repository: { owner: 'other', name: 'repo' } }]));
    render(<PrioritySourceControl projectId="p1" kind="issue" number={42} />);
    await screen.findByRole('button', { name: 'Load more issues' });
    expect(screen.queryByRole('link', { name: 'Open issue' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Load more issues' }));
    await screen.findByText('More than one repository has this number. This link cannot identify its target.');
    expect(screen.queryByRole('link', { name: 'Open issue' })).toBeNull();
  });
});
