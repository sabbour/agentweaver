import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, cleanup, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import { LifecycleEventCard } from '../components/LifecycleEventCard';
import type { RunStreamEvent } from '../api/sse';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

afterEach(() => {
  // Explicit cleanup to prevent DOM state leaks between tests.
  cleanup();
  vi.unstubAllGlobals();
});

function makeEvent(type: string, payload: Record<string, unknown> = {}): RunStreamEvent {
  return { sequence: 1, type: type as RunStreamEvent['type'], payload };
}

describe('LifecycleEventCard — tool.approval_required', () => {
  it('renders the card with tool name and Allow once/Allow this run/Always allow (session)/Deny buttons', () => {
    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('tool.approval_required', {
            requestId: 'req-abc',
            toolName: 'web_fetch',
            url: 'https://example.com/some/path',
            intention: 'Fetch documentation for reference',
            message: 'The agent wants to fetch a URL. Operator approval required.',
          })}
          runId="run-001"
        />
      </Wrapper>,
    );

    expect(screen.getByText('Tool Approval Required')).toBeDefined();
    expect(screen.getByText('web_fetch')).toBeDefined();
    expect(screen.getByText('https://example.com/some/path')).toBeDefined();
    expect(screen.getByText('Fetch documentation for reference')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Allow once' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Allow this run' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Always allow (session)' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Deny' })).toBeDefined();
  });

  it('truncates URL longer than 80 chars', () => {
    const longUrl = 'https://example.com/' + 'a'.repeat(100);
    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('tool.approval_required', {
            requestId: 'req-xyz',
            toolName: 'web_fetch',
            url: longUrl,
          })}
          runId="run-002"
        />
      </Wrapper>,
    );

    // The pre element showing the URL should be truncated to 80 chars + '...'
    const preEl = document.querySelector('pre');
    expect(preEl).not.toBeNull();
    const text = preEl!.textContent ?? '';
    expect(text.endsWith('...')).toBe(true);
    expect(text.length).toBeLessThanOrEqual(83);
  });

  it('shows resolved state "✓ Allowed (once)" after Allow once click', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('tool.approval_required', {
            requestId: 'req-allow',
            toolName: 'web_fetch',
          })}
          runId="run-003"
        />
      </Wrapper>,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Allow once' }));
    expect(screen.getByText('✓ Allowed (once) · web_fetch')).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Allow once' })).toBeNull();
  });

  it('sends scope="once" in the request body when Allow once is clicked', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('tool.approval_required', {
            requestId: 'req-scope-once',
            toolName: 'web_fetch',
          })}
          runId="run-scope-1"
        />
      </Wrapper>,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Allow once' }));
    const body = JSON.parse(fetchMock.mock.calls[0][1].body as string) as { request_id: string; scope: string };
    expect(body.scope).toBe('once');
    expect(body.request_id).toBe('req-scope-once');
  });

  it('posts approval to childRunId when a bubbled tool approval carries one', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('tool.approval_required', {
            childRunId: 'child-run-approval',
            requestId: 'req-child',
            toolName: 'web_fetch',
          })}
          runId="coordinator-run"
        />
      </Wrapper>,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Allow once' }));
    expect(String(fetchMock.mock.calls[0][0])).toContain('/runs/child-run-approval/tool-approvals');
  });

  it('falls back to the runId prop when a tool approval has no childRunId', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('tool.approval_required', {
            requestId: 'req-run',
            toolName: 'web_fetch',
          })}
          runId="plain-run"
        />
      </Wrapper>,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Allow once' }));
    expect(String(fetchMock.mock.calls[0][0])).toContain('/runs/plain-run/tool-approvals');
  });

  it('shows resolved state "✓ Allowed (this run)" after Allow this run click', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('tool.approval_required', {
            requestId: 'req-allow-run',
            toolName: 'web_fetch',
          })}
          runId="run-004"
        />
      </Wrapper>,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Allow this run' }));
    expect(screen.getByText('✓ Allowed (this run) · web_fetch')).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Allow this run' })).toBeNull();
  });

  it('sends scope="run" in the request body when Allow this run is clicked', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('tool.approval_required', {
            requestId: 'req-scope-run',
            toolName: 'web_fetch',
          })}
          runId="run-scope-2"
        />
      </Wrapper>,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Allow this run' }));
    const body = JSON.parse(fetchMock.mock.calls[0][1].body as string) as { request_id: string; scope: string };
    expect(body.scope).toBe('run');
    expect(body.request_id).toBe('req-scope-run');
  });

  it('shows resolved state "✓ Allowed (always, this session)" after Always allow (session) click', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('tool.approval_required', {
            requestId: 'req-allow-always',
            toolName: 'web_fetch',
          })}
          runId="run-005"
        />
      </Wrapper>,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Always allow (session)' }));
    expect(screen.getByText('✓ Allowed (always, this session) · web_fetch')).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Always allow (session)' })).toBeNull();
  });

  it('sends scope="always" in the request body when Always allow (session) is clicked', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('tool.approval_required', {
            requestId: 'req-scope-always',
            toolName: 'web_fetch',
          })}
          runId="run-scope-3"
        />
      </Wrapper>,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Always allow (session)' }));
    const body = JSON.parse(fetchMock.mock.calls[0][1].body as string) as { request_id: string; scope: string };
    expect(body.scope).toBe('always');
    expect(body.request_id).toBe('req-scope-always');
  });

  it('shows resolved state after Deny click', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('tool.approval_required', {
            requestId: 'req-deny',
            toolName: 'web_fetch',
          })}
          runId="run-006"
        />
      </Wrapper>,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Deny' }));
    expect(screen.getByText('✗ Denied · web_fetch')).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Deny' })).toBeNull();
  });
});

describe('LifecycleEventCard — coordinator.integration_conflict_auto_resolved', () => {
  it('renders a neutral informational summary for auto-resolved merge conflicts', () => {
    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('coordinator.integration_conflict_auto_resolved', {
            conflictingBranch: 'agentweaver/child-b',
            conflictingFiles: ['shared.txt', 'docs/notes.md'],
            strategy: 'accept_child',
          })}
        />
      </Wrapper>,
    );

    expect(screen.getByText('auto-resolved merge conflict')).toBeDefined();
    expect(screen.getByText(/Accepted changes from agentweaver\/child-b/)).toBeDefined();
    expect(screen.getByText(/shared\.txt, docs\/notes\.md/)).toBeDefined();
  });
});

describe('LifecycleEventCard — coordinator steering events (unified steering)', () => {
  it('renders coordinator.steering_received naming the source and feedback', () => {
    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('coordinator.steering_received', {
            directiveId: 'dir-1',
            source: 'rubberduck',
            severity: 'request-changes',
            targetScope: 'subtask-2',
            feedback: 'Missing null check in parser',
          })}
        />
      </Wrapper>,
    );
    expect(screen.getByText('steering received')).toBeDefined();
    expect(screen.getByText(/from rubber-duck review/)).toBeDefined();
    expect(screen.getByText(/Missing null check in parser/)).toBeDefined();
  });

  it('renders coordinator.steering_decision dispatch_fresh as an explicit, explained re-dispatch (not a glitch)', () => {
    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('coordinator.steering_decision', {
            directiveId: 'dir-2',
            decision: 'dispatch_fresh',
            subtaskIds: ['subtask-3'],
            attempt: 2,
            rationale: 'Prior attempt diverged from the spec',
          })}
        />
      </Wrapper>,
    );
    expect(screen.getByText('fresh dispatch')).toBeDefined();
    expect(screen.getByText(/Dispatched fresh subtask/)).toBeDefined();
    expect(screen.getByText(/subtask subtask-3/)).toBeDefined();
    expect(screen.getByText(/attempt 2/)).toBeDefined();
    expect(screen.getByText(/because: Prior attempt diverged from the spec/)).toBeDefined();
  });

  it('renders coordinator.steering_decision in_place_steer as context-preserving steer', () => {
    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('coordinator.steering_decision', {
            decision: 'in_place_steer',
            subtaskIds: ['subtask-1'],
            rationale: 'Small fix',
          })}
        />
      </Wrapper>,
    );
    expect(screen.getByText('steered in place')).toBeDefined();
    expect(screen.getByText(/Steered in place \(context preserved\)/)).toBeDefined();
  });

  it('renders coordinator.steering_decision proceed as proceeded to review', () => {
    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('coordinator.steering_decision', {
            decision: 'proceed',
            rationale: 'Output is good enough',
          })}
        />
      </Wrapper>,
    );
    expect(screen.getByText('proceeded')).toBeDefined();
    expect(screen.getByText(/Proceeded to review/)).toBeDefined();
  });

  it('renders coordinator.steering_decision advisory as surfaced-but-no-action', () => {
    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('coordinator.steering_decision', {
            decision: 'advisory',
            rationale: 'Consider adding a test later',
          })}
        />
      </Wrapper>,
    );
    expect(screen.getByText('advisory noted')).toBeDefined();
    expect(screen.getByText(/Advisory noted \(no action taken\)/)).toBeDefined();
  });

  it('renders the legacy coordinator.assembly_changes_requested alias with the same steering treatment', () => {
    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('coordinator.assembly_changes_requested', {
            redispatchedSubtaskIds: ['subtask-4', 'subtask-5'],
            reason: 'Tests failing',
          })}
        />
      </Wrapper>,
    );
    expect(screen.getByText('fresh dispatch')).toBeDefined();
    expect(screen.getByText(/Dispatched fresh subtask/)).toBeDefined();
    expect(screen.getByText(/subtasks subtask-4, subtask-5/)).toBeDefined();
  });
});

describe('LifecycleEventCard — tool.approval_required resolved/expired states', () => {
  it('shows expired status when pre-rendered with isResolved=true and resolvedScope=expired', () => {
    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('tool.approval_required', {
            requestId: 'req-expired-pre',
            toolName: 'web_fetch',
          })}
          runId="run-expired"
          isResolved={true}
          resolvedScope="expired"
        />
      </Wrapper>,
    );

    expect(screen.getByText('This approval request expired · web_fetch')).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Allow once' })).toBeNull();
  });

  it('shows expired status when isResolved prop changes to true with resolvedScope=expired after mount', async () => {
    const event = makeEvent('tool.approval_required', {
      requestId: 'req-expired-live',
      toolName: 'web_fetch',
    });

    const { rerender } = render(
      <Wrapper>
        <LifecycleEventCard event={event} runId="run-live" isResolved={false} />
      </Wrapper>,
    );

    // Buttons should be live initially
    expect(screen.getByRole('button', { name: 'Allow once' })).toBeDefined();

    // Simulate server sending tool.approval_resolved (expired)
    await act(async () => {
      rerender(
        <Wrapper>
          <LifecycleEventCard event={event} runId="run-live" isResolved={true} resolvedScope="expired" />
        </Wrapper>,
      );
    });

    expect(screen.getByText('This approval request expired · web_fetch')).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Allow once' })).toBeNull();
  });

  it('shows denied status when pre-rendered with isResolved=true and resolvedScope=deny', () => {
    render(
      <Wrapper>
        <LifecycleEventCard
          event={makeEvent('tool.approval_required', {
            requestId: 'req-deny-pre',
            toolName: 'web_fetch',
          })}
          runId="run-deny"
          isResolved={true}
          resolvedScope="deny"
        />
      </Wrapper>,
    );

    expect(screen.getByText('✗ Denied · web_fetch')).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Allow once' })).toBeNull();
  });
});
