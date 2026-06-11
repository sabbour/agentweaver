import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
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
