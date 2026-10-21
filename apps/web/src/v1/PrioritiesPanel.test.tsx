import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createFakeGateway, NOW, priority, project, run } from './__fixtures__/cosGateway';
import type { FakeGateway } from './__fixtures__/cosGateway';
import type { ChiefOfStaffPriorityLink } from './contracts';

const hoisted = vi.hoisted(() => ({ gateway: null as FakeGateway | null }));

vi.mock('./api', async () => {
  const actual = await vi.importActual<typeof import('./api')>('./api');
  return { ...actual, gatewayClient: new actual.AgentweaverGatewayClient('https://gw.test/api/v1', (i, init) => hoisted.gateway!.fetch(i, init)) };
});

vi.mock('./AuthContext', () => {
  const auth = { apiCall: (op: (token: string, tenant: null) => Promise<unknown>, binding: unknown) => op(binding ? 'bound' : 'unbound', null) };
  return { useAuth: () => auth };
});

import { useChiefOfStaffConversations, useChiefOfStaffPriorities, useProjectChoices } from './chiefOfStaff';
import { LinkSummary, PRIORITY_CONFLICT_NOTICE, PRIORITY_ORDER_CONFLICT_NOTICE, PrioritiesPanel } from './PrioritiesPanel';

function Harness() {
  const priorities = useChiefOfStaffPriorities();
  const conversations = useChiefOfStaffConversations();
  const { projects } = useProjectChoices();
  return <PrioritiesPanel priorities={priorities} projects={projects} conversations={conversations} />;
}

function renderPanel() {
  return render(<MemoryRouter><Harness /></MemoryRouter>);
}

const item = (outcome: string) => screen.findByRole('listitem', { name: new RegExp(`^Priority \\d+: ${outcome}$`) });

/** Paths the panel wrote to. Every one must be a Chief of Staff Priority route. */
function writePaths() {
  return hoisted.gateway!.writes().map((request) => `${request.method} ${request.url.pathname}`);
}

beforeEach(() => {
  hoisted.gateway = createFakeGateway({
    projects: [project('p1', 'Payments')],
    runs: { p1: [run('r-1', 'active', 'Write the code'), run('r-2', 'completed', 'Ship it')] },
    priorities: [priority('pri-a', 'Cut latency', { notes: 'p95 under 200ms' }), priority('pri-b', 'Hire two engineers')],
    orderRevision: 7,
  });
});

describe('PrioritiesPanel', () => {
  it('creates once per draft and reuses the Idempotency-Key on retry', async () => {
    hoisted.gateway!.failNext('POST', '/api/v1/chief-of-staff/priorities', 'lost');
    renderPanel();
    const form = await screen.findByRole('form', { name: 'Add a Priority' });
    fireEvent.change(within(form).getByLabelText('Outcome'), { target: { value: 'Launch v2' } });
    act(() => {
      fireEvent.submit(form);
      fireEvent.submit(form);
    });
    await within(form).findByRole('alert');
    fireEvent.submit(form);
    await item('Launch v2');
    const creates = hoisted.gateway!.writes('/api/v1/chief-of-staff/priorities');
    expect(creates).toHaveLength(2);
    expect(creates[1].headers.get('Idempotency-Key')).toBe(creates[0].headers.get('Idempotency-Key'));
    expect(hoisted.gateway!.state.priorities.filter((entry) => entry.outcome === 'Launch v2')).toHaveLength(1);
  });

  it('rereads on a revision conflict, shows the notice, and keeps the draft', async () => {
    renderPanel();
    const row = await item('Cut latency');
    fireEvent.click(within(row).getByRole('button', { name: 'Edit: Cut latency' }));
    const form = within(row).getByRole('form', { name: 'Edit Priority: Cut latency' });
    fireEvent.change(within(form).getByLabelText('Notes'), { target: { value: 'p95 under 150ms' } });
    hoisted.gateway!.state.priorities[0] = { ...hoisted.gateway!.state.priorities[0], notes: 'Changed elsewhere', revision: 2 };
    fireEvent.submit(form);
    expect((await within(row).findByRole('alert')).textContent).toBe(PRIORITY_CONFLICT_NOTICE);
    expect((within(row).getByLabelText('Notes') as HTMLTextAreaElement).value).toBe('p95 under 150ms');
    const reads = hoisted.gateway!.requests.filter((request) => request.method === 'GET' && request.url.pathname === '/api/v1/chief-of-staff/priorities');
    expect(reads.length).toBeGreaterThanOrEqual(2);
    fireEvent.submit(within(row).getByRole('form', { name: 'Edit Priority: Cut latency' }));
    await waitFor(() => expect(hoisted.gateway!.state.priorities[0].notes).toBe('p95 under 150ms'));
    expect(hoisted.gateway!.state.priorities[0].revision).toBe(3);
  });

  it('reorders with the order revision and reports an order conflict', async () => {
    renderPanel();
    const second = await item('Hire two engineers');
    fireEvent.click(within(second).getByRole('button', { name: 'Move up: Hire two engineers' }));
    await waitFor(() => expect(hoisted.gateway!.state.priorities.map((entry) => entry.priorityId)).toEqual(['pri-b', 'pri-a']));
    const put = hoisted.gateway!.writes('/api/v1/chief-of-staff/priorities/order')[0];
    expect(put.body).toEqual({ expectedOrderRevision: 7, priorityIds: ['pri-b', 'pri-a'] });
    await item('Hire two engineers');
    expect(screen.getAllByRole('listitem', { name: /^Priority 1: / })[0].getAttribute('aria-label')).toBe('Priority 1: Hire two engineers');

    hoisted.gateway!.state.orderRevision = 20;
    fireEvent.click(screen.getByRole('button', { name: 'Move down: Hire two engineers' }));
    expect((await screen.findByText(PRIORITY_ORDER_CONFLICT_NOTICE)).getAttribute('role')).toBe('alert');
    expect(hoisted.gateway!.state.priorities.map((entry) => entry.priorityId)).toEqual(['pri-b', 'pri-a']);
  });

  it('edits checklists and links without calling any run or gate endpoint', async () => {
    renderPanel();
    let row = await item('Cut latency');
    const add = within(row).getByRole('form', { name: 'Add checklist item to Cut latency' });
    fireEvent.change(within(add).getByLabelText('New checklist item'), { target: { value: 'Profile the API' } });
    fireEvent.submit(add);
    const box = await within(row).findByRole('checkbox', { name: 'Profile the API' });
    fireEvent.click(box);
    await waitFor(() => expect(hoisted.gateway!.state.priorities[0].checklist[0]?.done).toBe(true));

    row = await item('Cut latency');
    fireEvent.click(within(row).getByRole('button', { name: 'Add link to Cut latency' }));
    const linkForm = within(row).getByRole('form', { name: 'Add link to Cut latency' });
    const runPicker = await within(linkForm).findByLabelText('Run');
    fireEvent.change(runPicker, { target: { value: 'r-2' } });
    fireEvent.submit(linkForm);
    await waitFor(() => expect(hoisted.gateway!.state.priorities[0].links).toHaveLength(1));
    expect(hoisted.gateway!.state.priorities[0].links[0]).toMatchObject({ kind: 'run', projectId: 'p1', runId: 'r-2' });
    row = await item('Cut latency');
    await within(row).findByText('Run r-2');
    expect(within(row).getByText('Done')).toBeTruthy();

    fireEvent.click(within(row).getByRole('button', { name: 'Remove checklist item: Profile the API' }));
    await waitFor(() => expect(hoisted.gateway!.state.priorities[0].checklist).toHaveLength(0));
    fireEvent.click(within(await item('Cut latency')).getByRole('button', { name: 'Remove link: Run' }));
    await waitFor(() => expect(hoisted.gateway!.state.priorities[0].links).toHaveLength(0));

    const writes = writePaths();
    expect(writes.length).toBe(5);
    expect(writes.every((path) => /^(PATCH|POST|DELETE) \/api\/v1\/chief-of-staff\/priorities\/pri-a(\/links(\/[^/]+)?)?$/.test(path))).toBe(true);
    const urls = hoisted.gateway!.requests.map((request) => request.url.pathname);
    expect(urls.some((url) => /\/start$|\/cancel$|\/decisions|\/gates|\/answers|\/coordination/.test(url))).toBe(false);
    expect(hoisted.gateway!.requests.filter((request) => request.url.pathname.includes('/runs')).every((request) => request.method === 'GET')).toBe(true);
  });

  it('offers artifacts but not issue or pull request links', async () => {
    renderPanel();
    const row = await item('Cut latency');
    fireEvent.click(within(row).getByRole('button', { name: 'Add link to Cut latency' }));
    const options = within(within(row).getByLabelText('Link type')).getAllByRole('option').map((option) => option.textContent);
    expect(options).toEqual(['Run', 'Conversation', 'Artifact']);
  });
});

describe('LinkSummary', () => {
  const names = new Map([['p1', 'Payments']]);
  const link = (overrides: Partial<ChiefOfStaffPriorityLink>): ChiefOfStaffPriorityLink => ({
    linkId: 'l1', kind: 'run', access: 'available', projectId: 'p1', runId: 'r-1', state: 'active', createdAt: NOW, ...overrides,
  });

  it('shows run state for run links', () => {
    const { container } = render(<LinkSummary link={link({})} projectNames={names} />);
    expect(container.textContent).toContain('Working');
    expect(container.textContent).toContain('Payments');
  });

  it('shows only the kind for an unavailable link', () => {
    const { container } = render(<LinkSummary link={link({ access: 'unavailable', projectId: undefined, runId: undefined, state: undefined })} projectNames={names} />);
    expect(container.textContent).toBe('UnavailableRun');
  });

  it('shows Not found for a missing target', () => {
    const { container } = render(<LinkSummary link={link({ access: 'missing', state: undefined })} projectNames={names} />);
    expect(container.textContent).toContain('Not found');
  });

  it('shows issue and pull request state as Unavailable', () => {
    const issue = render(<LinkSummary link={link({ kind: 'issue', runId: undefined, number: 12, state: 'unavailable' })} projectNames={names} />);
    expect(issue.container.textContent).toContain('Unavailable');
    expect(issue.container.textContent).toContain('#12');
    const pr = render(<LinkSummary link={link({ kind: 'pull_request', runId: undefined, number: 40, state: 'unavailable' })} projectNames={names} />);
    expect(pr.container.textContent).toContain('Unavailable');
    expect(pr.container.textContent).not.toMatch(/Open|Merged/);
  });
});