import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import type { ProjectRoleAssignment } from './contracts';
import { ProjectMembersPanel } from './ProjectMembers';

const mocks = vi.hoisted(() => ({
  apiCall: (operation: (token: string, tenant: string) => Promise<unknown>) => operation('token', 'tenant-1'),
  listProjectRoleAssignments: vi.fn(),
  assignProjectRole: vi.fn(),
  revokeProjectRoleAssignment: vi.fn(),
}));

vi.mock('./AuthContext', () => ({
  useAuth: () => ({ apiCall: mocks.apiCall }),
}));

vi.mock('./api', async () => {
  const actual = await vi.importActual<typeof import('./api')>('./api');
  return {
    ...actual,
    gatewayClient: {
      listProjectRoleAssignments: mocks.listProjectRoleAssignments,
      assignProjectRole: mocks.assignProjectRole,
      revokeProjectRoleAssignment: mocks.revokeProjectRoleAssignment,
    },
  };
});

const owner: ProjectRoleAssignment = {
  assignmentId: 'as-1', principalId: 'alice', role: 'owner', revision: 3, createdBy: 'admin', createdAt: '2026-01-02T00:00:00Z',
};
const viewer: ProjectRoleAssignment = {
  assignmentId: 'as-2', principalId: 'bob', role: 'viewer', revision: 11, createdBy: 'admin', createdAt: '2026-01-03T00:00:00Z',
};

function listOf(...assignments: ProjectRoleAssignment[]) {
  return { projectId: 'p1', assignments };
}

async function renderPanel(canManage: boolean) {
  render(<ProjectMembersPanel projectId="p1" canManage={canManage} />);
  await waitFor(() => expect(mocks.listProjectRoleAssignments).toHaveBeenCalled());
}

async function addMember(principalId: string, role?: string) {
  fireEvent.change(await screen.findByRole('textbox', { name: /Member ID/ }), { target: { value: principalId } });
  if (role) fireEvent.change(screen.getByRole('combobox', { name: 'Role' }), { target: { value: role } });
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Add member' })); });
}

describe('ProjectMembersPanel', () => {
  beforeEach(() => {
    mocks.listProjectRoleAssignments.mockReset();
    mocks.assignProjectRole.mockReset();
    mocks.revokeProjectRoleAssignment.mockReset();
  });

  it('renders members in a table', async () => {
    mocks.listProjectRoleAssignments.mockResolvedValue(listOf(owner, viewer));
    await renderPanel(false);
    const table = await screen.findByRole('table', { name: 'Project members' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(2);
    expect(within(rows[0]).getByRole('rowheader').textContent).toBe('alice');
    expect(within(rows[0]).getByText('Owner')).toBeTruthy();
    expect(within(rows[1]).getByRole('rowheader').textContent).toBe('bob');
    expect(within(rows[1]).getByText('Viewer')).toBeTruthy();
    expect(mocks.listProjectRoleAssignments).toHaveBeenCalledWith('token', 'p1', 'tenant-1');
  });

  it('says when the project has no members', async () => {
    mocks.listProjectRoleAssignments.mockResolvedValue(listOf());
    await renderPanel(true);
    expect(await screen.findByText('This project has no members.')).toBeTruthy();
  });

  it('hides change controls when the caller cannot manage members', async () => {
    mocks.listProjectRoleAssignments.mockResolvedValue(listOf(owner));
    await renderPanel(false);
    await screen.findByRole('table', { name: 'Project members' });
    expect(screen.queryByRole('button', { name: 'Add member' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Remove' })).toBeNull();
    expect(screen.getByText('People who can work in this project. Ask a tenant administrator to change members.')).toBeTruthy();
  });

  it('shows change controls when the caller can manage members', async () => {
    mocks.listProjectRoleAssignments.mockResolvedValue(listOf(owner));
    await renderPanel(true);
    await screen.findByRole('table', { name: 'Project members' });
    expect(screen.getByRole('button', { name: 'Add member' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Remove' })).toBeTruthy();
  });

  it('adds a member with the chosen role and reloads the list', async () => {
    mocks.listProjectRoleAssignments.mockResolvedValueOnce(listOf(owner)).mockResolvedValueOnce(listOf(owner, viewer));
    mocks.assignProjectRole.mockResolvedValue(viewer);
    await renderPanel(true);
    await addMember('  bob  ', 'viewer');
    expect(mocks.assignProjectRole).toHaveBeenCalledWith('token', 'p1', { principalId: 'bob', role: 'viewer' }, 'tenant-1');
    await waitFor(() => expect(mocks.listProjectRoleAssignments).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('bob')).toBeTruthy();
    expect((screen.getByRole('textbox', { name: /Member ID/ }) as HTMLInputElement).value).toBe('');
  });

  it('defaults the new member role to Contributor', async () => {
    mocks.listProjectRoleAssignments.mockResolvedValue(listOf());
    mocks.assignProjectRole.mockResolvedValue(viewer);
    await renderPanel(true);
    await addMember('carol');
    expect(mocks.assignProjectRole).toHaveBeenCalledWith('token', 'p1', { principalId: 'carol', role: 'contributor' }, 'tenant-1');
  });

  it('removes a member after confirmation using the assignment revision', async () => {
    mocks.listProjectRoleAssignments.mockResolvedValueOnce(listOf(owner, viewer)).mockResolvedValueOnce(listOf(owner));
    mocks.revokeProjectRoleAssignment.mockResolvedValue(undefined);
    await renderPanel(true);
    const table = await screen.findByRole('table', { name: 'Project members' });
    fireEvent.click(within(within(table).getAllByRole('row')[2]).getByRole('button', { name: 'Remove' }));
    expect(mocks.revokeProjectRoleAssignment).not.toHaveBeenCalled();
    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('Remove Viewer role from bob?');
    await act(async () => { fireEvent.click(within(dialog).getByRole('button', { name: 'Remove' })); });
    expect(mocks.revokeProjectRoleAssignment).toHaveBeenCalledWith('token', 'p1', 'as-2', 11, 'tenant-1');
    await waitFor(() => expect(mocks.listProjectRoleAssignments).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByText('bob')).toBeNull());
  });

  it('explains when the member store is unavailable', async () => {
    mocks.listProjectRoleAssignments.mockResolvedValue(listOf(owner));
    mocks.assignProjectRole.mockRejectedValue(new GatewayError(503, {}, 'Unavailable'));
    await renderPanel(true);
    await addMember('bob');
    expect(await screen.findByText('Member changes are not available right now. Try again later.')).toBeTruthy();
  });

  it('explains when the member already has the role', async () => {
    mocks.listProjectRoleAssignments.mockResolvedValue(listOf(owner));
    mocks.assignProjectRole.mockRejectedValue(new GatewayError(409, { code: 'role_assignment_exists' }, 'Conflict'));
    await renderPanel(true);
    await addMember('alice', 'owner');
    expect(await screen.findByText('This member already has that role.')).toBeTruthy();
    expect((screen.getByRole('textbox', { name: /Member ID/ }) as HTMLInputElement).value).toBe('alice');
  });

  it('explains when the member ID does not match a user or service account', async () => {
    mocks.listProjectRoleAssignments.mockResolvedValue(listOf(owner));
    mocks.assignProjectRole.mockRejectedValue(new GatewayError(404, { code: 'principal_not_found' }, 'Not found'));
    await renderPanel(true);
    await addMember('nobody');
    expect(await screen.findByText('No user or service account has this ID. Check the ID and try again.')).toBeTruthy();
  });

  it('explains when removing the last owner is refused', async () => {
    mocks.listProjectRoleAssignments.mockResolvedValue(listOf(owner));
    mocks.revokeProjectRoleAssignment.mockRejectedValue(new GatewayError(409, { code: 'last_project_owner' }, 'Conflict'));
    await renderPanel(true);
    await screen.findByRole('table', { name: 'Project members' });
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }));
    const dialog = await screen.findByRole('dialog');
    await act(async () => { fireEvent.click(within(dialog).getByRole('button', { name: 'Remove' })); });
    expect(await screen.findByText('A project needs at least one owner. Add another owner first.')).toBeTruthy();
  });

  it('says the project was not found when the list returns 404', async () => {
    mocks.listProjectRoleAssignments.mockRejectedValue(new GatewayError(404, {}, 'Not found'));
    await renderPanel(true);
    expect(await screen.findByText('This project was not found. It may have been deleted.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Add member' })).toBeNull();
  });

  it('explains who can see the list when the service returns 403', async () => {
    mocks.listProjectRoleAssignments.mockRejectedValue(new GatewayError(403, {}, 'Forbidden'));
    await renderPanel(false);
    expect(await screen.findByText('Only a tenant administrator or a project owner can see the member list.')).toBeTruthy();
  });

  it('shows other load failures as an error', async () => {
    mocks.listProjectRoleAssignments.mockRejectedValue(new GatewayError(500, { code: 'internal' }, 'Server error'));
    await renderPanel(false);
    expect(await screen.findByText(/HTTP 500 \(internal\): Server error/)).toBeTruthy();
  });
});
