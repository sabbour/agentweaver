import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectAuthorizationContextResponse, RunEgressPage } from './contracts';

const mocks = vi.hoisted(() => ({
  apiCall: vi.fn(),
  authorize: vi.fn(),
  ensureSession: vi.fn(),
  getAuthorizationContext: vi.fn(),
  readRunEgress: vi.fn(),
  auth: {
    actorId: 'actor-1',
    issuer: 'issuer-1',
    tenantId: 'tenant-1',
    token: 'unbound-token',
    generation: 0,
  },
}));

vi.mock('./api', () => ({
  gatewayClient: {
    getAuthorizationContext: mocks.getAuthorizationContext,
    readRunEgress: mocks.readRunEgress,
  },
  GatewayError: class GatewayError extends Error {
    status: number;
    code?: string;
    constructor(status: number, problem: Record<string, unknown>, message: string) {
      super(message);
      this.status = status;
      this.code = typeof problem.code === 'string' ? problem.code : undefined;
    }
  },
}));

vi.mock('./AuthContext', () => ({
  useAuth: () => ({
    session: { accessToken: mocks.auth.token, binding: null },
    authSessionGeneration: mocks.auth.generation,
    unboundSessionGeneration: mocks.auth.generation,
    authorizationContext: {
      issuer: mocks.auth.issuer,
      actorId: mocks.auth.actorId,
      tenantId: mocks.auth.tenantId,
    },
    apiCall: mocks.apiCall,
    authorize: mocks.authorize,
    ensureSession: mocks.ensureSession,
    busy: false,
  }),
}));

import { RunEgressPanel } from './RunEgressPanel';
import { GatewayError } from './api';

function context(overrides: Partial<ProjectAuthorizationContextResponse> = {}): ProjectAuthorizationContextResponse {
  return {
    contractVersion: 1,
    issuer: 'issuer-1',
    actorId: 'actor-1',
    tenantId: 'tenant-1',
    membershipRevision: 1,
    boundProjectId: 'project-1',
    boundRunId: null,
    effectiveAuthority: [],
    ...overrides,
  };
}

function page(
  items: RunEgressPage['items'] = [],
  nextCursor: string | null = null,
): RunEgressPage {
  return {
    runId: 'run-1',
    coverage: 'Unavailable',
    delayedUntil: null,
    items,
    nextCursor,
  };
}

const record = {
  host: 'api.example.test',
  address: '203.0.113.10',
  port: 443,
  verdict: 'allowed' as const,
  firstSeen: '2026-11-20T10:00:00Z',
  lastSeen: '2026-11-20T10:01:00Z',
  count: 3,
};

beforeEach(() => {
  vi.clearAllMocks();
  mocks.apiCall.mockReset();
  mocks.authorize.mockReset();
  mocks.ensureSession.mockReset();
  mocks.getAuthorizationContext.mockReset();
  mocks.readRunEgress.mockReset();
  mocks.auth.actorId = 'actor-1';
  mocks.auth.issuer = 'issuer-1';
  mocks.auth.tenantId = 'tenant-1';
  mocks.auth.token = 'unbound-token';
  mocks.auth.generation = 0;
  mocks.ensureSession.mockResolvedValue(true);
  mocks.authorize.mockResolvedValue(undefined);
  mocks.apiCall.mockImplementation(
    (operation: (token: string) => Promise<unknown>, binding: { projectId: string }) => {
      expect(binding).toEqual({ projectId: 'project-1' });
      return operation('project-token');
    },
  );
  mocks.getAuthorizationContext.mockResolvedValue(context());
  mocks.readRunEgress.mockResolvedValue(page());
});

describe('RunEgressPanel', () => {
  it('uses the exact project credential and shows coverage with a successful empty page', async () => {
    render(<RunEgressPanel projectId="project-1" runId="run-1" />);

    expect(await screen.findByText(
      'Coverage unavailable. This view shows stored records only.',
    )).toBeTruthy();
    expect(screen.getByText('No stored records')).toBeTruthy();
    expect(mocks.ensureSession).toHaveBeenCalledWith({ projectId: 'project-1' }, false);
    expect(mocks.getAuthorizationContext).toHaveBeenCalledWith(
      'project-token',
      'tenant-1',
      expect.any(AbortSignal),
    );
    expect(mocks.readRunEgress).toHaveBeenCalledWith(
      'project-token',
      'run-1',
      { limit: 50, cursor: null, signal: expect.any(AbortSignal) },
      'tenant-1',
    );
  });

  it('keeps server order and loads another page only while nextCursor exists', async () => {
    mocks.readRunEgress
      .mockResolvedValueOnce(page([record], 'cursor-2'))
      .mockResolvedValueOnce(page([{
        ...record,
        host: 'second.example.test',
        firstSeen: '2026-11-20T10:02:00Z',
      }], null));
    render(<RunEgressPanel projectId="project-1" runId="run-1" />);

    const table = await screen.findByRole('table', { name: 'Stored run egress' });
    expect(within(table).getByText('api.example.test')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(within(table).getAllByRole('row')).toHaveLength(3));
    expect(mocks.readRunEgress).toHaveBeenLastCalledWith(
      'project-token',
      'run-1',
      { limit: 50, cursor: 'cursor-2', signal: expect.any(AbortSignal) },
      'tenant-1',
    );
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
  });

  it('does not send a page 2 read when a late confirmation returns the original tenant', async () => {
    let resolveOriginalTenant!: (value: ProjectAuthorizationContextResponse) => void;
    const pendingOriginalTenant = new Promise<ProjectAuthorizationContextResponse>((resolve) => {
      resolveOriginalTenant = resolve;
    });
    mocks.readRunEgress
      .mockResolvedValueOnce(page([record], 'cursor-2'))
      .mockResolvedValueOnce(page([], null));
    mocks.getAuthorizationContext
      .mockResolvedValueOnce(context())
      .mockImplementationOnce(() => pendingOriginalTenant)
      .mockResolvedValueOnce(context({ tenantId: 'tenant-2' }));
    mocks.auth.tenantId = 'tenant-1';

    const view = render(<RunEgressPanel projectId="project-1" runId="run-1" />);
    await screen.findByText('api.example.test');
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(mocks.getAuthorizationContext).toHaveBeenCalledTimes(2));
    expect(screen.getByText('api.example.test')).toBeTruthy();

    mocks.auth.tenantId = 'tenant-2';
    view.rerender(<RunEgressPanel projectId="project-1" runId="run-1" />);
    expect(screen.queryByText('api.example.test')).toBeNull();
    resolveOriginalTenant(context());

    await waitFor(() => expect(mocks.readRunEgress).toHaveBeenCalledTimes(2));
    expect(mocks.readRunEgress.mock.calls[1][2]).toEqual({
      limit: 50,
      cursor: null,
      signal: expect.any(AbortSignal),
    });
    expect(mocks.readRunEgress).toHaveBeenLastCalledWith(
      'project-token',
      'run-1',
      { limit: 50, cursor: null, signal: expect.any(AbortSignal) },
      'tenant-2',
    );
  });

  it('abandons a page 2 response already in flight when the tenant changes', async () => {
    let resolveOldPage!: (value: RunEgressPage) => void;
    const oldPage = new Promise<RunEgressPage>((resolve) => {
      resolveOldPage = resolve;
    });
    let oldSignal!: AbortSignal;
    mocks.readRunEgress
      .mockImplementationOnce(() => Promise.resolve(page([record], 'cursor-2')))
      .mockImplementationOnce((_token, _runId, options) => {
        oldSignal = options.signal!;
        return oldPage;
      })
      .mockResolvedValueOnce(page([], null));
    mocks.getAuthorizationContext
      .mockResolvedValueOnce(context())
      .mockResolvedValueOnce(context())
      .mockResolvedValueOnce(context({ tenantId: 'tenant-2' }));

    const view = render(<RunEgressPanel projectId="project-1" runId="run-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(oldSignal).toBeDefined());

    mocks.auth.tenantId = 'tenant-2';
    view.rerender(<RunEgressPanel projectId="project-1" runId="run-1" />);
    expect(oldSignal.aborted).toBe(true);
    resolveOldPage(page([{ ...record, host: 'stale.example.test' }]));

    await waitFor(() => expect(mocks.readRunEgress).toHaveBeenCalledTimes(3));
    expect(screen.queryByText('api.example.test')).toBeNull();
    expect(screen.queryByText('stale.example.test')).toBeNull();
    expect(await screen.findByText('No stored records')).toBeTruthy();
  });

  it('aborts a pending page read when the panel unmounts', async () => {
    let signal!: AbortSignal;
    mocks.readRunEgress.mockImplementationOnce((_token, _runId, options) => {
      signal = options.signal!;
      return new Promise<RunEgressPage>(() => undefined);
    });

    const view = render(<RunEgressPanel projectId="project-1" runId="run-1" />);
    await waitFor(() => expect(signal).toBeDefined());
    view.unmount();

    expect(signal.aborted).toBe(true);
  });

  it('restarts from page 1 after an invalid cursor', async () => {
    mocks.readRunEgress
      .mockResolvedValueOnce(page([record], 'bad-cursor'))
      .mockRejectedValueOnce(new GatewayError(400, { code: 'run_egress_cursor_invalid' }, 'Invalid cursor.'))
      .mockResolvedValueOnce(page([record], null));

    render(<RunEgressPanel projectId="project-1" runId="run-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));

    await waitFor(() => expect(mocks.readRunEgress).toHaveBeenCalledTimes(3));
    expect(mocks.readRunEgress.mock.calls[2][2]).toEqual({
      limit: 50,
      cursor: null,
      signal: expect.any(AbortSignal),
    });
  });

  it('offers retry for a transient failure without presenting it as empty data', async () => {
    mocks.readRunEgress
      .mockRejectedValueOnce(new GatewayError(503, {}, 'Egress unavailable.'))
      .mockResolvedValueOnce(page([record]));
    render(<RunEgressPanel projectId="project-1" runId="run-1" />);

    expect(await screen.findByText(/Egress unavailable\./)).toBeTruthy();
    expect(screen.queryByText('No stored records')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('api.example.test')).toBeTruthy();
  });

  it('clears rows and cursor when project access is lost', async () => {
    mocks.readRunEgress
      .mockResolvedValueOnce(page([record], 'cursor-2'))
      .mockRejectedValueOnce(new GatewayError(403, {}, 'Forbidden.'));
    render(<RunEgressPanel projectId="project-1" runId="run-1" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));
    expect(await screen.findByText(/Forbidden\./)).toBeTruthy();
    expect(screen.queryByText('api.example.test')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
  });

  it('retries from page 1 after a page 2 binding mismatch clears the confirmed scope', async () => {
    mocks.readRunEgress
      .mockResolvedValueOnce(page([record], 'cursor-2'))
      .mockResolvedValueOnce(page([{ ...record, host: 'fresh.example.test' }]));
    mocks.getAuthorizationContext
      .mockResolvedValueOnce(context())
      .mockResolvedValueOnce(context({ boundProjectId: 'other-project' }))
      .mockResolvedValueOnce(context());
    render(<RunEgressPanel projectId="project-1" runId="run-1" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Retry' }));

    expect(await screen.findByText('fresh.example.test')).toBeTruthy();
    expect(mocks.readRunEgress).toHaveBeenCalledTimes(2);
    expect(mocks.readRunEgress.mock.calls[1][2]).toEqual({
      limit: 50,
      cursor: null,
      signal: expect.any(AbortSignal),
    });
  });

  it('offers project sign-in before any egress request when silent sign-in needs action', async () => {
    mocks.ensureSession.mockResolvedValue(false);
    render(<RunEgressPanel projectId="project-1" runId="run-1" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Sign in for this project' }));
    expect(mocks.authorize).toHaveBeenCalledWith({ projectId: 'project-1' }, undefined, false);
    expect(mocks.readRunEgress).not.toHaveBeenCalled();
  });

  it('stops before egress when the Projects context binding does not match', async () => {
    mocks.getAuthorizationContext.mockResolvedValue(context({ boundProjectId: 'other-project' }));
    render(<RunEgressPanel projectId="project-1" runId="run-1" />);

    expect(await screen.findByText(/different project/)).toBeTruthy();
    expect(mocks.readRunEgress).not.toHaveBeenCalled();
  });
});
